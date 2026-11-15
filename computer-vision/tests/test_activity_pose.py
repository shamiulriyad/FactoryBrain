"""Pose keypoints: validation, normalisation relative to the person's box, missing keypoints, association with track ids."""
import numpy as np
import pytest

from app.activity_recognition.pose_estimator import YoloPoseEstimator, associate_poses, iou
from app.activity_recognition.pose_preprocessor import InvalidPose, normalize_pose, validate_keypoints
from app.activity_recognition.schemas import KP, PoseDetection
from app.detection.types import BBox
from app.tracking.base import TrackedObject
from tests.poses import LEGS, person, pose


@pytest.mark.parametrize("bad", [
    np.zeros((16, 3)),                                        # wrong number of keypoints
    np.zeros((17, 2)),                                        # no confidences
    np.full((17, 3), np.nan),                                 # NaN
    np.concatenate([np.zeros((17, 2)), np.full((17, 1), 1.7)], axis=1),   # confidence > 1
])
def test_malformed_keypoints_are_rejected(bad):
    with pytest.raises(InvalidPose):
        validate_keypoints(bad)


def test_keypoints_are_normalised_to_the_box_and_independent_of_image_scale():
    box_a, k_a = person("STANDING", 200, 100, 200)
    box_b, k_b = person("STANDING", 800, 50, 400)        # same posture, twice as big, elsewhere in the frame
    na = normalize_pose(PoseDetection(BBox(*box_a), 0.9, k_a), BBox(*box_a), 0.35)
    nb = normalize_pose(PoseDetection(BBox(*box_b), 0.9, k_b), BBox(*box_b), 0.35)
    assert na.quality.usable and na.quality.visible_keypoints == 17
    assert np.allclose(na.xy, nb.xy, atol=1e-3)                   # box-height units: scale and position don't matter
    rel = na.box_relative()
    assert np.nanmin(rel) >= -0.01 and np.nanmax(rel) <= 1.01      # 0-1 fractions of the box
    assert abs(na.xy[KP["left_ankle"], 1] - na.xy[KP["nose"], 1]) > 0.8   # the body spans the box height


def test_low_confidence_and_unplaced_keypoints_count_as_missing():
    box, k = person("STANDING", 200, 100, 200, hide=LEGS)
    k[KP["left_wrist"], 2] = 0.2                                  # below the keypoint threshold
    n = normalize_pose(PoseDetection(BBox(*box), 0.9, k), BBox(*box), 0.35)
    assert not n.visible[KP["left_knee"]] and not n.visible[KP["left_wrist"]]
    assert np.isnan(n.xy[KP["left_knee"]]).all()
    assert n.quality.has_torso and not n.quality.has_legs and n.quality.usable


def test_keypoints_far_outside_the_track_box_are_ignored():
    box, k = person("STANDING", 200, 100, 200)
    k[KP["right_wrist"], :2] = (900, 50)                          # belongs to someone else / a mistake
    n = normalize_pose(PoseDetection(BBox(*box), 0.9, k), BBox(*box), 0.35)
    assert not n.visible[KP["right_wrist"]]


def test_a_pose_without_a_torso_is_not_usable():
    box, k = person("STANDING", 200, 100, 200, hide=("left_hip", "right_hip"))
    n = normalize_pose(PoseDetection(BBox(*box), 0.9, k), BBox(*box), 0.35)
    assert not n.quality.has_torso and not n.quality.usable


def test_poses_are_given_to_the_overlapping_track_and_keep_its_id():
    a = pose("STANDING", 150, 100, 200)
    b = pose("SITTING", 500, 120, 160)
    stray = pose("STANDING", 1100, 100, 200)                      # no track there
    tracks = [TrackedObject(7, b.bbox, 0.9, 0), TrackedObject(3, BBox(a.bbox.x1 + 4, a.bbox.y1 + 2, a.bbox.x2 + 4, a.bbox.y2), 0.9, 0),
              TrackedObject(9, BBox(700, 300, 760, 420), 0.8, 0)]   # a track with no pose
    m = associate_poses(tracks, [stray, a, b], 0.3)
    assert m[3] is a and m[7] is b and 9 not in m and stray not in m.values()
    assert iou(a.bbox, a.bbox) == pytest.approx(1.0) and iou(a.bbox, b.bbox) == 0.0


def test_one_pose_never_goes_to_two_tracks():
    a = pose("STANDING", 150, 100, 200)
    t1 = TrackedObject(1, a.bbox, 0.9, 0)
    t2 = TrackedObject(2, BBox(a.bbox.x1 + 10, a.bbox.y1, a.bbox.x2 + 10, a.bbox.y2), 0.9, 0)
    m = associate_poses([t1, t2], [a], 0.3)
    assert list(m) == [1]


def test_missing_pose_weights_are_reported_not_raised(tmp_path):
    est = YoloPoseEstimator(tmp_path / "nope-pose.pt", "cpu", 0.35, 320)
    info = est.load()
    assert not info.loaded and "not found" in info.error
    from app.core.errors import ModelNotReady
    with pytest.raises(ModelNotReady):
        est.estimate(np.zeros((32, 32, 3), np.uint8))
