#!/usr/bin/env python3
"""M0 story harness (02.1.3-T0) — one schema-valid `story.v1` story per call.

Calls the OpenAI Responses API **only** through the 01 usage tap
(`http://127.0.0.1:8002/v1`, 01 D6), with our real presets and the real
`prompts/schemas/story.v1.json` schema, so the A/B measures our prompts and our
schema instead of MPT's output format (F01). One cell writes six files under
`trial/stories/<run_id>/`: `request.json`, `response.json`, `story.json`,
`video_script.txt`, `video_terms.json`, `metrics.json` (plan §3.2-3.3); a cell
whose call failed keeps the three evidence files (`request.json`,
`response.json`, `metrics.json`).

Security (plan §8):
  * the key is read from the macOS Keychain (`security find-generic-password`,
    01 D3) at call time, kept in memory only — never printed, never written to
    any artifact, never passed to a child process;
  * `store: false` on every call (O5's default is `true`);
  * `safety_identifier = sha256("nts-m0-harness")` (hex, one platform identity);
  * `Authorization` never appears in `request.json` (the key is added, as an
    unredirected header, when the request is sent);
  * redirects are not followed (SEC-02D-1): the opener has a no-redirect
    handler, so a 3xx from the relay/tap comes back as `provider_error` and the
    bearer key is never re-sent to the `Location` host.

Cost and evidence:
  * every attempt of a cell is charged (`metrics.json.attempts`); a transport
    timeout (`status 0`) is **not** retried — the call is still in flight
    upstream and billed — and is recorded as `provider_unavailable`
    (D-TIMEOUT). An unpriced attempt is charged at a conservative bound built
    from the UTF-8 bytes of the request body (P6), never $0.
  * `--timeout` (default 240 s) bounds one call;
  * `metrics.json` is written atomically (tmp + `os.replace`), so a torn file
    never looks like a finished cell (`--retry-failed` re-runs recorded failures);
  * each loaded prompt file is verified against `prompts/manifest.json`
    (`sha256`) and its digest is recorded in `request.json` (D-HASH);
  * preset front matter is stripped before the prompt is sent (D-FM).

Exit codes (01 D9): 0 ok · 1 a check failed (validator issues on
`--validate-only`, budget exceeded) · 2 environment or usage error (missing
key, tap down, missing preset/schema, unknown placeholder, bad `--model`,
manifest hash mismatch, unsafe run/brief id).

Run:  uv run --no-project --python 3.12 trial/tools/story_harness.py --help
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import random
import re
import socket
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import story_validator as sv  # noqa: E402

TOOLS_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOLS_DIR.parents[1]
DEFAULT_PROMPTS_DIR = REPO_ROOT / "prompts"
DEFAULT_OUT = REPO_ROOT / "trial" / "stories"
DEFAULT_LENGTH = TOOLS_DIR / "LENGTH.v1.json"
DEFAULT_PRICES = REPO_ROOT / "trial" / "prices.json"
DEFAULT_KEY_SERVICE = "nts.openai.story"
DEFAULT_TAP_BASE_URL = "http://127.0.0.1:8002/v1"

# `gpt-6-sol` stays on the story allow-list only for the 02.1.3-T1 A/B window
# (02 CR-27, owner 2026-09-28; relay-side allow-list per 01 D7).
MODELS = {"gpt-6-luna": "luna", "gpt-6-sol": "sol"}
TIERS = ("short", "standard", "long")
LANGS = ("vi", "en")
# FR-1: a cell's language comes from its brief's own `language` field; this is
# only the fallback for an inline `--brief` (no brief object) and for a brief
# entry that carries no `language`.
DEFAULT_LANG = "vi"
TOPICS = ("ghost", "spiritual", "true_crime")

# D5 closed placeholder list (T49: the four shared blocks are substituted in
# place and are therefore required in every preset; the scalars are optional).
PLACEHOLDERS = ("language_name", "tier_minutes", "token_range", "segment_range",
                "keyword_rules", "ear_rules", "overused_phrases", "structure_vocab")
REQUIRED_SHARED_PLACEHOLDERS = ("keyword_rules", "ear_rules", "overused_phrases",
                                "structure_vocab")

LANGUAGE_NAMES = {"vi": "Vietnamese (tiếng Việt)", "en": "English"}
TIER_MINUTES = {"short": "0.75–1.0", "standard": "5–8", "long": "10–15"}

# D14 rates come from the rates file (`--prices`, default trial/prices.json); the
# harness embeds no rate table and never defaults a missing rate to 0 — a model
# with no row is an environment error (exit 2) and a missing single rate makes
# the cell unpriceable (`cost_usd: null` + a note naming the gap).
RATE_KEYS = ("input", "cached_input", "output")

# The tap's attribution contract (usage_tap.py:58, 191, 256): it reads
# <NTS_HOME>/current_run, with NTS_HOME defaulting to <repo>/.nts, and files the
# call under "no-run" unless the id matches this regex.
D10_RUN_ID = re.compile(r"^\d{8}-\d{4}-(vi|en)-(landscape|portrait)-\d{2}$")
TAP_ASPECT = "landscape"

DEFAULT_TIMEOUT_S = 240.0     # D-TIMEOUT: one client timeout for a paid call
MAX_ATTEMPTS = 2             # D7: SDK retries off, one resilience layer
DEFAULT_RETRY_AFTER_S = 2.0
_PLACEHOLDER_RE = re.compile(r"\{\{([^{}]*)\}\}")
_BASE_URL_RE = re.compile(r"^(https?)://([^/:]+)(?::(\d+))?(/.*)?$")
# D-RUNID / SEC-02D-3: one safe path component for --run-id and every brief id
# (rejects `.`/`..`, separators, absolute paths and control characters).
SAFE_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
# FR-1 fallback: the language a `briefs.<lang>.json` file name records, used only
# when a brief entry itself carries no `language` (every fixture carries it).
_BRIEFS_FILE_RE = re.compile(r"^briefs\.(vi|en)\.json$")


class EnvironmentError(RuntimeError):
    """Usage or environment error -> exit code 2 (01 D9)."""


class BudgetStop(RuntimeError):
    def __init__(self, spent: float, cap: float) -> None:
        super().__init__(f"spent={spent:.6f} cap={cap:.2f}")
        self.spent = spent
        self.cap = cap


# --------------------------------------------------------------------------- #
# small helpers
# --------------------------------------------------------------------------- #

def fail(message: str) -> None:
    raise EnvironmentError(message)


def repo_path(value: str) -> Path:
    """Relative CLI paths resolve from the repository root (predictable for a
    repo-scoped trial tool), absolute paths are used as given."""
    path = Path(value)
    return path if path.is_absolute() else REPO_ROOT / path


def load_json(path: Path):
    if not path.exists():
        fail(f"file not found: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (ValueError, OSError) as exc:
        fail(f"cannot read {path}: {exc}")


def read_text(path: Path) -> str:
    if not path.exists():
        fail(f"file not found: {path}")
    try:
        return path.read_text(encoding="utf-8")
    except OSError as exc:
        fail(f"cannot read {path}: {exc}")


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def utc_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def strip_front_matter(text: str) -> str:
    """Drop the `---` fenced front matter (preset-spec.v1.md §1)."""
    if not text.startswith("---"):
        return text.strip()
    lines = text.splitlines()
    for index in range(1, len(lines)):
        if lines[index].strip() == "---":
            return "\n".join(lines[index + 1:]).strip()
    return text.strip()


def check_choice(flag: str, value: str, allowed) -> str:
    if value not in allowed:
        fail(f"{flag} must be one of {', '.join(allowed)} (got {value!r})")
    return value


def check_safe_id(kind: str, value: str) -> str:
    """D-RUNID / SEC-02D-3: `--run-id` and every brief id must be one safe path
    component (no `/`, no `..`, no newline), so a run dir can never escape
    `--out` and the `X-Client-Request-Id` header stays well formed."""
    if not isinstance(value, str) or not SAFE_ID_RE.match(value) or value in (".", ".."):
        fail(f"{kind} {value!r} must match ^[A-Za-z0-9][A-Za-z0-9._-]{{0,63}}$ "
             f"(got {value!r})")
    return value


def assert_under_out(out_dir: Path, run_dir: Path) -> None:
    """D-RUNID: defence in depth — a resolved run dir must stay under `--out`."""
    base = out_dir.resolve()
    target = run_dir.resolve()
    if target != base and base not in target.parents:
        fail(f"run dir {run_dir} escapes --out {out_dir}")


def write_json_atomic(path: Path, document) -> None:
    """D-ATOMIC / QA-02D-10: a reader never sees a torn metrics.json, so a
    half-written file cannot look like a finished cell."""
    tmp = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        tmp.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n",
                       encoding="utf-8")
        os.replace(tmp, path)
    except OSError:
        _quiet_unlink(tmp)
        raise


def split_base_url(base_url: str) -> tuple[str, str, int, str]:
    """Only the 01 usage tap is ever called (01 D6): plain http on 127.0.0.1 with
    a /v1 prefix. Any other host would send the bearer key upstream (01 CR-15)."""
    match = _BASE_URL_RE.match(base_url.strip())
    if not match:
        fail(f"bad --tap-base-url: {base_url!r} (need http://127.0.0.1:<port>/v1)")
    scheme, host, port, prefix = match.group(1), match.group(2), match.group(3), match.group(4) or ""
    prefix = prefix.rstrip("/")
    if scheme != "http" or host != "127.0.0.1":
        fail(f"--tap-base-url must be http://127.0.0.1:<port>/v1 (got {base_url!r}); "
             f"the harness never calls the upstream provider directly (01 D6/CR-15)")
    if prefix != "/v1":
        fail(f"--tap-base-url path must be /v1 (got {base_url!r}); the tap forwards /v1/* only")
    if port is None:
        fail(f"--tap-base-url needs an explicit port (got {base_url!r})")
    return scheme, host, int(port), prefix


def nts_home() -> Path:
    """Mirrors lib.sh nts_home / run_manifest.py:104-107: $NTS_HOME wins, default
    <repo>/.nts; a relative value resolves against the cwd (so the harness and the
    tap/start_run.sh agree on the lock file and the tap's current_run)."""
    value = os.environ.get("NTS_HOME") or str(REPO_ROOT / ".nts")
    path = Path(os.path.expanduser(value))
    return path if path.is_absolute() else path.resolve()


def _read_current_run() -> str | None:
    try:
        return (nts_home() / "current_run").read_text(encoding="utf-8").strip()
    except OSError:
        return None


def peek_run_id(lang: str) -> str | None:
    """Read-only attribution check for --dry-run: never writes the lock file."""
    held = _read_current_run()
    if held and D10_RUN_ID.match(held) and f"-{lang}-" in held:
        return held
    return None


def take_run_lock(lang: str, ours: set[str]) -> str | None:
    """01 D10 lock protocol (start_run.sh:88-89, 157-158): `current_run` is the
    single-active-run lock. An id we did not write is never taken over — a live
    MPT render would lose its usage attribution. The first acquisition is an
    atomic create (`os.link(tmp, path)`), so a lock that appears between the read
    and the write is never clobbered (SEC-02D-4); `os.replace` is used only when
    the file already holds an id this process wrote (a language swap). A write
    failure stays non-fatal (plan §7) but is printed, and then the tap logs this
    run as `no-run`."""
    held = _read_current_run()
    if held and held not in ours:
        fail(f"current_run is held by run {held} (start_run.sh would refuse); "
             f"finish or --abort it, then retry")
    path = nts_home() / "current_run"
    run_id = time.strftime("%Y%m%d-%H%M", time.gmtime()) + f"-{lang}-{TAP_ASPECT}-01"
    tmp = path.with_name(f".current_run.{os.getpid()}.tmp")
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp.write_text(run_id + "\n", encoding="utf-8")
        if held:
            os.replace(tmp, path)          # already ours: in-place swap
        else:
            os.link(tmp, path)             # first acquisition: never clobber
    except FileExistsError:
        _quiet_unlink(tmp)
        other = _read_current_run() or "an unnamed run"
        fail(f"current_run is held by run {other} (created by another process); "
             f"finish or --abort it, then retry")
    except OSError as exc:
        _quiet_unlink(tmp)
        print(f"story_harness: warning: cannot write {path} ({exc}); the tap will log "
              f"this run as no-run (plan §7: not fatal)", file=sys.stderr)
        return None
    _quiet_unlink(tmp)
    return run_id


def _quiet_unlink(path: Path) -> None:
    try:
        path.unlink()
    except OSError:
        pass


def release_run_lock(ours: set[str]) -> None:
    """Drop the lock only while it still holds an id this process wrote, so a
    later start_run.sh can start (and another run's lock is never removed)."""
    path = nts_home() / "current_run"
    held = _read_current_run()
    if not held or held not in ours:
        return
    try:
        path.unlink()
    except OSError as exc:
        print(f"story_harness: warning: cannot release {path} ({exc})", file=sys.stderr)


def lock_for(lang: str, state: dict) -> str | None:
    """Hold one lock for the whole batch; swap it when the cell language changes."""
    current = state.get("id")
    if isinstance(current, str) and D10_RUN_ID.match(current) and f"-{lang}-" in current:
        return current
    run_id = take_run_lock(lang, state["owned"])
    if run_id:
        state["owned"].add(run_id)
        state["id"] = run_id
    return run_id


# --------------------------------------------------------------------------- #
# length table, secrets, transport
# --------------------------------------------------------------------------- #

def load_length_table(path: Path) -> dict:
    table = load_json(path)
    for tier in TIERS:
        if tier not in table:
            fail(f"{path} has no {tier!r} row")
        for lang in LANGS:
            if lang not in table[tier]:
                fail(f"{path} has no {tier}/{lang} row")
    return table


def limits_for(table: dict, tier: str, lang: str) -> dict:
    return dict(table[tier][lang])


def load_secret(service: str) -> str:
    """Read the API key from the Keychain; the value is never printed or logged."""
    account = os.environ.get("USER", "")
    if not account:
        fail("$USER is empty; cannot read the Keychain item")
    try:
        proc = subprocess.run(
            ["security", "find-generic-password", "-a", account, "-s", service, "-w"],
            capture_output=True, text=True, check=False)
    except OSError as exc:
        fail(f"cannot run `security` to read service {service}: {exc}")
    key = (proc.stdout or "").strip()
    if proc.returncode != 0 or not key:
        fail(f"Keychain item {service!r} not found for account {account!r} "
             f"(01 D3); add it with `security add-generic-password`")
    return key


def assert_tap(base_url: str) -> None:
    """Refuse to run without the 01 usage tap: the trial's spend evidence lives
    there, so falling back to the upstream host (01 CR-15) is forbidden."""
    _, host, port, _ = split_base_url(base_url)
    try:
        with socket.create_connection((host, port), timeout=3):
            return
    except OSError as exc:
        fail(f"usage tap not listening on {host}:{port} (01 D6): {exc}")


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """SEC-02D-1: a 3xx must not make urllib re-send the bearer key to the
    `Location` host (the tap relays the relay's `Location` header verbatim)."""

    def redirect_request(self, *args, **kwargs):  # noqa: D102 - urllib hook
        return None


def post_responses(base_url: str, body: dict, api_key: str,
                   client_request_id: str, timeout: float = DEFAULT_TIMEOUT_S) -> dict:
    """POST /responses through the tap with `timeout` seconds. Returns a dict,
    never raises for HTTP; `status: 0` is a transport timeout/connection error
    (not retried by the caller — the upstream call is still billed).

    The opener has an empty ProxyHandler so HTTP_PROXY/https_proxy from the
    environment cannot route the Authorization header through a proxy, and a
    no-redirect handler so a 3xx becomes `provider_error` instead of leaking the
    key to the `Location` host (SEC-02D-1). The key is an *unredirected* header
    as a second layer."""
    url = base_url.rstrip("/") + "/responses"
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=data, method="POST")
    request.add_header("Content-Type", "application/json")
    request.add_header("Accept", "application/json")
    request.add_unredirected_header("Authorization", "Bearer " + api_key)
    request.add_header("X-Client-Request-Id", client_request_id)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())
    try:
        with opener.open(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8", "replace")
            return {"status": response.status, "payload": _safe_json(raw), "raw": raw,
                    "retry_after": None, "request_id": response.headers.get("x-request-id")}
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        retry_after = None
        if exc.headers is not None:
            retry_after = exc.headers.get("Retry-After")
        return {"status": exc.code, "payload": _safe_json(raw), "raw": raw,
                "retry_after": retry_after,
                "request_id": (exc.headers or {}).get("x-request-id")}
    except (urllib.error.URLError, socket.timeout, TimeoutError, OSError) as exc:
        detail = getattr(exc, "reason", exc)
        return {"status": 0, "payload": {"error": "transport", "detail": str(detail)},
                "raw": "", "retry_after": None, "request_id": None}


def _safe_json(raw: str):
    try:
        return json.loads(raw)
    except (ValueError, RecursionError):
        return None


# `derive_body` / `derive_keywords` are the D3 derived-field functions the harness
# writes into `video_script.txt` / `video_terms.json` (M3). They live in
# story_validator.py (pure) so the validator and the harness share one
# implementation instead of two copies.
derive_body = sv.derive_body
derive_keywords = sv.derive_keywords


# --------------------------------------------------------------------------- #
# D7 outcome mapping
# --------------------------------------------------------------------------- #

def _error_code(payload) -> str:
    if not isinstance(payload, dict):
        return ""
    parts: list[str] = []
    error = payload.get("error")
    if isinstance(error, dict):
        parts.extend(str(error.get(key) or "") for key in ("code", "type", "message"))
    elif isinstance(error, str):
        parts.append(error)
    parts.append(str(payload.get("code") or ""))
    return " ".join(part for part in parts if part)


def _has_refusal(payload) -> bool:
    if not isinstance(payload, dict):
        return False
    for item in payload.get("output") or []:
        if not isinstance(item, dict):
            continue
        for part in item.get("content") or []:
            if isinstance(part, dict) and part.get("type") == "refusal":
                return True
    return False


def _incomplete_reason(payload):
    if not isinstance(payload, dict):
        return None
    details = payload.get("incomplete_details")
    if isinstance(details, dict):
        return details.get("reason")
    return None


def _is_funds_exhausted(code: str) -> bool:
    lowered = code.lower()
    return ("spend_limit_exceeded" in lowered or "credit_balance_exhausted" in lowered
            or "insufficient_quota" in lowered)


def classify(status: int, payload) -> tuple[str, str | None]:
    """D7 outcome table -> (outcome, incomplete_reason)."""
    if status == 0:
        return "provider_unavailable", None
    if 200 <= status < 300:
        if _has_refusal(payload):
            return "moderated", None
        reason = _incomplete_reason(payload)
        if reason == "content_filter":
            return "moderated", reason
        if reason == "max_output_tokens":
            return "invalid_output", reason
        return "ok", reason
    if status == 429 and _is_funds_exhausted(_error_code(payload)):
        return "funds_exhausted", None
    if 300 <= status < 400:
        # D-REDIRECT / SEC-02D-1: the opener follows no redirect, so a 3xx is a
        # provider error, never a second request to the Location host.
        return "provider_error", None
    if 400 <= status < 500:
        return "provider_error", None
    return "provider_unavailable", None


def extract_story(payload) -> tuple[dict | None, str | None]:
    """Parse the strict `story` JSON the provider was told to return."""
    if not isinstance(payload, dict):
        return None, "no_response_body"
    if _has_refusal(payload):
        return None, "refusal"
    texts: list[str] = []
    for item in payload.get("output") or []:
        if not isinstance(item, dict):
            continue
        for part in item.get("content") or []:
            if isinstance(part, dict) and part.get("type") == "output_text":
                texts.append(str(part.get("text", "")))
    if not texts and isinstance(payload.get("output_text"), str):
        texts.append(payload["output_text"])
    if not texts:
        return None, "no_output_text"
    try:
        story = json.loads("\n".join(texts))
    except (ValueError, RecursionError) as exc:
        return None, f"json_invalid: {exc}"
    if not isinstance(story, dict):
        return None, "json_not_an_object"
    return story, None


def usage_record(payload) -> dict:
    """Every usage field the response carried (D14, 02 T67)."""
    usage = payload.get("usage") if isinstance(payload, dict) else None
    if not isinstance(usage, dict) or not usage:
        return {}
    details_in = usage.get("input_tokens_details") or {}
    details_out = usage.get("output_tokens_details") or {}
    return {
        "input_tokens": usage.get("input_tokens", 0) or 0,
        "cached_tokens": details_in.get("cached_tokens", 0) or 0,
        "cache_write_tokens": details_in.get("cache_write_tokens", 0) or 0,
        "output_tokens": usage.get("output_tokens", 0) or 0,
        "reasoning_tokens": details_out.get("reasoning_tokens", 0) or 0,
    }


def load_rates(prices_path: Path) -> dict:
    """D14 rates per 1M tokens from the rates file (trial/prices.json). No
    embedded table and no defaults: the file is the single source of truth."""
    data = load_json(prices_path)
    models = data.get("models")
    if not isinstance(models, dict):
        fail(f"{prices_path} has no `models` object")
    rates: dict[str, dict] = {}
    for model, entry in models.items():
        if isinstance(entry, dict):
            rates[str(model)] = {key: float(value)
                                 for key, value in entry.items()
                                 if isinstance(value, (int, float))}
    return rates


def require_call_rates(model: str, rates: dict, prices_path: Path) -> dict:
    """Pre-flight: a paid call needs the model row and the three rates the D14
    formula always uses. A missing row is exit 2 before the call, never a silent
    zero in the cost."""
    rate = rates.get(model)
    if not rate:
        fail(f"{prices_path} has no rates for {model}; add the O3 row before calling")
    missing = [key for key in RATE_KEYS if key not in rate]
    if missing:
        fail(f"{prices_path} rates for {model} are missing {', '.join(missing)}")
    return rate


def compute_cost(model: str, usage: dict, rates: dict) -> float | None:
    """D14: (input - cached) x r_in + cached x r_cached + cache_write x r_write
    + output x r_out, rates per 1M tokens. Returns None when the usage cannot be
    priced (absent usage, no model row, or a rate the response actually used is
    missing) — never a fabricated 0."""
    if not usage:
        return None
    rate = rates.get(model)
    if not rate:
        return None
    if any(key not in rate for key in RATE_KEYS):
        return None
    input_tokens = usage.get("input_tokens") or 0
    cached = usage.get("cached_tokens") or 0
    cache_write = usage.get("cache_write_tokens") or 0
    if cache_write and "cache_write" not in rate:
        return None
    output = usage.get("output_tokens") or 0
    total = ((input_tokens - cached) * rate["input"]
             + cached * rate["cached_input"]
             + cache_write * rate.get("cache_write", 0.0)
             + output * rate["output"])
    return total / 1_000_000


def cost_bound_usd(model: str, usage: dict, rates: dict,
                   request_bytes: int, limits: dict) -> float | None:
    """Conservative USD bound for a paid cell whose usage cannot be priced
    exactly. It is never written as `cost_usd`; it keeps the `--max-usd` guard
    honest (an unpriced cell must not look free). Every token is charged at the
    model's highest known rate, so the bound cannot under-count.

    Without usage the input term is the **UTF-8 byte length of the full request
    body** (P6 rule / D-BOUND, PERF-02D-2): a billed token is never longer than a
    byte, so bytes are a safe upper bound on input tokens — the D4 story word
    count (`token_count`) is not, it under-counts the prompt by ~2.5x. The output
    term is the tier's `writer_max_output`."""
    rate = rates.get(model) or {}
    ceilings = [value for value in rate.values() if isinstance(value, (int, float))]
    if not ceilings:
        return None
    ceiling = max(ceilings)
    if usage:
        tokens = sum(int(usage.get(key) or 0) for key in
                     ("input_tokens", "cached_tokens", "cache_write_tokens", "output_tokens"))
    else:
        tokens = int(request_bytes or 0) + int(limits.get("writer_max_output") or 0)
    return tokens * ceiling / 1_000_000


def cost_note(model: str, usage: dict, rates: dict) -> str | None:
    """Why `cost_usd` is null, in the artifact itself."""
    if not usage:
        return "usage absent"
    rate = rates.get(model)
    if not rate:
        return f"{model} has no rates row (cost_usd null)"
    missing = [key for key in RATE_KEYS if key not in rate]
    if missing:
        return f"rates for {model} missing {', '.join(missing)} (cost_usd null)"
    if (usage.get("cache_write_tokens") or 0) and "cache_write" not in rate:
        return ("cache_write_tokens reported but the rates file has no `cache_write` rate: "
                "cost_usd null, the term is never dropped or defaulted to 0 (CR to 01)")
    return None


# --------------------------------------------------------------------------- #
# prompt bundle, substitution, request body
# --------------------------------------------------------------------------- #

def verify_prompt_files(prompts_dir: Path, digests: dict[str, str]) -> str:
    """D-HASH / QA-02D-14: every loaded prompt file must match
    `prompts/manifest.json`. Exit 2 naming the file on a mismatch or a missing
    entry, so a drifted preset is never recorded as the released version.

    The manifest is optional (02.2.3-T1 owns the real start-up check); without it
    the harness records `release=unknown` and skips the check rather than
    refusing to run in a fixture-only tree."""
    manifest_path = prompts_dir / "manifest.json"
    if not manifest_path.exists():
        return "unknown"
    manifest = load_json(manifest_path)
    entries = manifest.get("files") if isinstance(manifest, dict) else None
    if not isinstance(entries, dict):
        fail(f"{manifest_path} has no `files` object")
    for relative, digest in digests.items():
        entry = entries.get(relative)
        if not isinstance(entry, dict) or not entry.get("sha256"):
            fail(f"{manifest_path} has no sha256 for {relative}; re-run the manifest "
                 f"update after adding the file (02.1.1-T0)")
        if str(entry["sha256"]).lower() != digest:
            fail(f"{relative} does not match {manifest_path} "
                 f"(expected {entry['sha256']}, got {digest}); re-hash the manifest "
                 f"after editing the prompt")
    release = manifest.get("release", "unknown")
    return str(release)


def load_prompt_bundle(prompts_dir: Path, topic: str, lang: str) -> dict:
    """Read the files 02.1.x own (read-only) and fail cleanly when one is absent.
    Each file is hashed and checked against `prompts/manifest.json` (D-HASH) and
    its front matter is stripped (D-FM); the digests go into request.json."""
    system_path = prompts_dir / "system" / "writer.v1.md"
    preset_path = prompts_dir / "story" / f"{topic}.{lang}.v1.md"
    ear_path = prompts_dir / "shared" / f"ear-rules.{lang}.v1.md"
    overused_path = prompts_dir / "shared" / f"overused.{lang}.v1.txt"
    keyword_path = prompts_dir / "shared" / "keyword-rules.v1.md"
    vocab_path = prompts_dir / "shared" / "structure-vocab.v1.json"
    schema_path = prompts_dir / "schemas" / "story.v1.json"

    if not preset_path.exists():
        fail(f"preset missing: {preset_path} (02.1.1-T1..T3 own it)")
    if not schema_path.exists():
        fail(f"schema missing: {schema_path} (02.1.1-T0 owns it)")

    texts: dict[str, str] = {}
    for relative in ("system/writer.v1.md", f"story/{topic}.{lang}.v1.md",
                     f"shared/ear-rules.{lang}.v1.md", f"shared/overused.{lang}.v1.txt",
                     "shared/keyword-rules.v1.md", "shared/structure-vocab.v1.json",
                     "schemas/story.v1.json"):
        texts[relative] = read_text(prompts_dir / relative)
    digests = {relative: sha256_hex(text) for relative, text in texts.items()}
    release = verify_prompt_files(prompts_dir, digests)

    schema = json.loads(texts["schemas/story.v1.json"])
    vocab = json.loads(texts["shared/structure-vocab.v1.json"])

    return {
        "system": strip_front_matter(texts["system/writer.v1.md"]),
        "preset_raw": strip_front_matter(texts[f"story/{topic}.{lang}.v1.md"]),
        "preset_path": preset_path,
        "ear_rules": strip_front_matter(texts[f"shared/ear-rules.{lang}.v1.md"]),
        "overused": texts[f"shared/overused.{lang}.v1.txt"].strip(),
        "keyword_rules": strip_front_matter(texts["shared/keyword-rules.v1.md"]),
        "structure_vocab": vocab,
        "schema": schema,
        "schema_text": json.dumps(schema, ensure_ascii=False, sort_keys=True),
        "prompt_hashes": digests,
        "prompt_version": (f"rel={release};sys=writer.v1;preset={topic}.{lang}.v1;"
                           f"schema=story.v1"),
    }


def substitute(template: str, values: dict) -> str:
    """Replace `{{name}}` from the closed D5 list; anything else is exit 2."""
    def replace(match: re.Match) -> str:
        name = match.group(1)
        if name not in PLACEHOLDERS or name not in values:
            fail(f"unknown placeholder {{{{{name}}}}} (closed D5 list: "
                 f"{', '.join(PLACEHOLDERS)})")
        return values[name]

    return _PLACEHOLDER_RE.sub(replace, template)


def render_structure_vocab(vocab: dict, topic: str, lang: str) -> str:
    """The per-topic, per-language enabled list (T49: substituted in place, not
    the whole vocab file)."""
    topics = vocab.get("topics") or {}
    entry = topics.get(topic)
    if not isinstance(entry, dict):
        fail(f"structure-vocab.v1.json has no {topic!r} topic")
    lines = []
    for key in ("sub_genre", "setting", "opening", "twist_type"):
        values = (entry.get(key) or {}).get(lang) or []
        lines.append(f"{key}: " + " | ".join(str(value) for value in values))
    return "\n".join(lines)


def build_values(bundle: dict, tier: str, lang: str, limits: dict) -> dict:
    return {
        "language_name": LANGUAGE_NAMES[lang],
        "tier_minutes": TIER_MINUTES[tier],
        "token_range": f"{limits['min_tokens']}–{limits['max_tokens']}",
        "segment_range": f"{limits['min_segments']}–{limits['max_segments']}",
        "keyword_rules": bundle["keyword_rules"],
        "ear_rules": bundle["ear_rules"],
        "overused_phrases": bundle["overused"],
        "structure_vocab": render_structure_vocab(bundle["structure_vocab"], bundle["topic"], lang),
    }


def assemble_head(bundle: dict, values: dict) -> str:
    """D5 order: system -> preset with the shared blocks substituted in place."""
    required = [name for name in REQUIRED_SHARED_PLACEHOLDERS
                if ("{{" + name + "}}") not in bundle["preset_raw"]]
    if required:
        fail(f"preset {bundle['preset_path']} is missing required shared placeholder(s) "
             + ", ".join("{{" + name + "}}" for name in required)
             + " (README T49: the shared blocks are substituted in place)")
    preset = substitute(bundle["preset_raw"], values)
    return "\n\n".join(part.strip() for part in (bundle["system"], preset) if part.strip())


def units_per_segment(limits: dict) -> int:
    """README T97/D6: the per-segment budget the writer is told to plan against,
    `round(min_tokens / min_segments)` for that tier+language. The model cannot
    divide the token band itself, so the number travels in the data object."""
    min_segments = limits["min_segments"]
    if not isinstance(min_segments, int) or isinstance(min_segments, bool) or min_segments <= 0:
        fail(f"length table row has a non-positive min_segments ({min_segments!r}); "
             f"cannot derive units_per_segment (README T97)")
    return round(limits["min_tokens"] / min_segments)


def build_user_data(brief_text: str, tier: str, limits: dict, facts: list) -> str:
    """D6: exactly one JSON object, fixed key set, untrusted text only inside it."""
    data = {
        "task": "write_story",
        "brief": brief_text,
        "length": {"tier": tier,
                   "tokens": [limits["min_tokens"], limits["max_tokens"]],
                   "segments": [limits["min_segments"], limits["max_segments"]],
                   "units_per_segment": units_per_segment(limits)},
        "bible": None,
        "facts": facts,
        "avoid_plots": [],
        "structure_plan": None,
    }
    return json.dumps(data, ensure_ascii=False, separators=(",", ":"))


def build_request_body(model: str, system_text: str, user_json: str, schema: dict,
                       tier: str, preset: str, limits: dict) -> dict:
    return {
        "model": model,
        "input": [
            {"role": "system", "content": [{"type": "input_text", "text": system_text}]},
            {"role": "user", "content": [{"type": "input_text", "text": user_json}]},
        ],
        "text": {"format": {"type": "json_schema", "name": "story",
                            "schema": schema, "strict": True}},
        "reasoning": {"effort": "medium"},
        "max_output_tokens": limits["writer_max_output"],
        "store": False,
        "safety_identifier": sha256_hex("nts-m0-harness"),
        "prompt_cache_key": f"write_story:{preset}",
    }


# --------------------------------------------------------------------------- #
# briefs and cell enumeration
# --------------------------------------------------------------------------- #

def load_briefs(paths: list[Path]) -> list[dict]:
    briefs: list[dict] = []
    for path in paths:
        data = load_json(path)
        if not isinstance(data, list):
            fail(f"{path} must be a JSON array of briefs")
        for entry in data:
            if not isinstance(entry, dict) or "id" not in entry:
                fail(f"{path} has a brief without an `id`")
            check_safe_id(f"brief id in {path}", str(entry["id"]))
            briefs.append(entry)
    return briefs


def briefs_for_lang(lang: str) -> list[Path]:
    return [DEFAULT_PROMPTS_DIR / "fixtures" / f"briefs.{lang}.json"]


def brief_text(brief: dict) -> str:
    return str(brief.get("brief", "")).strip()


def load_fact_sheet(prompts_dir: Path, brief_id: str) -> list[dict]:
    """D-FACTS / QA-02D-4: the fixture fact sheet carries `brief_id` per fact, so
    a true-crime cell only ever sees **its own** case. Facts are projected onto
    the documented D6 shape `{id, statement, sources}` (no fixture bookkeeping
    reaches the model)."""
    fixture = prompts_dir / "fixtures" / "fact-sheet.true_crime.v1.json"
    data = load_json(fixture)
    facts = [fact for fact in (data.get("facts") or []) if isinstance(fact, dict)]
    own = [fact for fact in facts if str(fact.get("brief_id")) == brief_id]
    if not own:
        fail(f"{fixture} has no facts for brief {brief_id!r}; add `brief_id` to that "
             f"brief's facts (D-FACTS)")
    return [{"id": str(fact.get("id")), "statement": str(fact.get("statement", "")),
             "sources": [str(alias) for alias in (fact.get("sources") or [])]}
            for fact in own]


def derive_run_id(lang: str, preset: str, brief_id: str, model: str, tier: str) -> str:
    return f"{lang}-{preset}-{brief_id}-{MODELS[model]}-{tier}"


CONTRACT_FILES = ("request.json", "response.json", "story.json",
                  "video_script.txt", "video_terms.json", "metrics.json")


def clear_contract_files(run_dir: Path) -> None:
    """A reused dir (an unfinished cell, or --force) must not keep files from an
    earlier attempt: a stale story.json would otherwise sit next to a new
    non-ok response.json/metrics.json and mix two runs in one cell."""
    for name in CONTRACT_FILES:
        path = run_dir / name
        try:
            path.unlink()
        except FileNotFoundError:
            continue
        except OSError as exc:
            fail(f"cannot clear {path}: {exc}")


def _cell_outcome(directory: Path) -> str | None:
    """D-ATOMIC / QA-02D-10: a cell is finished only when `metrics.json` is
    present **and parseable**; a torn or empty file (a crash mid-write) is not a
    finished cell. Returns the recorded outcome (`ok`, `provider_unavailable`, …)
    or None, so the caller can decide whether a failure is retried."""
    path = directory / "metrics.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, RecursionError):
        return None
    if not isinstance(data, dict):
        return None
    outcome = data.get("outcome")
    return str(outcome) if outcome else "unknown"


def _cell_done(directory: Path) -> bool:
    """A cell directory is a finished run only when its metrics.json parses; a
    directory left by `--dry-run` (or a crash) is reused, never renumbered, so
    the acceptance sequence dry-run -> live run lands in the same cell dir."""
    return _cell_outcome(directory) is not None


def pick_run_dir(out_dir: Path, run_id: str, explicit: bool, force: bool) -> tuple[Path, str]:
    """Plan §3.1: a rerun of a completed cell appends -r2, -r3 — never overwrites
    the earlier evidence. An explicit --run-id refuses to be renumbered: it needs
    --force. D-RUNID: the resolved dir must stay under --out."""
    if not _cell_done(out_dir / run_id):
        chosen = out_dir / run_id
    elif explicit:
        if not force:
            fail(f"run dir exists: {out_dir / run_id} (pass --force to overwrite)")
        chosen = out_dir / run_id
    else:
        index = 2
        while True:
            numbered = f"{run_id}-r{index}"
            if not _cell_done(out_dir / numbered):
                chosen = out_dir / numbered
                break
            index += 1
    assert_under_out(out_dir, chosen)
    return chosen, chosen.name


# --------------------------------------------------------------------------- #
# one cell
# --------------------------------------------------------------------------- #

def run_cell(cell: dict, args, table: dict, rates: dict, api_key: str | None,
             spent: float) -> dict:
    lang, topic, tier, model = cell["lang"], cell["preset"], cell["tier"], cell["model"]
    brief = cell["brief"]
    brief_id = str(brief.get("id", "inline"))
    limits = limits_for(table, tier, lang)
    bundle = load_prompt_bundle(args.prompts_dir, topic, lang)
    bundle["topic"] = topic

    facts: list[dict] = []
    if topic == "true_crime" and brief_id != "inline":
        # D-FACTS / QA-02D-4: only this brief's facts, never the whole sheet.
        facts = load_fact_sheet(args.prompts_dir, brief_id)
        limits["fact_ids"] = [str(fact["id"]) for fact in facts]

    values = build_values(bundle, tier, lang, limits)
    system_text = assemble_head(bundle, values)
    user_json = build_user_data(brief_text(brief), tier, limits, facts)
    body = build_request_body(model, system_text, user_json, bundle["schema"],
                              tier, topic, limits)
    # D-BOUND / PERF-02D-2: the bound's input term is the request body's UTF-8
    # byte length, the same serialisation post_responses sends.
    request_bytes = len(json.dumps(body, ensure_ascii=False).encode("utf-8"))

    requested_id = cell["run_id"]
    explicit = cell["run_id_explicit"]
    run_dir, run_id = pick_run_dir(args.out, requested_id, explicit, args.force)
    run_dir.mkdir(parents=True, exist_ok=True)
    clear_contract_files(run_dir)
    tap_id = cell.get("tap_run_id")

    request_doc = {
        "tap_base_url": args.tap_base_url,
        "endpoint": "/responses",
        "model": model,
        "run_id": run_id,
        "tier": tier,
        "language": lang,
        "language_name": LANGUAGE_NAMES[lang],
        "preset": topic,
        "brief_id": brief_id,
        "prompt_version": bundle["prompt_version"],
        "prompt_files": bundle["prompt_hashes"],
        "tap_run_id": tap_id,
        "dry_run": bool(args.dry_run),
        "timeout_s": args.timeout,
        "length": limits,
        "length_table": str(args.length),
        "head_tokens_estimate": sv.token_count(system_text, lang),
        "request_body_bytes": request_bytes,
        "body": body,
    }
    (run_dir / "request.json").write_text(
        json.dumps(request_doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    if args.dry_run:
        print(f"cell {run_id} dry-run request.json written (no call)")
        return {"run_id": run_id, "outcome": "dry_run", "cost": 0.0,
                "cost_bound": None, "charge": 0.0, "estimated": False,
                "latency_ms": 0, "usage": {}, "http_status": None, "run_dir": run_dir}

    if spent >= args.max_usd:
        raise BudgetStop(spent, args.max_usd)

    started_at = utc_now()
    started = time.monotonic()
    attempt = 1
    attempts: list[dict] = []
    result = {"status": 0, "payload": None, "raw": "", "retry_after": None,
              "request_id": None}
    while True:
        result = post_responses(args.tap_base_url, body, api_key,
                                f"{run_id}:writer:{attempt}", timeout=args.timeout)
        outcome, reason = classify(result["status"], result["payload"])
        usage = usage_record(result["payload"])
        cost = compute_cost(model, usage, rates)
        bound = None if cost is not None else cost_bound_usd(
            model, usage, rates, request_bytes, limits)
        attempts.append({"attempt": attempt, "http_status": result["status"],
                         "outcome": outcome, "incomplete_reason": reason,
                         "usage": usage, "cost_usd": cost, "cost_bound_usd": bound})
        # D-TIMEOUT: a transport timeout / status 0 is NOT retried (the call is
        # still in flight upstream and billed); only 5xx and non-funds 429 are.
        retryable = (result["status"] >= 500
                     or (result["status"] == 429 and outcome != "funds_exhausted"))
        if not retryable or attempt >= MAX_ATTEMPTS:
            break
        retry_after = result["retry_after"]
        try:
            delay = float(retry_after) if retry_after else DEFAULT_RETRY_AFTER_S
        except (TypeError, ValueError):
            delay = DEFAULT_RETRY_AFTER_S
        time.sleep(min(max(delay, 0.0), 30.0) + random.random() * 0.25)
        attempt += 1
    latency_ms = int((time.monotonic() - started) * 1000)
    finished_at = utc_now()

    # PERF-02D-1: the cell's own cost/bound come from the last attempt, but every
    # attempt is charged (the guard and the summary must not under-count).
    usage = attempts[-1]["usage"]
    cost = attempts[-1]["cost_usd"]
    bound = attempts[-1]["cost_bound_usd"]
    charge = sum(
        a["cost_usd"] if a["cost_usd"] is not None else (a["cost_bound_usd"] or 0.0)
        for a in attempts)
    estimated = any(a["cost_bound_usd"] is not None for a in attempts)

    payload = result["payload"]
    if result["status"] == 0:
        response_doc = payload
    elif payload is None:
        response_doc = {"error": "unparsable_response_body", "raw": result["raw"][:4000]}
    else:
        response_doc = payload
    (run_dir / "response.json").write_text(
        json.dumps(response_doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    story: dict | None = None
    json_valid = False
    shape_error: str | None = None
    if outcome == "ok":
        story, parse_error = extract_story(payload)
        if story is None:
            outcome = "invalid_output"
            reason = reason or parse_error
        else:
            shape_error = sv.shape_reason(story)
            if shape_error:
                # D-SHAPE / SEC-02D-2: never derive from a malformed story.
                outcome = "invalid_output"
                reason = reason or f"schema_shape: {shape_error}"
                story = None
            else:
                json_valid = True

    issues: list[dict] = []
    body_tokens = 0
    segments = 0
    keywords_total = 0
    processing_error: str | None = None
    if story is not None:
        (run_dir / "story.json").write_text(
            json.dumps(story, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        # SEC-02D-2: a failure while deriving/validating still writes metrics.json
        # and still counts the spend, instead of killing the batch after a paid call.
        try:
            body_text = sv.derive_body(story)
            keyword_list = sv.derive_keywords(story)
            body_tokens = sv.token_count(body_text, lang)
            segments = len(story.get("paragraphs") or []) + 2
            keywords_total = len(keyword_list)
            (run_dir / "video_script.txt").write_text(body_text + "\n", encoding="utf-8")
            (run_dir / "video_terms.json").write_text(
                json.dumps(keyword_list, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8")
            issues = sv.validate(story, tier, lang, limits)
        except (AttributeError, TypeError, ValueError, RecursionError) as exc:
            processing_error = f"processing_error: {exc}"
            outcome = "invalid_output"
            reason = reason or processing_error

    notes = cost_note(model, usage, rates) or ""
    if bound is not None:
        notes = (notes + "; " if notes else "") + \
            f"spend counted at the conservative bound ${bound:.6f}"
    if attempt > 1:
        notes = (notes + "; " if notes else "") + f"retried once (attempt {attempt})"
    if processing_error:
        notes = (notes + "; " if notes else "") + processing_error

    metrics = {
        "run_id": run_id,
        "model": model,
        "tier": tier,
        "language": lang,
        "preset": topic,
        "brief_id": brief_id,
        "provider_request_id": result["request_id"] or (
            payload.get("id") if isinstance(payload, dict) else None),
        "http_status": result["status"],
        "attempt": len(attempts),
        "attempts": attempts,
        "timeout_s": args.timeout,
        "latency_ms": latency_ms,
        "started_at": started_at,
        "finished_at": finished_at,
        "outcome": outcome,
        "json_valid": json_valid,
        "shape_error": shape_error,
        "incomplete_reason": reason,
        "usage": usage,
        "tokens": {"body_tokens": body_tokens, "segments": segments,
                   "keywords_total": keywords_total},
        "cost_usd": cost,
        "cost_bound_usd": bound,
        "charge_usd": charge,
        "validator_issues": issues,
        "notes": notes,
    }
    write_json_atomic(run_dir / "metrics.json", metrics)

    blocking = len(sv.blocking_issues(issues))
    print(f"cell {run_id} outcome={outcome} http={result['status']} "
          f"out_tokens={usage.get('output_tokens', 0)} cost={cost if cost is not None else 'null'} "
          f"validator_blocking={blocking}")
    return {"run_id": run_id, "outcome": outcome, "cost": cost,
            "cost_bound": bound, "charge": charge, "estimated": estimated,
            "latency_ms": latency_ms, "usage": usage, "http_status": result["status"],
            "run_dir": run_dir}


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="story_harness.py",
        description="M0 story harness: one schema-valid story.v1 per call, through "
                    "the 01 usage tap. Relative paths resolve from the repository root.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="exit codes: 0 ok | 1 check failed (validator issues, budget) | "
               "2 usage or environment error")
    parser.add_argument("--preset", metavar="{ghost,spiritual,true_crime}",
                        help="topic preset for a single run")
    parser.add_argument("--brief", help="inline brief text")
    parser.add_argument("--brief-id", help="brief id (b1..b9) from the briefs file")
    parser.add_argument("--brief-ids", help="comma-separated brief ids")
    parser.add_argument("--briefs-file", action="append", default=None,
                        help="briefs JSON file (repeatable; default prompts/fixtures/briefs.<lang>.json)")
    parser.add_argument("--lang", metavar="{vi,en}", default=None,
                        help="filter to one language (validation/defaults fall back to "
                             "vi when it is absent). Without it every cell keeps the "
                             "language its own brief records; a brief whose language "
                             "differs from an explicit --lang is skipped with a note "
                             "(exit 2 if none remains)")
    parser.add_argument("--model", metavar="{gpt-6-luna,gpt-6-sol}",
                        help="default gpt-6-luna (both models with --matrix)")
    parser.add_argument("--tier", metavar="{short,standard,long}", default="short")
    parser.add_argument("--out", default=str(DEFAULT_OUT))
    parser.add_argument("--run-id", help="override the derived cell id (exactly one cell)")
    parser.add_argument("--dry-run", action="store_true",
                        help="assemble + write request.json, make no call")
    parser.add_argument("--matrix", action="store_true",
                        help="all presets x briefs x models from --briefs-file(s)")
    parser.add_argument("--only-missing", action="store_true",
                        help="skip finished cells (parseable metrics.json); default for --matrix")
    parser.add_argument("--retry-failed", action="store_true",
                        help="with --only-missing/--matrix: re-run cells whose metrics.json "
                             "records a non-ok outcome (the earlier dir is kept, "
                             "the retry lands in <run_id>-r2)")
    parser.add_argument("--force", action="store_true", help="overwrite an existing run dir")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_S,
                        help=f"client timeout in seconds for one call (default {DEFAULT_TIMEOUT_S:g}; "
                             "D-TIMEOUT: a transport timeout is recorded "
                             "provider_unavailable and never retried)")
    parser.add_argument("--max-usd", type=float, default=5.0)
    parser.add_argument("--sleep-ms", type=int, default=200)
    parser.add_argument("--key-service", default=DEFAULT_KEY_SERVICE)
    parser.add_argument("--tap-base-url", default=DEFAULT_TAP_BASE_URL)
    parser.add_argument("--prompts-dir", default=str(DEFAULT_PROMPTS_DIR),
                        help="prompts folder (default prompts/)")
    parser.add_argument("--length", default=str(DEFAULT_LENGTH),
                        help="LENGTH.v1.json (default trial/tools/LENGTH.v1.json)")
    parser.add_argument("--prices", default=str(DEFAULT_PRICES),
                        help="D14 rates file (default trial/prices.json); a model row "
                             "with input/cached_input/output is required before a call, "
                             "plus cache_write whenever the response reports it")
    parser.add_argument("--facts", metavar="FACT_SHEET_JSON",
                        help="with --validate-only: the fact sheet whose ids the story may "
                             "cite (default: the brief's facts from --brief-id)")
    parser.add_argument("--validate-only", metavar="STORY_JSON",
                        help="run the validator on a story.json and exit")
    return parser


def _fact_ids_for_validate(args, lang: str, prompts_dir: Path) -> list[str] | None:
    """D-FACTS / CR-2: the fact ids a story may cite on `--validate-only`, so a
    true-crime story validates against its own brief's facts instead of raising
    false `unknown_fact_ref` issues. `--facts` wins; otherwise the ids come from
    the brief named by `--brief-id` (its topic decides whether a sheet applies).
    None means "no sheet": every `fact_refs` entry is then unknown."""
    if args.facts:
        data = load_json(repo_path(args.facts))
        facts = data.get("facts") if isinstance(data, dict) else data
        facts = [fact for fact in (facts or []) if isinstance(fact, dict)]
        if args.brief_id and any(fact.get("brief_id") for fact in facts):
            facts = [fact for fact in facts
                     if str(fact.get("brief_id")) == args.brief_id]
        return [str(fact.get("id")) for fact in facts]
    if not args.brief_id:
        return None
    files = [repo_path(path) for path in (args.briefs_file or [])] or briefs_for_lang(lang)
    briefs = load_briefs(files)
    brief = next((b for b in briefs if str(b.get("id")) == args.brief_id), None)
    if brief is None:
        fail(f"unknown brief id {args.brief_id!r} in "
             + ", ".join(str(path) for path in files))
    if brief.get("topic") != "true_crime":
        return []
    sheet = load_json(prompts_dir / "fixtures" / "fact-sheet.true_crime.v1.json")
    facts = [fact for fact in (sheet.get("facts") or []) if isinstance(fact, dict)]
    return [str(fact.get("id")) for fact in facts
            if str(fact.get("brief_id")) == args.brief_id]


def _validate_only(args) -> int:
    lang = check_choice("--lang", args.lang, LANGS) if args.lang else DEFAULT_LANG
    tier = check_choice("--tier", args.tier, TIERS)
    table = load_length_table(repo_path(args.length))
    story = load_json(repo_path(args.validate_only))
    limits = limits_for(table, tier, lang)
    fact_ids = _fact_ids_for_validate(args, lang, repo_path(args.prompts_dir))
    if fact_ids is not None:
        limits["fact_ids"] = fact_ids
    issues = sv.validate(story, tier, lang, limits)
    print(json.dumps(issues, ensure_ascii=False, indent=2))
    return 1 if sv.blocking_issues(issues) else 0


def _briefs_file_language(path: Path) -> str | None:
    """FR-1 fallback: the `.vi`/`.en` a `briefs.<lang>.json` name records."""
    match = _BRIEFS_FILE_RE.match(path.name)
    return match.group(1) if match else None


def _brief_language(brief: dict, source: Path | None, default: str) -> str:
    """FR-1: the brief's own `language` decides the cell's language, so the same
    id in `briefs.vi.json` and `briefs.en.json` yields one `vi-…` and one `en-…`
    cell. `--lang` never overrides it; it only filters (see `_select_briefs`).
    A brief with no `language` falls back to its file name, then to `default`."""
    value = brief.get("language")
    if value is None and source is not None:
        value = _briefs_file_language(source)
    if value is None:
        value = default
    where = f" in {source}" if source is not None else ""
    return check_choice(f"brief {brief.get('id')!r} language{where}", str(value), LANGS)


def _load_briefs_with_source(paths: list[Path]) -> list[tuple[Path, dict]]:
    """`load_briefs`, but keeps which file each brief came from, so `--brief-ids`
    resolves per file/language instead of last-file-wins (FR-1)."""
    entries: list[tuple[Path, dict]] = []
    for path in paths:
        entries.extend((path, brief) for brief in load_briefs([path]))
    return entries


def _note_skipped(skipped: list[tuple[str, str]], lang_filter: str) -> None:
    """FR-1: a brief in another language is skipped visibly, never silently."""
    for brief_id, lang in skipped:
        print(f"story_harness: note: skipping brief {brief_id} "
              f"(language={lang}, not --lang {lang_filter})")


def _select_briefs(args, lang_filter: str | None) -> list[dict]:
    """Return the selected briefs, each carrying its resolved `language`.

    FR-1: `--lang` is a filter, not the source of truth. Two `--briefs-file`
    inputs holding the same ids (b1..b9 in vi and en) select both, and a brief
    whose language conflicts with an explicit `--lang` is skipped with a note
    (exit 2 if nothing remains)."""
    default_lang = lang_filter or DEFAULT_LANG
    files = [repo_path(path) for path in (args.briefs_file or [])]
    paths = files or briefs_for_lang(default_lang)
    if args.brief:
        return [{"id": "inline", "topic": args.preset, "brief": args.brief,
                 "language": default_lang}]
    ids = []
    if args.brief_id:
        ids.append(args.brief_id)
    if args.brief_ids:
        ids.extend(part.strip() for part in args.brief_ids.split(",") if part.strip())
    if ids:
        by_id: dict[str, list[tuple[dict, str]]] = {}
        for source, brief in _load_briefs_with_source(paths):
            by_id.setdefault(str(brief["id"]), []).append(
                (brief, _brief_language(brief, source, default_lang)))
        missing = [brief_id for brief_id in ids if brief_id not in by_id]
        if missing:
            fail(f"unknown brief id(s) {missing} in "
                 + ", ".join(str(path) for path in paths))
        chosen: list[dict] = []
        skipped: list[tuple[str, str]] = []
        for brief_id in ids:
            for brief, lang in by_id[brief_id]:
                if lang_filter and lang != lang_filter:
                    skipped.append((brief_id, lang))
                    continue
                chosen.append({**brief, "language": lang})
        _note_skipped(skipped, lang_filter)
        if not chosen:
            fail(f"--lang {lang_filter} skipped every requested brief id in "
                 + ", ".join(str(path) for path in paths)
                 + f" (their language is not {lang_filter})")
        return chosen
    if args.matrix:
        chosen = []
        skipped = []
        for source, brief in _load_briefs_with_source(paths):
            lang = _brief_language(brief, source, default_lang)
            if lang_filter and lang != lang_filter:
                skipped.append((str(brief.get("id")), lang))
                continue
            if args.preset and brief.get("topic") != args.preset:
                continue
            chosen.append({**brief, "language": lang})
        _note_skipped(skipped, lang_filter)
        if not chosen:
            if skipped:
                fail(f"--lang {lang_filter} skipped all {len(skipped)} brief(s) in "
                     + ", ".join(str(path) for path in paths)
                     + f" (their language is not {lang_filter})")
            fail("no briefs"
                 + (f" with topic {args.preset!r}" if args.preset else "")
                 + " in " + ", ".join(str(path) for path in paths))
        return chosen
    fail("one of --brief, --brief-id, --brief-ids, --briefs-file or --matrix is required")


def _build_cells(args, briefs: list[dict]) -> list[dict]:
    models = [args.model] if args.model else (
        list(MODELS) if args.matrix else ["gpt-6-luna"])
    if args.run_id:
        check_safe_id("--run-id", args.run_id)
    cells = []
    for brief in briefs:
        topic = args.preset or brief.get("topic")
        if not topic:
            fail(f"brief {brief.get('id')} has no topic and --preset was not given")
        if args.preset and brief.get("topic") and brief["topic"] != args.preset:
            fail(f"brief {brief.get('id')} has topic {brief['topic']!r}, not {args.preset!r}")
        # FR-1: the cell's language is the brief's, never `--lang`.
        lang = str(brief.get("language") or "")
        if lang not in LANGS:
            fail(f"brief {brief.get('id')} has no resolved language (FR-1)")
        for model in models:
            run_id = args.run_id or derive_run_id(lang, topic, str(brief.get("id")),
                                                  model, args.tier)
            check_safe_id("derived run id", run_id)
            cells.append({"lang": lang, "preset": topic, "tier": args.tier,
                          "model": model, "brief": brief, "run_id": run_id,
                          "run_id_explicit": bool(args.run_id)})
    return cells


def _run(args) -> int:
    if args.validate_only:
        return _validate_only(args)
    lang_filter = check_choice("--lang", args.lang, LANGS) if args.lang else None
    tier = check_choice("--tier", args.tier, TIERS)
    if args.model:
        check_choice("--model", args.model, tuple(MODELS))
    if args.preset:
        check_choice("--preset", args.preset, TOPICS)
    if args.sleep_ms < 0:
        fail("--sleep-ms must be >= 0")
    if args.timeout <= 0:
        fail("--timeout must be > 0 seconds")

    args.out = repo_path(args.out)
    args.prompts_dir = repo_path(args.prompts_dir)
    table = load_length_table(repo_path(args.length))
    rates = load_rates(repo_path(args.prices))
    briefs = _select_briefs(args, lang_filter)
    cells = _build_cells(args, briefs)
    # D-RUNID / CR-1: --run-id names exactly one cell, checked before any call.
    if args.run_id and len(cells) > 1:
        fail(f"--run-id names one run dir, but {len(cells)} cells would be built; "
             f"drop --brief-ids/--briefs-file/--matrix to use it")
    only_missing = args.only_missing or args.matrix

    if not args.dry_run:
        for model in sorted({cell["model"] for cell in cells}):
            require_call_rates(model, rates, repo_path(args.prices))
        assert_tap(args.tap_base_url)
    api_key = None if args.dry_run else load_secret(args.key_service)

    results: list[dict] = []
    spent = 0.0
    estimated = False
    lock_state: dict = {"id": None, "owned": set()}
    try:
        for index, cell in enumerate(cells):
            state = _cell_outcome(args.out / cell["run_id"])
            if only_missing and state is not None and (state == "ok" or not args.retry_failed):
                print(f"cell {cell['run_id']} skipped (exists, outcome={state})")
                continue
            if index and args.sleep_ms:
                time.sleep(args.sleep_ms / 1000.0)
            cell["tap_run_id"] = (peek_run_id(cell["lang"]) if args.dry_run
                                  else lock_for(cell["lang"], lock_state))
            result = run_cell(cell, args, table, rates, api_key, spent)
            spent += result["charge"]
            estimated = estimated or result["estimated"]
            results.append(result)
    finally:
        if not args.dry_run:
            release_run_lock(lock_state["owned"])

    _print_summary(results, spent, estimated)
    return 0


def _print_summary(results: list[dict], spent: float, estimated: bool = False) -> None:
    outcomes = {}
    for result in results:
        outcomes[result["outcome"]] = outcomes.get(result["outcome"], 0) + 1
    usage_totals = {"input_tokens": 0, "output_tokens": 0,
                    "cached_tokens": 0, "reasoning_tokens": 0}
    for result in results:
        for key in usage_totals:
            usage_totals[key] += (result["usage"] or {}).get(key, 0) or 0
    latencies = [result["latency_ms"] for result in results if result["latency_ms"]]
    p50 = int(statistics.median(latencies)) if latencies else 0
    print("summary: cells={cells} ok={ok} invalid={invalid} moderated={moderated} "
          "provider_error={provider_error} provider_unavailable={provider_unavailable} "
          "funds_exhausted={funds_exhausted} tokens_in={input_tokens} "
          "tokens_out={output_tokens} cached={cached_tokens} "
          "reasoning={reasoning_tokens} p50_ms={p50} spend_usd={spent:.6f} "
          "estimated={estimated}".format(
              cells=len(results), ok=outcomes.get("ok", 0),
              invalid=outcomes.get("invalid_output", 0),
              moderated=outcomes.get("moderated", 0),
              provider_error=outcomes.get("provider_error", 0),
              provider_unavailable=outcomes.get("provider_unavailable", 0),
              funds_exhausted=outcomes.get("funds_exhausted", 0), p50=p50, spent=spent,
              estimated=str(estimated).lower(),
              **usage_totals))


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return _run(args)
    except EnvironmentError as exc:
        print(f"story_harness: {exc}", file=sys.stderr)
        return 2
    except BudgetStop as exc:
        print(f"story_harness: budget exceeded spent={exc.spent:.6f} cap={exc.cap:.2f}",
              file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
