"""YOLO person detection.

One model instance serves every camera: loading a copy per camera would
multiply memory for no gain on a single CPU / GPU. Inference is serialised
with a lock (the model is not thread-safe), so camera threads take turns;
the per-camera processing FPS keeps the total load bounded.
"""
from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Protocol

import numpy as np

from app.detection.types import PERSON_CLASS_ID, Detection, keep_persons

log = logging.getLogger(__name__)


@dataclass
class ModelInfo:
    name: str
    version: str
    path: str
    device: str
    loaded: bool = False
    error: str | None = None
    loaded_at: float | None = None
    classes: list[str] = field(default_factory=lambda: ["person"])
    inference_count: int = 0
    avg_inference_ms: float = 0.0


class PersonDetector(Protocol):
    info: ModelInfo

    def detect(self, frame: np.ndarray, camera_id: str, frame_ts: float) -> list[Detection]: ...


class YoloPersonDetector:
    """Ultralytics YOLO restricted to the COCO person class."""

    def __init__(self, model_path: Path, device: str, confidence: float, image_size: int) -> None:
        import ultralytics

        self._path = Path(model_path)
        self._device = device
        self._confidence = confidence
        self._imgsz = image_size
        self._lock = threading.Lock()
        self._model = None
        self.info = ModelInfo(name=self._path.stem, version=f"ultralytics-{ultralytics.__version__}", path=str(self._path), device=device)

    def load(self) -> ModelInfo:
        """Load the weights (no download here: see scripts/download_model.py). Records an error instead of raising."""
        from ultralytics import YOLO

        try:
            if not self._path.is_file():
                raise FileNotFoundError(f"Model weights not found at {self._path}. Run: python scripts/download_model.py")
            model = YOLO(str(self._path))
            names = getattr(model, "names", {}) or {}
            if names.get(PERSON_CLASS_ID) != "person":
                raise ValueError("The model's class 0 is not 'person': use a COCO-trained YOLO model.")
            # Warm up once so the first real frame isn't slow.
            model.predict(np.zeros((self._imgsz, self._imgsz, 3), dtype=np.uint8), imgsz=self._imgsz, device=self._device, classes=[PERSON_CLASS_ID], verbose=False)
            self._model = model
            self.info.loaded, self.info.error, self.info.loaded_at = True, None, time.time()
            log.info("Model %s loaded on %s.", self.info.name, self._device)
        except Exception as exc:  # noqa: BLE001 - reported through /health/model
            self._model = None
            self.info.loaded, self.info.error = False, str(exc)
            log.error("Model could not be loaded: %s", exc)
        return self.info

    def detect(self, frame: np.ndarray, camera_id: str, frame_ts: float) -> list[Detection]:
        if self._model is None:
            from app.core.errors import ModelNotReady

            raise ModelNotReady(self.info.error or "The model is not loaded.")
        h, w = frame.shape[:2]
        started = time.perf_counter()
        with self._lock:
            result = self._model.predict(frame, imgsz=self._imgsz, conf=self._confidence, device=self._device,
                                         classes=[PERSON_CLASS_ID], verbose=False)[0]
        elapsed = (time.perf_counter() - started) * 1000
        self.info.inference_count += 1
        n = self.info.inference_count
        self.info.avg_inference_ms = round(self.info.avg_inference_ms + (elapsed - self.info.avg_inference_ms) / min(n, 50), 2)
        boxes = result.boxes
        if boxes is None or len(boxes) == 0:
            return []
        xyxy = boxes.xyxy.cpu().numpy()
        conf = boxes.conf.cpu().numpy()
        cls = boxes.cls.cpu().numpy().astype(int)
        rows = [(float(a), float(b), float(c), float(d), float(e), int(f)) for (a, b, c, d), e, f in zip(xyxy, conf, cls)]
        return keep_persons(rows, result.names, w, h, self._confidence, camera_id, frame_ts)
