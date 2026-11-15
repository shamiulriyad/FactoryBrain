"""Activity recognition settings, taken from the service settings (``CV_ACTIVITY_*``).

Kept in one frozen object so every part (pose, buffer, classifier, state
manager) reads the same values and tests can build one directly.
"""
from __future__ import annotations

from dataclasses import dataclass, replace
from pathlib import Path

#: Names and versions published with every activity event.
BASELINE_MODEL_NAME = "pose-temporal-baseline"
BASELINE_MODEL_VERSION = "1.0"


@dataclass(frozen=True)
class ActivityConfig:
    enabled: bool = False
    cameras: frozenset[str] | None = None        # None = every camera; else camera codes / ids (case-insensitive codes)
    model_path: Path = Path("models/pose/yolo11n-pose.pt")
    pose_confidence: float = 0.35
    keypoint_confidence: float = 0.35
    image_size: int = 640
    process_fps: float = 4.0
    window_seconds: float = 3.0
    min_window_seconds: float = 1.0
    min_observations: int = 4
    max_gap_seconds: float = 1.5
    reset_after_seconds: float = 3.0
    min_confidence: float = 0.55
    conflict_margin: float = 0.15
    min_state_seconds: float = 1.5
    unknown_after_seconds: float = 3.0
    report_seconds: float = 15.0
    walking_speed: float = 0.30
    match_iou: float = 0.30
    classifier: str = "baseline"
    sequence_model_path: Path | None = None
    history_size: int = 50

    @classmethod
    def from_settings(cls, s) -> "ActivityConfig":  # noqa: ANN001 - app.core.config.Settings
        cams = None
        raw = (s.activity_cameras or "").strip()
        if raw and raw != "*":
            cams = frozenset(p.strip().lower() for p in raw.split(",") if p.strip())
        return cls(
            enabled=s.activity_enabled, cameras=cams, model_path=s.activity_model_path, pose_confidence=s.activity_pose_confidence,
            keypoint_confidence=s.activity_keypoint_confidence, image_size=s.activity_image_size, process_fps=s.activity_process_fps,
            window_seconds=s.activity_window_seconds, min_window_seconds=min(s.activity_min_window_seconds, s.activity_window_seconds),
            min_observations=s.activity_min_observations, max_gap_seconds=s.activity_max_gap_seconds,
            reset_after_seconds=s.activity_reset_after_seconds, min_confidence=s.activity_min_confidence,
            conflict_margin=s.activity_conflict_margin, min_state_seconds=s.activity_min_state_seconds,
            unknown_after_seconds=s.activity_unknown_after_seconds, report_seconds=s.activity_report_seconds,
            walking_speed=s.activity_walking_speed, match_iou=s.activity_match_iou, classifier=s.activity_classifier.strip().lower(),
            sequence_model_path=s.activity_sequence_model_path,
        )

    def enabled_for(self, camera_id: str, camera_code: str) -> bool:
        """Whether a camera uses activity recognition unless its start request says otherwise."""
        if not self.enabled:
            return False
        return self.cameras is None or camera_id.lower() in self.cameras or camera_code.lower() in self.cameras

    def with_(self, **kw) -> "ActivityConfig":  # noqa: ANN003
        return replace(self, **kw)
