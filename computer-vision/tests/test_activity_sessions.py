"""Activity recognition inside camera sessions (pose model mocked): events, per-camera enable / disable,
track isolation across cameras, failure containment, graceful shutdown and the API."""
import pytest
from fastapi.testclient import TestClient

from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.schemas import PoseDetection
from app.camera.manager import SessionManager, StartOptions
from app.core.errors import ModelNotReady
from app.detection.types import BBox
from app.events.schemas import ACTIVITY_EVENT_TYPES
from app.main import create_app
from tests.conftest import FakeDirectory, FakeSource, MemoryPublisher, ScriptedDetector, ScriptedPoseEstimator, camera, wait_for
from tests.poses import person

CAM1 = "11111111-1111-4111-8111-111111111111"
CAM2 = "22222222-2222-4222-8222-222222222222"

# Fast settings so a few seconds of fake video exercise the whole chain.
FAST = ActivityConfig(enabled=True, process_fps=20, window_seconds=1.0, min_window_seconds=0.3, min_observations=3, max_gap_seconds=1.0,
                      reset_after_seconds=1.0, min_state_seconds=0.3, unknown_after_seconds=0.6, report_seconds=0.8)


def scene(postures: dict[range, str], frames: int, x0: float = 200, speed: float = 0.0):
    """Detector + pose scripts for one person whose posture follows ``postures`` (frame ranges)."""
    det, pose = {}, {}
    for i in range(frames):
        p = next((v for r, v in postures.items() if i in r), None)
        if p is None:
            continue
        box, k = person(p, x0 + speed * i, 60, 240, seed=i)
        det[i] = [(*box, 0.9)]
        pose[i] = [PoseDetection(BBox(*box), 0.9, k)]
    return det, pose


def manager(settings, det, pose_est, cams, sources, cfg=FAST):  # noqa: ANN001
    pub = MemoryPublisher()
    m = SessionManager(settings, FakeDirectory(cams), ScriptedDetector(det), pub, "cpu", source_factory=lambda c, o: sources[c.id],
                       pose_estimator=pose_est, activity_config=cfg)
    return m, pub


def test_a_posture_change_becomes_activity_events_for_the_same_temporary_track(settings):
    det, pose = scene({range(0, 45): "SITTING", range(45, 100): "STANDING"}, 100)
    m, pub = manager(settings, det, ScriptedPoseEstimator(pose), {CAM1: camera(CAM1)}, {CAM1: FakeSource(100, delay=0.03)})
    status = m.start(CAM1)
    assert status["activityEnabled"] is True
    assert wait_for(lambda: m.get(CAM1).state.value == "FINISHED", 15)
    acts = [e for e in pub.events if e.event_type in ACTIVITY_EVENT_TYPES]
    changes = [e.payload["activity"] for e in acts if e.event_type == "ACTIVITY_STATE_CHANGED"]
    assert changes[:2] == ["SITTING", "STANDING"], [(e.event_type, e.payload["activity"]) for e in acts]
    assert acts[[e.event_type for e in acts].index("ACTIVITY_STATE_CHANGED", 1)].payload["previousActivity"] == "SITTING"
    detected = next(e for e in pub.events if e.event_type == "PERSON_DETECTED")
    assert {e.track_id for e in acts} == {detected.track_id}                     # the camera's own temporary track id
    assert pub.events.index(detected) < pub.events.index(acts[0])              # never before the track is announced
    assert any(e.event_type == "ACTIVITY_CLASSIFIED" for e in acts)            # periodic confirmation while stable
    for e in acts:
        assert e.source_type == "REAL" and e.model_name == "pose-temporal-baseline" and e.camera_id == CAM1
        assert "keypoints" not in e.payload and e.payload["observationStart"] <= e.payload["observationEnd"]
    models = [e.payload["model"] for e in pub.events if e.event_type == "MODEL_STATUS_CHANGED"]
    assert models == ["scripted", "scripted-pose"]                             # detector and pose model reported ready
    snap = m.get(CAM1).activity.metrics.snapshot()
    assert snap["pose_inferences"] > 0 and snap["avg_step_ms"] >= 0 and snap["unknown_rate"] is not None


def test_activity_is_enabled_per_camera_by_configuration_or_start_request(settings):
    det, pose = scene({range(0, 80): "SITTING"}, 80)
    cams = {CAM1: camera(CAM1), CAM2: camera(CAM2, code="CAM-002")}
    sources = {CAM1: FakeSource(80, delay=0.02), CAM2: FakeSource(80, delay=0.02)}
    m, pub = manager(settings, det, ScriptedPoseEstimator(pose), cams, sources, FAST.with_(cameras=frozenset({"cam-001"})))
    assert m.start(CAM1)["activityEnabled"] is True
    assert m.start(CAM2)["activityEnabled"] is False
    assert wait_for(lambda: all(m.get(c).state.value == "FINISHED" for c in cams), 15)
    assert {e.camera_id for e in pub.events if e.event_type in ACTIVITY_EVENT_TYPES} == {CAM1}
    assert m.get(CAM2).activity is None
    # A start request overrides the configuration, both ways.
    sources[CAM1], sources[CAM2] = FakeSource(5), FakeSource(5)
    assert m.start(CAM1, StartOptions(activity=False))["activityEnabled"] is False
    assert m.start(CAM2, StartOptions(activity=True))["activityEnabled"] is True
    m.shutdown(2)


