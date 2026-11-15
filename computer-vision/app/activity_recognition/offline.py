"""Run the full pipeline over a recorded clip as fast as possible (no real-time pacing), for evaluation.

Timestamps are *video time* (frame index / the file's frame rate), so results
are reproducible and match annotation times. The same detector, tracker,
lifecycle, pose model, classifier and state manager as the live service are
used; nothing is published.
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field
from pathlib import Path

import cv2

from app.activity_recognition.activity_classifier import ActivityClassifier
from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.activity_service import ActivityService
from app.activity_recognition.pose_estimator import PoseEstimator
from app.detection.detector import PersonDetector
from app.tracking.bytetrack import ByteTrackTracker
from app.tracking.lifecycle import TrackLifecycle


@dataclass
class Tick:
    t: float                         # seconds from the start of the clip
    track_id: int
    bbox: tuple[float, float, float, float]   # normalised x, y, w, h
    predicted: str                   # classifier output for the window (UNKNOWN when uncertain)
    confidence: float
    reason: str | None
    state: str | None                # state after smoothing (None until the first state is accepted)


@dataclass
class ClipRun:
    path: str
    frames_read: int = 0
    frames_processed: int = 0
    wall_seconds: float = 0.0
    video_seconds: float = 0.0
    ticks: list[Tick] = field(default_factory=list)
    pose_ms: float = 0.0
    step_ms: float = 0.0
    detect_ms: float = 0.0

    @property
    def processing_fps(self) -> float:
        return round(self.frames_processed / self.wall_seconds, 2) if self.wall_seconds else 0.0


def analyse_clip(path: Path, detector: PersonDetector, pose: PoseEstimator, classifier: ActivityClassifier, cfg: ActivityConfig,
                 process_fps: float = 8.0, min_hits: int = 2, lost_after: float = 3.0) -> ClipRun:
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise FileNotFoundError(f"Can't open {path}")
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    tracker = ByteTrackTracker(frame_rate=process_fps, track_buffer_seconds=lost_after)
    lifecycle = TrackLifecycle(min_hits, lost_after, 3600, 2)
    svc = ActivityService("offline", cfg, pose, classifier)
    run = ClipRun(str(path))
    step = max(1, int(round(fps / process_fps)))
    index = 0
    detect_total = 0.0
    started = time.perf_counter()
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            run.frames_read += 1
            index += 1
            if (index - 1) % step:
                continue
            t = (index - 1) / fps
            h, w = frame.shape[:2]
            d0 = time.perf_counter()
            tracks = tracker.update(detector.detect(frame, "offline", t), w, h)
            detect_total += (time.perf_counter() - d0) * 1000
            transitions = lifecycle.step([(tr, None) for tr in tracks], t)
            for tr in transitions:
                if tr.kind == "LOST":
                    svc.track_lost(tr.track.track_id)
            confirmed = {tid for tid, st in lifecycle.tracks.items() if st.confirmed}
            due = svc.due(t)
            svc.step(frame, tracks, confirmed, t)
            run.frames_processed += 1
            if not due:
                continue
            for tr in tracks:
                if tr.track_id not in confirmed:
                    continue
                st = svc.states.get("offline", tr.track_id)
                p = st.last_prediction if st is not None else None
                if p is None or p.window_end != t:
                    continue
                b = tr.bbox
                run.ticks.append(Tick(round(t, 3), tr.track_id, (b.x1 / w, b.y1 / h, b.width / w, b.height / h), p.label.value, p.confidence,
                                      None if p.reason is None else p.reason.value, None if st.current is None else st.current.value))
    finally:
        cap.release()
    run.wall_seconds = time.perf_counter() - started
    run.video_seconds = run.frames_read / fps
    snap = svc.metrics.snapshot()
    run.pose_ms, run.step_ms = snap["avg_pose_ms"], snap["avg_step_ms"]
    run.detect_ms = round(detect_total / run.frames_processed, 2) if run.frames_processed else 0.0
    return run
