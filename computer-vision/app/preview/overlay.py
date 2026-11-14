"""Development preview: boxes, temporary track ids, confidence, zone and FPS drawn on a frame.

Only for developers checking the model (an OpenCV window on the service's
machine, or the API-key-protected preview.jpg). Never sent to the monitoring
dashboard, which only receives structured events.
"""
from __future__ import annotations

import cv2
import numpy as np

from app.tracking.base import TrackedObject
from app.zones.types import Zone

_GREEN = (60, 200, 60)
_RED = (60, 60, 230)
_ZONE = (230, 180, 40)


def draw_overlay(frame: np.ndarray, tracks: list[tuple[TrackedObject, Zone | None]], zones: list[Zone], camera_code: str, fps: float,
                 track_label) -> np.ndarray:  # noqa: ANN001 - callable(int) -> str
    out = frame.copy()
    h, w = out.shape[:2]
    for z in zones:
        pts = np.array([[int(x * w), int(y * h)] for x, y in z.points], dtype=np.int32)
        cv2.polylines(out, [pts], True, _RED if z.zone_type == "Restricted" else _ZONE, 2)
        cv2.putText(out, z.name, tuple(pts[0]), cv2.FONT_HERSHEY_SIMPLEX, 0.5, _ZONE, 1, cv2.LINE_AA)
    for obj, zone in tracks:
        b = obj.bbox
        p1, p2 = (int(b.x1), int(b.y1)), (int(b.x2), int(b.y2))
        cv2.rectangle(out, p1, p2, _GREEN, 2)
        label = f"{track_label(obj.track_id)} {obj.confidence:.2f}" + (f" [{zone.name}]" if zone else "")
        cv2.putText(out, label, (p1[0], max(14, p1[1] - 6)), cv2.FONT_HERSHEY_SIMPLEX, 0.5, _GREEN, 1, cv2.LINE_AA)
        bx, by = b.bottom_centre()
        cv2.circle(out, (int(bx), int(by)), 4, _GREEN, -1)
    cv2.rectangle(out, (0, 0), (w, 26), (0, 0, 0), -1)
    cv2.putText(out, f"CV DEVELOPMENT PREVIEW | {camera_code} | {fps:.1f} fps | track ids are temporary, not identities",
                (6, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1, cv2.LINE_AA)
    return out
