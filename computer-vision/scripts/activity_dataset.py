"""Activity dataset housekeeping: folder layout, leakage-safe splits, checks.

    python scripts/activity_dataset.py init     # create data/activity_dataset/{raw,annotated,processed,train,validation,test,reports}
    python scripts/activity_dataset.py split    # fill the split column by recording / person group (writes a .bak first)
    python scripts/activity_dataset.py check    # leakage between splits, class balance per split, missing files

Splitting is by *group*, never by clip: segments that share a recording or a
subject group (the same person, pseudonymised) always land in the same split,
so the model is never tested on footage it has effectively seen. Nothing here
creates or downloads footage; the dataset stays out of Git (data/ is ignored).
"""
from __future__ import annotations

import argparse
import csv
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from app.activity_recognition.evaluation import SplitRatios, check_leakage, class_balance, group_split  # noqa: E402

ROOT = Path(__file__).resolve().parents[1] / "data" / "activity_dataset"
FOLDERS = ("raw", "annotated", "processed", "train", "validation", "test", "reports")
COLUMNS = ["clip_id", "file", "split", "recording_id", "subject_group", "camera_angle", "label", "start_s", "end_s",
           "roi_x", "roi_y", "roi_w", "roi_h", "annotator", "notes"]
LABELS = {"STANDING", "SITTING", "WALKING", "BENDING", "REACHING", "OTHER"}


def _read(path: Path) -> list[dict]:
    if not path.is_file():
        return []
    with path.open(newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def _components(rows: list[dict]) -> list[str]:
    """Union-find over recordings and subject groups: rows linked by either share a component."""
    parent: dict[str, str] = {}

    def find(x: str) -> str:
        parent.setdefault(x, x)
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    keys = []
    for i, r in enumerate(rows):
        rec, subj = f"rec:{(r.get('recording_id') or '').strip() or f'row{i}'}", (r.get("subject_group") or "").strip()
        find(rec)
        if subj:
            parent[find(f"subj:{subj}")] = find(rec)
        keys.append(rec)
    return [find(k) for k in keys]


def cmd_init(root: Path) -> int:
    for d in FOLDERS:
        (root / d).mkdir(parents=True, exist_ok=True)
    ann = root / "annotations.csv"
    if not ann.exists():
        ann.write_text(",".join(COLUMNS) + "\n", encoding="utf-8")
    print(f"Dataset folders ready under {root}. Put clips in raw/, annotate in annotations.csv (header written).")
    return 0


def cmd_split(root: Path, ratios: SplitRatios, seed: str) -> int:
    ann = root / "annotations.csv"
    rows = _read(ann)
    if not rows:
        print(f"No annotations in {ann}.")
        return 1
    comps = _components(rows)
    assignment = group_split(comps, ratios, seed)
    for r, c in zip(rows, comps):
        r["split"] = assignment[c]
    shutil.copyfile(ann, ann.with_suffix(".csv.bak"))
    with ann.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    print(f"Assigned {len(set(comps))} recording/person groups to splits.")
    return cmd_check(root)


def cmd_check(root: Path) -> int:
    rows = _read(root / "annotations.csv")
    if not rows:
        print("No annotations yet.")
        return 1
    bad_labels = sorted({(r.get("label") or "").strip().upper() for r in rows} - LABELS)
    missing = sorted({r["file"] for r in rows if r.get("file") and not (root / r["file"]).is_file()})
    leaks = check_leakage(rows)
    for split in ("train", "validation", "test"):
        part = [r["label"].strip().upper() for r in rows if r.get("split") == split and r.get("label")]
        secs = sum(max(0.0, float(r["end_s"]) - float(r["start_s"])) for r in rows if r.get("split") == split and r.get("start_s") and r.get("end_s"))
        b = class_balance(part)
        print(f"{split:10}: {len(part)} segments, {secs:.0f} s, counts {b['counts']}, imbalance ratio {b['imbalanceRatio']}")
    unsplit = sum(1 for r in rows if not r.get("split"))
    if unsplit:
        print(f"{unsplit} segment(s) have no split: run 'split'.")
    if bad_labels:
        print(f"Unknown labels: {bad_labels} (allowed: {sorted(LABELS)})")
    if missing:
        print(f"Missing clip files: {missing[:10]}{' …' if len(missing) > 10 else ''}")
    if leaks:
        print("LEAKAGE between splits:\n  " + "\n  ".join(leaks))
    return 2 if leaks or bad_labels else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["init", "split", "check"])
    ap.add_argument("--dataset", type=Path, default=ROOT)
    ap.add_argument("--train", type=float, default=0.7)
    ap.add_argument("--validation", type=float, default=0.15)
    ap.add_argument("--seed", default="bunonbrain-activity-v1")
    a = ap.parse_args()
    if a.command == "init":
        return cmd_init(a.dataset)
    if a.command == "split":
        return cmd_split(a.dataset, SplitRatios(a.train, a.validation, max(0.0, 1 - a.train - a.validation)), a.seed)
    return cmd_check(a.dataset)


if __name__ == "__main__":
    raise SystemExit(main())
