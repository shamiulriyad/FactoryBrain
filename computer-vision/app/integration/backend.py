"""Talks to the ASP.NET Core backend with the service key (X-Service-Key).

The service never connects to PostgreSQL: cameras and zones come from the
integration API, events go through the monitoring ingestion API, so every
rule (factory reach, camera / zone ownership, REAL-only, no identities) is
enforced by the backend.
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Protocol

import httpx

from app.core.errors import BackendUnavailable, CameraNotFound
from app.integration.models import CameraConfig

log = logging.getLogger(__name__)

SERVICE_KEY_HEADER = "X-Service-Key"


class CameraDirectory(Protocol):
    def list_cameras(self) -> list[CameraConfig]: ...
    def get_camera(self, camera_id: str) -> CameraConfig: ...
    def download_video(self, camera_id: str, dest: Path) -> Path: ...


class BackendClient:
    def __init__(self, base_url: str, service_key: str, timeout: float = 10.0, transport: httpx.BaseTransport | None = None) -> None:
        self._client = httpx.Client(base_url=base_url, timeout=timeout, transport=transport,
                                    headers={SERVICE_KEY_HEADER: service_key, "User-Agent": "bunonbrain-computer-vision"})

    def _get(self, path: str) -> httpx.Response:
        try:
            res = self._client.get(path)
        except httpx.HTTPError as exc:
            raise BackendUnavailable(f"The backend can't be reached ({type(exc).__name__}).") from exc
        if res.status_code == 401:
            raise BackendUnavailable("The backend refused the service key (401). Check CV_BACKEND_SERVICE_KEY and the backend's CV_SERVICE_KEY.", "backend_auth_failed")
        if res.status_code == 404:
            raise CameraNotFound("The backend doesn't know this camera, or it is outside the service's factories.")
        if res.status_code >= 400:
            raise BackendUnavailable(f"The backend answered {res.status_code}.")
        return res

    def list_cameras(self) -> list[CameraConfig]:
        return [CameraConfig.model_validate(c) for c in self._get("/api/integrations/cv/cameras").json()]

    def get_camera(self, camera_id: str) -> CameraConfig:
        return CameraConfig.model_validate(self._get(f"/api/integrations/cv/cameras/{camera_id}").json())

    def download_video(self, camera_id: str, dest: Path) -> Path:
        """Stream the camera's recorded video to ``dest`` (a temporary working copy)."""
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_suffix(dest.suffix + ".part")
        try:
            with self._client.stream("GET", f"/api/integrations/cv/cameras/{camera_id}/video") as res:
                if res.status_code == 404:
                    raise CameraNotFound("The camera has no recorded video.")
                if res.status_code >= 400:
                    raise BackendUnavailable(f"Video download failed ({res.status_code}).")
                with tmp.open("wb") as f:
                    for chunk in res.iter_bytes(1 << 16):
                        f.write(chunk)
        except httpx.HTTPError as exc:
            tmp.unlink(missing_ok=True)
            raise BackendUnavailable(f"Video download failed ({type(exc).__name__}).") from exc
        tmp.replace(dest)
        return dest

    def post_event(self, body: dict) -> httpx.Response:
        return self._client.post("/api/monitoring/events", json=body)

    def close(self) -> None:
        self._client.close()
