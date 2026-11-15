"""Garment dataset preparation: label registry, annotations, clips, splits, leakage, distribution, video import.

Test videos are generated here (moving shapes), never factory footage, and are
only used to exercise import, metadata, clip export and duplicate detection.
"""
import json

import cv2
import numpy as np
import pytest

from app.garment_activities.activity_labels import UNKNOWN, LabelError, LabelRegistry
from app.garment_activities.clip_generator import clips_from_annotations, windows
from app.garment_activities.config import ClipConfig, DatasetPaths
from app.garment_activities.dataset_split import SplitPlan, assign, dataset_version
from app.garment_activities.dataset_validator import check_leakage, distribution, validate_annotations
from app.garment_activities.schemas import Annotation, ClipRecord, VideoRecord, read_annotation_rows, write_annotation_csv, write_jsonl

REG = LabelRegistry.load()


def video(vid="v-1", duration=30.0, camera="CAM-001", group=None) -> VideoRecord:
    return VideoRecord(vid, f"raw/unsorted/{vid}.mp4", f"{vid}.mp4", vid * 4, 1, 25.0, int(duration * 25), duration, 640, 360, "mp4v",
                       camera, group)


def ann(aid, vid, s, e, labels, status="LABELLED", subject=None, annotator="ann-1"):
    return Annotation(aid, vid, s, e, labels, status, annotator, 1.0, subject_group=subject)


# ─── Labels ───────────────────────────────────────────────────────────────

def test_the_registry_has_the_garment_classes_and_unknown_is_always_valid():
    assert REG.names[:3] == ("SEWING", "FABRIC_HANDLING", "CUTTING") and len(REG.names) == 12
    assert UNKNOWN in REG.names and UNKNOWN not in REG.trainable
    assert REG.normalise("quality inspection") == "QUALITY_INSPECTION" and REG.get("SITTING").kind == "posture"
    with pytest.raises(LabelError):
        REG.normalise("IDLE")                                       # stationary is not "idle": no such class


def test_factory_specific_classes_can_be_added_and_bad_definitions_are_refused():
    reg = LabelRegistry.from_dict({"labels": [{"name": "SEWING", "kind": "task"}, {"name": "button_attaching", "kind": "task"}]})
    assert reg.names == ("SEWING", "BUTTON_ATTACHING", UNKNOWN)       # UNKNOWN is added when missing
    for bad in ([{"name": "x"}], [{"name": "SEWING", "kind": "dance"}], [{"name": "SEWING"}, {"name": "SEWING"}], [{"name": "UNKNOWN", "kind": "unknown"}]):
        with pytest.raises(LabelError):
            LabelRegistry.from_dict({"labels": bad})


# ─── Annotations ──────────────────────────────────────────────────────────

def rows(**over):
    base = {"annotation_id": "a1", "video_id": "v-1", "start_s": 2, "end_s": 10, "labels": ["sewing"], "status": "LABELLED", "annotator": "ann-1",
            "confidence": 0.9}
    return [base | over]


def test_valid_annotations_are_normalised():
    res = validate_annotations(rows(subject_roi={"x": "0.1", "y": "0.2", "w": "0.3", "h": "0.5"}), {"v-1": video()}, REG)
    assert res.ok and res.annotations[0].labels == ["SEWING"] and res.annotations[0].subject_roi == {"x": 0.1, "y": 0.2, "w": 0.3, "h": 0.5}
    assert res.annotations[0].camera_id == "CAM-001"                 # taken from the video


