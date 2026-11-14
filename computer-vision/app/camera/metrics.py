"""Per-camera processing figures: real processing FPS and latency, not configured values."""
from __future__ import annotations

import threading
import time
from collections import deque
from dataclasses import dataclass, field


@dataclass
class SessionMetrics:
    started_at: float = field(default_factory=time.time)
    frames_read: int = 0
    frames_processed: int = 0
    frames_skipped: int = 0
    read_failures: int = 0
    reconnects: int = 0
    events_emitted: int = 0
    detections_last_frame: int = 0
    active_tracks: int = 0
    last_frame_at: float | None = None
    last_latency_ms: float = 0.0
    avg_latency_ms: float = 0.0
    last_error: str | None = None
    _processed_times: deque = field(default_factory=lambda: deque(maxlen=60))
    _lock: threading.Lock = field(default_factory=threading.Lock)

    def read(self) -> None:
        with self._lock:
            self.frames_read += 1
            self.last_frame_at = time.time()

    def skipped(self) -> None:
        with self._lock:
            self.frames_skipped += 1

    def processed(self, latency_ms: float, detections: int, tracks: int) -> None:
        with self._lock:
            self.frames_processed += 1
            self.last_latency_ms = round(latency_ms, 2)
            n = min(self.frames_processed, 50)
            self.avg_latency_ms = round(self.avg_latency_ms + (latency_ms - self.avg_latency_ms) / n, 2)
            self.detections_last_frame = detections
            self.active_tracks = tracks
            self._processed_times.append(time.monotonic())

    @property
    def processing_fps(self) -> float:
        with self._lock:
            times = list(self._processed_times)
        if len(times) < 2:
            return 0.0
        span = times[-1] - times[0]
        return round((len(times) - 1) / span, 2) if span > 0 else 0.0

    def snapshot(self) -> dict:
        with self._lock:
            data = {k: v for k, v in self.__dict__.items() if not k.startswith("_")}
        data["processing_fps"] = self.processing_fps
        data["uptime_seconds"] = round(time.time() - self.started_at, 1)
        return data
