"""CPU fallback and secret-free logging."""
import logging

from app.core.device import select_device
from app.core.logging import RedactingFilter, redact


def test_auto_uses_cpu_without_a_gpu_and_cuda_requests_fall_back():
    auto = select_device("auto", cuda_available=False)
    assert auto.device == "cpu" and "CPU" in auto.note
    forced = select_device("cuda", cuda_available=False)
    assert forced.device == "cpu" and "falling back to CPU" in forced.note
    assert select_device("cpu", cuda_available=True).device == "cpu"
    assert select_device("auto", cuda_available=True).device == "cuda:0"
    assert select_device("tpu", cuda_available=False).device == "cpu"


def test_urls_and_keys_are_redacted_from_logs(caplog):
    assert redact("open rtsp://admin:P4ss@10.0.0.1:554/live failed") == "open rtsp://***:***@10.0.0.1:554/live failed"
    assert redact("key=abcdef123456 sent", ["abcdef123456"]) == "key=*** sent"
    log = logging.getLogger("redaction-test")
    log.addFilter(RedactingFilter(["service-key-123456"]))
    with caplog.at_level(logging.INFO):
        log.info("connecting to %s with %s", "rtsp://u:pw@cam/1", "service-key-123456")
    assert "pw@" not in caplog.text and "service-key-123456" not in caplog.text
