"""Temporal activity classification over a track's recent observations.

:class:`BaselineTemporalClassifier` (``pose-temporal-baseline`` 1.0) is a
transparent, hand-written rule set over pose features and movement. It is
not trained and has not been evaluated on factory footage: treat its output
as a first approximation (see docs/ACTIVITY_RECOGNITION.md).

* Posture (STANDING, SITTING, BENDING, REACHING) is scored per frame from pose
  features and averaged over the window, weighted by keypoint confidence.
* Movement (WALKING) comes from the track's position over time: the speed of
  its feet point in body heights per second, using timestamps.
* The result is UNKNOWN unless the evidence is good enough: enough usable
  poses over a long enough span, a top score above the confidence threshold,
  a clear margin over the runner-up, and frames that mostly agree.

:class:`SequenceModelClassifier` is the interface for a trained temporal model
(LSTM / GRU / temporal CNN exported to TorchScript). No such model is shipped
or trained in this phase.
"""
from __future__ import annotations

import logging
import math
from pathlib import Path
from typing import Protocol

import numpy as np

from app.activity_recognition.activity_config import BASELINE_MODEL_NAME, BASELINE_MODEL_VERSION, ActivityConfig
from app.activity_recognition.feature_extractor import knee_angle
from app.activity_recognition.schemas import (
    FEATURE_NAMES, ActivityLabel, ActivityPrediction, FrameFeatures, PoseObservation, UncertainReason, WindowQuality,
)

log = logging.getLogger(__name__)

POSTURES = (ActivityLabel.STANDING, ActivityLabel.SITTING, ActivityLabel.BENDING, ActivityLabel.REACHING)
LABELS = (ActivityLabel.STANDING, ActivityLabel.SITTING, ActivityLabel.WALKING, ActivityLabel.BENDING, ActivityLabel.REACHING)


class ActivityClassifier(Protocol):
    name: str
    version: str

    def classify(self, window: list[PoseObservation]) -> ActivityPrediction: ...


def ramp(x: float | None, lo: float, hi: float) -> float:
    """0 at or below ``lo``, 1 at or above ``hi``, linear between (``lo > hi`` ramps downwards)."""
    if x is None or math.isnan(x):
        return 0.0
    if lo == hi:
        return 1.0 if x >= hi else 0.0
    t = (x - lo) / (hi - lo)
    return max(0.0, min(1.0, t))


def posture_scores(f: FrameFeatures) -> dict[ActivityLabel, float] | None:
    """Per-frame posture evidence, 0–1 each. None when the torso isn't measurable."""
    if f.torso_angle is None:
        return None
    upright = 1.0 - ramp(f.torso_angle, 20, 40)
    bent = max(ramp(f.torso_angle, 30, 55), ramp(f.head_offset, -0.15, 0.10) * ramp(f.torso_angle, 15, 30))

    knee = knee_angle(f)
    legs_visible = f.thigh_angle is not None
    if legs_visible:
        thigh_flat = ramp(f.thigh_angle, 35, 65)                 # thigh near horizontal: seated, seen from the side
        thigh_short = ramp(f.thigh_ratio, 0.65, 0.40)            # thigh foreshortened: seated, facing the camera
        knee_bent = ramp(knee, 150, 115) if knee is not None else None
        seated = max(0.6 * max(thigh_flat, thigh_short) + 0.4 * knee_bent if knee_bent is not None else 0.85 * max(thigh_flat, thigh_short),
                     0.75 * thigh_short)
        straight = ramp(knee, 145, 165) if knee is not None else 0.7 * (1.0 - max(thigh_flat, thigh_short))
    else:
        # Legs hidden (desk, machine, frame edge): standing and sitting can't be told apart well.
        seated, straight = 0.0, 0.0

    raised = ramp(f.wrist_raise, 0.0, 0.25)
    forward = ramp(f.wrist_reach, 0.6, 0.9) if f.wrist_above_hip else 0.0
    reach = max(raised, forward)

    standing = upright * (1.0 - seated) * (straight if legs_visible else 0.45)
    sitting = (1.0 - bent) * seated
    return {
        ActivityLabel.STANDING: standing * (1.0 - reach),
        ActivityLabel.SITTING: sitting * (1.0 - 0.8 * reach),
        ActivityLabel.BENDING: bent,
        ActivityLabel.REACHING: reach * (1.0 - bent),
    }


def window_speed(window: list[PoseObservation]) -> float | None:
    """Net speed of the feet point over the window, in body heights per second (None: under 0.5 s of history)."""
    if len(window) < 2:
        return None
    first, last = window[0], window[-1]
    dt = last.timestamp - first.timestamp
    if dt < 0.5:
        return None
    (x0, y0), (x1, y1) = first.anchor, last.anchor
    height = float(np.median([o.height for o in window]))
    return math.hypot(x1 - x0, y1 - y0) / dt / height


