#!/usr/bin/env python3
"""finish_run.py — turn one finished MPT task into one row of trial/measurements.csv.

01.3.1-T3 step C. Stdlib only, Python 3.12.

Usage:
  finish_run.py <run_id> --task-id <mpt task id> [--mpt-log P] [--proxy-log P]
                [--script P] [--output P] [--terms-count N] [--issue TEXT]...
                [--status failed] [--dry-run]
  finish_run.py <run_id> --abort
  finish_run.py <run_id> --dashboard-cost <usd> --dashboard-day <YYYY-MM-DD>
  finish_run.py --help

Exit codes: 0 ok / 1 check failed / 2 usage or env error (D9).
"""

import argparse
import ast
import csv
import fcntl
import hashlib
import io
import json
import math
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import unicodedata
from datetime import datetime, timedelta, timezone
from pathlib import Path

# ---------------------------------------------------------------------------
# Constants (one place, per spec)

COLUMNS = [
    "run_id", "date", "mpt_commit", "vieneu_commit", "language", "topic",
    "target_minutes", "story_model", "story_input_tokens",
    "story_output_tokens", "story_cost_usd", "image_model", "image_quality",
    "image_size", "image_count", "image_output_tokens_total",
    "image_cost_usd", "moderation_blocked_count", "tts_seconds_audio",
    "tts_wall_seconds", "whisper_wall_seconds", "render_wall_seconds",
    "total_wall_seconds", "ram_peak_vieneu_mb", "ram_peak_whisper_mb",
    "ram_peak_render_mb", "output_duration_sec", "output_size_mb",
    "words_in_story", "words_per_minute_measured", "issues", "schema_version",
    "run_status", "machine", "video_aspect", "voice_name", "story_source",
    "terms_count", "story_cached_input_tokens", "story_call_count",
    "image_quality_requested", "image_quality_returned",
    "image_input_text_tokens_total", "image_input_image_tokens_total",
    "image_tokens_per_image_avg", "image_cost_per_image_usd",
    "image_failed_count", "image_http_429_count", "story_wall_seconds",
    "image_wall_seconds", "subtitle_detected_language", "ram_peak_total_mb",
    "swap_peak_mb", "memory_pressure_max", "usd_per_output_minute",
    "local_compute_seconds_per_output_minute", "dashboard_cost_usd_check",
    "tts_chars",
]

# Stage markers on MPT log records (M9 + 01 CR-17, verified at pin 3d5f4e42).
AUDIO_END_RE = None  # Q1: end-of-audio line unconfirmed; audio ends at subtitle.
MARKERS = {
    "script": r"^\s*## generating video script",
    "terms": r"^\s*## generating video terms",
    "audio": r"^\s*## generating audio",
    "audio_end": AUDIO_END_RE,
    "subtitle": r"^\s*## generating subtitle",
    "materials": r"^\s*## downloading videos from (?P<source>\S+)",
    "combine": r"^\s*## combining video: \d+ =>",
    "final": r"^\s*## generating video: \d+ =>",
    "finished": r"task (?P<id>\S+) finished",
    "failed": r"task failed, task_id: (?P<id>[^,\s]+), stage: (?P<stage>[^,\s]+)",
}
MARKER_SET = 1
STAGE_ORDER = ["script", "terms", "audio", "subtitle", "materials",
               "combine", "final"]

NA_RE = re.compile(r"^n/a:[a-z0-9-]+$")
HOP_TOLERANCE_S = 5

RUN_ID_RE = re.compile(r"^\d{8}-\d{4}-(vi|en)-(landscape|portrait)-\d{2}$")
# MPT task ids are UUIDs. --task-id is joined into $NTS_HOME/src/mpt/storage/
# tasks/<id> and written into tracked run.json/stages.json, so it is validated
# before any path join or write (SEC-T6-1: pathlib drops the base on an
# absolute right-hand side, and '..' walks out of storage/tasks). Matched with
# fullmatch so a trailing newline cannot slip past the `$` anchor.
TASK_ID_RE = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
    re.IGNORECASE)

# MPT log record headers (M9; level is NOT padded in the tee'd log, CR-17):
#   2026-09-25 18:41:20 | INFO | "app/services/task.py:920": combine - msg
# and loguru's default startup form (millis, unquoted module:func:line):
#   2026-09-25 18:41:20.123 | INFO     | module:func:123 - msg
_HEADER_RES = [
    re.compile(
        r"(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})"
        r" \| (?P<level>[A-Z]+) *\| \"(?P<src>[^\"]+)\": (?P<func>\S+) - "),
    re.compile(
        r"(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})"
        r" \| (?P<level>[A-Z]+) *\| (?P<src>\S+:\S+:\d+) - "),
]
_ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")
_WHISPER_LANG_RE = re.compile(
    r"detected language: '(?P<l>\w+)', probability: (?P<p>[\d.]+)")
_WHISPER_ELAPSED_RE = re.compile(r"complete, elapsed: (?P<s>[\d.]+) s")
_TERMS_DONE_RE = re.compile(r"completed\s*:.*?(\[[^\]]*\])", re.S)
_TASK_SUBMIT_RE = re.compile(r"task (\S+) submitted")
_SNAPSHOT_SUFFIX_RE = re.compile(r"-\d{4}-\d{2}-\d{2}$")

RSS_RAW_FIELDS = (
    "ts_utc", "vieneu_pid", "vieneu_rss_kb", "vieneu_nproc",
    "proxy_pid", "proxy_rss_kb", "proxy_nproc",
    "mpt_pid", "mpt_rss_kb", "mpt_nproc",
    "webui_pid", "webui_rss_kb", "webui_nproc",
    "swap_used_mb", "pressure_level", "free_pct", "sample_ms",
)
RSS_CSV_HEADER = (
    "scope,start_utc,end_utc,samples,vieneu_peak_mb,proxy_peak_mb,"
    "mpt_peak_mb,webui_peak_mb,total_peak_mb,swap_start_mb,swap_peak_mb,"
    "pressure_max,free_pct_min,mpt_nproc_max,sample_ms_p95"
)
PRESSURE_NAMES = {"1": "normal", "2": "warn", "4": "critical"}

# Closed collector vocabulary (schema.md). note:<text> comes only from --issue.
ISSUE_CODES = {
    "story-cost-terms-only", "cached-tokens-field-absent",
    "quality-not-echoed", "tap-no-lines", "tap-crash", "tap-busy",
    "tap-foreign-call", "swap-used", "pressure-warn",
}
ISSUE_PREFIXES = ("image-count-mismatch:", "stage-gap:", "dashboard-delta-",
                  "note:")

ENUM_COLS = {
    "language": ("vi", "en"),
    "run_status": ("calibration", "trial", "failed"),
    "video_aspect": ("landscape", "portrait"),
    "story_source": ("mpt", "plan02"),
    "memory_pressure_max": ("normal", "warn", "critical"),
}
# Columns that may legitimately hold non-numeric string values.
STRING_COLS = {
    "run_id", "date", "mpt_commit", "vieneu_commit", "language", "topic",
    "story_model", "image_model", "image_quality", "image_size", "issues",
    "run_status", "machine", "video_aspect", "voice_name", "story_source",
    "image_quality_requested", "image_quality_returned",
    "subtitle_detected_language", "memory_pressure_max",
}
# Columns the dictionary marks "never n/a" under a normal finished run. The
# tap-sourced ones are NOT listed here: --no-tap rows must still validate
# with n/a:no-tap (plan.md §9 T-P10).
NEVER_NA_COLS = {
    "run_id", "date", "mpt_commit", "vieneu_commit", "language", "topic",
    "target_minutes", "issues", "schema_version", "run_status", "machine",
    "video_aspect", "story_source",
}

SCHEMA_VERSION = "2"
PENDING_DASHBOARD = "n/a:pending-dashboard"


class CollectorError(Exception):
    def __init__(self, message, code=1):
        super().__init__(message)
        self.code = code


def die(msg, code=1):
    raise CollectorError(msg, code)


