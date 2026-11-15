"""Test doubles: a scripted detector, frame sources, a camera directory and an in-memory publisher.

Unit tests replace only the model (inference) and the video input; tracking
(real ByteTrack), zones, lifecycle, events and the API are the real code.
``test_real_model.py`` runs the real YOLO model.
"""
from __future__ import annotations

import threading
import time
from pathlib import Path

import numpy as np
import pytest

from app.core.config import Settings
from app.core.errors import InvalidSource
from app.detection.detector import ModelInfo
from app.detection.types import BBox, Detection
from app.events.publisher import PublisherStats
from app.integration.models import CameraConfig

W, H = 640, 360


def frame_with_index(i: int) -> np.ndarray:
    """A blank frame that carries its own index in two pixels (read back by ScriptedDetector)."""
    f = np.zeros((H, W, 3), np.uint8)
    f[0, 0, 0], f[0, 1, 0] = i % 256, i // 256
    return f


def index_of(frame: np.ndarray) -> int:
    return int(frame[0, 0, 0]) + 256 * int(frame[0, 1, 0])


class ScriptedDetector:
    """Returns the boxes scripted for each frame index: {index: [(x1, y1, x2, y2, conf), ...]}."""

    def __init__(self, script: dict[int, list[tuple[float, float, float, float, float]]] | None = None, loaded: bool = True) -> None:
        self.script = script or {}
        self.info = ModelInfo(name="scripted", version="test", path="-", device="cpu", loaded=loaded)
        self.calls = 0

    def detect(self, frame: np.ndarray, camera_id: str, frame_ts: float) -> list[Detection]:
        self.calls += 1
        rows = self.script.get(index_of(frame), [])
        return [Detection(BBox(x1, y1, x2, y2), conf, 0, "person", camera_id, frame_ts) for x1, y1, x2, y2, conf in rows]


class ScriptedPoseEstimator:
    """Pose model double: the keypoints scripted for each frame index, {index: [PoseDetection, ...]} (or raises if ``fail``)."""

    def __init__(self, script: dict | None = None, loaded: bool = True, fail: bool = False) -> None:
        from app.activity_recognition.pose_estimator import PoseModelInfo

        self.script = script or {}
        self.fail = fail
        self.info = PoseModelInfo(name="scripted-pose", version="test", path="-", device="cpu", loaded=loaded)
        self.calls = 0

    def estimate(self, frame: np.ndarray):  # noqa: ANN201
        self.calls += 1
        if self.fail:
            raise RuntimeError("pose model crashed")
        return list(self.script.get(index_of(frame), []))


class FakeSource:
    """``frames`` indexed frames; a file ends, a live source fails after ``drop_after`` frames until reopened."""

    def __init__(self, frames: int, live: bool = False, fail_open: bool = False, drop_after: int | None = None,
                 reopen_fails: bool = False, delay: float = 0.0) -> None:
        self.kind = "stream" if live else "file"
        self.is_live = live
        self.description = "fake stream" if live else "fake video"
        self.frames, self.fail_open, self.drop_after, self.reopen_fails, self.delay = frames, fail_open, drop_after, reopen_fails, delay
        self.i = 0
        self.opened = 0
        self.released = 0
        self.dropped = False

    def open(self) -> None:
        if self.fail_open or (self.opened > 0 and self.reopen_fails):
            self.opened += 1
            raise InvalidSource("Can't open fake source.")
        self.opened += 1
        self.dropped = False

    def read(self):  # noqa: ANN201
        if self.delay:
            time.sleep(self.delay)
        if self.drop_after is not None and self.i == self.drop_after and not self.dropped and self.opened == 1:
            self.dropped = True
            return False, None
        if self.i >= self.frames:
            if self.is_live:
                time.sleep(0.01)
                return True, frame_with_index(self.frames - 1)
            return False, None
        f = frame_with_index(self.i)
        self.i += 1
        return True, f

    def release(self) -> None:
        self.released += 1


class FakeDirectory:
    def __init__(self, cameras: dict[str, CameraConfig]) -> None:
        self.cameras = cameras

    def list_cameras(self) -> list[CameraConfig]:
        return list(self.cameras.values())

    def get_camera(self, camera_id: str) -> CameraConfig:
        from app.core.errors import CameraNotFound

        if camera_id not in self.cameras:
            raise CameraNotFound("unknown camera")
        return self.cameras[camera_id]

    def download_video(self, camera_id: str, dest: Path) -> Path:
        raise AssertionError("not used")


class MemoryPublisher:
    def __init__(self) -> None:
        self.events = []
        self.stats = PublisherStats()
        self.closed = False
        self._lock = threading.Lock()

    def publish(self, event) -> bool:  # noqa: ANN001
        with self._lock:
            self.events.append(event)
            self.stats.queued += 1
        return True

    def start(self) -> None: ...

    def close(self, timeout: float = 5.0) -> None:
        self.closed = True

    def types(self, camera_id: str | None = None) -> list[str]:
        return [e.event_type for e in self.events if camera_id is None or e.camera_id == camera_id]


def camera(cid: str = "11111111-1111-4111-8111-111111111111", code: str = "CAM-001", zones: list[dict] | None = None,
           status: str = "Active", source: dict | None = None, factory: str = "f0000000-0000-4000-8000-000000000001") -> CameraConfig:
    return CameraConfig.model_validate({
        "id": cid, "code": code, "name": code, "cameraType": "IpCamera", "factoryId": factory, "factoryCode": "MAIN",
        "lineId": "line-1", "status": status, "source": source or {"sourceType": "RecordedVideo", "hasVideo": True},
        "zones": zones or [], "coveredWorkstations": [], "frameWidth": W, "frameHeight": H,
    })


def rect_zone(zid: str, name: str, x0: float, y0: float, x1: float, y1: float, ztype: str = "Workstation") -> dict:
    return {"id": zid, "name": name, "zoneType": ztype, "points": [{"x": x0, "y": y0}, {"x": x1, "y": y0}, {"x": x1, "y": y1}, {"x": x0, "y": y1}]}


@pytest.fixture
def settings(tmp_path: Path) -> Settings:
    return Settings(_env_file=None, api_key="test-api-key-0123456789abcdef", backend_service_key="svc-key", data_dir=tmp_path,
                    model_path=tmp_path / "missing.pt", process_fps=30, max_sessions=2, min_hits=2, lost_after_seconds=0.3,
                    track_update_seconds=1, zone_debounce_frames=2, reconnect_attempts=2, reconnect_delay_seconds=0.05,
                    config_refresh_seconds=3600, publish_events=False)


def wait_for(predicate, timeout: float = 5.0) -> bool:  # noqa: ANN001
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.02)
    return False
