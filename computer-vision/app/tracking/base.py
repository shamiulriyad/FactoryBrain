"""The tracker contract, so another tracker (e.g. BoT-SORT) can replace ByteTrack later."""
from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

from app.detection.types import BBox, Detection


@dataclass(frozen=True)
class TrackedObject:
    """A detection with a temporary, camera-local track number. Never a worker identity."""

    track_id: int
    bbox: BBox
    confidence: float
    class_id: int


class Tracker(Protocol):
    name: str

    def update(self, detections: list[Detection], frame_w: int, frame_h: int) -> list[TrackedObject]:
        """Associate this frame's detections with existing tracks; return the tracks visible in this frame."""
        ...

    def reset(self) -> None: ...
