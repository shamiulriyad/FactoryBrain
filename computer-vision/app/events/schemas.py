"""The monitoring event contract of the ASP.NET Core backend (POST /api/monitoring/events).

Validated here before sending, with the same rules the backend applies, so a
bug in this service shows up as a local error instead of a stream of 400s.
"""
from __future__ import annotations

import re
from datetime import datetime, timezone
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator
from pydantic.alias_generators import to_camel

EventType = Literal["PERSON_DETECTED", "TRACK_UPDATED", "PERSON_LOST", "ZONE_ENTERED", "ZONE_EXITED",
                    "UNCERTAIN_DETECTION", "CAMERA_STATUS_CHANGED", "MODEL_STATUS_CHANGED"]

_KEY = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")
_TRACK = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$")
_SECRET_PARTS = ("password", "passwd", "secret", "token", "apikey", "api_key", "credential", "authorization")
_IDENTITY_KEYS = {"workerid", "worker_id", "employeeid", "employee_id", "employeecode", "workercode", "worker_code", "faceid", "face_id",
                  "faceembedding", "face_embedding", "embedding", "personname", "person_name", "workername", "worker_name"}
PAYLOAD_MAX_BYTES = 8 * 1024


def _check_payload(value: Any, depth: int = 1) -> None:
    if depth > 5:
        raise ValueError("payload is nested more than 5 levels deep")
    if isinstance(value, dict):
        for k, v in value.items():
            key = str(k).lower()
            if any(part in key for part in _SECRET_PARTS):
                raise ValueError(f"payload can't carry secrets ('{k}')")
            if key.replace("-", "_") in _IDENTITY_KEYS or key.replace("-", "").replace("_", "") in _IDENTITY_KEYS:
                raise ValueError(f"payload can't name a worker ('{k}')")
            _check_payload(v, depth + 1)
    elif isinstance(value, list):
        for item in value:
            _check_payload(item, depth + 1)


class MonitoringEvent(BaseModel):
    """One event, serialised in the backend's camelCase JSON. ``sourceType`` is always REAL; never a worker id."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, frozen=True)

    event_type: EventType
    factory_id: str
    idempotency_key: str
    occurred_at: datetime
    source_type: Literal["REAL"] = "REAL"
    camera_id: str
    zone_id: str | None = None
    track_id: str | None = None
    confidence: float | None = Field(None, ge=0, le=1)
    model_name: str | None = Field(None, max_length=80)
    model_version: str | None = Field(None, max_length=40)
    payload: dict[str, Any] = Field(default_factory=dict)

    @field_validator("idempotency_key")
    @classmethod
    def _key(cls, v: str) -> str:
        if not _KEY.match(v):
            raise ValueError("idempotency key: 1–128 letters, digits, '.', '_', ':', '/', '-'")
        return v

    @field_validator("track_id")
    @classmethod
    def _track(cls, v: str | None) -> str | None:
        if v is not None and not _TRACK.match(v):
            raise ValueError("track id: 1–64 letters, digits, '.', '_', ':', '-'")
        return v

    @field_validator("occurred_at")
    @classmethod
    def _utc(cls, v: datetime) -> datetime:
        return v.replace(tzinfo=timezone.utc) if v.tzinfo is None else v.astimezone(timezone.utc)

    @field_validator("payload")
    @classmethod
    def _payload(cls, v: dict[str, Any]) -> dict[str, Any]:
        import json

        if len(json.dumps(v, default=str).encode()) > PAYLOAD_MAX_BYTES:
            raise ValueError("payload is larger than 8 KB")
        _check_payload(v)
        return v

    def to_wire(self) -> dict[str, Any]:
        data = self.model_dump(by_alias=True, exclude_none=True, mode="json")
        return data