def decide(scores: dict[ActivityLabel, float], frame_votes: list[ActivityLabel], quality_base: dict, cfg: ActivityConfig,
           window: list[PoseObservation], usable: int) -> ActivityPrediction:
    """Shared thresholding: turn raw scores into a prediction or UNKNOWN (with the reason)."""
    start, end = window[0].timestamp, window[-1].timestamp
    ranked = sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
    best, best_score = ranked[0]
    second = ranked[1][1] if len(ranked) > 1 else 0.0
    agree = sum(1 for v in frame_votes if v == best) / len(frame_votes) if frame_votes else 0.0
    pose_ratio = usable / max(1, len(window))
    confidence = best_score * (0.5 + 0.5 * agree) * (0.6 + 0.4 * pose_ratio)
    confidence = round(max(0.0, min(1.0, confidence)), 3)
    quality = WindowQuality(consistency=agree, **quality_base)
    rounded = {k.value: round(v, 3) for k, v in scores.items()}

    def unknown(reason: UncertainReason) -> ActivityPrediction:
        return ActivityPrediction(ActivityLabel.UNKNOWN, confidence, best, rounded, quality, start, end, reason)

    if confidence < cfg.min_confidence:
        return unknown(UncertainReason.LOW_CONFIDENCE)
    if best_score - second < cfg.conflict_margin or agree < 0.5:
        return unknown(UncertainReason.CONFLICTING)
    return ActivityPrediction(best, confidence, best, rounded, quality, start, end)


def insufficient(window: list[PoseObservation], cfg: ActivityConfig) -> ActivityPrediction | None:
    """UNKNOWN when there's too little to go on (missing pose data never becomes a prediction)."""
    if not window:
        raise ValueError("empty window")
    usable = [o for o in window if o.has_pose]
    span = window[-1].timestamp - window[0].timestamp
    reason = None
    if not usable:
        reason = UncertainReason.NO_POSE
    elif span < cfg.min_window_seconds:
        reason = UncertainReason.INSUFFICIENT_HISTORY
    elif len(usable) < cfg.min_observations:
        reason = UncertainReason.INSUFFICIENT_POSE
    if reason is None:
        return None
    q = WindowQuality(len(window), len(usable), span, _mean_conf(usable), _legs_ratio(usable), window_speed(window), 0.0)
    return ActivityPrediction(ActivityLabel.UNKNOWN, 0.0, None, {}, q, window[0].timestamp, window[-1].timestamp, reason)


def _mean_conf(usable: list[PoseObservation]) -> float:
    return float(np.mean([o.features.keypoint_confidence for o in usable])) if usable else 0.0


def _legs_ratio(usable: list[PoseObservation]) -> float:
    return sum(1 for o in usable if o.quality is not None and o.quality.has_legs) / len(usable) if usable else 0.0


class BaselineTemporalClassifier:
    name = BASELINE_MODEL_NAME
    version = BASELINE_MODEL_VERSION

    def __init__(self, cfg: ActivityConfig) -> None:
        self.cfg = cfg

    def classify(self, window: list[PoseObservation]) -> ActivityPrediction:
        early = insufficient(window, self.cfg)
        if early is not None:
            return early
        usable = [o for o in window if o.has_pose]
        per_frame: list[tuple[dict[ActivityLabel, float], float]] = []
        for o in usable:
            s = posture_scores(o.features)
            if s is not None:
                per_frame.append((s, max(0.05, o.features.keypoint_confidence)))
        speed = window_speed(window)
        base = dict(observations=len(window), pose_observations=len(usable), span_seconds=window[-1].timestamp - window[0].timestamp,
                    mean_keypoint_confidence=_mean_conf(usable), legs_visible_ratio=_legs_ratio(usable), speed=speed)
        if len(per_frame) < self.cfg.min_observations:
            q = WindowQuality(consistency=0.0, **base)
            return ActivityPrediction(ActivityLabel.UNKNOWN, 0.0, None, {}, q, window[0].timestamp, window[-1].timestamp,
                                      UncertainReason.INSUFFICIENT_POSE)
        total_w = sum(w for _, w in per_frame)
        avg = {lab: sum(s[lab] * w for s, w in per_frame) / total_w for lab in POSTURES}
        # Walking: sustained movement of an upright, not-seated body. It replaces standing (people walk upright).
        upright_share = avg[ActivityLabel.STANDING] + avg[ActivityLabel.REACHING]
        moving = ramp(speed, self.cfg.walking_speed * 0.6, self.cfg.walking_speed * 1.4) if speed is not None else 0.0
        walking = moving * min(1.0, upright_share + 0.3) * (1.0 - avg[ActivityLabel.SITTING]) * (1.0 - 0.7 * avg[ActivityLabel.BENDING])
        scores = {
            ActivityLabel.STANDING: avg[ActivityLabel.STANDING] * (1.0 - moving),
            ActivityLabel.SITTING: avg[ActivityLabel.SITTING],
            ActivityLabel.WALKING: walking,
            ActivityLabel.BENDING: avg[ActivityLabel.BENDING],
            ActivityLabel.REACHING: avg[ActivityLabel.REACHING] * (1.0 - 0.5 * moving),
        }
        votes = []
        for s, _ in per_frame:
            frame_best = max(POSTURES, key=lambda lab: s[lab])
            if moving >= 0.5 and frame_best == ActivityLabel.STANDING:
                frame_best = ActivityLabel.WALKING          # an upright frame of a moving body agrees with walking
            votes.append(frame_best)
        return decide(scores, votes, base, self.cfg, window, len(usable))


