"""FastAPI application: ``uvicorn app.main:create_app --factory --host 127.0.0.1 --port 8100``.

Startup: settings → logging (with secret redaction) → device selection →
model load (shared by every camera) → pose model load when activity
recognition is enabled (also shared) → backend client → event publisher.
Shutdown: stop every camera session, release sources, flush queued events.
"""
from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse

from app.activity_recognition.pose_estimator import PoseEstimator, YoloPoseEstimator
from app.api.routes import public, secured
from app.camera.manager import SessionManager
from app.core.config import Settings, get_settings
from app.core.device import select_device
from app.core.errors import CvError
from app.core.logging import setup_logging
from app.detection.detector import PersonDetector, YoloPersonDetector
from app.events.publisher import EventPublisher, HttpEventPublisher, NullPublisher
from app.integration.backend import BackendClient, CameraDirectory

log = logging.getLogger("app")


def create_app(settings: Settings | None = None, detector: PersonDetector | None = None, directory: CameraDirectory | None = None,
               publisher: EventPublisher | None = None, source_factory=None, pose_estimator: PoseEstimator | None = None) -> FastAPI:  # noqa: ANN001
    """Builds the app. Tests pass fakes for the detector, the backend and the publisher."""
    settings = settings or get_settings()
    setup_logging(settings.log_level, secrets=[settings.api_key, settings.backend_service_key])
    device_info = select_device(settings.device)
    client: BackendClient | None = None
    if directory is None:
        client = BackendClient(settings.backend_url, settings.backend_service_key, settings.backend_timeout_seconds)
        directory = client
    if publisher is None:
        if settings.publish_events and client is not None and settings.backend_configured:
            publisher = HttpEventPublisher(client.post_event, settings.publish_queue_size, settings.publish_max_attempts)
        else:
            publisher = NullPublisher()
    own_detector = detector is None
    detector = detector or YoloPersonDetector(settings.model_path, device_info.device, settings.confidence, settings.image_size)
    # The pose model serves generic activity recognition and garment activity recognition alike.
    own_pose = pose_estimator is None and (settings.activity_enabled or settings.garment_enabled)
    if own_pose:
        pose_estimator = YoloPoseEstimator(settings.activity_model_path, device_info.device, settings.activity_pose_confidence,
                                           settings.activity_image_size)
    manager = SessionManager(settings, directory, detector, publisher, device_info.device, source_factory, pose_estimator)

    @asynccontextmanager
    async def lifespan(app: FastAPI):  # noqa: ANN202
        log.info("Computer-vision service starting: %s", device_info.note)
        if not settings.backend_configured:
            log.warning("CV_BACKEND_SERVICE_KEY is not set: cameras can't be loaded and no event will be sent.")
        if own_detector:
            await run_in_threadpool(detector.load)  # type: ignore[union-attr]
        if own_pose:
            await run_in_threadpool(pose_estimator.load)  # type: ignore[union-attr]
        publisher.start()
        yield
        log.info("Shutting down: stopping camera sessions and flushing events.")
        await run_in_threadpool(manager.shutdown)
        if client is not None:
            client.close()

    app = FastAPI(title="BunonBrain computer vision", version=__import__("app").__version__, lifespan=lifespan,
                  description="Person detection (YOLO) and tracking (ByteTrack) for configured cameras, with optional activity recognition "
                              "(pose estimation + temporal classification of basic posture and movement). Sends structured events to the BunonBrain "
                              "backend. Track ids are temporary camera-local numbers, never identities; activities are predictions.")
    app.state.settings = settings
    app.state.manager = manager
    app.state.device_info = device_info

    @app.exception_handler(CvError)
    async def _cv_error(_: Request, exc: CvError) -> JSONResponse:
        return JSONResponse(status_code=exc.status, content={"code": exc.code, "message": exc.message})

    app.include_router(public)
    app.include_router(secured)
    return app

# Run with uvicorn's factory mode: uvicorn app.main:create_app --factory --host 127.0.0.1 --port 8100
