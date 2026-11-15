"""Pose features of one frame, from a normalised pose.

Angles are in degrees and lengths in *torso lengths* (shoulder-centre to
hip-centre), so the numbers describe body shape rather than image size.
Each feature uses only the keypoints it needs: when one side of the body is
hidden the other side is used, and when neither is visible the feature is
``None`` (missing), never a guess. Movement over time is not a frame feature;
the temporal classifier measures it from the track's positions.
"""
from __future__ import annotations

import math

import numpy as np

from app.activity_recognition.schemas import FrameFeatures, NormalizedPose

_MIN_TORSO = 0.04       # box-height units: a shorter torso means the keypoints are degenerate
_MIN_SHOULDER_WIDTH = 0.02


def _mid(p: NormalizedPose, a: str, b: str) -> np.ndarray | None:
    pa, pb = p.point(a), p.point(b)
    if pa is not None and pb is not None:
        return (pa + pb) / 2.0
    return pa if pa is not None else pb


def _angle_at(v: np.ndarray, a: np.ndarray, b: np.ndarray) -> float | None:
    """Angle a-v-b in degrees (180 = straight line)."""
    u, w = a - v, b - v
    nu, nw = float(np.linalg.norm(u)), float(np.linalg.norm(w))
    if nu < 1e-6 or nw < 1e-6:
        return None
    c = float(np.dot(u, w) / (nu * nw))
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def _from_vertical(vec: np.ndarray, up: bool) -> float:
    """Angle of ``vec`` from straight up (``up``) or straight down, 0–180 degrees. Image y grows downwards."""
    dx, dy = float(vec[0]), float(vec[1])
    return math.degrees(math.atan2(abs(dx), -dy if up else dy))


def _mean(values: list[float | None]) -> float | None:
    vals = [v for v in values if v is not None]
    return float(sum(vals) / len(vals)) if vals else None


def extract_features(p: NormalizedPose) -> FrameFeatures:
    sh = _mid(p, "left_shoulder", "right_shoulder")
    hip = _mid(p, "left_hip", "right_hip")
    torso_len = float(np.linalg.norm(sh - hip)) if sh is not None and hip is not None else 0.0
    kp_conf = p.quality.mean_confidence
    if torso_len < _MIN_TORSO:
        return FrameFeatures(box_aspect=p.box_aspect, keypoint_confidence=kp_conf)

    torso_angle = _from_vertical(sh - hip, up=True)

    def tilt(a: str, b: str) -> float | None:
        pa, pb = p.point(a), p.point(b)
        if pa is None or pb is None or np.linalg.norm(pa - pb) < _MIN_SHOULDER_WIDTH:
            return None
        return math.degrees(math.atan2(abs(float(pa[1] - pb[1])), abs(float(pa[0] - pb[0]))))

    knees: dict[str, float | None] = {}
    hips: list[float | None] = []
    thighs: list[float | None] = []
    thigh_lens: list[float | None] = []
    for side in ("left", "right"):
        h, k, a, s = p.point(f"{side}_hip"), p.point(f"{side}_knee"), p.point(f"{side}_ankle"), p.point(f"{side}_shoulder")
        knees[side] = _angle_at(k, h, a) if h is not None and k is not None and a is not None else None
        if h is not None and k is not None:
            hips.append(_angle_at(h, s if s is not None else sh, k))
            thighs.append(_from_vertical(k - h, up=False))
            thigh_lens.append(float(np.linalg.norm(k - h)) / torso_len)

    raise_, reach, above_hip = None, None, None
    for side in ("left", "right"):
        s, wr = p.point(f"{side}_shoulder"), p.point(f"{side}_wrist")
        if s is None or wr is None:
            continue
        r = float(s[1] - wr[1]) / torso_len
        raise_ = r if raise_ is None else max(raise_, r)
        x = abs(float(wr[0] - s[0])) / torso_len
        if reach is None or x > reach:
            reach, above_hip = x, bool(wr[1] < hip[1])

    head_pts = [p.point(n) for n in ("nose", "left_eye", "right_eye", "left_ear", "right_ear")]
    head_pts = [h for h in head_pts if h is not None]
    head_offset = (float(np.mean([h[1] for h in head_pts])) - float(sh[1])) / torso_len if head_pts else None

    ls, rs = p.point("left_shoulder"), p.point("right_shoulder")
    orientation = float(np.linalg.norm(ls - rs)) / torso_len if ls is not None and rs is not None else None

    return FrameFeatures(
        torso_angle=torso_angle, shoulder_tilt=tilt("left_shoulder", "right_shoulder"), hip_tilt=tilt("left_hip", "right_hip"),
        left_knee_angle=knees["left"], right_knee_angle=knees["right"], hip_angle=_mean(hips), thigh_angle=_mean(thighs),
        thigh_ratio=_mean(thigh_lens), wrist_raise=raise_, wrist_reach=reach, wrist_above_hip=above_hip, head_offset=head_offset,
        orientation=orientation, box_aspect=p.box_aspect, keypoint_confidence=kp_conf,
    )


def knee_angle(f: FrameFeatures) -> float | None:
    """The straighter of the visible knees (a person standing on one leg still stands)."""
    vals = [v for v in (f.left_knee_angle, f.right_knee_angle) if v is not None]
    return max(vals) if vals else None
