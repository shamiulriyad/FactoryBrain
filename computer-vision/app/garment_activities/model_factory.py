"""Garment activity models: one interface, interchangeable temporal heads, checkpoints with metadata.

Every model takes a clip as ``x`` (B, T, D) per-frame features and ``mask``
(B, T) (1 where the person was seen) and returns class logits (B, C). The
camera pipeline only ever sees this interface (via inference.py), so a new
architecture is one entry in ``ARCHITECTURES``.

Implemented:

* ``gru``      — the baseline: frozen-CNN crop embeddings + pose + motion,
  projected, then a GRU over time (a "CNN + GRU" with the CNN frozen and its
  features cached, which trains in minutes on a CPU from small datasets);
* ``tcn``      — the same inputs through dilated temporal convolutions;
* ``pose_gru`` — pose and motion only (no image), as a cheaper ablation.

Not implemented on purpose (hardware): end-to-end video networks (SlowFast,
X3D, video transformers) need a GPU and far more labelled data; they would
plug in here as models that take raw clips (see docs/GARMENT_ACTIVITIES.md).
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import torch
from torch import nn

from app.garment_activities.config import FEATURE_VERSION
from app.garment_activities.feature_extractor import layout

CHECKPOINT_FILE = "model.pt"
METADATA_FILE = "metadata.json"


def _select(parts: tuple[str, ...], backbone: str) -> list[int]:
    lay = layout(backbone)
    idx: list[int] = []
    for p in parts:
        if p not in lay:
            raise ValueError(f"Unknown input part '{p}' ({', '.join(lay)}).")
        idx.extend(range(*lay[p]))
    idx.extend(range(*lay["presence"]))
    return idx


class _Head(nn.Module):
    def __init__(self, feature_index: list[int], num_classes: int, hidden: int, dropout: float) -> None:
        super().__init__()
        self.register_buffer("index", torch.tensor(feature_index, dtype=torch.long), persistent=False)
        self.input_dim = len(feature_index)
        self.norm = nn.LayerNorm(self.input_dim)
        self.proj = nn.Sequential(nn.Linear(self.input_dim, hidden), nn.ReLU(), nn.Dropout(dropout))
        self.classify = nn.Sequential(nn.Dropout(dropout), nn.Linear(hidden, num_classes))

    def _inputs(self, x: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        return self.proj(self.norm(x.index_select(-1, self.index))) * mask.unsqueeze(-1)

    @staticmethod
    def _pool(h: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        w = mask.unsqueeze(-1)
        return (h * w).sum(1) / w.sum(1).clamp(min=1.0)


class GruHead(_Head):
    def __init__(self, feature_index: list[int], num_classes: int, hidden: int = 128, dropout: float = 0.3) -> None:
        super().__init__(feature_index, num_classes, hidden, dropout)
        self.gru = nn.GRU(hidden, hidden, batch_first=True)

    def forward(self, x: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        h, _ = self.gru(self._inputs(x, mask))
        return self.classify(self._pool(h, mask))


class TcnHead(_Head):
    def __init__(self, feature_index: list[int], num_classes: int, hidden: int = 128, dropout: float = 0.3) -> None:
        super().__init__(feature_index, num_classes, hidden, dropout)
        self.convs = nn.ModuleList([nn.Conv1d(hidden, hidden, 3, padding=d, dilation=d) for d in (1, 2, 4)])

    def forward(self, x: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
        h = self._inputs(x, mask).transpose(1, 2)
        for conv in self.convs:
            h = h + torch.relu(conv(h))
        return self.classify(self._pool(h.transpose(1, 2), mask))


@dataclass(frozen=True)
class Architecture:
    cls: type
    default_parts: tuple[str, ...]
    description: str


ARCHITECTURES: dict[str, Architecture] = {
    "gru": Architecture(GruHead, ("appearance", "pose", "motion"), "Frozen CNN crop embedding + pose + motion → GRU (baseline)"),
    "tcn": Architecture(TcnHead, ("appearance", "pose", "motion"), "Frozen CNN crop embedding + pose + motion → dilated temporal CNN"),
    "pose_gru": Architecture(GruHead, ("pose", "motion"), "Pose + motion only → GRU (no image features)"),
}


def build(architecture: str, backbone: str, num_classes: int, parts: tuple[str, ...] | None = None, hidden: int = 128,
          dropout: float = 0.3) -> nn.Module:
    if architecture not in ARCHITECTURES:
        raise ValueError(f"Unknown architecture '{architecture}' ({', '.join(ARCHITECTURES)}).")
    if num_classes < 2:
        raise ValueError("A classifier needs at least 2 classes.")
    return ARCHITECTURES[architecture].cls(_select(effective_parts(architecture, backbone, parts), backbone), num_classes, hidden, dropout)


def effective_parts(architecture: str, backbone: str, parts: tuple[str, ...] | None) -> tuple[str, ...]:
    """The input parts a model really uses (pose_gru and the "none" backbone drop appearance)."""
    if architecture not in ARCHITECTURES:
        raise ValueError(f"Unknown architecture '{architecture}' ({', '.join(ARCHITECTURES)}).")
    parts = tuple(parts) if parts else ARCHITECTURES[architecture].default_parts
    if architecture == "pose_gru" or layout(backbone)["appearance"][1] == 0:
        parts = tuple(p for p in parts if p != "appearance")
    if not parts:
        raise ValueError("A model needs at least one input part.")
    return parts


def save(model: nn.Module, directory: Path, metadata: dict) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    torch.save({"state_dict": model.state_dict(), "build": metadata["build"]}, directory / CHECKPOINT_FILE)
    (directory / METADATA_FILE).write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    return directory


class ModelLoadError(RuntimeError):
    pass


def load(directory: Path, device: str = "cpu", allow_unevaluated: bool = False) -> tuple[nn.Module, dict]:
    """Load a checkpoint directory; refuses missing files, another feature layout, or (unless allowed) an unevaluated model."""
    directory = Path(directory)
    meta_path, ckpt_path = directory / METADATA_FILE, directory / CHECKPOINT_FILE
    if not meta_path.is_file() or not ckpt_path.is_file():
        raise ModelLoadError(f"No garment activity model at {directory} (needs {CHECKPOINT_FILE} and {METADATA_FILE}).")
    try:
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise ModelLoadError(f"{meta_path}: invalid JSON ({exc.msg}).") from exc
    if meta.get("featureVersion") != FEATURE_VERSION:
        raise ModelLoadError(f"{directory.name}: trained on feature layout {meta.get('featureVersion')!r}, this service extracts "
                             f"{FEATURE_VERSION!r}. Re-extract features and retrain.")
    status = (meta.get("evaluation") or {}).get("status")
    if status != "EVALUATED" and not allow_unevaluated:
        raise ModelLoadError(f"{meta.get('modelName')} {meta.get('modelVersion')} has no held-out test evaluation (status {status!r}). "
                             "Evaluate it first (scripts/garment.py evaluate), or set allow_unevaluated for development.")
    b = meta["build"]
    try:
        model = build(b["architecture"], b["backbone"], len(meta["labels"]), tuple(b["parts"]), b["hidden"], b["dropout"])
        state = torch.load(ckpt_path, map_location=device, weights_only=True)
        model.load_state_dict(state["state_dict"])
    except Exception as exc:  # noqa: BLE001 - any mismatch is a load failure, reported clearly
        raise ModelLoadError(f"{directory}: checkpoint doesn't match its metadata ({type(exc).__name__}: {exc}).") from exc
    return model.to(device).eval(), meta
