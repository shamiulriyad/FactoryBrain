"""Live garment recognition (model mocked): smoothing, UNKNOWN, per-camera isolation, failure containment,
session integration, event schema and publishing, per-camera configuration and the API."""
import json
from datetime import datetime, timezone

import httpx
import numpy as np
import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError

from app.activity_recognition.activity_config import ActivityConfig
from app.activity_recognition.schemas import PoseDetection
from app.camera.manager import SessionManager, StartOptions
from app.core.errors import ModelNotReady
from app.detection.types import BBox
from app.events.builder import EventBuilder, EventContext
from app.events.publisher import HttpEventPublisher
from app.events.schemas import GARMENT_EVENT_TYPES, MonitoringEvent
from app.garment_activities.activity_service import GarmentActivityService
from app.garment_activities.config import InferenceConfig
from app.integration.backend import BackendClient
from app.main import create_app
from app.tracking.base import TrackedObject
from tests.conftest import FakeDirectory, FakeSource, MemoryPublisher, ScriptedDetector, ScriptedPoseEstimator, camera, rect_zone, wait_for
from tests.garment_helpers import FakeClassifier, FakeEmbedder
from tests.poses import person

CAM1 = "11111111-1111-4111-8111-111111111111"
CAM2 = "22222222-2222-4222-8222-222222222222"
FAST = InferenceConfig(enabled=True, inference_fps=4, min_state_seconds=0.5, unknown_after_seconds=1.0, report_seconds=1.0, reset_after_seconds=2.0)
FRAME = np.zeros((480, 640, 3), np.uint8)


def track(tid: int, x: float = 200):  # noqa: ANN201
    box, k = person("SITTING", x, 60, 240)
    return TrackedObject(tid, BBox(*box), 0.9, 0), PoseDetection(BBox(*box), 0.9, k)


def run(svc: GarmentActivityService, seconds: float, tids=(1,), dt: float = 0.25, t0: float = 100.0, poses: bool = True):  # noqa: ANN001
    ups = []
    t = t0
    while t < t0 + seconds:
        objs = [track(i, 150 + 200 * n) for n, i in enumerate(tids)]
        ups += svc.step_frame(FRAME, [o for o, _ in objs], set(tids), {o.track_id: p for o, p in objs} if poses else {}, t,
                              {o.track_id: {"workstationCode": "WS-7", "lineId": "L1"} for o, _ in objs})
        t += dt
    return ups


def probs(label_index: int, n: int = 3, p: float = 0.9):  # noqa: ANN201
    v = np.full(n, (1 - p) / (n - 1))
    v[label_index] = p
    return lambda x, m: np.tile(v, (len(x), 1))


def test_a_stable_prediction_becomes_a_state_after_the_minimum_duration():
    svc = GarmentActivityService("cam-1", FakeClassifier(rule=probs(0)), FakeEmbedder(0, "none"), FAST)
    ups = run(svc, 4.0)
    kinds = [(u.kind, str(u.activity)) for u in ups]
    assert kinds[0] == ("STATE_CHANGED", "SEWING") and ("CLASSIFIED", "SEWING") in kinds
    first = ups[0]
    assert first.prediction.window_end - first.prediction.window_start == pytest.approx(1.75)   # T-1 steps of the 2 s / 4 fps clip
    assert svc.context_for(1) == {"workstationCode": "WS-7", "lineId": "L1"}                     # kept beside, not inside, the prediction
    m = svc.metrics.snapshot()
    assert m["inferences"] > 5 and m["predictions"]["SEWING"] > 5 and m["unknown_rate"] == 0


def test_low_confidence_and_missing_pose_give_unknown_never_a_forced_label():
    low = GarmentActivityService("cam-1", FakeClassifier(rule=probs(0, p=0.45)), FakeEmbedder(0, "none"), FAST)
    ups = run(low, 4.0)
    assert [str(u.activity) for u in ups if u.kind != "CLASSIFIED"] == ["UNKNOWN"] and ups[0].kind == "UNCERTAIN"
    assert ups[0].prediction.reason == "LOW_CONFIDENCE" and ups[0].prediction.candidate == "SEWING"
    nopose = GarmentActivityService("cam-1", FakeClassifier(rule=probs(0)), FakeEmbedder(0, "none"), FAST)
    ups = run(nopose, 4.0, poses=False)
    assert ups and all(str(u.activity) == "UNKNOWN" and u.prediction.reason == "NO_POSE" for u in ups)