class SequenceModelClassifier:
    """A trained temporal model behind the same interface (not shipped: no model has been trained yet).

    Expects a TorchScript module taking ``(1, T, 2F)`` float input (the
    FEATURE_NAMES vector of each observation, NaN replaced by 0, followed by a
    0/1 "present" mask) and returning ``(1, len(labels))`` logits. LSTM, GRU
    and temporal-CNN models can all be exported this way. The labels, model
    name and version come from the file's ``extra_files`` metadata
    (``labels.txt``, ``meta.txt``) when present.
    """

    def __init__(self, cfg: ActivityConfig, model_path: Path, device: str = "cpu", labels: tuple[ActivityLabel, ...] = LABELS) -> None:
        import torch

        if not Path(model_path).is_file():
            raise FileNotFoundError(f"Sequence model not found at {model_path}. No trained activity model ships with this service.")
        extra = {"labels.txt": "", "meta.txt": ""}
        self._model = torch.jit.load(str(model_path), map_location=device, _extra_files=extra)
        self._model.eval()
        self._torch = torch
        self._device = device
        self.cfg = cfg
        names = [s.strip() for s in (extra["labels.txt"].decode() if isinstance(extra["labels.txt"], bytes) else extra["labels.txt"]).split(",") if s.strip()]
        self.labels = tuple(ActivityLabel(n) for n in names) if names else labels
        meta = extra["meta.txt"].decode() if isinstance(extra["meta.txt"], bytes) else extra["meta.txt"]
        parts = dict(p.split("=", 1) for p in meta.split(";") if "=" in p)
        self.name = parts.get("name", Path(model_path).stem)[:80]
        self.version = parts.get("version", "untracked")[:40]

    @staticmethod
    def sequence(window: list[PoseObservation]) -> np.ndarray:
        rows = []
        for o in window:
            v = o.features.as_vector() if o.features is not None else np.full(len(FEATURE_NAMES), np.nan, np.float32)
            present = (~np.isnan(v)).astype(np.float32)
            rows.append(np.concatenate([np.nan_to_num(v, nan=0.0), present]))
        return np.stack(rows)[None, ...]

    def classify(self, window: list[PoseObservation]) -> ActivityPrediction:
        early = insufficient(window, self.cfg)
        if early is not None:
            return early
        usable = [o for o in window if o.has_pose]
        x = self._torch.from_numpy(self.sequence(window)).to(self._device)
        with self._torch.no_grad():
            probs = self._torch.softmax(self._model(x), dim=-1)[0].cpu().numpy()
        scores = {lab: float(p) for lab, p in zip(self.labels, probs)}
        base = dict(observations=len(window), pose_observations=len(usable), span_seconds=window[-1].timestamp - window[0].timestamp,
                    mean_keypoint_confidence=_mean_conf(usable), legs_visible_ratio=_legs_ratio(usable), speed=window_speed(window))
        best = max(scores, key=scores.get)
        return decide(scores, [best] * len(usable), base, self.cfg, window, len(usable))


def build_classifier(cfg: ActivityConfig, device: str = "cpu") -> ActivityClassifier:
    """The configured classifier; a missing sequence model falls back to the baseline with a warning."""
    if cfg.classifier == "sequence":
        try:
            if cfg.sequence_model_path is None:
                raise FileNotFoundError("CV_ACTIVITY_SEQUENCE_MODEL_PATH is not set.")
            return SequenceModelClassifier(cfg, cfg.sequence_model_path, device)
        except Exception as exc:  # noqa: BLE001
            log.warning("Sequence activity model unavailable (%s): using the baseline classifier.", exc)
    return BaselineTemporalClassifier(cfg)
