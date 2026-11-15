"""Synthetic COCO-17 skeletons for unit tests (the pose model itself is mocked in unit tests).

Templates are in box-height units from the top-centre of the person's box
(x right, y down). ``skeleton(...)`` places one in a pixel box and returns the
``(17, 3)`` keypoints array a pose model would give.
"""
from __future__ import annotations

import numpy as np

from app.activity_recognition.schemas import KP, PoseDetection
from app.detection.types import BBox

_STANDING = {
    "nose": (0.0, 0.06), "left_eye": (0.02, 0.05), "right_eye": (-0.02, 0.05), "left_ear": (0.04, 0.06), "right_ear": (-0.04, 0.06),
    "left_shoulder": (0.11, 0.20), "right_shoulder": (-0.11, 0.20), "left_elbow": (0.13, 0.35), "right_elbow": (-0.13, 0.35),
    "left_wrist": (0.13, 0.48), "right_wrist": (-0.13, 0.48), "left_hip": (0.08, 0.52), "right_hip": (-0.08, 0.52),
    "left_knee": (0.08, 0.74), "right_knee": (-0.08, 0.74), "left_ankle": (0.08, 0.97), "right_ankle": (-0.08, 0.97),
}
# Seen from the side: thigh horizontal, shin vertical.
_SITTING = {
    "nose": (0.05, 0.08), "left_eye": (0.06, 0.07), "right_eye": (0.06, 0.07), "left_ear": (0.0, 0.08), "right_ear": (0.0, 0.08),
    "left_shoulder": (0.0, 0.25), "right_shoulder": (-0.02, 0.25), "left_elbow": (0.05, 0.42), "right_elbow": (0.03, 0.42),
    "left_wrist": (0.18, 0.50), "right_wrist": (0.16, 0.50), "left_hip": (0.0, 0.60), "right_hip": (-0.02, 0.60),
    "left_knee": (0.30, 0.60), "right_knee": (0.28, 0.61), "left_ankle": (0.30, 0.95), "right_ankle": (0.28, 0.96),
}
# Torso tilted ~60 degrees forward, legs straight.
_BENDING = {
    "nose": (0.42, 0.42), "left_eye": (0.41, 0.40), "right_eye": (0.41, 0.40), "left_ear": (0.38, 0.38), "right_ear": (0.38, 0.38),
    "left_shoulder": (0.30, 0.35), "right_shoulder": (0.28, 0.36), "left_elbow": (0.32, 0.52), "right_elbow": (0.30, 0.53),
    "left_wrist": (0.33, 0.66), "right_wrist": (0.31, 0.67), "left_hip": (0.0, 0.50), "right_hip": (-0.02, 0.50),
    "left_knee": (0.0, 0.73), "right_knee": (-0.02, 0.73), "left_ankle": (0.0, 0.97), "right_ankle": (-0.02, 0.97),
}
# Standing, right arm raised above the head.
_REACHING = dict(_STANDING, right_elbow=(-0.14, 0.10), right_wrist=(-0.14, -0.02))

TEMPLATES = {"STANDING": _STANDING, "SITTING": _SITTING, "BENDING": _BENDING, "REACHING": _REACHING}


def person(posture: str, x: float, top: float, h: float, conf: float = 0.9, hide: tuple[str, ...] = (), noise: float = 0.0,
           seed: int = 0) -> tuple[tuple[float, float, float, float], np.ndarray]:
    """A person of height ``h`` px whose template origin is at (x, top): returns (tight box, (17, 3) keypoints)."""
    rng = np.random.default_rng(seed)
    k = np.zeros((17, 3), np.float32)
    for name, (dx, dy) in TEMPLATES[posture].items():
        jitter = rng.normal(0, noise, 2) if noise else (0.0, 0.0)
        k[KP[name]] = (x + (dx + jitter[0]) * h, top + (dy + jitter[1]) * h, conf)
    pad = 0.04 * h
    box = (float(k[:, 0].min() - pad), float(top - 0.02 * h), float(k[:, 0].max() + pad), float(k[:, 1].max() + pad))
    for name in hide:
        k[KP[name]] = (0.0, 0.0, 0.05)
    return box, k


def pose(posture: str, x: float, top: float, h: float, **kw) -> PoseDetection:  # noqa: ANN003
    box, k = person(posture, x, top, h, **kw)
    return PoseDetection(BBox(*box), 0.9, k)


LEGS = ("left_knee", "right_knee", "left_ankle", "right_ankle")