def test_asking_for_activity_without_a_pose_model_is_refused_but_the_default_degrades(settings):
    m, _ = manager(settings, {}, ScriptedPoseEstimator(loaded=False), {CAM1: camera(CAM1)}, {CAM1: FakeSource(5)})
    with pytest.raises(ModelNotReady) as e:
        m.start(CAM1, StartOptions(activity=True))
    assert e.value.code == "pose_model_not_ready"
    assert m.start(CAM1)["activityEnabled"] is False          # configured default: detection still starts, without activity
    m.shutdown(2)


def test_tracks_are_never_shared_between_cameras(settings):
    det, pose = scene({range(0, 60): "SITTING"}, 60)
    cams = {CAM1: camera(CAM1), CAM2: camera(CAM2, code="CAM-002")}
    sources = {CAM1: FakeSource(60, delay=0.03), CAM2: FakeSource(60, delay=0.03)}
    m, pub = manager(settings, det, ScriptedPoseEstimator(pose), cams, sources)
    m.start(CAM1)
    m.start(CAM2)
    assert wait_for(lambda: all(m.get(c).state.value == "FINISHED" for c in cams), 15)
    t1 = {e.track_id for e in pub.events if e.camera_id == CAM1 and e.event_type in ACTIVITY_EVENT_TYPES}
    t2 = {e.track_id for e in pub.events if e.camera_id == CAM2 and e.event_type in ACTIVITY_EVENT_TYPES}
    assert t1 and t2 and t1.isdisjoint(t2)                    # the same picture on two cameras: two separate tracks
    assert m.get(CAM1).activity.states is not m.get(CAM2).activity.states
    assert all(k[0] == CAM1 for k in m.get(CAM1).activity.buffer.keys())


def test_a_failing_pose_model_switches_activity_off_but_detection_continues(settings):
    det, _ = scene({range(0, 60): "STANDING"}, 60)
    m, pub = manager(settings, det, ScriptedPoseEstimator(fail=True), {CAM1: camera(CAM1)}, {CAM1: FakeSource(60, delay=0.02)})
    m.start(CAM1)
    assert wait_for(lambda: m.get(CAM1).state.value == "FINISHED", 15)
    s = m.get(CAM1)
    assert s.state.value == "FINISHED" and not s.activity.active
    assert "consecutive pose errors" in s.status()["activityError"]
    assert "PERSON_DETECTED" in pub.types() and not any(t in ACTIVITY_EVENT_TYPES for t in pub.types())
    assert s.activity.metrics.errors == 5


def test_graceful_shutdown_clears_activity_state(settings):
    det, pose = scene({range(0, 10): "SITTING"}, 10)
    sources = {CAM1: FakeSource(10, live=True)}
    m, pub = manager(settings, det, ScriptedPoseEstimator(pose), {CAM1: camera(CAM1)}, sources)
    m.start(CAM1)
    assert wait_for(lambda: m.get(CAM1).activity.metrics.pose_inferences > 3, 10)
    m.shutdown(3)
    s = m.get(CAM1)
    assert not s.alive and s.state.value == "STOPPED" and pub.closed
    assert len(s.activity.buffer) == 0 and s.activity.states.states == {}


def test_activity_api_and_pose_preview(settings):
    det, pose = scene({range(0, 2000): "SITTING"}, 2000)
    settings.enable_preview = True
    app = create_app(settings, detector=ScriptedDetector(det), directory=FakeDirectory({CAM1: camera(CAM1)}), publisher=MemoryPublisher(),
                     source_factory=lambda cfg, opts: FakeSource(2000, live=True, delay=0.01), pose_estimator=ScriptedPoseEstimator(pose))
    app.state.manager.activity_config = FAST                    # CV_ACTIVITY_ENABLED is off in these settings: the request turns it on
    with TestClient(app) as c:
        c.headers.update({"X-API-Key": settings.api_key})
        r = c.post(f"/cameras/{CAM1}/start", json={"activity": True})
        assert r.status_code == 202 and r.json()["activityEnabled"] is True
        assert wait_for(lambda: any(t["activity"] == "SITTING" for t in c.get(f"/cameras/{CAM1}/activity").json()["tracks"]), 10)
        body = c.get(f"/cameras/{CAM1}/activity").json()
        assert body["classifier"]["name"] == "pose-temporal-baseline" and "keypoints" not in str(body)
        assert body["tracks"][0]["trackId"].startswith(r.json()["sessionTag"])
        assert c.get(f"/cameras/{CAM1}/metrics").json()["activity"]["pose_inferences"] > 0
        assert c.get("/health/model").json()["pose"]["name"] == "scripted-pose"
        for q in ("", "?pose=false"):
            img = c.get(f"/cameras/{CAM1}/preview.jpg{q}")
            assert img.status_code == 200 and img.headers["content-type"] == "image/jpeg"
        assert TestClient(app).get(f"/cameras/{CAM1}/activity").status_code == 401     # the key is required
        c.post(f"/cameras/{CAM1}/stop")
