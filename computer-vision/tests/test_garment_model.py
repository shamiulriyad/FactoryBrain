"""Garment features, model interface, training mechanics, checkpoints, loading failures, decisions and evaluation.

Training here uses tests/garment_helpers.synthetic_set: clearly synthetic
tensors that only check the mechanics (loss, early stopping, checkpoint
metadata). No model is saved outside the temporary test folder.
"""
import json

import numpy as np
import pytest
import torch

from app.activity_recognition.schemas import PoseDetection
from app.detection.types import BBox
from app.garment_activities import model_factory
from app.garment_activities.config import FEATURE_VERSION, ClipConfig, DecisionConfig, FeatureConfig, TrainingConfig
from app.garment_activities.dataset_loader import FeatureSet
from app.garment_activities.evaluator import evaluate, record, to_markdown
from app.garment_activities.feature_extractor import (
    MOTION_DIM, POSE_DIM, crop_person, describe, feature_dim, layout, resample, sequence,
)
from app.garment_activities.inference import decide, load_classifier
from app.garment_activities.trainer import TrainingError, class_weights, train
from tests.garment_helpers import FakeEmbedder, synthetic_set
from tests.poses import person

LABELS = ("SEWING", "CUTTING", "PACKING")
FCFG = FeatureConfig(backbone="none")


# ─── Features ─────────────────────────────────────────────────────────────

def test_the_feature_layout_is_fixed_per_backbone():
    lay = layout("mobilenet_v3_small")
    assert lay["appearance"] == (0, 576) and lay["pose"][1] - lay["pose"][0] == POSE_DIM == 81
    assert lay["motion"][1] - lay["motion"][0] == MOTION_DIM and feature_dim("mobilenet_v3_small") == 665 and feature_dim("none") == 89
    with pytest.raises(ValueError):
        layout("vgg")


def test_crops_are_square_padded_and_safe_at_the_frame_edge():
    frame = np.full((100, 200, 3), 200, np.uint8)
    c = crop_person(frame, BBox(180, 10, 200, 90), 0.15, 112)
    assert c.shape == (112, 112, 3) and c[:, -5:].max() == 0            # outside the frame is black, not stretched


def frames_of(postures, step=0.25, move=0.0, hide=None):  # noqa: ANN001
    frame = np.zeros((480, 640, 3), np.uint8)
    out = []
    for i, p in enumerate(postures):
        if p is None:
            out.append(describe(None, i * step, None, None, FCFG))
            continue
        box, k = person(p, 200 + move * i, 60, 240, hide=hide or ())
        out.append(describe(frame, i * step, BBox(*box), PoseDetection(BBox(*box), 0.9, k), FCFG))
    return out


def test_a_frame_vector_has_pose_motion_and_presence_and_missing_frames_stay_empty():
    frames = frames_of(["SITTING", "SITTING", None, "SITTING"], move=12)
    x = sequence(frames, FakeEmbedder(0, "none"))
    lay = layout("none")
    assert x.shape == (4, 89)
    assert list(x[:, lay["presence"][0]]) == [1, 1, 0, 1]
    assert not x[2].any()                                               # missing frame: all zeros, never a guess
    vx = x[:, lay["motion"][0]]
    assert vx[0] == 0 and vx[1] > 0.1                                   # moving right, in body heights per second
    assert vx[3] > 0.1                                                  # measured across the missing frame using timestamps


def test_hidden_keypoints_are_masked_not_invented():
    lay = layout("none")
    x = sequence(frames_of(["STANDING"] * 2, hide=("left_knee", "right_knee", "left_ankle", "right_ankle")), FakeEmbedder(0, "none"))
    pose = x[0, slice(*lay["pose"])]
    n = 15
    assert pose[n:2 * n].sum() < n                                       # some feature presence flags are 0
    assert pose[2 * n + 34:].sum() == 13                                 # 13 of 17 keypoints visible


def test_appearance_embeddings_come_from_the_person_crop():
    frame = np.zeros((480, 640, 3), np.uint8)
    frame[:, :320] = (0, 0, 255)                                         # left half red (BGR)
    box, k = person("STANDING", 150, 60, 240)
    d = describe(frame, 0.0, BBox(*box), PoseDetection(BBox(*box), 0.9, k), FeatureConfig(backbone="mobilenet_v3_small"))
    x = sequence([d], FakeEmbedder(576))
    assert x.shape == (1, 665) and x[0, 0] > 0.5                        # the fake embedder saw a red crop


def test_resampling_aligns_any_observation_rate_to_the_models_frames():
    frames = frames_of(["SITTING"] * 10, step=0.1)                      # 10 fps observations
    r = resample(frames, 0.0, 4, 0.25)                                  # a 4 fps, 1 s model
    assert [round(d.t, 2) for d in r] == [0.0, 0.2, 0.5, 0.7] and all(d.present for d in r)
    gap = resample(frames, 2.0, 2, 0.25)
    assert not any(d.present for d in gap)


# ─── Models ───────────────────────────────────────────────────────────────

