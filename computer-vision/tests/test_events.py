"""Event schema and builder: the backend contract, REAL only, no identities, unique idempotency keys."""
import re
from datetime import datetime, timezone

import pytest
from pydantic import ValidationError

from app.detection.types import BBox
from app.events.builder import EventBuilder, EventContext
from app.events.schemas import MonitoringEvent
from app.tracking.base import TrackedObject
from app.tracking.lifecycle import Transition
from app.zones.types import Zone

CTX = EventContext("cam-1", "CAM-001", "fac-1", "a1b2c3", "yolo11n", "ultralytics-8.3.253", "cpu")


def ev(**kw):
    base = dict(event_type="PERSON_DETECTED", factory_id="f", idempotency_key="cv:x:1", occurred_at=datetime.now(timezone.utc), camera_id="c")
    return MonitoringEvent(**(base | kw))


def test_events_serialise_to_the_backend_contract():
    wire = ev(track_id="a1-t3", confidence=0.87, payload={"bbox": {"x": 0.1}}).to_wire()
    assert wire["eventType"] == "PERSON_DETECTED"
    assert wire["sourceType"] == "REAL"
    assert wire["trackId"] == "a1-t3"
    assert wire["occurredAt"].endswith("Z") or wire["occurredAt"].endswith("+00:00")
    assert "zoneId" not in wire                     # nulls are omitted


@pytest.mark.parametrize("kw", [
    {"track_id": "has space"},
    {"confidence": 1.4},
    {"idempotency_key": "-bad"},
    {"payload": {"workerId": "W-001"}},
    {"payload": {"camera": {"rtspPassword": "x"}}},
    {"payload": {"blob": "x" * 9000}},
    {"source_type": "SIMULATED"},
    {"event_type": "ACTIVITY_CLASSIFIED"},
])
def test_invalid_events_are_refused_before_sending(kw):
    with pytest.raises(ValidationError):
        ev(**kw)


def test_the_builder_makes_unique_keys_temporary_track_ids_and_zone_events():
    b = EventBuilder(CTX)
    zone = Zone("z-1", "T-025", "Workstation", ((0, 0), (1, 0), (1, 1)), "ws-1", "T-025")
    obj = TrackedObject(17, BBox(64, 36, 128, 180), 0.91, 0)
    detected = b.from_transition(Transition("DETECTED", obj, zone), 1700000000.0, 640, 360, 5, 4.8)
    entered = b.from_transition(Transition("ZONE_ENTERED", obj, zone), 1700000000.0, 640, 360, 5, 4.8)
    lost = b.from_transition(Transition("LOST", obj, zone, 12.34), 1700000010.0, 640, 360, 50, 4.8)
    keys = {e.idempotency_key for e in (detected, entered, lost)}
    assert len(keys) == 3 and all(re.match(r"^cv:a1b2c3:CAM-001:\d{6}$", k) for k in keys)
    assert detected.track_id == "a1b2c3-t17"
    assert detected.zone_id is None and entered.zone_id == "z-1"
    assert entered.event_type == "ZONE_ENTERED" and lost.event_type == "PERSON_LOST"
    assert detected.payload["bbox"] == {"x": 0.1, "y": 0.1, "w": 0.1, "h": 0.4}
    assert detected.payload["bottomCentre"] == {"x": 0.15, "y": 0.5}
    assert lost.payload["visibleSeconds"] == 12.3
    assert detected.model_name == "yolo11n" and detected.source_type == "REAL"
    for e in (detected, entered, lost):
        text = str(e.to_wire()).lower()
        assert "workerid" not in text and "worker_id" not in text


def test_camera_and_model_status_events():
    b = EventBuilder(CTX)
    off = b.camera_status("OFFLINE", 1700000000.0, "No frames", "ONLINE")
    assert off.payload["toStatus"] == "OFFLINE" and off.payload["fromStatus"] == "ONLINE"
    ready = b.model_status("READY", 1700000000.0, "loaded")
    assert ready.event_type == "MODEL_STATUS_CHANGED" and ready.payload["model"] == "yolo11n"
