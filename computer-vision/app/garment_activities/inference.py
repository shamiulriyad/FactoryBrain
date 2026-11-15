"""Runs a trained garment model and decides when its answer is UNKNOWN.

The decision rules are the same offline (evaluation, recorded videos) and
live (camera sessions):

* the person must be visible in ``min_person_ratio`` of the clip's frames
  (else NO_PERSON / INSUFFICIENT_FRAMES), and, for models that use pose, have a
  usable pose in ``min_pose_ratio`` of them (else NO_POSE);
* a model trained with an UNKNOWN class may answer UNKNOWN (MODEL_UNKNOWN);
* the top probability must reach ``min_confidence`` (else LOW_CONFIDENCE)
  and beat the runner-up by ``min_margin`` (else CONFLICTING).

The best guess is kept as ``candidate``; it is never asserted.
"""
from __future__ import annotations

import threading
import time
from dataclasses import replace
from pathlib import Path

import numpy as np
import torch

from app.garment_activities import model_factory
from app.garment_activities.activity_labels import UNKNOWN
from app.garment_activities.config import ClipConfig, DecisionConfig, FeatureConfig
from app.garment_activities.schemas import GarmentPrediction


def decide(probs: np.ndarray, labels: tuple[str, ...], d: DecisionConfig, quality: dict, window: tuple[float, float],
           uses_pose: bool) -> GarmentPrediction:
    order = np.argsort(-probs)
    best, second = int(order[0]), (int(order[1]) if len(order) > 1 else None)
    top = float(probs[best])
    margin = top - (float(probs[second]) if second is not None else 0.0)
    scores = {labels[i]: round(float(probs[i]), 4) for i in order[:5]}
    candidate = labels[best] if labels[best] != UNKNOWN else (labels[second] if second is not None else None)

    def out(label: str, reason: str | None) -> GarmentPrediction:
        return GarmentPrediction(label, round(top, 4), candidate, scores, window[0], window[1], reason, quality | {"margin": round(margin, 4)})

    person, pose = quality.get("personRatio", 1.0), quality.get("poseRatio", 1.0)
    if person == 0:
        return out(UNKNOWN, "NO_PERSON")
    if person < d.min_person_ratio:
        return out(UNKNOWN, "INSUFFICIENT_FRAMES")
    if uses_pose and pose < d.min_pose_ratio:
        return out(UNKNOWN, "NO_POSE")
    if labels[best] == UNKNOWN:
        return out(UNKNOWN, "MODEL_UNKNOWN")
    if top < d.min_confidence:
        return out(UNKNOWN, "LOW_CONFIDENCE")
    if margin < d.min_margin:
        return out(UNKNOWN, "CONFLICTING")
    return out(labels[best], None)


class GarmentClassifier:
    """A loaded model with its metadata; thread-safe, shared by every camera that uses it."""

    def __init__(self, model: torch.nn.Module, meta: dict, device: str = "cpu", decision: DecisionConfig | None = None,
                 directory: Path | None = None) -> None:
        self.model, self.meta, self.device = model, meta, device
        self.directory = directory
        self.labels: tuple[str, ...] = tuple(meta["labels"])
        self.name, self.version = meta["modelName"], meta["modelVersion"]
        self.dataset_version = meta.get("datasetVersion")
        inp = meta["input"]
        self.clip = ClipConfig(**inp["clip"])
        self.features = FeatureConfig(**inp["features"])
        self.decision = decision or DecisionConfig(**meta.get("decision", {}))
        self.uses_pose = "pose" in meta["build"]["parts"]
        self.evaluation = meta.get("evaluation") or {"status": "PENDING"}
        self._lock = threading.Lock()
        self.calls, self.avg_ms, self.errors = 0, 0.0, 0

    @property
    def frames(self) -> int:
        return self.clip.frames

    def summary(self) -> dict:
        e = self.evaluation
        return {"name": self.name, "version": self.version, "datasetVersion": self.dataset_version, "architecture": self.meta["build"]["architecture"],
                "backbone": self.features.backbone, "labels": list(self.labels), "evaluation": {k: e.get(k) for k in ("status", "split", "macroF1", "accuracy",
                                                                                                                    "evaluatedAt", "dataSource") if k in e}}

    def probabilities(self, x: np.ndarray, mask: np.ndarray) -> np.ndarray:
        started = time.perf_counter()
        with self._lock, torch.no_grad():
            logits = self.model(torch.from_numpy(np.ascontiguousarray(x, np.float32)).to(self.device),
                                torch.from_numpy(np.ascontiguousarray(mask, np.float32)).to(self.device))
            probs = torch.softmax(logits, -1).cpu().numpy()
        self.calls += 1
        self.avg_ms = round(self.avg_ms + ((time.perf_counter() - started) * 1000 - self.avg_ms) / min(self.calls, 50), 2)
        return probs

    def predict(self, x: np.ndarray, mask: np.ndarray, windows: list[tuple[float, float]], qualities: list[dict]) -> list[GarmentPrediction]:
        if len(x) == 0:
            return []
        probs = self.probabilities(x, mask)
        return [decide(p, self.labels, self.decision, q, w, self.uses_pose) for p, q, w in zip(probs, qualities, windows)]


def load_classifier(directory: Path, device: str = "cpu", allow_unevaluated: bool = False,
                    decision: DecisionConfig | None = None) -> GarmentClassifier:
    model, meta = model_factory.load(Path(directory), device, allow_unevaluated)
    if decision is not None:
        decision = replace(decision)
    return GarmentClassifier(model, meta, device, decision, Path(directory))
