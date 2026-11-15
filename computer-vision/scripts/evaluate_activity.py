"""Evaluate activity recognition on a labelled, properly split dataset.

    python scripts/evaluate_activity.py                       # the test split
    python scripts/evaluate_activity.py --split validation
    python scripts/evaluate_activity.py --dataset D:/activity_dataset --process-fps 8

Reads ``<dataset>/annotations.csv`` (see docs/ACTIVITY_RECOGNITION.md and
``data/activity_dataset/annotations.template.csv``), runs the real pipeline
(YOLO person detection, ByteTrack, YOLO pose, the configured classifier and
the state manager) over every clip of the chosen split, and compares the
prediction at each activity tick with the annotated label of that moment.

Reports precision, recall, F1, the confusion matrix and per-class results
for both the raw window prediction and the smoothed state, the UNKNOWN rate,
inference latency and processing FPS, and writes a JSON report to
``<dataset>/reports/``. With no labelled data it says that evaluation is
pending: it never invents a score.
"""
from __future__ import annotations

import argparse
import csv
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.activity_recognition.activity_classifier import build_classifier  # noqa: E402
from app.activity_recognition.activity_config import ActivityConfig  # noqa: E402
from app.activity_recognition.evaluation import check_leakage, class_balance, report  # noqa: E402
from app.activity_recognition.offline import analyse_clip  # noqa: E402

LABELS = ["STANDING", "SITTING", "WALKING", "BENDING", "REACHING"]
PENDING = """Model evaluation is PENDING: no labelled {split} data was found in {path}.

No accuracy can be reported without a real, separated test set. To evaluate:
  1. Record short clips (with the consent of the people filmed) from the factory's real camera angles.
  2. Put them in <dataset>/raw/ and annotate time segments per person in <dataset>/annotations.csv
     (columns: see data/activity_dataset/annotations.template.csv; guidance: docs/ACTIVITY_RECOGNITION.md).
  3. Assign splits by recording and person, never by clip:  python scripts/activity_dataset.py split
  4. Check for leakage and class balance:                  python scripts/activity_dataset.py check
  5. Run this script on the validation split while tuning, and on the test split once at the end.
"""


def _overlap_iou(a, b) -> float:  # noqa: ANN001 - (x, y, w, h) normalised
    ax2, ay2, bx2, by2 = a[0] + a[2], a[1] + a[3], b[0] + b[2], b[1] + b[3]
    iw, ih = max(0.0, min(ax2, bx2) - max(a[0], b[0])), max(0.0, min(ay2, by2) - max(a[1], b[1]))
    inter = iw * ih
    union = a[2] * a[3] + b[2] * b[3] - inter
    return inter / union if union > 0 else 0.0


