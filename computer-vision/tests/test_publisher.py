"""Publishing failures: rejections aren't retried, outages are, a full queue drops, shutdown flushes."""
from datetime import datetime, timezone

import httpx

from app.events.publisher import HttpEventPublisher
from app.events.schemas import MonitoringEvent
from app.integration.backend import BackendClient


def event(n: int = 1) -> MonitoringEvent:
    return MonitoringEvent(event_type="PERSON_DETECTED", factory_id="f", idempotency_key=f"cv:t:{n}", occurred_at=datetime.now(timezone.utc), camera_id="c")


def client(handler) -> BackendClient:  # noqa: ANN001
    return BackendClient("http://backend", "svc-key", transport=httpx.MockTransport(handler))


def test_delivered_duplicate_and_the_service_key_header():
    seen = []

    def handler(req: httpx.Request) -> httpx.Response:
        seen.append(req)
        return httpx.Response(201 if len(seen) == 1 else 200, json={"outcome": "Accepted"})

    p = HttpEventPublisher(client(handler).post_event)
    assert p.deliver(event()) == "delivered"
    assert p.deliver(event()) == "duplicate"
    assert seen[0].headers["X-Service-Key"] == "svc-key"
    assert seen[0].url.path == "/api/monitoring/events"
    assert p.stats.delivered == 1 and p.stats.duplicates == 1


def test_a_rejection_is_not_retried():
    calls = []

    def handler(req):  # noqa: ANN001
        calls.append(1)
        return httpx.Response(400, json={"code": "zone_inactive"})

    p = HttpEventPublisher(client(handler).post_event, backoff_seconds=0.001)
    assert p.deliver(event()) == "rejected"
    assert len(calls) == 1 and p.stats.rejected == 1 and "zone_inactive" in p.stats.last_error


def test_outages_are_retried_then_reported_failed():
    state = {"n": 0}

    def flaky(req):  # noqa: ANN001
        state["n"] += 1
        return httpx.Response(503) if state["n"] < 3 else httpx.Response(201, json={})

    p = HttpEventPublisher(client(flaky).post_event, max_attempts=5, backoff_seconds=0.001)
    assert p.deliver(event()) == "delivered" and p.stats.retries == 2

    def down(req):  # noqa: ANN001
        raise httpx.ConnectError("refused")

    p2 = HttpEventPublisher(client(down).post_event, max_attempts=3, backoff_seconds=0.001)
    assert p2.deliver(event()) == "failed"
    assert p2.stats.failed == 1 and p2.stats.retries == 2 and p2.stats.last_error == "ConnectError"


def test_a_full_queue_drops_new_events_and_close_flushes_the_rest():
    sent = []
    p = HttpEventPublisher(client(lambda req: sent.append(req) or httpx.Response(201, json={})).post_event, queue_size=2)
    assert p.publish(event(1)) and p.publish(event(2))
    assert not p.publish(event(3))
    assert p.stats.dropped_queue_full == 1
    p.start()
    p.close(timeout=5)
    assert len(sent) == 2 and p.stats.delivered == 2
