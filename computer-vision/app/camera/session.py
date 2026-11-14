"""One camera's processing loop, on its own thread.

    read frame → (skip to keep CV_PROCESS_FPS) → detect people → ByteTrack
    → zone of each track → lifecycle transitions → events → publisher

The loop never blocks on the network (events are queued) and keeps the
source open only while running. A disconnected live source is retried
``reconnect_attempts`` times; while it is down the camera is reported
OFFLINE, and ONLINE again when frames come back. A recorded video that ends
finishes the session. Stopping the session is not "camera offline": it only
ends the analysis (still-visible tracks are reported lost).
"""
from __future__ import annotations

import logging
import threading
import time
from enum import Enum

import numpy as np

from app.camera.metrics import SessionMetrics
from app.camera.sources import FrameSource
from app.core.errors import CvError
from app.detection.detector import PersonDetector
from app.events.builder import EventBuilder
from app.events.publisher import EventPublisher
from app.integration.backend import CameraDirectory
from app.integration.models import CameraConfig
from app.tracking.base import TrackedObject, Tracker
from app.tracking.lifecycle import TrackLifecycle
from app.zones.association import ZoneAssociator
from app.zones.types import Zone

log = logging.getLogger(__name__)


class SessionState(str, Enum):
    STARTING = "STARTING"
    RUNNING = "RUNNING"
    RECONNECTING = "RECONNECTING"
    STOPPING = "STOPPING"
    STOPPED = "STOPPED"
    FINISHED = "FINISHED"     # a recorded video reached its end
    FAILED = "FAILED"


ENDED = {SessionState.STOPPED, SessionState.FINISHED, SessionState.FAILED}


