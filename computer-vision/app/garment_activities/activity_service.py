"""Live garment activity recognition for one camera session.

It does not detect, track or estimate pose itself: the camera session hands
it, after each pose-stage tick, the frame, the confirmed tracks and the poses
matched to them (from the existing activity recognition). It then:

1. samples each track at the model's ``sample_fps`` (the rate it was trained
   at): person crop → CNN embedding (once per observation), pose features,
   box, timestamp, kept per ``(camera, track)``;
2. every ``1 / inference_fps`` seconds, builds each track's last clip (the
   model's duration and frame count, missing frames absent), runs the model
   for all tracks in one batch and applies the UNKNOWN rules;
3. smooths the answers with the same state manager as the generic activity
   (minimum durations before a change, a longer hold before UNKNOWN,
   periodic re-confirmation) and returns the updates to publish.

Workstation / zone context is kept beside the prediction, never fed into it.
Failures stay inside this camera: after ``max_consecutive_errors`` errors the
garment recognition of this camera switches itself off; detection, tracking
and generic posture recognition carry on.
"""
from __future__ import annotations

import logging
import threading
import time
from collections import Counter, deque
from dataclasses import dataclass, field

import numpy as np

from app.activity_recognition.schemas import ActivityUpdate, PoseDetection
from app.activity_recognition.state_manager import ActivityStateManager
from app.garment_activities.config import InferenceConfig
from app.garment_activities.feature_extractor import Embedder, FrameDescription, add_motion, assemble, describe, quality, resample
from app.garment_activities.inference import GarmentClassifier
from app.tracking.base import TrackedObject

log = logging.getLogger(__name__)


@dataclass
class GarmentMetrics:
    observations: int = 0
    embeddings: int = 0
    avg_embed_ms: float = 0.0
    inferences: int = 0
    clips_classified: int = 0
    avg_inference_ms: float = 0.0
    predictions: dict = field(default_factory=Counter)
    unknown_reasons: dict = field(default_factory=Counter)
    low_confidence: int = 0
    updates: dict = field(default_factory=Counter)
    errors: int = 0
    last_error: str | None = None
    _lock: threading.Lock = field(default_factory=threading.Lock)

    def snapshot(self) -> dict:
        with self._lock:
            d = {k: (dict(v) if isinstance(v, Counter) else v) for k, v in self.__dict__.items() if not k.startswith("_")}
        total = sum(d["predictions"].values())
        d["unknown_rate"] = round(d["predictions"].get("UNKNOWN", 0) / total, 3) if total else None
        d["low_confidence_rate"] = round(d["low_confidence"] / total, 3) if total else None
        return d


def _avg(old: float, new: float, n: int) -> float:
    return round(old + (new - old) / min(n, 50), 2)


