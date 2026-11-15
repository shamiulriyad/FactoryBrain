"""Development preview: boxes, temporary track ids, confidence, zones, FPS and (with activity
recognition) pose keypoints and the predicted activity, drawn on a frame.

Only for developers checking the models (an OpenCV window on the service's
machine, or the API-key-protected preview.jpg). Never sent to the monitoring
dashboard, which only receives structured events; keypoints never leave this
service except drawn on this preview.
"""
from __future__ import annotations

import cv2
import numpy as np

from app.activity_recognition.schemas import SKELETON, PoseDetection
from app.tracking.base import TrackedObject
from app.zones.types import Zone

_GREEN = (60, 200, 60)
_RED = (60, 60, 230)
_ZONE = (230, 180, 40)
_POSE = (255, 200, 0)
_UNKNOWN = (150, 150, 150)
_KP_MIN = 0.35


def draw_overlay(frame: np.ndarray, tracks: list[tuple[TrackedObject, Zone | None]], zones: list[Zone], camera_code: str, fps: float,
                 track_label, activity: dict | None = None, show_pose: bool = True,  # noqa: ANN001 - callable(int) -> str
                 activity_fps: float | None = None) -> np.ndarray:
    out = frame.copy()
    h, w = out.shape[:2]
    activity = activity or {}
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
        act, pose, garment = (tuple(activity.get(obj.track_id, ())) + (None, None, None))[:3]
        if show_pose and pose is not None:
            _draw_pose(out, pose)
        if act is not None:
            name, conf = act
            text = f"{name} {conf:.2f}" if name not in ("...", "UNKNOWN") else ("UNKNOWN" if name == "UNKNOWN" else "activity: observing")
            colour = _UNKNOWN if name in ("...", "UNKNOWN") else _POSE
            y = min(h - 6, p2[1] + 16)
            cv2.rectangle(out, (p1[0], y - 13), (p1[0] + 8 * len(text) + 6, y + 4), (0, 0, 0), -1)
            cv2.putText(out, text, (p1[0] + 3, y), cv2.FONT_HERSHEY_SIMPLEX, 0.5, colour, 1, cv2.LINE_AA)
        if garment is not None:
            gname, gconf = garment
            gtext = f"garment: {gname} {gconf:.2f}" if gname not in ("...", "UNKNOWN") else f"garment: {'UNKNOWN' if gname == 'UNKNOWN' else 'observing'}"
            y2 = min(h - 6, p2[1] + 34)
            cv2.rectangle(out, (p1[0], y2 - 13), (p1[0] + 8 * len(gtext) + 6, y2 + 4), (0, 0, 0), -1)
            cv2.putText(out, gtext, (p1[0] + 3, y2), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (80, 220, 255), 1, cv2.LINE_AA)
    cv2.rectangle(out, (0, 0), (w, 26), (0, 0, 0), -1)
    act_part = f" | activity {activity_fps:.1f}/s" if activity_fps is not None else ""
    cv2.putText(out, f"CV DEVELOPMENT PREVIEW | {camera_code} | {fps:.1f} fps{act_part} | track ids are temporary, not identities; activity is a prediction",
                (6, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (255, 255, 255), 1, cv2.LINE_AA)
    return out


def _draw_pose(out: np.ndarray, pose: PoseDetection) -> None:
    k = pose.keypoints
    for a, b in SKELETON:
        if k[a, 2] >= _KP_MIN and k[b, 2] >= _KP_MIN:
            cv2.line(out, (int(k[a, 0]), int(k[a, 1])), (int(k[b, 0]), int(k[b, 1])), _POSE, 2, cv2.LINE_AA)
    for x, y, c in k:
        if c >= _KP_MIN:
            cv2.circle(out, (int(x), int(y)), 3, (0, 0, 255), -1)