def test_single_odd_predictions_do_not_flip_the_state():
    calls = {"n": 0}

    def flicker(x, m):  # noqa: ANN001, ANN202
        calls["n"] += 1
        return np.tile(np.array([0.05, 0.9, 0.05]) if calls["n"] == 12 else np.array([0.9, 0.05, 0.05]), (len(x), 1))

    svc = GarmentActivityService("cam-1", FakeClassifier(rule=flicker), FakeEmbedder(0, "none"), FAST)
    ups = run(svc, 6.0)
    assert {str(u.activity) for u in ups} == {"SEWING"}


def test_tracks_and_cameras_are_isolated():
    a = GarmentActivityService("cam-A", FakeClassifier(rule=probs(0)), FakeEmbedder(0, "none"), FAST)
    b = GarmentActivityService("cam-B", FakeClassifier(rule=probs(1)), FakeEmbedder(0, "none"), FAST)
    run(a, 3.0, tids=(5, 6))
    run(b, 3.0, tids=(5,))
    assert set(a.buffers) == {("cam-A", 5), ("cam-A", 6)} and set(b.buffers) == {("cam-B", 5)}
    assert str(a.states.get("cam-A", 5).current) == "SEWING" and str(b.states.get("cam-B", 5).current) == "FABRIC_HANDLING"
    a.track_lost(5)
    assert ("cam-A", 5) not in a.buffers and a.states.get("cam-A", 5) is None and ("cam-A", 6) in a.buffers


def test_embeddings_are_computed_once_per_observation_and_images_are_not_kept():
    emb = FakeEmbedder(576)
    svc = GarmentActivityService("cam-1", FakeClassifier(rule=probs(0), backbone="mobilenet_v3_small"), emb, FAST)
    run(svc, 2.0, tids=(1, 2))
    assert emb.calls == 8                                                # one batch per sample (4/s × 2 s), both tracks together
    assert all(d.crop is None and d.embedding is not None for buf in svc.buffers.values() for d in buf)


def test_model_failures_switch_garment_off_for_this_camera_only():
    svc = GarmentActivityService("cam-1", FakeClassifier(fail=True), FakeEmbedder(0, "none"), FAST)
    assert run(svc, 4.0) == []
    assert not svc.active and "consecutive errors" in svc.disabled_reason and svc.metrics.errors == 5


# ─── Events ───────────────────────────────────────────────────────────────

CTX = EventContext("cam-1", "CAM-001", "fac-1", "a1b2c3", "yolo11n", "ultralytics-8.3.253", "cpu")
MODEL = FakeClassifier().summary()


def test_garment_events_carry_the_model_and_keep_context_separate():
    svc = GarmentActivityService("cam-1", FakeClassifier(rule=probs(0)), FakeEmbedder(0, "none"), FAST)
    u = run(svc, 2.0)[0]
    e = EventBuilder(CTX).garment(u, 1700000003.0, 640, 480, MODEL, {"workstationCode": "WS-7", "lineId": "L1", "machineId": None}, 7.9)
    w = e.to_wire()
    assert w["eventType"] == "GARMENT_ACTIVITY_CHANGED" and w["modelName"] == "garment-test" and w["modelVersion"] == "0.0-test"
    p = w["payload"]
    assert p["activity"] == "SEWING" and p["model"]["datasetVersion"] == "ds-test" and p["model"]["evaluation"]["status"] == "EVALUATED"
    assert p["context"] == {"workstationCode": "WS-7", "lineId": "L1", "note": p["context"]["note"]} and "not evidence" in p["context"]["note"]
    assert "zoneId" not in w and "workerId" not in json.dumps(w) and p["observationStart"] < p["observationEnd"]


def gbase(**kw):  # noqa: ANN003, ANN201
    d = dict(event_type="GARMENT_ACTIVITY_CHANGED", factory_id="f", idempotency_key="cv:x:1", occurred_at=datetime.now(timezone.utc), camera_id="c",
             track_id="t-1", confidence=0.8, model_name="garment-cnn-gru", model_version="0.1.0", payload={"activity": "SEWING"})
    return MonitoringEvent(**(d | kw))


