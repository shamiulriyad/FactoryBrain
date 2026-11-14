"""ByteTrack, through Ultralytics' implementation, fed with our own detections.

Each camera gets its own :class:`ByteTrackTracker` (its own state, track
buffer and frame counter), so cameras never influence each other. Track
numbers come from Ultralytics' process-wide counter: they are unique within
the process, start again after a restart, and mean nothing outside the
camera. The session prefixes them with a per-session tag before publishing.
"""
from __future__ import annotations

import numpy as np

from app.detection.types import BBox, Detection
from app.tracking.base import TrackedObject


class _Batch:
    """The minimal "results" object BYTETracker.update() reads: conf, xywh, cls (and slicing)."""

    def __init__(self, xyxy: np.ndarray, conf: np.ndarray, cls: np.ndarray) -> None:
        self.xyxy = xyxy
        self.conf = conf
        self.cls = cls

    @property
    def xywh(self) -> np.ndarray:
        if len(self.xyxy) == 0:
            return np.zeros((0, 4), dtype=np.float32)
        x1, y1, x2, y2 = self.xyxy.T
        return np.stack([(x1 + x2) / 2, (y1 + y2) / 2, x2 - x1, y2 - y1], axis=1)

    def __len__(self) -> int:
        return len(self.conf)

    def __getitem__(self, idx):  # noqa: ANN001 - numpy index
        return _Batch(self.xyxy[idx], self.conf[idx], self.cls[idx])


class ByteTrackTracker:
    name = "bytetrack"

    def __init__(self, frame_rate: float, track_buffer_seconds: float = 2.0, high_thresh: float = 0.4, low_thresh: float = 0.1,
                 new_track_thresh: float = 0.45, match_thresh: float = 0.8) -> None:
        self._frame_rate = max(1, int(round(frame_rate)))
        self._args = dict(
            tracker_type="bytetrack",
            track_high_thresh=high_thresh,
            track_low_thresh=low_thresh,
            new_track_thresh=new_track_thresh,
            # Frames a lost track is kept for re-identification after a short occlusion.
            track_buffer=max(1, int(round(track_buffer_seconds * self._frame_rate))),
            match_thresh=match_thresh,
            fuse_score=True,
        )
        self._tracker = self._new()

    def _new(self):  # noqa: ANN202
        from ultralytics.trackers.byte_tracker import BYTETracker
        from ultralytics.utils import IterableSimpleNamespace

        return BYTETracker(IterableSimpleNamespace(**self._args), frame_rate=self._frame_rate)

    def update(self, detections: list[Detection], frame_w: int, frame_h: int) -> list[TrackedObject]:
        if detections:
            xyxy = np.array([[d.bbox.x1, d.bbox.y1, d.bbox.x2, d.bbox.y2] for d in detections], dtype=np.float32)
            conf = np.array([d.confidence for d in detections], dtype=np.float32)
            cls = np.array([d.class_id for d in detections], dtype=np.float32)
        else:
            xyxy, conf, cls = np.zeros((0, 4), np.float32), np.zeros((0,), np.float32), np.zeros((0,), np.float32)
        rows = self._tracker.update(_Batch(xyxy, conf, cls))
        out: list[TrackedObject] = []
        for row in rows if rows is not None else []:
            x1, y1, x2, y2, track_id, score, cls_id = (float(v) for v in row[:7])
            box = BBox(x1, y1, x2, y2).clipped(frame_w, frame_h)
            if box.is_valid(frame_w, frame_h):
                out.append(TrackedObject(int(track_id), box, round(score, 4), int(cls_id)))
        return out

    def reset(self) -> None:
        self._tracker = self._new()
