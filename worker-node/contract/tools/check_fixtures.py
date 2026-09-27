#!/usr/bin/env python3
"""check_fixtures.py -- D14 golden-fixture checker for the render-node.v1 contract.

Owner: 05.4.1-T2 (plan 05). Sources: plans/features/05-video-assembly-subtitles/README.md
D3 (mpt-submit.v1), D4 (render-node.v1), D5 (outcome codes), D9 (render-check.v1),
D10 (render-quality.v1), D14 (fixture list), D15 S5 (sample redaction).

Usage
    check_fixtures.py DIR               validate every fixture in DIR
    check_fixtures.py --coverage DIR    every route / error code / outcome code /
                                        status-health-check field named in
                                        DIR/../render-node.v1.md is carried by a
                                        fixture, or is on the documented exempt list
    check_fixtures.py --self-test       mutate a temp copy of the fixtures (and a
                                        small sample corpus) and prove the checker
                                        fails on it (and passes pristine)
    check_fixtures.py --scan DIR        redaction scan of committed samples (D15 S5):
                                        absolute host paths (quoted, `=`, backtick
                                        and traceback forms), content values that
                                        are not redaction markers, secret-shaped
                                        values, strings > 200 chars outside the
                                        allow-list, secret-ish key names. Symlinks,
                                        non-regular files and files over 8 MiB fail
                                        without being read

Exit codes: 0 ok . 1 check failed (printed as "file: key") . 2 usage/environment.

Stdlib only; run with `uv run --no-project --python 3.12`.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import stat
import sys
import tempfile

PLACEHOLDER = "NTS_FIXTURE_SCRIPT"
MAX_STRING = 200
TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_FIXTURES = os.path.normpath(os.path.join(TOOLS_DIR, os.pardir, "fixtures"))

# D14 placeholders for the two content keys that carry real story text in a
# live job (SEC-0542-10 / T70). DIR mode enforces them, --scan accepts them so
# that scanning the fixtures stays clean, and no fixture may hold anything else.
VIDEO_SUBJECT_RE = re.compile(r"^Fixture story title \([a-z-]+\)$")
VIDEO_TERM_RE = re.compile(r"^<styled scene prompt [a-z]{2} \d+>$")

# --------------------------------------------------------------------------- #
# Top-level key sets -- D14: the key set of a fixture equals its contract exactly.
# Sources: D4 (render-submit.v1, render-status.v1, render-node-health.v1),
# D9 (render-check.v1), D10 (render-quality.v1).
# --------------------------------------------------------------------------- #
KEY_SETS = {
    "render-submit.v1": (
        "schema", "render_job_id", "mpt", "image_plan",
    ),
    "render-status.v1": (
        "schema", "task_id", "render_job_id", "state", "stage", "percent",
        "cancel_requested", "submitted_at", "started_at", "finished_at",
        "outcome", "output", "check", "images", "mpt",
    ),
    "render-node-health.v1": (
        "schema", "status", "reasons", "capacity", "mpt", "tts_proxy",
        "image_relay", "assets", "disk", "adapter", "gpu",
    ),
    "render-check.v1": (
        "schema", "tool_version", "ffmpeg_version", "input", "output",
        "container", "video", "audio", "narration", "loudness", "silence",
        "subtitles", "warnings", "errors",
    ),
    "render-quality.v1": (
        "schema", "pass", "policy_version", "check_tool_version", "duration_sec",
        "tts_duration_sec", "integrated_lufs", "true_peak_dbtp", "width", "height",
        "subtitles_present", "sha256", "normalized", "subtitles", "checks",
        "hints", "images",
    ),
}

# The 24 D14 files, by filename.
FIXTURES = (
    "render-submit.v1.landscape-vi.json",
    "render-submit.v1.portrait-en.json",
    "render-submit.v1.no-music.json",
    "render-status.v1.queued.json",
    "render-status.v1.running-voice.json",
    "render-status.v1.running-images.json",
    "render-status.v1.checking.json",
    "render-status.v1.succeeded.json",
    "render-status.v1.failed-tts.json",
    "render-status.v1.failed-materials.json",
    "render-status.v1.failed-assembly.json",
    "render-status.v1.lost.json",
    "render-status.v1.cancelled.json",
    "render-node-health.v1.ok.json",
    "render-node-health.v1.proxy-down.json",
    "render-node-health.v1.disk-low.json",
    "render-check.v1.ok.json",
    "render-check.v1.loud.json",
    "render-check.v1.silent.json",
    "render-check.v1.no-subtitles.json",
    "render-check.v1.zero-cues.json",
    "render-check.v1.truncated.json",
    "render-quality.v1.pass.json",
    "render-quality.v1.fail-loudness.json",
)

# Tracked fixtures of other plans that live in (or may be copied into) the dir.
IGNORE_FILES = frozenset({"i18n-style-labels.v1.json", "music-tracks.v1.json"})

# QA-0542-3: filename <-> scenario coherence. Enum membership alone lets
# `render-status.v1.queued.json` carry `state = "running"`; the README fixture
# table fixes one (state, stage, percent, outcome code) per status filename,
# `percent` being the stage's progress per the S3 trace. The cancelled fixture
# is additionally the only `cancel_requested = true` record (T64).
STATUS_SCENARIOS = {
    "render-status.v1.queued.json": ("queued", "queued", 0, None),
    "render-status.v1.running-voice.json": ("running", "voice", 20, None),
    "render-status.v1.running-images.json": ("running", "images", 40, None),
    "render-status.v1.checking.json": ("running", "checking", 100, None),
    "render-status.v1.succeeded.json": ("succeeded", "done", 100, None),
    "render-status.v1.failed-tts.json": ("failed", "voice", 20, "tts_failed"),
    "render-status.v1.failed-materials.json": ("failed", "images", 40,
                                               "image_generation_failed"),
    "render-status.v1.failed-assembly.json": ("failed", "assembling", 75,
                                              "render_assembly_failed"),
    "render-status.v1.lost.json": ("failed", "images", 40, "render_lost"),
    "render-status.v1.cancelled.json": ("cancelled", "done", 100, None),
}
CANCELLED_FIXTURES = frozenset({"render-status.v1.cancelled.json"})

# --------------------------------------------------------------------------- #
# Nested key sets named in the contract examples (D3/D4/D9/D10).
# --------------------------------------------------------------------------- #
D3_MPT_KEYS = (  # D3 mpt-submit.v1 minus n_threads (D4: the adapter adds it)
    "video_subject", "video_script", "video_terms", "video_aspect",
    "video_fit_mode", "video_concat_mode", "video_transition_mode",
    "video_clip_duration", "video_clip_speed", "match_materials_to_script",
    "video_count", "video_source", "video_materials", "custom_audio_file",
    "video_language", "voice_name", "voice_volume", "voice_rate",
    "bgm_type", "bgm_file", "bgm_volume", "video_music_prompt",
    "sonilo_bgm_prompt", "subtitle_enabled", "subtitle_display_mode",
    "subtitle_animation", "subtitle_position", "custom_position", "font_size",
    "stroke_color", "stroke_width", "text_fore_color", "text_background_color",
    "rounded_subtitle_background", "font_name", "paragraph_number",
    "video_script_prompt", "custom_system_prompt",
)

ELEMENT_KEY_SETS = {
    "submit.image_plan": ("max_images", "prompt_sha256"),
    "status.mpt": ("state", "progress", "failed_stage", "warnings"),
    "status.outcome": ("code", "detail", "message"),
    "status.output": ("video", "subtitles", "narration_seconds"),
    "status.output.video": ("bytes", "sha256"),
    "status.output.subtitles": ("bytes", "sha256", "cues"),
    "health.capacity": ("max_concurrent_tasks", "active_tasks", "accepting"),
    "health.mpt": ("ok", "commit", "config_profile", "started_at",
                   "whisper_device", "video_codec", "threads_per_task"),
    "health.image_relay": ("ok", "tokens_available", "active_plans"),
    "health.assets": ("ok", "font", "music_revision", "music_missing", "checked_at"),
    "health.disk": ("ok", "task_dir_free_bytes", "used_pct"),
    "health.adapter": ("version", "started_at"),
    "health.gpu": ("ok", "util_pct", "mem_used_mb", "mem_total_mb", "temp_c",
                   "sampled_at"),
    "check.input": ("file", "bytes", "sha256"),
    "check.output": ("file", "bytes", "sha256", "normalized"),
    "check.container": ("format", "duration_s", "faststart"),
    "check.video": ("codec", "pix_fmt", "width", "height", "fps"),
    "check.audio": ("codec", "sample_rate", "channels"),
    "check.narration": ("duration_s",),
    "check.loudness": ("before", "after", "normalization_type"),
    "check.loudness.measure": ("integrated_lufs", "true_peak_dbtp", "lra"),
    "check.silence": ("longest_s", "total_s"),
    "check.subtitles": ("present", "cues", "zero_duration", "overlapping",
                        "non_monotonic", "out_of_range", "coverage_share",
                        "max_cue_chars", "long_cues", "cps_p95", "sync_suspect",
                        "text_matches_script", "text_mismatch_cues"),
    "quality.subtitles": ("cues", "coverage_share", "sync_suspect_share",
                          "long_cues", "text_matches_script"),
    "quality.checks[]": ("id", "value", "threshold", "pass"),
}

# --------------------------------------------------------------------------- #
# Enums (D4 state/stage/reasons, D5 outcome codes).
# `health.status`: "ok" is the D4 example; "error" mirrors 04 D8 for the 503 case
# (D4 does not spell the second member -> [INFERENCE], proposed as a ruling).
# --------------------------------------------------------------------------- #
STATE = ("queued", "running", "succeeded", "failed", "cancelled")
STAGE = ("queued", "voice", "subtitles", "images", "assembling", "checking", "done")
HEALTH_STATUS = ("ok", "error")
OUTCOME_CODES = (
    "tts_failed", "image_moderated", "image_generation_failed",
    "render_assembly_failed", "render_node_error", "render_lost",
    "render_timeout", "render_output_invalid", "render_quality_failed",
    "render_cancelled",
)
REASONS = (
    "mpt_unreachable", "mpt_unauthorized", "tts_proxy_not_ok",
    "image_relay_not_ok", "font_missing", "music_missing", "disk_low",
    "config_mismatch", "codec_unavailable", "gpu_unavailable",
)
ERROR_CODES = (
    "adapter_unauthorized", "adapter_route_not_found", "adapter_body_too_large",
    "adapter_auth_throttled", "adapter_contract_violation",
    "adapter_capacity_full", "adapter_node_unhealthy",
    "adapter_idempotency_conflict", "adapter_task_unknown",
    "adapter_output_not_ready", "adapter_output_absent", "adapter_task_busy",
    "adapter_probe_unavailable",
)

BY_REFERENCE_SCHEMA = {  # block -> required inner schema (D4: by reference)
    "status.images": "images.v1",
    "health.tts_proxy": "proxy-health.v1",
}

# No content may cross this line (F11/S5): MPT paths and URIs never appear in an
# adapter document, and no fixture may carry script/terms/prompt text.
FORBIDDEN_KEYS = frozenset({"script", "terms", "prompt", "prompts", "audio_file",
                            "subtitle_path", "materials"})
HOST_PATH_PREFIXES = ("/Users/", "/Volumes/", "/home/", "/root/", "/var/",
                      "/opt/", "/storage/", "C:\\", "D:\\")
FORBIDDEN_IN_STATUS = ("http://",)

# --------------------------------------------------------------------------- #
# --coverage: routes -> the fixture that exercises them (D14 has no probe fixture)
# and the contract elements no D14 fixture carries.
# --------------------------------------------------------------------------- #
ROUTE_FIXTURES = {
    "POST /v1/renders": "render-submit.v1.landscape-vi.json",
    "GET /v1/renders/{task_id}": "render-status.v1.running-images.json",
    "GET /v1/renders/{task_id}/video": "render-status.v1.succeeded.json",
    "GET /v1/renders/{task_id}/subtitles": "render-status.v1.succeeded.json",
    "POST /v1/renders/{task_id}/cancel": "render-status.v1.cancelled.json",
    "DELETE /v1/renders/{task_id}": "render-status.v1.succeeded.json",
    "GET /v1/health": "render-node-health.v1.ok.json",
}
EXEMPT_ROUTES = {
    "POST /v1/probe-image": "contract-only (12 CR-19, request {}); T67: the probe "
                            "drives one cheapest image through the node's own relay "
                            "and is excluded from capacity accounting, so the D14 list "
                            "has no probe fixture - 11.1.2-T1/11.3.5-T1 exercise it",
}
EXEMPT_OUTCOME_CODES = {
    "image_moderated": "Worker-raised (03 D7); D14 carries failed-materials only",
    "render_node_error": "adapter, failed_stage preflight/script/terms/pipeline; no "
                         "D14 fixture - injected by 11.3.5-T1 contract tests (D5)",
    "render_timeout": "Worker-raised (09.6.3, Render:MaxMinutes) - not an adapter status code",
    "render_output_invalid": "Worker-raised (09.6.3) after the download sha256/size check",
    "render_quality_failed": "Worker-raised (05.5.1-T2, D10)",
    "render_cancelled": "Worker job state (09.6.4); the adapter reports "
                        "state=cancelled, outcome=null (D4)",
}
ERROR_CODE_EXEMPT_REASON = (
    "no error-envelope fixture exists: D14 fixes 24 success/status/health fixtures, "
    "so no fixture carries an `error.code` - the envelope is exercised by 11.3.5-T1 "
    "(D4/D15 S2)"
)
ROUTE_RE = re.compile(r"^\|\s*`(GET|POST|DELETE)\s+(/[^`\s]*)`", re.MULTILINE)
BACKTICK_RE = re.compile(r"`([^`\n]+)`")

SCHEMAS_WITH_FIXTURES = frozenset(KEY_SETS) | {"images.v1", "proxy-health.v1"}

# --------------------------------------------------------------------------- #
# --scan (D15 S5)
# --------------------------------------------------------------------------- #
SCAN_KEY_RE = re.compile(r"(key|secret|token)", re.IGNORECASE)
# Keys named like a secret that are part of a documented, safe contract/sample:
# the S2 auth-matrix scenario labels, the relay's `tokens_available` bucket, and
# the images.v1 `usage[]` counters (03 D6). Verified absent as credential keys
# in samples/ and fixtures/ (SEC-0542-2). An allow-listed key must still carry an
# object, a number or a `<...>` marker — a plain string flags.
SCAN_KEY_ALLOW = frozenset(
    {"tokens_available", "no_key", "wrong_key", "duplicate_key", "right_key",
     "missing_key", "text_tokens", "image_tokens", "output_tokens"})
# Values that look like a live credential, wherever they appear (SEC-0542-2).
SECRET_VALUE_RES = (
    ("api key", re.compile(r"sk-[A-Za-z0-9_-]{16,}")),
    ("bearer token", re.compile(r"Bearer\s+\S{16,}")),
    ("api key header", re.compile(r"x-api-key:\s*(?![<\s]|WRONG-KEY)")),
)
# A redaction marker, anchored on the whole string: `<text sha256=ab12>` and the
# legacy `<prompt sha256=ab12>` pass, while
# `<text sha256=ab12> the rest of the story` does not (SEC-0542-3).
SCAN_CONTENT_MARKER_RE = re.compile(r"^<[a-z_ ]*sha256=[0-9a-f]{8,64}>$")
# Content keys (D15 S5): the value under one of them is story text. The name may
# carry a prefix (`video_terms`, `video_music_prompt`), which is why the rule
# matches the tail of the key name.
SCAN_CONTENT_KEY_RE = re.compile(r"^(?:[a-z_]*_)?(script|terms|prompt|video_subject)$")
# Path-valued keys: the value may only be a marker or a path, never bare text.
SCAN_PATH_KEY_RE = re.compile(r"^(?:[a-z_]*_)?(audio_file|subtitle_path|materials)$")
# S4-validation.json is a schema-probe capture: its request/input bodies hold
# the 1-char dummies "x" / "t" that triggered MPT's 400 `missing`/`enum` answers
# (see the sample's own `note`). Only those exact values, and only in that file,
# are accepted; any longer value under a content key still flags.
SCAN_CONTENT_ALLOW = {"S4-validation.json": frozenset({"x", "t"})}
# A redaction marker used as a value: `<task_dir>/x.mp3`, `<random-x-task-id>:
# task not found`.
SCAN_STRING_ALLOW = (
    re.compile(r"^<[^<>]{1,120}>(/[\w./-]*)?$"),
    re.compile(r"^<[^<>]{1,120}>: [\w -]{0,80}$"),
)
# A URI is a service address, never a host path: strip it before the path rules
# so that `http://host/v1/x` cannot be read as the path `/host/v1/x`.
URI_RE = re.compile(r"[A-Za-z][A-Za-z0-9+.-]*://[^\s\"'<>`)\\]")
# An absolute host path is a `/seg/seg` token, or `~/…` / `$HOME/…`, not
# preceded by a word char, `.`, `/`, `$` or a marker delimiter. So
# `/opt/homebrew/bin/ffmpeg`, `File "/Users/alice/app.py"`, `ffmpeg_path='/opt/…'`,
# `--output=/Volumes/…`, `` `/root/x` `` and `$HOME/x/y` all flag, while
# `./storage/...`, `<task_dir>/x.mp3` and URIs do not (SEC-0542-3).
ABS_PATH_RE = re.compile(
    r"(?<![\w./$<>])(?:/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]*"
    r"|~/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+"
    r"|\$HOME/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+)")
# Request/API URI segments (MPT routes and the stub's `/img/<n>.png` download
# path) are service addresses, never host filesystem paths.
URI_ALLOW_RE = re.compile(
    r"^/(tasks|api|static|ping|docs|openapi|v1|img)(/|$)")
WIN_PATH_RE = re.compile(r"(?<![\w.:/-])[A-Za-z]:[\\/]")
# A key:value pair in a text line (log slices, JSONL lines that failed to
# parse), classified by the key name: a content key must hold a marker (or a
# D14 placeholder), a path-valued key must hold a marker or a path (`/…`,
# `./…`) — bare text under either is an unredacted leak. Any other key is
# ignored here and covered by the path/length rules.
LEAK_RE = re.compile(r'"?(?P<key>[a-z_]+)"?\s*[:=]\s*"(?P<value>[^"]*)"')
# The >200-char guard (D15 S5) targets *content* strings. These JSON keys hold
# the sample's own documentation, not content (verified: every long JSON value
# in samples/ is under one of these leaves) — path/leak rules still apply.
SCAN_VALUE_ALLOW_KEYS = frozenset(
    {"note", "pin_evidence", "verdict", "setup", "summary", "evidence"})
# MPT log lines carry ANSI colour codes; rules run on the stripped text.
ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")
# An MPT log line (`YYYY-MM-DD HH:MM:SS | LEVEL | "file:line": …`) after the
# ANSI codes are removed; the path and unredacted-content rules apply, the
# length guard does not (a log line is not a JSON string value).
LOG_LINE_RE = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \|")
# --scan file handling (SEC-0542-4): dotfiles are scanned, these two names are
# housekeeping; a symlink, a non-regular file or a file over the cap fails
# without being read.
SCAN_SKIP_NAMES = frozenset({".DS_Store", ".gitkeep"})
SCAN_MAX_BYTES = 8 * 1024 * 1024
# The >200-char guard (D15 S5) targets *content* strings. These JSON keys hold
# the sample's own documentation, not content (verified: every long JSON value
# in samples/ is under one of these leaves) — path/leak rules still apply.
SCAN_VALUE_ALLOW_KEYS = frozenset(
    {"note", "pin_evidence", "verdict", "setup", "summary", "evidence"})
# MPT log lines carry ANSI colour codes; rules run on the stripped text.
ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")
# An MPT log line (`YYYY-MM-DD HH:MM:SS | LEVEL | "file:line": …`) after the
# ANSI codes are removed; the path and unredacted-content rules apply, the
# length guard does not (a log line is not a JSON string value).
LOG_LINE_RE = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \|")


def usage(parser_msg=None):
    if parser_msg:
        sys.stderr.write("usage error: %s\n" % parser_msg)
    sys.stderr.write(
        "usage: check_fixtures.py DIR | --coverage DIR | --self-test | --scan DIR\n"
        "exit: 0 ok . 1 check failed (file: key) . 2 usage/environment\n")
    return 2


def read_json(path):
    with open(path, "rb") as fh:
        return json.loads(fh.read().decode("utf-8"))


def iter_dicts(node, path=()):
    if isinstance(node, dict):
        yield path, node
        for key, value in node.items():
            yield from iter_dicts(value, path + (str(key),))
    elif isinstance(node, list):
        for i, value in enumerate(node):
            yield from iter_dicts(value, path + ("%d" % i,))


def iter_values(node, path=()):
    if isinstance(node, dict):
        for key, value in node.items():
            yield from iter_values(value, path + (str(key),))
    elif isinstance(node, list):
        for i, value in enumerate(node):
            yield from iter_values(value, path + ("%d" % i,))
    elif isinstance(node, str):
        yield path, node


def keyset_failures(fail, name, where, obj, expected):
    keys = set(obj)
    want = set(expected)
    for key in sorted(want - keys):
        fail(name, where, "missing key `%s`" % key)
    for key in sorted(keys - want):
        fail(name, where, "unexpected key `%s`" % key)


def subdoc_failures(fail, name, root, prefix, where, spec):
    """spec is (json path, element key set) or (json path, element key set, schema)."""
    node = root
    for part in prefix:
        if not isinstance(node, dict) or part not in node:
            return
        node = node[part]
    if node is None:
        return
    key_set = spec["keys"]
    if not isinstance(node, dict):
        fail(name, where, "expected an object, got %s" % type(node).__name__)
        return
    if "by_ref" in spec:
        got = node.get("schema")
        if got != spec["by_ref"]:
            fail(name, where, "schema is %r, expected %r" % (got, spec["by_ref"]))
        return  # by-reference shape (03 D6 / 04 D8): keys owned by that plan
    keyset_failures(fail, name, where, node, key_set)
    for child_prefix, child in spec.get("children", ()):
        subdoc_failures(fail, name, root, prefix + child_prefix, where + "." + ".".join(child_prefix), child)


def bad_prefix(value, name, where, fail, prefixes):
    if not isinstance(value, str):
        return
    for prefix in prefixes:
        if prefix in value:
            fail(name, where, "contains the forbidden path prefix %r" % prefix)
            return


def check_document(name, doc):
    """Validate one parsed fixture. Returns a list of failures."""
    failures = []

    def fail(f_name, where, message):
        failures.append("%s: %s: %s" % (f_name, where, message))

    match = re.fullmatch(r"(render-[a-z-]+\.v1)\.[a-z0-9-]+\.json", name)
    if not match or match.group(1) not in KEY_SETS:
        fail(name, "<file>", "not a D14 fixture name")
        return failures
    family = match.group(1)
    expected = KEY_SETS[family]

    if not isinstance(doc, dict):
        fail(name, "<root>", "expected a JSON object")
        return failures
    if doc.get("schema") != family:
        fail(name, "schema", "%r does not match the filename family %r"
             % (doc.get("schema"), family))
    keyset_failures(fail, name, "<root>", doc, expected)

    if family == "render-submit.v1":
        if isinstance(doc.get("mpt"), dict):
            keyset_failures(fail, name, "mpt", doc["mpt"], D3_MPT_KEYS)
            if doc["mpt"].get("video_script") != PLACEHOLDER:
                fail(name, "mpt.video_script",
                     "must be the placeholder %r" % PLACEHOLDER)
            subject = doc["mpt"].get("video_subject")
            if not isinstance(subject, str) or not VIDEO_SUBJECT_RE.fullmatch(subject):
                fail(name, "mpt.video_subject",
                     "must match the fixture title placeholder %r"
                     % VIDEO_SUBJECT_RE.pattern)
            terms = doc["mpt"].get("video_terms")
            if not isinstance(terms, list):
                fail(name, "mpt.video_terms", "must be a list of scene placeholders")
            else:
                for i, term in enumerate(terms):
                    if not isinstance(term, str) or not VIDEO_TERM_RE.fullmatch(term):
                        fail(name, "mpt.video_terms[%d]" % i,
                             "must match the scene-prompt placeholder %r"
                             % VIDEO_TERM_RE.pattern)
        else:
            fail(name, "mpt", "expected an object")
        subdoc_failures(fail, name, doc, ("image_plan",), "image_plan",
                        {"keys": ELEMENT_KEY_SETS["submit.image_plan"]})

    elif family == "render-status.v1":
        state, stage = doc.get("state"), doc.get("stage")
        if state not in STATE:
            fail(name, "state", "%r not in %s" % (state, list(STATE)))
        if stage not in STAGE:
            fail(name, "stage", "%r not in %s" % (stage, list(STAGE)))
        if not isinstance(doc.get("percent"), int) or isinstance(doc.get("percent"), bool) \
                or not 0 <= doc["percent"] <= 100:
            fail(name, "percent", "must be an integer 0..100")
        if not isinstance(doc.get("cancel_requested"), bool):
            fail(name, "cancel_requested", "must be a boolean")
        outcome = doc.get("outcome")
        if outcome is not None:
            if isinstance(outcome, dict) and outcome.get("code") not in OUTCOME_CODES:
                fail(name, "outcome.code", "%r not in the D5 code list"
                     % outcome.get("code"))
        scenario = STATUS_SCENARIOS.get(name)
        if scenario is not None:
            code = outcome.get("code") if isinstance(outcome, dict) else None
            if (state, stage, doc.get("percent"), code) != scenario:
                fail(name, "state",
                     "scenario mismatch: the README fixture table says state=%s, "
                     "stage=%s, percent=%s, outcome=%s" % tuple(
                         "null" if item is None else item for item in scenario))
            if name in CANCELLED_FIXTURES and doc.get("cancel_requested") is not True:
                fail(name, "cancel_requested",
                     "must be true for the cancelled fixture (T64)")
        subdoc_failures(fail, name, doc, ("mpt",), "mpt",
                        {"keys": ELEMENT_KEY_SETS["status.mpt"]})
        subdoc_failures(fail, name, doc, ("outcome",), "outcome",
                        {"keys": ELEMENT_KEY_SETS["status.outcome"]})
        subdoc_failures(fail, name, doc, ("output",), "output",
                        {"keys": ELEMENT_KEY_SETS["status.output"],
                         "children": ((("video",), {"keys": ELEMENT_KEY_SETS["status.output.video"]}),
                                      (("subtitles",), {"keys": ELEMENT_KEY_SETS["status.output.subtitles"]}))})
        subdoc_failures(fail, name, doc, ("images",), "images",
                        {"keys": (), "by_ref": BY_REFERENCE_SCHEMA["status.images"]})
        for where, value in iter_values(doc):
            for token in FORBIDDEN_IN_STATUS:
                if isinstance(value, str) and token in value:
                    fail(name, ".".join(where), "contains the forbidden token %r" % token)

    elif family == "render-node-health.v1":
        if doc.get("status") not in HEALTH_STATUS:
            fail(name, "status", "%r not in %s" % (doc.get("status"), list(HEALTH_STATUS)))
        reasons = doc.get("reasons")
        if not isinstance(reasons, list):
            fail(name, "reasons", "must be a list")
        else:
            for reason in reasons:
                if reason not in REASONS:
                    fail(name, "reasons", "%r not in the reason set" % (reason,))
        for block, file_key in (("capacity", "health.capacity"), ("mpt", "health.mpt"),
                                ("image_relay", "health.image_relay"),
                                ("assets", "health.assets"), ("disk", "health.disk"),
                                ("adapter", "health.adapter"), ("gpu", "health.gpu")):
            subdoc_failures(fail, name, doc, (block,), block,
                            {"keys": ELEMENT_KEY_SETS[file_key]})
        subdoc_failures(fail, name, doc, ("tts_proxy",), "tts_proxy",
                        {"keys": (), "by_ref": BY_REFERENCE_SCHEMA["health.tts_proxy"]})

    elif family == "render-check.v1":
        for block, file_key in (("input", "check.input"), ("output", "check.output"),
                                ("container", "check.container"), ("video", "check.video"),
                                ("audio", "check.audio"), ("narration", "check.narration"),
                                ("silence", "check.silence"),
                                ("subtitles", "check.subtitles")):
            subdoc_failures(fail, name, doc, (block,), block,
                            {"keys": ELEMENT_KEY_SETS[file_key]})
        subdoc_failures(fail, name, doc, ("loudness",), "loudness",
                        {"keys": ELEMENT_KEY_SETS["check.loudness"],
                         "children": ((("before",), {"keys": ELEMENT_KEY_SETS["check.loudness.measure"]}),
                                      (("after",), {"keys": ELEMENT_KEY_SETS["check.loudness.measure"]}))})
        for list_key in ("warnings", "errors"):
            if not isinstance(doc.get(list_key), list):
                fail(name, list_key, "must be a list")

    elif family == "render-quality.v1":
        if not isinstance(doc.get("pass"), bool):
            fail(name, "pass", "must be a boolean")
        subdoc_failures(fail, name, doc, ("subtitles",), "subtitles",
                        {"keys": ELEMENT_KEY_SETS["quality.subtitles"]})
        checks = doc.get("checks")
        if not isinstance(checks, list):
            fail(name, "checks", "must be a list")
        else:
            for i, item in enumerate(checks):
                if not isinstance(item, dict):
                    fail(name, "checks[%d]" % i, "must be an object")
                    continue
                keyset_failures(fail, name, "checks[%d]" % i, item,
                                ELEMENT_KEY_SETS["quality.checks[]"])
        if not isinstance(doc.get("hints"), list):
            fail(name, "hints", "must be a list")

    # Forbidden content, any depth (F11/S5): no host path, no real script.
    for key_path, node in iter_dicts(doc):
        for key in node:
            if key in FORBIDDEN_KEYS:
                fail(name, ".".join(key_path + (str(key),)),
                     "forbidden content key `%s`" % key)
            if key == "video_script" and node[key] != PLACEHOLDER:
                fail(name, ".".join(key_path + ("video_script",)),
                     "`video_script` may only hold the placeholder %r" % PLACEHOLDER)
    for value_path, value in iter_values(doc):
        bad_prefix(value, name, ".".join(value_path), fail, HOST_PATH_PREFIXES)
    return failures


def collect_fixture_paths(directory):
    try:
        names = sorted(os.listdir(directory))
    except OSError as exc:
        raise EnvironmentError("cannot read %s: %s" % (directory, exc))
    paths = []
    for name in names:
        full = os.path.join(directory, name)
        if not name.endswith(".json") or not os.path.isfile(full):
            continue
        if name in IGNORE_FILES:
            continue
        paths.append((name, full))
    return paths


def check_directory(directory):
    """Returns (failures, notes)."""
    failures, notes = [], []
    paths = collect_fixture_paths(directory)
    present = set(name for name, _ in paths)
    for name in FIXTURES:
        if name not in present:
            failures.append("%s: <file>: missing D14 fixture" % name)
    for name, _ in paths:
        if name not in FIXTURES:
            failures.append("%s: <file>: not in the D14 fixture list" % name)
    for name, full in paths:
        try:
            doc = read_json(full)
        except (OSError, ValueError) as exc:
            failures.append("%s: <json>: %s" % (name, exc))
            continue
        failures.extend(check_document(name, doc))
    notes.append("%d fixtures checked" % len(paths))
    return failures, notes


# --------------------------------------------------------------------------- #
# --coverage
# --------------------------------------------------------------------------- #
def known_field_names():
    names = set()
    for key_set in KEY_SETS.values():
        names.update(key_set)
    names.update(D3_MPT_KEYS)
    for key_set in ELEMENT_KEY_SETS.values():
        names.update(key_set)
    names.update(BY_REFERENCE_SCHEMA.values())
    return names


def covered_tokens(directory):
    keys, values = set(), set()
    for _name, full in collect_fixture_paths(directory):
        doc = read_json(full)
        for _path, node in iter_dicts(doc):
            keys.update(str(key) for key in node)
        for _path, value in iter_values(doc):
            values.add(value)
    return keys, values


def run_coverage(directory):
    if not os.path.isdir(directory):
        return usage("coverage needs an existing fixtures directory: %s" % directory)
    doc_path = os.path.join(os.path.dirname(os.path.abspath(directory)),
                            "render-node.v1.md")
    if not os.path.isfile(doc_path):
        sys.stderr.write("coverage: %s not found (owned by 05.4.1-T2 ContractDoc)\n"
                         % doc_path)
        return 2
    with open(doc_path, "r", encoding="utf-8") as fh:
        text = fh.read()
    keys, values = covered_tokens(directory)
    fields = known_field_names()
    failures, carried, exempt, ignored = [], 0, 0, 0
    present = set(name for name, _ in collect_fixture_paths(directory))

    routes = sorted(set("%s %s" % pair for pair in ROUTE_RE.findall(text)))
    if not routes:
        sys.stderr.write("coverage: no route rows in the agreed form "
                         "(| `METHOD /path`) found in %s\n" % doc_path)
        return 2
    for route in routes:
        if route in ROUTE_FIXTURES:
            target = ROUTE_FIXTURES[route]
            if target in present:
                carried += 1
            else:
                failures.append("%s: <file>: missing - the map assigns it route '%s'"
                                % (target, route))
            print("route   %-38s -> %s" % (route, target))
        elif route in EXEMPT_ROUTES:
            print("route   %-38s exempt: %s" % (route, EXEMPT_ROUTES[route]))
            exempt += 1
        else:
            failures.append("render-node.v1.md: route: no fixture carries %r "
                            "(add it to ROUTE_FIXTURES or EXEMPT_ROUTES)" % route)

    for token in sorted(set(BACKTICK_RE.findall(text))):
        if token in fields:
            if token in keys or token in values:
                carried += 1
            else:
                failures.append("render-node.v1.md: %s: no fixture carries this field"
                                % token)
        elif token in SCHEMAS_WITH_FIXTURES:
            if token in values:
                carried += 1
            else:
                failures.append("render-node.v1.md: %s: no fixture carries this schema"
                                % token)
        elif token in OUTCOME_CODES:
            if token in values:
                carried += 1
            elif token in EXEMPT_OUTCOME_CODES:
                print("code    %-38s exempt: %s" % (token, EXEMPT_OUTCOME_CODES[token]))
                exempt += 1
            else:
                failures.append("render-node.v1.md: %s: no fixture carries this outcome "
                                "code (add an exemption with a reason)" % token)
        elif re.fullmatch(r"adapter_[a-z_]+", token):
            print("error   %-38s exempt: %s" % (token, ERROR_CODE_EXEMPT_REASON))
            exempt += 1
        else:
            ignored += 1
    print("coverage: %d carried, %d exempt, %d ignored (not a contract data token), "
          "%d gaps" % (carried, exempt, ignored, len(failures)))
    for failure in failures:
        print("GAP " + failure)
    return 1 if failures else 0


# --------------------------------------------------------------------------- #
# --self-test
# --------------------------------------------------------------------------- #
def _mutations():
    """(label, writer, expected fragment) per DIR-mode failure class."""
    def rewrite(name, mutate):
        def go(root):
            path = os.path.join(root, name)
            doc = read_json(path)
            mutate(doc)
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(doc, fh, indent=2, ensure_ascii=False)
                fh.write("\n")
        return go

    def drop_key(doc):
        doc.pop("check", None)

    def extra_key(doc):
        doc["surprise"] = True

    def bad_enum(doc):
        doc["stage"] = "renderizing"

    def extra_nested_key(doc):
        doc["mpt"]["unknown_field"] = 1

    def forbidden_key(doc):
        doc["terms"] = ["real spy text"]

    def forbidden_path(doc):
        doc["output"]["video"]["file"] = "/Users/operator/runs/final-1.mp4"

    def bad_placeholder(doc):
        doc["mpt"]["video_script"] = "the real story body"

    def bad_schema(doc):
        doc["schema"] = "render-status.v1"

    def bad_reason(doc):
        doc["reasons"] = ["something_else"]

    def reasons_not_a_list(doc):
        doc["reasons"] = "disk_low"

    def bad_health_status(doc):
        doc["status"] = "degraded"

    def extra_health_block_key(doc):
        doc["disk"]["unknown_field"] = 1

    def mpt_not_an_object(doc):
        doc["mpt"] = []

    def extra_image_plan_key(doc):
        doc["image_plan"]["unknown_field"] = 1

    def bad_outcome_code(doc):
        doc["outcome"] = {"code": "tts_broken", "detail": None, "message": None}

    def percent_not_an_int(doc):
        doc["percent"] = True

    def cancel_not_a_bool(doc):
        doc["cancel_requested"] = "no"

    def scenario_mismatch(doc):
        doc["state"] = "running"

    def status_forbidden_token(doc):
        doc["render_job_id"] = "http://mpt.internal/v1/tasks/x"

    def extra_output_subtitles_key(doc):
        doc["output"]["subtitles"]["unknown_field"] = 1

    def bad_subject_placeholder(doc):
        doc["mpt"]["video_subject"] = "The real story title"

    def bad_terms_placeholder(doc):
        doc["mpt"]["video_terms"] = ["a real scene prompt"]

    def warnings_not_a_list(doc):
        doc["warnings"] = "loudnorm_dynamic"

    def errors_not_a_list(doc):
        doc["errors"] = {}

    def extra_check_block_key(doc):
        doc["subtitles"]["unknown_field"] = 1

    def pass_not_a_bool(doc):
        doc["pass"] = "yes"

    def hints_not_a_list(doc):
        doc["hints"] = "none"

    def extra_check_row_key(doc):
        doc["checks"][0]["unknown_field"] = 1

    def add_extra_fixture(root):
        with open(os.path.join(root, "render-status.v1.extra.json"), "w",
                  encoding="utf-8") as fh:
            fh.write("{}\n")

    def delete_ok_fixture(root):
        os.remove(os.path.join(root, "render-check.v1.ok.json"))

    return [
        ("missing top-level key", rewrite("render-status.v1.succeeded.json", drop_key),
         "missing key `check`"),
        ("unexpected top-level key", rewrite("render-status.v1.succeeded.json", extra_key),
         "unexpected key `surprise`"),
        ("bad state/stage enum", rewrite("render-status.v1.running-images.json", bad_enum),
         "stage: 'renderizing'"),
        ("extra nested key", rewrite("render-status.v1.running-images.json", extra_nested_key),
         "mpt: unexpected key `unknown_field`"),
        ("forbidden content key", rewrite("render-status.v1.succeeded.json", forbidden_key),
         "terms: forbidden content key"),
        ("host absolute path", rewrite("render-status.v1.succeeded.json", forbidden_path),
         "contains the forbidden path prefix '/Users/'"),
        ("video_script not the placeholder",
         rewrite("render-submit.v1.landscape-vi.json", bad_placeholder),
         "mpt.video_script: must be the placeholder"),
        ("video_subject not the placeholder",
         rewrite("render-submit.v1.landscape-vi.json", bad_subject_placeholder),
         "mpt.video_subject: must match"),
        ("video_terms not the placeholder",
         rewrite("render-submit.v1.landscape-vi.json", bad_terms_placeholder),
         "mpt.video_terms[0]: must match"),
        ("schema/filename mismatch", rewrite("render-submit.v1.portrait-en.json", bad_schema),
         "schema:"),
        ("bad health reason", rewrite("render-node-health.v1.disk-low.json", bad_reason),
         "reasons: 'something_else'"),
        ("health reasons not a list",
         rewrite("render-node-health.v1.proxy-down.json", reasons_not_a_list),
         "reasons: must be a list"),
        ("bad health status", rewrite("render-node-health.v1.ok.json", bad_health_status),
         "status: 'degraded'"),
        ("extra health block key",
         rewrite("render-node-health.v1.disk-low.json", extra_health_block_key),
         "disk: unexpected key `unknown_field`"),
        ("mpt not an object", rewrite("render-submit.v1.portrait-en.json", mpt_not_an_object),
         "mpt: expected an object"),
        ("extra image_plan key",
         rewrite("render-submit.v1.no-music.json", extra_image_plan_key),
         "image_plan: unexpected key `unknown_field`"),
        ("bad outcome code", rewrite("render-status.v1.succeeded.json", bad_outcome_code),
         "outcome.code: 'tts_broken'"),
        ("percent not an integer", rewrite("render-status.v1.queued.json", percent_not_an_int),
         "percent: must be an integer 0..100"),
        ("cancel_requested not a boolean",
         rewrite("render-status.v1.queued.json", cancel_not_a_bool),
         "cancel_requested: must be a boolean"),
        ("filename/scenario mismatch",
         rewrite("render-status.v1.queued.json", scenario_mismatch), "scenario mismatch"),
        ("forbidden status token",
         rewrite("render-status.v1.succeeded.json", status_forbidden_token),
         "contains the forbidden token 'http://'"),
        ("extra output.subtitles key",
         rewrite("render-status.v1.succeeded.json", extra_output_subtitles_key),
         "output.subtitles: unexpected key `unknown_field`"),
        ("warnings not a list", rewrite("render-check.v1.loud.json", warnings_not_a_list),
         "warnings: must be a list"),
        ("errors not a list", rewrite("render-check.v1.ok.json", errors_not_a_list),
         "errors: must be a list"),
        ("extra check block key", rewrite("render-check.v1.ok.json", extra_check_block_key),
         "subtitles: unexpected key `unknown_field`"),
        ("pass not a boolean", rewrite("render-quality.v1.pass.json", pass_not_a_bool),
         "pass: must be a boolean"),
        ("hints not a list", rewrite("render-quality.v1.pass.json", hints_not_a_list),
         "hints: must be a list"),
        ("extra checks[] row key", rewrite("render-quality.v1.pass.json", extra_check_row_key),
         "checks[0]: unexpected key `unknown_field`"),
        ("extra file outside the D14 list", add_extra_fixture,
         "render-status.v1.extra.json: <file>: not in the D14 fixture list"),
        ("deleted fixture", delete_ok_fixture,
         "render-check.v1.ok.json: <file>: missing D14 fixture"),
    ]


def _scan_mutations():
    """(label, writer, expected fragment) per --scan failure class. Each writer
    builds a self-contained corpus in the case directory it is given."""
    def json_file(name, doc):
        def go(root):
            with open(os.path.join(root, name), "w", encoding="utf-8") as fh:
                json.dump(doc, fh, ensure_ascii=False)
                fh.write("\n")
        return go

    def text_file(name, text):
        def go(root):
            with open(os.path.join(root, name), "w", encoding="utf-8") as fh:
                fh.write(text)
        return go

    def symlink_file(root):
        sub = os.path.join(root, "sub")
        os.mkdir(sub)
        with open(os.path.join(sub, "note.txt"), "w", encoding="utf-8") as fh:
            fh.write("nothing to leak\n")
        os.symlink("/etc/hosts", os.path.join(root, "linked-log.txt"))
        os.symlink(sub, os.path.join(root, "linked-dir"))

    def oversize_file(root):
        path = os.path.join(root, "huge-sample.json")
        with open(path, "wb") as fh:
            fh.truncate(SCAN_MAX_BYTES + 1)

    long_prefix = ("<text sha256=0123456789abcdef> " + "Ngày xửa ngày xưa " * 20)
    return [
        ("content value without a marker",
         json_file("S1.json", {"params": {"video_script": "Ngày xửa ngày xưa có một cô bé"}}),
         "content value under 'video_script' not redacted"),
        ("content array element without a marker",
         json_file("S2.json", {"params": {"video_terms": ["a real scene prompt"]}}),
         "content value under 'video_terms' not redacted"),
        ("content value in a JSONL line",
         text_file("S3.jsonl", '{"video_subject": "The real story title"}\n'),
         "content value under 'video_subject' not redacted"),
        ("content value in a text line",
         text_file("S4.txt", '  "video_script": "1. Ngày xưa có một cô bé",\n'),
         "content value under 'video_script' not redacted"),
        ("content value under a log line",
         text_file("S5-log.txt",
                   '2026-09-26 22:11:24 | INFO | "./app/services/task.py":25 - '
                   '{"video_script": "Ngày xửa"}\n'),
         "content value under 'video_script' not redacted"),
        ("path value without a redaction marker",
         json_file("S6.json", {"params": {"audio_file": "audio.mp3"}}),
         "path value under 'audio_file' not redacted"),
        ("api-key-shaped value",
         json_file("S7.json", {"request": {"api_key": "sk-proj-" + "a" * 20}}),
         "secret-shaped value (api key)"),
        ("bearer token",
         text_file("S8.txt", "Authorization: Bearer abcdefghijklmnopqrst\n"),
         "secret-shaped value (bearer token)"),
        ("api key header",
         text_file("S9.txt", "x-api-key: realkeyvalue123456\n"),
         "secret-shaped value (api key header)"),
        ("allow-listed key with a plain string",
         json_file("S10.json", {"tokens_available": "plenty"}),
         "allow-listed key 'tokens_available'"),
        ("secret-ish key outside the allow-list",
         json_file("S17.json", {"api_key": "not-a-secret-shape"}),
         "key matches *key*|*secret*|*token*"),
        ("placeholder under the wrong key",
         json_file("S19.json", {"params": {"video_script": "<styled scene prompt vi 1>"}}),
         "content value under 'video_script' not redacted"),
        ("backtick path",
         json_file("S18.json", {"cmd": "cat `/root/.ssh/id_ed25519`"}),
         "absolute path '/root/.ssh/id_ed25519'"),
        ("traceback path",
         text_file("S11.txt",
                   '  File "/Users/alice/MoneyPrinterTurbo/app/services/task.py", line 953\n'),
         "absolute path '/Users/alice/MoneyPrinterTurbo/app/services/task.py'"),
        ("key=value path",
         json_file("S12.json", {"env": "ffmpeg_path='/opt/homebrew/bin/ffmpeg'"}),
         "absolute path '/opt/homebrew/bin/ffmpeg'"),
        ("$HOME path", json_file("S13.json", {"env": "$HOME/MoneyPrinterTurbo/app"}),
         "absolute path '$HOME/MoneyPrinterTurbo/app'"),
        ("~/ path", json_file("S14.json", {"env": "~/Movies/nts/final.mp4"}),
         "absolute path '~/Movies/nts/final.mp4'"),
        ("marker prefix on unredacted text",
         json_file("S15.json", {"request": long_prefix}),
         "outside the allow-list"),
        ("secret in a dotfile",
         json_file(".S16-raw.json", {"headers": "x-api-key: realkeyvalue123456"}),
         "secret-shaped value (api key header)"),
        ("symlink", symlink_file, "symlink not allowed"),
        ("oversize file", oversize_file, "too large to scan"),
    ]


def run_self_test(directory):
    problems = []
    tmp = tempfile.mkdtemp(prefix="nts-check-fixtures-")
    try:
        for name in FIXTURES:
            shutil.copy2(os.path.join(directory, name), os.path.join(tmp, name))
        pristine = main([tmp])
        if pristine != 0:
            problems.append("pristine copy exited %d, expected 0" % pristine)
            return problems
        print("self-test: pristine copy ok")
        pristine_scan = run_capture(["--scan", tmp])
        if pristine_scan[0] != 0:
            problems.append("pristine fixtures failed --scan (exit %d)" % pristine_scan[0])
        else:
            print("self-test: %-32s -> exit 0" % "pristine copy passes --scan")
        for label, mutation, expected in _mutations():
            case = tempfile.mkdtemp(prefix="nts-mut-", dir=tmp)
            for name in FIXTURES:
                shutil.copy2(os.path.join(directory, name), os.path.join(case, name))
            try:
                mutation(case)
            except OSError as exc:
                problems.append("%s: cannot build the case: %s" % (label, exc))
                continue
            status, text = run_capture([case])
            if status != 1:
                problems.append("%s: exited %d, expected 1" % (label, status))
                continue
            if expected not in text:
                problems.append("%s: output lacks %r" % (label, expected))
                continue
            print("self-test: %-36s -> exit 1 (%s)" % (label, expected))
        for label, mutation, expected in _scan_mutations():
            case = tempfile.mkdtemp(prefix="nts-scan-", dir=tmp)
            try:
                mutation(case)
            except OSError as exc:
                problems.append("%s: cannot build the case: %s" % (label, exc))
                continue
            status, text = run_capture(["--scan", case])
            if status != 1:
                problems.append("%s: --scan exited %d, expected 1" % (label, status))
                continue
            if expected not in text:
                problems.append("%s: --scan output lacks %r" % (label, expected))
                continue
            print("self-test: scan %-30s -> exit 1 (%s)" % (label, expected))
        # QA-0542-2: --coverage must name a mapped fixture that is absent.
        doc_path = os.path.normpath(os.path.join(directory, os.pardir, "render-node.v1.md"))
        if not os.path.isfile(doc_path):
            print("self-test: %-36s -> skipped (no %s)" % ("coverage with a deleted fixture",
                                                           os.path.basename(doc_path)))
        else:
            cov_root = tempfile.mkdtemp(prefix="nts-cov-", dir=tmp)
            cov_dir = os.path.join(cov_root, "fixtures")
            os.mkdir(cov_dir)
            missing = "render-status.v1.cancelled.json"
            for name in FIXTURES:
                if name != missing:
                    shutil.copy2(os.path.join(directory, name), os.path.join(cov_dir, name))
            shutil.copy2(doc_path, os.path.join(cov_root, "render-node.v1.md"))
            status, text = run_capture(["--coverage", cov_dir])
            if status != 1 or missing not in text:
                problems.append("coverage with a deleted mapped fixture: exit %d, lacks %r"
                                % (status, missing))
            else:
                print("self-test: %-36s -> exit 1 (%s)" % (
                    "coverage with a deleted fixture", missing))
        # usage errors must be 2
        for argv, label in (([], "no arguments"),
                            (["--coverage"], "flag without a directory"),
                            (["--coverage="], "flag with an empty directory"),
                            (["--scan="], "--scan with an empty directory"),
                            (["--coveragex", tmp], "unknown --coverage* flag"),
                            (["--scanfoo"], "unknown --scan* flag"),
                            (["--self-test=x"], "unknown --self-test value"),
                            ([tmp, "--self-test"], "two modes at once")):
            status = run_capture(argv)[0]
            if status != 2:
                problems.append("%s: exited %d, expected 2" % (label, status))
            else:
                print("self-test: %-32s -> exit 2" % label)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return problems


def run_capture(argv):
    """Run main(argv), returning (exit_code, captured stdout+stderr text)."""
    import contextlib
    import io
    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
        code = main(argv)
    return code, buffer.getvalue()


# --------------------------------------------------------------------------- #
# --scan
# --------------------------------------------------------------------------- #
def _string_ok(value):
    if len(value) <= MAX_STRING:
        return True
    for pattern in SCAN_STRING_ALLOW:
        if pattern.search(value):
            return True
    return False


def _content_value_ok(value, key, name):
    """D15 S5: a content value is a redaction marker. The D14 placeholders are
    the only other accepted shapes and each is bound to its own key, so that
    `--scan worker-node/contract/fixtures` (wired by 11.3.5-T1) passes while a
    real value still flags. The S4 probe dummies are the one file-scoped
    exception."""
    if SCAN_CONTENT_MARKER_RE.fullmatch(value):
        return True
    if key == "video_script" and value == PLACEHOLDER:
        return True
    if key == "video_subject" and VIDEO_SUBJECT_RE.fullmatch(value):
        return True
    if key.endswith("terms") and VIDEO_TERM_RE.fullmatch(value):
        return True
    return value in SCAN_CONTENT_ALLOW.get(name, ())


def _allow_key_value_ok(value):
    """An allow-listed *key*|*secret*|*token* key carries a documented scenario
    label or token bucket: an object, a number or a `<...>` marker (SEC-0542-2)."""
    if isinstance(value, bool):
        return False
    if isinstance(value, (int, float, dict)):
        return True
    return isinstance(value, str) and SCAN_STRING_ALLOW[0].search(value) is not None


def _scanned_strings(name, text):
    """Yield (kind, label, string, leaf key) for every checked string.

    kind: "value" (JSON scalar value; `leaf key` is the key it sits under, array
    elements inheriting their parent key), "key" (secret-ish JSON key name),
    "allowkey" (allow-listed key whose value is the string in field 3),
    "line" (plain text).
    A `.json` file is first parsed as one document; on failure it is treated as
    JSONL; on failure again, as plain text. `.jsonl` is parsed per line.
    """
    if name.endswith(".jsonl"):
        for lineno, line in enumerate(text.splitlines(), 1):
            if not line.strip():
                continue
            try:
                doc = json.loads(line)
            except ValueError:
                yield ("line", "line %d" % lineno, line, None)
                continue
            yield from _json_strings(doc, "line %d" % lineno)
        return
    if name.endswith(".json"):
        try:
            yield from _json_strings(json.loads(text), "")
            return
        except ValueError:
            for lineno, line in enumerate(text.splitlines(), 1):
                if not line.strip():
                    continue
                try:
                    doc = json.loads(line)
                except ValueError:
                    yield ("line", "line %d" % lineno, line, None)
                    continue
                yield from _json_strings(doc, "line %d" % lineno)
            return
    for lineno, line in enumerate(text.splitlines(), 1):
        yield ("line", "line %d" % lineno, line, None)


def _json_strings(node, where, path=(), leaf=None):
    if isinstance(node, dict):
        for key, value in node.items():
            key_name = str(key)
            label = where + ("." if where and path else "") + ".".join(path + (key_name,))
            if SCAN_KEY_RE.search(key_name):
                if key_name not in SCAN_KEY_ALLOW:
                    yield ("key", key_name, key_name, key_name)
                elif not _allow_key_value_ok(value):
                    what = "plain string" if isinstance(value, str) else type(value).__name__
                    yield ("allowkey", label, what, key_name)
            yield from _json_strings(value, where, path + (key_name,), key_name)
    elif isinstance(node, list):
        for i, value in enumerate(node):
            yield from _json_strings(value, where, path + ("%d" % i,), leaf)
    elif isinstance(node, str):
        label = where + ("." if where and path else "") + ".".join(path)
        yield ("value", label, node, leaf)


def run_scan(directory):
    if not os.path.isdir(directory):
        return usage("--scan needs an existing directory: %s" % directory)
    cap_mib = SCAN_MAX_BYTES // (1024 * 1024)
    failures = []
    files = 0
    for root, dirs, names in os.walk(directory):
        for dirname in list(dirs):
            full = os.path.join(root, dirname)
            if os.path.islink(full):
                failures.append("%s: <dir>: symlink not allowed under --scan"
                                % os.path.relpath(full, directory))
                dirs.remove(dirname)
        for name in sorted(names):
            if name in SCAN_SKIP_NAMES:
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, directory)
            try:
                info = os.lstat(full)
            except OSError as exc:
                failures.append("%s: <file>: %s" % (rel, exc))
                continue
            if stat.S_ISLNK(info.st_mode):
                failures.append("%s: <file>: symlink not allowed under --scan" % rel)
                continue
            if not stat.S_ISREG(info.st_mode):
                failures.append("%s: <file>: not a regular file" % rel)
                continue
            if info.st_size > SCAN_MAX_BYTES:
                failures.append("%s: <file>: %d bytes (> %d MiB) - too large to scan"
                                % (rel, info.st_size, cap_mib))
                continue
            files += 1
            try:
                with open(full, "r", encoding="utf-8") as fh:
                    text = fh.read()
            except (OSError, UnicodeDecodeError) as exc:
                failures.append("%s: <read>: %s" % (rel, exc))
                continue
            for kind, label, value, leaf in _scanned_strings(name, text):
                if not value:
                    continue
                if kind == "line":
                    value = ANSI_RE.sub("", value)  # log lines carry colour codes
                if kind == "key":
                    failures.append("%s: %s: key matches *key*|*secret*|*token*"
                                    % (rel, label))
                    continue
                if kind == "allowkey":
                    failures.append("%s: %s: allow-listed key '%s' carries a %s, not "
                                    "an object, a number or a <...> marker"
                                    % (rel, label, leaf, value))
                    continue
                if kind == "value" and leaf:
                    if SCAN_CONTENT_KEY_RE.match(leaf):
                        if not _content_value_ok(value, leaf, name):
                            failures.append("%s: %s: content value under '%s' not "
                                            "redacted" % (rel, label, leaf))
                    elif SCAN_PATH_KEY_RE.match(leaf) and not (
                            value.startswith("<") or value[0] in "./"):
                        failures.append("%s: %s: path value under '%s' not redacted"
                                        % (rel, label, leaf))
                if kind in ("value", "line"):
                    for match in LEAK_RE.finditer(value):
                        leak_key, leak_value = match.group("key"), match.group("value")
                        if not leak_value or leak_value.startswith("<"):
                            continue  # nothing to leak, or a redaction marker
                        if SCAN_CONTENT_KEY_RE.match(leak_key):
                            failures.append("%s: %s: content value under '%s' not "
                                            "redacted" % (rel, label, leak_key))
                        elif SCAN_PATH_KEY_RE.match(leak_key) and leak_value[0] not in "./":
                            failures.append("%s: %s: path value under '%s' not "
                                            "redacted" % (rel, label, leak_key))
                for token in ABS_PATH_RE.findall(URI_RE.sub(" ", value)):
                    if not URI_ALLOW_RE.match(token):
                        failures.append("%s: %s: absolute path %r"
                                        % (rel, label, token))
                        break
                if WIN_PATH_RE.search(value):
                    failures.append("%s: %s: absolute path (drive letter)"
                                    % (rel, label))
                for secret_label, pattern in SECRET_VALUE_RES:
                    if pattern.search(value):
                        failures.append("%s: %s: secret-shaped value (%s)"
                                        % (rel, label, secret_label))
                if not _string_ok(value):
                    doc_key = kind == "value" and label.rsplit(".", 1)[-1] in SCAN_VALUE_ALLOW_KEYS
                    log_line = kind == "line" and LOG_LINE_RE.match(value)
                    if not (doc_key or log_line):
                        failures.append(
                            "%s: %s: string of %d chars (> %d) outside the "
                            "allow-list" % (rel, label, len(value), MAX_STRING))
    print("scan: %d files under %s" % (files, directory))
    for failure in failures:
        print("SCAN " + failure)
    return 1 if failures else 0


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    coverage = scan = self_test = None
    positional = []
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg in ("-h", "--help"):
            sys.stdout.write(__doc__)
            return 0
        if arg.startswith("--coverage"):
            if arg == "--coverage":
                i += 1
                if i >= len(argv):
                    return usage("--coverage needs a directory")
                coverage = argv[i]
            elif arg.startswith("--coverage="):
                coverage = arg.split("=", 1)[1]
                if not coverage:
                    return usage("--coverage needs a directory")
            else:
                return usage("unknown flag %r" % arg)
        elif arg.startswith("--scan"):
            if arg == "--scan":
                i += 1
                if i >= len(argv):
                    return usage("--scan needs a directory")
                scan = argv[i]
            elif arg.startswith("--scan="):
                scan = arg.split("=", 1)[1]
                if not scan:
                    return usage("--scan needs a directory")
            else:
                return usage("unknown flag %r" % arg)
        elif arg == "--self-test":
            self_test = True
        elif arg.startswith("-"):
            return usage("unknown flag %r" % arg)
        else:
            positional.append(arg)
        i += 1

    modes = sum(1 for mode in (coverage, scan, self_test) if mode)
    if modes and positional:
        return usage("a directory argument cannot be combined with a mode flag")
    if modes > 1:
        return usage("pick one mode")

    if self_test:
        directory = DEFAULT_FIXTURES
        if not os.path.isdir(directory):
            return usage("fixtures directory %s not found" % directory)
        problems = run_self_test(directory)
        if problems:
            for problem in problems:
                print("SELFTEST " + problem)
            return 1
        print("self-test: ok")
        return 0

    if coverage is not None:
        return run_coverage(coverage)
    if scan is not None:
        return run_scan(scan)
    if not positional:
        return usage("no directory given")
    if len(positional) > 1:
        return usage("one directory at a time")
    directory = positional[0]
    if not os.path.isdir(directory):
        return usage("not a directory: %s" % directory)
    failures, notes = check_directory(directory)
    for note in notes:
        print("ok: %s" % note)
    for failure in failures:
        print("FAIL " + failure)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
