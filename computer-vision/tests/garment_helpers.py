"""Test doubles for garment activity tests.

``synthetic_set`` builds CLEARLY SYNTHETIC feature tensors (random noise with
a class-dependent offset) to test training mechanics only: they are not video,
not garment data, and no model trained on them is ever saved outside a
temporary test folder or reported anywhere.
"""
from __future__ import annotations

import numpy as np

from app.garment_activities.config import ClipConfig, DecisionConfig, FeatureConfig
from app.garment_activities.dataset_loader import FeatureSet
from app.garment_activities.feature_extractor import feature_dim, layout
from app.garment_activities.inference import decide
from app.garment_activities.schemas import ClipRecord, GarmentPrediction


def synthetic_set(labels: tuple[str, ...], n_per_class: int, frames: int = 8, backbone: str = "none", seed: int = 0,
                  camera: str = "CAM-T") -> FeatureSet:
    rng = np.random.default_rng(seed)
    d = feature_dim(backbone)
    pres = layout(backbone)["presence"][0]
    xs, ys, clips = [], [], []
    for ci, lab in enumerate(labels):
        for k in range(n_per_class):
            x = rng.normal(0, 1, (frames, d)).astype(np.float32)
            x[:, ci % (d - 1)] += 3.0                     # a separable signal so the mechanics can be checked
            x[:, pres] = 1.0
            xs.append(x)
            ys.append(ci)
            clips.append(ClipRecord(f"syn-{lab}-{k}", f"syn-v-{ci}-{k}", 0.0, 4.0, lab, [lab], "LABELLED", [], camera_id=camera))
    x = np.stack(xs)
    return FeatureSet(x, x[:, :, pres].copy(), np.array(ys, np.int64), labels, clips)


class FakeEmbedder:
    """A deterministic stand-in for the CNN: mean colour per channel, tiled to ``dim``."""

    def __init__(self, dim: int = 576, name: str = "mobilenet_v3_small") -> None:
        self.dim, self.name, self.calls = dim, name, 0

    def embed(self, crops):  # noqa: ANN001, ANN201
        self.calls += 1
        if not crops:
            return np.zeros((0, self.dim), np.float32)
        means = np.stack([c.reshape(-1, 3).mean(0) / 255.0 for c in crops]).astype(np.float32)
        return np.tile(means, (1, self.dim // 3 + 1))[:, :self.dim]


class FakeClassifier:
    """A stand-in GarmentClassifier: ``rule(x, mask) -> probabilities``; the real UNKNOWN rules (decide) apply."""

    def __init__(self, labels=("SEWING", "FABRIC_HANDLING", "SITTING"), rule=None, sample_fps: float = 4.0, duration: float = 2.0,  # noqa: ANN001
                 backbone: str = "none", fail: bool = False, name: str = "garment-test", version: str = "0.0-test", evaluated: bool = True) -> None:
        self.labels = tuple(labels)
        self.rule = rule or (lambda x, m: np.tile(np.array([0.9] + [0.1 / (len(labels) - 1)] * (len(labels) - 1)), (len(x), 1)))
        self.clip = ClipConfig(duration_s=duration, stride_s=duration, sample_fps=sample_fps)
        self.features = FeatureConfig(backbone=backbone)
        self.decision = DecisionConfig()
        self.uses_pose = True
        self.fail = fail
        self.name, self.version, self.dataset_version = name, version, "ds-test"
        self.evaluation = {"status": "EVALUATED" if evaluated else "PENDING", "macroF1": 0.5}
        self.calls, self.avg_ms = 0, 0.0
        self.meta = {"build": {"architecture": "fake", "parts": ["pose", "motion"]}}

    @property
    def frames(self) -> int:
        return self.clip.frames

    def summary(self) -> dict:
        return {"name": self.name, "version": self.version, "datasetVersion": self.dataset_version, "architecture": "fake",
                "backbone": self.features.backbone, "labels": list(self.labels), "evaluation": self.evaluation}

    def predict(self, x, mask, windows, qualities) -> list[GarmentPrediction]:  # noqa: ANN001
        self.calls += 1
        if self.fail:
            raise RuntimeError("model crashed")
        probs = self.rule(x, mask)
        return [decide(p, self.labels, self.decision, q, w, self.uses_pose) for p, q, w in zip(probs, qualities, windows)]
