"""Evaluation metrics and leakage-safe dataset splits (no model, no footage)."""
import pytest

from app.activity_recognition.evaluation import SplitRatios, check_leakage, class_balance, group_split, report

LABELS = ["STANDING", "SITTING", "WALKING"]


def test_precision_recall_f1_and_the_confusion_matrix():
    truth = ["STANDING"] * 4 + ["SITTING"] * 4 + ["WALKING"] * 2
    pred = ["STANDING", "STANDING", "STANDING", "SITTING", "SITTING", "SITTING", "UNKNOWN", "UNKNOWN", "WALKING", "STANDING"]
    r = report(truth, pred, LABELS)
    st = r["perClass"]["STANDING"]
    assert st["precision"] == 0.75 and st["recall"] == 0.75 and st["f1"] == 0.75 and st["support"] == 4
    si = r["perClass"]["SITTING"]
    assert si["precision"] == pytest.approx(2 / 3, abs=1e-4) and si["recall"] == 0.5 and si["unknown"] == 2
    assert r["confusionMatrix"]["SITTING"]["UNKNOWN"] == 2 and r["confusionMatrix"]["WALKING"]["STANDING"] == 1
    assert r["unknownRate"] == 0.2 and r["accuracy"] == 0.6 and r["accuracyWhenDecided"] == 0.75
    assert r["labels"][-1] == "UNKNOWN"


def test_classes_without_predictions_or_support_are_not_scored_as_perfect():
    r = report(["STANDING", "STANDING"], ["UNKNOWN", "UNKNOWN"], LABELS)
    assert r["perClass"]["STANDING"]["recall"] == 0.0 and r["perClass"]["STANDING"]["precision"] is None
    assert r["perClass"]["WALKING"]["support"] == 0 and r["perClass"]["WALKING"]["recall"] is None
    assert r["unknownRate"] == 1.0


def test_class_imbalance_is_reported():
    b = class_balance(["STANDING"] * 30 + ["SITTING"] * 10 + ["REACHING"] * 2)
    assert b["imbalanceRatio"] == 15.0 and b["majority"] == "STANDING" and b["minority"] == "REACHING"


def test_groups_never_straddle_splits_and_the_split_is_deterministic():
    groups = [f"rec-{i // 3}" for i in range(300)]                 # 100 recordings, 3 clips each
    a = group_split(groups, SplitRatios())
    assert a == group_split(groups, SplitRatios())
    rows = [{"recording_id": g, "split": a[g]} for g in groups]
    assert check_leakage(rows, ("recording_id",)) == []
    shares = {s: sum(1 for v in a.values() if v == s) / len(a) for s in ("train", "validation", "test")}
    assert 0.55 < shares["train"] < 0.85 and shares["test"] > 0.05


def test_leakage_by_recording_or_person_is_detected():
    rows = [{"recording_id": "r1", "subject_group": "p1", "split": "train"}, {"recording_id": "r1", "subject_group": "p1", "split": "test"},
            {"recording_id": "r2", "subject_group": "p2", "split": "train"}, {"recording_id": "r3", "subject_group": "p2", "split": "validation"}]
    problems = check_leakage(rows)
    assert any("recording_id 'r1'" in p for p in problems) and any("subject_group 'p2'" in p for p in problems)


def test_the_dataset_tool_links_recordings_through_shared_people(tmp_path):
    import importlib.util
    from pathlib import Path

    spec = importlib.util.spec_from_file_location("activity_dataset", Path(__file__).resolve().parents[1] / "scripts" / "activity_dataset.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    rows = [{"recording_id": "r1", "subject_group": "p1"}, {"recording_id": "r2", "subject_group": "p1"}, {"recording_id": "r3", "subject_group": "p9"}]
    comps = mod._components(rows)
    assert comps[0] == comps[1] != comps[2]                        # r1 and r2 share a person: same group, same split


def test_evaluation_without_a_dataset_reports_pending(tmp_path, capsys):
    import subprocess
    import sys
    from pathlib import Path

    script = Path(__file__).resolve().parents[1] / "scripts" / "evaluate_activity.py"
    out = subprocess.run([sys.executable, str(script), "--dataset", str(tmp_path)], capture_output=True, text=True, timeout=120)
    assert out.returncode == 0 and "PENDING" in out.stdout
