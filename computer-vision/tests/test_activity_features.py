"""Pose features: angles and proportions per posture, partial visibility, missing keypoints."""
import pytest

from app.activity_recognition.feature_extractor import extract_features, knee_angle
from app.activity_recognition.pose_preprocessor import normalize_pose
from app.activity_recognition.schemas import FEATURE_NAMES, PoseDetection
from app.detection.types import BBox
from tests.poses import LEGS, person


def features(posture: str, **kw):  # noqa: ANN003
    box, k = person(posture, 300, 100, 240, **kw)
    return extract_features(normalize_pose(PoseDetection(BBox(*box), 0.9, k), BBox(*box), 0.35))


def test_standing_is_upright_with_straight_legs_and_lowered_arms():
    f = features("STANDING")
    assert f.torso_angle == pytest.approx(0, abs=2)
    assert knee_angle(f) == pytest.approx(180, abs=2)
    assert f.thigh_angle == pytest.approx(0, abs=2) and f.thigh_ratio > 0.6
    assert f.wrist_raise < 0 and f.wrist_reach < 0.3
    assert f.head_offset < -0.3                                # head well above the shoulders
    assert f.orientation > 0.6                                 # facing the camera (both shoulders apart)
    assert f.shoulder_tilt == pytest.approx(0, abs=2) and f.hip_tilt == pytest.approx(0, abs=2)


def test_sitting_has_a_horizontal_thigh_and_bent_knees():
    f = features("SITTING")
    assert f.thigh_angle > 80
    assert knee_angle(f) < 100
    assert f.torso_angle < 15


def test_bending_tilts_the_torso_forward_with_the_head_low():
    f = features("BENDING")
    assert f.torso_angle > 55
    assert f.head_offset > -0.1


def test_reaching_raises_a_wrist_above_the_shoulder():
    f = features("REACHING")
    assert f.wrist_raise > 0.5 and f.wrist_above_hip


def test_hidden_legs_leave_leg_features_missing_but_keep_the_rest():
    f = features("STANDING", hide=LEGS)
    assert f.left_knee_angle is None and f.right_knee_angle is None and knee_angle(f) is None
    assert f.thigh_angle is None and f.thigh_ratio is None
    assert f.torso_angle == pytest.approx(0, abs=2)


def test_one_hidden_side_uses_the_other():
    f = features("STANDING", hide=("left_shoulder", "left_elbow", "left_wrist", "left_hip", "left_knee", "left_ankle"))
    assert f.torso_angle is not None and f.right_knee_angle is not None and f.left_knee_angle is None
    assert f.orientation is None                               # needs both shoulders


def test_no_torso_means_no_posture_features():
    f = features("STANDING", hide=("left_shoulder", "right_shoulder"))
    assert f.torso_angle is None and f.hip_angle is None and f.wrist_raise is None


def test_feature_vector_has_a_fixed_order_and_nan_for_missing():
    import numpy as np

    v = features("STANDING", hide=LEGS).as_vector()
    assert v.shape == (len(FEATURE_NAMES),)
    assert np.isnan(v[FEATURE_NAMES.index("left_knee_angle")]) and not np.isnan(v[FEATURE_NAMES.index("torso_angle")])