@pytest.mark.parametrize("over, fragment", [
    ({"video_id": "v-x"}, "unknown video_id"),
    ({"start_s": 5, "end_s": 5}, "start_s < end_s"),
    ({"end_s": 99}, "after the video's end"),
    ({"labels": ["DANCING"]}, "not a registered"),
    ({"labels": []}, "at least one label"),
    ({"labels": ["UNKNOWN", "SEWING"]}, "can't be combined"),
    ({"status": "MAYBE"}, "status must be"),
    ({"confidence": 1.5}, "between 0 and 1"),
    ({"subject_roi": {"x": 0.9, "y": 0, "w": 0.5, "h": 0.5}}, "subject_roi"),
])
def test_invalid_annotations_are_reported(over, fragment):
    res = validate_annotations(rows(**over), {"v-1": video()}, REG)
    assert not res.ok and any(fragment in i.message for i in res.errors), [str(i) for i in res.issues]


def test_ambiguous_and_unusable_annotations_and_annotator_disagreement():
    res = validate_annotations([
        {"annotation_id": "a1", "video_id": "v-1", "start_s": 0, "end_s": 5, "labels": [], "status": "AMBIGUOUS", "annotator": "ann-1"},
        {"annotation_id": "a2", "video_id": "v-1", "start_s": 5, "end_s": 8, "labels": [], "status": "UNUSABLE", "annotator": "ann-1"},
        {"annotation_id": "a3", "video_id": "v-1", "start_s": 10, "end_s": 20, "labels": ["SEWING"], "annotator": "ann-1"},
        {"annotation_id": "a4", "video_id": "v-1", "start_s": 15, "end_s": 25, "labels": ["SITTING"], "annotator": "ann-2"},
    ], {"v-1": video()}, REG)
    assert res.ok
    assert res.annotations[0].labels == [UNKNOWN] and res.annotations[1].labels == []
    assert any("disagree" in i.message for i in res.issues)          # reported for review, not silently merged


def test_csv_and_jsonl_annotations_round_trip(tmp_path):
    src = [{"annotation_id": "a1", "video_id": "v-1", "start_s": 1, "end_s": 5, "labels": ["SEWING", "FABRIC_HANDLING"], "status": "LABELLED",
            "annotator": "ann-1", "confidence": 0.8, "subject_roi": {"x": 0.1, "y": 0.1, "w": 0.2, "h": 0.4}, "notes": "both hands on fabric"}]
    write_annotation_csv(tmp_path / "a.csv", src)
    back = read_annotation_rows(tmp_path / "a.csv")
    assert back[0]["labels"] == ["SEWING", "FABRIC_HANDLING"] and back[0]["subject_roi"]["w"] == "0.2"
    write_jsonl(tmp_path / "a.jsonl", src)
    assert read_annotation_rows(tmp_path / "a.jsonl") == src
    assert validate_annotations(back, {"v-1": video()}, REG).ok


# ─── Clips ────────────────────────────────────────────────────────────────

def test_windows_have_a_fixed_duration_and_stay_inside_the_video():
    assert windows(10.0, 4.0, 2.0) == [(0.0, 4.0), (2.0, 6.0), (4.0, 8.0), (6.0, 10.0)]
    assert windows(10.0, 6.0, 6.0, start=2.0, end=30.0) == [(2.0, 8.0)]
    assert windows(3.0, 4.0, 2.0) == []


