"""Leakage-safe, reproducible train / validation / test splits.

Clips are never split one by one. Videos are joined into groups when they
share a recording group, a subject group (the same person) or are
near-duplicates of each other, and whole groups are assigned to splits by a
seeded hash, so the same seed always gives the same split. Optionally whole
cameras are held out as the test set, to measure generalisation to an angle
the model has never seen. The split and how it was made are written to
``splits.json``, and the dataset version is a hash of the clips, labels and
split.
"""
from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from datetime import datetime, timezone

from app.garment_activities.schemas import ClipRecord


@dataclass(frozen=True)
class SplitPlan:
    train: float = 0.7
    validation: float = 0.15
    test: float = 0.15
    seed: int = 13
    holdout_cameras: tuple[str, ...] = ()       # every clip of these cameras goes to test


def _union_find(clips: list[ClipRecord], near_dupes) -> dict[str, str]:  # noqa: ANN001
    parent: dict[str, str] = {}

    def find(x: str) -> str:
        parent.setdefault(x, x)
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a: str, b: str) -> None:
        parent[find(a)] = find(b)

    for c in clips:
        v = f"video:{c.video_id}"
        find(v)
        if c.recording_group:
            union(v, f"rec:{c.recording_group}")
        if c.subject_group:
            union(v, f"subj:{c.subject_group}")
    for a, b, _ in near_dupes:
        union(f"video:{a}", f"video:{b}")
    return {c.video_id: find(f"video:{c.video_id}") for c in clips}


def assign(clips: list[ClipRecord], plan: SplitPlan = SplitPlan(), near_dupes=()) -> dict:  # noqa: ANN001, B008
    """Sets ``clip.split`` on every clip; returns the split metadata."""
    group_of = _union_find(clips, near_dupes)
    held = {c.lower() for c in plan.holdout_cameras}
    split_of_group: dict[str, str] = {}
    for g in sorted(set(group_of.values())):
        cams = {(c.camera_id or "").lower() for c in clips if group_of[c.video_id] == g}
        if held and cams & held:
            split_of_group[g] = "test"
            continue
        h = int(hashlib.sha256(f"{plan.seed}:{g}".encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
        if held:   # cameras are the test set; the rest is train / validation
            split_of_group[g] = "train" if h < plan.train / (plan.train + plan.validation) else "validation"
        else:
            split_of_group[g] = "train" if h < plan.train else "validation" if h < plan.train + plan.validation else "test"
    for c in clips:
        c.split = split_of_group[group_of[c.video_id]]
    groups = {}
    for vid, g in group_of.items():
        groups.setdefault(g, set()).add(vid)
    return {
        "createdAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "method": "group" + ("+holdout-cameras" if held else ""),
        "seed": plan.seed, "ratios": {"train": plan.train, "validation": plan.validation, "test": plan.test},
        "holdoutCameras": sorted(held), "groups": {g: {"videos": sorted(v), "split": split_of_group[g]} for g, v in sorted(groups.items())},
        "videos": {vid: split_of_group[g] for vid, g in sorted(group_of.items())},
        "counts": {s: sum(1 for c in clips if c.split == s) for s in ("train", "validation", "test")},
        "datasetVersion": dataset_version(clips),
    }


def dataset_version(clips: list[ClipRecord]) -> str:
    """A short hash of every clip's id, labels and split: changes whenever the training data changes."""
    rows = sorted((c.clip_id, "|".join(c.labels), c.split or "") for c in clips)
    return "ds-" + hashlib.sha256(json.dumps(rows).encode()).hexdigest()[:10]
