"""Temporal classification: each activity from synthetic poses over time, thresholds, UNKNOWN when evidence is weak."""
import pytest

from app.activity_recognition.activity_classifier import BaselineTemporalClassifier, SequenceModelClassifier, build_classifier, window_speed
from app.activity_recognition.schemas import ActivityLabel, UncertainReason
from tests.activity_helpers import CFG, observe
from tests.poses import LEGS

clf = BaselineTemporalClassifier(CFG)


@pytest.mark.parametrize("posture, expected", [
    ("STANDING", ActivityLabel.STANDING),
    ("SITTING", ActivityLabel.SITTING),
    ("BENDING", ActivityLabel.BENDING),
    ("REACHING", ActivityLabel.REACHING),
])
def test_each_posture_is_recognised_over_a_window(posture, expected):
    p = clf.classify(observe(posture, noise=0.004))
    assert p.label == expected, (p.scores, p.reason)
    assert p.confidence >= CFG.min_confidence and p.reason is None
    assert p.quality.pose_observations == 12 and p.quality.consistency >= 0.9


def test_walking_comes_from_movement_over_time_not_from_one_frame():
    walking = observe("STANDING", speed_px=120)                    # 0.6 body heights per second
    p = clf.classify(walking)
    assert p.label == ActivityLabel.WALKING and p.quality.speed == pytest.approx(0.58, abs=0.05)
    # The very same frames, standing still, are standing: movement decides, not the pose of a single frame.
    assert clf.classify(observe("STANDING")).label == ActivityLabel.STANDING


def test_speed_uses_timestamps_so_missing_frames_do_not_change_it():
    full = observe("STANDING", n=12, fps=4, speed_px=120)
    sparse = full[::3]                                             # two of every three frames missing
    assert window_speed(sparse) == pytest.approx(window_speed(full), rel=0.02)


def test_too_short_a_history_is_unknown():
    p = clf.classify(observe("STANDING", n=3))
    assert p.label == ActivityLabel.UNKNOWN and p.reason == UncertainReason.INSUFFICIENT_HISTORY and p.confidence == 0


def test_missing_pose_data_never_becomes_a_prediction():
    p = clf.classify(observe("STANDING", with_pose=False))
    assert p.label == ActivityLabel.UNKNOWN and p.reason == UncertainReason.NO_POSE and p.candidate is None
    mostly_missing = observe("STANDING", with_pose=[i % 4 == 0 for i in range(12)])   # 3 usable poses of 12
    q = clf.classify(mostly_missing)
    assert q.label == ActivityLabel.UNKNOWN and q.reason == UncertainReason.INSUFFICIENT_POSE


def test_hidden_legs_lower_the_confidence_below_the_threshold():
    p = clf.classify(observe("STANDING", hide=LEGS))
    assert p.label == ActivityLabel.UNKNOWN and p.reason == UncertainReason.LOW_CONFIDENCE
    assert p.candidate == ActivityLabel.STANDING                   # the best guess is kept for review, not asserted


def test_low_keypoint_confidence_is_not_trusted():
    p = clf.classify(observe("STANDING", conf=0.2))                 # every keypoint below the keypoint threshold
    assert p.label == ActivityLabel.UNKNOWN


def test_conflicting_frames_are_uncertain():
    a = observe("STANDING", n=6, t0=1000.0)
    b = observe("SITTING", n=6, t0=1000.0 + 6 / 4)
    p = clf.classify(a + b)
    assert p.label == ActivityLabel.UNKNOWN and p.reason in (UncertainReason.CONFLICTING, UncertainReason.LOW_CONFIDENCE)


def test_the_confidence_threshold_is_configurable():
    window = observe("SITTING", with_pose=[i % 3 != 0 for i in range(12)])     # a third of the looks without a pose
    normal = clf.classify(window)
    assert normal.label == ActivityLabel.SITTING and 0.8 < normal.confidence < 0.95      # missing poses lower the confidence
    strict = BaselineTemporalClassifier(CFG.with_(min_confidence=0.95)).classify(window)
    assert strict.label == ActivityLabel.UNKNOWN and strict.reason == UncertainReason.LOW_CONFIDENCE
    assert strict.candidate == ActivityLabel.SITTING


def test_scores_cover_every_supported_activity():
    p = clf.classify(observe("STANDING"))
    assert set(p.scores) == {"STANDING", "SITTING", "WALKING", "BENDING", "REACHING"}
    assert all(0 <= v <= 1 for v in p.scores.values())


def test_the_sequence_model_interface_needs_a_real_trained_model(tmp_path):
    with pytest.raises(FileNotFoundError):
        SequenceModelClassifier(CFG, tmp_path / "activity.ts")
    # Asking for it without a model falls back to the baseline instead of inventing predictions.
    c = build_classifier(CFG.with_(classifier="sequence", sequence_model_path=tmp_path / "missing.ts"))
    assert isinstance(c, BaselineTemporalClassifier)


def test_a_torchscript_sequence_model_plugs_in(tmp_path):
    """Interface check only: a tiny untrained GRU exported to TorchScript produces predictions through the same thresholds."""
    import torch

    from app.activity_recognition.schemas import FEATURE_NAMES

    class Tiny(torch.nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.gru = torch.nn.GRU(2 * len(FEATURE_NAMES), 8, batch_first=True)
            self.head = torch.nn.Linear(8, 5)

        def forward(self, x):  # noqa: ANN001, ANN201
            _, h = self.gru(x)
            return self.head(h[-1])

    path = tmp_path / "tiny.ts"
    torch.jit.save(torch.jit.script(Tiny()), str(path), _extra_files={"labels.txt": "STANDING,SITTING,WALKING,BENDING,REACHING",
                                                                       "meta.txt": "name=untrained-gru;version=0.0-test"})
    c = SequenceModelClassifier(CFG.with_(min_confidence=0.0, conflict_margin=0.0), path)
    assert c.name == "untrained-gru" and c.version == "0.0-test"
    p = c.classify(observe("STANDING"))
    assert set(p.scores) == {"STANDING", "SITTING", "WALKING", "BENDING", "REACHING"}
    assert abs(sum(p.scores.values()) - 1) < 0.01