class CameraSession:
    def __init__(self, cfg: CameraConfig, source: FrameSource, detector: PersonDetector, tracker: Tracker, publisher: EventPublisher,
                 builder: EventBuilder, lifecycle: TrackLifecycle, process_fps: float, directory: CameraDirectory | None = None,
                 reconnect_attempts: int = 5, reconnect_delay: float = 3.0, config_refresh_seconds: float = 60.0,
                 keep_preview: bool = False, preview_window: bool = False, clock=time.time) -> None:  # noqa: ANN001
        self.cfg = cfg
        self.source = source
        self.detector = detector
        self.tracker = tracker
        self.publisher = publisher
        self.builder = builder
        self.lifecycle = lifecycle
        self.zones = ZoneAssociator(cfg.zone_objects())
        self.metrics = SessionMetrics()
        self.state = SessionState.STARTING
        self.error: str | None = None
        self.started_at = clock()
        self.ended_at: float | None = None
        self._interval = 1.0 / process_fps
        self._directory = directory
        self._reconnect_attempts = reconnect_attempts
        self._reconnect_delay = reconnect_delay
        self._refresh_every = config_refresh_seconds
        self._keep_preview = keep_preview or preview_window
        self._preview_window = preview_window
        self._clock = clock
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name=f"cv-camera-{cfg.code}", daemon=True)
        self._frame_index = 0
        self._last_preview: tuple[np.ndarray, list[tuple[TrackedObject, Zone | None]]] | None = None
        self._preview_lock = threading.Lock()

    # ── lifecycle ──
    def start(self) -> None:
        self._thread.start()

    def stop(self, timeout: float = 10.0) -> None:
        if self.state not in ENDED:
            self.state = SessionState.STOPPING
        self._stop.set()
        if self._thread.is_alive() and threading.current_thread() is not self._thread:
            self._thread.join(timeout)

    @property
    def alive(self) -> bool:
        return self._thread.is_alive()

    def _emit(self, event) -> None:  # noqa: ANN001
        if self.publisher.publish(event):
            self.metrics.events_emitted += 1

    # ── loop ──
    def _run(self) -> None:
        try:
            try:
                self.source.open()
            except CvError as exc:
                self._fail(exc.message, report_offline=True)
                return
            self.state = SessionState.RUNNING
            self._emit(self.builder.camera_status("ONLINE", self._clock(), f"Computer-vision analysis started ({self.source.description})."))
            next_due = 0.0
            next_refresh = self._clock() + self._refresh_every
            while not self._stop.is_set():
                ok, frame = self.source.read()
                if not ok or frame is None:
                    self.metrics.read_failures += 1
                    if not self.source.is_live:
                        self.state = SessionState.FINISHED
                        log.info("%s: recorded video finished.", self.cfg.code)
                        break
                    if not self._reconnect():
                        return
                    continue
                self.metrics.read()
                now = self._clock()
                if now < next_due:
                    self.metrics.skipped()       # frame skipping keeps the configured processing rate
                    continue
                next_due = max(next_due + self._interval, now) if next_due else now + self._interval
                self._process(frame, now)
                if now >= next_refresh:
                    next_refresh = now + self._refresh_every
                    self._refresh_zones()
        except Exception as exc:  # noqa: BLE001 - a crash must end in a visible FAILED state
            log.exception("%s: processing failed.", self.cfg.code)
            self._fail(f"Processing failed: {type(exc).__name__}: {exc}", report_offline=False)
        finally:
            for t in self.lifecycle.flush():
                self._emit(self.builder.from_transition(t, self._clock(), *self._frame_size(), self._frame_index, self.metrics.processing_fps))
            self.source.release()
            if self._preview_window:
                _close_window(self.cfg.code)
            if self.state not in ENDED:
                self.state = SessionState.STOPPED
            self.ended_at = self._clock()

    def _process(self, frame: np.ndarray, now: float) -> None:
        h, w = frame.shape[:2]
        self._size = (w, h)
        self._frame_index += 1
        started = time.perf_counter()
        detections = self.detector.detect(frame, self.cfg.id, now)
        tracks = self.tracker.update(detections, w, h)
        visible = [(t, self.zones.zone_for_box(t.bbox, w, h)) for t in tracks]
        transitions = self.lifecycle.step(visible, now)
        self.metrics.processed((time.perf_counter() - started) * 1000, len(detections), self.lifecycle.active)
        for t in transitions:
            self._emit(self.builder.from_transition(t, now, w, h, self._frame_index, self.metrics.processing_fps))
        if self._keep_preview:
            with self._preview_lock:
                self._last_preview = (frame.copy(), visible)
            if self._preview_window:
                _show_window(self.cfg.code, self.preview_frame())

    def _frame_size(self) -> tuple[int, int]:
        return getattr(self, "_size", (self.cfg.frame_width or 1280, self.cfg.frame_height or 720))

    def _reconnect(self) -> bool:
        """Report OFFLINE, retry the source; report ONLINE when it's back. False = gave up (FAILED)."""
        self.state = SessionState.RECONNECTING
        reason = f"No frames from {self.source.description}."
        self._emit(self.builder.camera_status("OFFLINE", self._clock(), reason, "ONLINE"))
        for t in self.lifecycle.flush():
            self._emit(self.builder.from_transition(t, self._clock(), *self._frame_size(), self._frame_index, self.metrics.processing_fps))
        self.tracker.reset()
        for attempt in range(1, self._reconnect_attempts + 1):
            if self._stop.wait(self._reconnect_delay):
                return False
            self.metrics.reconnects += 1
            self.source.release()
            try:
                self.source.open()
            except CvError as exc:
                log.warning("%s: reconnect %d/%d failed: %s", self.cfg.code, attempt, self._reconnect_attempts, exc.message)
                continue
            self.state = SessionState.RUNNING
            self._emit(self.builder.camera_status("ONLINE", self._clock(), "Video source reconnected.", "OFFLINE"))
            return True
        self._fail(f"{reason} Gave up after {self._reconnect_attempts} reconnect attempt(s).", report_offline=False)
        return False

    def _fail(self, message: str, report_offline: bool) -> None:
        self.state = SessionState.FAILED
        self.error = self.metrics.last_error = message
        log.error("%s: %s", self.cfg.code, message)
        if report_offline:
            self._emit(self.builder.camera_status("OFFLINE", self._clock(), message))

    def _refresh_zones(self) -> None:
        if self._directory is None:
            return
        try:
            fresh = self._directory.get_camera(self.cfg.id)
            self.apply_config(fresh)
        except CvError as exc:
            log.warning("%s: zones not refreshed: %s", self.cfg.code, exc.message)

    def apply_config(self, cfg: CameraConfig) -> None:
        """New zones take effect from the next frame (tracks keep their ids)."""
        self.cfg = cfg
        self.zones = ZoneAssociator(cfg.zone_objects())
        if not cfg.active:
            log.info("%s was deactivated in the platform: stopping its analysis.", cfg.code)
            self._stop.set()

    # ── preview ──
    def preview_frame(self) -> np.ndarray | None:
        from app.preview.overlay import draw_overlay

        with self._preview_lock:
            if self._last_preview is None:
                return None
            frame, visible = self._last_preview
        return draw_overlay(frame, visible, self.zones.zones, self.cfg.code, self.metrics.processing_fps, self.builder.track_id)

    def status(self) -> dict:
        return {
            "cameraId": self.cfg.id, "cameraCode": self.cfg.code, "factoryId": self.cfg.factory_id, "state": self.state.value,
            "source": self.source.description, "sourceKind": self.source.kind, "zones": len(self.zones.zones),
            "sessionTag": self.builder.ctx.session_tag, "startedAt": self.started_at, "endedAt": self.ended_at, "error": self.error,
            "activeTracks": self.lifecycle.active, "processingFps": self.metrics.processing_fps,
        }


def _show_window(name: str, frame: np.ndarray | None) -> None:  # pragma: no cover - needs a display
    import cv2

    if frame is not None:
        cv2.imshow(f"CV preview {name}", frame)
        cv2.waitKey(1)


def _close_window(name: str) -> None:  # pragma: no cover - needs a display
    import cv2

    try:
        cv2.destroyWindow(f"CV preview {name}")
    except cv2.error:
        pass