@pytest.mark.parametrize("arch", list(model_factory.ARCHITECTURES))
def test_every_architecture_has_the_same_input_output_schema(arch):
    m = model_factory.build(arch, "mobilenet_v3_small", 5)
    x = torch.randn(3, 16, 665)
    mask = torch.ones(3, 16)
    mask[0, 8:] = 0
    out = m(x, mask)
    assert out.shape == (3, 5)
    x2 = x.clone()
    x2[0, 8:] = 1000.0                                                  # masked-out frames don't change the answer
    assert torch.allclose(m.eval()(x, mask)[0], m(x2, mask)[0], atol=1e-4)
    if arch == "pose_gru":
        assert m.input_dim == POSE_DIM + MOTION_DIM + 1


def test_unknown_architectures_and_too_few_classes_are_refused():
    with pytest.raises(ValueError):
        model_factory.build("slowfast", "none", 3)
    with pytest.raises(ValueError):
        model_factory.build("gru", "none", 1)


# ─── Training ─────────────────────────────────────────────────────────────

def cfg(**kw):
    return TrainingConfig(model_name="garment-test", model_version="0.0.1", epochs=30, batch_size=16, early_stopping_patience=4, seed=3, **kw)


def test_training_learns_saves_a_checkpoint_with_full_metadata_and_is_reproducible(tmp_path):
    tr, va = synthetic_set(LABELS, 30, seed=1), synthetic_set(LABELS, 10, seed=2)
    res = train(tr, va, cfg(), ClipConfig(duration_s=2, sample_fps=4), FCFG, DecisionConfig(), "ds-synthetic", tmp_path, data_source="synthetic-test")
    meta = json.loads((res.directory / "metadata.json").read_text())
    for k in ("modelName", "modelVersion", "datasetVersion", "labels", "featureVersion", "input", "training", "validation", "evaluation", "trainedAt"):
        assert k in meta
    assert meta["labels"] == list(LABELS) and meta["featureVersion"] == FEATURE_VERSION and meta["dataSource"] == "synthetic-test"
    assert meta["evaluation"]["status"] == "PENDING"                      # not usable live until evaluated on a test split
    assert meta["input"]["frames"] == 8 and meta["training"]["seed"] == 3 and meta["build"]["parts"] == ["pose", "motion"]
    assert res.history[-1]["val_loss"] < res.history[0]["val_loss"] and meta["validation"]["accuracy"] > 0.9
    assert (res.directory / "history.csv").read_text().startswith("epoch,train_loss,val_loss")
    again = train(tr, va, cfg(), ClipConfig(duration_s=2, sample_fps=4), FCFG, DecisionConfig(), "ds-synthetic", tmp_path / "again")
    assert [h["val_loss"] for h in again.history] == [h["val_loss"] for h in res.history]


def test_early_stopping_and_class_weights():
    w = class_weights(np.array([0, 0, 0, 0, 1]), 3)
    assert w[1] > w[0] and w[2] == 0
    tr, va = synthetic_set(LABELS, 20, seed=1), synthetic_set(LABELS, 6, seed=9)
    import tempfile
    from pathlib import Path

    with tempfile.TemporaryDirectory() as d:
        res = train(tr, va, cfg().with_(epochs=200, early_stopping_patience=2, learning_rate=0.05), ClipConfig(duration_s=2, sample_fps=4), FCFG,
                    DecisionConfig(), "ds", Path(d))
    assert len(res.history) < 200


def test_training_refuses_without_real_data(tmp_path):
    empty = FeatureSet(np.zeros((0, 8, 89), np.float32), np.zeros((0, 8), np.float32), np.zeros(0, np.int64), LABELS, [])
    with pytest.raises(TrainingError, match="No training clips"):
        train(empty, synthetic_set(LABELS, 2), cfg(), ClipConfig(), FCFG, DecisionConfig(), "ds", tmp_path)
    with pytest.raises(TrainingError, match="validation"):
        train(synthetic_set(LABELS, 2), empty, cfg(), ClipConfig(), FCFG, DecisionConfig(), "ds", tmp_path)
    with pytest.raises(TrainingError, match="2 classes"):
        one = synthetic_set(("SEWING",), 4)
        train(one, one, cfg(), ClipConfig(), FCFG, DecisionConfig(), "ds", tmp_path)


# ─── Loading ──────────────────────────────────────────────────────────────

@pytest.fixture(scope="module")
def trained(tmp_path_factory):
    d = tmp_path_factory.mktemp("models")
    res = train(synthetic_set(LABELS, 20, seed=1), synthetic_set(LABELS, 8, seed=2), cfg(), ClipConfig(duration_s=2, sample_fps=4), FCFG,
                DecisionConfig(), "ds-synthetic", d, data_source="synthetic-test")
    return res.directory


def test_an_unevaluated_model_is_refused_for_live_use(trained):
    with pytest.raises(model_factory.ModelLoadError, match="no held-out test evaluation"):
        load_classifier(trained)
    clf = load_classifier(trained, allow_unevaluated=True)
    assert clf.labels == LABELS and clf.frames == 8 and clf.uses_pose


