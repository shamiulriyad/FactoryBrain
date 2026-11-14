"""API-key protection for the service's own endpoints (everything but ``/health``)."""
from __future__ import annotations

import hmac

from fastapi import Depends, HTTPException, Request, status
from fastapi.security import APIKeyHeader

API_KEY_HEADER = "X-API-Key"
_header = APIKeyHeader(name=API_KEY_HEADER, auto_error=False)


def require_api_key(request: Request, presented: str | None = Depends(_header)) -> None:
    expected: str = request.app.state.settings.api_key
    if not presented or not hmac.compare_digest(presented.encode(), expected.encode()):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail={"code": "api_key_invalid", "message": "Send a valid X-API-Key."})
