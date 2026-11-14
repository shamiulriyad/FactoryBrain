"""Detection output: person-class filtering, thresholds, box validation, missing model."""
import numpy as np
import pytest

from app.core.errors import ModelNotReady
from app.detection.detector import YoloPersonDetector
from app.detection.types import BBox, keep_persons

NAMES = {0: "person", 2: "car"}


def test_only_persons_at_or_above_the_threshold_are_kept():
    rows = [(10, 10, 60, 120, 0.9, 0), (100, 10, 160, 120, 0.95, 2), (200, 10, 260, 120, 0.3, 0), (300, 10, 360, 120, 0.4, 0)]
    out = keep_persons(rows, NAMES, 640, 360, 0.4, "cam-1", 1700000000.0)
    assert [d.confidence for d in out] == [0.9, 0.4]
    assert all(d.class_id == 0 and d.class_name == "person" and d.camera_id == "cam-1" for d in out)
    assert out[0].frame_ts == 1700000000.0


def test_invalid_boxes_are_dropped_and_partial_ones_clipped():
    rows = [(10, 10, 10.5, 120, 0.9, 0),        # 0.5 px wide
            (700, 10, 760, 120, 0.9, 0),        # outside the frame
            (-20, -5, 50, 400, 0.9, 0)]         # partly outside: clipped
    out = keep_persons(rows, NAMES, 640, 360, 0.25, "c", 0.0)
    assert len(out) == 1
    assert out[0].bbox == BBox(0.0, 0.0, 50.0, 360.0)


def test_boxes_normalise_and_give_the_feet_position():
    b = BBox(64, 36, 128, 180)
    assert b.normalised(640, 360) == {"x": 0.1, "y": 0.1, "w": 0.1, "h": 0.4}
    assert b.bottom_centre() == (96.0, 180.0)
    assert not BBox(5, 5, 5, 50).is_valid(640, 360)


def test_a_missing_model_is_reported_not_crashed(tmp_path):
    d = YoloPersonDetector(tmp_path / "nope.pt", "cpu", 0.4, 320)
    info = d.load()
    assert not info.loaded
    assert "not found" in info.error
    with pytest.raises(ModelNotReady):
        d.detect(np.zeros((32, 32, 3), np.uint8), "c", 0.0)