def test_broken_or_incompatible_models_fail_clearly(trained, tmp_path):
    with pytest.raises(model_factory.ModelLoadError, match="No garment activity model"):
        load_classifier(tmp_path / "nothing")
    import shutil

    other = tmp_path / "old"
    shutil.copytree(trained, other)
    meta = json.loads((other / "metadata.json").read_text())
    (other / "metadata.json").write_text(json.dumps(meta | {"featureVersion": "garment-features-0"}))
    with pytest.raises(model_factory.ModelLoadError, match="feature layout"):
        load_classifier(other, allow_unevaluated=True)
    (other / "metadata.json").write_text(json.dumps(meta | {"labels": ["A", "B"]}))
    with pytest.raises(model_factory.ModelLoadError, match="doesn't match"):
        load_classifier(other, allow_unevaluated=True)
    (other / "metadata.json").write_text("{not json")
    with pytest.raises(model_factory.ModelLoadError, match="invalid JSON"):
        load_classifier(other, allow_unevaluated=True)


# ─── Decisions ────────────────────────────────────────────────────────────

D = DecisionConfig(min_confidence=0.6, min_margin=0.15, min_person_ratio=0.6, min_pose_ratio=0.4)
Q = {"personRatio": 1.0, "poseRatio": 1.0}


@pytest.mark.parametrize("probs, quality, label, reason", [
    ([0.8, 0.1, 0.1], Q, "SEWING", None),
    ([0.5, 0.3, 0.2], Q, "UNKNOWN", "LOW_CONFIDENCE"),
    ([0.58, 0.0, 0.42], Q, "UNKNOWN", "LOW_CONFIDENCE"),
    ([0.62, 0.0, 0.38], Q, "SEWING", None),                            # just above both thresholds
    ([0.9, 0.05, 0.05], {"personRatio": 0.0, "poseRatio": 0.0}, "UNKNOWN", "NO_PERSON"),
    ([0.9, 0.05, 0.05], {"personRatio": 0.4, "poseRatio": 0.4}, "UNKNOWN", "INSUFFICIENT_FRAMES"),
    ([0.9, 0.05, 0.05], {"personRatio": 1.0, "poseRatio": 0.1}, "UNKNOWN", "NO_POSE"),
])
def test_uncertain_answers_become_unknown_with_a_reason(probs, quality, label, reason):
    p = decide(np.array(probs), LABELS, D, quality, (0.0, 2.0), uses_pose=True)
    assert (p.label, p.reason) == (label, reason)
    assert p.candidate == "SEWING" and set(p.scores) == set(LABELS)


def test_conflicting_answers_and_a_model_unknown_class():
    p = decide(np.array([0.62, 0.0, 0.0, 0.38]), ("SEWING", "CUTTING", "PACKING", "FABRIC_HANDLING"), DecisionConfig(min_confidence=0.5, min_margin=0.3),
               Q, (0, 1), True)
    assert p.label == "UNKNOWN" and p.reason == "CONFLICTING"
    q = decide(np.array([0.1, 0.8, 0.1]), ("SEWING", "UNKNOWN", "PACKING"), D, Q, (0, 1), True)
    assert q.label == "UNKNOWN" and q.reason == "MODEL_UNKNOWN" and q.candidate == "SEWING"


# ─── Evaluation ───────────────────────────────────────────────────────────

def test_evaluation_reports_every_metric_and_marks_the_model_evaluated(trained):
    clf = load_classifier(trained, allow_unevaluated=True)
    test = synthetic_set(LABELS, 12, seed=5)
    r = evaluate(clf, test, min_camera_clips=5, data_source="synthetic-test")
    a = r["afterUncertainty"]
    for k in ("accuracy", "accuracyWhenDecided", "macro", "weighted", "perClass", "confusionMatrix", "unknownRate"):
        assert k in a
    assert set(a["perClass"]) == set(LABELS) and "UNKNOWN" in a["labels"]
    assert {"unknownRate", "lowConfidenceRate", "classErrors", "latency", "perCamera", "rawArgmax", "matchesAnyLabel"} <= set(r)
    assert r["latency"]["modelMsPerClip"] > 0 and r["perCamera"]["CAM-T"]["clips"] == 36
    record(trained, r)
    meta = json.loads((trained / "metadata.json").read_text())
    assert meta["evaluation"]["status"] == "EVALUATED" and meta["evaluation"]["dataSource"] == "synthetic-test"
    assert load_classifier(trained).evaluation["status"] == "EVALUATED"   # now loadable without the development flag
    assert "| Macro F1 |" in to_markdown(r)


def test_without_a_labelled_test_set_evaluation_is_pending(trained):
    clf = load_classifier(trained, allow_unevaluated=True)
    empty = FeatureSet(np.zeros((0, 8, 89), np.float32), np.zeros((0, 8), np.float32), np.zeros(0, np.int64), LABELS, [])
    r = evaluate(clf, empty)
    assert r["status"] == "PENDING" and not {"afterUncertainty", "rawArgmax", "perCamera"} & set(r)   # no score of any kind
    assert "PENDING" in to_markdown(r)
