"""The HTTP API: authentication, start / stop lifecycle, status, metrics, preview, no secrets in responses."""
import pytest
from fastapi.testclient import TestClient

from app.main import create_app
from tests.conftest import FakeDirectory, FakeSource, MemoryPublisher, ScriptedDetector, camera, wait_for

CAM1 = "11111111-1111-4111-8111-111111111111"
RTSP = "44444444-4444-4444-8444-444444444444"


@pytest.fixture
def client(settings):
    cams = {CAM1: camera(CAM1), RTSP: camera(RTSP, code="CAM-RTSP", source={"sourceType": "Rtsp", "streamUrl": "rtsp://admin:S3cret-pw@10.0.0.9/live"})}
    settings.enable_preview = True
    app = create_app(settings, detector=ScriptedDetector({i: [(100, 100, 160, 300, 0.9)] for i in range(500)}),
                     directory=FakeDirectory(cams), publisher=MemoryPublisher(),
                     source_factory=lambda cfg, opts: FakeSource(500, live=True, delay=0.005))
    with TestClient(app) as c:
        c.headers.update({"X-API-Key": settings.api_key})
        yield c


def test_health_is_public_and_everything_else_needs_the_key(settings, client):
    anon = TestClient(client.app)
    assert anon.get("/health").json()["status"] == "ok"
    for path in ("/cameras", "/metrics", "/health/model", "/system/device", f"/cameras/{CAM1}/status"):
        assert anon.get(path).status_code == 401
        assert anon.get(path, headers={"X-API-Key": "wrong-wrong-wrong-wrong-wrong"}).status_code == 401
    assert anon.post(f"/cameras/{CAM1}/start").status_code == 401


def test_cameras_never_expose_stream_urls_or_credentials(client):
    r = client.get("/cameras")
    assert r.status_code == 200
    assert {c["code"] for c in r.json()} == {"CAM-001", "CAM-RTSP"}
    assert "S3cret-pw" not in r.text and "rtsp://" not in r.text


def test_start_status_metrics_preview_stop(client):
    r = client.post(f"/cameras/{CAM1}/start", json={"process_fps": 20})
    assert r.status_code == 202 and r.json()["cameraCode"] == "CAM-001"
    dup = client.post(f"/cameras/{CAM1}/start")
    assert dup.status_code == 409 and dup.json()["code"] == "session_already_running"
    assert wait_for(lambda: client.get(f"/cameras/{CAM1}/metrics").json()["frames_processed"] >= 5)
    status = client.get(f"/cameras/{CAM1}/status").json()
    assert status["state"] == "RUNNING" and status["activeTracks"] == 1
    m = client.get("/metrics").json()
    assert m["sessions"][0]["cameraCode"] == "CAM-001" and m["process"]["rssMb"] > 0
    preview = client.get(f"/cameras/{CAM1}/preview.jpg")
    assert preview.status_code == 200 and preview.headers["content-type"] == "image/jpeg" and preview.content[:2] == b"\xff\xd8"
    stopped = client.post(f"/cameras/{CAM1}/stop")
    assert stopped.status_code == 200 and stopped.json()["state"] == "STOPPED"
    again = client.post(f"/cameras/{CAM1}/stop")
    assert again.status_code == 409 and again.json()["code"] == "session_not_running"


def test_unknown_cameras_and_bad_requests(client):
    r = client.post("/cameras/99999999-9999-4999-8999-999999999999/start")
    assert r.status_code == 404 and r.json()["code"] == "camera_not_found"
    assert client.post(f"/cameras/{CAM1}/start", json={"process_fps": 500}).status_code == 422
    assert client.post(f"/cameras/{CAM1}/start", json={"source": {"type": "file", "path": "a.mp4"}}).json()["code"] == "override_disabled"
    assert client.get("/cameras/never-started/status").status_code == 400


def test_device_and_model_endpoints(client):
    dev = client.get("/system/device").json()
    assert dev["device"] in ("cpu", "cuda:0") and "torch_version" in dev
    model = client.get("/health/model").json()
    assert model["loaded"] is True and model["classes"] == ["person"]
