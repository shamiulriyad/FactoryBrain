"""Make two short test videos from the sample photos that ship with Ultralytics (real people).

    python scripts/make_sample_video.py

* data/samples/people_walkthrough.mp4 (12 s): a wide strip of two street
  photos pans across the frame, so several people enter on one side and leave
  on the other; a dark bar passes in front of them for ~1 s (occlusion).
* data/samples/two_people.mp4 (8 s): a slow zoom on a photo of two people
  (a second, independent camera source).

These are real photographs, so detections on them are genuine model output;
nothing here fabricates a detection. For factory testing, record your own
footage (with consent) and put it in data/ or upload it to a camera.
"""
from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np

OUT = Path(__file__).resolve().parents[1] / "data" / "samples"
W, H, FPS = 1280, 720, 15


def _assets() -> tuple[np.ndarray, np.ndarray]:
    from ultralytics.utils import ASSETS

    bus = cv2.imread(str(ASSETS / "bus.jpg"))
    zidane = cv2.imread(str(ASSETS / "zidane.jpg"))
    if bus is None or zidane is None:
        raise SystemExit("Ultralytics sample images not found.")
    return bus, zidane


def _fit_height(img: np.ndarray, h: int) -> np.ndarray:
    return cv2.resize(img, (int(img.shape[1] * h / img.shape[0]), h), interpolation=cv2.INTER_AREA)


def _writer(path: Path) -> cv2.VideoWriter:
    path.parent.mkdir(parents=True, exist_ok=True)
    w = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), FPS, (W, H))
    if not w.isOpened():
        raise SystemExit(f"Can't write {path}")
    return w


def walkthrough(bus: np.ndarray, zidane: np.ndarray) -> Path:
    path = OUT / "people_walkthrough.mp4"
    gap = np.full((H, 500, 3), (70, 74, 80), np.uint8)
    strip = np.hstack([np.full((H, W, 3), (70, 74, 80), np.uint8), _fit_height(bus, H), gap, _fit_height(zidane, H),
                       np.full((H, W, 3), (70, 74, 80), np.uint8)])
    frames = 12 * FPS
    span = strip.shape[1] - W
    w = _writer(path)
    for i in range(frames):
        x = int(span * i / (frames - 1))
        frame = strip[:, x:x + W].copy()
        if int(5.0 * FPS) <= i < int(6.0 * FPS):          # ~1 s occlusion in the middle of the frame
            frame[:, 420:860] = (25, 25, 25)
        w.write(frame)
    w.release()
    return path


def two_people(zidane: np.ndarray) -> Path:
    path = OUT / "two_people.mp4"
    base = cv2.resize(zidane, (W, H))
    w = _writer(path)
    frames = 8 * FPS
    for i in range(frames):
        z = 1.0 + 0.15 * i / frames
        m = cv2.getRotationMatrix2D((W / 2, H / 2), 0, z)
        w.write(cv2.warpAffine(base, m, (W, H), borderMode=cv2.BORDER_REPLICATE))
    w.release()
    return path


if __name__ == "__main__":
    b, z = _assets()
    for p in (walkthrough(b, z), two_people(z)):
        print(f"Wrote {p} ({p.stat().st_size / 2**20:.1f} MB)")
