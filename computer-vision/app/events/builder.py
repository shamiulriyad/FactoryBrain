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

from app.activity_recognition.schemas import ActivityUpdate
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
ACTIVITY_NOTE = ("Predicted physical posture or movement of a temporary camera track. "
                 "Not a worker identity, not a verified work activity and not a measure of productivity.")


GARMENT_NOTE = ("Garment activity predicted by a trained model for a temporary camera track. Not a worker identity, not verified work, "
                "not a productivity measure; workstation context is shown beside the prediction, not used as evidence.")


def _iso(ts: float) -> str:
    return datetime.fromtimestamp(ts, tz=timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


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
        kw.setdefault("model_name", self.ctx.model_name)
        kw.setdefault("model_version", self.ctx.model_version)
        return MonitoringEvent(
            event_type=event_type, factory_id=self.ctx.factory_id, idempotency_key=self._key(),
            occurred_at=datetime.fromtimestamp(ts, tz=timezone.utc), camera_id=self.ctx.camera_id, payload=payload, **kw)

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

    def model_status(self, to_status: str, ts: float, reason: str, model_name: str | None = None, model_version: str | None = None) -> MonitoringEvent:
        name = model_name or self.ctx.model_name
        return self._base("MODEL_STATUS_CHANGED", ts, model_name=name, model_version=model_version or self.ctx.model_version,
                          payload={"toStatus": to_status, "model": name, "reason": reason})

    def activity(self, u: ActivityUpdate, ts: float, frame_w: int, frame_h: int, classifier: tuple[str, str], pose_model: tuple[str, str],
                 fps: float) -> MonitoringEvent:
        """ACTIVITY_STATE_CHANGED / ACTIVITY_UNCERTAIN / ACTIVITY_CLASSIFIED from a state-manager update.

        Carries the prediction's observation window, confidence, scores and
        quality, never keypoints or images, and never a worker id.
        """
        p = u.prediction
        payload: dict = {
            "activity": u.activity.value,
            "observationStart": _iso(p.window_start),
            "observationEnd": _iso(p.window_end),
            "predictedAt": _iso(ts),
            "stateSince": _iso(u.state_since),
            "uncertain": u.activity.value == "UNKNOWN",
            "scores": p.scores,
            "quality": p.quality.as_dict(),
            "classifier": {"name": classifier[0], "version": classifier[1]},
            "poseModel": {"name": pose_model[0], "version": pose_model[1]},
            "detector": self.ctx.model_name,
            "processingFps": round(fps, 2),
            "note": ACTIVITY_NOTE,
        }
        if u.previous is not None:
            payload["previousActivity"] = u.previous.value
        if u.previous_duration is not None:
            payload["previousDurationSeconds"] = round(u.previous_duration, 1)
        if p.reason is not None and u.activity.value == "UNKNOWN":
            payload["reason"] = p.reason.value
            if p.candidate is not None:
                payload["candidate"] = p.candidate.value
        if u.bbox is not None:
            payload["bbox"] = u.bbox.normalised(frame_w, frame_h)
        event_type = {"STATE_CHANGED": "ACTIVITY_STATE_CHANGED", "UNCERTAIN": "ACTIVITY_UNCERTAIN", "CLASSIFIED": "ACTIVITY_CLASSIFIED"}[u.kind]
        return self._base(event_type, ts, track_id=self.track_id(u.track_id), confidence=round(min(1.0, max(0.0, u.confidence)), 4),
                          model_name=classifier[0], model_version=classifier[1], payload=payload)

    def garment(self, u: ActivityUpdate, ts: float, frame_w: int, frame_h: int, model: dict, context: dict | None, fps: float) -> MonitoringEvent:
        """GARMENT_ACTIVITY_CHANGED / _UNCERTAIN / _CLASSIFIED from a garment state update (model metadata and context kept separate)."""
        p = u.prediction
        activity = str(getattr(u.activity, "value", u.activity))
        previous = None if u.previous is None else str(getattr(u.previous, "value", u.previous))
        ev = model.get("evaluation") or {}
        payload: dict = {
            "activity": activity, "observationStart": _iso(p.window_start), "observationEnd": _iso(p.window_end), "predictedAt": _iso(ts),
            "stateSince": _iso(u.state_since), "uncertain": activity == "UNKNOWN", "scores": p.scores,
            "quality": {k: v for k, v in p.quality.items() if k in ("frames", "personRatio", "poseRatio", "margin")},
            "model": {"name": model["name"], "version": model["version"], "datasetVersion": model.get("datasetVersion"),
                      "architecture": model.get("architecture"), "evaluation": {k: ev.get(k) for k in ("status", "macroF1", "evaluatedAt") if k in ev}},
            "processingFps": round(fps, 2), "note": GARMENT_NOTE,
        }
        if previous is not None:
            payload["previousActivity"] = previous
        if u.previous_duration is not None:
            payload["previousDurationSeconds"] = round(u.previous_duration, 1)
        if activity == "UNKNOWN" and p.reason:
            payload["reason"] = p.reason
            if p.candidate:
                payload["candidate"] = p.candidate
        if context:
            payload["context"] = {k: v for k, v in context.items() if v is not None} | {
                "note": "Where the person is (camera zone / workstation): context only, not evidence of the activity."}
        if u.bbox is not None:
            payload["bbox"] = u.bbox.normalised(frame_w, frame_h)
        event_type = {"STATE_CHANGED": "GARMENT_ACTIVITY_CHANGED", "UNCERTAIN": "GARMENT_ACTIVITY_UNCERTAIN", "CLASSIFIED": "GARMENT_ACTIVITY_CLASSIFIED"}[u.kind]
        # No zoneId on the event: zone context is informational here (ZONE_* events report presence in zones).
        return self._base(event_type, ts, track_id=self.track_id(u.track_id),
                          confidence=round(min(1.0, max(0.0, u.confidence)), 4), model_name=model["name"][:80], model_version=str(model["version"])[:40],
                          payload=payload)


def new_session_tag() -> str:
    return uuid.uuid4().hex[:6]
