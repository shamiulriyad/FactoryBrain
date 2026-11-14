"""HTTP endpoints. Everything except ``GET /health`` needs X-API-Key."""
from __future__ import annotations

import time

import cv2
import psutil
from fastapi import APIRouter, Depends, Request, Response
from fastapi.concurrency import run_in_threadpool

from app import __version__
from app.api.schemas import CameraListItem, Health, ModelHealth, SessionStatus, StartRequest
from app.camera.manager import SessionManager, StartOptions
from app.camera.sources import SourceOverride
from app.core.errors import CvError
from app.core.security import require_api_key

public = APIRouter()
secured = APIRouter(dependencies=[Depends(require_api_key)])


def _manager(request: Request) -> SessionManager:
    return request.app.state.manager


@public.get("/health", response_model=Health, tags=["health"])
def health(request: Request) -> Health:
    m = _manager(request)
    loaded = m.detector.info.loaded
    return Health(status="ok" if loaded else "degraded", version=__version__, model_loaded=loaded, sessions_running=len(m.running()))


@secured.get("/health/model", response_model=ModelHealth, tags=["health"])
def model_health(request: Request) -> ModelHealth:
    i = _manager(request).detector.info
    return ModelHealth(name=i.name, version=i.version, device=i.device, loaded=i.loaded, error=i.error, classes=i.classes,
                       loaded_at=i.loaded_at, inference_count=i.inference_count, avg_inference_ms=i.avg_inference_ms)


@secured.get("/system/device", tags=["health"])
def system_device(request: Request) -> dict:
    return request.app.state.device_info.as_dict()


@secured.get("/cameras", response_model=list[CameraListItem], tags=["cameras"])
async def cameras(request: Request) -> list[CameraListItem]:
    m = _manager(request)
    configs = await run_in_threadpool(m.directory.list_cameras)
    out = []
    for c in configs:
        s = m.get(c.id)
        out.append(CameraListItem(id=c.id, code=c.code, name=c.name, factoryCode=c.factory_code, status=c.status,
                                  sourceType=c.source.source_type, zones=len(c.zones), processing=SessionStatus(**s.status()) if s else None))
    return out


@secured.post("/cameras/{camera_id}/start", response_model=SessionStatus, status_code=202, tags=["cameras"])
async def start(camera_id: str, request: Request, body: StartRequest | None = None) -> SessionStatus:
    body = body or StartRequest()
    override = SourceOverride(body.source.type, body.source.path, body.source.index) if body.source else None
    # Starting may download a recorded video: keep it off the event loop.
    status = await run_in_threadpool(_manager(request).start, camera_id, StartOptions(body.process_fps, body.loop, override))
    return SessionStatus(**status)


@secured.post("/cameras/{camera_id}/stop", response_model=SessionStatus, tags=["cameras"])
async def stop(camera_id: str, request: Request) -> SessionStatus:
    return SessionStatus(**await run_in_threadpool(_manager(request).stop, camera_id))


def _session(request: Request, camera_id: str):  # noqa: ANN202
    s = _manager(request).get(camera_id)
    if s is None:
        raise CvError("This camera hasn't been processed since the service started.", "session_not_found")
    return s


@secured.get("/cameras/{camera_id}/status", response_model=SessionStatus, tags=["cameras"])
def camera_status(camera_id: str, request: Request) -> SessionStatus:
    return SessionStatus(**_session(request, camera_id).status())


@secured.get("/cameras/{camera_id}/metrics", tags=["metrics"])
def camera_metrics(camera_id: str, request: Request) -> dict:
    s = _session(request, camera_id)
    return {"cameraCode": s.cfg.code, "state": s.state.value, **s.metrics.snapshot()}


@secured.get("/metrics", tags=["metrics"])
def metrics(request: Request) -> dict:
    m = _manager(request)
    proc = psutil.Process()
    return {
        "time": time.time(),
        "model": {"name": m.detector.info.name, "device": m.device, "inferenceCount": m.detector.info.inference_count,
                  "avgInferenceMs": m.detector.info.avg_inference_ms},
        "sessions": [{"cameraCode": s.cfg.code, "state": s.state.value, **s.metrics.snapshot()} for s in m.all()],
        "publisher": m.publisher.stats.as_dict() | {"pending": getattr(m.publisher, "pending", 0)},
        "process": {"rssMb": round(proc.memory_info().rss / 2**20, 1), "threads": proc.num_threads(), "cpuPercent": proc.cpu_percent(interval=None)},
    }


@secured.post("/config/refresh", tags=["cameras"])
async def refresh_config(request: Request) -> dict:
    """Camera configuration changed in the platform (zones, status): apply it to running cameras now."""
    return {"updated": await run_in_threadpool(_manager(request).refresh_config)}


@secured.get("/cameras/{camera_id}/preview.jpg", tags=["development"], response_class=Response)
def preview(camera_id: str, request: Request) -> Response:
    """Development only (CV_ENABLE_PREVIEW=true): the last analysed frame with boxes, track ids, confidence, zones and FPS."""
    if not request.app.state.settings.enable_preview:
        raise CvError("The development preview is off (CV_ENABLE_PREVIEW=false).", "preview_disabled")
    frame = _session(request, camera_id).preview_frame()
    if frame is None:
        raise CvError("No frame analysed yet.", "no_frame")
    ok, jpg = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
    return Response(jpg.tobytes(), media_type="image/jpeg", headers={"Cache-Control": "no-store"})
