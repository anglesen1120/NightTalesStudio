#!/usr/bin/env python3
"""mpt_client.py — the executable reference client for the pinned MoneyPrinterTurbo API
(05.4.1-T1 design D13; the D3 submit body and the D4/D5 state mapping are implemented
once here and reused by the render-node adapter).

It submits a run spec to MPT, polls `GET /api/v1/tasks/{id}`, maps MPT's state to the
`render-status.v1` shape and downloads the four files the render check reads
(`final-1.mp4`, `subtitle.srt`, `audio.mp3`, `script.json`). It is NOT a production
client (09.6.2-T1 is) and it never prints the API key nor writes script/term text
outside `$NTS_HOME/runs/`.

Run:  uv run --no-project --python 3.12 trial/tools/mpt_client.py <cmd> [flags]
Exit codes (01 D9): 0 ok · 1 render failure / lost task / HTTP error · 2 usage or
environment error.

Secrets: the key is read from `NTS_MPT_API_KEY` (never argv, never a file), e.g.
  source worker-node/bin/lib.sh && NTS_MPT_API_KEY="$(secret nts.mpt.api_key)" \
      uv run --no-project --python 3.12 trial/tools/mpt_client.py poll …

Transport (SEC-0541-1/2): one module-level opener, `build_opener(ProxyHandler({}),
NoRedirect)` — `HTTP_PROXY`/`ALL_PROXY` from the environment can never route the
`x-api-key` header (or the script body) through a proxy, and a 3xx is refused
(exit 1) instead of re-sending the key to the `Location` host (T95/SEC-02D-1).
The key travels as an *unredirected* header — and so does `x-task-id` (T57), on
submit, poll and download. `--base-url` must be http(s) on a
loopback host unless `--allow-remote` is given (exit 2 otherwise), and `--task-id`
must be a UUID (exit 2 for argv, exit 1 for a server-supplied id).

Commands
  submit   --spec FILE [--run-dir DIR]   build the D3 body, POST /api/v1/videos
  poll     --task-id ID [--interval 5] [--timeout 3600] [--trace FILE]
  download --task-id ID --dest DIR [--download-timeout 300]
  run      --spec FILE --run-dir DIR [--download-timeout 300]    submit → poll → download → [render_check.py]
  --self-test                            in-process checks + loopback stub checks

Run spec (`trial/fixtures/spike-render-spec.json`):
  { run_id, story_file, terms_file, aspect, language, voice_id, preset_file|null,
    music_track|null }
`video_subject` = the story file's first non-empty line (<= 200 chars); `video_script`
= the rest of the file, NFC (the whole file when it is a single line).

SEC-03: `story_file` / `terms_file` / `preset_file` are resolved with
`os.path.realpath` and must lie inside an approved root — the repo, or `$NTS_HOME`
(default `<repo>/.nts`); anything else is a usage error (exit 2). SEC-04: the
catalog `track["file"]` must be a plain `.mp3` basename before it becomes
`bgm_file`.
"""

import argparse
import hashlib
import http.client
import ipaddress
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

DEFAULT_BASE_URL = "http://127.0.0.1:8080"
KEY_ENV = "NTS_MPT_API_KEY"
SUBMIT_PATH = "/api/v1/videos"
TASK_PATH = "/api/v1/tasks/{task_id}"
DOWNLOAD_PATH = "/api/v1/download/{task_id}/{name}"
DOWNLOAD_FILES = ("final-1.mp4", "subtitle.srt", "audio.mp3", "script.json")
# The files render_check.py reads (D9). Only subtitle.srt may 404: the SRT is
# absent whenever subtitles are off; a missing mp4/audio/script is an output
# failure (QA-0541-10).
DOWNLOAD_REQUIRED = ("final-1.mp4", "audio.mp3", "script.json")
DEFAULT_INTERVAL = 5
DEFAULT_TIMEOUT = 3600          # = Render:MaxMinutes (D15 P7)
DOWNLOAD_TIMEOUT = 300          # F7: plan 05.4.1 plan.md:137 — one download's read budget
RENDER_CHECK_TIMEOUT = 900      # 05 T78: the adapter's CheckTimeoutSeconds (11.3.5-T3)
SUBJECT_MAX = 200
MAX_BODY_BYTES = 2 << 20        # D15 S2 cap, mirrored from the adapter
MAX_DOWNLOAD_BYTES = 512 << 20  # hard ceiling on one downloaded artefact
DOWNLOAD_CHUNK = 64 << 10       # stream chunk: peak RSS stays ~flat on big files

# D10-style safe ids, the same shape story_harness.py uses (SAFE_ID_RE); also the
# only class of value allowed into request headers / paths (QA-0541-1, QA-0541-12).
SAFE_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
# Server-side task ids are UUIDs (SEC-0541-3); MPT's normalize_task_id emits
# uuid4() strings, so the canonical dashed form is enforced.
UUID_RE = re.compile(
    r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
)
# SEC-04: the catalog `file` becomes the D3 `bgm_file`, so it must be a plain
# basename of the `seed/music-tracks.v1.json` shape — no separator, no `..`, no
# scheme. A path there would make the render node read an arbitrary file.
BGM_FILE_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*\.mp3$")

# The exact VideoParams key set the adapter sends (D3). `n_threads` is deliberately
# absent: it is an adapter/node setting (F22, D15 P6), never a job parameter.
D3_KEYS = (
    "video_subject",
    "video_script",
    "video_terms",
    "video_aspect",
    "video_fit_mode",
    "video_concat_mode",
    "video_transition_mode",
    "video_clip_duration",
    "video_clip_speed",
    "match_materials_to_script",
    "video_count",
    "video_source",
    "video_materials",
    "custom_audio_file",
    "video_language",
    "voice_name",
    "voice_volume",
    "voice_rate",
    "bgm_type",
    "bgm_file",
    "bgm_volume",
    "video_music_prompt",
    "sonilo_bgm_prompt",
    "subtitle_enabled",
    "subtitle_display_mode",
    "subtitle_animation",
    "subtitle_position",
    "custom_position",
    "font_size",
    "stroke_color",
    "stroke_width",
    "text_fore_color",
    "text_background_color",
    "rounded_subtitle_background",
    "font_name",
    "paragraph_number",
    "video_script_prompt",
    "custom_system_prompt",
)

# D6 defaults, inline while `seed/subtitle-presets.v1.json` (05.2.3-T1) does not exist.
D6_PRESETS = {
    "landscape": {
        "fontSize": 84,
        "strokeWidth": 3.0,
        "strokeColor": "#000000",
        "textForeColor": "#FFFFFF",
        "textBackgroundColor": False,
        "roundedSubtitleBackground": False,
        "subtitlePosition": "bottom",
        "customPosition": 70.0,
    },
    "portrait": {
        "fontSize": 64,
        "strokeWidth": 3.0,
        "strokeColor": "#000000",
        "textForeColor": "#FFFFFF",
        "textBackgroundColor": False,
        "roundedSubtitleBackground": False,
        "subtitlePosition": "custom",
        "customPosition": 62.0,
    },
}
D6_FONT_NAME = "BeVietnamPro-Bold.ttf"
# camelCase preset key -> MPT snake_case field (D6/D3).
PRESET_FIELD_MAP = {
    "fontSize": "font_size",
    "strokeWidth": "stroke_width",
    "strokeColor": "stroke_color",
    "textForeColor": "text_fore_color",
    "textBackgroundColor": "text_background_color",
    "roundedSubtitleBackground": "rounded_subtitle_background",
    "subtitlePosition": "subtitle_position",
    "customPosition": "custom_position",
}
ASPECT_PRESET = {"16:9": "landscape", "9:16": "portrait"}

# D4/D5 mapping. MPT `state`: -1 failed · 1 complete · 4 processing.
FAILED_STAGE_OUTCOME = {
    "audio": ("voice", "tts_failed"),
    "materials": ("images", "image_generation_failed"),
    "video": ("assembling", "render_assembly_failed"),
}
# D5: failed_stage ∈ {preflight, script, terms, pipeline} — and any stage MPT adds
# later — folds into render_node_error via the else branch in map_task (CRV-0541-4);
# detail keeps the observed stage string.
STATE_QUEUED, STATE_RUNNING, STATE_SUCCEEDED, STATE_FAILED = "queued", "running", "succeeded", "failed"
STAGES = ("queued", "voice", "subtitles", "images", "assembling", "checking", "done")


def eprint(*a):
    print(*a, file=sys.stderr)


def now_iso():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def nts_home(repo):
    """$NTS_HOME, defaulting to <repo>/.nts (01 CR-11); never inside the tracked tree."""
    env = os.environ.get("NTS_HOME")
    if env:
        return os.path.abspath(env)
    return os.path.join(repo, ".nts")


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def approved_roots(repo):
    """SEC-03 approved input roots (repo, $NTS_HOME) as (path, st_dev, st_ino);
    resolved once per submit. A root that does not exist, or is the filesystem
    root (`NTS_HOME=/` would approve every host file), is skipped."""
    roots = []
    for root in (os.path.realpath(repo), os.path.realpath(nts_home(repo))):
        if os.path.dirname(root) == root:
            continue
        try:
            st = os.stat(root)
        except OSError:
            continue
        roots.append((root, st.st_dev, st.st_ino))
    return roots


def resolve_input_path(path, repo, label, roots=None):
    """SEC-03: a spec-supplied path (story, terms, preset) is resolved with
    `os.path.realpath` and must lie inside an approved root — the repo, or
    `$NTS_HOME` (default `<repo>/.nts`, 01 CR-11, as worker-node/bin/lib.sh).

    The run spec is data: an absolute or `..`-laden entry must never reach an
    arbitrary host file (its text would be read, and `submit-body.json` would
    carry it). Outside both roots is a usage error, not a fixture miss.
    Containment is by (st_dev, st_ino) identity, as `_is_within`: APFS is
    case-insensitive and realpath keeps the given case.
    """
    if not isinstance(path, str) or not path:
        raise ValueError(f"{label} must be a non-empty path string, got {path!r}")
    if roots is None:
        roots = approved_roots(repo)
    resolved = os.path.realpath(path if os.path.isabs(path) else os.path.join(repo, path))
    ids = {(dev, ino) for _root, dev, ino in roots}
    cur = resolved
    while True:
        try:
            st = os.stat(cur)
            if (st.st_dev, st.st_ino) in ids:
                return resolved
        except OSError:
            pass
        parent = os.path.dirname(cur)
        if parent == cur:
            break
        cur = parent
    raise ValueError(
        f"{label} {path!r} resolves to {resolved!r}, outside the approved roots "
        f"(the repo and $NTS_HOME: {', '.join(repr(r[0]) for r in roots)})"
    )


