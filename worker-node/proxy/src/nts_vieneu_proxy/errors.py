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


# D6 contract: every proxy error code and the HTTP status it maps to
# (post-commit aborts/log-only outcomes keep their *logged* status — no
# status line is sent for them). `ProxyError` refuses any other code.
CODES: dict[str, int] = {
    "proxy_unauthorized": 401,
    "route_not_found": 404,
    "method_not_allowed": 405,
    "invalid_request": 400,
    "unsupported_format": 400,
    "voice_not_in_catalog": 400,
    "upstream_rejected": 400,
    "body_too_large": 413,
    "input_too_long": 413,
    "upstream_auth_failed": 502,
    "upstream_unavailable": 502,
    "upstream_bad_format": 502,
    "upstream_empty_audio": 502,
    "upstream_first_audio_timeout": 504,
    "transcode_failed": 500,
    "upstream_busy": 429,
    "proxy_busy": 429,
    # Post-commit connection aborts / log-only outcomes (D4, D6):
    "upstream_stream_broken": 502,
    "request_max_exceeded": 502,
    "client_disconnected": 499,
    "internal_error": 500,
}


class ProxyError(Exception):
    """Request-scoped failure that maps to a D6 code and HTTP status."""

    def __init__(
        self,
        code: str,
        http_status: int,
        message: str,
        retry_after: int | float | None = None,
    ) -> None:
        assert code in CODES, f"unknown proxy error code: {code}"
        super().__init__(message)
        self.code = code
        self.http_status = http_status
        self.message = message
        self.retry_after = retry_after


def map_upstream_status(status: int, message: str | None) -> ProxyError:
    """D6 rows for a non-200 upstream status before commit.

    429 is normally retried by the D5 admission loop; reached only after the
    budget is spent. ``message`` is VieNeu's ``error.message`` (T3), truncated
    to the D2 200-char cap.
    """
    if status == 400:
        return ProxyError(
            "upstream_rejected",
            CODES["upstream_rejected"],
            (message or "upstream rejected request")[:200],
        )
    if status in (401, 403):
        return ProxyError(
            "upstream_auth_failed",
            CODES["upstream_auth_failed"],
            "upstream authentication failed",
        )
    if status == 429:
        return ProxyError(
            "upstream_busy", CODES["upstream_busy"], "upstream busy", retry_after=5
        )
    return ProxyError(
        "upstream_unavailable",
        CODES["upstream_unavailable"],
        "upstream returned an unexpected status",
    )


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
