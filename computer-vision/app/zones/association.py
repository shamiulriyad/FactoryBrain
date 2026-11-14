"""Which zone a tracked person is standing in.

The person's position is the bottom-centre of their bounding box (where the
feet are), normalised to the frame. Overlapping zones are resolved by one
deterministic rule: the **smallest** zone containing the point wins (the most
specific place, e.g. a workstation inside a line area); equal areas are
ordered by name, then id. No zone → ``None`` ("unassigned").

Being inside a zone means only that a detected person is geographically in
that part of the picture. It is not an identity, an assignment, proof of
work, productivity or attendance.
"""
from __future__ import annotations

from app.detection.types import BBox
from app.zones.geometry import point_in_polygon, polygon_area
from app.zones.types import Zone


class ZoneAssociator:
    def __init__(self, zones: list[Zone]) -> None:
        valid = [z for z in zones if len(z.points) >= 3]
        # Pre-sorted once by the tie-break rule: smallest area, then name, then id.
        self._zones = sorted(valid, key=lambda z: (round(polygon_area(z.points), 9), z.name, z.id))

    @property
    def zones(self) -> list[Zone]:
        return list(self._zones)

    def zone_for_point(self, x: float, y: float) -> Zone | None:
        for z in self._zones:
            if point_in_polygon((x, y), z.points):
                return z
        return None

    def zone_for_box(self, box: BBox, frame_w: int, frame_h: int) -> Zone | None:
        bx, by = box.bottom_centre()
        return self.zone_for_point(bx / frame_w, by / frame_h)
