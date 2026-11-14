"""Logging that never writes connection secrets.

Every record passes through :class:`RedactingFilter`, which masks credentials
inside URLs (``rtsp://user:pass@host`` → ``rtsp://***:***@host``) and any
registered secret value (API keys).
"""
from __future__ import annotations

import logging
import re
from typing import Iterable

_URL_CREDENTIALS = re.compile(r"(?P<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)[^/@\s:]+(?::[^/@\s]*)?@")


def redact(text: str, secrets: Iterable[str] = ()) -> str:
    """Mask URL credentials and the given secret values in ``text``."""
    out = _URL_CREDENTIALS.sub(lambda m: f"{m.group('scheme')}***:***@", text)
    for s in secrets:
        if s and len(s) >= 6:
            out = out.replace(s, "***")
    return out


class RedactingFilter(logging.Filter):
    def __init__(self, secrets: Iterable[str] = ()) -> None:
        super().__init__()
        self._secrets = [s for s in secrets if s]

    def filter(self, record: logging.LogRecord) -> bool:  # noqa: A003 - logging API
        message = record.getMessage()
        clean = redact(message, self._secrets)
        if clean != message:
            record.msg, record.args = clean, ()
        return True


def setup_logging(level: str, secrets: Iterable[str] = ()) -> None:
    root = logging.getLogger()
    root.setLevel(level.upper())
    if not any(isinstance(h, logging.StreamHandler) and getattr(h, "_cv", False) for h in root.handlers):
        handler = logging.StreamHandler()
        handler._cv = True  # type: ignore[attr-defined]
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
        root.addHandler(handler)
    for h in root.handlers:
        h.filters = [f for f in h.filters if not isinstance(f, RedactingFilter)]
        h.addFilter(RedactingFilter(secrets))
    # Ultralytics and httpx are chatty at INFO.
    logging.getLogger("ultralytics").setLevel(logging.WARNING)
    logging.getLogger("httpx").setLevel(logging.WARNING)
