"""Errors the API turns into clear HTTP responses (a stable ``code`` and a message)."""
from __future__ import annotations


class CvError(Exception):
    status = 400
    code = "cv_error"

    def __init__(self, message: str, code: str | None = None) -> None:
        super().__init__(message)
        self.message = message
        if code:
            self.code = code


class CameraNotFound(CvError):
    status, code = 404, "camera_not_found"


class SessionAlreadyRunning(CvError):
    status, code = 409, "session_already_running"


class SessionNotRunning(CvError):
    status, code = 409, "session_not_running"


class TooManySessions(CvError):
    status, code = 429, "too_many_sessions"


class InvalidSource(CvError):
    status, code = 422, "invalid_source"


class BackendUnavailable(CvError):
    status, code = 503, "backend_unavailable"


class ModelNotReady(CvError):
    status, code = 503, "model_not_ready"