# --------------------------------------------------------------------------- mapping


def stage_for_progress(progress):
    """C7 progress -> D4 stage. `progress` is MPT's integer progress."""
    try:
        p = int(progress)
    except (TypeError, ValueError):
        return "queued"
    if p <= 0:
        return "queued"
    if p < 30:
        return "voice"
    if p < 40:
        return "subtitles"
    if p < 50:
        return "images"
    if p < 100:
        return "assembling"
    return "assembling"


def map_task(task, http_status=200):
    """Map one MPT task JSON (the `data` object) to (state, stage, outcome, percent).

    `http_status` 404 for a task the client submitted is the lost-task rule (F06):
    MPT keeps video task state in memory (C9), so a restart erases it.
    """
    if http_status == 404:
        return STATE_FAILED, None, {
            "code": "render_lost",
            "detail": "mpt_task_not_found",
            "message": "The render node lost the MPT task it submitted.",
        }, None
    state = task.get("state")
    progress = task.get("progress")
    if state == 4:
        return (STATE_QUEUED if not progress else STATE_RUNNING,
                stage_for_progress(progress), None, progress)
    if state == 1:
        return STATE_SUCCEEDED, "done", None, progress
    failed_stage = task.get("failed_stage")
    if failed_stage in FAILED_STAGE_OUTCOME:
        stage, code = FAILED_STAGE_OUTCOME[failed_stage]
    else:
        stage, code = stage_for_progress(progress), "render_node_error"
    return STATE_FAILED, stage, {
        "code": code,
        "detail": failed_stage if failed_stage is not None else "unknown",
        "message": "MPT reported the task failed.",
    }, progress


def render_status(task_id, run_id, state, stage, outcome, percent, mpt, **extra):
    """The `render-status.v1` shape (D4), never carrying node paths or content."""
    status = {
        "schema": "render-status.v1",
        "task_id": task_id,
        "render_job_id": run_id,
        "state": state,
        "stage": stage,
        "percent": percent,
        "cancel_requested": False,
        "outcome": outcome,
        "output": None,
        "check": None,
        "images": None,
        "mpt": mpt,
    }
    status.update(extra)
    return status


# ------------------------------------------------------------------------ body build


def read_terms(path):
    """Accept the spike fixture shape (`terms`: [{"prompt": …}] or [str]) and a bare array."""
    doc = json.loads(open(path, encoding="utf-8").read())
    items = doc["terms"] if isinstance(doc, dict) else doc
    terms = []
    for item in items:
        if isinstance(item, str):
            terms.append(item)
        else:
            terms.append(item.get("prompt") or item.get("keyword") or "")
    terms = [t for t in terms if t]
    if not terms:
        raise ValueError(f"{path}: no terms")
    return terms


def load_preset_file(path, aspect):
    """Read `subtitle-presets.v1` when 05.2.3-T1 has landed, else the D6 defaults."""
    if path and os.path.exists(path):
        doc = json.loads(open(path, encoding="utf-8").read())
        preset = dict(doc["presets"][ASPECT_PRESET[aspect]])
        preset.setdefault("fontName", doc.get("fontName", D6_FONT_NAME))
        return preset, "preset-file"
    return dict(D6_PRESETS[ASPECT_PRESET[aspect]]), "d6-inline"


def load_music_track(track_id, repo):
    """Look a catalog id up in `seed/music-tracks.v1.json` (D8); a miss is a usage error."""
    if not track_id:
        return None
    catalog = os.path.join(repo, "seed", "music-tracks.v1.json")
    if not os.path.exists(catalog):
        raise ValueError(f"music_track is set but {catalog} does not exist")
    doc = json.loads(open(catalog, encoding="utf-8").read())
    for track in doc.get("tracks", []):
        if track.get("id") == track_id:
            return track
    raise ValueError(f"music_track {track_id!r} not in {catalog}")


def bgm_file_name(track):
    """SEC-04: validate the catalog track's `file` before it becomes `bgm_file`."""
    name = track.get("file")
    if (not isinstance(name, str) or ".." in name or "/" in name or "\\" in name
            or not BGM_FILE_RE.fullmatch(name)):
        raise ValueError(
            f"music_track {track.get('id')!r} has an unsafe file {name!r}; a plain "
            f"basename matching {BGM_FILE_RE.pattern} is required (no path "
            "separators, no '..', no scheme)"
        )
    return name


def build_body(spec, repo, preset_file=None, music_track_id=None):
    """Build the D3 `VideoParams` body from a run spec. Pure; no I/O beyond the
    fixtures. SEC-03: story_file/terms_file/preset_file must resolve inside the
    repo or $NTS_HOME (`resolve_input_path`); SEC-04: the catalog `file` must be
    a plain basename (`bgm_file_name`)."""
    roots = approved_roots(repo)
    story_path = resolve_input_path(spec["story_file"], repo, "story_file", roots)
    story = unicodedata.normalize("NFC", open(story_path, encoding="utf-8").read())
    # CRV-0541-3: subject and script come from the same first-non-empty index, so
    # leading blank lines never leak the subject line into the narration script.
    raw_lines = story.splitlines()
    first = next((i for i, ln in enumerate(raw_lines) if ln.strip()), None)
    if first is None:
        raise ValueError(f"{story_path}: empty story fixture")
    subject = raw_lines[first].strip()[:SUBJECT_MAX]
    body_lines = raw_lines[first + 1:]
    script = unicodedata.normalize("NFC", "\n".join(body_lines).strip()) or subject
    terms_path = resolve_input_path(spec["terms_file"], repo, "terms_file", roots)
    terms = read_terms(terms_path)
    aspect = spec["aspect"]
    if aspect not in ASPECT_PRESET:
        raise ValueError(f"aspect must be 16:9 or 9:16, got {aspect!r}")
    preset_path = preset_file or spec.get("preset_file")
    if preset_path:
        preset_path = resolve_input_path(preset_path, repo, "preset_file", roots)
    preset, _source = load_preset_file(preset_path, aspect)
    track = load_music_track(music_track_id if music_track_id is not None else spec.get("music_track"), repo)

    body = {
        "video_subject": subject,
        "video_script": script,
        "video_terms": terms,
        "video_aspect": aspect,
        "video_fit_mode": "cover",
        "video_concat_mode": "sequential",
        "video_transition_mode": None,
        "video_clip_duration": 10,
        "video_clip_speed": 1.0,
        "match_materials_to_script": True,
        "video_count": 1,
        "video_source": "openai_image",
        "video_materials": None,
        "custom_audio_file": None,
        "video_language": spec["language"],
        "voice_name": "kokoro:" + spec["voice_id"],
        "voice_volume": 1.0,
        "voice_rate": 1.0,
        "bgm_type": "",
        "bgm_file": "",
        "bgm_volume": 0,
        "video_music_prompt": "",
        "sonilo_bgm_prompt": "",
        "subtitle_enabled": True,
        "subtitle_display_mode": "sentence",
        "subtitle_animation": "none",
        "paragraph_number": 1,
        "video_script_prompt": "",
        "custom_system_prompt": "",
        "font_name": preset.get("fontName", D6_FONT_NAME),
    }
    for camel, value in preset.items():
        field = PRESET_FIELD_MAP.get(camel)
        if field:
            body[field] = value
    if track:
        body["bgm_type"] = "custom"
        body["bgm_file"] = bgm_file_name(track)
        body["bgm_volume"] = float(track["bgmVolume"])
    missing = [k for k in D3_KEYS if k not in body]
    extra = [k for k in body if k not in D3_KEYS]
    if missing or extra:
        raise ValueError(f"body does not match D3: missing={missing} extra={extra}")
    return body


# ----------------------------------------------------------------------------- http


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """T95/SEC-02D-1/SEC-0541-1: never follow 3xx — the `Location` host would get
    the request (and, for regular headers, the key). A redirect answer from MPT
    is a provider/HTTP error, not a hop."""

    def redirect_request(self, *args, **kwargs):  # noqa: D102 - urllib hook
        return None


# SEC-0541-2: one module-level opener with an empty ProxyHandler (the audition.py
# pattern) so HTTP_PROXY/ALL_PROXY/NO_PROXY env vars can never route the key or
# the story body through a proxy; `urlopen` (which reads *_PROXY) is never used.
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect)

# Set from --allow-remote in main(); also honourable directly in-process.
_ALLOW_REMOTE = False


def check_base_url(base_url, allow_remote=None):
    """SEC-0541-3: http(s) only, and loopback-only unless --allow-remote; exit 2."""
    allow = _ALLOW_REMOTE if allow_remote is None else allow_remote
    try:
        parsed = urllib.parse.urlsplit(base_url)
    except ValueError as exc:
        eprint(f"mpt_client.py: unusable --base-url {base_url!r}: {exc}")
        sys.exit(2)
    if parsed.scheme not in ("http", "https"):
        eprint(f"mpt_client.py: --base-url must be http or https, got {base_url!r}")
        sys.exit(2)
    host = parsed.hostname or ""
    if allow:
        return
    try:
        loopback = ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = host.lower() == "localhost"
    if not loopback:
        eprint(
            f"mpt_client.py: --base-url {base_url!r} is not loopback; the MPT key "
            f"would leave this host. Pass --allow-remote to override."
        )
        sys.exit(2)


def check_task_id(task_id, source):
    """SEC-0541-3: task ids are UUIDs. argv (source="argv") -> exit 2; a bad id
    coming *back from the server* (source="server") is an HTTP/contract failure
    -> exit 1."""
    if not UUID_RE.match(task_id or ""):
        eprint(f"mpt_client.py: task_id {task_id!r} from {source} is not a UUID")
        sys.exit(2 if source == "argv" else 1)


