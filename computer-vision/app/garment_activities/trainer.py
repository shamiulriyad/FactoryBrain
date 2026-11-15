"""Trains a garment activity model on extracted clip features.

* Reproducible: Python, NumPy and PyTorch seeds, deterministic data order.
* Class weighting (inverse frequency) for imbalanced data.
* Train and validation loss every epoch; the checkpoint with the lowest
  validation loss is kept; training stops after ``early_stopping_patience``
  epochs without improvement.
* CPU or CUDA (``device: auto``).
* Saves ``model.pt`` and ``metadata.json`` (model name / version, dataset
  version, labels, input and training configuration, validation metrics,
  training date) and ``history.csv``. The evaluation status stays PENDING
  until ``evaluator.py`` scores the model on the held-out test split.

It never trains on fabricated data: the caller passes real extracted clips
(the unit tests use clearly synthetic tensors only to test the mechanics,
and never save a model into models/).
"""
from __future__ import annotations

import csv
import random
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import torch
from torch import nn

from app.activity_recognition.evaluation import report
from app.garment_activities import model_factory
from app.garment_activities.config import FEATURE_VERSION, ClipConfig, DecisionConfig, FeatureConfig, TrainingConfig, as_json
from app.garment_activities.dataset_loader import FeatureSet


class TrainingError(ValueError):
    pass


def seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.use_deterministic_algorithms(True, warn_only=True)


def pick_device(requested: str) -> str:
    if requested == "auto":
        return "cuda" if torch.cuda.is_available() else "cpu"
    if requested.startswith("cuda") and not torch.cuda.is_available():
        return "cpu"
    return requested


def class_weights(y: np.ndarray, n: int) -> torch.Tensor:
    counts = np.bincount(y, minlength=n).astype(np.float32)
    w = np.where(counts > 0, counts.sum() / (n * np.maximum(counts, 1)), 0.0)
    return torch.tensor(w, dtype=torch.float32)


@dataclass
class TrainResult:
    directory: Path
    metadata: dict
    history: list[dict]


def _batches(n: int, size: int, rng: np.random.Generator | None):  # noqa: ANN202
    order = rng.permutation(n) if rng is not None else np.arange(n)
    for i in range(0, n, size):
        yield order[i:i + size]


def _evaluate(model: nn.Module, data: FeatureSet, loss_fn: nn.Module, device: str, batch: int) -> tuple[float, np.ndarray]:
    model.eval()
    total, probs = 0.0, []
    with torch.no_grad():
        for idx in _batches(len(data), batch, None):
            x = torch.from_numpy(data.x[idx]).to(device)
            m = torch.from_numpy(data.mask[idx]).to(device)
            y = torch.from_numpy(data.y[idx]).to(device)
            logits = model(x, m)
            total += float(loss_fn(logits, y)) * len(idx)
            probs.append(torch.softmax(logits, -1).cpu().numpy())
    return total / max(1, len(data)), np.concatenate(probs) if probs else np.zeros((0, len(data.labels)))


