"""Builds contract events from track transitions and status changes.

Idempotency keys are ``cv:<session>:<camera code>:<sequence>``: unique per
event, stable across retries of the same event (the key is fixed when the
event is built). Track ids are published as ``<session>-t<n>``: a temporary,
camera-local number that changes when the camera restarts. No event ever
carries a worker id.
"""
from __future__ import annotations

import itertools
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone

from app.events.schemas import MonitoringEvent
from app.tracking.base import TrackedObject
from app.tracking.lifecycle import Transition

_KIND_TO_TYPE = {
    "DETECTED": "PERSON_DETECTED",
    "UPDATED": "TRACK_UPDATED",
    "LOST": "PERSON_LOST",
    "ZONE_ENTERED": "ZONE_ENTERED",
    "ZONE_EXITED": "ZONE_EXITED",
}

IDENTITY_NOTE = "Track ids are temporary camera-local numbers, not worker identities."


@dataclass
class EventContext:
    camera_id: str
    camera_code: str
    factory_id: str
    session_tag: str
    model_name: str
    model_version: str
    device: str


class EventBuilder:
    def __init__(self, ctx: EventContext) -> None:
        self.ctx = ctx
        self._seq = itertools.count(1)

    def _key(self) -> str:
        return f"cv:{self.ctx.session_tag}:{self.ctx.camera_code}:{next(self._seq):06d}"

    def track_id(self, n: int) -> str:
        return f"{self.ctx.session_tag}-t{n}"

    def _base(self, event_type: str, ts: float, **kw) -> MonitoringEvent:  # noqa: ANN003
        payload = kw.pop("payload", {})
        payload.setdefault("cvEventId", str(uuid.uuid4()))
        payload.setdefault("producer", "computer-vision")
        payload.setdefault("device", self.ctx.device)
        return MonitoringEvent(
            event_type=event_type, factory_id=self.ctx.factory_id, idempotency_key=self._key(),
            occurred_at=datetime.fromtimestamp(ts, tz=timezone.utc), camera_id=self.ctx.camera_id,
            model_name=self.ctx.model_name, model_version=self.ctx.model_version, payload=payload, **kw)

    def from_transition(self, t: Transition, ts: float, frame_w: int, frame_h: int, frame_index: int, fps: float) -> MonitoringEvent:
        obj: TrackedObject = t.track
        bx, by = obj.bbox.bottom_centre()
        payload = {
            "bbox": obj.bbox.normalised(frame_w, frame_h),
            "bottomCentre": {"x": round(bx / frame_w, 4), "y": round(by / frame_h, 4)},
            "frame": {"index": frame_index, "width": frame_w, "height": frame_h},
            "processingFps": round(fps, 2),
            "note": IDENTITY_NOTE,
        }
        if t.zone is not None:
            payload["zone"] = {"name": t.zone.name, "type": t.zone.zone_type, "workstation": t.zone.workstation_code}
        if t.kind in ("UPDATED", "LOST"):
            payload["visibleSeconds"] = round(t.duration_seconds, 1)
        zone_id = t.zone.id if t.kind in ("ZONE_ENTERED", "ZONE_EXITED") and t.zone is not None else None
        return self._base(_KIND_TO_TYPE[t.kind], ts, track_id=self.track_id(obj.track_id), zone_id=zone_id,
                          confidence=round(min(1.0, max(0.0, obj.confidence)), 4), payload=payload)

    def camera_status(self, to_status: str, ts: float, reason: str, from_status: str | None = None) -> MonitoringEvent:
        payload = {"toStatus": to_status, "reason": reason}
        if from_status:
            payload["fromStatus"] = from_status
        return self._base("CAMERA_STATUS_CHANGED", ts, payload=payload)

    def model_status(self, to_status: str, ts: float, reason: str) -> MonitoringEvent:
        return self._base("MODEL_STATUS_CHANGED", ts, payload={"toStatus": to_status, "model": self.ctx.model_name, "reason": reason})


def new_session_tag() -> str:
    return uuid.uuid4().hex[:6]
