"""Builds observation sequences (synthetic poses through the real preprocessing and feature code)."""
from __future__ import annotations

from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.feature_extractor import extract_features
from app.activity_recognition.pose_preprocessor import normalize_pose
from app.activity_recognition.schemas import PoseDetection, PoseObservation
from app.detection.types import BBox
from tests.poses import person

CFG = ActivityConfig(enabled=True)


def observe(posture: str, n: int = 12, fps: float = 4.0, speed_px: float = 0.0, h: float = 200, t0: float = 1000.0,
            camera: str = "cam-1", track: int = 1, with_pose: bool | list[bool] = True, x0: float = 300, **kw) -> list[PoseObservation]:  # noqa: ANN003
    """``n`` observations ``1/fps`` apart; the person moves ``speed_px`` pixels per second to the right."""
    out = []
    for i in range(n):
        t = t0 + i / fps
        box, k = person(posture, x0 + speed_px * (i / fps), 100, h, seed=i, **kw)
        has = with_pose[i] if isinstance(with_pose, list) else with_pose
        features = quality = None
        if has:
            norm = normalize_pose(PoseDetection(BBox(*box), 0.9, k), BBox(*box), CFG.keypoint_confidence)
            quality = norm.quality
            if quality.usable:
                features = extract_features(norm)
        out.append(PoseObservation(camera, track, t, BBox(*box), features, quality))
    return out
