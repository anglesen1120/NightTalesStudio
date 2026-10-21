#!/usr/bin/env python3
"""mpt-upgrade-diff.py -- MPT upgrade drift check (05.1.1-T1, plan 05).

Compares a **candidate** MPT checkout against the three pinned baselines and answers
one question before an ``MPT_COMMIT`` bump: did anything D2, D3 or D4 depends on
change?  It fails closed (exit 1) on a change to a config key the node/Mac profiles
use, to a ``VideoParams`` field, or to a route the adapter uses, and stays quiet
(exit 0 + advisory lines) on everything else.

Sources: plans/features/05-video-assembly-subtitles/README.md D1 (ownership), D2 (node
config rows), D3 (submit fields), D4 (MPT-side route usage), C1, C5, C7, C20, C22,
F09, F10, F12, F22, T5, T6, T16, T19, T20; task file 05.1-render-config/05.1.1-T1.

Usage
    mpt-upgrade-diff.py --candidate DIR
                        [--baseline-config worker-node/mpt/config.example.pinned.toml]
                        [--baseline-params worker-node/contract/mpt-videoparams.pinned.json]
                        [--baseline-routes worker-node/contract/mpt-routes.pinned.json]
                        [--json]
    mpt-upgrade-diff.py --self-test

The candidate must carry its own interpreter at ``<candidate>/.venv/bin/python``
(``uv sync`` already run there).  **The check executes the candidate's own code** (one
child process importing ``app.models.schema`` and ``app.asgi``) -- only run it against a
commit fetched from the pinned upstream.  The child gets a minimal allow-listed
environment (PATH, LANG, LC_ALL, TMPDIR), a throwaway 0700 ``HOME``, ``stdin`` from
``/dev/null`` and its own process group, and the parent kills that group on timeout.
That is confinement of the *environment*, not a sandbox: the child still runs with the
operator's privileges and can read the filesystem and the network.  The candidate's
``config.toml``, ``resource/`` and ``storage/`` are restored to their pre-run state
(only entries the child created are removed); ``__pycache__`` is deliberately kept.

Exit codes (01 D9): 0 no blocking change . 1 at least one blocking change . 2 usage or
environment (missing ``--candidate``, missing/unreadable/unparsable baseline, missing
``<candidate>/.venv/bin/python``, unparsable candidate file, ambiguous or oversized
child output, subprocess timeout, the 90 s whole-run deadline, a candidate HEAD that is
not ``MPT_COMMIT``).

Secret-bearing config keys (``*key``/``*keys``/``*token``/``*secret``/``*password``/
``*passwd``/``*credential``/``*auth``) and any string value carrying URL userinfo print
as ``<set>`` / ``<empty>`` / ``<changed>``, never their value.  Child stderr is never
echoed: it goes to a 0600 file under ``$NTS_HOME/logs`` and the report names only the
exception class and that path.  Every candidate-derived string in the text report has
its C0/C1 controls escaped.

Stdlib only; run with ``uv run --no-project --python 3.12``.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import hashlib
import io
import json
import os
import re
import shlex
import shutil
import signal
import subprocess
import sys
import tempfile
import textwrap
import threading
import time
import tomllib
from pathlib import Path

EXIT_OK = 0
EXIT_BLOCKING = 1
EXIT_ENV = 2

HERE = Path(__file__).resolve().parent
WORKER_NODE = HERE.parent
DEFAULT_BASELINE_CONFIG = WORKER_NODE / "mpt" / "config.example.pinned.toml"
DEFAULT_BASELINE_PARAMS = WORKER_NODE / "contract" / "mpt-videoparams.pinned.json"
DEFAULT_BASELINE_ROUTES = WORKER_NODE / "contract" / "mpt-routes.pinned.json"
DEFAULT_PINS = WORKER_NODE / "pins.env"

# --------------------------------------------------------------------------- #
# Diff (a) -- config keys/sections/defaults (D2, C20, F16, T5, T19, T20)
# The `used` set, verbatim from the task file.  Bare names are `[app]` keys except
# the three top-level audit keys; `whisper.*` / `kokoro.*` are tables of their own.
# --------------------------------------------------------------------------- #
USED = (
    "log_level", "listen_host", "listen_port", "app.api_key", "video_source",
    "pexels_api_keys", "pixabay_api_keys", "openai_image_base_url",
    "openai_image_api_keys", "openai_image_model", "openai_image_size",
    "openai_image_prompt_template", "material_directory", "endpoint",
    "twelvelabs_rerank_terms", "llm_provider", "openai_api_key", "openai_base_url",
    "subtitle_provider", "enable_redis", "max_concurrent_tasks", "max_queued_tasks",
    "video_codec", "upload_post_enabled", "upload_post_auto_upload",
    "upload_post_api_key", "whisper.model_size", "whisper.device",
    "whisper.compute_type", "whisper.initial_prompt", "kokoro.base_url",
    "kokoro.api_key", "kokoro.model_id", "kokoro.voices",
)
# Top-level keys of config.example.toml; every other bare `used` name is under [app].
TOP_LEVEL_USED = ("log_level", "listen_host", "listen_port")
# Keys that exist only as comments at the pin (C20: video_codec L440,
# whisper.initial_prompt L511).  Compared as comment lines, advisory (T5).
COMMENT_ONLY_USED = ("app.video_codec", "whisper.initial_prompt")
SECTION_ORDER = ("", "app", "whisper", "kokoro")

# --------------------------------------------------------------------------- #
# Diff (b) -- VideoParams JSON schema (D3, C22, C5, F10, T20)
# --------------------------------------------------------------------------- #
D3_FIELDS = (
    "video_subject", "video_script", "video_terms", "video_aspect", "video_fit_mode",
    "video_concat_mode", "video_transition_mode", "video_clip_duration",
    "video_clip_speed", "match_materials_to_script", "video_count", "video_source",
    "video_materials", "custom_audio_file", "video_language", "voice_name",
    "voice_volume", "voice_rate", "bgm_type", "bgm_file", "bgm_volume",
    "video_music_prompt", "sonilo_bgm_prompt", "subtitle_enabled",
    "subtitle_display_mode", "subtitle_animation", "subtitle_position",
    "custom_position", "font_size", "stroke_color", "stroke_width", "text_fore_color",
    "text_background_color", "rounded_subtitle_background", "font_name",
    "paragraph_number", "video_script_prompt", "custom_system_prompt", "n_threads",
)
TYPE_NAMES = {
    "string": "str", "integer": "int", "number": "number", "boolean": "bool",
    "null": "null", "array": "array", "object": "object",
}

# --------------------------------------------------------------------------- #
# Diff (c) -- routes (C1, D4, F12, T16)
# --------------------------------------------------------------------------- #
BLOCKING_ROUTE_PATHS = (
    "/api/v1/videos", "/api/v1/tasks/{task_id}", "/api/v1/download/{file_path}",
    "/ping",
)
# The D4 MPT-side (method, path) pairs that carry `verify_token` at the pin (C1, T16).
# Keyed per method: GET and DELETE share `/api/v1/tasks/{task_id}`, so a path-only map
# would let the last registered route decide (SEC-0511-1).  `/ping` never had the
# dependency, so it is reported as info only.
VERIFY_TOKEN_ROUTES = (
    ("POST", "/api/v1/videos"),
    ("GET", "/api/v1/tasks/{task_id}"),
    ("DELETE", "/api/v1/tasks/{task_id}"),
    ("GET", "/api/v1/download/{file_path}"),
)
PATH_PARAM_RE = re.compile(r"\{([A-Za-z_][A-Za-z0-9_]*)\s*:\s*path\}")

# --------------------------------------------------------------------------- #
# Diff (d) -- progress constants (C7, F09, T6)
# --------------------------------------------------------------------------- #
PROGRESS_LITERALS = (5, 10, 20, 30, 40, 50, 100)
PROGRESS_RE = re.compile(r"\bprogress=(\d+)")
PROGRESS_STEP_RE = re.compile(r"_progress\s*\+=\s*50\s*/\s*params\.video_count\s*/\s*2")
PROGRESS_STEP_EXPECTED = 2

# --------------------------------------------------------------------------- #
# Child process contract (01 D9, secrets)
# --------------------------------------------------------------------------- #
SENTINEL = "__NTS_MPT_JSON__"
# HOME is not inherited: run_child() points it at a throwaway 0700 directory.
CHILD_ENV_ALLOW = ("PATH", "LANG", "LC_ALL", "TMPDIR")
# One whole-run deadline; the child and git get min(remaining, their own cap) (PRF-0511-2).
TOTAL_TIMEOUT_S = 90.0
CHILD_TIMEOUT_S = 60.0
GIT_TIMEOUT_S = 10.0
# Bounded child capture: a flooding candidate is killed instead of exhausting memory
# (SEC-0511-3).
CHILD_OUTPUT_CAP = 16 * 1024 * 1024
# Schema flattening: a ref already on the stack is a cycle, and the total node count is
# capped, so a self-referencing $defs/anyOf fan-out cannot spin the parent (SEC-0511-3).
SCHEMA_NODE_BUDGET = 10_000
CONSTRAINT_KEYS = ("minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
                   "minLength", "maxLength", "pattern", "minItems", "maxItems")
# Last-segment names that carry a secret (SEC-0511-6), plus any value with URL userinfo.
SECRET_NAME_RE = re.compile(
    r"(key|keys|token|secret|password|passwd|credential|auth)$", re.IGNORECASE)
URL_USERINFO_RE = re.compile(r"://[^/@\s]+@")
# Candidate worktree entries MPT's own import creates; restored after the child runs
# (PRF-0511-3).  `__pycache__` is intentionally not in the list.
CLEANUP_ENTRIES = ("config.toml", "resource", "storage")
# A line that starts with an exception class, for the stderr hint (SEC-0511-4).
EXC_CLASS_RE = re.compile(
    r"^([A-Za-z_][A-Za-z0-9_.]*(?:Error|Exception|Warning|Interrupt))(?::|$)")

# One fused child: a single interpreter start serves both extractions and prints exactly
# one sentinel line carrying the schema, the path->methods map and the per-(method, path)
# verify_token map (PRF-0511-1, QA-0511-5, SEC-0511-1).
CHILD_CODE = "\n".join((
    "import json",
    "import fastapi.routing",
    "from app.models.schema import VideoParams",
    "import app.asgi as a",
    "paths = {p: sorted({m.upper() for m in d}) for p, d in a.app.openapi()['paths'].items()}",
    "vt = {}",
    "for r in a.app.routes:",
    "    if isinstance(r, fastapi.routing.APIRoute):",
    "        names = [getattr(getattr(d, 'call', None), '__name__', None) for d in r.dependant.dependencies]",
    "        for m in r.methods:",
    "            vt[m.upper() + ' ' + r.path] = 'verify_token' in names",
    "print('" + SENTINEL + "' + json.dumps({'schema': VideoParams.model_json_schema(),"
    " 'paths': paths, 'verify_token': vt}, sort_keys=True))",
    "",
))

TEXT_BLOCKS = ("config", "params", "routes", "progress")

UPGRADE_STEPS = """\
Upgrade procedure -- a green diff is only step 3 of six:
  1. Bump MPT_COMMIT in worker-node/pins.env in its own commit (01 D2).
  2. git -C "$NTS_HOME/src/mpt" fetch && git -C "$NTS_HOME/src/mpt" worktree add \\
       "$NTS_HOME/src/mpt-candidate" "$MPT_COMMIT", then cd "$NTS_HOME/src/mpt-candidate" && uv sync.
  3. mpt-upgrade-diff.py --candidate "$NTS_HOME/src/mpt-candidate" -> exit 0 required before
     anything else.  Steps 2-3 execute the candidate's own code: only run commits fetched
     from the pinned upstream.  The check restores the candidate's config.toml, resource/
     and storage/ to their pre-run state (only what the child created is removed) and
     leaves __pycache__ in place.
  4. Re-run the 05.4.1-T1 scenario subset S0, S3, S5, S12 (pin check, route/OpenAPI
     inventory, validation, restart mid-task) against the candidate; record in
     worker-node/contract/mpt-api.md.
  5. Refresh the three pinned snapshots in the same commit as the bump and re-run the
     diff -- exit 0 against the refreshed baselines.
  6. A confirmed BLOCKING change is escalated to the TL, not patched here: D2/D3/D4,
     MptSubmitFields, the D14 fixtures (05.4.1-T2), the adapter (11.3.5-T1) and
     mock-mpt (11.1.2-T1) move together.  If the change is unacceptable, keep the old
     MPT_COMMIT and revert the bump commit.
