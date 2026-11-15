"""Starts, stops and tracks camera sessions.

* one session per camera (a second start → 409), at most ``max_sessions``;
* the camera must exist in the platform, be Active and have a usable source;
* every session shares the one loaded model and the one publisher;
* ``shutdown()`` stops every session, releases every source and flushes events;
* activity recognition is on for a camera when CV_ACTIVITY_ENABLED is set and
  the camera is in CV_ACTIVITY_CAMERAS, unless the start request says
  otherwise; each session gets its own buffers and states, all sessions share
  the one pose model.
"""
from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass, replace
from pathlib import Path

from app.activity_recognition.activity_classifier import build_classifier
from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.activity_service import ActivityService
from app.activity_recognition.pose_estimator import PoseEstimator
from app.camera.session import ENDED, CameraSession
from app.garment_activities.activity_service import GarmentActivityService
from app.garment_activities.config import InferenceConfig
from app.garment_activities.feature_extractor import make_embedder
from app.garment_activities.inference import GarmentClassifier, load_classifier
from app.garment_activities.model_factory import ModelLoadError
from app.camera.sources import FrameSource, SourceOverride, resolve_source
from app.core.config import ROOT, Settings
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
    activity: bool | None = None          # None = CV_ACTIVITY_CAMERAS decides
    garment: bool | None = None           # None = CV_GARMENT_CONFIG decides