@pytest.mark.parametrize("duration", [2.0, 4.0, 6.0])
def test_clip_duration_and_sampling_are_configurable(duration):
    cfg = ClipConfig(duration_s=duration, stride_s=duration, sample_fps=5)
    clips, _ = clips_from_annotations([ann("a1", "v-1", 0, 12, ["SEWING"])], {"v-1": video()}, cfg)
    assert len(clips) == int(12 // duration) and all(c.duration_s == duration for c in clips)
    assert cfg.frames == int(duration * 5)


def test_overlapping_annotations_give_multi_label_clips_with_a_primary_label():
    cfg = ClipConfig(duration_s=4, stride_s=2, min_overlap=0.5)
    clips, _ = clips_from_annotations([ann("a1", "v-1", 0, 10, ["SEWING"]), ann("a2", "v-1", 6, 12, ["FABRIC_HANDLING"])], {"v-1": video()}, cfg)
    at = {c.start_s: c for c in clips}
    assert at[0.0].labels == ["SEWING"]
    assert set(at[6.0].labels) == {"SEWING", "FABRIC_HANDLING"} and at[6.0].primary_label in at[6.0].labels
    assert at[8.0].primary_label == "FABRIC_HANDLING"                 # covers 4 of 4 s vs sewing's 2


def test_ambiguous_segments_become_unknown_and_unusable_footage_is_dropped():
    cfg = ClipConfig(duration_s=4, stride_s=2)
    clips, _ = clips_from_annotations([ann("a1", "v-1", 0, 8, [UNKNOWN], status="AMBIGUOUS"), ann("a2", "v-1", 10, 20, ["CUTTING"]),
                                       ann("u", "v-1", 13, 15, [], status="UNUSABLE")], {"v-1": video()}, cfg)
    assert {c.primary_label for c in clips if c.start_s < 8} == {UNKNOWN} and all(c.status == "AMBIGUOUS" for c in clips if c.start_s < 8)
    cutting = [c for c in clips if c.primary_label == "CUTTING"]
    assert cutting and all(c.end_s <= 13 or c.start_s >= 15 for c in cutting)


def test_subjects_are_kept_apart_and_clip_ids_are_stable():
    cfg = ClipConfig(duration_s=4, stride_s=4)
    anns = [ann("a1", "v-1", 0, 8, ["SEWING"], subject="P01"), ann("a2", "v-1", 0, 8, ["PACKING"], subject="P02")]
    clips, _ = clips_from_annotations(anns, {"v-1": video()}, cfg)
    assert {(c.subject_group, c.primary_label) for c in clips} == {("P01", "SEWING"), ("P02", "PACKING")}
    again, _ = clips_from_annotations(anns, {"v-1": video()}, cfg)
    assert [c.clip_id for c in clips] == [c.clip_id for c in again] and len({c.clip_id for c in clips}) == 4


# ─── Splits and leakage ───────────────────────────────────────────────────

def many_clips(n_videos=40, per_video=5):
    out = []
    for v in range(n_videos):
        for k in range(per_video):
            out.append(ClipRecord(f"c-{v}-{k}", f"v-{v}", k * 4.0, k * 4.0 + 4, ["SEWING", "CUTTING", "PACKING"][v % 3], ["SEWING", "CUTTING", "PACKING"][v % 3],
                                  "LABELLED", [], camera_id=f"CAM-{v % 4}", subject_group=f"P{v // 2}"))
    return out


def test_splits_are_by_video_group_and_reproducible():
    clips = many_clips()
    meta = assign(clips, SplitPlan(seed=7))
    assert check_leakage(clips) == []
    by_video = {}
    for c in clips:
        by_video.setdefault(c.video_id, set()).add(c.split)
    assert all(len(s) == 1 for s in by_video.values())               # a recording never straddles splits
    again = many_clips()
    assign(again, SplitPlan(seed=7))
    assert [c.split for c in clips] == [c.split for c in again]
    assert set(meta["counts"]) == {"train", "validation", "test"} and meta["seed"] == 7 and meta["datasetVersion"].startswith("ds-")
    other = many_clips()
    assign(other, SplitPlan(seed=8))
    assert [c.split for c in other] != [c.split for c in clips]


def test_people_and_near_duplicates_pull_videos_into_the_same_split():
    clips = many_clips()
    assign(clips, SplitPlan(seed=3), near_dupes=[("v-0", "v-39", 2.0)])
    split = {c.video_id: c.split for c in clips}
    assert split["v-0"] == split["v-1"]                               # same person P0
    assert split["v-0"] == split["v-39"]                              # near-duplicate recordings


def test_holding_out_a_camera_makes_it_the_test_set():
    clips = many_clips()
    assign(clips, SplitPlan(seed=1, holdout_cameras=("CAM-2",)))
    assert all(c.split == "test" for c in clips if c.camera_id == "CAM-2")
    assert {c.split for c in clips if c.camera_id != "CAM-2" and c.split == "test"} <= {"test"}


def test_leakage_is_detected():
    clips = [ClipRecord("c1", "v-1", 0, 4, "SEWING", ["SEWING"], "LABELLED", [], split="train", subject_group="P1"),
             ClipRecord("c2", "v-1", 4, 8, "SEWING", ["SEWING"], "LABELLED", [], split="test", subject_group="P2"),
             ClipRecord("c3", "v-2", 0, 4, "CUTTING", ["CUTTING"], "LABELLED", [], split="validation", subject_group="P1"),
             ClipRecord("c4", "v-3", 0, 4, "CUTTING", ["CUTTING"], "LABELLED", [], split="test")]
    problems = check_leakage(clips, [("v-2", "v-3", 1.5)])
    assert any("video_id 'v-1'" in p for p in problems) and any("subject_group 'P1'" in p for p in problems)
    assert any("near-duplicate" in p for p in problems)


def test_class_distribution_and_dataset_version():
    clips = many_clips()
    assign(clips, SplitPlan(seed=7))
    d = distribution(clips, REG.names)
    assert d["all"]["clips"] == 200 and d["all"]["byLabel"]["SEWING"] == 70
    assert "IRONING" in d["all"]["missing"] and d["all"]["imbalanceRatio"] == pytest.approx(70 / 65, abs=0.01)
    v1 = dataset_version(clips)
    clips[0].labels = ["CUTTING"]
    assert dataset_version(clips) != v1                               # relabelling changes the dataset version


# ─── Video import ─────────────────────────────────────────────────────────

def make_video(path, frames=40, seed=0, size=(160, 120)):
    rng = np.random.default_rng(seed)
    w = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), 10.0, size)
    base = rng.integers(0, 255, (size[1], size[0], 3), dtype=np.uint8)
    for i in range(frames):
        f = base.copy()
        cv2.rectangle(f, (10 + i, 30), (40 + i, 90), (255, 255, 255), -1)
        w.write(f)
    w.release()
    return path


