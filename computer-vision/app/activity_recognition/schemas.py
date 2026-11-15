"""Data shapes of activity recognition: labels, keypoints, observations, predictions and state changes.

Coordinates: pose detections are in frame pixels. Normalised poses are in
*box-height units* relative to the person's box (x and y both divided by the
box height, so angles and proportions are preserved and the person's size in
the image does not matter). Times are UTC epoch seconds (``time.time()``).
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum

import numpy as np

from app.detection.types import BBox


class ActivityLabel(str, Enum):
    STANDING = "STANDING"
    SITTING = "SITTING"
    WALKING = "WALKING"
    BENDING = "BENDING"
    REACHING = "REACHING"
    UNKNOWN = "UNKNOWN"


#: What the baseline can predict (UNKNOWN aside).
SUPPORTED_ACTIVITIES: tuple[ActivityLabel, ...] = (ActivityLabel.STANDING, ActivityLabel.SITTING, ActivityLabel.WALKING,
                                                   ActivityLabel.BENDING, ActivityLabel.REACHING)

#: Reserved for trained, validated models later. Generic pose estimation can't recognise these reliably,
#: so nothing in this service produces them.
FUTURE_ACTIVITIES: tuple[str, ...] = ("SEWING_LIKE_MOTION", "HANDLING_MATERIAL", "OPERATING_MACHINE", "PICKING_ITEM")


class UncertainReason(str, Enum):
    NO_POSE = "NO_POSE"                          # no pose matched the track in the window
    INSUFFICIENT_POSE = "INSUFFICIENT_POSE"      # too few usable poses (occlusion, partial view, low keypoint confidence)
    INSUFFICIENT_HISTORY = "INSUFFICIENT_HISTORY"  # observed for too short a time
    LOW_CONFIDENCE = "LOW_CONFIDENCE"            # best activity below the confidence threshold
    CONFLICTING = "CONFLICTING"                  # two activities about equally likely / frames disagree


# COCO-17 keypoints, the order YOLO pose models output.
KEYPOINT_NAMES: tuple[str, ...] = (
    "nose", "left_eye", "right_eye", "left_ear", "right_ear", "left_shoulder", "right_shoulder", "left_elbow", "right_elbow",
    "left_wrist", "right_wrist", "left_hip", "right_hip", "left_knee", "right_knee", "left_ankle", "right_ankle",
)
NUM_KEYPOINTS = len(KEYPOINT_NAMES)
KP = {name: i for i, name in enumerate(KEYPOINT_NAMES)}

#: Lines drawn in the development preview.
SKELETON: tuple[tuple[int, int], ...] = (
    (KP["left_shoulder"], KP["right_shoulder"]), (KP["left_shoulder"], KP["left_elbow"]), (KP["left_elbow"], KP["left_wrist"]),
    (KP["right_shoulder"], KP["right_elbow"]), (KP["right_elbow"], KP["right_wrist"]), (KP["left_shoulder"], KP["left_hip"]),
    (KP["right_shoulder"], KP["right_hip"]), (KP["left_hip"], KP["right_hip"]), (KP["left_hip"], KP["left_knee"]),
    (KP["left_knee"], KP["left_ankle"]), (KP["right_hip"], KP["right_knee"]), (KP["right_knee"], KP["right_ankle"]),
    (KP["nose"], KP["left_eye"]), (KP["nose"], KP["right_eye"]), (KP["left_eye"], KP["left_ear"]), (KP["right_eye"], KP["right_ear"]),
)


@dataclass(frozen=True)
class PoseDetection:
    """One person found by the pose model: its box and 17 keypoints ``(x, y, confidence)`` in frame pixels."""

    bbox: BBox
    confidence: float
    keypoints: np.ndarray          # shape (17, 3)


@dataclass(frozen=True)
class PoseQuality:
    """How much of the body the pose shows, and how sure the model is about it."""

    visible_keypoints: int
    mean_confidence: float         # over visible keypoints (0 when none)
    has_head: bool
    has_torso: bool                # at least one shoulder and one hip
    has_arms: bool                 # a shoulder with its elbow or wrist
    has_legs: bool                 # a hip with its knee, on at least one side
    usable: bool                   # enough to describe posture

    def as_dict(self) -> dict:
        return {"visibleKeypoints": self.visible_keypoints, "meanKeypointConfidence": round(self.mean_confidence, 3),
                "head": self.has_head, "torso": self.has_torso, "arms": self.has_arms, "legs": self.has_legs}


@dataclass(frozen=True)
class NormalizedPose:
    """Keypoints relative to the person's box: ``xy`` in box-height units from the box's top-left corner."""

    xy: np.ndarray                 # (17, 2); NaN where not visible
    confidence: np.ndarray         # (17,)
    visible: np.ndarray            # (17,) bool
    box_aspect: float              # box height / width
    quality: PoseQuality

    def point(self, name: str) -> np.ndarray | None:
        i = KP[name]
        return self.xy[i] if self.visible[i] else None

    def box_relative(self) -> np.ndarray:
        """Keypoints as 0–1 fractions of the box's width and height (NaN where not visible)."""
        out = self.xy.copy()
        out[:, 0] = out[:, 0] * self.box_aspect
        return out


