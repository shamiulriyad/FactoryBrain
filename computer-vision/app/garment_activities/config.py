"""Configuration of the garment activity pipeline: dataset layout, clips, features, training and live inference.

Every part reads plain dataclasses that can be built from JSON files
(``configs/garment_training.example.json``, ``configs/garment_inference.example.json``)
so experiments are reproducible from the config alone.
"""
from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field, fields, replace
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DATASET = ROOT / "data" / "garment_activity"
MODELS_DIR = ROOT / "models" / "garment_activity"

#: Bumped whenever the per-frame feature layout changes; a model trained on another layout is refused.
FEATURE_VERSION = "garment-features-1"


def _from(cls, data: dict | None):  # noqa: ANN001, ANN202
    data = data or {}
    known = {f.name for f in fields(cls)}
    unknown = set(data) - known
    if unknown:
        raise ValueError(f"{cls.__name__}: unknown setting(s) {', '.join(sorted(unknown))}")
    return cls(**data)


@dataclass(frozen=True)
class DatasetPaths:
    root: Path = DEFAULT_DATASET

    @property
    def raw(self) -> Path: return self.root / "raw"
    @property
    def annotated(self) -> Path: return self.root / "annotated"
    @property
    def processed(self) -> Path: return self.root / "processed"
    @property
    def features(self) -> Path: return self.processed / "features"
    @property
    def reports(self) -> Path: return self.root / "reports"
    @property
    def videos_manifest(self) -> Path: return self.root / "videos.jsonl"
    @property
    def clips_manifest(self) -> Path: return self.processed / "clips.jsonl"
    @property
    def split_file(self) -> Path: return self.root / "splits.json"

    def annotations(self) -> Path:
        """annotations.jsonl if present, else annotations.csv (JSONL is preferred)."""
        j = self.root / "annotations.jsonl"
        return j if j.exists() or not (self.root / "annotations.csv").exists() else self.root / "annotations.csv"

    def split_dir(self, split: str) -> Path:
        return self.root / split

    def ensure(self) -> None:
        for d in (self.raw, self.annotated, self.features, self.reports, self.split_dir("train"), self.split_dir("validation"), self.split_dir("test")):
            d.mkdir(parents=True, exist_ok=True)


@dataclass(frozen=True)
class ClipConfig:
    duration_s: float = 4.0            # 2, 4 and 6 s are all reasonable: no single length suits every activity
    stride_s: float = 2.0              # overlap between consecutive training clips of one segment
    sample_fps: float = 4.0            # frames sampled per second of clip (T = duration × sample_fps)
    min_overlap: float = 0.5           # share of a clip an annotation must cover to give the clip its label
    min_segment_s: float = 1.0         # shorter annotated segments are skipped

    @property
    def frames(self) -> int:
        return max(2, int(round(self.duration_s * self.sample_fps)))


@dataclass(frozen=True)
class FeatureConfig:
    backbone: str = "mobilenet_v3_small"   # frozen ImageNet CNN for person crops; "none" = pose and motion only
    input_size: int = 112                  # crop resolution (px, square)
    crop_padding: float = 0.15             # box enlarged by this share before cropping (hands and tools reach out)
    keypoint_confidence: float = 0.35


@dataclass(frozen=True)
class TrainingConfig:
    model_name: str = "garment-cnn-gru"
    model_version: str = "0.1.0"
    architecture: str = "gru"               # gru | tcn | pose_gru (see model_factory.ARCHITECTURES)
    input_parts: tuple[str, ...] = ("appearance", "pose", "motion")
    hidden_size: int = 128
    dropout: float = 0.3
    batch_size: int = 32
    learning_rate: float = 1e-3
    weight_decay: float = 1e-4
    epochs: int = 40
    early_stopping_patience: int = 6
    class_weighting: bool = True           # inverse-frequency weights for imbalanced classes
    train_on_unknown: bool = False          # learn UNKNOWN / ambiguous clips as a class (off: UNKNOWN comes from thresholds)
    seed: int = 13
    device: str = "auto"
    num_threads: int = 0                    # 0 = PyTorch default

    def with_(self, **kw) -> "TrainingConfig":  # noqa: ANN003
        return replace(self, **kw)


@dataclass(frozen=True)
class DecisionConfig:
    """When a model's answer becomes UNKNOWN."""

    min_confidence: float = 0.6            # top probability below this → UNKNOWN (LOW_CONFIDENCE)
    min_margin: float = 0.15                # top minus runner-up below this → UNKNOWN (CONFLICTING)
    min_person_ratio: float = 0.6           # share of the clip's frames in which the person was seen
    min_pose_ratio: float = 0.4             # share of frames with a usable pose (when the model uses pose)
    low_confidence: float = 0.75            # predictions below this are counted as "low confidence" in metrics


@dataclass(frozen=True)
class CameraModel:
    enabled: bool = True
    model: str | None = None                # a model directory (models/garment_activity/<name>/<version>); None = default


@dataclass(frozen=True)
class InferenceConfig:
    enabled: bool = False
    default_model: str | None = None
    cameras: dict = field(default_factory=dict)          # camera code / id → CameraModel settings; "*" = all
    inference_fps: float = 1.0               # model runs per second per camera (each run covers the last clip)
    decision: DecisionConfig = field(default_factory=DecisionConfig)
    min_state_seconds: float = 4.0
    unknown_after_seconds: float = 6.0
    report_seconds: float = 30.0
    reset_after_seconds: float = 4.0
    allow_unevaluated: bool = False          # development only: run a model without a held-out test evaluation

    @classmethod
    def load(cls, path: Path | None) -> "InferenceConfig":
        if path is None or not Path(path).is_file():
            return cls()
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        data.pop("note", None)
        decision = _from(DecisionConfig, data.pop("decision", None))
        cams = {str(k).lower(): _from(CameraModel, v) for k, v in (data.pop("cameras", None) or {}).items()}
        return replace(_from(cls, data), decision=decision, cameras=cams)

    def camera(self, camera_id: str, camera_code: str) -> CameraModel | None:
        """The settings for a camera, or None when garment recognition is off for it."""
        if not self.enabled:
            return None
        c = self.cameras.get(camera_code.lower()) or self.cameras.get(camera_id.lower()) or self.cameras.get("*")
        if c is None:
            return CameraModel(True, self.default_model) if not self.cameras else None
        if not c.enabled:
            return None
        return c if c.model else replace(c, model=self.default_model)


def load_training_config(path: Path) -> tuple[TrainingConfig, ClipConfig, FeatureConfig, DecisionConfig]:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    data.pop("note", None)
    t = data.get("training", {})
    if "input_parts" in t:
        t = dict(t, input_parts=tuple(t["input_parts"]))
    return (_from(TrainingConfig, t), _from(ClipConfig, data.get("clips")), _from(FeatureConfig, data.get("features")),
            _from(DecisionConfig, data.get("decision")))


def as_json(obj) -> dict:  # noqa: ANN001
    d = asdict(obj)
    return {k: (str(v) if isinstance(v, Path) else list(v) if isinstance(v, tuple) else v) for k, v in d.items()}
