"""Delivers events to the backend without ever blocking video processing.

Camera threads only put events on a bounded queue. One worker thread posts
them: 2xx is delivered; a 4xx is a permanent rejection (logged with the
backend's code and dropped: retrying wouldn't help); network errors and 5xx
are retried with back-off up to ``max_attempts`` (the idempotency key makes
retries safe). A full queue drops the new event and counts it.
"""
from __future__ import annotations

import logging
import queue
import threading
import time
from dataclasses import asdict, dataclass
from typing import Protocol

import httpx

from app.events.schemas import MonitoringEvent

log = logging.getLogger(__name__)


@dataclass
class PublisherStats:
    queued: int = 0
    delivered: int = 0
    duplicates: int = 0
    rejected: int = 0
    failed: int = 0
    dropped_queue_full: int = 0
    retries: int = 0
    last_error: str | None = None
    last_delivered_at: float | None = None

    def as_dict(self) -> dict:
        return asdict(self)


class EventPublisher(Protocol):
    stats: PublisherStats

    def publish(self, event: MonitoringEvent) -> bool: ...
    def start(self) -> None: ...
    def close(self, timeout: float = 5.0) -> None: ...


class NullPublisher:
    """Publishing switched off (CV_PUBLISH_EVENTS=false): events are counted, not sent."""

    def __init__(self) -> None:
        self.stats = PublisherStats()

    def publish(self, event: MonitoringEvent) -> bool:
        self.stats.queued += 1
        return True

    def start(self) -> None: ...
    def close(self, timeout: float = 5.0) -> None: ...


class HttpEventPublisher:
    def __init__(self, post, queue_size: int = 1000, max_attempts: int = 5, backoff_seconds: float = 0.5) -> None:  # noqa: ANN001
        """``post(dict) -> httpx.Response`` sends one event (see BackendClient.post_event)."""
        self._post = post
        self._queue: queue.Queue[tuple[MonitoringEvent, int]] = queue.Queue(maxsize=queue_size)
        self._max_attempts = max_attempts
        self._backoff = backoff_seconds
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.stats = PublisherStats()

    def start(self) -> None:
        if self._thread is None or not self._thread.is_alive():
            self._stop.clear()
            self._thread = threading.Thread(target=self._run, name="cv-event-publisher", daemon=True)
            self._thread.start()

    def publish(self, event: MonitoringEvent) -> bool:
        try:
            self._queue.put_nowait((event, 1))
            self.stats.queued += 1
            return True
        except queue.Full:
            self.stats.dropped_queue_full += 1
            log.warning("Event queue full: %s dropped.", event.event_type)
            return False

    @property
    def pending(self) -> int:
        return self._queue.qsize()

    def _run(self) -> None:
        while not (self._stop.is_set() and self._queue.empty()):
            try:
                event, attempt = self._queue.get(timeout=0.2)
            except queue.Empty:
                continue
            try:
                self.deliver(event, attempt)
            finally:
                self._queue.task_done()

    def deliver(self, event: MonitoringEvent, attempt: int = 1) -> str:
        """Send one event now; returns delivered | duplicate | rejected | retry | failed."""
        while True:
            try:
                res = self._post(event.to_wire())
                if res.status_code in (200, 201):
                    self.stats.last_delivered_at = time.time()
                    outcome = "duplicate" if res.status_code == 200 else "delivered"
                    if outcome == "duplicate":
                        self.stats.duplicates += 1
                    else:
                        self.stats.delivered += 1
                    return outcome
                if 400 <= res.status_code < 500 and res.status_code != 429:
                    code = _code(res)
                    self.stats.rejected += 1
                    self.stats.last_error = f"{res.status_code} {code}"
                    log.warning("Backend rejected %s (%s): %s", event.event_type, res.status_code, code)
                    return "rejected"
                error = f"HTTP {res.status_code}"
            except httpx.HTTPError as exc:
                error = type(exc).__name__
            self.stats.last_error = error
            if attempt >= self._max_attempts or self._stop.is_set():
                self.stats.failed += 1
                log.error("Event %s not delivered after %d attempt(s): %s", event.event_type, attempt, error)
                return "failed"
            self.stats.retries += 1
            time.sleep(min(10.0, self._backoff * (2 ** (attempt - 1))))
            attempt += 1

    def close(self, timeout: float = 5.0) -> None:
        """Stop accepting work, try to send what's queued for up to ``timeout`` seconds."""
        deadline = time.monotonic() + timeout
        while not self._queue.empty() and time.monotonic() < deadline:
            time.sleep(0.05)
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=max(0.1, deadline - time.monotonic()))
        left = self._queue.qsize()
        if left:
            self.stats.failed += left
            log.warning("%d event(s) not delivered at shutdown.", left)


def _code(res: httpx.Response) -> str:
    try:
        body = res.json()
        return str(body.get("code") or body.get("title") or "")
    except Exception:  # noqa: BLE001
        return res.text[:120]
