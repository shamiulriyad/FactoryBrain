"""What a detector produces: people, as pixel bounding boxes with a confidence."""
from __future__ import annotations

from dataclasses import dataclass

PERSON_CLASS_ID = 0          # COCO "person"
PERSON_CLASS_NAME = "person"


@dataclass(frozen=True)
class BBox:
    """Pixel box, top-left (x1, y1) to bottom-right (x2, y2)."""

    x1: float
    y1: float
    x2: float
    y2: float

    @property
    def width(self) -> float:
        return self.x2 - self.x1

    @property
    def height(self) -> float:
        return self.y2 - self.y1

    def is_valid(self, frame_w: int, frame_h: int) -> bool:
        return (self.width > 1 and self.height > 1 and self.x2 > 0 and self.y2 > 0
                and self.x1 < frame_w and self.y1 < frame_h)

    def clipped(self, frame_w: int, frame_h: int) -> "BBox":
        return BBox(max(0.0, self.x1), max(0.0, self.y1), min(float(frame_w), self.x2), min(float(frame_h), self.y2))

    def normalised(self, frame_w: int, frame_h: int) -> dict[str, float]:
        """``{x, y, w, h}`` as fractions of the frame (0–1), rounded for transport."""
        return {"x": round(self.x1 / frame_w, 4), "y": round(self.y1 / frame_h, 4),
                "w": round(self.width / frame_w, 4), "h": round(self.height / frame_h, 4)}

    def bottom_centre(self) -> tuple[float, float]:
        """Where the person stands: the middle of the box's bottom edge (pixels)."""
        return ((self.x1 + self.x2) / 2.0, self.y2)


@dataclass(frozen=True)
class Detection:
    bbox: BBox
    confidence: float
    class_id: int
    class_name: str
    camera_id: str
    frame_ts: float          # time.time() of the frame (UTC epoch seconds)


def keep_persons(rows: list[tuple[float, float, float, float, float, int]], names: dict[int, str], frame_w: int, frame_h: int,
                 min_confidence: float, camera_id: str, frame_ts: float) -> list[Detection]:
    """From raw ``(x1, y1, x2, y2, conf, cls)`` rows keep valid person boxes at or above the threshold, clipped to the frame."""
    out: list[Detection] = []
    for x1, y1, x2, y2, conf, cls in rows:
        if int(cls) != PERSON_CLASS_ID or conf < min_confidence:
            continue
        box = BBox(float(x1), float(y1), float(x2), float(y2))
        if not box.is_valid(frame_w, frame_h):
            continue
        out.append(Detection(box.clipped(frame_w, frame_h), round(float(conf), 4), PERSON_CLASS_ID,
                             names.get(PERSON_CLASS_ID, PERSON_CLASS_NAME), camera_id, frame_ts))
    return out
