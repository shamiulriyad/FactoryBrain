"""Validates pose keypoints and normalises them relative to the person's box.

* Malformed output (wrong shape, NaN / infinite values) is rejected.
* A keypoint counts as visible only with confidence ≥ the keypoint threshold
  and a position inside (or just around) the person's box; everything else
  is treated as missing, never guessed.
* Coordinates become box-height units from the box's top-left corner, so a
  person far from the camera and one close to it give the same numbers, and
  angles are not distorted by the box's aspect ratio.
* The quality summary says which body parts are visible. A pose without a
  shoulder and a hip (no torso) is not usable for posture.
"""
from __future__ import annotations

import numpy as np

from app.activity_recognition.schemas import KP, NUM_KEYPOINTS, NormalizedPose, PoseDetection, PoseQuality
from app.detection.types import BBox

#: How far outside the box (as a fraction of its size) a keypoint may lie and still count (limbs often poke out).
BOX_MARGIN = 0.25
#: Fewest visible keypoints for a usable pose (besides needing the torso).
MIN_VISIBLE = 5

_HEAD = [KP[n] for n in ("nose", "left_eye", "right_eye", "left_ear", "right_ear")]


class InvalidPose(ValueError):
    pass


def validate_keypoints(kpts: np.ndarray) -> np.ndarray:
    """``(17, 3)`` float array with finite values and confidences in 0–1, or :class:`InvalidPose`."""
    arr = np.asarray(kpts, dtype=np.float32)
    if arr.shape != (NUM_KEYPOINTS, 3):
        raise InvalidPose(f"Expected {NUM_KEYPOINTS} keypoints with (x, y, confidence), got shape {arr.shape}.")
    if not np.all(np.isfinite(arr)):
        raise InvalidPose("Keypoints contain NaN or infinite values.")
    if np.any(arr[:, 2] < 0) or np.any(arr[:, 2] > 1.0001):
        raise InvalidPose("Keypoint confidences must be between 0 and 1.")
    return arr


def normalize_pose(pose: PoseDetection, track_box: BBox, keypoint_confidence: float) -> NormalizedPose:
    """Normalise ``pose`` against the *track's* box (the box the track id belongs to)."""
    kpts = validate_keypoints(pose.keypoints)
    box = track_box
    h = max(1.0, box.height)
    w = max(1.0, box.width)
    x, y, conf = kpts[:, 0], kpts[:, 1], np.clip(kpts[:, 2], 0.0, 1.0)
    inside = ((x >= box.x1 - BOX_MARGIN * w) & (x <= box.x2 + BOX_MARGIN * w)
              & (y >= box.y1 - BOX_MARGIN * h) & (y <= box.y2 + BOX_MARGIN * h))
    # (0, 0) is what pose models emit for keypoints they did not place.
    placed = ~((x == 0) & (y == 0))
    visible = (conf >= keypoint_confidence) & inside & placed
    xy = np.stack([(x - box.x1) / h, (y - box.y1) / h], axis=1).astype(np.float32)
    xy[~visible] = np.nan
    return NormalizedPose(xy=xy, confidence=conf.astype(np.float32), visible=visible, box_aspect=h / w, quality=assess(visible, conf))


def assess(visible: np.ndarray, conf: np.ndarray) -> PoseQuality:
    def v(name: str) -> bool:
        return bool(visible[KP[name]])

    shoulders = v("left_shoulder") or v("right_shoulder")
    hips = v("left_hip") or v("right_hip")
    arms = any(v(f"{s}_shoulder") and (v(f"{s}_elbow") or v(f"{s}_wrist")) for s in ("left", "right"))
    legs = any(v(f"{s}_hip") and v(f"{s}_knee") for s in ("left", "right"))
    head = any(bool(visible[i]) for i in _HEAD)
    n = int(visible.sum())
    mean = float(conf[visible].mean()) if n else 0.0
    torso = shoulders and hips
    return PoseQuality(visible_keypoints=n, mean_confidence=mean, has_head=head, has_torso=torso, has_arms=arms, has_legs=legs,
                       usable=torso and n >= MIN_VISIBLE)
