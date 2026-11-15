"""Records of the garment activity pipeline: videos, annotations, clips, predictions, model metadata.

Annotations are time segments of a video (or of a pre-cut clip) with one or
more labels:

* ``status`` LABELLED: the labels are what the subject is visibly doing;
  AMBIGUOUS: the annotator couldn't tell (treated as UNKNOWN);
  UNUSABLE: the footage can't be used (blurred, blocked, no subject): no clip
  overlapping it is generated.
* several labels, or overlapping annotations of the same subject, describe a
  person doing several things in a short period (the clip keeps all of them
  and its *primary* label is the one covering most of the clip).
* ``subject_roi`` (0–1 box at ``start_s``) says which person is meant when
  several are in view; ``subject_group`` is a pseudonym (never a name or an
  employee code).
"""
from __future__ import annotations

import csv
import hashlib
import json
from dataclasses import asdict, dataclass, field
from pathlib import Path

ANNOTATION_STATUSES = ("LABELLED", "AMBIGUOUS", "UNUSABLE")
SPLITS = ("train", "validation", "test")


@dataclass
class VideoRecord:
    video_id: str
    file: str                         # relative to the dataset root
    original_name: str
    sha256: str
    size_bytes: int
    fps: float
    frame_count: int
    duration_s: float
    width: int
    height: int
    codec: str
    camera_id: str | None = None
    recording_group: str | None = None   # recordings that belong together (same session) never cross splits
    label_hint: str | None = None        # the raw/<folder> it was imported into; a hint, never a label
    imported_at: str = ""
    signature: list[int] = field(default_factory=list)   # perceptual hashes of sampled frames (near-duplicate check)


@dataclass
class Annotation:
    annotation_id: str
    video_id: str
    start_s: float
    end_s: float
    labels: list[str]
    status: str = "LABELLED"
    annotator: str = ""                  # pseudonymous annotator id
    confidence: float = 1.0              # the annotator's confidence, 0–1
    clip_id: str | None = None           # when annotating a pre-cut clip
    camera_id: str | None = None
    subject_group: str | None = None
    subject_roi: dict | None = None      # {"x","y","w","h"} 0–1 at start_s
    notes: str = ""


@dataclass
class ClipRecord:
    clip_id: str
    video_id: str
    start_s: float
    end_s: float
    primary_label: str
    labels: list[str]
    status: str                          # LABELLED | AMBIGUOUS
    annotation_ids: list[str]
    camera_id: str | None = None
    subject_group: str | None = None
    subject_roi: dict | None = None
    recording_group: str | None = None
    split: str | None = None
    features: str | None = None          # relative path of the extracted feature file
    feature_quality: dict | None = None

    @property
    def duration_s(self) -> float:
        return self.end_s - self.start_s


@dataclass(frozen=True)
class GarmentPrediction:
    label: str                           # UNKNOWN when uncertain
    confidence: float
    candidate: str | None
    scores: dict[str, float]
    window_start: float
    window_end: float
    reason: str | None = None            # LOW_CONFIDENCE | CONFLICTING | INSUFFICIENT_FRAMES | NO_PERSON | NO_POSE | MODEL_UNKNOWN
    quality: dict = field(default_factory=dict)

    @property
    def uncertain(self) -> bool:
        return self.label == "UNKNOWN"


def clip_id_for(video_id: str, start_s: float, duration_s: float, subject: str | None = None) -> str:
    key = f"{video_id}|{start_s:.3f}|{duration_s:.3f}|{subject or ''}"
    return "c-" + hashlib.sha1(key.encode()).hexdigest()[:12]


# ─── JSONL / CSV ──────────────────────────────────────────────────────────

def read_jsonl(path: Path) -> list[dict]:
    if not path.is_file():
        return []
    out = []
    for i, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = line.strip()
        if line and not line.startswith("#"):
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise ValueError(f"{path.name} line {i}: not valid JSON ({exc.msg})") from exc
    return out


def write_jsonl(path: Path, rows) -> None:  # noqa: ANN001
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8", newline="\n") as f:
        for r in rows:
            f.write(json.dumps(asdict(r) if hasattr(r, "__dataclass_fields__") else r, ensure_ascii=False) + "\n")
    tmp.replace(path)


ANNOTATION_CSV_COLUMNS = ["annotation_id", "video_id", "clip_id", "camera_id", "start_s", "end_s", "labels", "status", "annotator",
                          "confidence", "subject_group", "roi_x", "roi_y", "roi_w", "roi_h", "notes"]


def read_annotation_rows(path: Path) -> list[dict]:
    """Raw annotation rows from JSONL or CSV (CSV: labels separated by '|', ROI in roi_x / roi_y / roi_w / roi_h)."""
    if path.suffix.lower() == ".jsonl":
        return read_jsonl(path)
    if not path.is_file():
        return []
    rows = []
    with path.open(newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            row = {k: (v.strip() if isinstance(v, str) else v) for k, v in r.items() if k}
            row["labels"] = [x for x in (row.get("labels") or "").split("|") if x.strip()]
            if all(row.get(k) for k in ("roi_x", "roi_y", "roi_w", "roi_h")):
                row["subject_roi"] = {k[4:]: row[k] for k in ("roi_x", "roi_y", "roi_w", "roi_h")}
            for k in ("roi_x", "roi_y", "roi_w", "roi_h"):
                row.pop(k, None)
            rows.append(row)
    return rows


def write_annotation_csv(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=ANNOTATION_CSV_COLUMNS)
        w.writeheader()
        for r in rows:
            roi = r.get("subject_roi") or {}
            w.writerow({**{k: r.get(k, "") for k in ANNOTATION_CSV_COLUMNS}, "labels": "|".join(r.get("labels") or []),
                        "roi_x": roi.get("x", ""), "roi_y": roi.get("y", ""), "roi_w": roi.get("w", ""), "roi_h": roi.get("h", "")})


def load_videos(path: Path) -> dict[str, VideoRecord]:
    return {r["video_id"]: VideoRecord(**r) for r in read_jsonl(path)}


def load_clips(path: Path) -> list[ClipRecord]:
    return [ClipRecord(**r) for r in read_jsonl(path)]
