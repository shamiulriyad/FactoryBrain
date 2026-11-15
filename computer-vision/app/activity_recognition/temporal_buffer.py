"""Recent observations per (camera, track), so activity is judged over time, not from one frame.

* Keyed by ``(camera_id, track_id)``: observations of different cameras or
  tracks can never mix, even if two cameras' trackers use the same number.
* Keeps ``window_seconds`` of history (by timestamp, not frame count), so a
  slower processing rate means fewer samples, not a longer window.
* Missing frames are fine (movement is computed from timestamps); a gap
  longer than ``max_gap_seconds`` restarts the track's window, because motion
  across a long gap can't be trusted.
* A track not seen for ``reset_after_seconds`` is dropped.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass, field

from app.activity_recognition.schemas import PoseObservation

Key = tuple[str, int]


@dataclass
class BufferStats:
    gap_resets: int = 0
    expired: int = 0
    rejected: int = 0


@dataclass
class TemporalBuffer:
    window_seconds: float = 3.0
    max_gap_seconds: float = 1.5
    reset_after_seconds: float = 3.0
    max_observations: int = 256
    _data: dict[Key, deque] = field(default_factory=dict)
    stats: BufferStats = field(default_factory=BufferStats)

    def add(self, obs: PoseObservation) -> Key:
        key = (obs.camera_id, obs.track_id)
        buf = self._data.get(key)
        if buf is None:
            buf = self._data[key] = deque(maxlen=self.max_observations)
        if buf:
            last = buf[-1].timestamp
            if obs.timestamp <= last:              # out of order or duplicate: ignore
                self.stats.rejected += 1
                return key
            if obs.timestamp - last > self.max_gap_seconds:
                buf.clear()
                self.stats.gap_resets += 1
        buf.append(obs)
        cutoff = obs.timestamp - self.window_seconds
        while buf and buf[0].timestamp < cutoff:
            buf.popleft()
        return key

    def window(self, camera_id: str, track_id: int) -> list[PoseObservation]:
        return list(self._data.get((camera_id, track_id), ()))

    def expire(self, now: float) -> list[Key]:
        """Drop tracks not seen for ``reset_after_seconds``; returns their keys."""
        gone = [k for k, buf in self._data.items() if not buf or now - buf[-1].timestamp >= self.reset_after_seconds]
        for k in gone:
            del self._data[k]
        self.stats.expired += len(gone)
        return gone

    def drop(self, camera_id: str, track_id: int) -> None:
        self._data.pop((camera_id, track_id), None)

    def clear(self) -> None:
        self._data.clear()

    def keys(self) -> list[Key]:
        return list(self._data)

    def __len__(self) -> int:
        return len(self._data)
