"""A camera zone as configured in the platform (read from the backend, never stored here)."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Zone:
    id: str
    name: str
    zone_type: str
    points: tuple[tuple[float, float], ...]   # normalised (0–1) frame coordinates
    workstation_id: str | None = None
    workstation_code: str | None = None
    line_id: str | None = None
