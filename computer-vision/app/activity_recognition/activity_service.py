"""Activity recognition for one camera session: pose → features → temporal buffer → classifier → state.

The camera session calls :meth:`ActivityService.step` with every analysed
frame and the tracker's tracks. The service decides itself when pose
inference is due (``process_fps``, independent of the camera's capture rate
and of the detection rate), skips inference when no confirmed track is
visible, and only describes tracks the lifecycle has confirmed, so activity
events never mention a track the platform hasn't been told about.

Failures stay inside this camera: a pose-model error is counted, and after
``max_consecutive_errors`` the camera's activity recognition switches itself
off while person detection and tracking carry on.
"""
from __future__ import annotations

import logging
import threading
import time
from collections import Counter, deque
from dataclasses import dataclass, field

import numpy as np

from app.activity_recognition.activity_classifier import ActivityClassifier
from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.feature_extractor import extract_features
from app.activity_recognition.pose_estimator import PoseEstimator, associate_poses
from app.activity_recognition.pose_preprocessor import InvalidPose, normalize_pose
from app.activity_recognition.schemas import ActivityUpdate, PoseDetection, PoseObservation
from app.activity_recognition.state_manager import ActivityStateManager
from app.activity_recognition.temporal_buffer import TemporalBuffer
from app.tracking.base import TrackedObject

log = logging.getLogger(__name__)


@dataclass
class ActivityMetrics:
    steps: int = 0
    pose_inferences: int = 0
    skipped_no_tracks: int = 0
    avg_pose_ms: float = 0.0
    avg_step_ms: float = 0.0
    last_step_ms: float = 0.0
    observations: int = 0
    observations_with_pose: int = 0
    observations_without_pose: int = 0
    invalid_poses: int = 0
    predictions: dict = field(default_factory=Counter)
    unknown_reasons: dict = field(default_factory=Counter)
    updates: dict = field(default_factory=Counter)
    errors: int = 0
    last_error: str | None = None
    _times: deque = field(default_factory=lambda: deque(maxlen=40))
    _lock: threading.Lock = field(default_factory=threading.Lock)

    def timed(self, pose_ms: float | None, step_ms: float) -> None:
        with self._lock:
            self.steps += 1
            self.last_step_ms = round(step_ms, 2)
            n = min(self.steps, 50)
            self.avg_step_ms = round(self.avg_step_ms + (step_ms - self.avg_step_ms) / n, 2)
            if pose_ms is not None:
                self.pose_inferences += 1
                m = min(self.pose_inferences, 50)
                self.avg_pose_ms = round(self.avg_pose_ms + (pose_ms - self.avg_pose_ms) / m, 2)
            self._times.append(time.monotonic())

    @property
    def activity_fps(self) -> float:
        with self._lock:
            t = list(self._times)
        if len(t) < 2 or t[-1] <= t[0]:
            return 0.0
        return round((len(t) - 1) / (t[-1] - t[0]), 2)

    def snapshot(self) -> dict:
        with self._lock:
            data = {k: (dict(v) if isinstance(v, Counter) else v) for k, v in self.__dict__.items() if not k.startswith("_")}
        total = sum(data["predictions"].values())
        data["unknown_rate"] = round(data["predictions"].get("UNKNOWN", 0) / total, 3) if total else None
        data["activity_fps"] = self.activity_fps
        return data