@dataclass(frozen=True)
class PoseObservation:
    """One look at one track: where it was, and its pose features when a usable pose matched it."""

    camera_id: str
    track_id: int
    timestamp: float
    bbox: BBox                     # the track's box (pixels)
    features: "FrameFeatures | None"   # None: no usable pose for the track at this moment
    quality: PoseQuality | None

    @property
    def has_pose(self) -> bool:
        return self.features is not None

    @property
    def anchor(self) -> tuple[float, float]:
        """Where the person stands (bottom-centre of the box, pixels): used for movement."""
        return self.bbox.bottom_centre()

    @property
    def height(self) -> float:
        return max(1.0, self.bbox.height)


@dataclass(frozen=True)
class FrameFeatures:
    """Pose features of one frame. ``None`` means the keypoints needed weren't visible."""

    torso_angle: float | None = None          # degrees from vertical (0 = upright)
    shoulder_tilt: float | None = None        # degrees from horizontal
    hip_tilt: float | None = None
    left_knee_angle: float | None = None      # hip-knee-ankle, degrees (180 = straight leg)
    right_knee_angle: float | None = None
    hip_angle: float | None = None            # shoulder-hip-knee, degrees (180 = straight body)
    thigh_angle: float | None = None          # hip→knee from vertical, degrees (90 = horizontal thigh)
    thigh_ratio: float | None = None          # thigh length / torso length (small = foreshortened, e.g. sitting facing the camera)
    wrist_raise: float | None = None          # highest wrist above its shoulder, in torso lengths (> 0 = above)
    wrist_reach: float | None = None          # widest horizontal wrist-to-shoulder distance, in torso lengths
    wrist_above_hip: bool | None = None       # the reaching wrist is above hip level
    head_offset: float | None = None          # head below the shoulder line, in torso lengths (< 0 = above, the usual)
    orientation: float | None = None          # shoulder width / torso length (~0.7+ facing the camera, ~0 side-on)
    box_aspect: float | None = None           # box height / width
    keypoint_confidence: float = 0.0          # mean confidence of visible keypoints

    def as_vector(self) -> np.ndarray:
        """Fixed-order numeric vector (NaN = missing) for a learned model; see FEATURE_NAMES."""
        vals = []
        for name in FEATURE_NAMES:
            v = getattr(self, name)
            vals.append(np.nan if v is None else float(v))
        return np.array(vals, dtype=np.float32)


FEATURE_NAMES: tuple[str, ...] = (
    "torso_angle", "shoulder_tilt", "hip_tilt", "left_knee_angle", "right_knee_angle", "hip_angle", "thigh_angle", "thigh_ratio",
    "wrist_raise", "wrist_reach", "wrist_above_hip", "head_offset", "orientation", "box_aspect", "keypoint_confidence",
)


@dataclass(frozen=True)
class WindowQuality:
    """What the classifier had to go on for one prediction."""

    observations: int              # looks at the track in the window
    pose_observations: int         # of which had a usable pose
    span_seconds: float
    mean_keypoint_confidence: float
    legs_visible_ratio: float
    speed: float | None            # body heights per second (None: not enough history)
    consistency: float             # share of frames agreeing with the predicted activity

    def as_dict(self) -> dict:
        return {"observations": self.observations, "poseObservations": self.pose_observations, "windowSeconds": round(self.span_seconds, 2),
                "meanKeypointConfidence": round(self.mean_keypoint_confidence, 3), "legsVisibleRatio": round(self.legs_visible_ratio, 2),
                "speedBodyHeightsPerSecond": None if self.speed is None else round(self.speed, 3), "consistency": round(self.consistency, 2)}


@dataclass(frozen=True)
class ActivityPrediction:
    """The classifier's answer for one track over its current window. ``label`` is UNKNOWN when uncertain."""

    label: ActivityLabel
    confidence: float
    candidate: ActivityLabel | None            # best known activity, even when ``label`` is UNKNOWN
    scores: dict[str, float]
    quality: WindowQuality
    window_start: float
    window_end: float
    reason: UncertainReason | None = None      # why it is UNKNOWN

    @property
    def uncertain(self) -> bool:
        return self.label == ActivityLabel.UNKNOWN


@dataclass(frozen=True)
class ActivityUpdate:
    """Something the state manager wants published.

    kind: STATE_CHANGED (a new known activity was accepted), UNCERTAIN (the
    track's activity became UNKNOWN), CLASSIFIED (periodic confirmation of a
    stable activity).
    """

    kind: str
    camera_id: str
    track_id: int
    activity: ActivityLabel
    previous: ActivityLabel | None
    confidence: float
    prediction: ActivityPrediction
    state_since: float
    previous_duration: float | None = None
    bbox: BBox | None = None


@dataclass
class ActivityTransition:
    """One entry of a track's activity history."""

    at: float
    from_activity: ActivityLabel | None
    to_activity: ActivityLabel
    confidence: float

    def as_dict(self) -> dict:
        # Labels are ActivityLabel members here, or plain strings when another recogniser (garment activities) reuses the state manager.
        def v(x):  # noqa: ANN001, ANN202
            return None if x is None else getattr(x, "value", x)

        return {"at": self.at, "from": v(self.from_activity), "to": v(self.to_activity), "confidence": round(self.confidence, 3)}


@dataclass
class TrackActivityState:
    camera_id: str
    track_id: int
    current: ActivityLabel | None = None
    since: float = 0.0
    confidence: float = 0.0
    last_prediction: ActivityPrediction | None = None
    pending: ActivityLabel | None = None
    pending_since: float = 0.0
    pending_count: int = 0
    last_report: float = 0.0
    last_seen: float = 0.0
    history: list[ActivityTransition] = field(default_factory=list)