@pytest.mark.parametrize("kw", [
    {"payload": {"activity": "sewing"}},                                  # labels are upper snake case
    {"payload": {"activity": "UNKNOWN"}},                                 # a change to UNKNOWN is GARMENT_ACTIVITY_UNCERTAIN
    {"event_type": "GARMENT_ACTIVITY_UNCERTAIN", "payload": {"activity": "SEWING"}},
    {"model_version": None},                                              # a garment prediction names its trained model
    {"track_id": None},
    {"confidence": None},
    {"payload": {"activity": "SEWING", "crop": "base64…"}},             # no images
    {"payload": {"activity": "SEWING", "workerId": "W-1"}},              # no identities
])
def test_invalid_garment_events_are_refused(kw):
    with pytest.raises(ValidationError):
        gbase(**kw)


def test_factory_specific_labels_pass_and_uncertain_may_omit_confidence():
    assert gbase(payload={"activity": "BUTTON_ATTACHING"}).payload["activity"] == "BUTTON_ATTACHING"
    assert gbase(event_type="GARMENT_ACTIVITY_UNCERTAIN", confidence=None, payload={"activity": "UNKNOWN"}).confidence is None


def test_garment_events_are_published_to_the_ingestion_api():
    seen = []
    client = BackendClient("http://backend", "k", transport=httpx.MockTransport(lambda r: seen.append(json.loads(r.content)) or httpx.Response(201, json={})))
    assert HttpEventPublisher(client.post_event).deliver(gbase()) == "delivered"
    assert seen[0]["eventType"] == "GARMENT_ACTIVITY_CHANGED" and seen[0]["modelVersion"] == "0.1.0"


# ─── Sessions ─────────────────────────────────────────────────────────────

ACT = ActivityConfig(enabled=True, process_fps=20, window_seconds=1.0, min_window_seconds=0.3, min_observations=3, max_gap_seconds=1.0,
                     reset_after_seconds=1.0, min_state_seconds=0.3, unknown_after_seconds=0.6, report_seconds=0.8)


def scene(frames: int):  # noqa: ANN201
    det, pose = {}, {}
    for i in range(frames):
        box, k = person("SITTING", 200, 60, 240, seed=i)
        det[i] = [(*box, 0.9)]
        pose[i] = [PoseDetection(BBox(*box), 0.9, k)]
    return det, pose


def manager(settings, cams, sources, garment_cfg, clf, frames=120):  # noqa: ANN001, ANN201
    det, pose = scene(frames)
    pub = MemoryPublisher()
    m = SessionManager(settings, FakeDirectory(cams), ScriptedDetector(det), pub, "cpu", source_factory=lambda c, o: sources[c.id],
                       pose_estimator=ScriptedPoseEstimator(pose), activity_config=ACT)
    m.garment_config = garment_cfg
    m._garment_models[str((settings.data_dir / "model").resolve())] = clf          # stands in for a loaded, evaluated model
    m._embedders["none"] = FakeEmbedder(0, "none")
    return m, pub


def test_a_camera_session_publishes_garment_events_alongside_generic_activity(settings):
    cfg = InferenceConfig(enabled=True, default_model=str(settings.data_dir / "model"), cameras={"cam-001": __import__("app.garment_activities.config",
                          fromlist=["CameraModel"]).CameraModel()}, inference_fps=4, min_state_seconds=0.5, unknown_after_seconds=1.0, report_seconds=1.0)
    zone = rect_zone("z-1", "WS-7", 0.0, 0.0, 1.0, 1.0)
    zone["workstationCode"] = "WS-7"
    cams = {CAM1: camera(CAM1, zones=[zone]), CAM2: camera(CAM2, code="CAM-002")}
    sources = {CAM1: FakeSource(120, delay=0.03), CAM2: FakeSource(120, delay=0.03)}
    m, pub = manager(settings, cams, sources, cfg, FakeClassifier(rule=probs(0)))
    s1, s2 = m.start(CAM1), m.start(CAM2)
    assert s1["garmentEnabled"] and s1["garmentModel"]["evaluation"] == "EVALUATED" and s1["activityEnabled"]
    assert not s2["garmentEnabled"]                                                  # not configured for CAM-002
    assert wait_for(lambda: all(m.get(c).state.value == "FINISHED" for c in cams), 20)
    garment = [e for e in pub.events if e.event_type in GARMENT_EVENT_TYPES]
    assert garment and {e.camera_id for e in garment} == {CAM1}
    assert garment[0].event_type == "GARMENT_ACTIVITY_CHANGED" and garment[0].payload["activity"] == "SEWING"
    assert garment[0].payload["context"]["workstationCode"] == "WS-7" and garment[0].payload["context"]["zoneName"] == "WS-7"
    detected = next(e for e in pub.events if e.event_type == "PERSON_DETECTED" and e.camera_id == CAM1)
    assert {e.track_id for e in garment} == {detected.track_id}
    assert any(e.event_type.startswith("ACTIVITY_") and e.camera_id == CAM1 for e in pub.events)   # generic posture still reported
    assert any(e.event_type == "MODEL_STATUS_CHANGED" and e.payload["model"] == "garment-test" for e in pub.events)


