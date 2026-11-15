"""Garment activity dataset, training, evaluation and inference: one command-line workflow.

    python scripts/garment.py init                                   # dataset folders + annotation templates
    python scripts/garment.py import D:/footage/line3_cam2.mp4 --camera CAM-002 --recording-group L3-2026-11-20 [--label-hint sewing]
    python scripts/garment.py frames <video_id> --every 2             # JPEG previews for annotators
    python scripts/garment.py cut <video_id> --duration 4 [--export] # fixed windows → annotations template rows (+ MP4 clips)
    python scripts/garment.py validate                               # annotations, labels, conflicts, near-duplicates
    python scripts/garment.py clips --config configs/garment_training.json
    python scripts/garment.py split --seed 13 [--holdout-camera CAM-003]
    python scripts/garment.py features --config configs/garment_training.json
    python scripts/garment.py report                                 # class distribution per split
    python scripts/garment.py train --config configs/garment_training.json
    python scripts/garment.py evaluate models/garment_activity/garment-cnn-gru/0.1.0 [--split test]
    python scripts/garment.py infer models/garment_activity/garment-cnn-gru/0.1.0 data/some_video.mp4 [--out preds.json]
    python scripts/garment.py export-report data/garment_activity/reports/<report>.json

Every step reads and writes data/garment_activity/ (or --dataset). Nothing is
fabricated: without labelled clips, training refuses and evaluation reports
PENDING.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
for _stream in (sys.stdout, sys.stderr):           # Windows consoles default to cp1252; keep arrows and names printable
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

from app.garment_activities.activity_labels import LabelRegistry  # noqa: E402
from app.garment_activities.config import MODELS_DIR, ClipConfig, DatasetPaths, FeatureConfig, load_training_config  # noqa: E402
from app.garment_activities.schemas import clip_id_for, load_clips, load_videos, write_annotation_csv, write_jsonl  # noqa: E402


def _paths(a) -> DatasetPaths:  # noqa: ANN001
    return DatasetPaths(Path(a.dataset).resolve()) if a.dataset else DatasetPaths()


def _registry(a) -> LabelRegistry:  # noqa: ANN001
    return LabelRegistry.load(Path(a.labels) if a.labels else None)


def _config(a):  # noqa: ANN001, ANN202
    if a.config:
        return load_training_config(Path(a.config))
    from app.garment_activities.config import DecisionConfig, TrainingConfig
    return TrainingConfig(), ClipConfig(), FeatureConfig(), DecisionConfig()


def _models(device: str):  # noqa: ANN202
    from app.activity_recognition.pose_estimator import YoloPoseEstimator
    from app.core.config import get_settings
    from app.core.device import select_device
    from app.detection.detector import YoloPersonDetector

    s = get_settings()
    dev = select_device(device or s.device).device
    det = YoloPersonDetector(s.model_path, dev, s.confidence, s.image_size)
    pose = YoloPoseEstimator(s.activity_model_path, dev, s.activity_pose_confidence, s.activity_image_size)
    for m in (det.load(), pose.load()):
        if not m.loaded:
            raise SystemExit(f"Model not loaded: {m.error}")
    return det, pose, dev


# ─── commands ─────────────────────────────────────────────────────────────

def cmd_init(a) -> int:  # noqa: ANN001
    p = _paths(a)
    p.ensure()
    for label in _registry(a).trainable:
        (p.raw / label.lower()).mkdir(parents=True, exist_ok=True)
    if not p.annotations().exists():
        write_annotation_csv(p.root / "annotations.csv", [])
    print(f"Dataset ready at {p.root}: raw/<activity>/, annotated/, processed/, train/, validation/, test/, reports/, annotations.csv")
    return 0


def cmd_import(a) -> int:  # noqa: ANN001
    from app.garment_activities.video_preprocessor import VIDEO_EXTENSIONS, VideoError, import_video, near_duplicates

    p = _paths(a)
    p.ensure()
    src = Path(a.source)
    files = sorted(f for f in src.rglob("*") if f.suffix.lower() in VIDEO_EXTENSIONS) if src.is_dir() else [src]
    if not files:
        print(f"No videos found at {src}.")
        return 1
    hint = a.label_hint
    if hint:
        hint = _registry(a).normalise(hint).lower()
    for f in files:
        try:
            rec, outcome = import_video(f, p, a.camera, a.recording_group, hint, a.move)
            print(f"{outcome:9} {f.name} → {rec.video_id} ({rec.duration_s} s, {rec.width}×{rec.height} @ {rec.fps} fps, {rec.codec})")
        except VideoError as exc:
            print(f"skipped   {exc}")
    dupes = near_duplicates(load_videos(p.videos_manifest))
    for x, y, d in dupes:
        print(f"WARNING near-duplicate videos {x} and {y} (distance {d}): they will always be kept in the same split.")
    return 0


def _video(p: DatasetPaths, video_id: str):  # noqa: ANN202
    v = load_videos(p.videos_manifest).get(video_id)
    if v is None:
        raise SystemExit(f"Unknown video_id {video_id} (see {p.videos_manifest}).")
    return v, p.root / v.file


def cmd_frames(a) -> int:  # noqa: ANN001
    from app.garment_activities.video_preprocessor import extract_frames

    p = _paths(a)
    v, path = _video(p, a.video_id)
    n = extract_frames(path, p.annotated / "frames" / v.video_id, a.every, a.max)
    print(f"{n} frame(s) written to {p.annotated / 'frames' / v.video_id}")
    return 0


def cmd_cut(a) -> int:  # noqa: ANN001
    from app.garment_activities.clip_generator import windows
    from app.garment_activities.video_preprocessor import export_clip

    p = _paths(a)
    v, path = _video(p, a.video_id)
    rows = []
    for s, e in windows(v.duration_s, a.duration, a.stride or a.duration):
        cid = clip_id_for(v.video_id, s, e - s)
        if a.export:
            export_clip(path, s, e, p.annotated / "clips" / f"{cid}.mp4")
        rows.append({"annotation_id": f"a-{cid[2:]}", "video_id": v.video_id, "clip_id": cid, "camera_id": v.camera_id or "",
                     "start_s": s, "end_s": e, "labels": [], "status": "", "annotator": "", "confidence": "", "notes": ""})
    dest = p.annotated / f"{v.video_id}_template.csv"
    write_annotation_csv(dest, rows)
    print(f"{len(rows)} window(s) of {a.duration} s. Fill labels / status / annotator in {dest}, then append the rows to "
          f"{p.root / 'annotations.csv'}." + (f" Clips: {p.annotated / 'clips'}" if a.export else ""))
    return 0


def cmd_validate(a) -> int:  # noqa: ANN001
    from app.garment_activities.dataset_loader import load_annotations
    from app.garment_activities.dataset_validator import check_leakage
    from app.garment_activities.video_preprocessor import near_duplicates

    p = _paths(a)
    reg = _registry(a)
    res = load_annotations(p, reg)
    videos = load_videos(p.videos_manifest)
    print(f"{len(videos)} video(s), {len(res.annotations)} valid annotation(s), {len(res.errors)} error(s), "
          f"{len(res.issues) - len(res.errors)} warning(s).")
    for i in res.issues[:200]:
        print(" ", i)
    dupes = near_duplicates(videos)
    for x, y, d in dupes:
        print(f"  WARNING near-duplicate videos {x} and {y} (distance {d})")
    leaks = check_leakage(load_clips(p.clips_manifest), dupes)
    for leak in leaks:
        print(f"  ERROR   leakage: {leak}")
    return 0 if res.ok and not leaks else 2


def cmd_clips(a) -> int:  # noqa: ANN001
    from app.garment_activities.clip_generator import clips_from_annotations
    from app.garment_activities.dataset_loader import load_annotations

    p = _paths(a)
    _, clip_cfg, _, _ = _config(a)
    res = load_annotations(p, _registry(a))
    if not res.ok:
        print(f"{len(res.errors)} annotation error(s): run 'validate' and fix them first.")
        return 2
    clips, notes = clips_from_annotations(res.annotations, load_videos(p.videos_manifest), clip_cfg)
    old = {c.clip_id: c for c in load_clips(p.clips_manifest)}
    for c in clips:                                    # keep split and extracted features of unchanged clips
        if c.clip_id in old:
            c.split, c.features, c.feature_quality = old[c.clip_id].split, old[c.clip_id].features, old[c.clip_id].feature_quality
    write_jsonl(p.clips_manifest, clips)
    for n in notes[:50]:
        print("  note:", n)
    print(f"{len(clips)} clip(s) of {clip_cfg.duration_s} s (stride {clip_cfg.stride_s} s) → {p.clips_manifest}")
    return 0


def cmd_split(a) -> int:  # noqa: ANN001
    from app.garment_activities.dataset_loader import write_split_lists
    from app.garment_activities.dataset_split import SplitPlan, assign
    from app.garment_activities.dataset_validator import check_leakage
    from app.garment_activities.video_preprocessor import near_duplicates

    p = _paths(a)
    clips = load_clips(p.clips_manifest)
    if not clips:
        print("No clips: run 'clips' first.")
        return 1
    dupes = near_duplicates(load_videos(p.videos_manifest))
    meta = assign(clips, SplitPlan(a.train, a.validation, round(1 - a.train - a.validation, 4), a.seed, tuple(a.holdout_camera or ())), dupes)
    leaks = check_leakage(clips, dupes)
    if leaks:
        print("Leakage after splitting (this is a bug):", leaks)
        return 2
    write_jsonl(p.clips_manifest, clips)
    p.split_file.write_text(json.dumps(meta, indent=2), encoding="utf-8")
    write_split_lists(p, clips)
    print(f"Split {meta['counts']} with seed {a.seed} ({meta['method']}), dataset version {meta['datasetVersion']} → {p.split_file}")
    return cmd_report(a)


def cmd_features(a) -> int:  # noqa: ANN001
    from app.garment_activities.dataset_loader import save_features
    from app.garment_activities.feature_extractor import ClipFeatureExtractor, make_embedder

    p = _paths(a)
    _, clip_cfg, feat_cfg, _ = _config(a)
    clips = load_clips(p.clips_manifest)
    todo = [c for c in clips if a.force or not c.features]
    if a.split:
        todo = [c for c in todo if c.split == a.split]
    if not todo:
        print("Nothing to extract (all clips have features; --force re-extracts).")
        return 0
    det, pose, dev = _models(a.device)
    ext = ClipFeatureExtractor(det, pose, make_embedder(feat_cfg, dev), feat_cfg, clip_cfg.sample_fps)
    videos = load_videos(p.videos_manifest)
    for i, c in enumerate(todo, 1):
        started = time.perf_counter()
        x, q = ext.extract(p.root / videos[c.video_id].file, c.start_s, c.end_s, c.subject_roi)
        q["extractionMs"] = round((time.perf_counter() - started) * 1000, 1)
        q["clipConfig"] = {"duration_s": clip_cfg.duration_s, "sample_fps": clip_cfg.sample_fps, "backbone": feat_cfg.backbone}
        c.features, c.feature_quality = save_features(p, c, x, q), q
        print(f"[{i}/{len(todo)}] {c.clip_id} {c.primary_label:18} person {q['personRatio']:.2f} pose {q['poseRatio']:.2f} "
              f"subject {'found' if q['subjectFound'] else 'NOT FOUND'} ({q['extractionMs']:.0f} ms)")
    write_jsonl(p.clips_manifest, clips)
    return 0


def cmd_report(a) -> int:  # noqa: ANN001
    from app.garment_activities.dataset_validator import distribution

    p = _paths(a)
    reg = _registry(a)
    d = distribution(load_clips(p.clips_manifest), reg.names)
    p.reports.mkdir(parents=True, exist_ok=True)
    dest = p.reports / "class_distribution.json"
    dest.write_text(json.dumps(d, indent=2), encoding="utf-8")
    for split, r in d.items():
        counts = ", ".join(f"{k} {v}" for k, v in r["byLabel"].items() if v)
        print(f"{split:11} {r['clips']:5} clips | {counts or '-'} | imbalance {r['imbalanceRatio']} | missing {len(r['missing'])} class(es)")
    print(f"→ {dest}")
    return 0


def cmd_train(a) -> int:  # noqa: ANN001
    from app.garment_activities.dataset_loader import load_split, ordered_labels
    from app.garment_activities.dataset_split import dataset_version
    from app.garment_activities.trainer import TrainingError, train

    p = _paths(a)
    reg = _registry(a)
    tcfg, clip_cfg, feat_cfg, dec = _config(a)
    clips = load_clips(p.clips_manifest)
    train0 = load_split(p, "train", None, feat_cfg.backbone, tcfg.train_on_unknown, clip_cfg.frames)
    labels = ordered_labels(set(train0.labels), reg)
    tr = load_split(p, "train", labels, feat_cfg.backbone, tcfg.train_on_unknown, clip_cfg.frames)
    va = load_split(p, "validation", labels, feat_cfg.backbone, tcfg.train_on_unknown, clip_cfg.frames)
    try:
        res = train(tr, va, tcfg, clip_cfg, feat_cfg, dec, dataset_version(clips), Path(a.out) if a.out else MODELS_DIR, reg.version)
    except TrainingError as exc:
        print(f"Not trained: {exc}")
        return 1
    v = res.metadata["validation"]
    print(f"Saved {res.directory} — best epoch {v['bestEpoch']}/{v['epochsRun']}, validation loss {v['loss']}, macro F1 {v['macroF1']} "
          "(validation only; run 'evaluate' on the test split).")
    return 0


def cmd_evaluate(a) -> int:  # noqa: ANN001
    from app.garment_activities.dataset_loader import load_split
    from app.garment_activities.evaluator import evaluate, record, to_markdown
    from app.garment_activities.inference import load_classifier

    p = _paths(a)
    clf = load_classifier(Path(a.model), "cpu", allow_unevaluated=True)
    test = load_split(p, a.split, clf.labels, clf.features.backbone, include_unknown=True, frames=clf.frames)
    result = evaluate(clf, test, [c.feature_quality or {} for c in test.clips], split=a.split)
    if a.split == "test":
        record(Path(a.model), result)
    p.reports.mkdir(parents=True, exist_ok=True)
    dest = p.reports / f"garment-eval-{clf.name}-{clf.version}-{a.split}-{time.strftime('%Y%m%d-%H%M%S')}.json"
    dest.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(to_markdown(result))
    print(f"Report: {dest}")
    return 0


def cmd_infer(a) -> int:  # noqa: ANN001
    from app.garment_activities.activity_service import infer_video
    from app.garment_activities.config import InferenceConfig
    from app.garment_activities.feature_extractor import make_embedder
    from app.garment_activities.inference import load_classifier

    det, pose, dev = _models(a.device)
    clf = load_classifier(Path(a.model), dev, allow_unevaluated=a.allow_unevaluated)
    cfg = InferenceConfig.load(Path(a.config) if a.config else None)
    res = infer_video(Path(a.video), det, pose, clf, make_embedder(clf.features, dev), cfg, a.process_fps)
    for e in res["events"]:
        extra = f" ({e['reason']}, best guess {e['candidate']})" if e["activity"] == "UNKNOWN" else ""
        print(f"{e['t']:8.2f}s track {e['track']:<4} {e['kind']:13} {e['previous'] or '-':>18} → {e['activity']:<18} {e['confidence']:.2f}{extra}")
    print(f"\n{res['framesProcessed']} frames in {res['wallSeconds']} s ({res['processingFps']} fps); model {res['metrics']['modelInferenceMs']} ms/batch; "
          f"evaluation status {clf.evaluation.get('status')}")
    if a.out:
        Path(a.out).write_text(json.dumps(res, indent=2), encoding="utf-8")
        print(f"→ {a.out}")
    return 0


def cmd_export_report(a) -> int:  # noqa: ANN001
    from app.garment_activities.evaluator import to_markdown

    src = Path(a.report)
    result = json.loads(src.read_text(encoding="utf-8"))
    dest = src.with_suffix(".md")
    dest.write_text(to_markdown(result), encoding="utf-8")
    print(f"→ {dest}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dataset", help="Dataset folder (default data/garment_activity)")
    ap.add_argument("--labels", help="Label registry JSON (default configs/garment_activity_labels.json)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("init")
    s = sub.add_parser("import"); s.add_argument("source"); s.add_argument("--camera"); s.add_argument("--recording-group")  # noqa: E702
    s.add_argument("--label-hint"); s.add_argument("--move", action="store_true")  # noqa: E702
    s = sub.add_parser("frames"); s.add_argument("video_id"); s.add_argument("--every", type=float, default=1.0); s.add_argument("--max", type=int, default=500)  # noqa: E702
    s = sub.add_parser("cut"); s.add_argument("video_id"); s.add_argument("--duration", type=float, default=4.0)  # noqa: E702
    s.add_argument("--stride", type=float); s.add_argument("--export", action="store_true")  # noqa: E702
    sub.add_parser("validate")
    s = sub.add_parser("clips"); s.add_argument("--config")  # noqa: E702
    s = sub.add_parser("split"); s.add_argument("--seed", type=int, default=13); s.add_argument("--train", type=float, default=0.7)  # noqa: E702
    s.add_argument("--validation", type=float, default=0.15); s.add_argument("--holdout-camera", action="append")  # noqa: E702
    s = sub.add_parser("features"); s.add_argument("--config"); s.add_argument("--split"); s.add_argument("--force", action="store_true")  # noqa: E702
    s.add_argument("--device", default="")
    sub.add_parser("report")
    s = sub.add_parser("train"); s.add_argument("--config"); s.add_argument("--out")  # noqa: E702
    s = sub.add_parser("evaluate"); s.add_argument("model"); s.add_argument("--split", default="test", choices=["validation", "test"])  # noqa: E702
    s = sub.add_parser("infer"); s.add_argument("model"); s.add_argument("video"); s.add_argument("--config"); s.add_argument("--out")  # noqa: E702
    s.add_argument("--process-fps", type=float, default=8.0); s.add_argument("--device", default="")  # noqa: E702
    s.add_argument("--allow-unevaluated", action="store_true", help="development: run a model without a test evaluation")
    s = sub.add_parser("export-report"); s.add_argument("report")  # noqa: E702
    a = ap.parse_args()
    return {"init": cmd_init, "import": cmd_import, "frames": cmd_frames, "cut": cmd_cut, "validate": cmd_validate, "clips": cmd_clips,
            "split": cmd_split, "features": cmd_features, "report": cmd_report, "train": cmd_train, "evaluate": cmd_evaluate,
            "infer": cmd_infer, "export-report": cmd_export_report}[a.cmd](a)


if __name__ == "__main__":
    raise SystemExit(main())
