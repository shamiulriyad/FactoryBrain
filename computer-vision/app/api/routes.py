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
    m = _manager(request)
    i = m.detector.info
    return ModelHealth(name=i.name, version=i.version, device=i.device, loaded=i.loaded, error=i.error, classes=i.classes,
                       loaded_at=i.loaded_at, inference_count=i.inference_count, avg_inference_ms=i.avg_inference_ms, pose=_pose_info(m),
                       garment=_garment_info(m))


def _garment_info(m: SessionManager) -> dict:
    g = m.garment_config
    return {"enabled": g.enabled, "defaultModel": g.default_model, "inferenceFps": g.inference_fps, "allowUnevaluated": g.allow_unevaluated,
            "loadedModels": [{**c.summary(), "avgInferenceMs": c.avg_ms, "calls": c.calls} for c in m._garment_models.values()]}


def _pose_info(m: SessionManager) -> dict:
    ac = m.activity_config
    base = {"activityEnabled": ac.enabled, "cameras": "*" if ac.cameras is None else sorted(ac.cameras), "processFps": ac.process_fps,
            "windowSeconds": ac.window_seconds, "minConfidence": ac.min_confidence, "classifier": ac.classifier}
    p = m.pose_estimator
    if p is None:
        return base | {"loaded": False, "error": "Activity recognition is off (CV_ACTIVITY_ENABLED=false)." if not ac.enabled else "No pose estimator."}
    i = p.info
    return base | {"name": i.name, "version": i.version, "device": i.device, "loaded": i.loaded, "error": i.error, "keypoints": i.keypoints,
                   "loadedAt": i.loaded_at, "inferenceCount": i.inference_count, "avgInferenceMs": i.avg_inference_ms}


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
    status = await run_in_threadpool(_manager(request).start, camera_id, StartOptions(body.process_fps, body.loop, override, body.activity, body.garment))
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
    return {"cameraCode": s.cfg.code, "state": s.state.value, **s.metrics.snapshot(),
            "activity": None if s.activity is None else s.activity.metrics.snapshot(),
            "garment": None if s.garment is None else s.garment.metrics.snapshot() | {"modelInferenceMs": s.garment.clf.avg_ms}}


@secured.get("/cameras/{camera_id}/garment", tags=["activity"])
def camera_garment(camera_id: str, request: Request) -> dict:
    """Current garment activity prediction of each track on this camera, with the model and its evaluation status. No images or keypoints."""
    s = _session(request, camera_id)
    if s.garment is None:
        return {"cameraCode": s.cfg.code, "garmentEnabled": False, "tracks": [],
                "note": "Garment activity recognition is off for this camera (CV_GARMENT_ENABLED / CV_GARMENT_CONFIG, or start it with garment=true)."}
    return {"cameraCode": s.cfg.code, "garmentEnabled": s.garment.active, "error": s.garment.disabled_reason, "model": s.garment.clf.summary(),
            "tracks": s.garment.snapshot(s.builder.track_id),
            "note": "Predictions of a trained model for temporary track ids: not identities, not verified work, not productivity."}


@secured.get("/cameras/{camera_id}/activity", tags=["activity"])
def camera_activity(camera_id: str, request: Request) -> dict:
    """Current activity state of each track on this camera (temporary track ids, predicted activity, history). No keypoints or images."""
    s = _session(request, camera_id)
    if s.activity is None:
        return {"cameraCode": s.cfg.code, "activityEnabled": False, "tracks": [],
                "note": "Activity recognition is off for this camera (CV_ACTIVITY_ENABLED / CV_ACTIVITY_CAMERAS, or start it with activity=true)."}
    return {"cameraCode": s.cfg.code, "activityEnabled": s.activity.active, "error": s.activity.disabled_reason,
            "classifier": {"name": s.activity.classifier.name, "version": s.activity.classifier.version},
            "tracks": s.activity.snapshot(s.builder.track_id),
            "note": "Activities are predictions of basic posture / movement for temporary track ids: not identities, not productivity."}


@secured.get("/metrics", tags=["metrics"])
def metrics(request: Request) -> dict:
    m = _manager(request)
    proc = psutil.Process()
    return {
        "time": time.time(),
        "model": {"name": m.detector.info.name, "device": m.device, "inferenceCount": m.detector.info.inference_count,
                  "avgInferenceMs": m.detector.info.avg_inference_ms},
        "pose": _pose_info(m),
        "garment": _garment_info(m),
        "sessions": [{"cameraCode": s.cfg.code, "state": s.state.value, **s.metrics.snapshot(),
                      "activity": None if s.activity is None else s.activity.metrics.snapshot(),
                      "garment": None if s.garment is None else s.garment.metrics.snapshot()} for s in m.all()],
        "publisher": m.publisher.stats.as_dict() | {"pending": getattr(m.publisher, "pending", 0)},
        "process": {"rssMb": round(proc.memory_info().rss / 2**20, 1), "threads": proc.num_threads(), "cpuPercent": proc.cpu_percent(interval=None)},
    }


@secured.post("/config/refresh", tags=["cameras"])
async def refresh_config(request: Request) -> dict:
    """Camera configuration changed in the platform (zones, status): apply it to running cameras now."""
    return {"updated": await run_in_threadpool(_manager(request).refresh_config)}


@secured.get("/cameras/{camera_id}/preview.jpg", tags=["development"], response_class=Response)
def preview(camera_id: str, request: Request, pose: bool | None = None) -> Response:
    """Development only (CV_ENABLE_PREVIEW=true): the last analysed frame with boxes, track ids, confidence, zones, FPS and,
    with activity recognition, pose keypoints and the predicted activity. ``?pose=false`` hides the keypoints."""
    settings = request.app.state.settings
    if not settings.enable_preview:
        raise CvError("The development preview is off (CV_ENABLE_PREVIEW=false).", "preview_disabled")
    frame = _session(request, camera_id).preview_frame(show_pose=settings.preview_pose if pose is None else pose)
    if frame is None:
        raise CvError("No frame analysed yet.", "no_frame")
    ok, jpg = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
    return Response(jpg.tobytes(), media_type="image/jpeg", headers={"Cache-Control": "no-store"})
