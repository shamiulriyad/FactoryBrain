"""Activity events: schema rules, builder output, nothing identifying or raw, and delivery to the backend."""
import json
from datetime import datetime, timezone

import httpx
import pytest
from pydantic import ValidationError

from app.activity_recognition.schemas import ActivityLabel, ActivityPrediction, ActivityUpdate, UncertainReason, WindowQuality
from app.detection.types import BBox
from app.events.builder import EventBuilder, EventContext
from app.events.publisher import HttpEventPublisher
from app.events.schemas import MonitoringEvent
from app.integration.backend import BackendClient

CTX = EventContext("cam-1", "CAM-001", "fac-1", "a1b2c3", "yolo11n", "ultralytics-8.3.253", "cpu")
Q = WindowQuality(12, 11, 2.75, 0.88, 1.0, 0.61, 0.92)
CLASSIFIER = ("pose-temporal-baseline", "1.0")
POSE = ("yolo11n-pose", "ultralytics-8.3.253")


def update(kind: str, activity: str, previous: str | None = None, reason: UncertainReason | None = None) -> ActivityUpdate:
    lab = ActivityLabel(activity)
    p = ActivityPrediction(lab, 0.91 if reason is None else 0.42, ActivityLabel.WALKING, {"WALKING": 0.93, "STANDING": 0.1}, Q,
                           1700000000.0, 1700000002.75, reason)
    return ActivityUpdate(kind, "cam-1", 12, lab, ActivityLabel(previous) if previous else None, p.confidence, p, 1700000001.0,
                          31.24 if previous else None, BBox(64, 36, 128, 180))


def test_a_state_change_event_matches_the_contract():
    e = EventBuilder(CTX).activity(update("STATE_CHANGED", "WALKING", "STANDING"), 1700000003.0, 640, 360, CLASSIFIER, POSE, 7.9)
    w = e.to_wire()
    assert w["eventType"] == "ACTIVITY_STATE_CHANGED" and w["sourceType"] == "REAL"
    assert w["cameraId"] == "cam-1" and w["trackId"] == "a1b2c3-t12" and w["confidence"] == 0.91
    assert w["modelName"] == "pose-temporal-baseline" and w["modelVersion"] == "1.0"
    p = w["payload"]
    assert p["activity"] == "WALKING" and p["previousActivity"] == "STANDING" and p["previousDurationSeconds"] == 31.2
    assert p["observationStart"] == "2023-11-14T22:13:20.000Z" and p["observationEnd"] == "2023-11-14T22:13:22.750Z"
    assert p["predictedAt"] == "2023-11-14T22:13:23.000Z" and p["uncertain"] is False
    assert p["quality"]["poseObservations"] == 11 and p["poseModel"]["name"] == "yolo11n-pose"
    assert p["cvEventId"] and w["idempotencyKey"].startswith("cv:a1b2c3:CAM-001:")
    text = json.dumps(w).lower()
    assert "keypoint\"" not in text and "workerid" not in text and "employee" not in text


def test_uncertain_and_classified_events():
    b = EventBuilder(CTX)
    unc = b.activity(update("UNCERTAIN", "UNKNOWN", "SITTING", UncertainReason.LOW_CONFIDENCE), 1700000003.0, 640, 360, CLASSIFIER, POSE, 8)
    assert unc.event_type == "ACTIVITY_UNCERTAIN" and unc.payload["activity"] == "UNKNOWN"
    assert unc.payload["reason"] == "LOW_CONFIDENCE" and unc.payload["candidate"] == "WALKING" and unc.payload["uncertain"] is True
    cls = b.activity(update("CLASSIFIED", "WALKING"), 1700000003.0, 640, 360, CLASSIFIER, POSE, 8)
    assert cls.event_type == "ACTIVITY_CLASSIFIED" and "previousActivity" not in cls.payload
    assert unc.idempotency_key != cls.idempotency_key


def base(**kw):  # noqa: ANN003
    d = dict(event_type="ACTIVITY_STATE_CHANGED", factory_id="f", idempotency_key="cv:x:1", occurred_at=datetime.now(timezone.utc),
             camera_id="c", track_id="t-1", confidence=0.8, payload={"activity": "SITTING"})
    return MonitoringEvent(**(d | kw))


@pytest.mark.parametrize("kw", [
    {"payload": {"activity": "SEWING"}},                                   # not something this service can recognise
    {"payload": {"activity": "SEWING_LIKE_MOTION"}},                       # reserved for a future trained model
    {"payload": {}},                                                       # no activity
    {"payload": {"activity": "UNKNOWN"}},                                  # a change to UNKNOWN is ACTIVITY_UNCERTAIN
    {"event_type": "ACTIVITY_UNCERTAIN", "payload": {"activity": "SITTING"}},
    {"track_id": None},                                                    # activity belongs to a track
    {"confidence": None},
    {"payload": {"activity": "SITTING", "keypoints": [[1, 2, 0.9]]}},     # raw keypoints never leave the service
    {"payload": {"activity": "SITTING", "workerId": "W-1"}},              # never an identity
])
def test_invalid_activity_events_are_refused_before_sending(kw):
    with pytest.raises(ValidationError):
        base(**kw)


def test_uncertain_events_may_omit_the_confidence():
    assert base(event_type="ACTIVITY_UNCERTAIN", confidence=None, payload={"activity": "UNKNOWN"}).confidence is None


def test_activity_events_are_published_to_the_ingestion_api():
    seen = []

    def handler(req: httpx.Request) -> httpx.Response:
        seen.append(json.loads(req.content))
        return httpx.Response(201, json={"outcome": "Accepted"})

    client = BackendClient("http://backend", "svc-key", transport=httpx.MockTransport(handler))
    pub = HttpEventPublisher(client.post_event)
    e = EventBuilder(CTX).activity(update("STATE_CHANGED", "WALKING", "STANDING"), 1700000003.0, 640, 360, CLASSIFIER, POSE, 8)
    assert pub.deliver(e) == "delivered"
    body = seen[0]
    assert body["eventType"] == "ACTIVITY_STATE_CHANGED" and body["payload"]["activity"] == "WALKING"
    assert body["idempotencyKey"] == e.idempotency_key and "workerId" not in body