def train(train_set: FeatureSet, val_set: FeatureSet, cfg: TrainingConfig, clips: ClipConfig, features: FeatureConfig,
          decision: DecisionConfig, dataset_version: str, out_dir: Path, label_registry_version: str = "1.0",
          data_source: str = "factory-labelled") -> TrainResult:
    if len(train_set) == 0:
        raise TrainingError("No training clips with features: label clips, run 'split' and 'features' first. Nothing was trained.")
    if len(val_set) == 0:
        raise TrainingError("No validation clips: early stopping and model selection need a validation split.")
    labels = train_set.labels
    if len(labels) < 2:
        raise TrainingError(f"Training needs at least 2 classes; found {list(labels)}.")
    if (train_set.y < 0).any():
        raise TrainingError("Some training clips have a label outside the model's classes.")
    val = FeatureSet(val_set.x[val_set.y >= 0], val_set.mask[val_set.y >= 0], val_set.y[val_set.y >= 0], labels,
                     [c for c, y in zip(val_set.clips, val_set.y) if y >= 0])
    seed_everything(cfg.seed)
    if cfg.num_threads:
        torch.set_num_threads(cfg.num_threads)
    device = pick_device(cfg.device)
    model = model_factory.build(cfg.architecture, features.backbone, len(labels), cfg.input_parts, cfg.hidden_size, cfg.dropout).to(device)
    weights = class_weights(train_set.y, len(labels)).to(device) if cfg.class_weighting else None
    loss_fn = nn.CrossEntropyLoss(weight=weights)
    eval_loss = nn.CrossEntropyLoss()
    opt = torch.optim.AdamW(model.parameters(), lr=cfg.learning_rate, weight_decay=cfg.weight_decay)
    rng = np.random.default_rng(cfg.seed)
    history: list[dict] = []
    best = (float("inf"), -1, None)
    stale = 0
    started = time.perf_counter()
    for epoch in range(1, cfg.epochs + 1):
        model.train()
        total = 0.0
        for idx in _batches(len(train_set), cfg.batch_size, rng):
            x = torch.from_numpy(train_set.x[idx]).to(device)
            m = torch.from_numpy(train_set.mask[idx]).to(device)
            y = torch.from_numpy(train_set.y[idx]).to(device)
            opt.zero_grad()
            loss = loss_fn(model(x, m), y)
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            opt.step()
            total += float(loss) * len(idx)
        val_loss, probs = _evaluate(model, val, eval_loss, device, cfg.batch_size)
        pred = probs.argmax(1) if len(probs) else np.zeros(0, int)
        val_acc = float((pred == val.y).mean()) if len(val) else 0.0
        history.append({"epoch": epoch, "train_loss": round(total / len(train_set), 5), "val_loss": round(val_loss, 5), "val_accuracy": round(val_acc, 4)})
        if val_loss < best[0] - 1e-5:
            best = (val_loss, epoch, {k: v.detach().cpu().clone() for k, v in model.state_dict().items()})
            stale = 0
        else:
            stale += 1
            if stale >= cfg.early_stopping_patience:
                break
    model.load_state_dict(best[2])
    val_loss, probs = _evaluate(model, val, eval_loss, device, cfg.batch_size)
    val_report = report([labels[i] for i in val.y], [labels[i] for i in probs.argmax(1)], list(labels)) if len(val) else None
    directory = out_dir / cfg.model_name / cfg.model_version
    meta = {
        "modelName": cfg.model_name, "modelVersion": cfg.model_version, "datasetVersion": dataset_version, "dataSource": data_source,
        "labels": list(labels), "labelRegistryVersion": label_registry_version, "featureVersion": FEATURE_VERSION,
        "build": {"architecture": cfg.architecture, "backbone": features.backbone,
                  "parts": list(model_factory.effective_parts(cfg.architecture, features.backbone, cfg.input_parts)),
                  "hidden": cfg.hidden_size, "dropout": cfg.dropout},
        "input": {"clip": as_json(clips), "features": as_json(features), "frames": clips.frames},
        "training": as_json(cfg) | {"device": device, "torch": torch.__version__},
        "decision": as_json(decision),
        "data": {"trainClips": len(train_set), "validationClips": len(val),
                 "trainByLabel": {lab: int((train_set.y == i).sum()) for i, lab in enumerate(labels)}},
        "validation": {"bestEpoch": best[1], "epochsRun": len(history), "loss": round(val_loss, 5),
                       "accuracy": val_report["accuracy"] if val_report else None,
                       "macroF1": val_report["macro"]["f1"] if val_report else None},
        "evaluation": {"status": "PENDING", "note": "Not yet scored on the held-out test split."},
        "trainedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"), "trainingSeconds": round(time.perf_counter() - started, 1),
    }
    model_factory.save(model, directory, meta)
    with (directory / "history.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(history[0]))
        w.writeheader()
        w.writerows(history)
    return TrainResult(directory, meta, history)