class GarmentActivityService:
    def __init__(self, camera_id: str, clf: GarmentClassifier, embedder: Embedder, cfg: InferenceConfig, max_consecutive_errors: int = 5) -> None:
        self.camera_id = camera_id
        self.clf = clf
        self.embedder = embedder
        self.cfg = cfg
        self.step = 1.0 / clf.clip.sample_fps
        self.buffers: dict[tuple[str, int], deque[FrameDescription]] = {}
        self.context: dict[tuple[str, int], dict] = {}
        self.boxes: dict[tuple[str, int], object] = {}
        self.states = ActivityStateManager(cfg.min_state_seconds, cfg.unknown_after_seconds, cfg.report_seconds, cfg.reset_after_seconds)
        self.metrics = GarmentMetrics()
        self.active = True
        self.disabled_reason: str | None = None
        self._next_sample = 0.0
        self._next_infer = 0.0
        self._consecutive_errors = 0
        self._max_errors = max_consecutive_errors
        self._maxlen = clf.frames * 3

    def step_frame(self, frame: np.ndarray, tracks: list[TrackedObject], confirmed: set[int], poses: dict[int, PoseDetection], now: float,
                   contexts: dict[int, dict] | None = None) -> list[ActivityUpdate]:
        """Observe (at the model's sample rate) and infer (at ``inference_fps``); returns state updates to publish."""
        if not self.active:
            return []
        try:
            if now >= self._next_sample:
                self._next_sample = max(self._next_sample + self.step, now) if self._next_sample else now + self.step
                self._observe(frame, [t for t in tracks if t.track_id in confirmed], poses, now, contexts or {})
            updates: list[ActivityUpdate] = []
            if now >= self._next_infer:
                interval = 1.0 / self.cfg.inference_fps
                self._next_infer = max(self._next_infer + interval, now) if self._next_infer else now + interval
                updates = self._infer(now)
            self._expire(now)
            self._consecutive_errors = 0
            return updates
        except Exception as exc:  # noqa: BLE001 - contained to this camera's garment recognition
            self._error(exc)
            return []

    def _observe(self, frame: np.ndarray, tracks: list[TrackedObject], poses: dict[int, PoseDetection], now: float, contexts: dict[int, dict]) -> None:
        if not tracks:
            return
        descs = [describe(frame, now, t.bbox, poses.get(t.track_id), self.clf.features, need_crop=self.embedder.dim > 0) for t in tracks]
        if self.embedder.dim:
            started = time.perf_counter()
            emb = self.embedder.embed([d.crop for d in descs])
            self.metrics.embeddings += 1
            self.metrics.avg_embed_ms = _avg(self.metrics.avg_embed_ms, (time.perf_counter() - started) * 1000, self.metrics.embeddings)
            for d, e in zip(descs, emb):
                d.embedding, d.crop = e, None          # keep the 576 numbers, not the image
        for t, d in zip(tracks, descs):
            key = (self.camera_id, t.track_id)
            buf = self.buffers.get(key)
            if buf is None:
                buf = self.buffers[key] = deque(maxlen=self._maxlen)
            buf.append(d)
            self.boxes[key] = t.bbox
            if t.track_id in contexts:
                self.context[key] = contexts[t.track_id]
            self.metrics.observations += 1

    def _infer(self, now: float) -> list[ActivityUpdate]:
        n, duration = self.clf.frames, self.clf.clip.duration_s
        keys, seqs, masks, windows, quals = [], [], [], [], []
        for key, buf in self.buffers.items():
            if not buf or now - buf[0].t < duration * 0.5:      # not observed long enough for a clip yet
                continue
            start = now - (n - 1) * self.step
            frames = resample(list(buf), start, n, self.step)
            add_motion(frames)
            x = assemble(frames, np.zeros((0, self.embedder.dim), np.float32), self.embedder.name)
            keys.append(key)
            seqs.append(x)
            masks.append(np.array([1.0 if d.present else 0.0 for d in frames], np.float32))
            windows.append((start, now))
            quals.append(quality(frames))
        if not keys:
            return []
        started = time.perf_counter()
        preds = self.clf.predict(np.stack(seqs), np.stack(masks), windows, quals)
        self.metrics.inferences += 1
        self.metrics.clips_classified += len(keys)
        self.metrics.avg_inference_ms = _avg(self.metrics.avg_inference_ms, (time.perf_counter() - started) * 1000, self.metrics.inferences)
        updates: list[ActivityUpdate] = []
        for key, p in zip(keys, preds):
            self.metrics.predictions[p.label] += 1
            if p.reason:
                self.metrics.unknown_reasons[p.reason] += 1
            if p.confidence < self.clf.decision.low_confidence:
                self.metrics.low_confidence += 1
            for u in self.states.update(key[0], key[1], p, now, self.boxes.get(key)):   # type: ignore[arg-type]
                self.metrics.updates[u.kind] += 1
                updates.append(u)
        return updates

    def _expire(self, now: float) -> None:
        for key in [k for k, b in self.buffers.items() if not b or now - b[-1].t >= self.cfg.reset_after_seconds]:
            self._forget(key)
        self.states.expire(now)

    def _forget(self, key: tuple[str, int]) -> None:
        self.buffers.pop(key, None)
        self.context.pop(key, None)
        self.boxes.pop(key, None)

    def _error(self, exc: Exception) -> None:
        self._consecutive_errors += 1
        self.metrics.errors += 1
        self.metrics.last_error = f"{type(exc).__name__}: {exc}"
        log.warning("%s: garment activity recognition failed (%d in a row): %s", self.camera_id, self._consecutive_errors, self.metrics.last_error)
        if self._consecutive_errors >= self._max_errors:
            self.active = False
            self.disabled_reason = f"Garment activity recognition stopped after {self._consecutive_errors} consecutive errors: {self.metrics.last_error}"
            log.error("%s: %s Other recognition continues.", self.camera_id, self.disabled_reason)
            self.reset()

    def track_lost(self, track_id: int) -> None:
        key = (self.camera_id, track_id)
        self._forget(key)
        self.states.end(*key)

    def reset(self) -> None:
        self.buffers.clear()
        self.context.clear()
        self.boxes.clear()
        self.states.clear()

    def context_for(self, track_id: int) -> dict | None:
        return self.context.get((self.camera_id, track_id))

    def label_for(self, track_id: int) -> tuple[str, float] | None:
        st = self.states.get(self.camera_id, track_id)
        if st is None:
            return None
        return ("...", 0.0) if st.current is None else (str(st.current), st.confidence)

    def snapshot(self, track_label) -> list[dict]:  # noqa: ANN001
        out = []
        for st in list(self.states.states.values()):
            p = st.last_prediction
            out.append({"trackId": track_label(st.track_id), "activity": st.current, "confidence": st.confidence,
                        "since": st.since if st.current is not None else None, "pending": st.pending,
                        "lastPrediction": None if p is None else {"activity": p.label, "confidence": p.confidence, "candidate": p.candidate,
                                                                 "reason": p.reason, "quality": p.quality},
                        "context": self.context_for(st.track_id), "history": [h.as_dict() for h in st.history[-10:]]})
        return out


