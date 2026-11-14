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

    # ── Development only ──
    enable_preview: bool = Field(False, description="GET /cameras/{id}/preview.jpg (API key required). Off in production.")
    preview_window: bool = Field(False, description="Show an OpenCV window per camera on the machine running the service.")
    allow_source_override: bool = Field(False, description="Allow POST /cameras/{id}/start to use a local file or webcam index instead of the camera's configured source.")

    log_level: str = "INFO"

    @field_validator("backend_url")
    @classmethod
    def _strip_slash(cls, v: str) -> str:
        return v.rstrip("/")

    @field_validator("model_path", "data_dir")
    @classmethod
    def _absolute(cls, v: Path) -> Path:
        return v if v.is_absolute() else (ROOT / v).resolve()

    @property
    def backend_configured(self) -> bool:
        return bool(self.backend_url and self.backend_service_key)


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]
