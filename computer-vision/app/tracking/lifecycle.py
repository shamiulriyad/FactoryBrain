"""Turns per-frame tracks into a story: detected → (updated) → lost, and zone changes.

Rules (all configurable):
* a track is *detected* once it has been seen in ``min_hits`` frames
  (so one-frame flickers never become events);
* it is *updated* at most every ``update_seconds`` while visible;
* it is *lost* when not seen for ``lost_after_seconds`` (a short occlusion
  inside that window keeps the same track and produces nothing);
* it *enters* / *exits* a zone when its zone has been different for
  ``zone_debounce_frames`` consecutive frames.

Pure and clock-driven (``now`` is passed in), so it is tested without video.
"""
from __future__ import annotations

from dataclasses import dataclass, field

from app.tracking.base import TrackedObject
from app.zones.types import Zone


@dataclass
class TrackState:
    track_id: int
    first_seen: float
    last_seen: float
    hits: int = 0
    confirmed: bool = False
    last_published_update: float = 0.0
    zone: Zone | None = None
    pending_zone: Zone | None = None
    pending_count: int = 0
    last: TrackedObject | None = None


@dataclass
class Transition:
    kind: str                      # DETECTED | UPDATED | LOST | ZONE_ENTERED | ZONE_EXITED
    track: TrackedObject
    zone: Zone | None = None
    duration_seconds: float = 0.0


@dataclass
class TrackLifecycle:
    min_hits: int = 2
    lost_after_seconds: float = 3.0
    update_seconds: float = 10.0
    zone_debounce_frames: int = 2
    tracks: dict[int, TrackState] = field(default_factory=dict)

    def step(self, visible: list[tuple[TrackedObject, Zone | None]], now: float) -> list[Transition]:
        out: list[Transition] = []
        seen: set[int] = set()
        for obj, zone in visible:
            seen.add(obj.track_id)
            st = self.tracks.get(obj.track_id)
            if st is None:
                st = self.tracks[obj.track_id] = TrackState(obj.track_id, now, now)
            st.hits += 1
            st.last_seen = now
            st.last = obj
            if not st.confirmed:
                if st.hits >= self.min_hits:
                    st.confirmed = True
                    st.last_published_update = now
                    st.zone = zone
                    out.append(Transition("DETECTED", obj, zone))
                    if zone is not None:
                        out.append(Transition("ZONE_ENTERED", obj, zone))
                continue
            out.extend(self._zone_change(st, obj, zone))
            if now - st.last_published_update >= self.update_seconds:
                st.last_published_update = now
                out.append(Transition("UPDATED", obj, st.zone, now - st.first_seen))
        for track_id in [t for t in self.tracks if t not in seen]:
            st = self.tracks[track_id]
            if now - st.last_seen >= self.lost_after_seconds:
                del self.tracks[track_id]
                if st.confirmed and st.last is not None:
                    if st.zone is not None:
                        out.append(Transition("ZONE_EXITED", st.last, st.zone))
                    out.append(Transition("LOST", st.last, st.zone, st.last_seen - st.first_seen))
        return out

    def _zone_change(self, st: TrackState, obj: TrackedObject, zone: Zone | None) -> list[Transition]:
        if _same(zone, st.zone):
            st.pending_zone, st.pending_count = None, 0
            return []
        if _same(zone, st.pending_zone) and st.pending_count > 0:
            st.pending_count += 1
        else:
            st.pending_zone, st.pending_count = zone, 1
        if st.pending_count < self.zone_debounce_frames:
            return []
        out: list[Transition] = []
        if st.zone is not None:
            out.append(Transition("ZONE_EXITED", obj, st.zone))
        if zone is not None:
            out.append(Transition("ZONE_ENTERED", obj, zone))
        st.zone, st.pending_zone, st.pending_count = zone, None, 0
        return out

    def flush(self) -> list[Transition]:
        """Everything still visible is lost (the camera stopped)."""
        out: list[Transition] = []
        for st in self.tracks.values():
            if st.confirmed and st.last is not None:
                if st.zone is not None:
                    out.append(Transition("ZONE_EXITED", st.last, st.zone))
                out.append(Transition("LOST", st.last, st.zone, st.last_seen - st.first_seen))
        self.tracks.clear()
        return out

    @property
    def active(self) -> int:
        return sum(1 for t in self.tracks.values() if t.confirmed)


def _same(a: Zone | None, b: Zone | None) -> bool:
    return (a is None and b is None) or (a is not None and b is not None and a.id == b.id)
