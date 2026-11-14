"""Plane geometry for zone association (normalised coordinates)."""
from __future__ import annotations

from collections.abc import Sequence

Point = tuple[float, float]


def point_in_polygon(p: Point, polygon: Sequence[Point]) -> bool:
    """Even-odd ray casting. A point exactly on an edge counts as inside."""
    x, y = p
    n = len(polygon)
    if n < 3:
        return False
    inside = False
    for i in range(n):
        x1, y1 = polygon[i]
        x2, y2 = polygon[(i + 1) % n]
        if _on_segment(x, y, x1, y1, x2, y2):
            return True
        if (y1 > y) != (y2 > y):
            x_cross = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if x < x_cross:
                inside = not inside
    return inside


def _on_segment(x: float, y: float, x1: float, y1: float, x2: float, y2: float, eps: float = 1e-9) -> bool:
    cross = (x - x1) * (y2 - y1) - (y - y1) * (x2 - x1)
    if abs(cross) > eps:
        return False
    return min(x1, x2) - eps <= x <= max(x1, x2) + eps and min(y1, y2) - eps <= y <= max(y1, y2) + eps


def polygon_area(polygon: Sequence[Point]) -> float:
    """Shoelace formula (absolute value)."""
    n = len(polygon)
    s = 0.0
    for i in range(n):
        x1, y1 = polygon[i]
        x2, y2 = polygon[(i + 1) % n]
        s += x1 * y2 - x2 * y1
    return abs(s) / 2.0