# ─── Recorded video (CLI: scripts/garment.py infer) ───────────────────────

def infer_video(path, detector, pose, clf: GarmentClassifier, embedder: Embedder, cfg: InferenceConfig, process_fps: float = 8.0,  # noqa: ANN001
                activity_fps: float = 4.0, min_hits: int = 2) -> dict:
    """Run the live chain (detector → ByteTrack → pose stage → garment model → smoothing) over a video, as fast as possible,
    with video time as the clock. Nothing is published. Returns the predictions, state changes and timings."""
    import cv2

    from app.activity_recognition.activity_classifier import BaselineTemporalClassifier
    from app.activity_recognition.activity_config import ActivityConfig
    from app.activity_recognition.activity_service import ActivityService
    from app.tracking.bytetrack import ByteTrackTracker
    from app.tracking.lifecycle import TrackLifecycle

    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise FileNotFoundError(f"Can't open {path}")
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    ac = ActivityConfig(enabled=True, process_fps=max(activity_fps, clf.clip.sample_fps))
    stage = ActivityService("video", ac, pose, BaselineTemporalClassifier(ac))
    svc = GarmentActivityService("video", clf, embedder, cfg)
    tracker = ByteTrackTracker(frame_rate=process_fps, track_buffer_seconds=3.0)
    lifecycle = TrackLifecycle(min_hits, 3.0, 3600, 2)
    step = max(1, int(round(fps / process_fps)))
    events, index, processed = [], 0, 0
    started = time.perf_counter()
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            index += 1
            if (index - 1) % step:
                continue
            t = (index - 1) / fps
            h, w = frame.shape[:2]
            tracks = tracker.update(detector.detect(frame, "video", t), w, h)
            for tr in lifecycle.step([(x, None) for x in tracks], t):
                if tr.kind == "LOST":
                    stage.track_lost(tr.track.track_id)
                    svc.track_lost(tr.track.track_id)
            confirmed = {tid for tid, st in lifecycle.tracks.items() if st.confirmed}
            stage.step(frame, tracks, confirmed, t)
            processed += 1
            if stage.last_tick_at == t:
                for u in svc.step_frame(frame, tracks, confirmed, stage.last_matched, t):
                    p = u.prediction
                    events.append({"t": round(t, 2), "kind": u.kind, "track": u.track_id, "activity": str(u.activity),
                                   "previous": None if u.previous is None else str(u.previous), "confidence": round(u.confidence, 3),
                                   "reason": p.reason, "candidate": p.candidate, "window": [round(p.window_start, 2), round(p.window_end, 2)]})
    finally:
        cap.release()
    wall = time.perf_counter() - started
    return {"video": str(path), "videoSeconds": round(index / fps, 2), "wallSeconds": round(wall, 2), "framesProcessed": processed,
            "processingFps": round(processed / wall, 2) if wall else None, "model": clf.summary(), "events": events,
            "metrics": svc.metrics.snapshot() | {"modelInferenceMs": clf.avg_ms, "poseInferenceMs": stage.metrics.snapshot()["avg_pose_ms"]}}