def test_videos_are_imported_with_metadata_and_duplicates_are_caught(tmp_path):
    from app.garment_activities.video_preprocessor import export_clip, import_video, near_duplicates, read_frames
    from app.garment_activities.schemas import load_videos

    paths = DatasetPaths(tmp_path / "ds")
    paths.ensure()
    a = make_video(tmp_path / "a.mp4", seed=1)
    rec, outcome = import_video(a, paths, camera_id="CAM-001", recording_group="rec-1", label_hint="sewing")
    assert outcome == "imported" and rec.video_id.startswith("v-") and rec.file.startswith("raw/sewing/")
    assert rec.fps == 10.0 and rec.frame_count == 40 and rec.duration_s == 4.0 and (rec.width, rec.height) == (160, 120)
    assert import_video(a, paths)[1] == "duplicate"
    b = tmp_path / "b.mp4"
    make_video(b, seed=1, frames=38)                                 # a trimmed re-encode of the same picture
    import_video(b, paths)
    c = make_video(tmp_path / "c.mp4", seed=99)
    import_video(c, paths)
    videos = load_videos(paths.videos_manifest)
    dupes = near_duplicates(videos)
    ids = {rec.video_id}
    assert any({x, y} & ids and d <= 6 for x, y, d in dupes)
    frames = read_frames(paths.root / rec.file, 1.0, 3.0, 4.0)
    assert [t for t, _ in frames] == [1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75]
    out = export_clip(paths.root / rec.file, 1.0, 3.0, tmp_path / "clip.mp4")
    cap = cv2.VideoCapture(str(out))
    assert int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) == 20
    cap.release()
    assert json.loads(paths.videos_manifest.read_text().splitlines()[0])["camera_id"] == "CAM-001"
