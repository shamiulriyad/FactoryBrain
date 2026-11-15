"""Real YOLO pose + the activity pipeline on real photos and the sample video (skipped without the weights).

    python scripts/download_model.py
    python scripts/download_model.py --pose
    python scripts/make_sample_video.py
    pytest -m model

These check that the real model integrates (keypoints for real people, track
association, events that pass the contract). They are not an accuracy test:
the sample video is panned photographs, not labelled factory footage.
"""
from pathlib import Path

import cv2
import numpy as np
import pytest

from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.pose_estimator import YoloPoseEstimator
from app.core.config import ROOT

POSE = ROOT / "models" / "pose" / "yolo11n-pose.pt"
DET = ROOT / "models" / "yolo11n.pt"
SAMPLE = ROOT / "data" / "samples" / "people_walkthrough.mp4"

pytestmark = [pytest.mark.model, pytest.mark.skipif(not (POSE.is_file() and DET.is_file()), reason="pose / detector weights not downloaded")]


@pytest.fixture(scope="module")
def pose() -> YoloPoseEstimator:
    p = YoloPoseEstimator(POSE, "cpu", 0.35, 640)
    assert p.load().loaded, p.info.error
    return p


def test_the_pose_model_finds_keypoints_of_real_people(pose):
    from ultralytics.utils import ASSETS

    img = cv2.imread(str(Path(ASSETS) / "bus.jpg"))
    found = pose.estimate(img)
    assert len(found) >= 3
    for d in found:
        assert d.keypoints.shape == (17, 3) and np.isfinite(d.keypoints).all()
    confident = [int((d.keypoints[:, 2] > 0.5).sum()) for d in found]
    assert sum(1 for n in confident if n >= 8) >= 2, confident      # most bodies largely visible; people at the edge are partial
    assert pose.estimate(np.full((480, 640, 3), 128, np.uint8)) == []
    assert pose.info.avg_inference_ms > 0


def test_a_detection_model_is_refused_as_a_pose_model():
    bad = YoloPoseEstimator(DET, "cpu", 0.35, 320)
    assert not bad.load().loaded and "not a pose model" in bad.info.error


@pytest.mark.skipif(not SAMPLE.is_file(), reason="run scripts/make_sample_video.py first")
def test_the_full_pipeline_publishes_valid_activity_events(pose, settings):
    from app.camera.manager import SessionManager
    from app.camera.sources import OpenCvSource
    from app.detection.detector import YoloPersonDetector
    from app.events.schemas import ACTIVITY_EVENT_TYPES, REPORTABLE_ACTIVITIES
    from tests.conftest import FakeDirectory, MemoryPublisher, camera, wait_for

    det = YoloPersonDetector(DET, "cpu", 0.4, 640)
    assert det.load().loaded
    cam = camera()
    pub = MemoryPublisher()
    settings.process_fps = 8
    m = SessionManager(settings, FakeDirectory({cam.id: cam}), det, pub, "cpu", pose_estimator=pose,
                       activity_config=ActivityConfig(enabled=True, process_fps=4),
                       source_factory=lambda cfg, opts: OpenCvSource(str(SAMPLE), "file", "sample video", realtime=True))
    m.start(cam.id)
    assert wait_for(lambda: m.get(cam.id).state.value == "FINISHED", timeout=300)
    acts = [e for e in pub.events if e.event_type in ACTIVITY_EVENT_TYPES]
    snap = m.get(cam.id).activity.metrics.snapshot()
    assert snap["pose_inferences"] > 10 and snap["observations_with_pose"] > 0, snap
    assert acts, snap                                               # at least one state was reached on real people
    tracks = {e.track_id for e in pub.events if e.event_type == "PERSON_DETECTED"}
    for e in acts:
        assert e.payload["activity"] in REPORTABLE_ACTIVITIES and e.track_id in tracks
        e.model_validate(e.model_dump())                            # still passes the contract
