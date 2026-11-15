"""Service settings, read from the environment (prefix ``CV_``) or a ``.env`` file.

Nothing secret has a default. The service refuses to start without its own
API key; without a backend URL / service key it still runs but cannot load
camera configuration or publish events.
"""
from __future__ import annotations

from functools import lru_cache
from pathlib import Path

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

ROOT = Path(__file__).resolve().parents[2]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="CV_", env_file=str(ROOT / ".env"), env_file_encoding="utf-8", extra="ignore")

    # ── This service's own API ──
    api_key: str = Field(..., min_length=24, description="Key callers send as X-API-Key to every endpoint except /health.")
    host: str = "127.0.0.1"
    port: int = 8100

    # ── The ASP.NET Core backend ──
    backend_url: str = "http://localhost:5000"
    backend_service_key: str = Field("", description="Sent as X-Service-Key; must equal the backend's CV_SERVICE_KEY.")
    backend_timeout_seconds: float = 10.0

    # ── Model ──
    model_path: Path = ROOT / "models" / "yolo11n.pt"
    device: str = Field("auto", description="auto | cpu | cuda | cuda:N")
    confidence: float = Field(0.40, ge=0.05, le=0.95)
    image_size: int = Field(640, ge=160, le=1920)

    # ── Processing ──
    # 8 fps keeps ByteTrack ids stable for walking people (5 fps split tracks on fast motion in tests);
    # yolo11n on a laptop CPU manages ~12 frames/s in total, shared by all cameras.
    process_fps: float = Field(8.0, gt=0, le=30, description="Frames analysed per second per camera; the rest are skipped.")
    max_sessions: int = Field(4, ge=1, le=32)
    min_hits: int = Field(2, ge=1, le=10, description="Frames a track must be seen before PERSON_DETECTED.")
    lost_after_seconds: float = Field(3.0, gt=0, le=60, description="A track not seen for this long is reported lost.")
    track_update_seconds: float = Field(10.0, ge=1, le=600, description="At most one TRACK_UPDATED per track per interval.")
    zone_debounce_frames: int = Field(2, ge=1, le=30, description="Frames a track must stay in a new zone before ZONE_ENTERED / ZONE_EXITED.")
    reconnect_attempts: int = Field(5, ge=0, le=100)
    reconnect_delay_seconds: float = Field(3.0, gt=0, le=120)
    config_refresh_seconds: float = Field(60.0, ge=5, le=3600, description="How often a running camera reloads its zones from the backend.")
    webcam_index: int = Field(0, ge=0, le=16, description="Local webcam used when a camera's source is LocalWebcam without a number in its label.")
    data_dir: Path = ROOT / "data"

    # ── Event publishing ──
    publish_events: bool = True
    publish_queue_size: int = Field(1000, ge=10, le=100_000)
    publish_max_attempts: int = Field(5, ge=1, le=20)

    # ── Activity recognition (pose estimation + temporal classification) ──
    # Off by default: it loads a second model. CV_ACTIVITY_CAMERAS chooses which cameras use it
    # ("*" = every camera, or a comma-separated list of camera codes / ids); a start request can override it.
    activity_enabled: bool = Field(False, description="Load the pose model at startup and allow activity recognition.")
    activity_cameras: str = Field("*", description="Cameras analysed for activity by default: * or comma-separated codes / ids.")
    activity_model_path: Path = ROOT / "models" / "pose" / "yolo11n-pose.pt"
    activity_pose_confidence: float = Field(0.35, ge=0.05, le=0.95, description="Minimum pose-model box confidence.")
    activity_keypoint_confidence: float = Field(0.35, ge=0.05, le=0.95, description="A keypoint below this confidence counts as missing.")
    activity_image_size: int = Field(640, ge=160, le=1920)
    activity_process_fps: float = Field(4.0, gt=0, le=15, description="Pose inferences per second per camera (independent of CV_PROCESS_FPS).")
    activity_window_seconds: float = Field(3.0, ge=0.5, le=30, description="Temporal window the classifier looks at.")
    activity_min_window_seconds: float = Field(1.0, ge=0.2, le=30, description="Shortest observed span before any prediction.")
    activity_min_observations: int = Field(4, ge=2, le=100, description="Usable pose observations needed in the window.")
    activity_max_gap_seconds: float = Field(1.5, gt=0, le=30, description="A longer gap between observations of a track restarts its window.")
    activity_reset_after_seconds: float = Field(3.0, gt=0, le=120, description="A track unseen this long loses its buffer and state.")
    activity_min_confidence: float = Field(0.55, ge=0.05, le=0.99, description="Predictions below this confidence become UNKNOWN.")
    activity_conflict_margin: float = Field(0.15, ge=0, le=0.9, description="Top two activities closer than this are uncertain (UNKNOWN).")
    activity_min_state_seconds: float = Field(1.5, ge=0, le=60, description="A new activity must persist this long before the state changes.")
    activity_unknown_after_seconds: float = Field(3.0, ge=0, le=120, description="UNKNOWN must persist this long before it replaces a known state.")
    activity_report_seconds: float = Field(15.0, ge=1, le=600, description="At most one ACTIVITY_CLASSIFIED per track per interval while the state is stable.")
    activity_walking_speed: float = Field(0.30, gt=0, le=5, description="Body-centre speed (body heights per second) at which movement counts as walking.")
    activity_match_iou: float = Field(0.30, ge=0.05, le=0.95, description="Minimum overlap between a pose box and a track box to associate them.")
    activity_classifier: str = Field("baseline", description="baseline (pose-feature rules) | sequence (a TorchScript model, see docs)")
    activity_sequence_model_path: Path | None = Field(None, description="TorchScript sequence model for CV_ACTIVITY_CLASSIFIER=sequence.")

    # ── Garment activity recognition (a trained model; see docs/GARMENT_ACTIVITIES.md) ──
    # Off by default. Needs the pose model (it reuses the pose stage), a trained and evaluated model, and
    # configs/garment_inference.json (per-camera models, inference rate, thresholds).
    garment_enabled: bool = Field(False, description="Run garment activity models on the cameras configured in CV_GARMENT_CONFIG.")
    garment_config: Path = ROOT / "configs" / "garment_inference.json"

    # ── Development only ──
    enable_preview: bool = Field(False, description="GET /cameras/{id}/preview.jpg (API key required). Off in production.")
    preview_pose: bool = Field(True, description="Draw pose keypoints in the development preview (GET preview.jpg?pose=false hides them).")
    preview_window: bool = Field(False, description="Show an OpenCV window per camera on the machine running the service.")
    allow_source_override: bool = Field(False, description="Allow POST /cameras/{id}/start to use a local file or webcam index instead of the camera's configured source.")

    log_level: str = "INFO"

    @field_validator("backend_url")
    @classmethod
    def _strip_slash(cls, v: str) -> str:
        return v.rstrip("/")

    @field_validator("model_path", "data_dir", "activity_model_path", "activity_sequence_model_path", "garment_config")
    @classmethod
    def _absolute(cls, v: Path | None) -> Path | None:
        if v is None or str(v) == "":
            return None
        return v if v.is_absolute() else (ROOT / v).resolve()

    @property
    def backend_configured(self) -> bool:
        return bool(self.backend_url and self.backend_service_key)


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]
