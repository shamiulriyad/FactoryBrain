"""Real YOLO + ByteTrack on real photos of people (skipped when the weights aren't downloaded).

    python scripts/download_model.py
    python scripts/make_sample_video.py
    pytest -m model
"""
from pathlib import Path

import cv2
import pytest

from app.core.config import ROOT
from app.detection.detector import YoloPersonDetector

WEIGHTS = ROOT / "models" / "yolo11n.pt"
SAMPLE = ROOT / "data" / "samples" / "people_walkthrough.mp4"

pytestmark = [pytest.mark.model, pytest.mark.skipif(not WEIGHTS.is_file(), reason="models/yolo11n.pt not downloaded")]


@pytest.fixture(scope="module")
def detector() -> YoloPersonDetector:
    d = YoloPersonDetector(WEIGHTS, "cpu", 0.4, 640)
    assert d.load().loaded, d.info.error
    return d


def _asset(name: str):  # noqa: ANN202
    from ultralytics.utils import ASSETS

    return cv2.imread(str(Path(ASSETS) / name))


def test_the_model_finds_the_people_in_a_street_photo(detector):
    img = _asset("bus.jpg")                      # four people and a bus
    found = detector.detect(img, "cam", 0.0)
    assert len(found) >= 3
    assert all(d.class_name == "person" and d.confidence >= 0.4 for d in found)
    h, w = img.shape[:2]
    assert all(d.bbox.is_valid(w, h) for d in found)
    assert detector.info.inference_count >= 1 and detector.info.avg_inference_ms > 0


def test_a_photo_without_people_gives_nothing(detector):
    import numpy as np

    assert detector.detect(np.full((480, 640, 3), 128, np.uint8), "cam", 0.0) == []


@pytest.mark.skipif(not SAMPLE.is_file(), reason="run scripts/make_sample_video.py first")
def test_the_full_pipeline_tracks_people_through_a_video(detector, settings):
    from app.camera.manager import SessionManager
    from app.camera.sources import OpenCvSource
    from tests.conftest import FakeDirectory, MemoryPublisher, camera, rect_zone, wait_for

    cam = camera(zones=[rect_zone("z", "Left half", 0.0, 0.0, 0.5, 1.0)])
    pub = MemoryPublisher()
    settings.process_fps = 5
    m = SessionManager(settings, FakeDirectory({cam.id: cam}), detector, pub, "cpu",
                       # Played at its own speed, like a camera: unpaced playback made the number of analysed frames depend on machine load.
                       source_factory=lambda cfg, opts: OpenCvSource(str(SAMPLE), "file", "sample video", realtime=True))
    m.start(cam.id)
    assert wait_for(lambda: m.get(cam.id).state.value == "FINISHED", timeout=300)
    types = pub.types()
    tracks = {e.track_id for e in pub.events if e.event_type == "PERSON_DETECTED"}
    assert len(tracks) >= 2, types                     # several real people, each with its own temporary id
    assert "PERSON_LOST" in types and "ZONE_ENTERED" in types
    assert all(e.confidence is None or 0.4 <= e.confidence <= 1 for e in pub.events)