def check_safe_id(label, value):
    """QA-0541-1/12: ids that travel into headers and file paths must match the
    harness safe-id shape — CRLF, slashes and other control bytes exit 2 here,
    before http.client can ever see them."""
    if not isinstance(value, str) or not SAFE_ID_RE.match(value):
        eprint(f"mpt_client.py: {label} {value!r} is not a safe id ({SAFE_ID_RE.pattern})")
        sys.exit(2)


def _is_within(path, ancestor):
    """SEC-0523F-1: identity containment: True if `path` or an existing parent is
    `ancestor`. String prefixes are not enough — APFS is case-insensitive and
    os.path.realpath keeps the given case, so a case-swapped repo path walks past
    every startswith check. Walk up to "/" comparing (st_dev, st_ino); a path that
    does not exist yet starts from its nearest existing ancestor (usage_tap.py
    _under_dir precedent)."""
    try:
        a = os.stat(ancestor)
    except OSError:
        return False
    cur = os.path.realpath(path)
    while True:
        try:
            st = os.stat(cur)
            if (st.st_dev, st.st_ino) == (a.st_dev, a.st_ino):
                return True
        except OSError:
            pass
        parent = os.path.dirname(cur)
        if parent == cur:
            return False
        cur = parent


def run_dir_for(repo, run_id, run_dir=None):
    """QA-0541-1: the resolved run dir must stay under the runs root. With an
    explicit --run-dir the root IS the dir; otherwise it is $NTS_HOME/runs.

    F11: the default root lives under $NTS_HOME (itself `<repo>/.nts` by default,
    gitignored, 01 CR-11). An *explicit* --run-dir may not point into the repo at
    all: `submit` writes submit-body.json (= the narration) there, so
    `submit --run-dir .` would drop the script into the tracked tree. Refused
    before any write, exit 2.

    SEC-0523F-1: the repo / <repo>/.nts questions are decided by identity
    (_is_within), never by string prefixes.
    """
    check_safe_id("run_id", run_id)
    repo_real = os.path.realpath(repo)
    if run_dir:
        root = os.path.realpath(run_dir)
        target = root
        if _is_within(target, repo_real):
            eprint(
                f"mpt_client.py: --run-dir {target} is inside the repo ({repo_real}); "
                "the submit body holds the script and is never written into the tracked "
                "tree. Pass a --run-dir outside the repo or omit it ($NTS_HOME/runs)."
            )
            sys.exit(2)
    else:
        root = os.path.realpath(os.path.join(nts_home(repo), "runs"))
        target = os.path.realpath(os.path.join(root, run_id))
        # SEC-0523-4: an NTS_HOME inside the repo is allowed only under the gitignored <repo>/.nts.
        # SEC-0523F-1: identity finds <repo>/.nts even when the volume is case-insensitive.
        nts_real = os.path.join(repo_real, ".nts")
        under_nts = _is_within(root, nts_real) or root.startswith(nts_real + os.sep)
        if _is_within(root, repo_real) and not under_nts:
            eprint(f"mpt_client.py: runs root {root} is inside the repo but not under {nts_real}; "
                   "set NTS_HOME outside the tracked tree or leave it unset.")
            sys.exit(2)
    if not (target == root or target.startswith(root + os.sep) or _is_within(target, root)):
        eprint(f"mpt_client.py: run dir {target} escapes runs root {root} (run_id {run_id!r})")
        sys.exit(2)
    return target


def _key():
    key = os.environ.get(KEY_ENV, "")
    if not key:
        eprint(
            f"mpt_client.py: {KEY_ENV} is not set; get it with "
            'source worker-node/bin/lib.sh && '
            f'{KEY_ENV}="$(secret nts.mpt.api_key)" (never argv)'
        )
        sys.exit(2)
    return key


def encode_body(body):
    """PRF-0541-2: the exact bytes put on the wire — non-ASCII narration text is
    sent as UTF-8, not \\uXXXX escapes, so the 2 MiB cap (checked on these same
    bytes in cmd_submit) is the real wire size."""
    return json.dumps(body, ensure_ascii=False).encode("utf-8")


def request(base_url, path, key, method="GET", body=None, headers=None, timeout=60):
    """One HTTP call via _OPENER. Returns (status, headers, bytes). Never logs
    the key or the body; never follows a redirect; exits 1 on a 3xx refusal and
    2 on any transport-level failure (connect refused, reset, incomplete body)."""
    data = encode_body(body) if body is not None else None
    req = urllib.request.Request(base_url.rstrip("/") + path, data=data, method=method)
    # Unredirected: even a *known* redirect target would not re-receive this key.
    req.add_unredirected_header("x-api-key", key)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        # F1/T57: extra headers (x-task-id) travel unredirected exactly like the
        # key — the NoRedirect handler is the other half of the guarantee.
        for name, value in (headers or {}).items():
            req.add_unredirected_header(name, value)
    except (TypeError, ValueError) as exc:
        eprint(f"mpt_client.py: header rejected: {exc}")
        sys.exit(2)
    try:
        with _OPENER.open(req, timeout=timeout) as resp:
            return resp.status, dict(resp.headers), resp.read()
    except urllib.error.HTTPError as exc:
        if 300 <= exc.code < 400:
            location = (exc.headers or {}).get("Location") or ""
            eprint(
                f"mpt_client.py: refusing {exc.code} redirect to {location!r}; "
                f"the key is never forwarded to a Location host"
            )
            sys.exit(1)
        return exc.code, dict(exc.headers or {}), exc.read()
    except (urllib.error.URLError, http.client.HTTPException, OSError) as exc:
        # QA-0541-6: a reset/aborted connection mid-poll (or an IncompleteRead in
        # a download) is a transport failure like connect-refused, not a render
        # failure and never a traceback.
        detail = getattr(exc, "reason", exc)
        eprint(f"mpt_client.py: cannot reach {base_url}: {type(exc).__name__}: {detail}")
        sys.exit(2)


def json_body(raw):
    try:
        return json.loads(raw.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None


def die_http(message, raw):
    eprint(f"mpt_client.py: {message}")
    sys.stderr.write(raw.decode("utf-8", "replace")[:2000] + "\n")
    sys.exit(1)


# ------------------------------------------------------------------------- commands


def cmd_submit(args, run_dir=None, spec=None):
    repo = repo_root()
    check_base_url(args.base_url)
    if spec is None:
        spec = json.loads(open(args.spec, encoding="utf-8").read())
    run_id = spec["run_id"]
    # run_dir_for() validates run_id (safe-id) and containment before any write;
    # cmd_run passes the spec and the dir it already validated from that spec
    # (re-checking the dir as an explicit --run-dir would refuse the default
    # $NTS_HOME/runs under <repo>/.nts).
    if run_dir is None:
        run_dir = run_dir_for(repo, run_id, args.run_dir or None)
    body = build_body(spec, repo)
    encoded = encode_body(body)
    if len(encoded) > MAX_BODY_BYTES:
        eprint(f"mpt_client.py: body is {len(encoded)} bytes, over the {MAX_BODY_BYTES} cap")
        sys.exit(2)
    os.makedirs(run_dir, exist_ok=True)
    # The body holds the script: default run dir is $NTS_HOME/runs (gitignored); an explicit --run-dir inside the repo exits 2 in run_dir_for (D15 S6, T11).
    with open(os.path.join(run_dir, "submit-body.json"), "w", encoding="utf-8") as fh:
        fh.write(json.dumps(body, ensure_ascii=False, indent=2) + "\n")
    key = _key()
    status, _headers, raw = request(
        args.base_url, SUBMIT_PATH, key, method="POST", body=body, headers={"x-task-id": run_id}
    )
    doc = json_body(raw)
    if status != 200 or not isinstance(doc, dict) or not (doc.get("data") or {}).get("task_id"):
        die_http(f"submit failed: HTTP {status}", raw)
    task_id = doc["data"]["task_id"]
    check_task_id(str(task_id), "server")
    with open(os.path.join(run_dir, "submit-response.json"), "w", encoding="utf-8") as fh:
        fh.write(json.dumps(doc, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"task_id": task_id, "run_id": run_id, "request_id": run_id}))
    return 0


def cmd_poll(args):
    """Returns (rc, report). rc 0 done · 1 failure/lost/timeout · 2 transport.
    `report` is the last render-status.v1 object printed — `run` reuses it, it
    is never re-derived or re-fabricated (CRV-0541-1)."""
    check_base_url(args.base_url)
    check_task_id(args.task_id, "argv")
    # PRF-0541-3: --interval 0 tight-loops (~4k req/s against the render node);
    # --interval <0 dies inside time.sleep with a traceback. Usage error → 2.
    if not (args.interval > 0):
        eprint(f"mpt_client.py: --interval must be > 0, got {args.interval}")
        sys.exit(2)
    key = _key()
    trace = open(args.trace, "a", encoding="utf-8") if args.trace else None
    started = now_iso()
    last = None
    deadline = time.monotonic() + args.timeout
    try:
        while True:
            # F1/T57: pollers always send x-task-id so MPT's log and error text
            # can name the caller (task_id is UUID-validated above).
            status, _headers, raw = request(
                args.base_url, TASK_PATH.format(task_id=args.task_id), key,
                headers={"x-task-id": args.task_id},
            )
            doc = json_body(raw)
            http_status = 200 if isinstance(doc, dict) and "data" in doc else status
            task = (doc or {}).get("data") or {}
            state, stage, outcome, percent = map_task(task, http_status=http_status)
            # QA-0541-7: no D5 outcome code is named for auth/transport answers,
            # so the code stays render_node_error and the observed HTTP status is
            # recorded on the status (and mirrored into outcome.detail).
            if outcome is not None and outcome.get("code") == "render_node_error" and status != 200:
                outcome["detail"] = f"http_{status}"
            mpt = {
                "state": task.get("state"),
                "progress": task.get("progress"),
                "failed_stage": task.get("failed_stage"),
                "warnings": task.get("warnings") or [],
            }
            report = render_status(
                args.task_id, args.run_id, state, stage, outcome, percent, mpt,
                submitted_at=started, started_at=started,
                finished_at=now_iso() if state in (STATE_FAILED, STATE_SUCCEEDED) else None,
                http_status=status,
            )
            if trace is not None:
                trace.write(json.dumps({
                    "ts": now_iso(),
                    "state": task.get("state"),
                    "progress": task.get("progress"),
                    "failed_stage": task.get("failed_stage"),
                }) + "\n")
                trace.flush()
            if report != last:
                print(json.dumps(report, ensure_ascii=False))
                last = report
            if state in (STATE_FAILED, STATE_SUCCEEDED):
                return (0 if state == STATE_SUCCEEDED else 1), report
            if time.monotonic() >= deadline:
                # D15 P7: a poll timeout is NOT a failed render — resume with `poll`,
                # never re-submit while MPT still reports state 4.
                report["poll_timeout"] = True
                report["state"] = STATE_RUNNING
                print(json.dumps(report, ensure_ascii=False))
                return 1, report
            time.sleep(args.interval)
    finally:
        if trace is not None:
            trace.close()


