"""Pose estimation with a pretrained YOLO pose model, and association of poses with ByteTrack tracks.

Why YOLO pose: the service already runs Ultralytics YOLO on PyTorch, so a
YOLO11 pose checkpoint (COCO-17 keypoints) loads with the same library and
device handling, with no new dependency. RTMPose would need MMPose / ONNX
Runtime and a separate person detector pipeline. Any model that returns
COCO-17 keypoints can replace it behind :class:`PoseEstimator`.

The pose model runs once per analysed frame on the whole frame (not once per
person), and only when a camera has tracked people to describe. Its boxes are
matched to the tracker's boxes by overlap, so every pose keeps the camera's
own temporary track id; the pose model never creates track ids.

Like the person detector, one model instance serves every camera and
inference is serialised with a lock.
"""
from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Protocol

import numpy as np

from app.activity_recognition.schemas import NUM_KEYPOINTS, PoseDetection
from app.detection.types import BBox
from app.tracking.base import TrackedObject

log = logging.getLogger(__name__)


@dataclass
class PoseModelInfo:
    name: str
    version: str
    path: str
    device: str
    loaded: bool = False
    error: str | None = None
    loaded_at: float | None = None
    keypoints: int = NUM_KEYPOINTS
    inference_count: int = 0
    avg_inference_ms: float = 0.0
    errors: int = 0
    extra: dict = field(default_factory=dict)

    def record(self, ms: float) -> None:
        self.inference_count += 1
        n = min(self.inference_count, 50)
        self.avg_inference_ms = round(self.avg_inference_ms + (ms - self.avg_inference_ms) / n, 2)


class PoseEstimator(Protocol):
    info: PoseModelInfo

    def estimate(self, frame: np.ndarray) -> list[PoseDetection]: ...


class YoloPoseEstimator:
    """Ultralytics YOLO pose (e.g. ``yolo11n-pose.pt``): person boxes with 17 COCO keypoints."""

    def __init__(self, model_path: Path, device: str, confidence: float, image_size: int) -> None:
        import ultralytics

        self._path = Path(model_path)
        self._device = device
        self._confidence = confidence
        self._imgsz = image_size
        self._lock = threading.Lock()
        self._model = None
        self.info = PoseModelInfo(name=self._path.stem, version=f"ultralytics-{ultralytics.__version__}", path=str(self._path), device=device)

    def load(self) -> PoseModelInfo:
        """Load the weights once (no download here: ``python scripts/download_model.py --pose``). Records an error instead of raising."""
        from ultralytics import YOLO

        try:
            if not self._path.is_file():
                raise FileNotFoundError(f"Pose model weights not found at {self._path}. Run: python scripts/download_model.py --pose")
            model = YOLO(str(self._path))
            if getattr(model, "task", None) != "pose":
                raise ValueError(f"{self._path.name} is a '{getattr(model, 'task', '?')}' model, not a pose model.")
            kpt_shape = list(getattr(model.model, "kpt_shape", None) or model.model.yaml.get("kpt_shape", []))
            if kpt_shape and kpt_shape[0] != NUM_KEYPOINTS:
                raise ValueError(f"The pose model has {kpt_shape[0]} keypoints; COCO-17 keypoints are required.")
            model.predict(np.zeros((self._imgsz, self._imgsz, 3), dtype=np.uint8), imgsz=self._imgsz, device=self._device, verbose=False)
            self._model = model
            self.info.loaded, self.info.error, self.info.loaded_at = True, None, time.time()
            log.info("Pose model %s loaded on %s.", self.info.name, self._device)
        except Exception as exc:  # noqa: BLE001 - reported through /health/model
            self._model = None
            self.info.loaded, self.info.error = False, str(exc)
            log.error("Pose model could not be loaded: %s", exc)
        return self.info

    def estimate(self, frame: np.ndarray) -> list[PoseDetection]:
        if self._model is None:
            from app.core.errors import ModelNotReady

            raise ModelNotReady(self.info.error or "The pose model is not loaded.", "pose_model_not_ready")
        started = time.perf_counter()
        with self._lock:
            result = self._model.predict(frame, imgsz=self._imgsz, conf=self._confidence, device=self._device, classes=[0], verbose=False)[0]
        self.info.record((time.perf_counter() - started) * 1000)
        boxes, kpts = result.boxes, result.keypoints
        if boxes is None or kpts is None or len(boxes) == 0:
            return []
        xyxy = boxes.xyxy.cpu().numpy()
        conf = boxes.conf.cpu().numpy()
        data = kpts.data.cpu().numpy()            # (n, 17, 3): x, y, confidence
        h, w = frame.shape[:2]
        out = []
        for (x1, y1, x2, y2), c, k in zip(xyxy, conf, data):
            box = BBox(float(x1), float(y1), float(x2), float(y2)).clipped(w, h)
            if box.is_valid(w, h) and k.shape == (NUM_KEYPOINTS, 3):
                out.append(PoseDetection(box, round(float(c), 4), k.astype(np.float32)))
        return out


def iou(a: BBox, b: BBox) -> float:
    ix1, iy1, ix2, iy2 = max(a.x1, b.x1), max(a.y1, b.y1), min(a.x2, b.x2), min(a.y2, b.y2)
    inter = max(0.0, ix2 - ix1) * max(0.0, iy2 - iy1)
    union = a.width * a.height + b.width * b.height - inter
    return inter / union if union > 0 else 0.0


def associate_poses(tracks: list[TrackedObject], poses: list[PoseDetection], min_iou: float) -> dict[int, PoseDetection]:
    """Give each track at most one pose and each pose at most one track: greedy, highest overlap first.

    A pose that overlaps no track well enough is ignored (it can't be given a
    track id), and a track without a matching pose simply has no pose now.
    """
    pairs = sorted(((iou(t.bbox, p.bbox), ti, pi) for ti, t in enumerate(tracks) for pi, p in enumerate(poses)), reverse=True)
    used_t: set[int] = set()
    used_p: set[int] = set()
    out: dict[int, PoseDetection] = {}
    for score, ti, pi in pairs:
        if score < min_iou:
            break
        if ti in used_t or pi in used_p:
            continue
        used_t.add(ti)
        used_p.add(pi)
        out[tracks[ti].track_id] = poses[pi]
    return out