def subject_ticks(ticks, seg: dict):  # noqa: ANN001, ANN201
    """The ticks of the annotated person in the segment: the track overlapping the ROI most, else the longest-seen track."""
    start, end = float(seg["start_s"]), float(seg["end_s"])
    inside = [t for t in ticks if start <= t.t <= end]
    if not inside:
        return []
    by_track: dict[int, list] = {}
    for t in inside:
        by_track.setdefault(t.track_id, []).append(t)
    roi = None
    if all(seg.get(k) not in (None, "") for k in ("roi_x", "roi_y", "roi_w", "roi_h")):
        roi = tuple(float(seg[k]) for k in ("roi_x", "roi_y", "roi_w", "roi_h"))
    if roi is not None:
        best = max(by_track, key=lambda tid: _overlap_iou(by_track[tid][0].bbox, roi))
        if _overlap_iou(by_track[best][0].bbox, roi) < 0.2:
            return []
    else:
        best = max(by_track, key=lambda tid: len(by_track[tid]))
    return by_track[best]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dataset", type=Path, default=Path(__file__).resolve().parents[1] / "data" / "activity_dataset")
    ap.add_argument("--split", default="test", choices=["train", "validation", "test"])
    ap.add_argument("--process-fps", type=float, default=8.0)
    ap.add_argument("--warmup", type=float, default=1.0, help="Seconds at the start of each segment not scored (the window is filling).")
    args = ap.parse_args()

    ann = args.dataset / "annotations.csv"
    rows = []
    if ann.is_file():
        with ann.open(newline="", encoding="utf-8") as f:
            rows = [r for r in csv.DictReader(f) if (r.get("split") or "").strip() == args.split and (r.get("label") or "").strip()]
    if not rows:
        print(PENDING.format(split=args.split, path=ann))
        return 0
    if problems := check_leakage(_all_rows(ann)):
        print("Refusing to evaluate: the splits leak.\n  " + "\n  ".join(problems))
        return 2

    from app.core.config import get_settings
    from app.core.device import select_device
    from app.detection.detector import YoloPersonDetector
    from app.activity_recognition.pose_estimator import YoloPoseEstimator

    s = get_settings()
    dev = select_device(s.device).device
    detector = YoloPersonDetector(s.model_path, dev, s.confidence, s.image_size)
    pose = YoloPoseEstimator(s.activity_model_path, dev, s.activity_pose_confidence, s.activity_image_size)
    for m in (detector.load(), pose.load()):
        if not m.loaded:
            print(f"Model not loaded: {m.error}")
            return 1
    cfg = ActivityConfig.from_settings(s).with_(enabled=True)
    classifier = build_classifier(cfg, dev)

    truth, pred_raw, pred_state = [], [], []
    runs, missing = [], 0
    clips: dict[str, list[dict]] = {}
    for r in rows:
        clips.setdefault(r["file"], []).append(r)
    for rel, segs in clips.items():
        path = (args.dataset / rel) if not Path(rel).is_absolute() else Path(rel)
        if not path.is_file():
            print(f"  missing clip: {rel}")
            missing += 1
            continue
        run = analyse_clip(path, detector, pose, classifier, cfg, args.process_fps, s.min_hits, s.lost_after_seconds)
        runs.append(run)
        for seg in segs:
            label = seg["label"].strip().upper()
            for t in subject_ticks(run.ticks, seg):
                if t.t < float(seg["start_s"]) + args.warmup:
                    continue
                truth.append(label)
                pred_raw.append(t.predicted)
                pred_state.append(t.state or "UNKNOWN")
        print(f"  {rel}: {run.frames_processed} frames in {run.wall_seconds:.1f} s ({run.processing_fps} fps), {len(run.ticks)} predictions")

    if not truth:
        print(PENDING.format(split=args.split, path=ann) + "\n(Annotations exist, but no prediction matched an annotated person.)")
        return 0
    frames = sum(r.frames_processed for r in runs)
    wall = sum(r.wall_seconds for r in runs)
    out = {
        "generatedAt": datetime.now(timezone.utc).isoformat(), "split": args.split, "dataset": str(args.dataset),
        "clips": len(runs), "missingClips": missing, "segments": len(rows),
        "classifier": {"name": classifier.name, "version": classifier.version}, "poseModel": pose.info.name, "detector": detector.info.name,
        "device": dev, "processFps": args.process_fps, "groundTruthBalance": class_balance(truth),
        "windowPrediction": report(truth, pred_raw, LABELS), "smoothedState": report(truth, pred_state, LABELS),
        "latency": {"poseInferenceMs": round(sum(r.pose_ms for r in runs) / len(runs), 2),
                    "activityStepMs": round(sum(r.step_ms for r in runs) / len(runs), 2),
                    "detectionAndTrackingMs": round(sum(r.detect_ms for r in runs) / len(runs), 2),
                    "processingFps": round(frames / wall, 2) if wall else None},
    }
    reports = args.dataset / "reports"
    reports.mkdir(parents=True, exist_ok=True)
    dest = reports / f"activity-eval-{args.split}-{time.strftime('%Y%m%d-%H%M%S')}.json"
    dest.write_text(json.dumps(out, indent=2), encoding="utf-8")
    _print(out)
    print(f"\nReport: {dest}")
    return 0


def _all_rows(ann: Path) -> list[dict]:
    with ann.open(newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def _print(out: dict) -> None:
    for name in ("windowPrediction", "smoothedState"):
        r = out[name]
        print(f"\n{name}: {r['samples']} scored predictions, UNKNOWN rate {r['unknownRate']}, accuracy {r['accuracy']}, "
              f"macro F1 {r['macro']['f1']}")
        print(f"  {'class':10} {'precision':>9} {'recall':>7} {'f1':>6} {'support':>8} {'unknown':>8}")
        for lab, c in r["perClass"].items():
            print(f"  {lab:10} {c['precision'] if c['precision'] is not None else '-':>9} {c['recall'] if c['recall'] is not None else '-':>7} "
                  f"{c['f1'] if c['f1'] is not None else '-':>6} {c['support']:>8} {c['unknown']:>8}")
        print("  confusion (rows = truth):", json.dumps(r["confusionMatrix"]))
    print("\nlatency:", out["latency"])
    print("ground truth balance:", out["groundTruthBalance"])


if __name__ == "__main__":
    raise SystemExit(main())
