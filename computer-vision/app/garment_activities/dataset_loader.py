"""Loads annotations and extracted clip features into arrays for training and evaluation.

Features are cached per clip in ``processed/features/<clip id>.npz``
(``x``: (T, D) float32, plus the extraction quality), written by
``scripts/garment.py features``. A split is loaded as X (N, T, D), the
presence mask (N, T), integer labels (N,) and the clip records.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from app.garment_activities.activity_labels import UNKNOWN, LabelRegistry
from app.garment_activities.config import DatasetPaths
from app.garment_activities.dataset_validator import ValidationResult, validate_annotations
from app.garment_activities.feature_extractor import layout
from app.garment_activities.schemas import ClipRecord, load_clips, load_videos, read_annotation_rows


@dataclass
class FeatureSet:
    x: np.ndarray            # (N, T, D)
    mask: np.ndarray         # (N, T) 1 = person present
    y: np.ndarray            # (N,) index into labels; -1 = not one of the model's classes
    labels: tuple[str, ...]
    clips: list[ClipRecord]

    def __len__(self) -> int:
        return len(self.clips)


def load_annotations(paths: DatasetPaths, registry: LabelRegistry) -> ValidationResult:
    return validate_annotations(read_annotation_rows(paths.annotations()), load_videos(paths.videos_manifest), registry)


def save_features(paths: DatasetPaths, clip: ClipRecord, x: np.ndarray, quality: dict) -> str:
    paths.features.mkdir(parents=True, exist_ok=True)
    dest = paths.features / f"{clip.clip_id}.npz"
    np.savez_compressed(dest, x=x.astype(np.float32), quality=json.dumps(quality))
    return dest.relative_to(paths.root).as_posix()


def load_split(paths: DatasetPaths, split: str, labels: tuple[str, ...] | None, backbone: str, include_unknown: bool = False,
               frames: int | None = None) -> FeatureSet:
    """Clips of ``split`` with extracted features. ``labels`` None = the classes found in this split (registry order is kept by the caller)."""
    lay = layout(backbone)
    dim = lay["presence"][1]
    clips = [c for c in load_clips(paths.clips_manifest) if c.split == split and c.features]
    if not include_unknown:
        clips = [c for c in clips if c.primary_label != UNKNOWN]
    xs, kept = [], []
    for c in clips:
        f = paths.root / c.features
        if not f.is_file():
            continue
        x = np.load(f)["x"]
        if x.ndim != 2 or x.shape[1] != dim:
            raise ValueError(f"{c.clip_id}: features have shape {x.shape}, expected (T, {dim}) for backbone {backbone}. Re-extract features.")
        if frames is not None and x.shape[0] != frames:
            raise ValueError(f"{c.clip_id}: {x.shape[0]} frames, expected {frames}. Re-extract features with the same clip settings.")
        xs.append(x)
        kept.append(c)
    if labels is None:
        labels = tuple(sorted({c.primary_label for c in kept}))
    index = {lab: i for i, lab in enumerate(labels)}
    if not kept:
        t = frames or 1
        return FeatureSet(np.zeros((0, t, dim), np.float32), np.zeros((0, t), np.float32), np.zeros((0,), np.int64), labels, [])
    x = np.stack(xs).astype(np.float32)
    mask = x[:, :, lay["presence"][0]].copy()
    y = np.array([index.get(c.primary_label, -1) for c in kept], np.int64)
    return FeatureSet(x, mask, y, labels, kept)


def ordered_labels(found: set[str], registry: LabelRegistry) -> tuple[str, ...]:
    return tuple(lab for lab in registry.names if lab in found)


def write_split_lists(paths: DatasetPaths, clips: list[ClipRecord]) -> None:
    """train/ validation/ test/ each get clips.jsonl: the clips (ids, labels, feature files) of that split."""
    for split in ("train", "validation", "test"):
        d = paths.split_dir(split)
        d.mkdir(parents=True, exist_ok=True)
        rows = [{"clip_id": c.clip_id, "video_id": c.video_id, "primary_label": c.primary_label, "labels": c.labels, "features": c.features}
                for c in clips if c.split == split]
        (d / "clips.jsonl").write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")


def feature_path(paths: DatasetPaths, clip: ClipRecord) -> Path | None:
    return paths.root / clip.features if clip.features else None