def download_file(base_url, task_id, name, key, dest, timeout=DOWNLOAD_TIMEOUT, max_bytes=MAX_DOWNLOAD_BYTES):
    """PRF-0541-1: stream one artefact to a temp file in the same directory,
    hashing as it goes, then os.replace — a failure never leaves a partial
    final-1.mp4 and peak RSS stays flat on big files. Returns (status, row)."""
    req = urllib.request.Request(
        base_url.rstrip("/") + DOWNLOAD_PATH.format(task_id=task_id, name=name),
        method="GET",
    )
    req.add_unredirected_header("x-api-key", key)
    # F1/T57: the download request is a poller-shaped request too — x-task-id
    # travels unredirected like the key (task_id is UUID-validated by callers).
    req.add_unredirected_header("x-task-id", task_id)
    tmp_path = None
    try:
        with _OPENER.open(req, timeout=timeout) as resp:
            ctype = resp.headers.get("Content-Type") or resp.headers.get("content-type")
            # Chunked resp.read() does NOT raise IncompleteRead on a short body:
            # a dying upstream must be caught by comparing bytes to Content-Length.
            declared = resp.headers.get("Content-Length") or resp.headers.get("content-length")
            try:
                declared = int(declared) if declared is not None else None
            except (TypeError, ValueError):
                declared = None
            sha = hashlib.sha256()
            written = 0
            fd, tmp_path = tempfile.mkstemp(dir=dest, prefix="." + name + ".", suffix=".tmp")
            with os.fdopen(fd, "wb") as fh:
                while True:
                    chunk = resp.read(DOWNLOAD_CHUNK)
                    if not chunk:
                        break
                    written += len(chunk)
                    if written > max_bytes:
                        raise ValueError(f"{name}: download over the {max_bytes}-byte cap")
                    sha.update(chunk)
                    fh.write(chunk)
                fh.flush()
                os.fsync(fh.fileno())
            if declared is not None and written != declared:
                return 200, {"file": name, "status": "truncated", "bytes": written,
                             "sha256": None, "content_type": ctype}
            os.replace(tmp_path, os.path.join(dest, name))
            tmp_path = None
            return 200, {
                "file": name,
                "status": 200,
                "bytes": written,
                "sha256": sha.hexdigest(),
                "content_type": ctype,
            }
    except urllib.error.HTTPError as exc:
        if 300 <= exc.code < 400:
            location = (exc.headers or {}).get("Location") or ""
            eprint(f"mpt_client.py: refusing {exc.code} redirect to {location!r}")
            sys.exit(1)
        return exc.code, {"file": name, "status": exc.code, "bytes": 0,
                          "sha256": None, "content_type": None}
    except (urllib.error.URLError, http.client.HTTPException, OSError) as exc:
        detail = getattr(exc, "reason", exc)
        eprint(f"mpt_client.py: download of {name} failed: {type(exc).__name__}: {detail}")
        sys.exit(2)
    finally:
        if tmp_path is not None:
            try:
                os.unlink(tmp_path)
            except OSError:
                pass


def count_srt_cues(path):
    """Cue count = timestamp lines (`HH:MM:SS,mmm --> HH:MM:SS,mmm`)."""
    cues = 0
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "-->" in line and re.match(r"^\d{1,2}:\d{2}:\d{2}[,.]\d{3}", line.strip()):
                    cues += 1
    except OSError:
        return None
    return cues


