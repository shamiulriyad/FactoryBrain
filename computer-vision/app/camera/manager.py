"""Starts, stops and tracks camera sessions.

* one session per camera (a second start → 409), at most ``max_sessions``;
* the camera must exist in the platform, be Active and have a usable source;
* every session shares the one loaded model and the one publisher;
* ``shutdown()`` stops every session, releases every source and flushes events.
"""
from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass

from app.camera.session import ENDED, CameraSession
from app.camera.sources import FrameSource, SourceOverride, resolve_source
from app.core.config import Settings
from app.core.errors import InvalidSource, ModelNotReady, SessionAlreadyRunning, SessionNotRunning, TooManySessions
from app.detection.detector import PersonDetector
from app.events.builder import EventBuilder, EventContext, new_session_tag
from app.events.publisher import EventPublisher
from app.integration.backend import CameraDirectory
from app.integration.models import CameraConfig
from app.tracking.bytetrack import ByteTrackTracker
from app.tracking.lifecycle import TrackLifecycle

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class StartOptions:
    process_fps: float | None = None
    loop: bool = False
    source: SourceOverride | None = None


class SessionManager:
    def __init__(self, settings: Settings, directory: CameraDirectory, detector: PersonDetector, publisher: EventPublisher, device: str,
                 source_factory=None) -> None:  # noqa: ANN001 - (cfg, options) -> FrameSource, for tests
        self.settings = settings
        self.directory = directory
        self.detector = detector
        self.publisher = publisher
        self.device = device
        self._sessions: dict[str, CameraSession] = {}
        self._lock = threading.Lock()
        self._model_reported: set[str] = set()
        self._source_factory = source_factory
        self._closed = False

    def _source(self, cfg: CameraConfig, opts: StartOptions) -> FrameSource:
        if self._source_factory is not None:
            return self._source_factory(cfg, opts)
        return resolve_source(cfg, self.directory, self.settings.data_dir, self.settings.webcam_index, opts.loop, opts.source,
                              self.settings.allow_source_override)

    def start(self, camera_id: str, opts: StartOptions | None = None) -> dict:
        opts = opts or StartOptions()
        if opts.source is not None and not self.settings.allow_source_override:
            raise InvalidSource("Source overrides are disabled (set CV_ALLOW_SOURCE_OVERRIDE=true for development).", "override_disabled")
        with self._lock:
            if self._closed:
                raise InvalidSource("The service is shutting down.", "shutting_down")
            existing = self._sessions.get(camera_id)
            if existing is not None and existing.state not in ENDED:
                raise SessionAlreadyRunning(f"{existing.cfg.code} is already being processed.")
            running = sum(1 for s in self._sessions.values() if s.state not in ENDED)
            if running >= self.settings.max_sessions:
                raise TooManySessions(f"{running} camera(s) are already being processed (limit CV_MAX_SESSIONS={self.settings.max_sessions}).")
            if not self.detector.info.loaded:
                raise ModelNotReady(self.detector.info.error or "The model isn't loaded yet.")
            cfg = self.directory.get_camera(camera_id)
            if not cfg.active:
                raise InvalidSource(f"{cfg.code} is {cfg.status.lower()} in the platform; activate it first.", "camera_not_active")
            source = self._source(cfg, opts)
            fps = min(opts.process_fps or self.settings.process_fps, 30.0)
            builder = EventBuilder(EventContext(cfg.id, cfg.code, cfg.factory_id, new_session_tag(), self.detector.info.name,
                                                self.detector.info.version[:40], self.device))
            if cfg.factory_id not in self._model_reported:
                # Tell the platform the model is ready for this factory (once per service run).
                self.publisher.publish(builder.model_status("READY", time.time(), f"{self.detector.info.name} loaded on {self.device}."))
                self._model_reported.add(cfg.factory_id)
            session = CameraSession(
                cfg, source, self.detector, ByteTrackTracker(frame_rate=fps, track_buffer_seconds=self.settings.lost_after_seconds),
                self.publisher, builder,
                TrackLifecycle(self.settings.min_hits, self.settings.lost_after_seconds, self.settings.track_update_seconds,
                               self.settings.zone_debounce_frames),
                fps, self.directory, self.settings.reconnect_attempts, self.settings.reconnect_delay_seconds,
                self.settings.config_refresh_seconds, keep_preview=self.settings.enable_preview, preview_window=self.settings.preview_window)
            self._sessions[camera_id] = session
            session.start()
            log.info("Started %s (%s) at %.1f fps.", cfg.code, source.description, fps)
            return session.status()

    def stop(self, camera_id: str, timeout: float = 10.0) -> dict:
        with self._lock:
            session = self._sessions.get(camera_id)
        if session is None or session.state in ENDED:
            raise SessionNotRunning("This camera isn't being processed.")
        session.stop(timeout)
        log.info("Stopped %s.", session.cfg.code)
        return session.status()

    def get(self, camera_id: str) -> CameraSession | None:
        return self._sessions.get(camera_id)

    def all(self) -> list[CameraSession]:
        return list(self._sessions.values())

    def running(self) -> list[CameraSession]:
        return [s for s in self._sessions.values() if s.state not in ENDED]

    def refresh_config(self) -> int:
        """Reload camera configuration (zones, status) for running sessions now. Returns how many were updated."""
        updated = 0
        for s in self.running():
            s.apply_config(self.directory.get_camera(s.cfg.id))
            updated += 1
        return updated

    def shutdown(self, timeout: float = 10.0) -> None:
        with self._lock:
            self._closed = True
            sessions = list(self._sessions.values())
        for s in sessions:
            s.stop(timeout)
        self.publisher.close(timeout)
