"""Checks a garment dataset before anything is trained on it.

* Annotations: registered labels, valid times inside the video, a known
  video, a status, an annotator, confidence 0–1, a sane ROI; UNKNOWN never
  mixed with a task label; conflicting labels from two annotators on the same
  moment of the same subject reported.
* Splits: no video, recording group, subject group or near-duplicate pair in
  more than one split (leakage).
* Distribution: clips and seconds per class per split, imbalance ratio,
  classes missing from a split.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass, field

from app.garment_activities.activity_labels import UNKNOWN, LabelError, LabelRegistry
from app.garment_activities.schemas import ANNOTATION_STATUSES, SPLITS, Annotation, ClipRecord, VideoRecord


@dataclass
class Issue:
    severity: str          # ERROR | WARNING
    where: str
    message: str

    def __str__(self) -> str:
        return f"{self.severity:7} {self.where}: {self.message}"


@dataclass
class ValidationResult:
    annotations: list[Annotation] = field(default_factory=list)
    issues: list[Issue] = field(default_factory=list)

    @property
    def errors(self) -> list[Issue]:
        return [i for i in self.issues if i.severity == "ERROR"]

    @property
    def ok(self) -> bool:
        return not self.errors


def _num(v, name: str, where: str, issues: list[Issue]) -> float | None:  # noqa: ANN001
    try:
        return float(v)
    except (TypeError, ValueError):
        issues.append(Issue("ERROR", where, f"{name} must be a number (got {v!r})."))
        return None


def validate_annotations(rows: list[dict], videos: dict[str, VideoRecord], registry: LabelRegistry) -> ValidationResult:
    res = ValidationResult()
    seen_ids: set[str] = set()
    for i, r in enumerate(rows, 1):
        aid = str(r.get("annotation_id") or f"row{i}")
        where = f"annotation {aid}"
        issues: list[Issue] = []
        if aid in seen_ids:
            issues.append(Issue("ERROR", where, "duplicate annotation_id."))
        seen_ids.add(aid)
        vid = str(r.get("video_id") or "")
        video = videos.get(vid)
        if video is None:
            issues.append(Issue("ERROR", where, f"unknown video_id '{vid}' (import the video first)."))
        start = _num(r.get("start_s"), "start_s", where, issues)
        end = _num(r.get("end_s"), "end_s", where, issues)
        if start is not None and end is not None:
            if start < 0 or end <= start:
                issues.append(Issue("ERROR", where, f"needs 0 ≤ start_s < end_s (got {start}–{end})."))
            elif video is not None and video.duration_s and end > video.duration_s + 0.5:
                issues.append(Issue("ERROR", where, f"ends at {end} s, after the video's end ({video.duration_s} s)."))
        status = str(r.get("status") or "LABELLED").strip().upper()
        if status not in ANNOTATION_STATUSES:
            issues.append(Issue("ERROR", where, f"status must be one of {', '.join(ANNOTATION_STATUSES)}."))
        labels: list[str] = []
        for lab in r.get("labels") or []:
            try:
                labels.append(registry.normalise(str(lab)))
            except LabelError as exc:
                issues.append(Issue("ERROR", where, str(exc)))
        labels = list(dict.fromkeys(labels))
        if status == "LABELLED" and not labels:
            issues.append(Issue("ERROR", where, "a LABELLED annotation needs at least one label (use AMBIGUOUS or UNKNOWN when unsure)."))
        if UNKNOWN in labels and len(labels) > 1:
            issues.append(Issue("ERROR", where, "UNKNOWN can't be combined with another label."))
        conf = _num(r.get("confidence", 1.0) if r.get("confidence") not in (None, "") else 1.0, "confidence", where, issues)
        if conf is not None and not 0 <= conf <= 1:
            issues.append(Issue("ERROR", where, "confidence must be between 0 and 1."))
        if not str(r.get("annotator") or "").strip():
            issues.append(Issue("WARNING", where, "no annotator id (use a pseudonym, e.g. ann-02)."))
        roi = r.get("subject_roi")
        if roi:
            try:
                x, y, w, h = (float(roi[k]) for k in ("x", "y", "w", "h"))
                if not (0 <= x <= 1 and 0 <= y <= 1 and 0 < w <= 1 and 0 < h <= 1 and x + w <= 1.001 and y + h <= 1.001):
                    raise ValueError
                roi = {"x": x, "y": y, "w": w, "h": h}
            except (KeyError, TypeError, ValueError):
                issues.append(Issue("ERROR", where, "subject_roi must be {x, y, w, h} fractions of the frame (0–1)."))
                roi = None
        res.issues.extend(issues)
        if any(x.severity == "ERROR" for x in issues):
            continue
        res.annotations.append(Annotation(
            annotation_id=aid, video_id=vid, start_s=start, end_s=end, labels=labels if status == "LABELLED" else ([UNKNOWN] if status == "AMBIGUOUS" else []),
            status=status, annotator=str(r.get("annotator") or ""), confidence=conf if conf is not None else 1.0,
            clip_id=r.get("clip_id") or None, camera_id=r.get("camera_id") or (video.camera_id if video else None),
            subject_group=r.get("subject_group") or None, subject_roi=roi, notes=str(r.get("notes") or "")))
    res.issues.extend(_conflicts(res.annotations))
    return res


def _subject(a: Annotation) -> str:
    return a.subject_group or (f"roi:{a.subject_roi['x']:.2f},{a.subject_roi['y']:.2f}" if a.subject_roi else "only")


def _conflicts(anns: list[Annotation]) -> list[Issue]:
    """Two annotators giving different labels to the same moment of the same subject (agreement problem, not an error)."""
    out = []
    by: dict[tuple[str, str], list[Annotation]] = {}
    for a in anns:
        if a.status == "LABELLED":
            by.setdefault((a.video_id, _subject(a)), []).append(a)
    for (vid, subj), group in by.items():
        group.sort(key=lambda a: a.start_s)
        for i, a in enumerate(group):
            for b in group[i + 1:]:
                if b.start_s >= a.end_s:
                    break
                if a.annotator and b.annotator and a.annotator != b.annotator and set(a.labels) != set(b.labels):
                    out.append(Issue("WARNING", f"video {vid}", f"annotators {a.annotator} and {b.annotator} disagree on {subj} at "
                                                                 f"{max(a.start_s, b.start_s):.1f}–{min(a.end_s, b.end_s):.1f} s "
                                                                 f"({'|'.join(a.labels)} vs {'|'.join(b.labels)})."))
    return out


def check_leakage(clips: list[ClipRecord], near_dupes: list[tuple[str, str, float]] = ()) -> list[str]:  # noqa: B006
    """Every video, recording group, subject group and near-duplicate pair must sit in exactly one split."""
    problems = []
    for key in ("video_id", "recording_group", "subject_group"):
        where: dict[str, set[str]] = {}
        for c in clips:
            v = getattr(c, key)
            if v and c.split:
                where.setdefault(v, set()).add(c.split)
        problems += [f"{key} '{v}' is in {', '.join(sorted(s))}" for v, s in sorted(where.items()) if len(s) > 1]
    split_of_video = {}
    for c in clips:
        if c.split:
            split_of_video.setdefault(c.video_id, set()).add(c.split)
    for a, b, d in near_dupes:
        sa, sb = split_of_video.get(a, set()), split_of_video.get(b, set())
        if sa and sb and sa != sb:
            problems.append(f"near-duplicate videos {a} and {b} (distance {d}) are in {', '.join(sorted(sa | sb))}")
    return problems


def distribution(clips: list[ClipRecord], labels: tuple[str, ...]) -> dict:
    """Clips and seconds per primary label, per split (and overall), with the imbalance ratio."""
    out = {}
    for split in (*SPLITS, "unassigned", "all"):
        part = [c for c in clips if (split == "all") or (c.split == split) or (split == "unassigned" and not c.split)]
        if not part and split not in ("all",):
            continue
        n = Counter(c.primary_label for c in part)
        secs = Counter()
        for c in part:
            secs[c.primary_label] += c.duration_s
        present = [lab for lab in labels if n.get(lab)]
        trained = [lab for lab in present if lab != UNKNOWN]
        ratio = round(max(n[lab] for lab in trained) / min(n[lab] for lab in trained), 2) if len(trained) > 1 else None
        out[split] = {"clips": len(part), "byLabel": {lab: n.get(lab, 0) for lab in labels},
                      "secondsByLabel": {lab: round(secs.get(lab, 0.0), 1) for lab in labels},
                      "missing": [lab for lab in labels if lab != UNKNOWN and not n.get(lab)], "imbalanceRatio": ratio,
                      "multiLabelClips": sum(1 for c in part if len(c.labels) > 1)}
    return out