"""


class EnvError(Exception):
    """Usage or environment problem -> exit 2."""


class SchemaError(Exception):
    """A candidate file that cannot be read as the expected shape -> exit 2."""


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #
def dotted(name: str) -> str:
    """Map a task-file `used` name to its dotted key in config.example.toml."""
    if name in TOP_LEVEL_USED or "." in name:
        return name
    return "app." + name


USED_DOTTED = tuple(dotted(n) for n in USED)


def render_value(value, *, raw: bool = False) -> str:
    if value is None:
        return "<absent>"
    if raw:
        return sanitise(str(value))
    return json.dumps(value, sort_keys=True, ensure_ascii=False)


def sanitise(text: str) -> str:
    """Escape C0/C1 controls and DEL so a candidate string cannot rewrite the TTY.

    The JSON report keeps json.dumps' own escaping; this is for the text report and for
    messages printed to stderr (SEC-0511-5).
    """
    out = []
    for char in str(text):
        code = ord(char)
        if code < 0x20 or 0x7F <= code <= 0x9F:
            out.append(f"\\x{code:02x}")
        else:
            out.append(char)
    return "".join(out)


def is_secret(name: str) -> bool:
    """True for a last dotted segment that ends in a secret-bearing name (SEC-0511-6)."""
    return bool(SECRET_NAME_RE.search(str(name).rsplit(".", 1)[-1]))


def is_url_secret(value) -> bool:
    """True for a string carrying URL userinfo (`scheme://user:pass@host`)."""
    return isinstance(value, str) and bool(URL_USERINFO_RE.search(value))


def mask_pair(name: str, base, cand) -> tuple[str, str]:
    """Render a config value pair; a secret value is never printed."""
    if not is_secret(name) and not is_url_secret(base) and not is_url_secret(cand):
        return render_value(base), render_value(cand)
    base_set = base is not None and base != "" and base != []
    cand_set = cand is not None and cand != "" and cand != []
    if base_set and cand_set and base != cand:
        return "<set>", "<changed>"
    return ("<set>" if base_set else "<empty>"), ("<set>" if cand_set else "<empty>")


def child_env(home: Path) -> dict:
    """Minimal allow-listed environment; no *KEY*/*TOKEN* variable is inherited.

    ``HOME`` is always the caller's throwaway directory, never the operator's.
    """
    env = {k: os.environ[k] for k in CHILD_ENV_ALLOW if k in os.environ}
    env.setdefault("PATH", "/usr/bin:/bin:/usr/sbin:/sbin")
    env["HOME"] = str(home)
    return env


@contextlib.contextmanager
def fresh_home():
    """A throwaway 0700 directory to use as the child's HOME (SEC-0511-7)."""
    home = Path(tempfile.mkdtemp(prefix="mpt-upgrade-diff-home-"))
    os.chmod(home, 0o700)
    try:
        yield home
    finally:
        shutil.rmtree(home, ignore_errors=True)


def nts_home() -> Path:
    """$NTS_HOME, defaulting to <repo>/.nts (CR-11), as the other bin tools do."""
    env = os.environ.get("NTS_HOME")
    return Path(env).expanduser() if env else WORKER_NODE.parent / ".nts"


def write_stderr_log(data: bytes) -> str | None:
    """Write child stderr to a 0600 file under $NTS_HOME/logs; return its path.

    Child stderr is never echoed (SEC-0511-4): it can carry the candidate's config
    values.  Returns None when there is nothing to write or the write fails.
    """
    if not data:
        return None
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    path = nts_home() / "logs" / f"mpt-upgrade-diff-{stamp}.stderr.log"
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "wb") as handle:
            handle.write(data)
    except OSError:
        return None
    return str(path)


def last_exception_class(stderr: str) -> str:
    """The exception class of the stderr tail, e.g. `ModuleNotFoundError` (SEC-0511-4).

    The traceback's final line is the exception, but a candidate can print more after it,
    so the last non-empty lines are scanned backwards for an Error/Exception/Warning
    shaped class; failing that, the last line's identifier head is used.  Only a class
    name is ever returned -- never a value.
    """
    lines = [line.strip() for line in stderr.splitlines() if line.strip()]
    for line in reversed(lines[-20:]):
        match = EXC_CLASS_RE.match(line)
        if match:
            return match.group(1)
    for line in reversed(lines):
        head = line.split(":", 1)[0].strip()
        if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", head):
            return head
    return "<unknown>"


def unparsable_message(what: str, path, exc: BaseException) -> str:
    """The one-line exit-2 form: `mpt-upgrade-diff.py: <what> unparsable: <path>: <class>`."""
    return f"mpt-upgrade-diff.py: {what} unparsable: {sanitise(str(path))}: {type(exc).__name__}"


def read_baseline_file(path) -> bytes:
    p = Path(path)
    try:
        if not p.is_file():
            raise EnvError(f"baseline missing or not a file: {path}")
        return p.read_bytes()
    except OSError as exc:
        raise EnvError(f"baseline unreadable: {path}: {exc}") from None


def read_pin(name: str, pins_path) -> str | None:
    try:
        text = Path(pins_path).read_text(encoding="utf-8")
    except OSError:
        return None
    for line in text.splitlines():
        if line.startswith(name + "="):
            return line.split("=", 1)[1].strip()
    return None


def baseline_schema(params) -> dict:
    """`model_schema` from the pinned params JSON; wrong shape -> SchemaError (exit 2)."""
    schema = params.get("model_schema") if isinstance(params, dict) else None
    if not isinstance(schema, dict):
        raise SchemaError("`model_schema` is not an object")
    return schema


def baseline_paths(routes) -> dict:
    """`paths` from the pinned routes JSON: path -> list of method strings (exit 2 else)."""
    paths = routes.get("paths") if isinstance(routes, dict) else None
    if not isinstance(paths, dict):
        raise SchemaError("`paths` is not an object of path -> list of methods")
    for path, methods in paths.items():
        if not isinstance(path, str) or not isinstance(methods, list) \
                or not all(isinstance(m, str) for m in methods):
            raise SchemaError(f"`paths[{path}]` is not a list of method strings")
    return paths


def entry(kind: str, key: str, baseline: str, candidate: str, rule: str) -> dict:
    return {"kind": kind, "key": key, "baseline": baseline, "candidate": candidate,
            "rule": rule}


def text_line(severity: str, item: dict) -> str:
    """One report line; every candidate-derived field is control-escaped (SEC-0511-5)."""
    return (f"{sanitise(severity)}  {sanitise(item['key'])}  {sanitise(item['baseline'])}"
            f" -> {sanitise(item['candidate'])}  [{sanitise(item['rule'])}]")


# --------------------------------------------------------------------------- #
# Diff (a) -- config
# --------------------------------------------------------------------------- #
SECTION_RE = re.compile(r"^\s*\[([^\]]+)\]\s*$")
COMMENT_ASSIGN_RE = re.compile(r"^\s*#\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$")


def flatten_toml(text: str) -> dict:
    data = tomllib.loads(text)
    out: dict[str, object] = {}

    def walk(prefix, obj):
        for key, value in obj.items():
            if isinstance(value, dict):
                walk(prefix + [key], value)
            else:
                out[".".join(prefix + [key])] = value

    walk([], data)
    return out


def comment_values(text: str) -> dict:
    """`# name = value` lines -> dotted key, section aware (T5)."""
    out: dict[str, str] = {}
    section = None
    for line in text.splitlines():
        match = SECTION_RE.match(line)
        if match:
            section = match.group(1).strip()
            continue
        match = COMMENT_ASSIGN_RE.match(line)
        if match:
            name, value = match.group(1), match.group(2)
            out[f"{section}.{name}" if section else name] = value
    return out


def diff_config(base_text: str, cand_text: str):
    blocking, advisory, info, warnings = [], [], [], []
    base = flatten_toml(base_text)
    cand = flatten_toml(cand_text)
    base_comments = comment_values(base_text)
    cand_comments = comment_values(cand_text)
    used = set(USED_DOTTED)
    for name in sorted(used):
        b, c = base.get(name), cand.get(name)
        if b is not None and c is not None:
            if type(b).__name__ != type(c).__name__:
                bl, cl = mask_pair(name, b, c)
                blocking.append(entry("config", name, bl, cl,
                                      f"type changed ({type(b).__name__}->{type(c).__name__})"))
            elif b != c:
                bl, cl = mask_pair(name, b, c)
                blocking.append(entry("config", name, bl, cl, "default changed"))
            continue
        if b is not None:
            bl, _ = mask_pair(name, b, c)
            blocking.append(entry("config", name, bl, "<absent>", "removed"))
            continue
        if c is not None:
            _, cl = mask_pair(name, b, c)
            blocking.append(entry("config", name, "<absent>", cl, "added"))
            continue
        if name in COMMENT_ONLY_USED:
            bc, cc = base_comments.get(name), cand_comments.get(name)
            if bc is not None and cc is None:
                advisory.append(entry("config", name, render_value(bc, raw=True),
                                      "<absent>", "commented form removed"))
            elif bc is None and cc is not None:
                advisory.append(entry("config", name, "<absent>",
                                      render_value(cc, raw=True), "commented form added"))
            elif bc != cc:
                advisory.append(entry("config", name, render_value(bc, raw=True),
                                      render_value(cc, raw=True), "comment value changed"))
            continue
        advisory.append(entry("config", name, "<absent>", "<absent>",
                              "used key absent from both; the guard is inert"))
    for name in sorted(set(base) | set(cand)):
        if name in used:
            continue
        b, c = base.get(name), cand.get(name)
        if b is not None and c is not None and type(b) is type(c) and b == c:
            continue
        rule = "added" if b is None else "removed" if c is None else "changed"
        bl, cl = mask_pair(name, b, c)
        advisory.append(entry("config", name, bl, cl, rule))
    return blocking, advisory, info, warnings


# --------------------------------------------------------------------------- #
# Diff (b) -- VideoParams schema
# --------------------------------------------------------------------------- #
class SchemaWalker:
    """Flatten a JSON schema into comparable descriptors (SEC-0511-2, SEC-0511-3).

    A descriptor is ``{"type", "enum", "default", "constraints", "items", "props",
    "required_names"}``; ``flatten_schema`` adds ``required`` for each top-level
    property.  ``constraints`` carries the numeric/length/pattern/items-count bounds,
    ``items`` the recursed array item descriptor and ``props``/``required_names`` the
    recursed ``$defs`` object properties, so a constraint-only or nested-object change
    is no longer invisible.  A ``$ref`` already on the current stack is a cycle and the
    total visit count is budgeted, so a self-referencing fan-out cannot spin the parent.
    """

    def __init__(self, defs):
        self.defs = defs if isinstance(defs, dict) else {}
        self.visits = 0
        self.stack: list[str] = []

    def _blank(self, type_name: str = "unknown") -> dict:
        return {"type": type_name, "enum": None, "default": None, "constraints": {},
                "items": None, "props": None, "required_names": None, "members": None}

    def describe(self, spec, depth: int = 0) -> dict:
        self.visits += 1
        if self.visits > SCHEMA_NODE_BUDGET:
            raise SchemaError(f"schema exceeds the {SCHEMA_NODE_BUDGET}-node budget")
        if not isinstance(spec, dict) or depth > 8:
            return self._blank()
        if "$ref" in spec:
            name = str(spec["$ref"]).rsplit("/", 1)[-1]
            target = self.defs.get(name)
            if not isinstance(target, dict):
                return self._blank(f"unresolved: {name}")
            if name in self.stack:
                raise SchemaError(f"cyclic $ref: {name}")
            self.stack.append(name)
            try:
                out = self.describe(target, depth + 1)
            finally:
                self.stack.pop()
            if "default" in spec:
                out = dict(out, default=json.dumps(spec["default"]))
            return out
        combinators = [k for k in ("anyOf", "allOf", "oneOf") if isinstance(spec.get(k), list)]
        if combinators:
            members, types, enums = [], [], []
            for key in combinators:
                for member in spec[key]:
                    sub = self.describe(member, depth + 1)
                    members.append(sub)
                    types.append(sub["type"])
                    if sub["enum"]:
                        enums.extend(sub["enum"])
            joined = "|".join(sorted({t for t in types if t}))
            return {"type": joined or "unknown",
                    "enum": sorted({str(e) for e in enums}) or None,
                    "default": json.dumps(spec["default"]) if "default" in spec else None,
                    "constraints": self.constraints(spec), "items": None, "props": None,
                    "required_names": None, "members": tuple(members)}
        enum = None
        if isinstance(spec.get("enum"), list):
            enum = sorted(str(v) for v in spec["enum"])
        raw_type = spec.get("type")
        if isinstance(raw_type, list):
            raw_type = "|".join(sorted(str(t) for t in raw_type))
        if raw_type is None:
            raw_type = "const" if "const" in spec else "unknown"
        out = {"type": TYPE_NAMES.get(str(raw_type), str(raw_type)), "enum": enum,
               "default": json.dumps(spec["default"]) if "default" in spec else None,
               "constraints": self.constraints(spec), "items": None, "props": None,
               "required_names": None, "members": None}
        if str(raw_type) == "array" and isinstance(spec.get("items"), dict):
            out["items"] = self.describe(spec["items"], depth + 1)
        if isinstance(spec.get("properties"), dict):
            out["props"] = {name: self.describe(sub, depth + 1)
                            for name, sub in spec["properties"].items()}
            required = spec.get("required")
            out["required_names"] = (tuple(sorted(str(n) for n in required))
                                     if isinstance(required, list) else ())
        return out

    @staticmethod
    def constraints(spec: dict) -> dict:
        return {key: spec[key] for key in CONSTRAINT_KEYS if key in spec}


def flatten_schema(schema) -> dict:
    if not isinstance(schema, dict) or not isinstance(schema.get("properties"), dict):
        raise SchemaError("schema has no `properties` object")
    walker = SchemaWalker(schema.get("$defs", {}))
    required = schema.get("required")
    required = {str(n) for n in required} if isinstance(required, list) else set()
    out = {}
    for name, spec in schema["properties"].items():
        field = walker.describe(spec)
        field["required"] = name in required
        out[name] = field
    return out


def render_bounds(field) -> str:
    return ",".join(f"{k}={json.dumps(v, sort_keys=True, ensure_ascii=False)}"
                    for k, v in sorted(field["constraints"].items()))


def render_props(field) -> str:
    return "{" + ",".join(f"{name}:{render_item(sub)}"
                          for name, sub in sorted((field.get("props") or {}).items())) + "}"


def render_item(field) -> str:
    """A descriptor's nested shape as one compact token (bounded by the depth cap)."""
    out = field["type"]
    if field["constraints"]:
        out += "(" + render_bounds(field) + ")"
    if field.get("props"):
        out += render_props(field)
    if field.get("required_names"):
        out += "[" + ",".join(field["required_names"]) + "]"
    if field.get("items") is not None:
        out += "[]items=" + render_item(field["items"])
    if field.get("members"):
        out += "(" + "|".join(render_item(m) for m in field["members"]) + ")"
    return out


def render_nested(field) -> str:
    """Only the nested parts of a descriptor: what a shallow print would hide."""
    parts = []
    if field.get("props"):
        parts.append("props=" + render_props(field))
    if field.get("required_names"):
        parts.append("required=" + ",".join(field["required_names"]))
    if field.get("items") is not None:
        parts.append("items=" + render_item(field["items"]))
    if field.get("members"):
        parts.append("members=[" + "|".join(render_nested(m) or "<none>"
                                            for m in field["members"]) + "]")
    return " ".join(parts)


def render_field(field) -> str:
    if field is None:
        return "<absent>"
    out = field["type"]
    if field.get("required"):
        out += " required"
    if field["enum"]:
        out += " enum=" + json.dumps(field["enum"])
    if field["constraints"]:
        out += " " + render_bounds(field)
    if field.get("items") is not None:
        out += " items=" + render_item(field["items"])
    if field.get("props"):
        out += " props=" + render_props(field)
    if field.get("required_names"):
        out += " required=" + ",".join(field["required_names"])
    if field.get("members"):
        nested = [render_nested(member) for member in field["members"]]
        if any(nested):
            out += " nested=members[" + "|".join(part or "<none>" for part in nested) + "]"
    if field["default"] is not None:
        out += " default=" + field["default"]
    return out


def shape_of(field: dict) -> dict:
    """The descriptor minus the two keys with their own rule (default, top-level required)."""
    return {key: value for key, value in field.items()
            if key not in ("default", "required")}


def diff_params(base: dict, cand: dict):
    blocking, advisory, info, warnings = [], [], [], []
    for name in sorted(set(base) | set(cand) | set(D3_FIELDS)):
        b, c = base.get(name), cand.get(name)
        if c is not None and "unresolved:" in str(c["type"]):
            blocking.append(entry("params", name, render_field(b), render_field(c),
                                  "unresolved $ref"))
            continue
        if c is not None and c.get("required") and not (b or {}).get("required"):
            blocking.append(entry("params", name, render_field(b), render_field(c),
                                  "field newly required"))
            continue
        if name in D3_FIELDS:
            if b is None and c is None:
                blocking.append(entry("params", name, "<absent>", "<absent>",
                                      "D3 field missing from both the candidate and the pinned snapshot"))
            elif c is None:
                blocking.append(entry("params", name, render_field(b), "<absent>",
                                      "D3 field removed"))
            elif b is None:
                blocking.append(entry("params", name, "<absent>", render_field(c),
                                      "D3 field absent from the pinned snapshot"))
            elif b["type"] != c["type"]:
                blocking.append(entry("params", name, render_field(b), render_field(c),
                                      f"type changed ({b['type']}->{c['type']})"))
            elif b["enum"] != c["enum"]:
                blocking.append(entry("params", name, render_field(b), render_field(c),
                                      "enum changed"))
            elif shape_of(b) != shape_of(c):
                blocking.append(entry("params", name, render_field(b), render_field(c),
                                      "constraints changed"))
            elif b["default"] != c["default"]:
                advisory.append(entry("params", name, render_field(b), render_field(c),
                                      "default changed (D3 always sends an explicit value)"))
            elif b.get("required") and not c.get("required"):
                advisory.append(entry("params", name, render_field(b), render_field(c),
                                      "no longer required"))
            continue
        if b == c:
            continue
        rule = "added" if b is None else "removed" if c is None else "changed"
        advisory.append(entry("params", name, render_field(b), render_field(c), rule))
    return blocking, advisory, info, warnings


# --------------------------------------------------------------------------- #
# Diff (c) -- routes
# --------------------------------------------------------------------------- #
def normalise_path(path: str) -> str:
    return PATH_PARAM_RE.sub(r"{\1}", str(path))


def normalise_methods(methods) -> tuple:
    return tuple(sorted({str(m).strip().upper() for m in methods}))


def render_methods(methods) -> str:
    return ",".join(methods) if methods else "<absent>"


def normalise_vt(cand_verify_token: dict) -> dict:
    """`"METHOD /path"` -> bool, with the path normalised like the route table."""
    out = {}
    for key, value in (cand_verify_token or {}).items():
        parts = str(key).split(" ", 1)
        if len(parts) != 2:
            out[str(key)] = bool(value)
            continue
        method, path = parts
        out[f"{method.strip().upper()} {normalise_path(path)}"] = bool(value)
    return out


def diff_routes(base_paths: dict, cand_paths: dict, cand_verify_token: dict):
    blocking, advisory, info, warnings = [], [], [], []
    base = {normalise_path(p): normalise_methods(m) for p, m in base_paths.items()}
    cand = {normalise_path(p): normalise_methods(m) for p, m in cand_paths.items()}
    for path in sorted(set(base) | set(cand)):
        b, c = base.get(path), cand.get(path)
        if b == c:
            continue
        rule = "methods changed" if b and c else ("route added" if not b else "route removed")
        item = entry("routes", path, render_methods(b), render_methods(c), rule)
        (blocking if path in BLOCKING_ROUTE_PATHS else advisory).append(item)
    vt = normalise_vt(cand_verify_token)
    for method, path in VERIFY_TOKEN_ROUTES:
        if not vt.get(f"{method} {path}"):
            blocking.append(entry("routes", f"verify_token {method} {path}", "present", "absent",
                                  "verify_token dependency missing (C1)"))
    ping_vt = "present" if vt.get("GET /ping") else "absent"
    info.append(entry("routes", "verify_token GET /ping", "absent", ping_vt,
                      "never present at the pin (C1)"))
    return blocking, advisory, info, warnings


# --------------------------------------------------------------------------- #
# Diff (d) -- progress constants
# --------------------------------------------------------------------------- #
def progress_lines(text: str) -> dict:
    out: dict[int, list[int]] = {}
    for number, line in enumerate(text.splitlines(), start=1):
        for match in PROGRESS_RE.findall(line):
            out.setdefault(int(match), []).append(number)
    return out


def diff_progress(text: str | None):
    blocking, advisory, info, warnings = [], [], [], []
    if text is None:
        warnings.append(entry("progress", "app/services/task.py", "present", "<absent>",
                              "progress constants: source not found"))
        return blocking, advisory, info, warnings
    found = progress_lines(text)
    values = tuple(sorted(found))
    if values != tuple(sorted(PROGRESS_LITERALS)):
        lines = sorted({n for ns in found.values() for n in ns})
        warnings.append(entry("progress", "progress.set",
                              ",".join(str(v) for v in PROGRESS_LITERALS),
                              ",".join(str(v) for v in values),
                              "literal set changed [lines " + ",".join(str(n) for n in lines) + "]"))
    steps = PROGRESS_STEP_RE.findall(text)
    if len(steps) != PROGRESS_STEP_EXPECTED:
        warnings.append(entry("progress", "progress.step",
                              f"{PROGRESS_STEP_EXPECTED} matches",
                              f"{len(steps)} matches",
                              "_progress += 50 / params.video_count / 2"))
    return blocking, advisory, info, warnings


# --------------------------------------------------------------------------- #
# Report
# --------------------------------------------------------------------------- #
def evaluate(*, base_config: str, cand_config: str | None, base_schema: dict,
             cand_schema: dict, base_paths: dict, cand_paths: dict,
             cand_verify_token: dict, cand_task: str | None) -> dict:
    """Pure diff seam: synthetic inputs in, report buckets out (used by --self-test).

    ``cand_config = None`` skips diff (a) (candidate example config missing).
    """
    buckets = {"blocking": [], "advisory": [], "info": [], "warnings": []}
    parts = []
    if cand_config is not None:
        parts.append(diff_config(base_config, cand_config))
    parts.extend((
        diff_params(flatten_schema(base_schema), flatten_schema(cand_schema)),
        diff_routes(base_paths, cand_paths, cand_verify_token),
        diff_progress(cand_task),
    ))
    for part in parts:
        for name, items in zip(("blocking", "advisory", "info", "warnings"), part):
            buckets[name].extend(items)
    return buckets


def exit_code_for(report: dict) -> int:
    if report.get("env"):
        return EXIT_ENV
    return EXIT_BLOCKING if report["blocking"] else EXIT_OK


def render_text(report: dict) -> str:
    """Human report: `## pins`, `## environment`, the four diff blocks, then the counts.

    Each printed severity has its own summary line, so the counts always agree with the
    blocks above them (CRV-0511-5).
    """
    lines = []
    pins = [w for w in report["warnings"] if w["kind"] == "pins"]
    if pins:
        lines.append("## pins")
        lines.extend(text_line("warning", item) for item in pins)
    if report.get("env"):
        lines.append("## environment")
        lines.extend(text_line("env", item) for item in report["env"])
    for block in TEXT_BLOCKS:
        lines.append(f"## {block}")
        selected = []
        for severity, bucket in (("BLOCKING", "blocking"), ("advisory", "advisory"),
                                 ("info", "info"), ("warning", "warnings")):
            selected.extend((severity, item) for item in report[bucket]
                            if item["kind"] == block)
        lines.extend(text_line(severity, item) for severity, item in selected)
    if report["blocking"]:
        lines.append(f"BLOCKING: {len(report['blocking'])} change(s)")
    if report.get("env"):
        lines.append(f"ENVIRONMENT: {len(report['env'])} problem(s)")
    if report["warnings"]:
        lines.append(f"WARNING: {len(report['warnings'])} warning(s)")
    if not report["blocking"] and not report.get("env"):
        lines.append("OK: no blocking change")
    return "\n".join(lines)


def build_report(*, candidate: str | None, mpt_commit: str | None, buckets: dict,
                 baselines: dict, env=None, dirty: bool = False) -> dict:
    report = {
        "schema": "mpt-upgrade-diff.report.v1",
        "candidate": candidate,
        "mpt_commit": mpt_commit,
        "dirty": bool(dirty),
        "blocking": buckets["blocking"],
        "advisory": buckets["advisory"],
        "warnings": buckets["warnings"],
        "info": buckets["info"],
        "env": list(env or []),
        "baselines": baselines,
    }
    report["ok"] = exit_code_for(report) == EXIT_OK
    return report


# --------------------------------------------------------------------------- #
# Candidate extraction
# --------------------------------------------------------------------------- #
def parse_sentinel(stdout: str, what: str):
    """The single sentinel line the child must print (QA-0511-3).

    Zero lines or two or more lines is ambiguous and exit 2: the first or the last is
    never picked, so a candidate cannot append a spoofed payload.
    """
    hits = [line for line in stdout.splitlines() if SENTINEL in line]
    if len(hits) != 1:
        raise EnvError(f"{what} extraction child output ambiguous: {len(hits)} "
                       f"{SENTINEL} line(s), exactly 1 required")
    payload = hits[0].split(SENTINEL, 1)[1]
    try:
        return json.loads(payload)
    except json.JSONDecodeError as exc:
        raise EnvError(f"{what} extraction produced unparsable JSON: "
                       f"{type(exc).__name__}") from None
    except (RecursionError, MemoryError) as exc:
        raise EnvError(f"{what} extraction JSON is too deeply nested: "
                       f"{type(exc).__name__}") from None


def kill_process_group(proc) -> None:
    """SIGKILL the child's whole process group, not just the direct child (SEC-0511-7)."""
    try:
        os.killpg(proc.pid, signal.SIGKILL)
    except OSError:
        try:
            proc.kill()
        except OSError:
            pass


class BoundedReader(threading.Thread):
    """Drain a child pipe, stopping (and killing the group) past the size cap."""

    def __init__(self, proc, stream, cap: int):
        super().__init__(daemon=True)
        self.proc = proc
        self.stream = stream
        self.cap = cap
        self.data = bytearray()
        self.overflow = False

    def run(self) -> None:
        try:
            while True:
                chunk = self.stream.read(1 << 16)
                if not chunk:
                    break
                self.data.extend(chunk)
                if len(self.data) > self.cap:
                    self.overflow = True
                    kill_process_group(self.proc)
                    break
        except (OSError, ValueError):
            pass

    def text(self) -> str:
        return bytes(self.data).decode("utf-8", "replace")


def run_child(candidate: Path, code: str, what: str, *, timeout: float) -> dict:
    """Run the candidate's interpreter once and parse its single sentinel line.

    ``stdin`` is /dev/null, ``HOME`` is a throwaway 0700 directory, the child gets its
    own session/process group (killed on timeout), stdout is capped at
    ``CHILD_OUTPUT_CAP`` and stderr is never echoed: it is written to a 0600 log whose
    path the error names (SEC-0511-3, SEC-0511-4, SEC-0511-7).
    """
    if timeout <= 0:
        raise EnvError(f"{what} extraction skipped: the whole-run deadline expired")
    cmd = [str(candidate / ".venv" / "bin" / "python"), "-c", code]
    with fresh_home() as home:
        try:
            proc = subprocess.Popen(cmd, cwd=str(candidate), env=child_env(home),
                                    stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                    stderr=subprocess.PIPE, start_new_session=True)
        except OSError as exc:
            raise EnvError(f"{what} extraction could not start: "
                           f"{type(exc).__name__}") from None
        out = BoundedReader(proc, proc.stdout, CHILD_OUTPUT_CAP)
        err = BoundedReader(proc, proc.stderr, CHILD_OUTPUT_CAP)
        out.start()
        err.start()
        timed_out = False
        try:
            proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            kill_process_group(proc)
            proc.wait()
        finally:
            out.join(10)
            err.join(10)
            if proc.poll() is None:
                kill_process_group(proc)
                proc.wait()
        stdout, stderr = out.text(), err.text()
    if timed_out:
        raise EnvError(f"{what} extraction timed out after {timeout:g} s")
    if out.overflow or err.overflow:
        raise EnvError(f"{what} extraction output exceeded "
                       f"{CHILD_OUTPUT_CAP} bytes")
    if proc.returncode != 0:
        log = write_stderr_log(stderr.encode("utf-8", "replace"))
        detail = last_exception_class(stderr)
        if log:
            detail += f" [stderr log: {sanitise(log)}]"
        raise EnvError(f"{what} extraction failed (exit {proc.returncode}): {detail}")
    return parse_sentinel(stdout, what)


def git_out(args_list, candidate: Path, env: dict, timeout: float) -> str | None:
    """`git -C <candidate> <args>`; None when git is absent or exits non-zero.

    An exhausted budget or a timeout raises EnvError: the pin binding (T73) must not
    degrade to the no-git-metadata warning just because git was slow.
    """
    if timeout <= 0:
        raise EnvError(f"git {args_list[0]} skipped: the run deadline expired")
    try:
        proc = subprocess.run(["git", "-C", str(candidate), *args_list], env=env,
                              text=True, capture_output=True, timeout=timeout,
                              stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        raise EnvError(f"git {args_list[0]} timed out after {timeout:.0f} s") from None
    except OSError:
        return None
    return proc.stdout if proc.returncode == 0 else None


def candidate_commit(candidate: Path, *, timeout: float) -> tuple[str | None, bool, list]:
    """(HEAD sha or None, dirty, pin warnings) for the candidate checkout.

    No git metadata is a warning (the spec's `"mpt_commit": null`); a dirty worktree is a
    warning plus the report's `dirty` flag, never a failure -- the pin checkout at
    `.nts/src/mpt` is dirty by design (strip-bundled-music deletes tracked files).
    """
    warnings = []
    with fresh_home() as home:
        env = child_env(home)
        head = git_out(["rev-parse", "--verify", "HEAD"], candidate, env, timeout)
        status = git_out(["status", "--porcelain", "--untracked-files=normal"],
                         candidate, env, timeout)
    if head is None:
        warnings.append(entry("pins", "mpt_commit", "<absent>", "null",
                              "candidate has no git metadata; the pin is not proven"))
        return None, False, warnings
    sha = head.strip()
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        warnings.append(entry("pins", "mpt_commit", "<absent>", "null",
                              "candidate git HEAD is not a 40-char sha"))
        return None, False, warnings
    dirty = bool(status and status.strip())
    if dirty:
        warnings.append(entry("pins", "dirty", "clean", "dirty",
                              "candidate worktree has modifications or untracked files "
                              "(the check still runs)"))
    return sha, dirty, warnings


def worktree_snapshot(candidate: Path) -> dict:
    """Which of config.toml / resource / storage already existed before the child runs."""
    return {name: (candidate / name).exists() for name in CLEANUP_ENTRIES}


def cleanup_worktree(candidate: Path, snapshot: dict) -> None:
    """Remove only the entries the child created (PRF-0511-3).

    MPT's own import writes `<candidate>/config.toml` and may create `resource/` and
    `storage/`; a tool that claims to inspect a checkout must leave it as it found it.
    `__pycache__` is deliberately kept, so no PYTHONDONTWRITEBYTECODE.
    """
    for name in CLEANUP_ENTRIES:
        if snapshot.get(name):
            continue
        target = candidate / name
        try:
            if target.is_dir() and not target.is_symlink():
                shutil.rmtree(target)
            elif target.exists() or target.is_symlink():
                target.unlink()
        except OSError:
            pass


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="mpt-upgrade-diff.py",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        description=textwrap.dedent("""\
            MPT upgrade drift check (05.1.1-T1): compare a candidate MPT checkout with the
            pinned config / VideoParams / route baselines.  Exit 0 no blocking change,
            1 at least one blocking change, 2 usage or environment error."""),
        epilog=UPGRADE_STEPS,
    )
    parser.add_argument("--candidate", metavar="DIR",
                        help="MPT checkout at the proposed MPT_COMMIT (needs .venv/bin/python)")
    parser.add_argument("--baseline-config", default=str(DEFAULT_BASELINE_CONFIG),
                        help="pinned config.example.toml copy (default: %(default)s)")
    parser.add_argument("--baseline-params", default=str(DEFAULT_BASELINE_PARAMS),
                        help="pinned VideoParams schema JSON (default: %(default)s)")
    parser.add_argument("--baseline-routes", default=str(DEFAULT_BASELINE_ROUTES),
                        help="pinned routes JSON (default: %(default)s)")
    parser.add_argument("--json", action="store_true",
                        help="emit mpt-upgrade-diff.report.v1 JSON instead of the text report")
    parser.add_argument("--self-test", action="store_true",
                        help="run the synthetic cases and ignore the other arguments")
    return parser.parse_args(argv)


def run(args, *, child_timeout_s: float | None = None,
        total_timeout_s: float | None = None) -> int:
    """Run the check; the two timeout overrides exist for the --self-test seam."""
    if not args.candidate:
        print("mpt-upgrade-diff.py: --candidate DIR is required (or use --self-test)",
              file=sys.stderr)
        return EXIT_ENV
    candidate = Path(args.candidate).expanduser()
    if not candidate.is_dir():
        print(f"mpt-upgrade-diff.py: candidate directory not found: {candidate}", file=sys.stderr)
        print(f"  hint: clone/checkout it at MPT_COMMIT, then cd {candidate} && uv sync",
              file=sys.stderr)
        return EXIT_ENV
    candidate = candidate.resolve()
    interpreter = candidate / ".venv" / "bin" / "python"
    if not interpreter.is_file():
        print(f"mpt-upgrade-diff.py: candidate interpreter missing: {interpreter}", file=sys.stderr)
        print(f"  hint: cd {candidate} && uv sync", file=sys.stderr)
        return EXIT_ENV

    child_cap = CHILD_TIMEOUT_S if child_timeout_s is None else child_timeout_s
    deadline = time.monotonic() + (TOTAL_TIMEOUT_S if total_timeout_s is None
                                  else total_timeout_s)

    def remaining() -> float:
        return deadline - time.monotonic()

    try:
        config_bytes = read_baseline_file(args.baseline_config)
        params_bytes = read_baseline_file(args.baseline_params)
        routes_bytes = read_baseline_file(args.baseline_routes)
    except EnvError as exc:
        print(f"mpt-upgrade-diff.py: {exc}", file=sys.stderr)
        return EXIT_ENV
    baselines = {
        "config": hashlib.sha256(config_bytes).hexdigest(),
        "params": hashlib.sha256(params_bytes).hexdigest(),
        "routes": hashlib.sha256(routes_bytes).hexdigest(),
    }
    try:
        base_config_text = config_bytes.decode("utf-8")
    except UnicodeDecodeError as exc:
        print(unparsable_message("baseline config", args.baseline_config, exc), file=sys.stderr)
        return EXIT_ENV
    try:
        flatten_toml(base_config_text)
    except (tomllib.TOMLDecodeError, RecursionError, MemoryError) as exc:
        print(unparsable_message("baseline config", args.baseline_config, exc), file=sys.stderr)
        return EXIT_ENV
    try:
        base_schema = baseline_schema(json.loads(params_bytes.decode("utf-8")))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, MemoryError) as exc:
        print(unparsable_message("baseline params", args.baseline_params, exc), file=sys.stderr)
        return EXIT_ENV
    except SchemaError as exc:
        print(unparsable_message("baseline params", args.baseline_params, exc), file=sys.stderr)
        return EXIT_ENV
    try:
        base_paths = baseline_paths(json.loads(routes_bytes.decode("utf-8")))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, MemoryError) as exc:
        print(unparsable_message("baseline routes", args.baseline_routes, exc), file=sys.stderr)
        return EXIT_ENV
    except SchemaError as exc:
        print(unparsable_message("baseline routes", args.baseline_routes, exc), file=sys.stderr)
        return EXIT_ENV

    env_problems = []
    config_path = candidate / "config.example.toml"
    cand_config = None
    if config_path.is_file():
        try:
            cand_config = config_path.read_text(encoding="utf-8")
        except UnicodeDecodeError as exc:
            env_problems.append(entry("env", "config.example.toml", "readable", "<absent>",
                                      "not UTF-8: diff (a) skipped"))
            print(unparsable_message("candidate config", config_path, exc), file=sys.stderr)
        except OSError as exc:
            env_problems.append(entry("env", "config.example.toml", "readable", "<absent>",
                                      "unreadable: diff (a) skipped"))
            print(f"mpt-upgrade-diff.py: candidate config unreadable: "
                  f"{sanitise(str(config_path))}: {type(exc).__name__}", file=sys.stderr)
    else:
        env_problems.append(entry("env", "config.example.toml", "present", "<absent>",
                                  "diff (a) skipped: candidate example config missing"))
    if cand_config is not None:
        try:
            flatten_toml(cand_config)
        except (tomllib.TOMLDecodeError, RecursionError, MemoryError) as exc:
            env_problems.append(entry("env", "config.example.toml", "parsable", "<unparsable>",
                                      f"{type(exc).__name__}: diff (a) skipped"))
            print(unparsable_message("candidate config", config_path, exc), file=sys.stderr)
            cand_config = None

    snapshot = worktree_snapshot(candidate)
    try:
        payload = run_child(candidate, CHILD_CODE, "params+routes",
                            timeout=min(remaining(), child_cap))
    except EnvError as exc:
        print(f"mpt-upgrade-diff.py: {sanitise(str(exc))}", file=sys.stderr)
        return EXIT_ENV
    finally:
        cleanup_worktree(candidate, snapshot)

    if not isinstance(payload, dict) or not isinstance(payload.get("paths"), dict) \
            or not isinstance(payload.get("verify_token"), dict) \
            or not isinstance(payload.get("schema"), dict):
        print("mpt-upgrade-diff.py: params+routes extraction produced an unexpected shape",
              file=sys.stderr)
        return EXIT_ENV
    cand_schema, cand_paths, cand_vt = payload["schema"], payload["paths"], payload["verify_token"]

    task_path = candidate / "app" / "services" / "task.py"
    try:
        cand_task = task_path.read_text(encoding="utf-8") if task_path.is_file() else None
    except OSError:
        cand_task = None

    git_timeout = min(remaining(), GIT_TIMEOUT_S)
    try:
        commit, dirty, commit_warnings = candidate_commit(candidate, timeout=git_timeout)
    except EnvError as exc:
        print(f"mpt-upgrade-diff.py: {sanitise(str(exc))}", file=sys.stderr)
        return EXIT_ENV
    pins_commit = read_pin("MPT_COMMIT", DEFAULT_PINS)
    if commit and pins_commit and commit != pins_commit:
        env_problems.append(entry("env", "mpt_commit", pins_commit, commit,
                                  "candidate HEAD differs from MPT_COMMIT in pins.env"))

    try:
        buckets = evaluate(
            base_config=base_config_text,
            cand_config=cand_config,
            base_schema=base_schema,
            cand_schema=cand_schema,
            base_paths=base_paths,
            cand_paths=cand_paths,
            cand_verify_token=cand_vt,
            cand_task=cand_task,
        )
    except (SchemaError, RecursionError, MemoryError) as exc:
        print(unparsable_message("candidate schema", candidate, exc), file=sys.stderr)
        return EXIT_ENV
    buckets["warnings"] = commit_warnings + buckets["warnings"]
    report = build_report(candidate=str(candidate), mpt_commit=commit, buckets=buckets,
                          baselines=baselines, env=env_problems, dirty=dirty)

    print(json.dumps(report, indent=2, sort_keys=False) if args.json else render_text(report))
    return exit_code_for(report)


# --------------------------------------------------------------------------- #
# --self-test
# --------------------------------------------------------------------------- #
SYNTH_CONFIG_VALUES = {
    "log_level": '"INFO"',
    "listen_host": '"127.0.0.1"',
    "listen_port": "8080",
    "app.api_key": '""',
    "app.video_source": '"openai_image"',
    "app.pexels_api_keys": "[]",
    "app.pixabay_api_keys": "[]",
    "app.openai_image_base_url": '"http://127.0.0.1:8091/v1"',
    "app.openai_image_api_keys": "[]",
    "app.openai_image_model": '"gpt-image-2"',
    "app.openai_image_size": '""',
    "app.openai_image_prompt_template": '""',
    "app.material_directory": '""',
    "app.endpoint": '""',
    "app.twelvelabs_rerank_terms": "false",
    "app.llm_provider": '"openai"',
    "app.openai_api_key": '""',
    "app.openai_base_url": '""',
    "app.subtitle_provider": '"whisper"',
    "app.enable_redis": "false",
    "app.max_concurrent_tasks": "4",
    "app.max_queued_tasks": "0",
    "app.upload_post_enabled": "false",
    "app.upload_post_auto_upload": "false",
    "app.upload_post_api_key": '""',
    "whisper.model_size": '"large-v3-turbo"',
    "whisper.device": '"cuda"',
    "whisper.compute_type": '"float16"',
    "kokoro.base_url": '"http://127.0.0.1:8001/v1"',
    "kokoro.api_key": '""',
    "kokoro.model_id": '"vieneu-v3-turbo"',
    "kokoro.voices": "[]",
}
SYNTH_COMMENTS = {
    "app": ["# video_codec = \"libx264\""],
    "whisper": ["# initial_prompt = \"\""],
}


def render_toml(values: dict, comments: dict | None = None) -> str:
    comments = SYNTH_COMMENTS if comments is None else comments
    lines = []
    for section in SECTION_ORDER:
        body = [f"{k.rsplit('.', 1)[-1]} = {v}" for k, v in values.items()
                if (k.rsplit(".", 1)[0] if "." in k else "") == section]
        if section:
            if not body:
                continue
            lines.append(f"[{section}]")
        lines.extend(body)
        lines.extend(comments.get(section, []))
    return "\n".join(lines) + "\n"


def synth_schema(fields: dict) -> dict:
    return {"$defs": {"VideoAspect": {"enum": ["16:9", "9:16", "1:1"], "type": "string"}},
            "properties": fields, "title": "VideoParams", "type": "object"}


def synth_fields(**overrides) -> dict:
    fields = {name: {"type": "string"} for name in D3_FIELDS}
    fields["voice_volume"] = {"anyOf": [{"type": "number"}, {"type": "null"}], "default": 1.0}
    fields.update(overrides)
    return fields


SYNTH_PATHS = {
    "/api/v1/videos": ["POST"],
    "/api/v1/tasks": ["GET"],
    "/api/v1/tasks/{task_id}": ["DELETE", "GET"],
    "/api/v1/download/{file_path}": ["GET"],
    "/api/v1/stream/{file_path}": ["GET"],
    "/ping": ["GET"],
}
SYNTH_VERIFY_TOKEN = {
    "POST /api/v1/videos": True,
    "GET /api/v1/tasks": True,
    "GET /api/v1/tasks/{task_id}": True,
    "DELETE /api/v1/tasks/{task_id}": True,
    "GET /api/v1/download/{file_path}": True,
    "GET /api/v1/stream/{file_path}": True,
    "GET /ping": False,
}
SYNTH_TASK = "\n".join(
    ["_progress = 50"]
    + [f"sm.state.update_task(task_id, progress={v})" for v in PROGRESS_LITERALS]
    + ["_progress += 50 / params.video_count / 2",
       "_progress += 50 / params.video_count / 2"]
) + "\n"


EMPTY_BUCKETS = {"blocking": [], "advisory": [], "info": [], "warnings": []}


@contextlib.contextmanager
def pins_env(value: str | None):
    """Point DEFAULT_PINS at a temp pins.env for the duration of the block (test seam)."""
    if value is None:
        yield
        return
    saved = DEFAULT_PINS
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "pins.env"
        path.write_text(f"MPT_COMMIT={value}\n", encoding="utf-8")
        globals()["DEFAULT_PINS"] = path
        try:
            yield
        finally:
            globals()["DEFAULT_PINS"] = saved


def write_any(path: Path, content) -> None:
    if isinstance(content, bytes):
        path.write_bytes(content)
    else:
        path.write_text(content, encoding="utf-8")


def synth_sentinel_line() -> str:
    return SENTINEL + json.dumps({"schema": synth_schema(synth_fields()),
                                  "paths": SYNTH_PATHS,
                                  "verify_token": SYNTH_VERIFY_TOKEN}, sort_keys=True)


def synth_baselines() -> dict:
    return {
        "config": render_toml(SYNTH_CONFIG_VALUES),
        "params": json.dumps({"model_schema": synth_schema(synth_fields())}),
        "routes": json.dumps({"paths": SYNTH_PATHS}),
    }


def self_test() -> int:
    base_toml = render_toml(SYNTH_CONFIG_VALUES)
    base_schema = synth_schema(synth_fields())
    cases: list[tuple[str, int, int, bool, str]] = []

    def case(name: str, expected: int, *, check=None, base_toml_=None, cand_toml=None,
             base_schema_=None, cand_schema=None, cand_paths=None, cand_vt=None,
             cand_task=SYNTH_TASK, skip_config=False) -> dict:
        try:
            buckets = evaluate(
                base_config=base_toml if base_toml_ is None else base_toml_,
                cand_config=None if skip_config else
                (base_toml if cand_toml is None else cand_toml),
                base_schema=base_schema if base_schema_ is None else base_schema_,
                cand_schema=base_schema if cand_schema is None else cand_schema,
                base_paths=SYNTH_PATHS,
                cand_paths=SYNTH_PATHS if cand_paths is None else cand_paths,
                cand_verify_token=SYNTH_VERIFY_TOKEN if cand_vt is None else cand_vt,
                cand_task=cand_task,
            )
            code = exit_code_for(build_report(candidate="/tmp/synth", mpt_commit=None,
                                              buckets=buckets, baselines={}))
        except (EnvError, SchemaError):
            code = EXIT_ENV
            buckets = dict(EMPTY_BUCKETS)
        detail = "" if check is None else (check(buckets) or "")
        cases.append((name, code, expected, code == expected and not detail, detail))
        return buckets

    def run_case(name: str, expected: int, *, sentinels: int = 1, sleep: int = 0,
                 make_config: bool = True, candidate_config=None, pre_config: bool = False,
                 touch_config: bool = False, git_repo: bool = False, git_commit: bool = True,
                 untracked: bool = False, pins_commit: str | None = None, fail: bool = False,
                 baseline_config=None, baseline_params=None, baseline_routes=None,
                 child_timeout: float | None = None, check=None) -> None:
        """Drive run() end to end against a stub candidate (CRV-0511-6)."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "cand"
            (root / ".venv" / "bin").mkdir(parents=True)
            payload = root / ".venv" / "bin" / "payload.txt"
            if sentinels == 0:
                payload.write_text("no sentinel on this line\n", encoding="utf-8")
            else:
                payload.write_text("\n".join([synth_sentinel_line()] * sentinels) + "\n",
                                   encoding="utf-8")
            body = ["#!/bin/sh"]
            if sleep:
                body.append(f"sleep {sleep}")
            if touch_config:
                body.append(f"touch {shlex.quote(str(root / 'config.toml'))}")
                body.append("mkdir -p " + shlex.quote(str(root / "resource" / "public"))
                            + " " + shlex.quote(str(root / "storage" / "tasks")))
            if fail:
                body.append("echo \"ModuleNotFoundError: No module named 'nope'\" >&2")
                body.append("echo 'api_key=" + "sk-" + "q" * 24 + "' >&2")
                body.append("exit 1")
            else:
                body.append(f"cat {shlex.quote(str(payload))}")
            stub = root / ".venv" / "bin" / "python"
            stub.write_text("\n".join(body) + "\n", encoding="utf-8")
            stub.chmod(0o755)
            if make_config:
                write_any(root / "config.example.toml",
                          render_toml(SYNTH_CONFIG_VALUES) if candidate_config is None
                          else candidate_config)
            if pre_config:
                (root / "config.toml").write_text("# pre-existing\n", encoding="utf-8")
            (root / "app" / "services").mkdir(parents=True, exist_ok=True)
            (root / "app" / "services" / "task.py").write_text(SYNTH_TASK, encoding="utf-8")
            if git_repo:
                git = ["git", "-C", str(root), "-c", "user.email=t@example.invalid",
                       "-c", "user.name=t"]
                subprocess.run(["git", "-C", str(root), "init", "-q"],
                               check=True, capture_output=True)
                subprocess.run(git + ["add", "-A"], check=True, capture_output=True)
                subprocess.run(git + ["commit", "-q", "-m", "stub"],
                               check=True, capture_output=True)
                if not git_commit:
                    with (root / "config.example.toml").open("a", encoding="utf-8") as handle:
                        handle.write("# dirty\n")
                if untracked:
                    (root / "app" / "services" / "untracked_extra.py").write_text(
                        "# untracked\n", encoding="utf-8")
                if pins_commit == "__HEAD__":
                    pins_commit = subprocess.run(
                        ["git", "-C", str(root), "rev-parse", "HEAD"], check=True,
                        capture_output=True, text=True).stdout.strip()
            defaults = synth_baselines()
            overrides = {"config": baseline_config, "params": baseline_params,
                         "routes": baseline_routes}
            paths = {}
            for key, content in overrides.items():
                path = Path(tmp) / f"baseline.{key}.txt"
                write_any(path, defaults[key] if content is None else content)
                paths[key] = path
            argv = ["--candidate", str(root), "--baseline-config", str(paths["config"]),
                    "--baseline-params", str(paths["params"]),
                    "--baseline-routes", str(paths["routes"])]
            nts = Path(tmp) / "nts"
            saved_nts = os.environ.get("NTS_HOME")
            os.environ["NTS_HOME"] = str(nts)
            out, err = io.StringIO(), io.StringIO()
            try:
                with pins_env(pins_commit), contextlib.redirect_stdout(out), \
                        contextlib.redirect_stderr(err):
                    code = run(parse_args(argv), child_timeout_s=child_timeout,
                               total_timeout_s=child_timeout)
            finally:
                if saved_nts is None:
                    os.environ.pop("NTS_HOME", None)
                else:
                    os.environ["NTS_HOME"] = saved_nts
            detail = "" if check is None else (
                check(root, out.getvalue(), err.getvalue(), nts) or "")
            cases.append((name, code, expected, code == expected and not detail, detail))

    # the synthetic baseline covers the whole `used` set (minus the two comment-only keys)
    covered = set(SYNTH_CONFIG_VALUES) | set(COMMENT_ONLY_USED)
    case("used-set-covered-by-self-test", EXIT_OK,
         check=lambda _b: "" if covered == set(USED_DOTTED)
         else f"missing: {sorted(set(USED_DOTTED) - covered)}")

    # identical TOML + JSON
    case("config+params+routes+progress-identical", EXIT_OK,
         check=lambda b: "" if not (b["blocking"] or b["advisory"] or b["warnings"])
         else json.dumps(b))

    # (a) used key removed / added / default changed / type changed -> BLOCKING, kind config
    removed = dict(SYNTH_CONFIG_VALUES)
    del removed["app.max_queued_tasks"]
    case("config-used-key-removed", EXIT_BLOCKING, cand_toml=render_toml(removed),
         check=lambda b: "" if [e["kind"] for e in b["blocking"]] == ["config"] else json.dumps(b))
    case("config-used-key-added-vs-baseline", EXIT_BLOCKING, base_toml_=render_toml(removed),
         check=lambda b: "" if b["blocking"] and "max_queued_tasks" in b["blocking"][0]["key"]
         else json.dumps(b))
    changed = dict(SYNTH_CONFIG_VALUES, **{"app.max_queued_tasks": "5"})
    case("config-used-key-default-changed", EXIT_BLOCKING, cand_toml=render_toml(changed),
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["baseline"] == "0"
         and b["blocking"][0]["candidate"] == "5" else json.dumps(b))
    retyped = dict(SYNTH_CONFIG_VALUES, **{"app.max_queued_tasks": '"0"'})
    case("config-used-key-type-changed", EXIT_BLOCKING, cand_toml=render_toml(retyped),
         check=lambda b: "" if b["blocking"] and "type changed" in b["blocking"][0]["rule"]
         else json.dumps(b))

    # (a) non-used key changed -> advisory only
    extra = dict(SYNTH_CONFIG_VALUES, **{"app.some_new_option": '"x"'})
    case("config-other-key-added", EXIT_OK, cand_toml=render_toml(extra),
         check=lambda b: "" if len(b["advisory"]) == 1
         and b["advisory"][0]["key"] == "app.some_new_option" else json.dumps(b))

    # (a) comment-only used keys (T5)
    case("config-comment-only-key-comment-removed", EXIT_OK,
         cand_toml=render_toml(SYNTH_CONFIG_VALUES, comments={}),
         check=lambda b: "" if [e["key"] for e in b["advisory"]] ==
         ["app.video_codec", "whisper.initial_prompt"] else json.dumps(b))
    promoted = render_toml(dict(SYNTH_CONFIG_VALUES, **{"app.video_codec": '"libx264"'}),
                           comments={"whisper": SYNTH_COMMENTS["whisper"]})
    case("config-comment-only-key-promoted", EXIT_BLOCKING, cand_toml=promoted,
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "app.video_codec"
         else json.dumps(b))

    # (a) a secret value is never printed
    secret = "sk-" + "0" * 30
    valued = dict(SYNTH_CONFIG_VALUES, **{"app.api_key": f'"{secret}"'})
    case("config-secret-masked", EXIT_BLOCKING, cand_toml=render_toml(valued),
         check=lambda b: "" if secret not in json.dumps(b) and "<set>" in json.dumps(b)
         else json.dumps(b))
    other = "sk-" + "1" * 30
    case("config-secret-changed", EXIT_BLOCKING,
         base_toml_=render_toml(valued),
         cand_toml=render_toml(dict(valued, **{"app.api_key": f'"{other}"'})),
         check=lambda b: "" if secret not in json.dumps(b) and other not in json.dumps(b)
         and b["blocking"] and b["blocking"][0]["candidate"] == "<changed>" else json.dumps(b))

    # (b) D3 type / enum / removed / default; non-D3 added; unresolved $ref
    case("params-d3-type-changed", EXIT_BLOCKING, cand_schema=synth_schema(synth_fields(
        voice_volume={"anyOf": [{"type": "string"}, {"type": "null"}], "default": "1.0"})),
        check=lambda b: "" if b["blocking"] and b["blocking"][0]["kind"] == "params"
        and b["blocking"][0]["key"] == "voice_volume" else json.dumps(b))
    dropped = synth_fields()
    del dropped["subtitle_display_mode"]
    case("params-d3-field-removed", EXIT_BLOCKING, cand_schema=synth_schema(dropped),
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "subtitle_display_mode"
         else json.dumps(b))
    both_dropped = synth_fields()
    del both_dropped["font_name"]
    case("params-d3-field-absent-from-both", EXIT_BLOCKING,
         base_schema_=synth_schema(both_dropped), cand_schema=synth_schema(both_dropped),
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "font_name"
         else json.dumps(b))
    case("params-d3-enum-changed", EXIT_BLOCKING, cand_schema=synth_schema(synth_fields(
        subtitle_display_mode={"type": "string", "enum": ["sentence", "word_by_word", "off"],
                               "default": "sentence"})),
        check=lambda b: "" if b["blocking"] and "enum changed" in b["blocking"][0]["rule"]
        else json.dumps(b))
    case("params-non-d3-field-added", EXIT_OK,
         cand_schema=synth_schema(synth_fields(some_new_field={"type": "string"})),
         check=lambda b: "" if len(b["advisory"]) == 1 else json.dumps(b))
    case("params-d3-default-changed", EXIT_OK, cand_schema=synth_schema(synth_fields(
        voice_volume={"anyOf": [{"type": "number"}, {"type": "null"}], "default": 2.0})),
        check=lambda b: "" if b["advisory"] and "default changed" in b["advisory"][0]["rule"]
        else json.dumps(b))
    case("params-unresolved-ref", EXIT_BLOCKING, cand_schema=synth_schema(synth_fields(
        voice_volume={"anyOf": [{"$ref": "#/$defs/Nope"}, {"type": "null"}]})),
        check=lambda b: "" if b["blocking"] and "unresolved" in b["blocking"][0]["rule"]
        else json.dumps(b))
    case("params-unparsable-schema", EXIT_ENV, cand_schema={"title": "VideoParams"})
    case("config-missing-skipped", EXIT_OK, skip_config=True,
         check=lambda b: "" if not b["blocking"] and not b["advisory"] else json.dumps(b))

    # (c) D4 route method changed / removed / verify_token dropped; /stream advisory
    case("routes-d4-method-changed", EXIT_BLOCKING,
         cand_paths=dict(SYNTH_PATHS, **{"/api/v1/tasks/{task_id}": ["POST"]}),
         check=lambda b: "" if b["blocking"]
         and b["blocking"][0]["key"] == "/api/v1/tasks/{task_id}" else json.dumps(b))
    paths = dict(SYNTH_PATHS)
    del paths["/api/v1/videos"]
    case("routes-d4-route-removed", EXIT_BLOCKING, cand_paths=paths,
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "/api/v1/videos"
         else json.dumps(b))
    case("routes-verify-token-dropped", EXIT_BLOCKING,
         cand_vt=dict(SYNTH_VERIFY_TOKEN, **{"DELETE /api/v1/tasks/{task_id}": False}),
         check=lambda b: "" if b["blocking"] and "verify_token" in b["blocking"][0]["key"]
         else json.dumps(b))
    case("routes-verify-token-per-method", EXIT_BLOCKING,
         cand_vt=dict(SYNTH_VERIFY_TOKEN, **{"GET /api/v1/tasks/{task_id}": False}),
         check=lambda b: "" if [e["key"] for e in b["blocking"]]
         == ["verify_token GET /api/v1/tasks/{task_id}"] else json.dumps(b))
    case("routes-stream-method-changed", EXIT_OK,
         cand_paths=dict(SYNTH_PATHS, **{"/api/v1/stream/{file_path}": ["POST"]}),
         check=lambda b: "" if not b["blocking"] and len(b["advisory"]) == 1 else json.dumps(b))
    case("routes-path-param-normalised", EXIT_OK,
         cand_paths={**{k: list(v) for k, v in SYNTH_PATHS.items()},
                     "/api/v1/stream/{file_path:path}": ["POST"]},
         cand_vt={**SYNTH_VERIFY_TOKEN, "POST /api/v1/stream/{file_path:path}": True},
         check=lambda b: "" if not b["blocking"] and len(b["advisory"]) == 1 else json.dumps(b))

    # (d) progress set changed / source missing (warnings, never blocking)
    case("progress-literal-set-changed", EXIT_OK,
         cand_task=SYNTH_TASK.replace("progress=50", "progress=55"),
         check=lambda b: "" if not b["blocking"] and b["warnings"]
         and b["warnings"][0]["key"] == "progress.set" else json.dumps(b))
    case("progress-source-missing", EXIT_OK, cand_task=None,
         check=lambda b: "" if b["warnings"]
         and b["warnings"][0]["key"] == "app/services/task.py" else json.dumps(b))

    # (a) a `used` key that exists in neither file leaves the guard inert (CRV-0511-2)
    absent = {k: v for k, v in SYNTH_CONFIG_VALUES.items() if k != "log_level"}
    case("config-used-key-absent-from-both", EXIT_OK,
         base_toml_=render_toml(absent), cand_toml=render_toml(absent),
         check=lambda b: "" if not b["blocking"]
         and [e["key"] for e in b["advisory"]] == ["log_level"]
         and "inert" in b["advisory"][0]["rule"] else json.dumps(b))

    # (a) secret names beyond api_key/token, and URL userinfo, are masked (SEC-0511-6)
    speech = "sk-" + "q" * 24
    with_speech = base_toml + "\n[azure]\nspeech_key = \"" + speech + "\"\n"
    case("config-secret-speech-key-masked", EXIT_OK, cand_toml=with_speech,
         check=lambda b: "" if speech not in json.dumps(b) and "<set>" in json.dumps(b)
         else json.dumps(b))
    userinfo = "http://user:" + "p" * 12 + "@proxy.invalid:3128"
    with_proxy = render_toml(dict(SYNTH_CONFIG_VALUES, **{"app.proxy_url": f'"{userinfo}"'}))
    case("config-url-userinfo-masked", EXIT_OK, cand_toml=with_proxy,
         check=lambda b: "" if userinfo not in json.dumps(b) and "<set>" in json.dumps(b)
         else json.dumps(b))

    # (b) constraints, `required` and nested `items` objects are compared (SEC-0511-2)
    base_bounded = synth_schema(synth_fields(
        video_count={"type": "integer", "minimum": 1, "default": 1}))
    case("params-d3-constraint-tightened", EXIT_BLOCKING,
         base_schema_=base_bounded,
         cand_schema=synth_schema(synth_fields(
             video_count={"type": "integer", "minimum": 2, "default": 1})),
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "video_count"
         and b["blocking"][0]["rule"] == "constraints changed" else json.dumps(b))
    case("params-d3-field-newly-required", EXIT_BLOCKING,
         cand_schema=dict(synth_schema(synth_fields()), required=["video_script"]),
         check=lambda b: "" if b["blocking"]
         and b["blocking"][0]["rule"] == "field newly required" else json.dumps(b))
    case("params-non-d3-field-newly-required", EXIT_BLOCKING,
         base_schema_=synth_schema(synth_fields(some_extra={"type": "string"})),
         cand_schema=dict(synth_schema(synth_fields(some_extra={"type": "string"})),
                          required=["some_extra"]),
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "some_extra"
         and b["blocking"][0]["rule"] == "field newly required" else json.dumps(b))
    case("params-non-d3-constraint-changed", EXIT_OK,
         base_schema_=synth_schema(synth_fields(some_extra={"type": "string"})),
         cand_schema=synth_schema(synth_fields(
             some_extra={"type": "string", "maxLength": 5})),
         check=lambda b: "" if not b["blocking"] and len(b["advisory"]) == 1
         and b["advisory"][0]["key"] == "some_extra" else json.dumps(b))
    case("params-d3-required-dropped", EXIT_OK,
         base_schema_=dict(synth_schema(synth_fields()), required=["voice_volume"]),
         check=lambda b: "" if not b["blocking"] and b["advisory"]
         and b["advisory"][0]["rule"] == "no longer required" else json.dumps(b))
    nested_defs = {"MaterialInfo": {"properties": {"url": {"type": "string"}},
                                    "required": ["url"], "type": "object"}}
    nested_items = {"items": {"$ref": "#/$defs/MaterialInfo"}, "type": "array"}
    nested_props = synth_fields()
    nested_props["video_materials"] = {"anyOf": [nested_items, {"type": "null"}]}
    nested_base = {"$defs": nested_defs, "properties": nested_props}
    nested_cand = json.loads(json.dumps(nested_base))
    nested_cand["$defs"]["MaterialInfo"]["properties"]["url"] = {"type": "integer"}
    case("params-d3-nested-items-property-changed", EXIT_BLOCKING,
         base_schema_=nested_base, cand_schema=nested_cand,
         check=lambda b: "" if b["blocking"] and b["blocking"][0]["key"] == "video_materials"
         and b["blocking"][0]["rule"] == "constraints changed" else json.dumps(b))

    # (b) a ref cycle and a fan-out past the node budget are exit 2 (SEC-0511-3)
    cyclic = {"$defs": {"A": {"anyOf": [{"$ref": "#/$defs/A"}]}},
              "properties": {"x": {"$ref": "#/$defs/A"}}, "type": "object"}
    case("params-ref-cycle", EXIT_ENV, cand_schema=cyclic)
    fanout = {f"A{i}": {"anyOf": [{"$ref": f"#/$defs/A{i + 1}"} for _ in range(12)]}
              for i in range(8)}
    fanout["A8"] = {"type": "string"}
    case("params-node-budget-exceeded", EXIT_ENV,
         cand_schema={"$defs": fanout, "properties": {"x": {"$ref": "#/$defs/A0"}},
                      "type": "object"})
    try:
        parse_sentinel(SENTINEL + "[" * 20000, "params+routes")
        deep_code = EXIT_OK
    except EnvError:
        deep_code = EXIT_ENV
    cases.append(("sentinel-deep-json", deep_code, EXIT_ENV, deep_code == EXIT_ENV, ""))
    try:
        candidate_commit(Path(tempfile.gettempdir()), timeout=0)
        git_deadline_code = EXIT_OK
    except EnvError:
        git_deadline_code = EXIT_ENV
    cases.append(("git-deadline-expired-fails-closed", git_deadline_code, EXIT_ENV,
                  git_deadline_code == EXIT_ENV, ""))

    # text report: C0/C1 controls in candidate strings are escaped (SEC-0511-5)
    evil = text_line("advisory", entry("config", "app.\u001b[2Kevil\nOK", "<absent>", "1", "added"))
    cases.append(("text-line-escapes-controls",
                  EXIT_OK if "\x1b" not in evil and "\\x1b" in evil and "\n" not in evil
                  else EXIT_BLOCKING, EXIT_OK,
                  "\x1b" not in evil and "\\x1b" in evil and "\n" not in evil, evil))

    # run() end to end against a stub candidate (CRV-0511-6)
    run_case("run-stub-clean-exit0-and-cleanup", EXIT_OK, touch_config=True,
             check=lambda root, out, err, nts: "" if "OK: no blocking change" in out
             and not (root / "config.toml").exists()
             and not (root / "resource").exists() and not (root / "storage").exists()
             else f"out={out!r} err={err!r}")
    run_case("run-stub-preexisting-config-kept", EXIT_OK, pre_config=True, touch_config=True,
             check=lambda root, out, err, nts: "" if "OK: no blocking change" in out
             and (root / "config.toml").exists() else f"out={out!r} err={err!r}")
    run_case("run-stub-missing-candidate-config", EXIT_ENV, make_config=False,
             check=lambda root, out, err, nts: "" if "ENVIRONMENT: 1 problem(s)" in out
             and "BLOCKING:" not in out and "## config" in out
             else f"out={out!r} err={err!r}")
    run_case("run-stub-zero-sentinels", EXIT_ENV, sentinels=0,
             check=lambda root, out, err, nts: "" if "ambiguous" in err
             and "0 " in err else f"out={out!r} err={err!r}")
    run_case("run-stub-two-sentinels", EXIT_ENV, sentinels=2,
             check=lambda root, out, err, nts: "" if "ambiguous" in err
             and "2 " in err else f"out={out!r} err={err!r}")
    run_case("run-stub-timeout", EXIT_ENV, sleep=5, touch_config=True, child_timeout=0.4,
             check=lambda root, out, err, nts: "" if "timed out" in err
             and not (root / "config.toml").exists() else f"out={out!r} err={err!r}")
    run_case("run-stub-head-mismatch", EXIT_ENV, git_repo=True,
             pins_commit="0" * 40,
             check=lambda root, out, err, nts: "" if "ENVIRONMENT: 1 problem(s)" in out
             and "mpt_commit" in out else f"out={out!r} err={err!r}")
    run_case("run-stub-dirty-is-a-warning", EXIT_OK, git_repo=True, git_commit=False,
             pins_commit="__HEAD__",
             check=lambda root, out, err, nts: "" if "OK: no blocking change" in out
             and "## pins" in out and "WARNING: 1 warning(s)" in out
             else f"out={out!r} err={err!r}")
    run_case("run-stub-untracked-is-dirty", EXIT_OK, git_repo=True, untracked=True,
             pins_commit="__HEAD__",
             check=lambda root, out, err, nts: "" if "OK: no blocking change" in out
             and "untracked files" in out and "WARNING: 1 warning(s)" in out
             else f"out={out!r} err={err!r}")
    run_case("run-stub-baseline-config-unparsable", EXIT_ENV,
             baseline_config="[app\n",
             check=lambda root, out, err, nts: "" if "baseline config unparsable" in err
             and "TOMLDecodeError" in err and "Traceback" not in err else f"err={err!r}")
    run_case("run-stub-baseline-config-non-utf8", EXIT_ENV,
             baseline_config=b"max_queued_tasks = 0\n# \xff\xfe\xfa\n",
             check=lambda root, out, err, nts: "" if "baseline config unparsable" in err
             and "UnicodeDecodeError" in err else f"err={err!r}")
    run_case("run-stub-baseline-routes-wrong-shape", EXIT_ENV,
             baseline_routes=json.dumps({"paths": []}),
             check=lambda root, out, err, nts: "" if "baseline routes unparsable" in err
             and "SchemaError" in err else f"err={err!r}")
    run_case("run-stub-baseline-params-wrong-shape", EXIT_ENV,
             baseline_params=json.dumps({"model_schema": []}),
             check=lambda root, out, err, nts: "" if "baseline params unparsable" in err
             and "SchemaError" in err else f"err={err!r}")
    secret = "sk-" + "q" * 24
    run_case("run-stub-child-stderr-never-echoed", EXIT_ENV, fail=True,
             check=lambda root, out, err, nts: "" if secret not in out and secret not in err
             and "ModuleNotFoundError" in err and "exit 1" in err
             and "stderr log" in err
             and [p for p in (nts / "logs").glob("*.stderr.log")
                  if p.stat().st_size and (p.stat().st_mode & 0o777) == 0o600]
             else f"out={out!r} err={err!r}")
    run_case("run-stub-candidate-config-unparsable", EXIT_ENV, candidate_config="[app\n",
             check=lambda root, out, err, nts: "" if "candidate config unparsable" in err
             and "diff (a) skipped" in out else f"out={out!r} err={err!r}")
    run_case("run-stub-candidate-config-non-utf8", EXIT_ENV,
             candidate_config=b"max_queued_tasks = 0\n# \xff\xfe\xfa\n",
             check=lambda root, out, err, nts: "" if "candidate config unparsable" in err
             and "UnicodeDecodeError" in err and "not UTF-8" in out
             else f"out={out!r} err={err!r}")

    # environment: an unreadable baseline is exit 2
    with tempfile.TemporaryDirectory() as tmp:
        try:
            read_baseline_file(Path(tmp) / "nope.toml")
            baseline_code = EXIT_OK
        except EnvError:
            baseline_code = EXIT_ENV
    cases.append(("baseline-missing", baseline_code, EXIT_ENV, baseline_code == EXIT_ENV, ""))

    # --json round-trip
    parsed = json.loads(json.dumps(build_report(
        candidate="/tmp/synth", mpt_commit=None, buckets=dict(EMPTY_BUCKETS),
        baselines={"config": "0" * 64, "params": "0" * 64, "routes": "0" * 64})))
    rt_ok = (parsed["schema"] == "mpt-upgrade-diff.report.v1" and parsed["blocking"] == []
             and parsed["ok"] is True)
    cases.append(("json-round-trip", EXIT_OK if rt_ok else EXIT_BLOCKING, EXIT_OK, rt_ok,
                  json.dumps(parsed)))

    failures = 0
    for name, actual, expected, ok, detail in cases:
        failures += 0 if ok else 1
        print(f"{name}: {actual} expected {expected} {'OK' if ok else 'FAIL'}")
        if not ok and detail:
            print(f"    detail: {detail}")
    return EXIT_OK if failures == 0 else EXIT_BLOCKING


def main(argv=None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    if args.self_test:
        return self_test()
    try:
        return run(args)
    except EnvError as exc:
        print(f"mpt-upgrade-diff.py: {exc}", file=sys.stderr)
        return EXIT_ENV


if __name__ == "__main__":
    sys.exit(main())
