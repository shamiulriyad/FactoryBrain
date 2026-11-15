"""Real models on the sample video: the garment feature pipeline end to end (skipped without the weights).

    python scripts/download_model.py ; python scripts/download_model.py --pose ; python scripts/make_sample_video.py
    pytest -m model

There is no trained garment model and no labelled garment footage, so no
garment classification is run here: these tests check that real detection,
tracking, pose and the ImageNet CNN produce the features a model would be
trained on. Inference with a real trained model is tested once one exists
(see docs/GARMENT_ACTIVITIES.md).
"""
import numpy as np
import pytest

from app.core.config import ROOT
from app.garment_activities.config import FeatureConfig
from app.garment_activities.feature_extractor import ClipFeatureExtractor, CnnEmbedder, layout

POSE = ROOT / "models" / "pose" / "yolo11n-pose.pt"
DET = ROOT / "models" / "yolo11n.pt"
SAMPLE = ROOT / "data" / "samples" / "two_people.mp4"

pytestmark = [pytest.mark.model,
              pytest.mark.skipif(not (POSE.is_file() and DET.is_file() and SAMPLE.is_file()), reason="weights or sample video missing")]


@pytest.fixture(scope="module")
def models():
    from app.activity_recognition.pose_estimator import YoloPoseEstimator
    from app.detection.detector import YoloPersonDetector

    det, pose = YoloPersonDetector(DET, "cpu", 0.4, 640), YoloPoseEstimator(POSE, "cpu", 0.35, 640)
    assert det.load().loaded and pose.load().loaded
    try:
        emb = CnnEmbedder("mobilenet_v3_small", "cpu")
    except Exception as exc:  # noqa: BLE001 - the ImageNet weights are downloaded once by torchvision
        pytest.skip(f"MobileNetV3 weights unavailable: {exc}")
    return det, pose, emb


def test_the_imagenet_cnn_embeds_person_crops(models):
    _, _, emb = models
    crops = [np.full((112, 112, 3), v, np.uint8) for v in (0, 128, 255)]
    out = emb.embed(crops)
    assert out.shape == (3, 576) and np.isfinite(out).all() and not np.allclose(out[0], out[2])


def test_real_clip_features_for_a_subject_selected_by_roi(models):
    det, pose, emb = models
    ext = ClipFeatureExtractor(det, pose, emb, FeatureConfig(), sample_fps=4.0)
    x, q = ext.extract(SAMPLE, 2.0, 4.0)                                # no ROI: the most-seen person
    lay = layout("mobilenet_v3_small")
    assert x.shape == (8, 665) and q["subjectFound"] and q["personRatio"] >= 0.75
    assert q["poseRatio"] > 0 and np.abs(x[:, slice(*lay["appearance"])]).sum() > 0
    right, qr = ext.extract(SAMPLE, 2.0, 4.0, roi={"x": 0.5, "y": 0.0, "w": 0.5, "h": 1.0})
    left, ql = ext.extract(SAMPLE, 2.0, 4.0, roi={"x": 0.0, "y": 0.0, "w": 0.5, "h": 1.0})
    assert qr["subjectFound"] and ql["subjectFound"] and qr["subjectTrack"] != ql["subjectTrack"]   # two people, two subjects
    nobody, qn = ext.extract(SAMPLE, 2.0, 4.0, roi={"x": 0.0, "y": 0.0, "w": 0.02, "h": 0.02})
    assert not qn["subjectFound"] and not nobody.any()                  # an ROI matching nobody gives an empty clip, not someone else