def _utc_z(dt):
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _parse_iso_z(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def _na(reason):
    return f"n/a:{reason}"


def _is_na(value):
    return isinstance(value, str) and NA_RE.match(value)


def _num(value):
    # SEC-0928-2: bool and non-finite values never enter cost math.
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return 0
    return value if math.isfinite(value) else 0


# ---------------------------------------------------------------------------
# MPT log parser (spec step 3, M9 + CR-17)

def _iter_records(text):
    """Yield (ts_str, level, src, msg) records; split at every header match.

    Records can be glued mid-line in the tee'd log (CR-17), so the scan runs
    over the whole text, not line by line. Bytes before the first header are
    dropped. The two header forms never overlap, so a plain sort works.
    """
    matches = []
    for rx in _HEADER_RES:
        matches.extend(rx.finditer(text))
    matches.sort(key=lambda m: m.start())
    for i, m in enumerate(matches):
        end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        yield m.group("ts"), m.group("level"), m.group("src"), \
            text[m.end():end]


def _marker_hit(key, msg):
    rx = MARKERS.get(key)
    if rx is None:
        return None
    return re.search(rx, msg, re.M)


def parse_mpt_log(path, task_id, started_at, allow_unfinished=False):
    """Parse the MPT API log for one task -> the stages.json dict.

    Raises CollectorError(1) when the task has no markers, or no end record
    and allow_unfinished is False. With allow_unfinished the window ends at
    the last record of the task (a task that died without logging an end
    record, --status failed).
    """
    try:
        raw = Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError as e:
        die(f"cannot read mpt log {path}: {e}", 2)
    text = _ANSI_RE.sub("", raw)
    floor = started_at.astimezone(timezone.utc) - timedelta(
        seconds=HOP_TOLERANCE_S)

    records = []
    tz_offset = None
    for ts_str, level, src, msg in _iter_records(text):
        naive = datetime.strptime(
            ts_str, "%Y-%m-%d %H:%M:%S.%f" if "." in ts_str
            else "%Y-%m-%d %H:%M:%S")
        aware_local = naive.astimezone()
        utc = aware_local.astimezone(timezone.utc)
        if tz_offset is None:
            off = aware_local.utcoffset()
            total_min = int(off.total_seconds() // 60)
            sign = "+" if total_min >= 0 else "-"
            tz_offset = f"{sign}{abs(total_min) // 60:02d}:" \
                        f"{abs(total_min) % 60:02d}"
        if utc >= floor:
            records.append((utc, level, src, msg))
    if tz_offset is None:
        die(f"mpt log has no parseable records: {path}")

    # Scope to the task: the first stage marker after the floor opens the
    # window; it ends at `task <id> finished` / `task failed … task_id: <id>`.
    window = []
    end_ts = None
    failed_stage = None
    first_marker_ts = None
    boundary_ts = None  # first record belonging to a different task
    for utc, level, src, msg in records:
        if first_marker_ts is None:
            if any(_marker_hit(k, msg) for k in STAGE_ORDER):
                first_marker_ts = utc
            else:
                continue
        fin = _marker_hit("finished", msg)
        fail = _marker_hit("failed", msg)
        if (fin or fail) and (fin or fail).group("id") == task_id:
            end_ts = utc
            if fail:
                failed_stage = fail.group("stage")
            break
        # A foreign task's submit/finish record marks where this task's
        # window must end. Keep scanning afterwards in case our own end
        # record still appears (queued submits during a live task).
        if boundary_ts is None:
            if fin or fail:
                boundary_ts = utc
            else:
                sub = _TASK_SUBMIT_RE.search(msg)
                if sub and sub.group(1) != task_id:
                    boundary_ts = utc
        window.append((utc, msg))

    if first_marker_ts is None:
        die(f"no stage markers for task {task_id} in {path}")
    if end_ts is None:
        if not allow_unfinished:
            die(f"no end record for task {task_id} in {path} "
                "(use --status failed if the task died silently)")
        # Unfinished: the window ends at this task's last record, not at
        # the last record of the shared log (later tasks must not stretch
        # total_wall_seconds / the last stage window).
        if boundary_ts is not None:
            window = [r for r in window if r[0] < boundary_ts]
        end_ts = window[-1][0] if window else first_marker_ts

    # Stage windows: marker ts -> next present marker ts (or end).
    starts = {}
    for utc, msg in window:
        for key in STAGE_ORDER:
            if key in starts:
                continue
            if _marker_hit(key, msg):
                starts[key] = utc

    present = [k for k in STAGE_ORDER if k in starts]
    windows = {}
    for i, key in enumerate(present):
        nxt = present[i + 1] if i + 1 < len(present) else None
        w_end = starts[nxt] if nxt else end_ts
        windows[key] = {
            "start": _utc_z(starts[key]),
            "end": _utc_z(w_end),
            "seconds": round((w_end - starts[key]).total_seconds(), 3),
        }

    # Counts inside the task window.
    image_failed = 0
    image_rendered = 0
    whisper = {"language": None, "probability": None, "elapsed_s": None}
    terms_list = None
    terms_seen = False
    for utc, msg in window:
        image_failed += msg.count("openai image generation failed")
        image_rendered += msg.count("image material rendered:")
        m = _WHISPER_LANG_RE.search(msg)
        if m:
            whisper["language"] = m.group("l")
            whisper["probability"] = float(m.group("p"))
        m = _WHISPER_ELAPSED_RE.search(msg)
        if m:
            whisper["elapsed_s"] = float(m.group("s"))
        if not terms_seen:
            if _marker_hit("terms", msg):
                terms_seen = True
        elif terms_list is None:
            m = _TERMS_DONE_RE.search(msg)
            if m:
                try:
                    terms_list = ast.literal_eval(m.group(1))
                except (ValueError, SyntaxError):
                    # Unparseable list -> terms_count n/a:marker-missing
                    # (schema col 38); leaving None lets a later
                    # completed: line retry.
                    pass

    total_seconds = round((end_ts - first_marker_ts).total_seconds(), 3)
    sum_windows = round(sum(w["seconds"] for w in windows.values()), 3)
    return {
        "marker_set": MARKER_SET,
        "tz_offset": tz_offset,
        "task_id": task_id,
        "windows": windows,
        "total_seconds": total_seconds,
        "counts": {
            "image_failed": image_failed,
            "image_rendered": image_rendered,
            "terms_count": (len(terms_list) if terms_list is not None
                            else None),
        },
        "whisper": whisper,
        "coverage": {
            "first_marker": _utc_z(first_marker_ts),
            "end": _utc_z(end_ts),
            "gaps_seconds": round(total_seconds - sum_windows, 3),
        },
        "failed_stage": failed_stage,
    }


# ---------------------------------------------------------------------------
# Tap JSONL aggregation (D6 fields; schema cols 8-18, 39-48)

def _load_prices(path):
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except OSError as e:
        die(f"cannot read prices file {path}: {e}", 2)
    except json.JSONDecodeError as e:
        die(f"prices file unparseable {path}: {e}", 2)
    return data


def _price_for(prices, model):
    """model_match: exact key, or key followed by -YYYY-MM-DD snapshot suffix.
    `prices` is the whole prices.json document."""
    models = prices.get("models") or {}
    if model in models:
        return models[model]
    base = _SNAPSHOT_SUFFIX_RE.sub("", model or "")
    if base and base in models:
        return models[base]
    return None


def _image_n(line):
    """Images billed per request (n defaults to 1 on the API; the tap rejects
    anything else)."""
    n = (line.get("request") or {}).get("n", 1)
    return n if isinstance(n, int) and not isinstance(n, bool) and n > 0 else 1


def _per_image_cost(line, rate, prices):
    """USD for a per_image-priced image line; None when the currency is
    unconvertible (unknown unit, or a VND rate without fx_vnd_per_usd)."""
    amount = rate.get("per_image")
    if not isinstance(amount, (int, float)) or isinstance(amount, bool) \
            or not math.isfinite(amount) or amount < 0:
        return None  # SEC-0928-2
    currency = rate.get("currency", "USD")
    if currency == "USD":
        return _image_n(line) * amount
    if currency == "VND":
        fx = (prices.get("fx_vnd_per_usd") or {}).get("value")
        if not isinstance(fx, (int, float)) or isinstance(fx, bool) \
                or not math.isfinite(fx) or fx <= 0:
            return None
        return _image_n(line) * amount / fx
    return None


def _line_cost(line, prices):
    """USD for one JSONL line; None when unpriceable (no price, responses
    passthrough host tokens — DEC-11)."""
    u = line.get("usage") or {}
    ep = line.get("endpoint")
    rate = _price_for(prices, line.get("model"))
    if rate is None:
        return None
    if ep == "/chat/completions":
        det = u.get("prompt_tokens_details") or {}
        c = _num(det.get("cached_tokens"))
        r_in = _num(rate.get("input"))
        return ((_num(u.get("prompt_tokens")) - c) * r_in
                + c * _num(rate.get("cached_input", r_in))
                + _num(u.get("completion_tokens"))
                * _num(rate.get("output"))) / 1e6
    if ep == "/images/generations":
        if rate.get("unit") == "per_image":
            # Flat per-image charge (owner-checked relay price, 2026-09-28);
            # applies to via=responses lines too — DEC-11's "host-model tokens
            # are unpriceable" does not apply to a flat per-image rate.
            return _per_image_cost(line, rate, prices)
        if line.get("via") == "responses":
            return None  # host-model tokens only; no image price (DEC-11)
        det = u.get("input_tokens_details") or {}
        return (_num(det.get("text_tokens")) * _num(rate.get("text_input"))
                + _num(det.get("image_tokens"))
                * _num(rate.get("image_input"))
                + _num(u.get("output_tokens"))
                * _num(rate.get("image_output"))) / 1e6
    return None


def _read_jsonl(path):
    lines = []
    p = Path(path) if path else None
    if p is None or not p.is_file():
        return None
    for raw in p.read_text(encoding="utf-8").splitlines():
        if not raw.strip():
            continue
        try:
            lines.append(json.loads(raw))
        except json.JSONDecodeError:
            continue
    return lines


def _select_lines(lines, issues):
    """Split JSONL lines into chat/image lists, applying SEC-10 exclusions."""
    chat, image = [], []
    for line in lines:
        ep = line.get("endpoint")
        if ep not in ("/chat/completions", "/images/generations"):
            continue
        if line.get("error_code") == "tap_busy":
            issues.add("tap-busy")
            continue
        if line.get("http_status") == 401 or line.get("model") is None:
            issues.add("tap-foreign-call")  # NTS01-SEC-10: excluded from sums
            continue
        err = line.get("error_code")
        if isinstance(err, str) and err.startswith("tap_"):
            issues.add("tap-crash")  # tap-side failure reaching upstream log
        (chat if ep == "/chat/completions" else image).append(line)
    return chat, image


def aggregate_tap(jsonl_path, prices_path, story_source, tap_disabled):
    """Return (cols, issues) for the tap-sourced columns."""
    cols = {}
    issues = set()
    tap_cols_all = COLUMNS[7:18] + COLUMNS[38:48]
    if tap_disabled:
        for name in tap_cols_all:
            cols[name] = _na("no-tap")
        return cols, issues

    lines = _read_jsonl(jsonl_path)
    if lines is None:
        lines = []
        issues.add("tap-no-lines")
    prices = _load_prices(prices_path)
    chat_lines, image_lines = _select_lines(lines, issues)
    if not chat_lines and not image_lines:
        issues.add("tap-no-lines")

    # --- story side (cols 8-11, 39, 40) ---
    chat200 = [l for l in chat_lines if l.get("http_status") == 200]
    if story_source == "plan02" and not chat_lines:
        na = _na("story-source-plan02")
        for name in ("story_model", "story_input_tokens",
                     "story_output_tokens", "story_cost_usd",
                     "story_cached_input_tokens"):
            cols[name] = na
    elif not chat_lines:
        na = _na("no-llm-calls")
        for name in ("story_model", "story_input_tokens",
                     "story_output_tokens", "story_cost_usd",
                     "story_cached_input_tokens"):
            cols[name] = na
    else:
        cols["story_model"] = "+".join(
            sorted({str(l.get("model")) for l in chat_lines}))
        if any(l.get("usage") is None for l in chat200):
            na = _na("usage-absent")
            for name in ("story_input_tokens", "story_output_tokens",
                         "story_cost_usd", "story_cached_input_tokens"):
                cols[name] = na
        else:
            total_in = sum(_num(l["usage"].get("prompt_tokens"))
                           for l in chat200)
            total_out = sum(_num(l["usage"].get("completion_tokens"))
                            for l in chat200)
            cached = 0
            cached_seen = False
            cost = 0.0
            for l in chat200:
                u = l["usage"]
                det = u.get("prompt_tokens_details") or {}
                if "cached_tokens" in det:
                    cached_seen = True
                cached += _num(det.get("cached_tokens"))
                line_cost = _line_cost(l, prices)
                cost = None if (cost is None or line_cost is None) \
                    else cost + line_cost
            if not cached_seen:
                issues.add("cached-tokens-field-absent")
            cols["story_input_tokens"] = str(int(total_in))
            cols["story_output_tokens"] = str(int(total_out))
            cols["story_cached_input_tokens"] = str(int(cached))
            cols["story_cost_usd"] = (_na("no-price") if cost is None
                                      else f"{cost:.6f}")
    cols["story_call_count"] = str(len(chat_lines))
    if story_source == "plan02":
        issues.add("story-cost-terms-only")

    # --- image side (cols 12-18, 41-48 except 47) ---
    img200 = [l for l in image_lines if l.get("http_status") == 200]
    cols["moderation_blocked_count"] = str(
        sum(1 for l in image_lines
            if l.get("error_code") == "moderation_blocked"))
    cols["image_http_429_count"] = str(
        sum(1 for l in image_lines if l.get("http_status") == 429))
    if not image_lines:
        na = _na("no-images")
        for name in ("image_model", "image_quality", "image_size",
                     "image_output_tokens_total", "image_cost_usd",
                     "image_quality_requested", "image_quality_returned",
                     "image_input_text_tokens_total",
                     "image_input_image_tokens_total"):
            cols[name] = na
        cols["image_count"] = "0"
    else:
        cols["image_model"] = "+".join(
            sorted({str(l.get("model")) for l in image_lines}))
        cols["image_count"] = str(len(img200))
        echoed = [str((l.get("response") or {}).get("quality"))
                  for l in img200
                  if (l.get("response") or {}).get("quality") is not None]
        if echoed:
            dist = {}
            for q in echoed:
                dist[q] = dist.get(q, 0) + 1
            cols["image_quality"] = next(
                iter(dist)) if len(dist) == 1 else \
                max(dist, key=dist.get)
            cols["image_quality_returned"] = "|".join(
                f"{q}:{n}" for q, n in
                sorted(dist.items(), key=lambda kv: (-kv[1], kv[0])))
        else:
            requested = [str((l.get("request") or {}).get("quality"))
                         for l in img200
                         if (l.get("request") or {}).get("quality")
                         is not None]
            cols["image_quality"] = requested[0] if requested else "auto"
            cols["image_quality_returned"] = _na("not-echoed")
            issues.add("quality-not-echoed")
        sizes = []
        for l in img200:
            size = (l.get("response") or {}).get("size") or \
                (l.get("request") or {}).get("size")
            if size is not None and str(size) not in sizes:
                sizes.append(str(size))
        cols["image_size"] = "+".join(sizes) if sizes else _na("not-echoed")
        req_q = sorted({str((l.get("request") or {}).get("quality"))
                        for l in image_lines
                        if (l.get("request") or {}).get("quality")
                        is not None})
        cols["image_quality_requested"] = "+".join(req_q) \
            if req_q else "omitted"

        usage_absent = any(l.get("usage") is None for l in img200)
        if usage_absent:
            na = _na("usage-absent")
            for name in ("image_output_tokens_total",
                         "image_input_text_tokens_total",
                         "image_input_image_tokens_total"):
                cols[name] = na
        else:
            out_tok = sum(_num(l["usage"].get("output_tokens"))
                          for l in img200)
            dets = [(l["usage"].get("input_tokens_details") or {})
                    for l in img200]
            # O6 splits image input into text_tokens/image_tokens. The relay
            # (DEC-11 responses path) answers with input_tokens +
            # cached_tokens instead (observed 2026-09-26, T4), so the split
            # is unknowable: n/a, never a silent 0 (cols 43/44) and never a
            # cost that silently drops the input component (col 17). Both
            # keys are required: a line carrying only one of them is as
            # split-less as an empty details object (D-2/L-1). One such line
            # is enough — summing only the others would under-report.
            split_absent = any(
                not ("text_tokens" in d and "image_tokens" in d)
                for d in dets)
            cols["image_output_tokens_total"] = str(int(out_tok))
            if split_absent:
                for name in ("image_input_text_tokens_total",
                             "image_input_image_tokens_total"):
                    cols[name] = _na("usage-split-absent")
            else:
                t_in = sum(_num((l["usage"].get("input_tokens_details") or {})
                                .get("text_tokens")) for l in img200)
                i_in = sum(_num((l["usage"].get("input_tokens_details") or {})
                                .get("image_tokens")) for l in img200)
                cols["image_input_text_tokens_total"] = str(int(t_in))
                cols["image_input_image_tokens_total"] = str(int(i_in))
        # Col 17 per line (QA-0928-3, CRV-0928-3): a per_image line is priced
        # from request.n alone; only a token-priced line needs usage and the
        # full O6 input split.
        cost = 0.0
        cost_na = None
        for l in img200:
            rate = _price_for(prices, l.get("model"))
            if rate is not None and rate.get("unit") != "per_image":
                u = l.get("usage")
                if u is None:
                    cost_na = cost_na or "usage-absent"
                    continue
                d = u.get("input_tokens_details") or {}
                if not ("text_tokens" in d and "image_tokens" in d):
                    cost_na = cost_na or "usage-split-absent"
                    continue
            line_cost = _line_cost(l, prices)
            cost = None if (cost is None or line_cost is None) \
                else cost + line_cost
        if cost is None:
            cols["image_cost_usd"] = _na("no-price")
        elif cost_na:
            cols["image_cost_usd"] = _na(cost_na)
        else:
            cols["image_cost_usd"] = f"{cost:.6f}"

    # Derived per-image columns (45, 46).
    count = int(cols.get("image_count", "0") or 0)
    out_t = cols.get("image_output_tokens_total")
    cost_s = cols.get("image_cost_usd")
    if count == 0:
        cols["image_tokens_per_image_avg"] = _na("no-images")
        cols["image_cost_per_image_usd"] = _na("no-images")
    else:
        cols["image_tokens_per_image_avg"] = (
            f"{int(out_t) / count:.1f}" if out_t and not _is_na(out_t)
            else _na("component-na"))
        cols["image_cost_per_image_usd"] = (
            f"{float(cost_s) / count:.6f}" if cost_s and not _is_na(cost_s)
            else _na("component-na"))
    return cols, issues


def tap_day_cost(trial_runs_dir, prices_path, day):
    """Sum of per-line computed costs over every JSONL line whose ts falls in
    `day` (UTC, Q10), across all runs incl. no-run. Unpriceable lines = 0."""
    prices = _load_prices(prices_path)
    total = 0.0
    for jsonl in sorted(Path(trial_runs_dir).glob("*/openai_usage.jsonl")):
        lines = _read_jsonl(jsonl) or []
        for line in lines:
            if line.get("endpoint") not in ("/chat/completions",
                                          "/images/generations"):
                continue
            if line.get("http_status") != 200 or line.get("model") is None:
                continue
            if line.get("usage") is None:
                # CRV-0928-3: a per_image line is priced without usage.
                rate = _price_for(prices, line.get("model"))
                if line.get("endpoint") != "/images/generations" \
                        or rate is None or rate.get("unit") != "per_image":
                    continue
            try:
                ts = _parse_iso_z(str(line.get("ts")))
            except (ValueError, AttributeError):
                continue
            if ts.strftime("%Y-%m-%d") != day:
                continue
            cost = _line_cost(line, prices)
            if cost is not None:
                total += cost
    return total


# ---------------------------------------------------------------------------
# Proxy log (CR-1 / 04 D7: one JSON object per request, ts = arrival, UTC)

def parse_proxy_log(path, audio_start, audio_end):
    """Sum status=200 speech lines with ts in [audio_start, audio_end).
    Returns {"audio_seconds", "wall_s", "chars"} or None."""
    if path is None or not Path(path).is_file():
        return None
    audio_s = 0.0
    wall_ms = 0.0
    chars = 0
    found = False
    try:
        text = Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError as e:
        die(f"cannot read proxy log {path}: {e}", 2)
    for raw in text.splitlines():
        if not raw.strip():
            continue
        try:
            line = json.loads(raw)
        except json.JSONDecodeError:
            continue
        if line.get("status") != 200:
            continue
        try:
            ts = _parse_iso_z(str(line.get("ts")))
        except (ValueError, AttributeError):
            continue
        if audio_start <= ts < audio_end:
            found = True
            audio_s += _num(line.get("audio_seconds"))
            wall_ms += _num(line.get("vieneu_ms")) \
                + _num(line.get("ffmpeg_ms"))
            chars += int(_num(line.get("chars")))
    if not found:
        return None
    return {"audio_seconds": audio_s, "wall_s": wall_ms / 1000.0,
            "chars": chars}


# ---------------------------------------------------------------------------
# RSS sampler raw -> per-stage peaks (cols 24-26, 52-54; rss.csv stage rows)

def _read_rss_raw(path):
    rows = []
    p = Path(path)
    if not p.is_file():
        return rows
    for i, raw in enumerate(p.read_text(encoding="utf-8").splitlines()):
        if i == 0 or not raw.strip():
            continue
        parts = raw.split(",")
        if len(parts) != len(RSS_RAW_FIELDS):
            continue
        row = dict(zip(RSS_RAW_FIELDS, parts))
        try:
            row["dt"] = _parse_iso_z(row["ts_utc"])
        except (ValueError, AttributeError):
            continue
        rows.append(row)
    return rows


def _kb(row, field):
    try:
        return float(row.get(field, "0"))
    except ValueError:
        return 0.0


def _window_stats(rows, start, end):
    """Aggregate sampler rows with ts in [start, end); None bound = all."""
    sel = [r for r in rows
           if (start is None or r["dt"] >= start)
           and (end is None or r["dt"] < end)]
    if not sel:
        return None
    v = max(_kb(r, "vieneu_rss_kb") for r in sel)
    p = max(_kb(r, "proxy_rss_kb") for r in sel)
    m = max(_kb(r, "mpt_rss_kb") for r in sel)
    w = max(_kb(r, "webui_rss_kb") for r in sel)
    total = max(_kb(r, "vieneu_rss_kb") + _kb(r, "proxy_rss_kb")
                + _kb(r, "mpt_rss_kb") + _kb(r, "webui_rss_kb") for r in sel)
    swap_vals = []
    for r in sel:
        s = r["swap_used_mb"]
        if s and s != "NA":
            try:
                swap_vals.append(float(s))
            except ValueError:
                pass
    lvls = [r["pressure_level"] for r in sel
            if r["pressure_level"] in PRESSURE_NAMES]
    frees = [int(r["free_pct"]) for r in sel if r["free_pct"].isdigit()]
    nprocs = [int(r["mpt_nproc"]) for r in sel if r["mpt_nproc"].isdigit()]
    ms = sorted(int(r["sample_ms"]) for r in sel
                if r["sample_ms"].isdigit())
    p95 = ms[max(0, (95 * len(ms) + 99) // 100 - 1)] if ms else 0
    return {
        "samples": len(sel),
        "vieneu_peak_mb": v / 1024, "proxy_peak_mb": p / 1024,
        "mpt_peak_mb": m / 1024, "webui_peak_mb": w / 1024,
        "total_peak_mb": total / 1024,
        "swap_start_mb": swap_vals[0] if swap_vals else None,
        "swap_peak_mb": max(swap_vals) if swap_vals else 0.0,
        "pressure_max": PRESSURE_NAMES[max(lvls, key=int)] if lvls else None,
        "free_pct_min": min(frees) if frees else None,
        "mpt_nproc_max": max(nprocs) if nprocs else 0,
        "sample_ms_p95": p95,
    }


def _rss_row(scope, start, end, st):
    """One rss.csv row (15 columns); a window without samples keeps the
    auxiliary file's bare ``NA`` convention (schema.md, D-10)."""
    if st is None:
        return [scope, start, end, "0"] + ["NA"] * 11
    return [
        scope, start, end, str(st["samples"]),
        f"{st['vieneu_peak_mb']:.1f}", f"{st['proxy_peak_mb']:.1f}",
        f"{st['mpt_peak_mb']:.1f}", f"{st['webui_peak_mb']:.1f}",
        f"{st['total_peak_mb']:.1f}",
        ("NA" if st["swap_start_mb"] is None
         else f"{st['swap_start_mb']:.1f}"),
        f"{st['swap_peak_mb']:.1f}",
        st["pressure_max"] or "NA",
        ("NA" if st["free_pct_min"] is None else str(st["free_pct_min"])),
        str(st["mpt_nproc_max"]), str(st["sample_ms_p95"]),
    ]


def stage_peaks(raw_rows, windows, coverage):
    """windows: stages['windows'], coverage: stages['coverage'] (ISO Z).

    Returns (cols, csv_rows) — cols 24-26, 52-54 plus the rss.csv rows: the
    whole-run ``all`` row first, then one row per stage window.

    Cols 52-54 and the ``all`` row are bounded by the task window
    (``coverage.first_marker`` -> ``coverage.end``), not the sampler's
    lifetime (perf review P3): the sampler outlives the task, and a heavier
    post-run tail (a second task in the shared MPT log, a peer bench, agent
    work) must not be attributed to this run.
    """
    cols = {}
    csv_rows = []

    def bounds(key):
        w = windows.get(key)
        if not w:
            return None
        return _parse_iso_z(w["start"]), _parse_iso_z(w["end"])

    def vals_in(keys, field):
        vals = []
        for k in keys:
            b = bounds(k)
            if not b:
                continue
            vals.extend(_kb(r, field) for r in raw_rows
                        if b[0] <= r["dt"] < b[1])
        return vals

    audio_vals = vals_in(["audio"], "vieneu_rss_kb")
    cols["ram_peak_vieneu_mb"] = (f"{max(audio_vals) / 1024:.1f}"
                                if audio_vals
                                else _na("no-samples-in-window"))
    whisper_vals = vals_in(["subtitle"], "mpt_rss_kb")
    cols["ram_peak_whisper_mb"] = (f"{max(whisper_vals) / 1024:.1f}"
                                 if whisper_vals
                                 else _na("no-samples-in-window"))
    render_vals = vals_in(["combine", "final"], "mpt_rss_kb")
    cols["ram_peak_render_mb"] = (f"{max(render_vals) / 1024:.1f}"
                                if render_vals
                                else _na("no-samples-in-window"))

    whole = _window_stats(raw_rows, _parse_iso_z(coverage["first_marker"]),
                          _parse_iso_z(coverage["end"]))
    csv_rows.append(_rss_row("all", coverage["first_marker"],
                             coverage["end"], whole))
    if whole is None:
        cols["ram_peak_total_mb"] = _na("no-samples-in-window")
        cols["swap_peak_mb"] = _na("no-samples-in-window")
        cols["memory_pressure_max"] = _na("pressure-unavailable")
    else:
        cols["ram_peak_total_mb"] = f"{whole['total_peak_mb']:.1f}"
        cols["swap_peak_mb"] = f"{whole['swap_peak_mb']:.1f}"
        cols["memory_pressure_max"] = (whole["pressure_max"]
                                       or _na("pressure-unavailable"))

    for key in STAGE_ORDER:
        b = bounds(key)
        if b is None:
            continue
        csv_rows.append(_rss_row(f"stage:{key}", _utc_z(b[0]), _utc_z(b[1]),
                                 _window_stats(raw_rows, b[0], b[1])))
    return cols, csv_rows


# ---------------------------------------------------------------------------
# Script word count (schema.md word-count rule)

def count_words(text):
    """NFC; P*/S* chars -> space except ' / ’ between two letters; split on
    whitespace; count non-empty tokens (digit groups count)."""
    text = unicodedata.normalize("NFC", text)
    chars = list(text)
    n = len(chars)
    out = []
    for i, ch in enumerate(chars):
        cat = unicodedata.category(ch)
        if ch in ("'", "’") and 0 < i < n - 1 \
                and chars[i - 1].isalpha() and chars[i + 1].isalpha():
            out.append(ch)
        elif cat[0] in ("P", "S"):
            out.append(" ")
        else:
            out.append(ch)
    return sum(1 for tok in "".join(out).split() if tok)


# ---------------------------------------------------------------------------
# Row assembly and validation

def _fmt_sec(x):
    return f"{x:.1f}"


def build_row(run, stages, tap_cols, issues, tts, ram_cols, probe,
              script_text, run_status, failed_stage, terms_count_override):
    cols = {}
    cols["run_id"] = run["run_id"]
    cols["date"] = run["started_at"]
    pins = run.get("pins", {})
    heads = run.get("heads", {})
    if heads.get("mpt") and pins.get("MPT_COMMIT") \
            and heads["mpt"] != pins["MPT_COMMIT"]:
        die("heads.mpt does not equal pins.MPT_COMMIT")
    if heads.get("vieneu") and pins.get("VIENEU_COMMIT") \
            and heads["vieneu"] != pins["VIENEU_COMMIT"]:
        die("heads.vieneu does not equal pins.VIENEU_COMMIT")
    cols["mpt_commit"] = pins.get("MPT_COMMIT", "")
    cols["vieneu_commit"] = pins.get("VIENEU_COMMIT", "")
    cols["language"] = run["lang"]
    cols["topic"] = re.sub(r"[\r\n]+", " ", run.get("topic", ""))
    cols["target_minutes"] = f"{float(run.get('target_minutes', 0)):g}"
    cols.update(tap_cols)

    def win_na(key):
        if failed_stage and key not in stages["windows"]:
            return _na(f"run-failed-at-{failed_stage}")
        if key == "script" and run.get("story_source") == "plan02":
            return _na("story-source-plan02")
        return _na("marker-missing")

    audio_w = stages["windows"].get("audio")

    # TTS: proxy log -> mpt audio ffprobe -> n/a (cols 19/20/58). The
    # run-failed code applies only when the audio window never opened.
    if tts is None:
        if audio_w is None and failed_stage:
            na = _na(f"run-failed-at-{failed_stage}")
            cols["tts_seconds_audio"] = na
            cols["tts_wall_seconds"] = na
            cols["tts_chars"] = na
        else:
            cols["tts_seconds_audio"] = _na("no-proxy-log")
            cols["tts_wall_seconds"] = (_fmt_sec(audio_w["seconds"])
                                        if audio_w else win_na("audio"))
            cols["tts_chars"] = (
                str(len(unicodedata.normalize("NFC", script_text)))
                if script_text is not None else _na("no-proxy-log"))
    else:
        cols["tts_seconds_audio"] = _fmt_sec(tts["audio_seconds"])
        cols["tts_wall_seconds"] = _fmt_sec(tts["wall_s"])
        cols["tts_chars"] = (
            str(tts["chars"]) if tts.get("chars") is not None
            else (str(len(unicodedata.normalize("NFC", script_text)))
                  if script_text is not None else _na("no-proxy-log")))

    cols["whisper_wall_seconds"] = (
        _fmt_sec(stages["windows"]["subtitle"]["seconds"])
        if "subtitle" in stages["windows"] else win_na("subtitle"))
    if "combine" in stages["windows"]:
        render_s = sum(stages["windows"][k]["seconds"]
                       for k in ("combine", "final")
                       if k in stages["windows"])
        cols["render_wall_seconds"] = _fmt_sec(render_s)
    else:
        cols["render_wall_seconds"] = win_na("combine")
    cols["total_wall_seconds"] = _fmt_sec(stages["total_seconds"])

    cols.update(ram_cols)

    # ffprobe of the final video (cols 27-28).
    if probe is None or not isinstance(probe.get("format"), dict):
        na = (_na(f"run-failed-at-{failed_stage}") if failed_stage
              else _na("no-output-file"))
        cols["output_duration_sec"] = na
        cols["output_size_mb"] = na
    else:
        try:
            cols["output_duration_sec"] = \
                f"{float(probe['format']['duration']):.3f}"
            cols["output_size_mb"] = \
                f"{int(probe['format']['size']) / 1048576:.2f}"
        except (KeyError, ValueError, TypeError):
            na = (_na(f"run-failed-at-{failed_stage}") if failed_stage
                  else _na("ffprobe-failed"))
            cols["output_duration_sec"] = na
            cols["output_size_mb"] = na

    # Script columns (29-30).
    if script_text is not None:
        cols["words_in_story"] = str(count_words(script_text))
    else:
        cols["words_in_story"] = (
            _na(f"run-failed-at-{failed_stage}") if failed_stage
            else _na("no-script"))
    if _is_na(cols["tts_seconds_audio"]) \
            or _is_na(cols["words_in_story"]):
        cols["words_per_minute_measured"] = _na("component-na")
    elif float(cols["tts_seconds_audio"]) == 0:
        cols["words_per_minute_measured"] = _na("zero-duration")
    else:
        cols["words_per_minute_measured"] = (
            f"{int(cols['words_in_story']) / (float(cols['tts_seconds_audio']) / 60):.1f}")

    cols["schema_version"] = SCHEMA_VERSION
    cols["run_status"] = run_status
    env = run.get("env") or {}
    cols["machine"] = "{}/{}/{}GB/macOS {}".format(
        env.get("hw_model", "?"), env.get("cpu_brand", "?"),
        round(_num(env.get("memory_bytes")) / (1 << 30)),
        env.get("macos_version", "?"))
    cols["video_aspect"] = run["aspect"]
    cols["voice_name"] = run.get("voice_name") or _na("not-given")
    cols["story_source"] = run.get("story_source", "mpt")
    if terms_count_override is not None:
        cols["terms_count"] = str(terms_count_override)
    elif stages["counts"]["terms_count"] is not None:
        cols["terms_count"] = str(stages["counts"]["terms_count"])
    else:
        cols["terms_count"] = win_na("terms")

    # Stage walls (cols 49-50, schema.md): the defining markers only; no
    # partial-fallback windows.
    script_w = stages["windows"].get("script")
    if script_w and audio_w:
        s0 = _parse_iso_z(script_w["start"])
        a0 = _parse_iso_z(audio_w["start"])
        cols["story_wall_seconds"] = _fmt_sec((a0 - s0).total_seconds())
    elif script_w is None and run.get("story_source") == "plan02":
        cols["story_wall_seconds"] = _na("story-source-plan02")
    elif script_w is None:
        cols["story_wall_seconds"] = win_na("script")
    else:
        cols["story_wall_seconds"] = win_na("audio")
    mat = stages["windows"].get("materials")
    comb = stages["windows"].get("combine")
    if mat and comb:
        m0 = _parse_iso_z(mat["start"])
        c0 = _parse_iso_z(comb["start"])
        cols["image_wall_seconds"] = _fmt_sec((c0 - m0).total_seconds())
    elif mat is None:
        cols["image_wall_seconds"] = win_na("materials")
    else:
        cols["image_wall_seconds"] = win_na("combine")

    w = stages["whisper"]
    if w.get("language") and w.get("probability") is not None:
        cols["subtitle_detected_language"] = \
            f"{w['language']}@{w['probability']:.2f}"
    else:
        cols["subtitle_detected_language"] = win_na("subtitle")

    # image_failed_count is an mpt-log column; under --no-tap the spec puts
    # cols 39-48 (which include it) on n/a:no-tap (T-P10) and tap_cols already
    # holds that value, so only fill it when the tap side did not.
    if "image_failed_count" not in cols:
        cols["image_failed_count"] = str(stages["counts"]["image_failed"])

    # col 15 cross-check vs mpt-log rendered lines.
    if cols["image_count"].isdigit() \
            and int(cols["image_count"]) \
            != stages["counts"]["image_rendered"]:
        issues.add(f"image-count-mismatch:{cols['image_count']}/"
                   f"{stages['counts']['image_rendered']}")

    # Derived cost columns (55, 56).
    dur = cols["output_duration_sec"]
    img_cost = cols["image_cost_usd"]
    story_cost = cols["story_cost_usd"]
    if _is_na(dur):
        cols["usd_per_output_minute"] = _na("component-na")
        cols["local_compute_seconds_per_output_minute"] = _na("component-na")
    elif float(dur) == 0:
        cols["usd_per_output_minute"] = _na("zero-duration")
        cols["local_compute_seconds_per_output_minute"] = _na("zero-duration")
    else:
        if _is_na(img_cost) or (_is_na(story_cost)
                                and story_cost != _na("story-source-plan02")):
            cols["usd_per_output_minute"] = _na("component-na")
        else:
            sc = 0.0 if story_cost == _na("story-source-plan02") \
                else float(story_cost)
            cols["usd_per_output_minute"] = \
                f"{(sc + float(img_cost)) / (float(dur) / 60):.6f}"
        walls = [cols["tts_wall_seconds"], cols["whisper_wall_seconds"],
                 cols["render_wall_seconds"]]
        if any(_is_na(x) for x in walls):
            cols["local_compute_seconds_per_output_minute"] = \
                _na("component-na")
        else:
            total = sum(float(x) for x in walls)
            cols["local_compute_seconds_per_output_minute"] = \
                f"{total / (float(dur) / 60):.1f}"

    cols["dashboard_cost_usd_check"] = PENDING_DASHBOARD

    if stages["coverage"]["gaps_seconds"] != 0:
        issues.add(f"stage-gap:{stages['coverage']['gaps_seconds']:g}")
    if not _is_na(cols.get("swap_peak_mb", "")) \
            and cols.get("swap_peak_mb") \
            and float(cols["swap_peak_mb"]) > 0:
        issues.add("swap-used")
    if cols.get("memory_pressure_max") in ("warn", "critical"):
        issues.add("pressure-warn")

    return cols, issues


def build_issues(issues, extra_issues):
    items = set(issues)
    for text in extra_issues or []:
        clean = re.sub(r"[\r\n]+", " ", text).replace(";", ",")
        # An --issue that already carries the collector's note: prefix is
        # not prefixed twice (L-5).
        items.add(clean if clean.startswith("note:") else f"note:{clean}")
    if not items:
        return "none"
    return ";".join(sorted(items))


def _issue_ok(code):
    if code in ISSUE_CODES or code == "none":
        return True
    for p in ISSUE_PREFIXES:
        if not code.startswith(p):
            continue
        rest = code[len(p):]
        if p == "image-count-mismatch:":
            return re.fullmatch(r"\d+/\d+", rest) is not None
        if p == "stage-gap:":
            return re.fullmatch(r"[0-9.]+", rest) is not None
        if p == "dashboard-delta-":
            return re.fullmatch(r"\d+pct", rest) is not None
        return True  # note:<free text>
    return False


def validate_row(row, header):
    """Return list of offending column names; empty = ok."""
    bad = []
    if len(row) != len(COLUMNS):
        return [f"__len__({len(row)} != {len(COLUMNS)})"]
    if list(header) != COLUMNS:
        bad.append("__header__")
    for name, cell in zip(COLUMNS, row):
        if cell == "" or cell is None:
            bad.append(name)
            continue
        if cell.startswith("n/a:"):
            # n/a:<reason> is the only missing-value form. A STRING_COLS
            # free-text value may legitimately start with 'n/a' without
            # the colon (e.g. a topic "n/a placeholder") — it falls
            # through to the string/enum checks below.
            if not NA_RE.match(cell) or name in NEVER_NA_COLS:
                bad.append(name)
            continue
        if name == "issues":
            if not all(_issue_ok(c) for c in cell.split(";")):
                bad.append(name)
            continue
        if name in ENUM_COLS:
            if cell not in ENUM_COLS[name]:
                bad.append(name)
            continue
        if name in STRING_COLS:
            continue
        try:
            if float(cell) < 0:
                bad.append(name)
        except ValueError:
            bad.append(name)
    return bad


# ---------------------------------------------------------------------------
# IO helpers

def _atomic_write(path, text):
    path = Path(path)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


def _atomic_write_json(path, obj):
    _atomic_write(path, json.dumps(obj, ensure_ascii=False, indent=2) + "\n")


def _copy_clone(src, dst):
    try:
        r = subprocess.run(["cp", "-c", str(src), str(dst)],
                           capture_output=True)
        if r.returncode != 0:
            shutil.copyfile(src, dst)
    except OSError:
        shutil.copyfile(src, dst)


def _store_path(path, nts_home, trial_dir):
    """Path string for a tracked file (I4): $NTS_HOME-relative under
    NTS_HOME, repo-relative under the repo, else basename only — never an
    absolute path."""
    ap = os.path.abspath(str(path))
    nh = os.path.abspath(str(nts_home))
    if ap == nh or ap.startswith(nh + os.sep):
        return "$NTS_HOME/" + os.path.relpath(ap, nh)
    repo = os.path.dirname(os.path.abspath(str(trial_dir)))
    if ap == repo or ap.startswith(repo + os.sep):
        return os.path.relpath(ap, repo)
    return os.path.basename(ap)


def _read_csv_header(path):
    with open(path, newline="", encoding="utf-8") as f:
        first = f.readline()
    return next(csv.reader([first.rstrip("\r\n")]))


def _append_csv_row(path, row, run_id):
    """Append one row under LOCK_EX; the I2 run_id duplicate scan runs
    inside the same lock. If the path was replaced while we waited
    (--dashboard-cost os.replace), reopen so the row lands on the live
    inode."""
    for _ in range(2):
        with open(path, "a+", newline="", encoding="utf-8") as f:
            fcntl.flock(f.fileno(), fcntl.LOCK_EX)
            try:
                try:
                    current_ino = Path(path).stat().st_ino
                except OSError:
                    current_ino = None
                if current_ino is not None \
                        and current_ino != os.fstat(f.fileno()).st_ino:
                    continue  # stale inode; reopen under a fresh lock
                f.seek(0)
                if any(r and r[0] == run_id for r in csv.reader(f)):
                    die(f"run {run_id} already in sheet (I2)")
                csv.writer(f, lineterminator="\r\n").writerow(row)
                return
            finally:
                fcntl.flock(f.fileno(), fcntl.LOCK_UN)
    die(f"measurements.csv inode kept changing; row for {run_id} "
        "not written (retry the finish)")


def _collector_revision():
    """sha256 of the collector sources at finish time (L-4), so a recorded
    row is attributable to a tool revision from run.json alone. ``null`` when
    a source cannot be read."""
    out = {}
    for name in ("finish_run.py", "run_manifest.py"):
        try:
            digest = hashlib.sha256(
                Path(__file__).resolve().with_name(name).read_bytes()
            ).hexdigest()
        except OSError:
            digest = None
        out[name[:-3] + "_sha256"] = digest
    return out


def _stop_sampler(nts_home, run_id):
    pidfile = Path(nts_home) / "runs" / run_id / "rss_sampler.pid"
    try:
        pid = int(pidfile.read_text().strip())
    except (OSError, ValueError):
        return
    # Stale/reused pid guard: only signal when the pid is really this
    # run's sampler (pidfile survives a SIGKILL; macOS reuses pids).
    try:
        out = subprocess.run(["ps", "-p", str(pid), "-o", "command="],
                             capture_output=True, text=True).stdout
    except OSError:
        out = ""
    if "rss_sampler.sh" not in out or run_id not in out:
        print(f"warning: pid {pid} is not rss_sampler.sh for {run_id}; "
              "leaving it alone", file=sys.stderr)
        return
    try:
        os.kill(pid, signal.SIGTERM)
    except OSError:
        return
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        try:
            os.kill(pid, 0)
        except OSError:
            return
        time.sleep(0.1)


def _load_run_json(trial_dir, run_id):
    path = Path(trial_dir) / "runs" / run_id / "run.json"
    if not path.is_file():
        die(f"run.json missing: {path}", 2)
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        die(f"run.json unparseable: {e}", 2)


def _current_run(nts_home):
    try:
        return (Path(nts_home) / "current_run").read_text().strip()
    except OSError:
        return None


def _run_id_in_csv(csv_path, run_id):
    p = Path(csv_path)
    if not p.is_file():
        return False
    with open(p, newline="", encoding="utf-8") as f:
        for row in csv.reader(f):
            if row and row[0] == run_id:
                return True
    return False


def _find_mpt_log(nts_home, started_at):
    logs = sorted(Path(nts_home).glob("logs/mpt-api-*.log"),
                  key=lambda p: p.stat().st_mtime, reverse=True)
    for p in logs:
        if datetime.fromtimestamp(p.stat().st_mtime, timezone.utc) \
                >= started_at:
            return p
    return logs[0] if logs else None


def _find_proxy_log(nts_home):
    logs = sorted(Path(nts_home).glob("logs/proxy-*.log"),
                  key=lambda p: p.stat().st_mtime, reverse=True)
    return logs[0] if logs else None


def _default_ffprobe(path):
    r = subprocess.run(
        ["ffprobe", "-v", "error", "-print_format", "json",
         "-show_format", "-show_streams", str(path)],
        capture_output=True, text=True)
    if r.returncode != 0:
        return None
    try:
        return json.loads(r.stdout)
    except json.JSONDecodeError:
        return None


def _read_script(script_arg, task_dir):
    """--script P: a .json path reads its `script` key (Q2); any other suffix
    is raw script text. Default: <task_dir>/script.json key `script`."""
    p = Path(script_arg) if script_arg else Path(task_dir) / "script.json"
    if not p.is_file():
        if script_arg:
            die(f"script file not found: {p}", 2)
        return None
    if p.suffix == ".json":
        try:
            return json.loads(p.read_text(encoding="utf-8")).get("script")
        except (OSError, json.JSONDecodeError) as e:
            die(f"cannot read script file {p}: {e}", 2)
    try:
        return p.read_text(encoding="utf-8")
    except OSError as e:
        die(f"cannot read script file {p}: {e}", 2)


# ---------------------------------------------------------------------------
# Modes

def do_abort(args, nts_home, trial_dir):
    run_id = args.run_id
    if not RUN_ID_RE.match(run_id):
        die(f"bad run_id '{run_id}'", 2)
    cur = _current_run(nts_home)
    if cur is not None and cur != run_id:
        die(f"current_run is {cur}, not {run_id}", 2)
    _stop_sampler(nts_home, run_id)
    run = _load_run_json(trial_dir, run_id)
    now = _utc_z(datetime.now(timezone.utc))
    run["run_status"] = "failed"
    run["finished_at"] = now
    _atomic_write_json(Path(trial_dir) / "runs" / run_id / "run.json", run)
    notes = Path(trial_dir) / "runs" / run_id / "notes.md"
    with open(notes, "a", encoding="utf-8") as f:
        f.write(f"aborted {now}\n")
    try:
        os.remove(Path(nts_home) / "current_run")
    except OSError:
        pass
    print(f"run {run_id} aborted (no CSV row)")
    return 0


def do_dashboard(args, nts_home, trial_dir, prices_path):
    run_id = args.run_id
    if not RUN_ID_RE.match(run_id):
        die(f"bad run_id '{run_id}'", 2)
    day = args.dashboard_day
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", day or ""):
        die("--dashboard-day must be YYYY-MM-DD", 2)
    csv_path = Path(trial_dir) / "measurements.csv"
    if not csv_path.is_file():
        die(f"measurements.csv missing: {csv_path}", 2)
    col_i = COLUMNS.index("dashboard_cost_usd_check")
    iss_i = COLUMNS.index("issues")
    try:
        dashboard = float(args.dashboard_cost)
    except ValueError:
        die("--dashboard-cost must be a number", 2)
    if not math.isfinite(dashboard):
        die("--dashboard-cost must be a finite number", 2)  # SEC-0928-2
    tap_total = tap_day_cost(Path(trial_dir) / "runs", prices_path, day)
    # LOCK_EX across read + tmp + os.replace so a concurrent finish
    # append is never lost. The appender rechecks the inode under its
    # own lock, so the replace cannot strand a row on a dead inode.
    with open(csv_path, "r+", newline="", encoding="utf-8") as f:
        fcntl.flock(f.fileno(), fcntl.LOCK_EX)
        try:
            f.seek(0)
            rows = list(csv.reader(f))
            idx = None
            for i, row in enumerate(rows):
                if row and row[0] == run_id:
                    idx = i
                    break
            if idx is None:
                die(f"run_id {run_id} not in measurements.csv")
            if rows[idx][col_i] != PENDING_DASHBOARD:
                die(f"dashboard_cost_usd_check for {run_id} is "
                    f"'{rows[idx][col_i]}', not {PENDING_DASHBOARD}")
            rows[idx][col_i] = f"{dashboard:.6f}"
            delta_pct = (abs(dashboard - tap_total) / tap_total * 100
                         if tap_total > 0 else float("inf"))
            z = int(round(delta_pct)) if delta_pct != float("inf") else 999
            exit_code = 0
            if delta_pct > 5:
                codes = set() if rows[idx][iss_i] == "none" \
                    else set(rows[idx][iss_i].split(";"))
                codes.add(f"dashboard-delta-{z}pct")
                rows[idx][iss_i] = ";".join(sorted(codes))
                exit_code = 1
            tmp = csv_path.with_name("measurements.csv.tmp")
            with open(tmp, "w", newline="", encoding="utf-8") as tf:
                csv.writer(tf, lineterminator="\r\n").writerows(rows)
            os.replace(tmp, csv_path)
        finally:
            fcntl.flock(f.fileno(), fcntl.LOCK_UN)
    print(f"tap={tap_total:.6f} dashboard={dashboard:.6f} "
          f"delta={delta_pct:.1f}%")
    return exit_code


def do_finish(args, nts_home, trial_dir, prices_path, probe_fn):
    run_id = args.run_id
    if not RUN_ID_RE.match(run_id):
        die(f"bad run_id '{run_id}'", 2)
    if not TASK_ID_RE.fullmatch(args.task_id or ""):
        die(f"bad --task-id {args.task_id!r} (want an MPT task UUID, "
            "8-4-4-4-12 hex)", 2)
    csv_path = Path(trial_dir) / "measurements.csv"
    if not csv_path.is_file():
        die(f"measurements.csv missing: {csv_path}", 2)
    header = _read_csv_header(csv_path)
    if list(header) != COLUMNS:
        die("measurements.csv header drifted from COLUMNS (I1)")
    if _run_id_in_csv(csv_path, run_id):
        die(f"run {run_id} already in sheet (I2)")
    cur = _current_run(nts_home)
    if cur != run_id:
        die(f"current_run is {cur!r}, not {run_id}", 2)

    run = _load_run_json(trial_dir, run_id)
    started_at = _parse_iso_z(run["started_at"])
    task_id = args.task_id
    run_dir_nts = Path(nts_home) / "runs" / run_id
    run_dir_trial = Path(trial_dir) / "runs" / run_id
    task_dir = Path(nts_home) / "src" / "mpt" / "storage" / "tasks" / task_id
    failed_flag = args.status == "failed"

    mpt_log = args.mpt_log or _find_mpt_log(nts_home, started_at)
    if mpt_log is None:
        die("no mpt-api log found ($NTS_HOME/logs/mpt-api-*.log)")
    stages = parse_mpt_log(mpt_log, task_id, started_at,
                           allow_unfinished=failed_flag)

    failed_stage = stages.get("failed_stage")
    run_status = "failed" if (failed_flag or failed_stage) \
        else run.get("run_status", "trial")

    tap_disabled = bool((run.get("tap") or {}).get("disabled")) or args.no_tap
    jsonl = run_dir_trial / "openai_usage.jsonl"
    tap_cols, tap_issues = aggregate_tap(
        jsonl, prices_path, run.get("story_source", "mpt"), tap_disabled)
    issues = set(tap_issues)

    # Proxy log -> task audio ffprobe fallback for TTS cols 19/20/58.
    audio_w = stages["windows"].get("audio")
    proxy_path = args.proxy_log or _find_proxy_log(nts_home)
    if proxy_path is not None and not Path(proxy_path).is_file():
        die(f"proxy log not found: {proxy_path}", 2)
    tts = None
    tts_source = "n/a"
    if audio_w:
        a0 = _parse_iso_z(audio_w["start"])
        a1 = _parse_iso_z(audio_w["end"])
        tts = parse_proxy_log(proxy_path, a0, a1)
        if tts is not None:
            tts_source = "proxy-log"
        else:
            audio_mp3 = task_dir / "audio.mp3"
            if audio_mp3.is_file():
                probe = (probe_fn or _default_ffprobe)(audio_mp3)
                if probe and probe.get("format", {}).get("duration"):
                    tts = {"audio_seconds":
                           float(probe["format"]["duration"]),
                           "wall_s": audio_w["seconds"], "chars": None}
                    tts_source = "mpt-audio-ffprobe"

    script_text = _read_script(args.script, task_dir)

    raw_rows = _read_rss_raw(run_dir_nts / "rss_raw.csv")
    ram_cols, rss_rows = stage_peaks(raw_rows, stages["windows"],
                                     stages["coverage"])

    output_path = Path(args.output) if args.output \
        else task_dir / "final-1.mp4"
    probe = None
    if output_path.is_file():
        probe = (probe_fn or _default_ffprobe)(output_path)

    cols, issues = build_row(
        run, stages, tap_cols, issues, tts, ram_cols, probe,
        script_text, run_status, failed_stage, args.terms_count)
    sources = {"tts_seconds_audio": tts_source,
               "mpt_log": _store_path(mpt_log, nts_home, trial_dir)}

    cols["issues"] = build_issues(issues, args.issue)
    row = [cols[name] for name in COLUMNS]
    bad = validate_row(row, header)
    if bad:
        die("row validation failed: " + ", ".join(bad))

    if args.dry_run:
        for name, val in zip(COLUMNS, row):
            print(f"{name}={val}")
        return 0

    # ---- writes (spec step 12). The sampler is stopped only here: after
    # validation, not on --dry-run, so a peek or a rejected row never
    # kills sampling or writes the premature 'all' rss.csv row. ----
    _stop_sampler(nts_home, run_id)
    _atomic_write_json(run_dir_trial / "stages.json", stages)
    if probe is not None:
        p = dict(probe)
        if isinstance(p.get("format"), dict):
            fmt = dict(p["format"])
            if "filename" in fmt:
                fmt["filename"] = os.path.basename(str(fmt["filename"]))
            p["format"] = fmt
        _atomic_write_json(run_dir_trial / "ffprobe.json", p)
    # Full atomic rewrite (D-3): header + the windowed `all` row + one row
    # per stage window. The sampler appends its own lifetime `all` row on
    # TERM, which _stop_sampler has already waited for; replacing it keeps
    # rss.csv consistent with cols 52-54 (perf P3) and makes a retried
    # finish idempotent instead of appending duplicate stage rows.
    rss_csv = run_dir_trial / "rss.csv"
    buf = io.StringIO()
    buf.write(RSS_CSV_HEADER + "\n")
    csv.writer(buf, lineterminator="\n").writerows(rss_rows)
    _atomic_write(rss_csv, buf.getvalue())

    if script_text is not None:
        run_dir_nts.mkdir(parents=True, exist_ok=True)
        dst = run_dir_nts / "script.txt"
        dst.write_text(script_text, encoding="utf-8")
        try:
            os.chmod(dst, 0o600)
        except OSError:
            pass
    if output_path.is_file():
        run_dir_nts.mkdir(parents=True, exist_ok=True)
        _copy_clone(output_path, run_dir_nts / "final-1.mp4")

    run["finished_at"] = _utc_z(datetime.now(timezone.utc))
    run["mpt_task_id"] = task_id
    run["run_status"] = run_status
    run["sources"] = sources
    run["collector"] = _collector_revision()
    _atomic_write_json(run_dir_trial / "run.json", run)

    _append_csv_row(csv_path, row, run_id)
    try:
        os.remove(Path(nts_home) / "current_run")
    except OSError:
        pass

    print(f"run {run_id} status={run_status}")
    print(f"total_wall_seconds={cols['total_wall_seconds']}")
    print(f"image_count={cols['image_count']} "
          f"image_cost_usd={cols['image_cost_usd']}")
    print(f"story_cost_usd={cols['story_cost_usd']}")
    print(f"usd_per_output_minute={cols['usd_per_output_minute']}")
    print(f"ram_peak_total_mb={cols['ram_peak_total_mb']} "
          f"swap_peak_mb={cols['swap_peak_mb']} "
          f"pressure={cols['memory_pressure_max']}")
    print(f"issues={cols['issues']}")
    return 0


# ---------------------------------------------------------------------------
# CLI

def build_parser():
    p = argparse.ArgumentParser(
        prog="finish_run.py",
        description="Collect one finished MPT task into a "
                    "measurements.csv row.")
    p.add_argument("run_id", help="D10 run id")
    p.add_argument("--task-id", help="MPT task UUID (8-4-4-4-12 hex)")
    p.add_argument("--mpt-log",
                   help="path to mpt-api log (default: newest "
                        "$NTS_HOME/logs/mpt-api-*.log)")
    p.add_argument("--proxy-log",
                   help="path to proxy log (default: newest "
                        "$NTS_HOME/logs/proxy-*.log)")
    p.add_argument("--script",
                   help="script.json or text file (default: "
                        "$NTS_HOME/src/mpt/storage/tasks/<id>/script.json)")
    p.add_argument("--output",
                   help="final video (default: .../tasks/<id>/final-1.mp4)")
    p.add_argument("--terms-count", type=int, default=None,
                   help="terms supplied externally (overrides log parse)")
    p.add_argument("--issue", action="append", default=[],
                   help="free-text note; recorded as note:<text>")
    p.add_argument("--status", choices=["failed"], default=None,
                   help="mark the run failed (task died without end record)")
    p.add_argument("--abort", action="store_true",
                   help="abort an active run; writes no CSV row")
    p.add_argument("--dashboard-cost", type=float, default=None,
                   help="next-day dashboard USD total for the run's UTC day")
    p.add_argument("--dashboard-day", default=None,
                   help="YYYY-MM-DD (UTC) for --dashboard-cost")
    p.add_argument("--dry-run", action="store_true",
                   help="print the row as name=value lines; write nothing")
    p.add_argument("--no-tap", action="store_true",
                   help="run was started with --no-tap; tap cols n/a:no-tap")
    return p


def main(argv=None, *, trial_dir=None, nts_home=None, prices_path=None,
         probe_fn=None):
    args = build_parser().parse_args(argv)
    repo_root = Path(__file__).resolve().parents[2]
    nts_home = Path(nts_home or os.environ.get("NTS_HOME")
                    or repo_root / ".nts")
    trial_dir = Path(trial_dir or repo_root / "trial")
    prices_path = Path(prices_path or trial_dir / "prices.json")

    try:
        if args.abort:
            return do_abort(args, nts_home, trial_dir)
        if args.dashboard_cost is not None:
            return do_dashboard(args, nts_home, trial_dir, prices_path)
        if not args.task_id:
            print("error: --task-id is required (or --abort / "
                  "--dashboard-cost)", file=sys.stderr)
            return 2
        return do_finish(args, nts_home, trial_dir, prices_path, probe_fn)
    except CollectorError as e:
        print(f"error: {e}", file=sys.stderr)
        return e.code


if __name__ == "__main__":
    sys.exit(main())