class ActivityService:
    def __init__(self, camera_id: str, cfg: ActivityConfig, estimator: PoseEstimator, classifier: ActivityClassifier,
                 max_consecutive_errors: int = 5, keep_poses: bool = False) -> None:
        self.camera_id = camera_id
        self.cfg = cfg
        self.estimator = estimator
        self.classifier = classifier
        self.buffer = TemporalBuffer(cfg.window_seconds, cfg.max_gap_seconds, cfg.reset_after_seconds)
        self.states = ActivityStateManager(cfg.min_state_seconds, cfg.unknown_after_seconds, cfg.report_seconds,
                                           cfg.reset_after_seconds, cfg.history_size)
        self.metrics = ActivityMetrics()
        self.active = True
        self.disabled_reason: str | None = None
        self._interval = 1.0 / cfg.process_fps
        self._next_due = 0.0
        self._consecutive_errors = 0
        self._max_errors = max_consecutive_errors
        self._keep_poses = keep_poses
        self.last_poses: dict[int, PoseDetection] = {}
        # The latest pose stage output, for other recognisers that reuse it (garment activities): when it ran and what it matched.
        self.last_tick_at: float | None = None
        self.last_matched: dict[int, PoseDetection] = {}

    def due(self, now: float) -> bool:
        return self.active and now >= self._next_due

    def step(self, frame: np.ndarray, tracks: list[TrackedObject], confirmed: set[int], now: float) -> list[ActivityUpdate]:
        """Run activity recognition on this frame if it's due; returns the updates to publish."""
        if not self.due(now):
            return []
        self._next_due = max(self._next_due + self._interval, now) if self._next_due else now + self._interval
        started = time.perf_counter()
        visible = [t for t in tracks if t.track_id in confirmed]
        updates: list[ActivityUpdate] = []
        pose_ms = None
        if not visible:
            self.metrics.skipped_no_tracks += 1      # nobody to describe: no inference
        else:
            try:
                t0 = time.perf_counter()
                poses = self.estimator.estimate(frame)
                pose_ms = (time.perf_counter() - t0) * 1000
                self._consecutive_errors = 0
            except Exception as exc:  # noqa: BLE001 - one camera's pose failure must not stop its detection
                self._error(exc)
                return []
            matched = associate_poses(visible, poses, self.cfg.match_iou)
            self.last_tick_at, self.last_matched = now, matched
            if self._keep_poses:
                self.last_poses = matched
            for t in visible:
                obs = self._observe(t, matched.get(t.track_id), now)
                self.buffer.add(obs)
                pred = self.classifier.classify(self.buffer.window(self.camera_id, t.track_id))
                self.metrics.predictions[pred.label.value] += 1
                if pred.reason is not None:
                    self.metrics.unknown_reasons[pred.reason.value] += 1
                for u in self.states.update(self.camera_id, t.track_id, pred, now, t.bbox):
                    self.metrics.updates[u.kind] += 1
                    updates.append(u)
        self.buffer.expire(now)
        self.states.expire(now)
        self.metrics.timed(pose_ms, (time.perf_counter() - started) * 1000)
        return updates

    def _observe(self, t: TrackedObject, pose: PoseDetection | None, now: float) -> PoseObservation:
        self.metrics.observations += 1
        features = quality = None
        if pose is not None:
            try:
                norm = normalize_pose(pose, t.bbox, self.cfg.keypoint_confidence)
                quality = norm.quality
                if quality.usable:
                    features = extract_features(norm)
            except InvalidPose:
                self.metrics.invalid_poses += 1
        if features is None:
            self.metrics.observations_without_pose += 1
        else:
            self.metrics.observations_with_pose += 1
        return PoseObservation(self.camera_id, t.track_id, now, t.bbox, features, quality)

    def _error(self, exc: Exception) -> None:
        self._consecutive_errors += 1
        self.metrics.errors += 1
        self.metrics.last_error = f"{type(exc).__name__}: {getattr(exc, 'message', None) or exc}"
        log.warning("%s: pose estimation failed (%d in a row): %s", self.camera_id, self._consecutive_errors, self.metrics.last_error)
        if self._consecutive_errors >= self._max_errors:
            self.active = False
            self.disabled_reason = f"Activity recognition stopped after {self._consecutive_errors} consecutive pose errors: {self.metrics.last_error}"
            log.error("%s: %s Detection and tracking continue.", self.camera_id, self.disabled_reason)
            self.reset()

    def track_lost(self, track_id: int) -> None:
        self.buffer.drop(self.camera_id, track_id)
        self.states.end(self.camera_id, track_id)
        self.last_poses.pop(track_id, None)
        self.last_matched.pop(track_id, None)

    def reset(self) -> None:
        """Camera stopped, reconnected or failed: every buffer and state goes (track ids restart)."""
        self.buffer.clear()
        self.states.clear()
        self.last_poses = {}
        self.last_matched = {}
        self.last_tick_at = None

    def label_for(self, track_id: int) -> tuple[str, float] | None:
        st = self.states.get(self.camera_id, track_id)
        if st is None:
            return None
        if st.current is None:
            return ("...", 0.0)
        return (st.current.value, st.confidence)

    def snapshot(self, track_label) -> list[dict]:  # noqa: ANN001 - callable(int) -> str
        """Current activity of every track (no keypoints, no images)."""
        out = []
        for st in list(self.states.states.values()):
            p = st.last_prediction
            out.append({
                "trackId": track_label(st.track_id), "activity": None if st.current is None else st.current.value,
                "confidence": st.confidence, "since": st.since if st.current is not None else None, "lastSeen": st.last_seen,
                "pending": None if st.pending is None else st.pending.value,
                "lastPrediction": None if p is None else {"activity": p.label.value, "confidence": p.confidence,
                                                         "candidate": None if p.candidate is None else p.candidate.value,
                                                         "reason": None if p.reason is None else p.reason.value, "quality": p.quality.as_dict()},
                "history": [h.as_dict() for h in st.history[-10:]],
            })
        return out
