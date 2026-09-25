"""Error types and the D6 error body shape.

Contract: README D2 (body shape), D6 (codes). This module only defines the
plumbing; each task defines the codes it raises (04.1.2-T2 completes D6).
"""

from __future__ import annotations

from typing import Any


class ConfigError(Exception):
    """Startup configuration failure; `str(err)` is `invalid config: <VAR>`.

    The variable NAME is reported, never its value (D13).
    """

    def __init__(self, var_name: str) -> None:
        super().__init__(f"invalid config: {var_name}")
        self.var_name = var_name


class CatalogError(Exception):
    """Voice catalog validation failure (D9); `code` is the D9 code."""

    def __init__(
        self, code: str, voice_id: str | None = None, detail: str | None = None
    ) -> None:
        super().__init__(f"invalid catalog: {code}" + (f" ({detail})" if detail else ""))
        self.code = code
        self.voice_id = voice_id


class ProxyError(Exception):
    """Request-scoped failure that maps to a D6 code and HTTP status."""

    def __init__(
        self,
        code: str,
        http_status: int,
        message: str,
        retry_after: int | float | None = None,
    ) -> None:
        super().__init__(message)
        self.code = code
        self.http_status = http_status
        self.message = message
        self.retry_after = retry_after


class AbortStream(Exception):
    """Raised inside the response iterator to abort the connection (D4).

    After commit there is no clean error path: raising makes uvicorn close the
    socket without a final chunk (X2), which MPT's `requests` surfaces as
    ChunkedEncodingError. `code`/`log_status` fill the D7 log line.
    """

    def __init__(self, code: str, log_status: int = 502) -> None:
        super().__init__(code)
        self.code = code
        self.log_status = log_status


def error_body(code: str, message: str) -> dict[str, Any]:
    """D2/D6 error body; message is capped at 200 chars, no input text."""
    return {
        "error": {
            "message": message[:200],
            "type": "proxy_error",
            "code": code,
        }
    }
