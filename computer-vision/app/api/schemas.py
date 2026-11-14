"""Request and response bodies of the service's API (no stream URLs or credentials anywhere)."""
from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field


class Health(BaseModel):
    status: Literal["ok", "degraded"]
    service: str = "bunonbrain-computer-vision"
    version: str
    model_loaded: bool
    sessions_running: int


class ModelHealth(BaseModel):
    name: str
    version: str
    device: str
    loaded: bool
    error: str | None
    classes: list[str]
    loaded_at: float | None
    inference_count: int
    avg_inference_ms: float


class SourceOverrideIn(BaseModel):
    """Development only (CV_ALLOW_SOURCE_OVERRIDE=true)."""

    type: Literal["file", "webcam"]
    path: str | None = Field(None, description="A video file inside CV_DATA_DIR (type=file).")
    index: int | None = Field(None, ge=0, le=16, description="Webcam number (type=webcam).")


class StartRequest(BaseModel):
    process_fps: float | None = Field(None, gt=0, le=30, description="Override CV_PROCESS_FPS for this camera.")
    loop: bool = Field(False, description="Recorded video only: start again at the end.")
    source: SourceOverrideIn | None = None


class SessionStatus(BaseModel):
    cameraId: str
    cameraCode: str
    factoryId: str
    state: str
    source: str
    sourceKind: str
    zones: int
    sessionTag: str
    startedAt: float
    endedAt: float | None
    error: str | None
    activeTracks: int
    processingFps: float


class CameraListItem(BaseModel):
    id: str
    code: str
    name: str
    factoryCode: str
    status: str
    sourceType: str
    zones: int
    processing: SessionStatus | None


class Problem(BaseModel):
    code: str
    message: str