def test_garment_needs_a_model_and_the_pose_stage_but_never_blocks_detection(settings):
    cfg = InferenceConfig(enabled=True, default_model=str(settings.data_dir / "missing-model"))
    cams = {CAM1: camera(CAM1)}
    m, _ = manager(settings, cams, {CAM1: FakeSource(5)}, cfg, FakeClassifier())
    status = m.start(CAM1)                                                            # configured default: degrades, detection starts
    assert status["garmentEnabled"] is False and status["state"] in ("STARTING", "RUNNING", "FINISHED")
    m.shutdown(2)
    m2, _ = manager(settings, cams, {CAM1: FakeSource(5)}, cfg, FakeClassifier())
    with pytest.raises(ModelNotReady) as e:
        m2.start(CAM1, StartOptions(garment=True))                                   # explicitly asked: the reason is returned
    assert e.value.code == "garment_model_unavailable"
    m3, _ = manager(settings, cams, {CAM1: FakeSource(5)}, InferenceConfig(enabled=True, default_model=str(settings.data_dir / "model")), FakeClassifier())
    with pytest.raises(ModelNotReady) as e:
        m3.start(CAM1, StartOptions(activity=False, garment=True))
    assert e.value.code == "garment_needs_pose"


def test_inference_config_per_camera_and_files(tmp_path):
    f = tmp_path / "g.json"
    f.write_text(json.dumps({"note": "x", "default_model": "models/a", "cameras": {"CAM-001": {"enabled": True}, "CAM-002": {"model": "models/b"},
                                                                                    "CAM-003": {"enabled": False}}, "inference_fps": 0.5,
                             "decision": {"min_confidence": 0.7}}))
    c = InferenceConfig.load(f)
    c = c.__class__(**{**c.__dict__, "enabled": True})
    assert c.camera("x", "CAM-001").model == "models/a" and c.camera("x", "CAM-002").model == "models/b"
    assert c.camera("x", "CAM-003") is None and c.camera("x", "CAM-009") is None and c.decision.min_confidence == 0.7
    f.write_text(json.dumps({"inference_fps": 1, "typo_setting": 1}))
    with pytest.raises(ValueError, match="typo_setting"):
        InferenceConfig.load(f)
    assert InferenceConfig.load(tmp_path / "absent.json").enabled is False


def test_garment_api(settings):
    det, pose = scene(3000)
    settings.enable_preview = True
    app = create_app(settings, detector=ScriptedDetector(det), directory=FakeDirectory({CAM1: camera(CAM1)}), publisher=MemoryPublisher(),
                     source_factory=lambda cfg, opts: FakeSource(3000, live=True, delay=0.01), pose_estimator=ScriptedPoseEstimator(pose))
    m = app.state.manager
    m.activity_config = ACT
    m.garment_config = InferenceConfig(enabled=False, default_model=str(settings.data_dir / "model"), inference_fps=4, min_state_seconds=0.5,
                                       unknown_after_seconds=1.0, report_seconds=1.0)
    m._garment_models[str((settings.data_dir / "model").resolve())] = FakeClassifier(rule=probs(0))
    m._embedders["none"] = FakeEmbedder(0, "none")
    with TestClient(app) as c:
        c.headers.update({"X-API-Key": settings.api_key})
        r = c.post(f"/cameras/{CAM1}/start", json={"activity": True, "garment": True})
        assert r.status_code == 202 and r.json()["garmentEnabled"] is True
        assert wait_for(lambda: any(t["activity"] == "SEWING" for t in c.get(f"/cameras/{CAM1}/garment").json()["tracks"]), 10)
        body = c.get(f"/cameras/{CAM1}/garment").json()
        assert body["model"]["name"] == "garment-test" and body["tracks"][0]["context"] is not None and "keypoints" not in str(body)
        assert c.get(f"/cameras/{CAM1}/metrics").json()["garment"]["inferences"] > 0
        assert c.get(f"/cameras/{CAM1}/preview.jpg").status_code == 200
        assert "garment" in c.get("/health/model").json()
        c.post(f"/cameras/{CAM1}/stop")
