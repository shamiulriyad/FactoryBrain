"""Fixed-duration clips: windows for annotators, and labelled training clips from annotations.

Labelled clips: every annotated segment (per video and subject) is covered by
windows of ``duration_s`` every ``stride_s``. A window's labels are all labels
whose annotations of the same subject cover at least ``min_overlap`` of it;
its primary label is the one covering most. AMBIGUOUS annotations count as
UNKNOWN. A window touching an UNUSABLE segment of the video is dropped.
Windows never extend past the video's end.
"""
from __future__ import annotations

from app.garment_activities.activity_labels import UNKNOWN
from app.garment_activities.config import ClipConfig
from app.garment_activities.schemas import Annotation, ClipRecord, VideoRecord, clip_id_for


def windows(duration_total: float, clip_s: float, stride_s: float, start: float = 0.0, end: float | None = None) -> list[tuple[float, float]]:
    """Clip windows [s, s + clip_s) inside [start, end] (end defaults to the video's duration)."""
    end = duration_total if end is None else min(end, duration_total)
    out = []
    s = start
    while s + clip_s <= end + 1e-6:
        out.append((round(s, 3), round(s + clip_s, 3)))
        s += stride_s
    return out


def _overlap(a0: float, a1: float, b0: float, b1: float) -> float:
    return max(0.0, min(a1, b1) - max(a0, b0))


def subject_key(a: Annotation) -> str:
    if a.subject_group:
        return f"subject:{a.subject_group}"
    if a.subject_roi:
        r = a.subject_roi
        return f"roi:{float(r['x']):.2f},{float(r['y']):.2f}"
    return "subject:only"


def clips_from_annotations(annotations: list[Annotation], videos: dict[str, VideoRecord], cfg: ClipConfig) -> tuple[list[ClipRecord], list[str]]:
    """Labelled clips and notes about what was skipped (and why)."""
    notes: list[str] = []
    clips: dict[str, ClipRecord] = {}
    unusable: dict[str, list[tuple[float, float]]] = {}
    for a in annotations:
        if a.status == "UNUSABLE":
            unusable.setdefault(a.video_id, []).append((a.start_s, a.end_s))
    groups: dict[tuple[str, str], list[Annotation]] = {}
    for a in annotations:
        if a.status != "UNUSABLE":
            groups.setdefault((a.video_id, subject_key(a)), []).append(a)

    for (video_id, subject), anns in sorted(groups.items()):
        video = videos.get(video_id)
        if video is None:
            notes.append(f"{video_id}: not imported, {len(anns)} annotation(s) skipped")
            continue
        for a in anns:
            if a.end_s - a.start_s < cfg.min_segment_s:
                notes.append(f"{a.annotation_id}: shorter than {cfg.min_segment_s} s, skipped")
                continue
            span = windows(video.duration_s, cfg.duration_s, cfg.stride_s, a.start_s, a.end_s)
            if not span and a.end_s - a.start_s >= cfg.duration_s * cfg.min_overlap:
                # A segment shorter than one clip: one clip centred on it, if the video allows.
                mid = (a.start_s + a.end_s) / 2
                s = max(0.0, min(mid - cfg.duration_s / 2, video.duration_s - cfg.duration_s))
                span = [(round(s, 3), round(s + cfg.duration_s, 3))] if s >= 0 and video.duration_s >= cfg.duration_s else []
            for s, e in span:
                if any(_overlap(s, e, u0, u1) > 0 for u0, u1 in unusable.get(video_id, [])):
                    continue
                cover: dict[str, float] = {}
                ids: list[str] = []
                for b in anns:
                    ov = _overlap(s, e, b.start_s, b.end_s)
                    if ov <= 0:
                        continue
                    ids.append(b.annotation_id)
                    for lab in ([UNKNOWN] if b.status == "AMBIGUOUS" else b.labels):
                        cover[lab] = cover.get(lab, 0.0) + ov / (e - s)
                labels = sorted((lab for lab, c in cover.items() if c >= cfg.min_overlap), key=lambda lab: -cover[lab])
                if not labels:
                    continue
                cid = clip_id_for(video_id, s, e - s, subject)
                if cid in clips:
                    continue
                primary = labels[0]
                clips[cid] = ClipRecord(clip_id=cid, video_id=video_id, start_s=s, end_s=e, primary_label=primary, labels=labels,
                                        status="AMBIGUOUS" if primary == UNKNOWN else "LABELLED", annotation_ids=sorted(set(ids)),
                                        camera_id=a.camera_id or video.camera_id, subject_group=a.subject_group,
                                        subject_roi=a.subject_roi, recording_group=video.recording_group)
    return sorted(clips.values(), key=lambda c: (c.video_id, c.start_s, c.clip_id)), notes