class SessionManager:
    def __init__(self, settings: Settings, directory: CameraDirectory, detector: PersonDetector, publisher: EventPublisher, device: str,
                 source_factory=None, pose_estimator: PoseEstimator | None = None,  # noqa: ANN001 - (cfg, options) -> FrameSource, for tests
                 activity_config: ActivityConfig | None = None) -> None:
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
        self.pose_estimator = pose_estimator
        self.activity_config = activity_config or ActivityConfig.from_settings(settings)
        self._pose_reported: set[str] = set()
        self._classifier = None
        self.garment_config = InferenceConfig.load(settings.garment_config)
        self.garment_config = replace(self.garment_config, enabled=settings.garment_enabled)
        self._garment_models: dict[str, GarmentClassifier] = {}
        self._embedders: dict[str, object] = {}
        self._garment_reported: set[tuple[str, str]] = set()

    def _garment_wanted(self, cfg: CameraConfig, opts: StartOptions) -> tuple[bool, str | None]:
        cam = self.garment_config.camera(cfg.id, cfg.code)
        if opts.garment is False:
            return False, None
        if opts.garment is None and cam is None:
            return False, None
        model = (cam.model if cam else None) or self.garment_config.default_model
        return True, model

    def garment_model(self, directory: str) -> GarmentClassifier:
        """Load a model directory once and share it (raises ModelNotReady with the reason)."""
        path = Path(directory)
        if not path.is_absolute():
            path = (ROOT / path).resolve()
        key = str(path)
        if key not in self._garment_models:
            try:
                clf = load_classifier(path, self.device, self.garment_config.allow_unevaluated, self.garment_config.decision)
            except ModelLoadError as exc:
                raise ModelNotReady(str(exc), "garment_model_unavailable") from exc
            self._garment_models[key] = clf
        return self._garment_models[key]

    def _embedder(self, clf: GarmentClassifier):  # noqa: ANN202
        name = clf.features.backbone
        if name not in self._embedders:
            self._embedders[name] = make_embedder(clf.features, self.device)
        return self._embedders[name]

    def _garment_for(self, cfg: CameraConfig, opts: StartOptions, activity: ActivityService | None) -> GarmentActivityService | None:
        wanted, model_dir = self._garment_wanted(cfg, opts)
        if not wanted:
            return None
        try:
            if activity is None:
                raise ModelNotReady("Garment activity recognition reuses the pose stage: the pose model must be loaded and activity "
                                    "recognition allowed for this camera.", "garment_needs_pose")
            if not model_dir:
                raise ModelNotReady("No garment activity model is configured for this camera (CV_GARMENT_CONFIG).", "garment_model_unavailable")
            clf = self.garment_model(model_dir)
            return GarmentActivityService(cfg.id, clf, self._embedder(clf), self.garment_config)
        except ModelNotReady as exc:
            if opts.garment:
                raise
            log.warning("%s: starting without garment activity recognition: %s", cfg.code, exc.message)
            return None
    def _activity_for(self, cfg: CameraConfig, opts: StartOptions) -> ActivityService | None:
        ac = self.activity_config
        wanted = opts.activity if opts.activity is not None else ac.enabled_for(cfg.id, cfg.code)
        if opts.activity is None and not wanted and self._garment_wanted(cfg, opts)[0]:
            wanted = True       # garment recognition reuses the pose stage; generic posture runs alongside as the fallback
        if not wanted:
            return None
        pose = self.pose_estimator
        if pose is None or not pose.info.loaded:
            reason = (pose.info.error if pose is not None else None) or "activity recognition is off (CV_ACTIVITY_ENABLED=false)"
            if opts.activity:
                raise ModelNotReady(f"Activity recognition isn't available: {reason}", "pose_model_not_ready")
            log.warning("%s: starting without activity recognition: %s", cfg.code, reason)
            return None
        if self._classifier is None:
            self._classifier = build_classifier(ac, self.device)     # stateless: loaded once, shared by every camera
        return ActivityService(cfg.id, ac, pose, self._classifier, keep_poses=self.settings.enable_preview or self.settings.preview_window)

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
            activity = self._activity_for(cfg, opts)
            garment = self._garment_for(cfg, opts, activity)
            source = self._source(cfg, opts)
            fps = min(opts.process_fps or self.settings.process_fps, 30.0)
            builder = EventBuilder(EventContext(cfg.id, cfg.code, cfg.factory_id, new_session_tag(), self.detector.info.name,
                                                self.detector.info.version[:40], self.device))
            if cfg.factory_id not in self._model_reported:
                # Tell the platform the model is ready for this factory (once per service run).
                self.publisher.publish(builder.model_status("READY", time.time(), f"{self.detector.info.name} loaded on {self.device}."))
                self._model_reported.add(cfg.factory_id)
            if activity is not None and cfg.factory_id not in self._pose_reported:
                info = activity.estimator.info
                self.publisher.publish(builder.model_status("READY", time.time(), f"{info.name} (pose) loaded on {self.device}; "
                                                            f"activity classifier {activity.classifier.name} {activity.classifier.version}.",
                                                            model_name=info.name, model_version=info.version[:40]))
                self._pose_reported.add(cfg.factory_id)
            if garment is not None and (cfg.factory_id, garment.clf.version) not in self._garment_reported:
                g = garment.clf
                self.publisher.publish(builder.model_status("READY", time.time(), f"{g.name} {g.version} (garment activity) loaded on {self.device}; "
                                                            f"evaluation {g.evaluation.get('status', 'PENDING')}.", model_name=g.name[:80],
                                                            model_version=str(g.version)[:40]))
                self._garment_reported.add((cfg.factory_id, g.version))
            session = CameraSession(
                cfg, source, self.detector, ByteTrackTracker(frame_rate=fps, track_buffer_seconds=self.settings.lost_after_seconds),
                self.publisher, builder,
                TrackLifecycle(self.settings.min_hits, self.settings.lost_after_seconds, self.settings.track_update_seconds,
                               self.settings.zone_debounce_frames),
                fps, self.directory, self.settings.reconnect_attempts, self.settings.reconnect_delay_seconds,
                self.settings.config_refresh_seconds, keep_preview=self.settings.enable_preview, preview_window=self.settings.preview_window,
                activity=activity, garment=garment)
            self._sessions[camera_id] = session
            session.start()
            log.info("Started %s (%s) at %.1f fps%s.", cfg.code, source.description, fps,
                     (f", activity recognition at {self.activity_config.process_fps:.1f}/s" if activity else "")
                     + (f", garment model {garment.clf.name} {garment.clf.version}" if garment else ""))
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