def download_files(base_url, task_id, dest, key, timeout=DOWNLOAD_TIMEOUT):
    """Download every DOWNLOAD_FILES entry; returns (rc, manifest).
    QA-0541-10: only subtitle.srt may 404 — any missing required artefact is
    rc 1 (an output failure, D5 `render_output_invalid` territory), never 0.
    F7: `timeout` is the per-connection read budget (plan.md:137 -> 300 s)."""
    manifest = {"schema": "mpt-download.v1", "task_id": task_id, "files": []}
    rc = 0
    for name in DOWNLOAD_FILES:
        try:
            _status, row = download_file(base_url, task_id, name, key, dest, timeout=timeout)
        except ValueError as exc:
            # Over the 512 MiB cap: an output failure, like a corrupted artefact.
            eprint(f"mpt_client.py: {exc}")
            manifest["files"].append({"file": name, "status": "too_large",
                                      "bytes": 0, "sha256": None, "content_type": None})
            rc = 1
            continue
        manifest["files"].append(row)
        if row["status"] == "truncated":
            # A short body for a 200 answer — the upstream died mid-write.
            eprint(f"mpt_client.py: {name}: truncated body ({row['bytes']} bytes)")
            rc = 1
        elif row["status"] != 200 and name in DOWNLOAD_REQUIRED:
            # A 404 on the SRT is not an error when subtitles are off (D4 route
            # contract); every other artefact must exist for a succeeded task.
            eprint(f"mpt_client.py: required artefact {name}: HTTP {row['status']}")
            rc = 1
    with open(os.path.join(dest, "manifest.json"), "w", encoding="utf-8") as fh:
        fh.write(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    return rc, manifest


def cmd_download(args):
    check_base_url(args.base_url)
    check_task_id(args.task_id, "argv")
    dest = os.path.abspath(args.dest)
    os.makedirs(dest, exist_ok=True)
    rc, manifest = download_files(args.base_url, args.task_id, dest, _key(),
                                   timeout=args.download_timeout)
    print(json.dumps(manifest, ensure_ascii=False))
    return rc


def video_output_block(manifest_video, check):
    """D4 `output.video` (D9, F3): `run` always passes --normalize, so when
    render_check.py ran and produced its own block (final-1.norm.mp4 + its
    hash + `normalized`), that artefact is the reportable video output; the
    downloaded manifest row (the raw final-1.mp4 hash) is only the fallback for
    trees where 05.5.1-T1 has not landed."""
    check_output = (check or {}).get("output") if isinstance(check, dict) else None
    if isinstance(check_output, dict) and check_output.get("sha256"):
        return dict(check_output)
    return {"bytes": manifest_video["bytes"], "sha256": manifest_video["sha256"]}


def run_checker(checker, run_dir, aspect, timeout=RENDER_CHECK_TIMEOUT):
    """render_check.py's report parsed from its stdout (05 T78); a timeout or
    non-JSON stdout reads as `render_check_failed`, never a hung `run`."""
    try:
        proc = subprocess.run(
            [sys.executable, checker, "run", "--task-dir", run_dir, "--aspect", aspect,
             "--subtitles", "on", "--normalize"],
            capture_output=True, text=True, timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return {"errors": ["render_check_failed"],
                "stderr": f"render_check.py timed out after {timeout} s"}
    try:
        return json.loads(proc.stdout)
    except ValueError:
        return {"errors": ["render_check_failed"], "stderr": proc.stderr[-500:]}


def cmd_run(args):
    repo = repo_root()
    spec = json.loads(open(args.spec, encoding="utf-8").read())
    run_dir = run_dir_for(repo, spec["run_id"], args.run_dir or None)
    rc = cmd_submit(args, run_dir=run_dir, spec=spec)
    if rc:
        return rc
    # Re-read the task id the submit printed by reading the response we just saved.
    task_id = json.loads(open(os.path.join(run_dir, "submit-response.json"), encoding="utf-8").read())["data"]["task_id"]
    check_task_id(str(task_id), "server")
    poll_args = argparse.Namespace(
        base_url=args.base_url, task_id=task_id, run_id=spec["run_id"],
        interval=args.interval, timeout=args.timeout,
        trace=os.path.join(run_dir, "trace.jsonl"),
    )
    rc, poll_status = cmd_poll(poll_args)
    if rc == 2:
        # Transport failure (QA-0541-6): the poll message already went to stderr;
        # re-run `poll` later — nothing here was fabricated.
        return 2
    manifest = None
    download_rc = 0
    if rc == 0:
        download_rc, manifest = download_files(args.base_url, task_id, run_dir, _key(),
                                               timeout=args.download_timeout)
    check = None
    checker = os.path.join(repo, "worker-node", "checks", "render_check.py")
    if os.path.exists(checker):
        check = run_checker(checker, run_dir, ASPECT_PRESET[spec["aspect"]])
    # CRV-0541-1/2: reuse the status the poll actually printed (a timeout keeps
    # state "running" + "poll_timeout": true — never a fabricated failed/-1) and
    # fill the D4 `output` from the download manifest plus the SRT cue count.
    # `narration_seconds` is render_check.py's measured audio.mp3 duration (D9);
    # it stays null when the checker failed, because the manifest cannot supply it.
    output = None
    if manifest:
        files = {f["file"]: f for f in manifest.get("files", [])}
        video = files.get("final-1.mp4") or {}
        srt = files.get("subtitle.srt") or {}
        cues = None
        if srt.get("status") == 200:
            cues = ((check or {}).get("subtitles") or {}).get("cues")
            if cues is None:
                cues = count_srt_cues(os.path.join(run_dir, "subtitle.srt"))
        if rc == 0 and video.get("status") == 200:
            output = {
                "video": video_output_block(video, check),
                "subtitles": ({"bytes": srt["bytes"], "sha256": srt["sha256"],
                               "cues": cues}
                              if srt.get("status") == 200 else None),
                "narration_seconds": ((check or {}).get("narration") or {}).get("duration_s"),
            }
    if poll_status is not None:
        status = dict(poll_status)
    else:
        status = render_status(
            task_id, spec["run_id"], STATE_FAILED, None, None, None,
            {"state": None, "progress": None, "failed_stage": None, "warnings": []},
        )
    if rc == 0 and download_rc != 0:
        # MPT reported success but a required artefact never arrived.
        status["state"] = STATE_FAILED
        status["stage"] = "checking"
        status["finished_at"] = now_iso()
        status["outcome"] = {
            "code": "render_output_invalid",
            "detail": "download_missing",
            "message": "MPT reported the task complete but a required artefact was not served.",
        }
        rc = 1
    # F6: `output` exists only for a terminal success — a run whose failure IS
    # an incomplete download must not carry a video artefact, and a poll timeout
    # (state "running") reports no output either.
    status["output"] = output if rc == 0 else None
    status["check"] = check
    print(json.dumps(status, ensure_ascii=False))
    return rc


# -------------------------------------------------------------------------- self-test


def self_test():
    failures = []

    def expect(name, cond, detail=""):
        if cond:
            print(f"PASS selftest:{name}")
        else:
            print(f"FAIL selftest:{name}{': ' + detail if detail else ''}")
            failures.append(name)

    repo = repo_root()
    spec_path = os.path.join(repo, "trial", "fixtures", "spike-render-spec.json")
    if not os.path.exists(spec_path):
        eprint(f"mpt_client.py: {spec_path} missing")
        return 2
    spec = json.loads(open(spec_path, encoding="utf-8").read())
    body = build_body(spec, repo)
    expect("d3-key-set", set(body) == set(D3_KEYS), f"missing={set(D3_KEYS) - set(body)} extra={set(body) - set(D3_KEYS)}")
    expect("d3-no-n-threads", "n_threads" not in body)
    expect("d3-fixed-values", body["video_count"] == 1 and body["video_source"] == "openai_image"
           and body["video_concat_mode"] == "sequential" and body["video_fit_mode"] == "cover"
           and body["video_transition_mode"] is None and body["match_materials_to_script"] is True
           and body["voice_volume"] == 1.0 and body["voice_rate"] == 1.0
           and body["subtitle_display_mode"] == "sentence" and body["subtitle_animation"] == "none")
    expect("d3-no-music", body["bgm_type"] == "" and body["bgm_file"] == "" and body["bgm_volume"] == 0)
    expect("d3-voice", body["voice_name"] == "kokoro:" + spec["voice_id"])
    expect("d3-subject-len", 0 < len(body["video_subject"]) <= SUBJECT_MAX)
    expect("d3-script-nfc", unicodedata.normalize("NFC", body["video_script"]) == body["video_script"])
    expect("d3-terms", isinstance(body["video_terms"], list) and len(body["video_terms"]) >= 1)
    expect("d3-font", body["font_name"] == D6_FONT_NAME)

    # CRV-0541-3: a story file with leading blank lines must not leak the
    # subject into the script. SEC-03: the fixture must sit under an approved
    # root, so this temp dir is the $NTS_HOME of the call.
    import tempfile as _tf0
    with _tf0.TemporaryDirectory() as _bl:
        _prev_home = os.environ.get("NTS_HOME")
        os.environ["NTS_HOME"] = _bl
        try:
            story_bl = os.path.join(_bl, "story.txt")
            with open(story_bl, "w", encoding="utf-8") as fh:
                fh.write("\n\nTitle line\nBody line one.\nBody line two.\n")
            bl_body = build_body(dict(spec, story_file=story_bl), repo)
            expect("crv3.leading-blank-subject", bl_body["video_subject"] == "Title line",
                   bl_body["video_subject"])
            expect("crv3.leading-blank-script",
                   bl_body["video_script"] == "Body line one.\nBody line two.",
                   bl_body["video_script"][:60])
            # SEC-03: an absolute path under $NTS_HOME is an approved root …
            expect("sec03.nts-home-accepted",
                   bl_body["video_subject"] == "Title line", bl_body["video_subject"])
            # … while an absolute path outside the repo and $NTS_HOME is not,
            # and neither is a `..` traversal out of the repo.
            for label, story in (("absolute", "/etc/hosts"),
                                 ("traversal", "../../../../etc/hosts")):
                try:
                    build_body(dict(spec, story_file=story), repo)
                    expect(f"sec03.story-{label}-rejected", False, "no ValueError")
                except ValueError as exc:
                    expect(f"sec03.story-{label}-rejected", "outside the approved roots" in str(exc),
                           str(exc))
            for key in ("terms_file", "preset_file"):
                try:
                    build_body(dict(spec, **{key: "/etc/hosts"}), repo)
                    expect(f"sec03.{key}-outside-rejected", False, "no ValueError")
                except ValueError as exc:
                    expect(f"sec03.{key}-outside-rejected", "outside the approved roots" in str(exc),
                           str(exc))
            # SEC-03: containment is by inode, so a case-swapped spelling of an
            # approved root (APFS is case-insensitive) is still inside it.
            swapped = story_bl.swapcase()
            if os.path.exists(swapped):
                cv_body = build_body(dict(spec, story_file=swapped), repo)
                expect("sec03.case-variant-accepted", cv_body["video_subject"] == "Title line",
                       cv_body["video_subject"])
            # SEC-03: NTS_HOME=/ must not approve every host file.
            os.environ["NTS_HOME"] = "/"
            try:
                build_body(dict(spec, story_file=story_bl), repo)
                expect("sec03.nts-home-root-rejected", False, "no ValueError")
            except ValueError as exc:
                expect("sec03.nts-home-root-rejected", "outside the approved roots" in str(exc),
                       str(exc))
        finally:
            if _prev_home is None:
                os.environ.pop("NTS_HOME", None)
            else:
                os.environ["NTS_HOME"] = _prev_home

    # SEC-04: the catalog `file` is a plain basename; anything else is refused
    # before it can become bgm_file.
    catalog_path = os.path.join(repo, "seed", "music-tracks.v1.json")
    catalog = json.loads(open(catalog_path, encoding="utf-8").read())
    expect("sec04.catalog-files-match",
           all(BGM_FILE_RE.fullmatch(t.get("file", "")) for t in catalog["tracks"]))
    expect("sec04.bgm-accept",
           bgm_file_name({"id": "x", "file": "apprehensive-at-best.mp3"}) == "apprehensive-at-best.mp3")
    for bad in ("../evil.mp3", "/tmp/evil.mp3", "http://evil/evil.mp3", ".evil.mp3", "evil.wav",
                "evil.mp3/../x", "", "safe.mp3\n"):
        try:
            bgm_file_name({"id": "x", "file": bad})
            expect(f"sec04.bgm-reject-{bad!r}", False, "no ValueError")
        except ValueError:
            expect(f"sec04.bgm-reject-{bad!r}", True)
    portrait = dict(spec, aspect="9:16")
    pbody = build_body(portrait, repo)
    expect("d3-portrait-preset", pbody["font_size"] == 64 and pbody["subtitle_position"] == "custom"
           and pbody["custom_position"] == 62.0)
    expect("d3-landscape-preset", body["font_size"] == 84 and body["subtitle_position"] == "bottom")

    # every mapping-table row (D4/D5)
    rows = [
        ({"state": 4, "progress": 0}, 200, STATE_QUEUED, "queued", None),
        ({"state": 4, "progress": 5}, 200, STATE_RUNNING, "voice", None),
        ({"state": 4, "progress": 29}, 200, STATE_RUNNING, "voice", None),
        ({"state": 4, "progress": 30}, 200, STATE_RUNNING, "subtitles", None),
        ({"state": 4, "progress": 39}, 200, STATE_RUNNING, "subtitles", None),
        ({"state": 4, "progress": 40}, 200, STATE_RUNNING, "images", None),
        ({"state": 4, "progress": 49}, 200, STATE_RUNNING, "images", None),
        ({"state": 4, "progress": 50}, 200, STATE_RUNNING, "assembling", None),
        ({"state": 4, "progress": 75}, 200, STATE_RUNNING, "assembling", None),
        ({"state": 4, "progress": 99}, 200, STATE_RUNNING, "assembling", None),
        ({"state": 1, "progress": 100}, 200, STATE_SUCCEEDED, "done", None),
        ({"state": -1, "progress": 20, "failed_stage": "audio"}, 200, STATE_FAILED, "voice", "tts_failed"),
        ({"state": -1, "progress": 40, "failed_stage": "materials"}, 200, STATE_FAILED, "images", "image_generation_failed"),
        ({"state": -1, "progress": 75, "failed_stage": "video"}, 200, STATE_FAILED, "assembling", "render_assembly_failed"),
        ({"state": -1, "progress": 5, "failed_stage": "preflight"}, 200, STATE_FAILED, "voice", "render_node_error"),
        ({"state": -1, "progress": 10, "failed_stage": "script"}, 200, STATE_FAILED, "voice", "render_node_error"),
        ({"state": -1, "progress": 20, "failed_stage": "terms"}, 200, STATE_FAILED, "voice", "render_node_error"),
        ({"state": -1, "progress": 50, "failed_stage": "pipeline"}, 200, STATE_FAILED, "assembling", "render_node_error"),
        ({}, 404, STATE_FAILED, None, "render_lost"),
    ]
    for task, http_status, want_state, want_stage, want_code in rows:
        state, stage, outcome, _pct = map_task(task, http_status=http_status)
        label = f"{task.get('state')}/{task.get('progress')}/{task.get('failed_stage')}/{http_status}"
        expect(f"map-{label}", state == want_state and stage == want_stage
               and (outcome or {}).get("code") == want_code, f"got {state}/{stage}/{outcome}")
    expect("map-every-stage-known", all(s in STAGES for s in ("queued", "voice", "subtitles", "images", "assembling", "done")))
    expect("status-shape", set(render_status("t", "r", STATE_RUNNING, "voice", None, 5, {})) >= {
        "schema", "task_id", "render_job_id", "state", "stage", "percent", "cancel_requested",
        "outcome", "output", "check", "images", "mpt"})
    expect("status-no-content", "video_script" not in json.dumps(render_status("t", "r", STATE_RUNNING, "voice", None, 5, {})))
    # --------------------------------------------------- loopback stub checks
    # SEC-0541-1/2, SEC-0541-3, QA-0541-1/6/7/10/12, CRV-0541-1/2, PRF-0541-1/2/3.
    # Every stub binds 127.0.0.1 on an ephemeral port — :8080/:8000/:8002 and the
    # spike profile ports (18003/18004/18080) are never touched.
    import socket
    import struct
    import threading
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

    me = os.path.abspath(__file__)

    class StubHandler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *a):
            pass

        def _json(self, status, obj):
            payload = json.dumps(obj).encode("utf-8")
            try:
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            except (BrokenPipeError, ConnectionResetError, OSError):
                pass

        def _redirect(self):
            try:
                self.send_response(302)
                self.send_header("Location", self.server.cfg["redirect"] + self.path)
                self.send_header("Content-Length", "0")
                self.end_headers()
            except OSError:
                pass

        def do_GET(self):
            s = self.server
            if s.cfg.get("redirect"):
                self._redirect()
                return
            s.hits.append(("GET", self.path))
            s.headers_log.append((self.path, self.headers.get("x-task-id"),
                                  self.headers.get("x-api-key")))
            if self.path.startswith(TASK_PATH.split("{")[0]):
                s.task_polls += 1
                if s.cfg.get("rst_after") is not None and s.task_polls > s.cfg["rst_after"]:
                    # Hard RST: the client sees ConnectionResetError mid-poll.
                    s2 = self.connection
                    s2.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                    self.close_connection = True
                    return
                mode = s.cfg.get("task_mode", "done")
                if mode == "lost":
                    self._json(404, {"status": 404, "message": "not found"})
                elif mode == "auth401":
                    self._json(401, {"status": 401, "message": "invalid API key"})
                elif mode == "running":
                    self._json(200, {"status": 200, "data": {
                        "task_id": s.cfg["task_id"], "state": 4, "progress": 40,
                        "failed_stage": None, "warnings": []}})
                else:
                    self._json(200, {"status": 200, "data": {
                        "task_id": s.cfg["task_id"], "state": 1, "progress": 100,
                        "failed_stage": None, "warnings": []}})
                return
            if self.path.startswith("/api/v1/download/"):
                name = self.path.rsplit("/", 1)[-1]
                # dl_delay[name] stalls the response — exercises the client's
                # download read timeout (F7).
                delay = (s.cfg.get("dl_delay") or {}).get(name)
                if delay:
                    time.sleep(delay)
                status = s.cfg.get("dl_status", {}).get(name, 200)
                blob = s.cfg.get("dl_files", {}).get(name, b"")
                if status != 200:
                    self._json(status, {"status": status, "message": "not found"})
                    return
                if s.cfg.get("dl_abort") == name:
                    try:
                        # Advertise the full length, write half, close: the client
                        # sees an incomplete body and must leave no partial file.
                        self.send_response(200)
                        self.send_header("Content-Type", "application/octet-stream")
                        self.send_header("Content-Length", str(len(blob)))
                        self.end_headers()
                        self.wfile.write(blob[: max(1, len(blob) // 2)])
                        self.wfile.flush()
                    except OSError:
                        pass
                    self.close_connection = True
                    return
                try:
                    self.send_response(200)
                    self.send_header("Content-Type", "application/octet-stream")
                    self.send_header("Content-Length", str(len(blob)))
                    self.end_headers()
                    self.wfile.write(blob)
                except (BrokenPipeError, ConnectionResetError, OSError):
                    pass
                return
            self._json(404, {"status": 404, "message": "not found"})

        def do_POST(self):
            s = self.server
            if s.cfg.get("redirect"):
                self._redirect()
                return
            s.hits.append(("POST", self.path))
            s.headers_log.append((self.path, self.headers.get("x-task-id"),
                                  self.headers.get("x-api-key")))
            try:
                length = int(self.headers.get("Content-Length") or 0)
            except ValueError:
                length = 0
            body_raw = self.rfile.read(length) if length > 0 else b""
            s.submit_body_bytes = len(body_raw)
            if self.path == SUBMIT_PATH:
                self._json(200, {"status": 200, "data": {"task_id": s.cfg["task_id"]}})
                return
            self._json(404, {"status": 404, "message": "not found"})

    def make_stub(**cfg):
        srv = ThreadingHTTPServer(("127.0.0.1", 0), StubHandler)
        srv.cfg = cfg
        srv.hits = []
        srv.headers_log = []
        srv.task_polls = 0
        srv.submit_body_bytes = None
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        return srv

    def run_cli(*argv, env_extra=None, timeout=90, cwd=None):
        env = dict(os.environ)
        env[KEY_ENV] = "selftest-key"
        env.pop("NTS_MPT_BASE_URL", None)
        if env_extra:
            env.update(env_extra)
        proc = subprocess.run(
            [sys.executable, me, *argv], env=env,
            capture_output=True, text=True, timeout=timeout, cwd=cwd,
        )
        return proc.returncode, proc.stdout, proc.stderr

    def exits(code):
        def wrap(fn):
            try:
                fn()
            except SystemExit as exc:
                return exc.code == code
            return False
        return wrap

    import tempfile as _tf
    task_uuid = "11111111-2222-3333-4444-555555555555"
    srt_blob = (
        "1\n00:00:00,000 --> 00:00:02,000\nline one\n\n"
        "2\n00:00:02,000 --> 00:00:04,000\nline two\n\n"
        "3\n00:00:04,000 --> 00:00:06,000\nline three\n"
    ).encode("utf-8")
    mp4_blob = (b"fake-mp4-moov-mdat" * 8192)  # ~144 KiB, streams fine
    mp3_blob = b"\xff\xfb" + b"\x00" * 4096
    script_blob = json.dumps({"id": task_uuid}).encode("utf-8")

    with _tf.TemporaryDirectory(prefix="mpt-client-selftest-") as td:
        spec_ok = dict(spec, run_id="selftest-run")
        spec_ok["story_file"] = os.path.abspath(os.path.join(repo, spec["story_file"])) \
            if not os.path.isabs(spec["story_file"]) else spec["story_file"]
        spec_ok["terms_file"] = os.path.abspath(os.path.join(repo, spec["terms_file"])) \
            if not os.path.isabs(spec["terms_file"]) else spec["terms_file"]
        spec_file = os.path.join(td, "spec.json")
        with open(spec_file, "w", encoding="utf-8") as fh:
            json.dump(spec_ok, fh)

        srv = make_stub(task_id=task_uuid, task_mode="done", dl_files={
            "final-1.mp4": mp4_blob, "subtitle.srt": srt_blob,
            "audio.mp3": mp3_blob, "script.json": script_blob,
        })
        srv2 = make_stub(task_id=task_uuid)  # redirect target: counts hits
        base = f"http://127.0.0.1:{srv.server_port}"

        # SEC-0541-1: a 302 must never be followed; the redirect target gets 0 hits.
        srv.cfg["redirect"] = f"http://127.0.0.1:{srv2.server_port}"
        rc, out, err = run_cli("--base-url", base, "submit", "--spec", spec_file,
                               "--run-dir", os.path.join(td, "redirect-run"))
        expect("sec1.redirect-refused-exit-1", rc == 1, f"rc={rc} err={err[-120:]}")
        expect("sec1.redirect-target-zero-hits", srv2.hits == [], str(srv2.hits))
        expect("sec1.redirect-reported", "refusing" in err, err[-120:])
        srv.cfg["redirect"] = None

        # SEC-0541-2: env proxies can never carry the request away from loopback.
        rc, out, err = run_cli(
            "--base-url", base, "submit", "--spec", spec_file,
            "--run-dir", os.path.join(td, "proxy-run"),
            env_extra={"HTTP_PROXY": "http://127.0.0.1:1",
                       "http_proxy": "http://127.0.0.1:1",
                       "ALL_PROXY": "http://127.0.0.1:1"})
        expect("sec2.env-proxy-ignored", rc == 0, f"rc={rc} err={err[-120:]}")
        expect("sec2.stub-reached", any(h[0] == "POST" for h in srv.hits), str(srv.hits))

        # SEC-0541-3: argv ids must be UUIDs; non-loopback URLs refused.
        rc, _, err = run_cli("--base-url", base, "poll", "--task-id", "not-a-uuid")
        expect("sec3.argv-task-id-exit-2", rc == 2, f"rc={rc}")
        rc, _, err = run_cli("--base-url", base, "poll", "--task-id", "../../etc")
        expect("sec3.task-id-traversal-exit-2", rc == 2, f"rc={rc}")
        rc, _, err = run_cli("--base-url", "http://192.0.2.1:8080",
                             "poll", "--task-id", task_uuid, "--timeout", "1")
        expect("sec3.non-loopback-exit-2", rc == 2, f"rc={rc} err={err[-120:]}")
        rc, _, err = run_cli("--base-url", "ftp://127.0.0.1:1",
                             "poll", "--task-id", task_uuid, "--timeout", "1")
        expect("sec3.non-http-scheme-exit-2", rc == 2, f"rc={rc}")
        check = exits(2)
        expect("sec3.allow-remote-host-ok",
               not check(lambda: check_base_url("http://192.0.2.1:8080", allow_remote=True)))
        expect("sec3.allow-remote-keeps-scheme",
               check(lambda: check_base_url("ftp://192.0.2.1:8080", allow_remote=True)))
        # A server that hands back a non-UUID task_id is an HTTP failure (exit 1).
        srv.cfg["task_id"] = "abc"
        rc, _, err = run_cli("--base-url", base, "submit", "--spec", spec_file,
                             "--run-dir", os.path.join(td, "bad-taskid"))
        expect("sec3.server-task-id-exit-1", rc == 1, f"rc={rc} err={err[-120:]}")
        srv.cfg["task_id"] = task_uuid

        # QA-0541-1 + QA-0541-12: run_id is a safe id, resolved path contained.
        nts = os.path.join(td, "nts")
        for label, bad_id in (("qa1.traversal", "../../evil-selftest"),
                              ("qa12.crlf", "s3\r\nX-Injected: yes")):
            bad_spec_file = os.path.join(td, label + ".json")
            with open(bad_spec_file, "w", encoding="utf-8") as fh:
                json.dump(dict(spec_ok, run_id=bad_id), fh)
            rc, _, err = run_cli("--base-url", base, "submit", "--spec", bad_spec_file,
                                 env_extra={"NTS_HOME": nts})
            expect(f"{label}-exit-2", rc == 2, f"rc={rc} err={err[-120:]}")
        expect("qa1.nothing-escaped", not os.path.exists(os.path.join(td, "evil-selftest"))
               and not os.path.exists(os.path.join(nts, "evil-selftest")))

        # PRF-0541-3: non-positive --interval is a usage error, never a busy loop.
        for label, val in (("zero", "0"), ("negative", "-1")):
            rc, _, _ = run_cli("--base-url", base, "poll", "--task-id", task_uuid,
                               "--interval", val, "--timeout", "2")
            expect(f"prf3.interval-{label}-exit-2", rc == 2, f"rc={rc}")

        # QA-0541-6: a hard reset mid-poll is a clean exit 2, no traceback, and
        # the next poll resumes (D15 P7).
        srv.cfg["task_mode"] = "running"
        srv.cfg["rst_after"] = 2
        rc, out, err = run_cli("--base-url", base, "poll", "--task-id", task_uuid,
                               "--interval", "0.1", "--timeout", "10")
        expect("qa6.reset-exit-2", rc == 2, f"rc={rc} err={err[-120:]}")
        expect("qa6.no-traceback", "Traceback" not in err, err[-160:])
        srv.cfg["rst_after"] = None
        srv.cfg["task_mode"] = "done"
        rc, out, err = run_cli("--base-url", base, "poll", "--task-id", task_uuid,
                               "--interval", "0.1", "--timeout", "10")
        expect("qa6.resumable", rc == 0, f"rc={rc} err={err[-120:]}")

        # QA-0541-7: an HTTP auth failure keeps render_node_error, with status recorded.
        srv.cfg["task_mode"] = "auth401"
        rc, out, _ = run_cli("--base-url", base, "poll", "--task-id", task_uuid,
                             "--timeout", "2")
        last_report = json.loads(out.strip().splitlines()[-1])
        expect("qa7.auth-exit-1", rc == 1, f"rc={rc}")
        expect("qa7.auth-still-node-error",
               last_report["outcome"]["code"] == "render_node_error", str(last_report.get("outcome")))
        expect("qa7.status-recorded",
               last_report.get("http_status") == 401
               and last_report["outcome"].get("detail") == "http_401",
               str(last_report.get("outcome")))
        srv.cfg["task_mode"] = "done"

        # CRV-0541-1: a poll timeout inside `run` prints running + poll_timeout.
        srv.cfg["task_mode"] = "running"
        rc, out, _ = run_cli("--base-url", base, "run", "--spec", spec_file,
                             "--run-dir", os.path.join(td, "run-timeout"),
                             "--interval", "0.2", "--timeout", "1")
        last_report = json.loads(out.strip().splitlines()[-1])
        expect("crv1.timeout-exit-1", rc == 1, f"rc={rc}")
        expect("crv1.timeout-state-running",
               last_report["state"] == STATE_RUNNING and last_report.get("poll_timeout") is True,
               str({k: last_report.get(k) for k in ("state", "poll_timeout", "mpt")}))
        expect("crv1.never-fabricated",
               last_report["mpt"]["state"] == 4 and last_report["outcome"] is None,
               str(last_report.get("mpt")))
        srv.cfg["task_mode"] = "done"

        # CRV-0541-2: a full `run` fills the D4 output from the manifest + cues.
        run_dir = os.path.join(td, "run-ok")
        rc, out, err = run_cli("--base-url", base, "run", "--spec", spec_file,
                               "--run-dir", run_dir,
                               "--interval", "0.2", "--timeout", "10")
        last_report = json.loads(out.strip().splitlines()[-1])
        out_block = last_report.get("output") or {}
        expect("crv2.run-exit-0", rc == 0, f"rc={rc} err={err[-120:]}")
        expect("crv2.state-succeeded", last_report["state"] == STATE_SUCCEEDED, str(last_report.get("state")))
        expect("crv2.video-block",
               out_block.get("video") == {"bytes": len(mp4_blob),
                                          "sha256": hashlib.sha256(mp4_blob).hexdigest()},
               str(out_block.get("video")))
        expect("crv2.subtitles-block",
               out_block.get("subtitles", {}).get("cues") == 3
               and out_block["subtitles"]["sha256"] == hashlib.sha256(srt_blob).hexdigest(),
               str(out_block.get("subtitles")))
        expect("crv2.narration-null-unknown",
               out_block.get("narration_seconds") is None and "narration_seconds" in out_block,
               str(out_block))
        expect("crv2.files-on-disk",
               os.path.exists(os.path.join(run_dir, "final-1.mp4"))
               and os.path.exists(os.path.join(run_dir, "manifest.json")))

        # CRV-0523-2 / SEC-0523-4: `run` with no --run-dir writes under an
        # NTS_HOME inside <repo>/.nts; an NTS_HOME elsewhere in the tree exits 2.
        os.makedirs(os.path.join(repo_root(), ".nts"), exist_ok=True)
        repo_nts = tempfile.mkdtemp(prefix="selftest-", dir=os.path.join(repo_root(), ".nts"))
        try:
            rc, out, err = run_cli("--base-url", base, "run", "--spec", spec_file,
                                   "--interval", "0.2", "--timeout", "10",
                                   env_extra={"NTS_HOME": repo_nts})
            expect("crv0523.run-default-run-dir-exit-0", rc == 0, f"rc={rc} err={err[-160:]}")
            expect("crv0523.run-default-artefacts",
                   os.path.exists(os.path.join(repo_nts, "runs", spec_ok["run_id"], "final-1.mp4")))
        finally:
            shutil.rmtree(repo_nts, ignore_errors=True)
        tracked_home = os.path.join(repo_root(), "plans", "selftest-nts-home")
        rc, _, err = run_cli("--base-url", base, "submit", "--spec", spec_file,
                             env_extra={"NTS_HOME": tracked_home})
        expect("sec0523.tracked-nts-home-exit-2", rc == 2, f"rc={rc} err={err[-160:]}")
        expect("sec0523.tracked-nothing-written", not os.path.exists(tracked_home))

        # SEC-0523F-1: os.path.realpath keeps the given case on a case-insensitive
        # volume (APFS), so a case-swapped repo path used to slip past the
        # string-prefix checks and aim submit-body.json at the tracked tree;
        # identity decides now. A case-sensitive volume sees the swap as a missing
        # path -> the cases are reported as PASS with a skip note.
        swapped_repo = os.path.join(os.path.dirname(repo), os.path.basename(repo).swapcase())
        swapped = swapped_repo != repo
        if swapped:
            try:
                swapped = os.path.samefile(swapped_repo, repo)
            except OSError:
                swapped = False
        if not swapped:
            for name in ("sec0523f.case-swapped-nts-home-exit-2",
                         "sec0523f.case-swapped-nts-home-nothing-written",
                         "sec0523f.case-swapped-run-dir-exit-2",
                         "sec0523f.case-swapped-run-dir-nothing-written"):
                print(f"PASS selftest:{name} (skipped: volume is case-sensitive)")
        else:
            rc, _, err = run_cli("--base-url", base, "submit", "--spec", spec_file,
                                 env_extra={"NTS_HOME": os.path.join(swapped_repo, "trial")})
            expect("sec0523f.case-swapped-nts-home-exit-2",
                   rc == 2 and "inside the repo but not under" in err,
                   f"rc={rc} err={err[-160:]}")
            expect("sec0523f.case-swapped-nts-home-nothing-written",
                   not os.path.exists(os.path.join(repo, "trial", "runs", spec_ok["run_id"])))
            rc, _, err = run_cli("--base-url", base, "submit", "--spec", spec_file,
                                 "--run-dir", os.path.join(swapped_repo, "trial"))
            expect("sec0523f.case-swapped-run-dir-exit-2",
                   rc == 2 and "inside the repo" in err, f"rc={rc} err={err[-160:]}")
            expect("sec0523f.case-swapped-run-dir-nothing-written",
                   not os.path.exists(os.path.join(repo, "trial", "submit-body.json")))

        # CRV-0541-6 (F6): MPT succeeded but a required artefact 404s -> the final
        # status is the failure AND carries no output (a failed render must never
        # hand a consumer a video block).
        run_dir_missing = os.path.join(td, "run-dl-missing")
        srv.cfg["dl_status"] = {"audio.mp3": 404}
        rc, out, err = run_cli("--base-url", base, "run", "--spec", spec_file,
                               "--run-dir", run_dir_missing,
                               "--interval", "0.2", "--timeout", "10")
        last_report = json.loads(out.strip().splitlines()[-1])
        expect("crv6.missing-artefact-exit-1", rc == 1, f"rc={rc} err={err[-120:]}")
        expect("crv6.state-failed-output-invalid",
               last_report["state"] == STATE_FAILED
               and (last_report.get("outcome") or {}).get("code") == "render_output_invalid",
               str({k: last_report.get(k) for k in ("state", "outcome")}))
        expect("crv6.output-null", last_report.get("output") is None,
               str(last_report.get("output")))
        expect("crv6.artefact-did-arrive",
               os.path.exists(os.path.join(run_dir_missing, "final-1.mp4")))
        srv.cfg["dl_status"] = {}

        # F3/D9: `run` always passes --normalize, so the checker's own block
        # (final-1.norm.mp4 + its hash) wins over the raw manifest row; the
        # manifest row is the fallback while 05.5.1-T1 has not landed.
        norm_block = {"file": "final-1.norm.mp4", "bytes": 1234,
                      "sha256": "d" * 64, "normalized": True}
        manifest_row = {"file": "final-1.mp4", "status": 200, "bytes": 999,
                        "sha256": "c" * 64, "content_type": "video/mp4"}
        expect("f3.checker-block-wins",
               video_output_block(manifest_row, {"output": dict(norm_block)}) == norm_block,
               str(video_output_block(manifest_row, {"output": dict(norm_block)})))
        expect("f3.manifest-fallback-no-checker",
               video_output_block(manifest_row, None) == {"bytes": 999, "sha256": "c" * 64})
        expect("f3.manifest-fallback-checker-without-output",
               video_output_block(manifest_row, {"errors": ["render_check_failed"]})
               == {"bytes": 999, "sha256": "c" * 64})
        hang = os.path.join(td, "hang_checker.py")
        with open(hang, "w", encoding="utf-8") as fh:
            fh.write("import time\ntime.sleep(30)\n")
        t0 = time.monotonic()
        hung = run_checker(hang, td, "landscape", timeout=1)
        expect("t78.checker-timeout-is-failed",
               hung.get("errors") == ["render_check_failed"] and time.monotonic() - t0 < 10,
               str(hung))
        noisy = os.path.join(td, "noisy_checker.py")
        with open(noisy, "w", encoding="utf-8") as fh:
            fh.write("import sys\nprint('not json')\nsys.stderr.write('boom')\n")
        expect("t78.checker-non-json-is-failed",
               run_checker(noisy, td, "landscape", timeout=30)
               == {"errors": ["render_check_failed"], "stderr": "boom"})

        # F1/T57: poll and download requests carry x-task-id (unredirected like
        # the key) so MPT's log and error text can name the caller.
        srv.headers_log = []
        rc, _, _ = run_cli("--base-url", base, "poll", "--task-id", task_uuid,
                           "--interval", "0.2", "--timeout", "5")
        poll_hdrs = [h for h in srv.headers_log if h[0].startswith(TASK_PATH.split("{")[0])]
        expect("f1.poll-sends-task-id",
               bool(poll_hdrs) and all(h[1] == task_uuid for h in poll_hdrs), str(poll_hdrs))
        expect("f1.poll-sends-key", all(h[2] == "selftest-key" for h in poll_hdrs), str(poll_hdrs))
        srv.headers_log = []
        rc, _, _ = run_cli("--base-url", base, "download", "--task-id", task_uuid,
                           "--dest", os.path.join(td, "dl-headers"))
        dl_hdrs = [h for h in srv.headers_log if h[0].startswith("/api/v1/download/")]
        expect("f1.download-sends-task-id",
               len(dl_hdrs) == len(DOWNLOAD_FILES)
               and all(h[1] == task_uuid for h in dl_hdrs), str(dl_hdrs))

        # F7: the download read budget is 300 s (plan.md:137) and the flag is
        # honoured — a stub that stalls the first artefact trips it.
        import inspect as _inspect
        expect("f7.default-timeout-300",
               DOWNLOAD_TIMEOUT == 300
               and _inspect.signature(download_file).parameters["timeout"].default == 300,
               str(DOWNLOAD_TIMEOUT))
        srv.cfg["dl_delay"] = {"final-1.mp4": 3}
        dest_slow = os.path.join(td, "dl-slow")
        rc, _, err = run_cli("--base-url", base, "download", "--task-id", task_uuid,
                             "--dest", dest_slow, "--download-timeout", "1")
        expect("f7.flag-honoured-exit-2", rc == 2, f"rc={rc} err={err[-120:]}")
        expect("f7.no-partial-file",
               not os.path.exists(os.path.join(dest_slow, "final-1.mp4")),
               str(os.listdir(dest_slow)))
        dest_slow2 = os.path.join(td, "dl-slow2")
        os.makedirs(dest_slow2, exist_ok=True)
        expect("f7.download-files-timeout-wired",
               exits(2)(lambda: download_files(base, task_uuid, dest_slow2,
                                               "selftest-key", timeout=1)))
        srv.cfg["dl_delay"] = None

        # F11: an explicit --run-dir inside the repo is refused before any write
        # (submit-body.json holds the narration); `.` is the repo only because
        # the child runs with cwd=repo.
        rc, _, err = run_cli("--base-url", base, "run", "--spec", spec_file,
                             "--run-dir", ".", cwd=repo)
        expect("f11.dot-run-dir-exit-2", rc == 2, f"rc={rc} err={err[-120:]}")
        expect("f11.dot-run-dir-message", "inside the repo" in err, err[-160:])
        rc, _, err = run_cli("--base-url", base, "submit", "--spec", spec_file,
                             "--run-dir", os.path.join(repo, "trial"))
        expect("f11.repo-subdir-exit-2", rc == 2, f"rc={rc} err={err[-120:]}")
        expect("f11.nothing-written",
               not os.path.exists(os.path.join(repo, "submit-body.json"))
               and not os.path.exists(os.path.join(repo, "trial", "submit-body.json")))

        # QA-0541-10: only the SRT may 404; a missing required artefact exits 1.
        srv.cfg["dl_status"] = {"final-1.mp4": 404}
        rc, out, _ = run_cli("--base-url", base, "download", "--task-id", task_uuid,
                             "--dest", os.path.join(td, "dl-missing"))
        expect("qa10.missing-mp4-exit-1", rc == 1, f"rc={rc}")
        expect("qa10.no-partial-file",
               not os.path.exists(os.path.join(td, "dl-missing", "final-1.mp4")))
        srv.cfg["dl_status"] = {"subtitle.srt": 404}
        rc, _, _ = run_cli("--base-url", base, "download", "--task-id", task_uuid,
                           "--dest", os.path.join(td, "dl-nosrt"))
        expect("qa10.srt-404-still-ok", rc == 0, f"rc={rc}")
        srv.cfg["dl_status"] = {}

        # PRF-0541-1: a truncated download never leaves a partial final-1.mp4.
        srv.cfg["dl_abort"] = "final-1.mp4"
        dest_abort = os.path.join(td, "dl-abort")
        rc, _, err = run_cli("--base-url", base, "download", "--task-id", task_uuid,
                             "--dest", dest_abort)
        expect("prf1.abort-exit-1", rc == 1, f"rc={rc} err={err[-120:]}")
        leftovers = [f for f in os.listdir(dest_abort)
                     if f.startswith(".") or f == "final-1.mp4"]
        expect("prf1.no-partial-file", leftovers == [], str(leftovers))
        srv.cfg["dl_abort"] = None

        # PRF-0541-2: the cap is checked on the UTF-8 bytes actually sent.
        # SEC-03: these synthetic story files live under `td`, so `td` is the
        # $NTS_HOME (an approved root) of both calls.
        expect("prf2.no-ascii-escaping",
               b"\\u" not in encode_body({"video_script": "Kiểm tra giọng đọc ẩ"}))
        story_under = os.path.join(td, "story-under.txt")
        with open(story_under, "w", encoding="utf-8") as fh:
            fh.write("Chủ đề kiểm tra\n" + "ẩ" * 600_000)
        under_spec_file = os.path.join(td, "spec-under.json")
        with open(under_spec_file, "w", encoding="utf-8") as fh:
            json.dump(dict(spec_ok, story_file=story_under), fh)
        hits_before = len(srv.hits)
        rc, _, err = run_cli("--base-url", base, "submit", "--spec", under_spec_file,
                             "--run-dir", os.path.join(td, "prf2-under"),
                             env_extra={"NTS_HOME": td})
        expect("prf2.utf8-body-under-cap", rc == 0, f"rc={rc} err={err[-120:]}")
        expect("prf2.wire-bytes-at-cap",
               srv.submit_body_bytes is not None and srv.submit_body_bytes <= MAX_BODY_BYTES,
               str(srv.submit_body_bytes))
        story_over = os.path.join(td, "story-over.txt")
        with open(story_over, "w", encoding="utf-8") as fh:
            fh.write("Chủ đề kiểm tra\n" + "ẩ" * 900_000)
        over_spec_file = os.path.join(td, "spec-over.json")
        with open(over_spec_file, "w", encoding="utf-8") as fh:
            json.dump(dict(spec_ok, story_file=story_over), fh)
        rc, _, err = run_cli("--base-url", base, "submit", "--spec", over_spec_file,
                             "--run-dir", os.path.join(td, "prf2-over"),
                             env_extra={"NTS_HOME": td})
        expect("prf2.over-cap-exit-2", rc == 2, f"rc={rc}")
        expect("prf2.over-cap-not-posted", len(srv.hits) == hits_before + 1, str(len(srv.hits)))

        srv.shutdown()
        srv2.shutdown()
        srv.server_close()
        srv2.server_close()

    return 0 if not failures else 1


def main():
    p = argparse.ArgumentParser(description="MPT reference client (05.4.1-T1, D13).")
    p.add_argument("--base-url", default=os.environ.get("NTS_MPT_BASE_URL", DEFAULT_BASE_URL))
    p.add_argument("--allow-remote", action="store_true",
                   help="allow a non-loopback --base-url host (default: refuse; exit 2)")
    p.add_argument("--self-test", action="store_true")
    sub = p.add_subparsers(dest="cmd")

    s = sub.add_parser("submit")
    s.add_argument("--spec", required=True)
    s.add_argument("--run-dir", default="")

    s = sub.add_parser("poll")
    s.add_argument("--task-id", required=True)
    s.add_argument("--run-id", default="")
    s.add_argument("--interval", type=float, default=DEFAULT_INTERVAL)
    s.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT)
    s.add_argument("--trace", default="")

    s = sub.add_parser("download")
    s.add_argument("--task-id", required=True)
    s.add_argument("--dest", required=True)
    s.add_argument("--download-timeout", type=int, default=DOWNLOAD_TIMEOUT,
                   help="per-connection download read budget in seconds")

    s = sub.add_parser("run")
    s.add_argument("--spec", required=True)
    s.add_argument("--run-dir", default="")
    s.add_argument("--interval", type=float, default=DEFAULT_INTERVAL)
    s.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT)
    s.add_argument("--download-timeout", type=int, default=DOWNLOAD_TIMEOUT,
                   help="per-connection download read budget in seconds")

    args = p.parse_args()
    if args.self_test:
        sys.exit(self_test())
    global _ALLOW_REMOTE
    _ALLOW_REMOTE = bool(getattr(args, "allow_remote", False))
    if not args.cmd:
        p.print_usage(sys.stderr)
        sys.exit(2)
    try:
        if args.cmd == "submit":
            sys.exit(cmd_submit(args))
        if args.cmd == "poll":
            sys.exit(cmd_poll(args)[0])
        if args.cmd == "download":
            sys.exit(cmd_download(args))
        if args.cmd == "run":
            sys.exit(cmd_run(args))
    except (ValueError, OSError, KeyError, TypeError) as exc:
        # CRV-0541-5: spec/usage problems (bad aspect, missing spec file, unreadable
        # fixture) are exit 2 — never a traceback that looks like a render failure.
        eprint(f"mpt_client.py: {exc}")
        sys.exit(2)
    p.print_usage(sys.stderr)
    sys.exit(2)


if __name__ == "__main__":
    main()
