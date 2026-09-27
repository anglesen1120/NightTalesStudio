#!/usr/bin/env python3
"""render-mpt-config.py — render worker-node/mpt/config.template.toml to
$NTS_HOME/src/mpt/config.toml (0600, atomic) after every guard passes (01.2.2-T2).

Run:  uv run --no-project --python 3.12 worker-node/bin/render-mpt-config.py [flags]

Exit codes (D9): 0 ok · 1 guard/substitution/value check failed (nothing written)
                 2 usage/environment error.
Secret values are read only from the four NTS_* environment variables, are never
printed and never appear in argv, diffs or the state file.

--profile spike (03.4.1-T1): renders config.spike.toml instead. Five lines differ from
a normal render: openai_image_base_url = the local image stub (127.0.0.1:18003),
openai_image_api_keys = [] (no auth header, I15), openai_image_prompt_template = ""
(raw term, so stub prompt_sha256 maps 1:1 to the fixture terms), openai_base_url = the
same stub (a stray LLM call hits a 404, never a paid upstream) and listen_port = 18080 so
the spike can run beside the live MPT.

--profile node (05.1.1-T2): renders worker-node/mpt/config.node.template.toml for a GPU
worker and enforces the README D2 table (loopback bind, queue off, no LLM key, image relay,
whisper on cuda). The six placeholders are read from the process environment or from
--env-file (KEY=VALUE lines, 0600 — or 0640 when the file is owned by root, as plan 11
deploys /etc/nts/mpt.env; the file's values win over the environment and the file is
never read implicitly). `--allow-direct-openai` is refused here (CRV-0511-1: D2 reaches
images through the relay only). The out path comes from --out (default
/var/lib/nts/mpt/config.toml, 11.3.1-T2/11 D6) and
`--check --profile node [FILE]` validates an existing file against the same table, writing
nothing. A missing required variable is exit 1; an unusable --env-file is exit 2.
"""

import argparse
import difflib
import errno
import json
import os
import re
from pathlib import Path
import secrets
import stat
import string
import sys
import tempfile
import tomllib

REQUIRED_VARS = (
    "NTS_MPT_API_KEY",
    "NTS_OPENAI_STORY_KEY",
    "NTS_OPENAI_IMAGE_KEY",
    "NTS_PROXY_API_KEY",
)
# SEC-0511-3: \Z, not $ — `$` also matches before a trailing newline, so an env
# value like "hex...\n" used to pass the guard and render verbatim.
VALUE_RE = re.compile(r"^[A-Za-z0-9._-]{16,512}\Z")
SECRET_NAME_RE = re.compile(r"(^|_)(api_key|api_keys|key|keys|token|secret|password)$")
TAP_URL = "http://127.0.0.1:8002/v1"
SPIKE_IMAGE_BASE_URL = "http://127.0.0.1:18003/v1"
SPIKE_TTS_BASE_URL = "http://127.0.0.1:18004/v1"
SPIKE_LISTEN_PORT = 18080

# --- node profile (05.1.1-T2, 05 README D2) ---------------------------------
NODE_TEMPLATE_RELPATH = "worker-node/mpt/config.node.template.toml"
# 11.3.1-T2 runs the renderer with --out /var/lib/nts/mpt/config.toml and then
# `--check --profile node` (no path), so that path is the node default.
NODE_OUT = "/var/lib/nts/mpt/config.toml"
# 11 CR-4 / 11 D8 fixes the node environment file; --env-file must be explicit.
NODE_ENV_FILE = "/etc/nts/mpt.env"
NODE_REQUIRED_VARS = (
    "NTS_MPT_API_KEY",
    "NTS_PROXY_API_KEY",
    "NTS_MPT_MAX_CONCURRENT",
    "NTS_MPT_VIDEO_CODEC",
    "NTS_WHISPER_DEVICE",
    "NTS_WHISPER_COMPUTE",
)
NODE_MAX_CONCURRENT_RE = re.compile(r"^[1-8]\Z")  # SEC-0511-3: \Z, not $
NODE_VIDEO_CODECS = ("libx264", "h264_nvenc")
NODE_WHISPER_DEVICES = ("cuda",)
NODE_WHISPER_COMPUTES = ("float16", "int8_float16")
NODE_LISTEN_PORT = 8080
NODE_IMAGE_BASE_URL = "http://127.0.0.1:8091/v1"
NODE_IMAGE_MODEL = "gpt-image-2"
NODE_KOKORO_BASE_URL = "http://127.0.0.1:8001/v1"
NODE_KOKORO_MODEL_ID = "vieneu-v3-turbo"
NODE_MIN_API_KEY_LEN = 32
# SEC-0511-3 / QA-0511T2-2: the only two real secrets of the node profile. The other
# four values are configuration (codec, device, compute type, concurrency), so they
# must stay legible in the drift diff and in a guard FAIL message.
NODE_SECRET_VARS = ("NTS_MPT_API_KEY", "NTS_PROXY_API_KEY")
# SEC-0511-2: a node env file is six short lines; anything past this is not one.
ENV_FILE_MAX = 64 * 1024
# Sentinel for "the key is not in the rendered TOML at all" (D2 rows must exist).
NODE_ABSENT = object()
# SEC-0511-1: our own state placeholders; re-redacting them must be a no-op
# ("<empty>" must not become "<set>" when the state file is re-read for printing).
REDACTED_RE = re.compile(r"^<(set|empty)( x \d+)?>$")


def _pinned_base_url():
    """OPENAI_BASE_URL from worker-node/pins.env (01 DEC-9); the only place the upstream lives."""
    pins = Path(__file__).resolve().parents[1] / "pins.env"
    for line in pins.read_text(encoding="utf-8").splitlines():
        if line.startswith("OPENAI_BASE_URL="):
            return line.split("=", 1)[1].strip()
    raise SystemExit("render-mpt-config.py: OPENAI_BASE_URL missing in worker-node/pins.env")


DIRECT_URL = _pinned_base_url()


def eprint(*a):
    print(*a, file=sys.stderr)


def default_nts_home(repo):
    env = os.environ.get("NTS_HOME")
    if env:
        return env
    cand = os.path.join(repo, ".nts")
    if os.path.isdir(cand):
        return cand
    return os.path.expanduser("~/.nts")


def flatten(cfg, prefix=""):
    """Yield (dotted_key, value) for every scalar/list leaf."""
    for k, v in cfg.items():
        name = f"{prefix}{k}" if not prefix else f"{prefix}.{k}"
        if isinstance(v, dict):
            yield from flatten(v, name)
        else:
            yield name, v


def redact_value(key, value, mapping_values):
    """Return the value to publish for `key`: redacted when the key name matches,
    or when the value equals a live secret or starts with sk- (SEC-09)."""
    if isinstance(value, str) and REDACTED_RE.match(value):
        return value  # SEC-0511-1: already one of our placeholders
    by_name = bool(SECRET_NAME_RE.search(key.rsplit(".", 1)[-1]))

    def is_secret_scalar(s):
        return isinstance(s, str) and (s in mapping_values or s.startswith("sk-"))

    if isinstance(value, list):
        if by_name or any(is_secret_scalar(s) for s in value):
            return f"<set x {len(value)}>" if value else "<empty>"
        return value
    if by_name or is_secret_scalar(value):
        return "<set>" if value else "<empty>"
    return value


def canonical(path_or_text, mapping_values, from_text=False):
    """Sorted, redacted canonical lines of a TOML config. Raises on parse error."""
    text = path_or_text if from_text else open(path_or_text, "rb").read()
    cfg = tomllib.loads(text) if from_text else tomllib.loads(text.decode("utf-8"))
    lines = []
    for k, v in sorted(flatten(cfg)):
        rv = redact_value(k, v, mapping_values)
        lines.append(f"{k} = {json.dumps(rv, ensure_ascii=False)}")
    return lines


def diff_lines(old_lines, new_lines, old_name, new_name):
    return list(difflib.unified_diff(old_lines, new_lines, old_name, new_name, lineterm=""))


def read_state(path):
    """Read the redacted last-render state file through the SEC-0511-1 checks.

    os.open(O_RDONLY|O_NOFOLLOW) + fstat: the state is only compared when it is a
    regular file owned by the effective user with no group/other bits. Returns
    (lines, warn): (None, None) when the file does not exist yet, (None, "WARN …")
    when it exists but is not trustworthy — the drift diff is then skipped and the
    render still proceeds, because drift is report-only (01.2.2-T2).
    """
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    except FileNotFoundError:
        return None, None
    except OSError as exc:
        return None, f"WARN state file cannot be opened ({exc.strerror or exc}): drift unknown"
    try:
        st = os.fstat(fd)
        mode = stat.S_IMODE(st.st_mode)
        if not stat.S_ISREG(st.st_mode):
            return None, "WARN state file is not a regular file: drift unknown"
        if st.st_uid != os.geteuid():
            return None, "WARN state file is not owned by the effective user: drift unknown"
        if mode & 0o077:
            return None, f"WARN state file mode {mode:04o} is group/other-accessible: drift unknown"
        chunks = []
        while True:
            chunk = os.read(fd, 65536)
            if not chunk:
                break
            chunks.append(chunk)
    except OSError as exc:
        return None, f"WARN state file unreadable ({exc.strerror or exc}): drift unknown"
    finally:
        os.close(fd)
    try:
        text = b"".join(chunks).decode("utf-8")
    except UnicodeDecodeError:
        return None, "WARN state file is not UTF-8: drift unknown"
    return text.splitlines(), None


def redact_state_lines(lines, mapping_values):
    """Re-redact state-file lines before printing them (SEC-0511-1): the state file is
    read back, so a hand-edited or foreign one must not put a value in the drift diff."""
    out = []
    for line in lines:
        key, sep, raw = line.partition(" = ")
        if not sep:
            out.append(line)
            continue
        try:
            value = json.loads(raw)
        except ValueError:
            out.append(line)
            continue
        redacted = json.dumps(redact_value(key, value, mapping_values), ensure_ascii=False)
        out.append(f"{key} = {redacted}")
    return out


def env_checks(out, require_mpt_root=True):
    """Exit-2 environment checks. Returns realpath(out).

    require_mpt_root is False for the node profile (05.1.1-T2): the node's config
    lives outside a checkout, so only the directory is required there.
    """
    real = os.path.realpath(out)
    nts_real = os.path.realpath(default_nts_home(repo_root()))
    if real.startswith("/Volumes/") and not (
        real == nts_real or real.startswith(nts_real.rstrip("/") + "/")
    ):
        eprint(f"FAIL env: output path resolves under /Volumes outside NTS_HOME: {out}")
        sys.exit(2)
    outdir = os.path.dirname(out) or "."
    if not os.path.isdir(outdir) or (
        require_mpt_root and not os.path.exists(os.path.join(outdir, "main.py"))
    ):
        eprint(
            f"FAIL env: no MPT root with main.py at {outdir}"
            if require_mpt_root
            else f"FAIL env: output directory missing: {outdir}"
        )
        sys.exit(2)
    if os.path.islink(out) or os.path.isdir(out):
        eprint(f"FAIL env: {out} is a symlink or directory")
        sys.exit(2)
    return real


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


# --- shared profile helpers (CRV-0511-3) ------------------------------------


def load_template(path):
    """Read `path` and return (string.Template, identifiers) for either profile.

    Exit 2 when the template file is missing (01 D9 environment error); exit 1 with a
    `FAIL template:` line when a placeholder cannot be parsed at all — a bare `$`
    (e.g. in a comment) used to traceback out of the tool (QA-0511T2-3)."""
    if not os.path.isfile(path):
        eprint(f"FAIL env: template missing: {path}")
        sys.exit(2)
    with open(path, "r", encoding="utf-8") as fh:
        text = fh.read()
    tpl = string.Template(text)
    try:
        ids = set(tpl.get_identifiers())
    except ValueError as exc:
        eprint(f"FAIL template: invalid placeholder: {exc}")
        sys.exit(1)
    return tpl, ids


def substitute_template(tpl, mapping):
    """tpl.substitute() with the same D9 message for an unparsable placeholder."""
    try:
        return tpl.substitute(mapping)
    except ValueError as exc:
        eprint(f"FAIL template: invalid placeholder: {exc}")
        sys.exit(1)


def read_config(path):
    """The shared exit-1 stat/parse checks for an existing rendered config.

    Returns (cfg, mode_ok): cfg is None when the caller must exit 1 (missing, not a
    regular file, unparsable TOML); mode_ok is False when the mode is not 0600. Used
    by both the mac and the node `--check` path, messages unchanged (CRV-0511-3)."""
    try:
        st = os.lstat(path)
    except OSError:
        eprint(f"FAIL exists: {path} missing")
        return None, False
    if not stat.S_ISREG(st.st_mode):
        eprint(f"FAIL exists: {path} not a regular file")
        return None, False
    mode_ok = stat.S_IMODE(st.st_mode) == 0o600
    if not mode_ok:
        eprint("FAIL mode: expected 600")
    try:
        cfg = tomllib.loads(open(path, "r", encoding="utf-8").read())
    except (tomllib.TOMLDecodeError, UnicodeDecodeError) as exc:
        eprint(f"FAIL toml: {exc}")
        return None, mode_ok
    return cfg, mode_ok


def node_secret_values(mapping):
    """The node profile's redaction set: the two live API keys only (SEC-0511-3 /
    QA-0511T2-2). Redacting the enumerated values by value hid real drift and printed
    `got "<set>"` for a merely malformed one."""
    return {mapping[name] for name in NODE_SECRET_VARS if mapping.get(name)}


def gather_mapping():
    """Read the 4 env values. Returns (mapping, failures). Never prints values."""
    mapping, failures = {}, []
    for name in REQUIRED_VARS:
        if name not in os.environ:
            failures.append(f"FAIL missing variable {name}")
            continue
        v = os.environ[name]
        if not VALUE_RE.match(v):
            failures.append(f"FAIL invalid value for {name}")
            continue
        mapping[name] = v
    return mapping, failures


def apply_spike(rendered):
    """Rewrite the spike-only lines for --profile spike (03.4.1-T1, extended by
    05.4.1-T1 with the `[kokoro]` TTS-stub block).

    The image upstream becomes the local stub, the image key list becomes
    empty (no Authorization header, I15), the prompt template is cleared so
    the raw term is sent (I3) and prompt_sha256 maps 1:1 to the fixture, the
    LLM upstream openai_base_url also becomes the stub so a stray LLM call
    hits a 404 and never a paid upstream, and MPT listens on
    SPIKE_LISTEN_PORT so it can run beside the live 8080 instance. In the
    [kokoro] section the TTS upstream becomes tts_stub.py on SPIKE_TTS_BASE_URL
    with `api_key = ""` (MPT then sends no Authorization header, D2/D12) —
    the proxy is bypassed for the spike so TTS modes (e500/abort/empty/sine/slow)
    are scriptable. Exits 1 when the template drifts so the profile cannot go
    silently half-applied.
    """
    out = []
    n_base = n_keys = n_tpl = n_port = n_llm = 0
    n_tts_base = n_tts_key = 0
    in_kokoro = False
    for line in rendered.splitlines():
        stripped = line.lstrip()
        section = re.match(r"^\[([a-zA-Z0-9_.]+)\]", stripped)
        if section:
            in_kokoro = section.group(1) == "kokoro"
        if line.startswith("openai_image_base_url ="):
            line = f'openai_image_base_url = "{SPIKE_IMAGE_BASE_URL}"  # spike: local stub (03.4.1-T1)'
            n_base += 1
        elif line.startswith("openai_image_api_keys ="):
            line = "openai_image_api_keys = []  # spike: no auth header (03.4.1-T1, I15)"
            n_keys += 1
        elif line.startswith("openai_image_prompt_template ="):
            line = 'openai_image_prompt_template = ""  # spike: raw term (03.4.1-T1, I3)'
            n_tpl += 1
        elif line.startswith("openai_base_url ="):
            line = f'openai_base_url = "{SPIKE_IMAGE_BASE_URL}"  # spike: a stray LLM call hits a 404, never a paid upstream (03.4.1-T1)'
            n_llm += 1
        elif line.startswith("listen_port ="):
            line = f"listen_port = {SPIKE_LISTEN_PORT}  # spike: beside the live 8080 MPT (03.4.1-T1)"
            n_port += 1
        elif in_kokoro and line.startswith("base_url ="):
            line = f'base_url = "{SPIKE_TTS_BASE_URL}"  # spike: tts_stub.py (05.4.1-T1, D2/D12)'
            n_tts_base += 1
        elif in_kokoro and line.startswith("api_key ="):
            line = 'api_key = ""  # spike: stub sends no key (05.4.1-T1); MPT omits the Authorization header'
            n_tts_key += 1
        out.append(line)
    if (n_base, n_keys, n_tpl, n_port, n_llm) != (1, 1, 1, 1, 1):
        eprint(
            "FAIL spike profile: template must hold exactly one openai_image_base_url,"
            f" openai_image_api_keys, openai_image_prompt_template, openai_base_url and"
            f" listen_port line (got image={n_base} keys={n_keys} template={n_tpl}"
            f" port={n_port} openai={n_llm})"
        )
        sys.exit(1)
    if (n_tts_base, n_tts_key) != (1, 1):
        eprint(
            "FAIL spike profile: [kokoro] must hold exactly one base_url and one api_key"
            f" line (got base_url={n_tts_base} api_key={n_tts_key})"
        )
        sys.exit(1)
    return "\n".join(out) + ("\n" if rendered.endswith("\n") else "")


def guards(cfg, allow_direct=False, profile="normal"):
    """Every guard prints PASS/FAIL (names only). Returns list of failed names."""
    app = cfg.get("app", {})
    whisper = cfg.get("whisper", {})
    kokoro = cfg.get("kokoro", {})
    url_ok = (TAP_URL, DIRECT_URL) if allow_direct else (TAP_URL,)
    spike = profile == "spike"

    # In the spike profile the image upstream is the local stub (18003) and the
    # image key list must be empty (I15). Everything else is identical to the
    # normal guards so the spike exercises the same shape.
    if spike:
        port_ok = cfg.get("listen_port") == SPIKE_LISTEN_PORT
        img_url_ok = app.get("openai_image_base_url") == SPIKE_IMAGE_BASE_URL
        img_url_want = SPIKE_IMAGE_BASE_URL
        llm_url_ok = app.get("openai_base_url") == SPIKE_IMAGE_BASE_URL
        llm_url_want = SPIKE_IMAGE_BASE_URL
        img_keys_ok = app.get("openai_image_api_keys") == []
        img_keys_want = "[] (empty: stub sends no Authorization)"
    else:
        port_ok = cfg.get("listen_port") == 8080
        img_url_ok = app.get("openai_image_base_url") in url_ok
        img_url_want = " ".join(url_ok)
        llm_url_ok = app.get("openai_base_url") in url_ok
        llm_url_want = " ".join(url_ok)
        img_keys_ok = (
            isinstance(app.get("openai_image_api_keys"), list)
            and len(app["openai_image_api_keys"]) == 1
            and isinstance(app["openai_image_api_keys"][0], str)
            and len(app["openai_image_api_keys"][0]) > 0
        )
        img_keys_want = "exactly 1 non-empty"

    checks = [
        ("listen_host", cfg.get("listen_host") == "127.0.0.1", '"127.0.0.1"'),
        ("listen_port", port_ok, str(SPIKE_LISTEN_PORT) if spike else "8080"),
        ("no_host_in_app", "listen_host" not in app, "absent"),
        ("log_level", cfg.get("log_level") == "INFO", '"INFO"'),
        ("api_key", isinstance(app.get("api_key"), str) and len(app["api_key"]) >= 32, "set, len>=32"),
        ("video_source", app.get("video_source") == "openai_image", '"openai_image"'),
        ("pexels_api_keys", app.get("pexels_api_keys") == [], "[]"),
        ("pixabay_api_keys", app.get("pixabay_api_keys") == [], "[]"),
        ("llm_provider", app.get("llm_provider") == "openai", '"openai"'),
        ("openai_api_key", isinstance(app.get("openai_api_key"), str) and len(app["openai_api_key"]) > 0, "set"),
        ("openai_model_name", bool(app.get("openai_model_name")), "non-empty"),
        ("openai_image_model", bool(app.get("openai_image_model")), "non-empty"),
        ("openai_base_url", llm_url_ok, llm_url_want),
        ("openai_image_base_url", img_url_ok, img_url_want),
        ("openai_image_api_keys", img_keys_ok, img_keys_want),
        ("subtitle_provider", app.get("subtitle_provider") == "whisper", '"whisper"'),
        ("whisper.model_size", whisper.get("model_size") == "large-v3-turbo", '"large-v3-turbo"'),
        ("enable_redis", app.get("enable_redis") is False, "false"),
        ("max_concurrent_tasks", app.get("max_concurrent_tasks") == 1, "1"),
        ("upload_post_enabled", app.get("upload_post_enabled") is False, "false"),
        ("upload_post_auto_upload", app.get("upload_post_auto_upload") is False, "false"),
        ("upload_post_api_key", app.get("upload_post_api_key") == "", '""'),
        ("kokoro.base_url", kokoro.get("base_url") == (SPIKE_TTS_BASE_URL if spike else "http://127.0.0.1:8001/v1"), SPIKE_TTS_BASE_URL if spike else "http://127.0.0.1:8001/v1"),
        ("kokoro.api_key", isinstance(kokoro.get("api_key"), str) and (kokoro["api_key"] == "" if spike else len(kokoro["api_key"]) > 0), '"" (stub)' if spike else "set"),
        ("kokoro.voices", kokoro.get("voices") == [], "[]"),
    ]
    failed = []
    for name, ok, expected in checks:
        if ok:
            eprint(f"PASS {name}")
        else:
            eprint(f"FAIL {name}: expected {expected}")
            failed.append(name)
    return failed


def atomic_write(path, data):
    """Write `data` (str) to `path` atomically with mode 0600."""
    outdir = os.path.dirname(path) or "."
    old_umask = os.umask(0o077)
    try:
        fd, tmp = tempfile.mkstemp(dir=outdir, prefix=".config.toml.")
        try:
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as fh:
                fh.write(data)
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(tmp, path)
            dfd = os.open(outdir, os.O_RDONLY)
            try:
                os.fsync(dfd)
            finally:
                os.close(dfd)
        except BaseException:
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise
    finally:
        os.umask(old_umask)

def reject_env_file(args):
    """--env-file belongs to the node profile only; refuse it elsewhere (exit 2)."""
    if getattr(args, "env_file", "") and args.profile != "node":
        eprint("FAIL usage: --env-file is only used by --profile node (05.1.1-T2)")
        sys.exit(2)


def reject_node_flags(args):
    """--allow-direct-openai has no D2 row: the node reaches images through the relay
    only, so the flag is a usage error there (CRV-0511-1)."""
    if args.profile == "node" and getattr(args, "allow_direct_openai", False):
        eprint("FAIL usage: --allow-direct-openai is not used by --profile node (CRV-0511-1)")
        sys.exit(2)


def render(args):
    reject_env_file(args)
    reject_node_flags(args)
    if args.profile == "node":
        return render_node(args)
    env_checks(args.out)
    tpl, ids = load_template(args.template)
    if ids != set(REQUIRED_VARS):
        eprint(f"FAIL template placeholders: expected {sorted(REQUIRED_VARS)}, found {sorted(ids)}")
        sys.exit(1)
    mapping, failures = gather_mapping()
    for f in failures:
        eprint(f)
    if failures:
        sys.exit(1)
    rendered = substitute_template(tpl, mapping)
    if args.profile == "spike":
        rendered = apply_spike(rendered)
    try:
        cfg = tomllib.loads(rendered)
    except tomllib.TOMLDecodeError as exc:
        eprint(f"FAIL toml: rendered config does not parse: {exc}")
        sys.exit(1)
    failed = guards(cfg, allow_direct=args.allow_direct_openai, profile=args.profile)
    if failed:
        sys.exit(1)
    return drift_and_write(args, rendered, {v for v in mapping.values() if v})


def drift_and_write(args, rendered, mapping_values):
    """Config-drift report (report only, never blocks) + atomic write of `args.out`
    and its redacted state file (01.2.2-T2; shared with the node profile).

    `mapping_values` is the caller's redaction set: every live secret for mac/spike,
    the two API keys only for node (QA-0511T2-2)."""
    new_canon = canonical(rendered, mapping_values, from_text=True)

    # Config drift (report only, never blocks).
    if os.path.exists(args.out):
        try:
            old_out = canonical(args.out, mapping_values)
        except (tomllib.TOMLDecodeError, OSError, UnicodeDecodeError):
            eprint("WARN existing config.toml unparseable")
            old_out = None
        if old_out is not None:
            old_state, warn = read_state(args.state)
            if warn:
                eprint(warn)
            if old_state is not None:
                last = redact_state_lines(old_state, mapping_values)
                if old_state != old_out:
                    eprint("WARN config.toml changed since last render (config drift)")
                    for line in diff_lines(last, old_out, "last-render", "current"):
                        eprint(line)
                if old_state != new_canon:
                    eprint("INFO template change")
                    for line in diff_lines(last, new_canon, "last-render", "new-render"):
                        eprint(line)
    atomic_write(args.out, rendered)
    atomic_write(args.state, "\n".join(new_canon) + "\n")
    eprint(f"rendered {args.out} (0600)")
    return 0


def check(args):
    reject_env_file(args)
    reject_node_flags(args)
    if args.profile == "node":
        return check_node(args)
    env_checks(args.out)
    cfg, mode_ok = read_config(args.out)
    if cfg is None:
        return 1
    failed_names = [] if mode_ok else ["mode"]
    failed_names += guards(
        cfg, allow_direct=args.allow_direct_openai, profile=args.profile
    )
    if failed_names:
        return 1
    eprint("check ok: config.toml passes every guard")
    return 0


# --- node profile (05.1.1-T2, 05 README D2) ---------------------------------


def read_env_file(path):
    """KEY=VALUE lines of a secret env file -> {name: value}. No value is ever printed.

    Exit 2 (usage/environment, 01 D9) when the file is absent, not a regular file, not
    owned by the effective user (or root), not 0600 (0640 only for a root-owned file —
    what plan 11 deploys as /etc/nts/mpt.env), unreadable, larger than ENV_FILE_MAX, or
    has a line that is not `KEY=VALUE`: the file holds the node's secrets, so an
    unusable one must stop a render (05.1.1-T2). SEC-0511-2: the path is opened once
    with O_NOFOLLOW and every check runs on that fd, so a symlink swapped in after the
    check cannot be read; lines split on '\\n' only (no splitlines(): no other Unicode
    line boundary may inject a line) and a trailing '\\r' is dropped.
    """
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    except OSError as exc:
        if exc.errno == errno.ELOOP:
            eprint("FAIL env: --env-file must be a regular file (not a symlink or directory)")
        else:
            eprint(f"FAIL env: --env-file unreadable: {exc.strerror or exc}")
        sys.exit(2)
    try:
        st = os.fstat(fd)
        mode = stat.S_IMODE(st.st_mode)
        if not stat.S_ISREG(st.st_mode):
            eprint("FAIL env: --env-file must be a regular file (not a symlink or directory)")
            sys.exit(2)
        if st.st_uid not in (0, os.geteuid()):
            eprint("FAIL env: --env-file must be owned by the effective user, or by root")
            sys.exit(2)
        # 0640 is what plan 11 deploys as root; a file the renderer owns must be 0600.
        allowed = (0o600, 0o640) if st.st_uid == 0 else (0o600,)
        if mode not in allowed:
            eprint(
                "FAIL env: --env-file must be 0600 (or 0640 for the root-owned"
                f" {NODE_ENV_FILE}), got {mode:04o}"
            )
            sys.exit(2)
        with os.fdopen(fd, "rb") as fh:
            fd = -1  # fdopen owns it now
            data = fh.read(ENV_FILE_MAX + 1)
    finally:
        if fd >= 0:
            os.close(fd)
    if len(data) > ENV_FILE_MAX:
        eprint(f"FAIL env: --env-file larger than {ENV_FILE_MAX} bytes")
        sys.exit(2)
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        eprint("FAIL env: --env-file is not UTF-8")
        sys.exit(2)
    values = {}
    for lineno, raw in enumerate(text.split("\n"), 1):
        line = raw.rstrip("\r").strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[len("export "):].lstrip()
        name, sep, value = line.partition("=")
        name = name.strip()
        if not sep or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name):
            eprint(f"FAIL env: --env-file line {lineno}: expected KEY=VALUE")
            sys.exit(2)
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
            value = value[1:-1]
        values[name] = value
    return values


def node_environment(args):
    """The node variables: the process environment, overridden by --env-file.

    11 CR-4 fixes the node's file as NODE_ENV_FILE (/etc/nts/mpt.env); this tool only
    reads it when the caller passes --env-file, so nothing is picked up implicitly.
    """
    env = {name: os.environ[name] for name in NODE_REQUIRED_VARS if name in os.environ}
    env_file = getattr(args, "env_file", "")
    if env_file:
        env.update(read_env_file(env_file))
    return env


def validate_node_env(env, require=True):
    """Node guard groups 1-2: required variables present, then their value sets (D2).

    Returns (mapping, failures). Every failure names the D2 key and the variable,
    never a value. `require=False` (--check) validates only the values supplied.
    """
    mapping, failures = {}, []

    def check(name, ok, expectation):
        value = env.get(name, "")
        if not value:
            if require:
                failures.append(f"FAIL missing variable {name}")
            return
        if not ok(value):
            failures.append(f"FAIL {expectation} (from {name})")
            return
        mapping[name] = value

    check(
        "NTS_MPT_API_KEY",
        lambda v: bool(VALUE_RE.match(v)) and len(v) >= NODE_MIN_API_KEY_LEN,
        f"api_key: expected set, len>={NODE_MIN_API_KEY_LEN}",
    )
    check(
        "NTS_PROXY_API_KEY",
        lambda v: bool(VALUE_RE.match(v)),
        "kokoro.api_key: expected set",
    )
    check(
        "NTS_MPT_MAX_CONCURRENT",
        lambda v: bool(NODE_MAX_CONCURRENT_RE.match(v)),
        "max_concurrent_tasks: expected an integer 1-8",
    )
    check(
        "NTS_MPT_VIDEO_CODEC",
        lambda v: v in NODE_VIDEO_CODECS,
        "video_codec: expected " + "|".join(NODE_VIDEO_CODECS),
    )
    check(
        "NTS_WHISPER_DEVICE",
        lambda v: v in NODE_WHISPER_DEVICES,
        "whisper.device: expected " + "|".join(NODE_WHISPER_DEVICES) + " (D15 P5)",
    )
    check(
        "NTS_WHISPER_COMPUTE",
        lambda v: v in NODE_WHISPER_COMPUTES,
        "whisper.compute_type: expected " + "|".join(NODE_WHISPER_COMPUTES) + " (D15 P5)",
    )
    return mapping, failures


def node_get(cfg, path):
    """The value at a dotted path, or NODE_ABSENT when the key is not present."""
    cur = cfg
    for part in path:
        if not isinstance(cur, dict) or part not in cur:
            return NODE_ABSENT
        cur = cur[part]
    return cur


def is_int(value):
    """True for a real int (a bool is an int in Python but never a task limit)."""
    return isinstance(value, int) and not isinstance(value, bool)


def is_empty_list(value):
    return isinstance(value, list) and not value


def node_guard_table():
    """The D2 rows as (name, path, ok(value), expected). One guard per row.

    D2 has no DIRECT_URL row: the node reaches image generation through the relay
    only, and `--allow-direct-openai` is refused for this profile (CRV-0511-1).
    """
    return (
        ("log_level", ("log_level",), lambda v: v == "INFO", '"INFO" (01 D5)'),
        ("listen_host", ("listen_host",), lambda v: v == "127.0.0.1", '"127.0.0.1" (F12)'),
        ("listen_port", ("listen_port",), lambda v: is_int(v) and v == NODE_LISTEN_PORT, str(NODE_LISTEN_PORT)),
        ("no_host_in_app", ("app", "listen_host"), lambda v: v is NODE_ABSENT, "absent from [app]"),
        ("api_key", ("app", "api_key"), lambda v: isinstance(v, str) and len(v) >= NODE_MIN_API_KEY_LEN, f"set, len>={NODE_MIN_API_KEY_LEN} (C2)"),
        ("video_source", ("app", "video_source"), lambda v: v == "openai_image", '"openai_image" (03 D4)'),
        ("pexels_api_keys", ("app", "pexels_api_keys"), is_empty_list, "[]"),
        ("pixabay_api_keys", ("app", "pixabay_api_keys"), is_empty_list, "[]"),
        ("openai_image_base_url", ("app", "openai_image_base_url"), lambda v: v == NODE_IMAGE_BASE_URL, f'"{NODE_IMAGE_BASE_URL}" (03 D5)'),
        ("openai_image_api_keys", ("app", "openai_image_api_keys"), is_empty_list, "[] (the relay holds the key, 03 F09)"),
        ("openai_image_model", ("app", "openai_image_model"), lambda v: v == NODE_IMAGE_MODEL, f'"{NODE_IMAGE_MODEL}" (DECISIONS §3)'),
        ("openai_image_size", ("app", "openai_image_size"), lambda v: v == "", '"" (03 D4)'),
        ("openai_image_prompt_template", ("app", "openai_image_prompt_template"), lambda v: v == "", '"" (03 D4/C6)'),
        ("material_directory", ("app", "material_directory"), lambda v: v == "", '"" (C6)'),
        ("endpoint", ("app", "endpoint"), lambda v: v == "", '"" (C6)'),
        ("twelvelabs_rerank_terms", ("app", "twelvelabs_rerank_terms"), lambda v: v is False, "false (03 I12)"),
        ("llm_provider", ("app", "llm_provider"), lambda v: v == "openai", '"openai"'),
        ("openai_api_key", ("app", "openai_api_key"), lambda v: v == "", '"" (C19: the node never calls an LLM)'),
        ("openai_base_url", ("app", "openai_base_url"), lambda v: v == "", '"" (C19)'),
        ("subtitle_provider", ("app", "subtitle_provider"), lambda v: v == "whisper", '"whisper"'),
        ("enable_redis", ("app", "enable_redis"), lambda v: v is False, "false (F06)"),
        ("max_concurrent_tasks", ("app", "max_concurrent_tasks"), lambda v: is_int(v) and 1 <= v <= 8, "an integer 1-8 (11 sizing)"),
        ("max_queued_tasks", ("app", "max_queued_tasks"), lambda v: is_int(v) and v == 0, "0 (F07)"),
        ("video_codec", ("app", "video_codec"), lambda v: v in NODE_VIDEO_CODECS, "|".join(NODE_VIDEO_CODECS) + " (C14, 01 CR-9)"),
        ("upload_post_enabled", ("app", "upload_post_enabled"), lambda v: v is False, "false"),
        ("upload_post_api_key", ("app", "upload_post_api_key"), lambda v: v == "", '""'),
        ("upload_post_auto_upload", ("app", "upload_post_auto_upload"), lambda v: v is False, "false"),
        ("whisper.model_size", ("whisper", "model_size"), lambda v: v == "large-v3-turbo", '"large-v3-turbo"'),
        ("whisper.device", ("whisper", "device"), lambda v: v in NODE_WHISPER_DEVICES, "|".join(NODE_WHISPER_DEVICES) + " (D15 P5)"),
        ("whisper.compute_type", ("whisper", "compute_type"), lambda v: v in NODE_WHISPER_COMPUTES, "|".join(NODE_WHISPER_COMPUTES) + " (D15 P5)"),
        ("whisper.initial_prompt", ("whisper", "initial_prompt"), lambda v: v is NODE_ABSENT, "absent (F16/C15)"),
        ("kokoro.base_url", ("kokoro", "base_url"), lambda v: v == NODE_KOKORO_BASE_URL, f'"{NODE_KOKORO_BASE_URL}" (04 D10)'),
        ("kokoro.api_key", ("kokoro", "api_key"), lambda v: isinstance(v, str) and len(v) > 0, "set (04 D10)"),
        ("kokoro.model_id", ("kokoro", "model_id"), lambda v: v == NODE_KOKORO_MODEL_ID, f'"{NODE_KOKORO_MODEL_ID}" (04 D10)'),
        ("kokoro.voices", ("kokoro", "voices"), is_empty_list, "[]"),
    )


def node_guards(cfg, mapping_values=()):
    """Node guard groups 3-4: every D2 row exists, is typed and holds its value.

    Prints one PASS/FAIL line per row (values redacted: a secret prints as `<set>`)
    and returns the failed names.
    """
    failed = []
    for name, path, ok, expected in node_guard_table():
        value = node_get(cfg, path)
        if ok(value):
            eprint(f"PASS {name}")
            continue
        if value is NODE_ABSENT:
            eprint(f"FAIL {name}: missing from the rendered config (expected {expected})")
        else:
            eprint(
                f"FAIL {name}: expected {expected}, got"
                f" {json.dumps(redact_value(name, value, mapping_values), ensure_ascii=False)}"
            )
        failed.append(name)
    return failed


def render_node(args):
    """Render the D2 node config: exit 0 ok · 1 guard/value failure (nothing written)
    · 2 usage/environment (01 D9, 05.1.1-T2)."""
    env_checks(args.out, require_mpt_root=False)
    tpl, ids = load_template(args.template)
    if ids != set(NODE_REQUIRED_VARS):
        eprint(
            f"FAIL template placeholders: expected {sorted(NODE_REQUIRED_VARS)},"
            f" found {sorted(ids)}"
        )
        sys.exit(1)
    mapping, failures = validate_node_env(node_environment(args))
    for f in failures:
        eprint(f)
    if failures:
        sys.exit(1)
    rendered = substitute_template(tpl, mapping)
    try:
        cfg = tomllib.loads(rendered)
    except tomllib.TOMLDecodeError as exc:
        eprint(f"FAIL toml: rendered config does not parse: {exc}")
        sys.exit(1)
    secrets = node_secret_values(mapping)
    if node_guards(cfg, secrets):
        sys.exit(1)
    return drift_and_write(args, rendered, secrets)


def check_node(args):
    """`--check --profile node`: validate an existing file against the D2 table, write
    nothing. Exit 0/1. Values exported in the environment (or in --env-file) are
    validated too, so an operator sees a bad variable before the render."""
    env_checks(args.out, require_mpt_root=False)
    cfg, mode_ok = read_config(args.out)
    if cfg is None:
        return 1
    failed_names = [] if mode_ok else ["mode"]
    mapping_values = set()
    env = node_environment(args)
    if env:
        mapping, env_failures = validate_node_env(env, require=False)
        for f in env_failures:
            eprint(f)
        if env_failures:
            failed_names.append("env")
        mapping_values = node_secret_values(mapping)
    failed_names += node_guards(cfg, mapping_values)
    if failed_names:
        return 1
    eprint(f"check ok: {args.out} passes every D2 node guard")
    return 0


def swap_env(env_extra, unset=()):
    """Self-test helper: clear both NTS_* variable sets, apply `env_extra`, drop
    `unset`; returns the saved environment for restore_env()."""
    saved = {k: os.environ.get(k) for k in tuple(REQUIRED_VARS) + NODE_REQUIRED_VARS}
    for k in saved:
        os.environ.pop(k, None)
    for k, v in (env_extra or {}).items():
        if v is not None:
            os.environ[k] = v
    for k in unset:
        os.environ.pop(k, None)
    return saved


def restore_env(saved):
    for k, v in saved.items():
        if v is None:
            os.environ.pop(k, None)
        else:
            os.environ[k] = v


def run_render(
    template, out, state, env_extra, unset=(), allow_direct=False, profile="normal",
    env_file="",
):
    """Self-test helper: run the render logic in-process. Returns exit code."""
    saved = swap_env(env_extra, unset)
    args = argparse.Namespace(
        template=template, out=out, state=state, allow_direct_openai=allow_direct,
        profile=profile, env_file=env_file,
    )
    try:
        code = render(args)
    except SystemExit as e:
        code = e.code if isinstance(e.code, int) else 1
    finally:
        restore_env(saved)
    return code


def run_check(out, env_extra=None, unset=(), profile="node", env_file="", allow_direct=False):
    """Self-test helper: run the --check logic in-process. Returns exit code."""
    saved = swap_env(env_extra, unset)
    args = argparse.Namespace(
        template="", out=out, state="", allow_direct_openai=allow_direct, profile=profile,
        env_file=env_file,
    )
    try:
        code = check(args)
    except SystemExit as e:
        code = e.code if isinstance(e.code, int) else 1
    finally:
        restore_env(saved)
    return code


def self_test():
    import contextlib
    import hashlib
    import io

    failures = []

    def expect(name, cond):
        if cond:
            print(f"PASS selftest:{name}")
        else:
            print(f"FAIL selftest:{name}")
            failures.append(name)

    repo = repo_root()
    tpl_path = os.path.join(repo, "worker-node", "mpt", "config.template.toml")
    vals = {n: secrets.token_hex(32) for n in REQUIRED_VARS}

    with tempfile.TemporaryDirectory() as td:
        root = os.path.join(td, "mpt")
        os.makedirs(os.path.join(root, "resource", "songs"), exist_ok=True)
        open(os.path.join(root, "main.py"), "w").close()
        out = os.path.join(root, "config.toml")
        state = os.path.join(td, "state.toml")

        # (a) valid -> 0, mode 600
        code = run_render(tpl_path, out, state, vals)
        mode = stat.S_IMODE(os.stat(out).st_mode) if os.path.exists(out) else None
        expect("valid", code == 0 and mode == 0o600)
        sha = hashlib.sha256(open(out, "rb").read()).hexdigest()

        def unchanged():
            return hashlib.sha256(open(out, "rb").read()).hexdigest() == sha

        # (b) missing variable -> 1
        bad = dict(vals)
        code = run_render(tpl_path, out, state, bad, unset=("NTS_PROXY_API_KEY",))
        expect("missing-var", code == 1 and unchanged())

        # (c) empty value -> 1
        bad = dict(vals)
        bad["NTS_MPT_API_KEY"] = ""
        code = run_render(tpl_path, out, state, bad)
        expect("empty-key", code == 1 and unchanged())

        # (d) listen_host 0.0.0.0 -> 1
        badtpl = os.path.join(td, "bad.toml")
        open(badtpl, "w").write(
            open(tpl_path).read().replace('listen_host = "127.0.0.1"', 'listen_host = "0.0.0.0"')
        )
        code = run_render(badtpl, out, state, vals)
        expect("bad-host", code == 1 and unchanged())

        # (e) value with a quote -> 1
        bad = dict(vals)
        bad["NTS_MPT_API_KEY"] = 'abc"def' + "x" * 16
        code = run_render(tpl_path, out, state, bad)
        expect("bad-chars", code == 1 and unchanged())

        # (f) guards() directly with api_key = "" -> FAIL api_key
        cfg = tomllib.loads(
            string.Template(open(tpl_path).read()).substitute(vals)
        )
        cfg["app"]["api_key"] = ""
        failed = guards(cfg)
        expect("guards-empty-key", "api_key" in failed)

        # (g) --out under /Volumes/... outside NTS_HOME -> 2
        vol_out = "/Volumes/__nts_selftest_nonexistent__/mpt/config.toml"
        code = run_render(tpl_path, vol_out, state, vals)
        expect("volumes-out", code == 2)

        # (h) spike profile -> stub URL, empty keys, port 18080; the same config
        # must fail the normal guard set (start-mpt-api.sh refuses to start the
        # normal profile while the spike file is in place).
        spike_out = os.path.join(root, "config.spike.toml")
        spike_state = os.path.join(td, "spike-state.toml")
        code = run_render(tpl_path, spike_out, spike_state, vals, profile="spike")
        expect("spike-render", code == 0)
        if code == 0:
            cfg = tomllib.loads(open(spike_out, "rb").read().decode("utf-8"))
            expect("spike-llm-url-stub", cfg["app"]["openai_base_url"] == SPIKE_IMAGE_BASE_URL)
            expect("spike-image-url", cfg["app"]["openai_image_base_url"] == SPIKE_IMAGE_BASE_URL)
            expect("spike-keys-empty", cfg["app"]["openai_image_api_keys"] == [])
            expect("spike-port", cfg["listen_port"] == SPIKE_LISTEN_PORT)
            expect("spike-template-empty", cfg["app"]["openai_image_prompt_template"] == "")
            expect("spike-fails-normal", "openai_image_base_url" in guards(cfg))

        # --- node profile (05.1.1-T2, 05 README D2) -------------------------
        node_tpl = os.path.join(repo, NODE_TEMPLATE_RELPATH)
        node_out = os.path.join(root, "config.node.toml")
        node_state = os.path.join(td, "node-state.toml")
        node_vals = {
            "NTS_MPT_API_KEY": secrets.token_hex(32),
            "NTS_PROXY_API_KEY": secrets.token_hex(32),
            "NTS_MPT_MAX_CONCURRENT": "2",
            "NTS_MPT_VIDEO_CODEC": "libx264",
            "NTS_WHISPER_DEVICE": "cuda",
            "NTS_WHISPER_COMPUTE": "float16",
        }

        def node_render(extra=None, unset=(), template=node_tpl, env_file=""):
            values = dict(node_vals)
            values.update(extra or {})
            return run_render(
                template, node_out, node_state, values, unset=unset, profile="node",
                env_file=env_file,
            )

        def capture(fn):
            """Run fn() with stderr captured. Returns (exit code, stderr text)."""
            buf = io.StringIO()
            with contextlib.redirect_stderr(buf):
                code = fn()
            return code, buf.getvalue()

        def write_0600(path, text):
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(text)
            os.chmod(path, 0o600)
            return path

        def derived(name, old, new):
            """A 0600 copy of the canonical node config with `old` replaced."""
            path = os.path.join(root, f"node-{name}.toml")
            text = node_good_text.replace(old, new)
            expect(f"node-anchor-{name}", text != node_good_text)
            return write_0600(path, text)

        def derived_template(name, old, new):
            """A copy of the node template with `old` replaced."""
            path = os.path.join(td, f"node-{name}.template.toml")
            text = open(node_tpl, "r", encoding="utf-8").read()
            newtext = text.replace(old, new)
            expect(f"node-anchor-template-{name}", newtext != text)
            return write_0600(path, newtext)

        # (a) valid -> 0, mode 600, D2 rows typed, no secret in the guard output
        code, err = capture(node_render)
        expect("node-valid", code == 0)
        node_good_text = open(node_out, "r", encoding="utf-8").read()
        if code == 0:
            ncfg = tomllib.loads(node_good_text)
            expect("node-mode-600", stat.S_IMODE(os.stat(node_out).st_mode) == 0o600)
            expect(
                "node-int-types",
                is_int(ncfg["app"]["max_concurrent_tasks"])
                and ncfg["app"]["max_concurrent_tasks"] == 2
                and is_int(ncfg["app"]["max_queued_tasks"])
                and ncfg["app"]["max_queued_tasks"] == 0
                and ncfg["listen_host"] == "127.0.0.1",
            )
            expect(
                "node-whisper-cuda",
                ncfg["whisper"]["device"] == "cuda"
                and ncfg["whisper"]["compute_type"] == "float16",
            )
            expect("node-initial-prompt-absent", "initial_prompt" not in ncfg["whisper"])
            expect(
                "node-guard-output-has-no-secret",
                node_vals["NTS_MPT_API_KEY"] not in err
                and node_vals["NTS_PROXY_API_KEY"] not in err,
            )
        node_sha = hashlib.sha256(open(node_out, "rb").read()).hexdigest()

        def node_unchanged():
            return hashlib.sha256(open(node_out, "rb").read()).hexdigest() == node_sha

        # (b) --check: the valid file -> 0; max_queued_tasks = 5 -> 1, key named
        expect("node-check-ok", run_check(node_out) == 0)
        code, err = capture(lambda: run_check(derived("queued", "max_queued_tasks = 0", "max_queued_tasks = 5")))
        expect("node-check-queued-5", code == 1 and "max_queued_tasks" in err)

        # (c) openai_api_key non-empty in the file -> 1, key named
        code, err = capture(
            lambda: run_check(derived("openai-key", 'openai_api_key = ""', 'openai_api_key = "x"'))
        )
        expect("node-check-openai-key-set", code == 1 and "openai_api_key" in err)

        # (d) listen_host = 0.0.0.0 in the template -> 1, key named, nothing written
        code, err = capture(
            lambda: node_render(
                template=derived_template(
                    "bad-host", 'listen_host = "127.0.0.1"', 'listen_host = "0.0.0.0"'
                )
            )
        )
        expect("node-bad-listen-host", code == 1 and "listen_host" in err and node_unchanged())

        # (e) codec / concurrency value sets -> 1, key named, nothing written
        code, err = capture(lambda: node_render({"NTS_MPT_VIDEO_CODEC": "h264_videotoolbox"}))
        expect("node-codec-videotoolbox", code == 1 and "video_codec" in err and node_unchanged())
        for raw in ("0", "9", "abc"):
            code, err = capture(
                lambda raw=raw: node_render({"NTS_MPT_MAX_CONCURRENT": raw})
            )
            expect(
                f"node-max-concurrent-{raw}",
                code == 1 and "max_concurrent_tasks" in err and node_unchanged(),
            )
        code, err = capture(lambda: node_render({"NTS_MPT_MAX_CONCURRENT": "8"}))
        expect("node-max-concurrent-8", code == 0)
        expect("node-max-concurrent-8-int", node_get(tomllib.loads(
            open(node_out, "r", encoding="utf-8").read()), ("app", "max_concurrent_tasks")) == 8)
        code = node_render()

        # (f) [whisper] initial_prompt present -> 1, key named
        code, err = capture(
            lambda: run_check(derived("initial-prompt", "[whisper]", '[whisper]\ninitial_prompt = "x"'))
        )
        expect("node-check-initial-prompt", code == 1 and "whisper.initial_prompt" in err)

        # (g) whisper value sets (D15 P5: cuda + float16/int8_float16 only) -> 1
        code, err = capture(lambda: node_render({"NTS_WHISPER_DEVICE": "mps"}))
        expect("node-whisper-device-mps", code == 1 and "whisper.device" in err and node_unchanged())
        code, err = capture(lambda: node_render({"NTS_WHISPER_DEVICE": "cpu"}))
        expect("node-whisper-device-cpu", code == 1 and "whisper.device" in err and node_unchanged())
        code, err = capture(lambda: node_render({"NTS_WHISPER_COMPUTE": "int8"}))
        expect("node-whisper-compute-int8", code == 1 and "whisper.compute_type" in err and node_unchanged())
        code, err = capture(lambda: node_render({"NTS_WHISPER_COMPUTE": "int8_float16"}))
        expect("node-whisper-compute-int8-float16", code == 0)
        code = node_render()

        # (h) a required variable absent -> 1, naming the variable and nothing else
        code, err = capture(lambda: node_render(unset=("NTS_PROXY_API_KEY",)))
        expect(
            "node-missing-proxy-key",
            code == 1 and "NTS_PROXY_API_KEY" in err and node_unchanged()
            and node_vals["NTS_MPT_API_KEY"] not in err,
        )

        # (i) max_concurrent_tasks quoted in the template -> TOML type guard, 1
        code, err = capture(
            lambda: node_render(
                template=derived_template(
                    "quoted",
                    "max_concurrent_tasks = ${NTS_MPT_MAX_CONCURRENT}",
                    'max_concurrent_tasks = "${NTS_MPT_MAX_CONCURRENT}"',
                )
            )
        )
        expect(
            "node-quoted-max-concurrent",
            code == 1 and "max_concurrent_tasks" in err and node_unchanged(),
        )

        # (j) a D2 row absent from the template -> 1 with "missing"
        code, err = capture(
            lambda: node_render(
                template=derived_template(
                    "missing-row", "max_queued_tasks = 0", "# max_queued_tasks: gone"
                )
            )
        )
        expect(
            "node-missing-row",
            code == 1 and "max_queued_tasks" in err and "missing" in err,
        )

        # (k) --env-file: values win over the environment; unusable file -> 2
        env_file = write_0600(
            os.path.join(td, "mpt.env"),
            "# node env sample\n" + "".join(f"{k}={v}\n" for k, v in node_vals.items()),
        )
        env_out = os.path.join(root, "config.node-envfile.toml")
        code = run_render(node_tpl, env_out, node_state, {}, profile="node", env_file=env_file)
        expect("node-env-file", code == 0)
        code = run_render(
            node_tpl, env_out, node_state, {"NTS_MPT_MAX_CONCURRENT": "7"}, profile="node",
            env_file=env_file,
        )
        expect(
            "node-env-file-wins",
            code == 0
            and node_get(
                tomllib.loads(open(env_out, "r", encoding="utf-8").read()),
                ("app", "max_concurrent_tasks"),
            ) == 2,
        )
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="node",
            env_file=os.path.join(td, "absent.env"),
        )
        expect("node-env-file-missing", code == 2)
        world_readable = write_0600(os.path.join(td, "mpt-open.env"), "NTS_MPT_API_KEY=x\n")
        os.chmod(world_readable, 0o644)
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="node", env_file=world_readable
        )
        expect("node-env-file-mode", code == 2)
        # SEC-0511-2: 0640 is legal only for a root-owned file (plan 11's deploy);
        # a file the renderer owns must be 0600 — 0640 used to be accepted.
        node_0640 = write_0600(os.path.join(td, "mpt-0640.env"), "".join(
            f"{k}={v}\n" for k, v in node_vals.items()))
        os.chmod(node_0640, 0o640)
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="node", env_file=node_0640
        )
        expect("node-env-file-0640-not-root", code == (0 if os.geteuid() == 0 else 2))
        node_0400 = write_0600(os.path.join(td, "mpt-0400.env"), "NTS_MPT_API_KEY=x\n")
        os.chmod(node_0400, 0o400)
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="node", env_file=node_0400
        )
        expect("node-env-file-0400", code == 2)
        crlf = write_0600(os.path.join(td, "mpt-crlf.env"), "".join(
            f"{k}={v}\r\n" for k, v in node_vals.items()))
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="node", env_file=crlf
        )
        expect("node-env-file-crlf", code == 0)
        too_big = write_0600(
            os.path.join(td, "mpt-big.env"),
            "".join(f"{k}={v}\n" for k, v in node_vals.items())
            + "#" + "x" * ENV_FILE_MAX + "\n",
        )
        code, err = capture(
            lambda: run_render(
                node_tpl, env_out, node_state, {}, profile="node", env_file=too_big
            )
        )
        expect("node-env-file-too-big", code == 2 and "larger than" in err)
        linked_env = os.path.join(td, "mpt-link.env")
        os.symlink(env_file, linked_env)
        code, err = capture(
            lambda: run_render(
                node_tpl, env_out, node_state, {}, profile="node", env_file=linked_env
            )
        )
        expect("node-env-file-symlink", code == 2 and "regular file" in err)
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="node",
            env_file=write_0600(os.path.join(td, "mpt-bad.env"), "NTS_MPT_API_KEY\n"),
        )
        expect("node-env-file-malformed", code == 2)
        code = run_render(
            node_tpl, env_out, node_state, {}, profile="normal", env_file=env_file
        )
        expect("node-env-file-refused-off-node", code == 2)

        # (k2) CRV-0511-1: --allow-direct-openai has no D2 row -> usage error, render
        # and check; the relay URL is the only one the table accepts.
        code, err = capture(
            lambda: run_render(
                node_tpl, node_out, node_state, node_vals, profile="node", allow_direct=True
            )
        )
        expect("node-allow-direct-refused-render", code == 2 and "usage" in err)
        code, err = capture(lambda: run_check(node_out, allow_direct=True))
        expect("node-allow-direct-refused-check", code == 2 and "usage" in err)
        direct = derived(
            "direct-url", f'openai_image_base_url = "{NODE_IMAGE_BASE_URL}"',
            f'openai_image_base_url = "{DIRECT_URL}"',
        )
        code, err = capture(lambda: run_check(direct))
        expect(
            "node-check-direct-url",
            code == 1 and "openai_image_base_url" in err and node_unchanged(),
        )

        # (k3) SEC-0511-3: `$` matched before a trailing newline, so "2\n" passed both
        # the value regex and the 1-8 range check.
        expect("value-re-newline", not VALUE_RE.match("a" * 32 + "\n"))
        expect("max-concurrent-re-newline", not NODE_MAX_CONCURRENT_RE.match("2\n"))
        code, err = capture(lambda: node_render({"NTS_MPT_MAX_CONCURRENT": "2\n"}))
        expect("node-max-concurrent-newline", code == 1 and node_unchanged())

        # (k4) QA-0511T2-2: only the two API keys are redacted, so the enumerated
        # values stay legible and a codec switch is real drift, not a false WARN.
        code = node_render()
        state_text = open(node_state, "r", encoding="utf-8").read()
        expect(
            "node-state-real-codec",
            code == 0 and 'app.video_codec = "libx264"' in state_text
            and 'whisper.device = "cuda"' in state_text,
        )
        code, err = capture(lambda: node_render({"NTS_MPT_VIDEO_CODEC": "h264_nvenc"}))
        expect(
            "node-codec-switch-no-false-drift",
            code == 0 and "changed since last render" not in err
            and 'app.video_codec = "h264_nvenc"' in err
            and 'app.video_codec = "<set>"' not in err,
        )
        code = node_render()
        expect("node-restored-after-codec", code == 0 and node_unchanged())

        # (k5) SEC-0511-1: an untrustworthy state file means "drift unknown" and no
        # diff at all — even when config.toml really drifted — and a secret written
        # into the state file is re-redacted before printing.
        code, err = capture(lambda: node_render())
        expect("node-state-anchor", code == 0 and "drift unknown" not in err)
        with open(node_out, "r", encoding="utf-8") as fh:
            drifted = fh.read().replace(
                'video_codec = "libx264"', 'video_codec = "h264_nvenc"'
            )
        expect("node-state-anchor-drift", drifted != node_good_text)
        write_0600(node_out, drifted)
        os.chmod(node_state, 0o644)
        code, err = capture(lambda: node_render())
        expect(
            "node-state-mode-warn",
            code == 0 and "drift unknown" in err
            and "changed since last render" not in err
            and "-app.video_codec" not in err,
        )
        expect("node-state-mode-warn-restores", node_unchanged())
        code, err = capture(lambda: node_render())
        expect("node-state-mode-restored", code == 0 and "drift unknown" not in err)
        linked_state = os.path.join(td, "node-state-link.toml")
        if os.path.lexists(linked_state):
            os.unlink(linked_state)
        os.symlink(node_state, linked_state)
        code, err = capture(
            lambda: run_render(node_tpl, node_out, linked_state, node_vals, profile="node")
        )
        expect("node-state-symlink-warn", code == 0 and "drift unknown" in err)
        state_dir = os.path.join(td, "state-dir")
        os.makedirs(state_dir, exist_ok=True)
        lines, warn = read_state(state_dir)
        expect(
            "node-state-not-regular",
            lines is None and warn is not None and "drift unknown" in warn,
        )
        tampered = os.path.join(td, "node-state-tampered.toml")
        write_0600(
            tampered, f'app.material_directory = "{node_vals["NTS_MPT_API_KEY"]}"\n'
        )
        code, err = capture(
            lambda: run_render(node_tpl, node_out, tampered, node_vals, profile="node")
        )
        expect(
            "node-state-redacted-diff",
            code == 0 and node_vals["NTS_MPT_API_KEY"] not in err and "<set>" in err,
        )
        code = node_render()
        expect("node-state-after-tamper", code == 0 and node_unchanged())

        # (k6) QA-0511T2-3: a bare `$` in a template is a guard FAIL, both profiles
        # (it used to escape as a ValueError traceback).
        code, err = capture(
            lambda: node_render(template=derived_template("dollar", "log_level", "# cost $5\nlog_level"))
        )
        expect(
            "node-template-dollar",
            code == 1 and "FAIL template: invalid placeholder" in err and node_unchanged(),
        )
        mac_dollar = os.path.join(td, "mac-dollar.toml")
        with open(mac_dollar, "w", encoding="utf-8") as fh:
            fh.write("# cost $5\n" + open(tpl_path, "r", encoding="utf-8").read())
        code, err = capture(lambda: run_render(mac_dollar, out, state, vals))
        expect(
            "mac-template-dollar",
            code == 1 and "FAIL template: invalid placeholder" in err and unchanged(),
        )

        # (l) --check against a file rendered by another profile / not TOML / mode 644
        expect("node-check-other-profile-file", run_check(out) == 1)
        code, err = capture(
            lambda: run_check(write_0600(os.path.join(root, "node-not-toml.toml"), "not toml\n"))
        )
        expect("node-check-unparseable", code == 1 and "toml" in err)
        mode_path = write_0600(os.path.join(root, "node-mode.toml"), node_good_text)
        os.chmod(mode_path, 0o644)
        code, err = capture(lambda: run_check(mode_path))
        expect("node-check-mode", code == 1 and "mode" in err)

        # (m) --check validates the values exported in the environment
        expect("node-check-bad-env", run_check(node_out, env_extra={"NTS_WHISPER_DEVICE": "cpu"}) == 1)

        # (n) a guard failure never prints a secret value, even under a non-secret key
        code, err = capture(
            lambda: node_render(
                template=derived_template(
                    "leak", 'openai_api_key = ""', 'openai_api_key = "${NTS_PROXY_API_KEY}"'
                )
            )
        )
        expect(
            "node-guard-output-redacts",
            code == 1 and node_vals["NTS_PROXY_API_KEY"] not in err and "<set>" in err,
        )

    return 0 if not failures else 1


def main():
    p = argparse.ArgumentParser(
        description="Render the MPT config template with guards (01.2.2-T2)."
    )
    repo = repo_root()
    home = default_nts_home(repo)
    p.add_argument(
        "--template",
        default="",
        help="default: worker-node/mpt/config.template.toml, or"
        " worker-node/mpt/config.node.template.toml for --profile node",
    )
    p.add_argument("--out", default="")
    p.add_argument("--state", default="")
    p.add_argument(
        "--profile",
        choices=("normal", "spike", "node"),
        default="normal",
        help="normal = config.toml; spike = config.spike.toml with the local image stub"
        " (03.4.1-T1); node = the D2 GPU-worker config (05.1.1-T2)",
    )
    p.add_argument("--check", action="store_true", help="validate the existing out file, write nothing")
    p.add_argument("--self-test", action="store_true", help="run the in-process guard cases")
    p.add_argument(
        "--allow-direct-openai",
        action="store_true",
        help="also accept the pinned OPENAI_BASE_URL (pins.env) as base URL (used by"
        " start-mpt-api.sh --no-tap); refused by --profile node (CRV-0511-1)",
    )
    p.add_argument(
        "--env-file",
        default="",
        help=f"--profile node only: KEY=VALUE file (0600, or 0640 when root owns it as"
        f" {NODE_ENV_FILE}) whose values win over the process environment; 11 CR-4 fixes"
        " the node's file as that path, which is never read unless this flag names it",
    )
    p.add_argument(
        "out_path",
        nargs="?",
        default="",
        help="optional positional path: same as --out (so `--check --profile node FILE` works)",
    )
    args = p.parse_args()

    if args.out_path:
        if args.out and args.out != args.out_path:
            eprint(f"FAIL usage: --out {args.out} and the positional path {args.out_path} differ")
            sys.exit(2)
        args.out = args.out_path
    node = args.profile == "node"
    if not args.template:
        args.template = os.path.join(
            repo,
            NODE_TEMPLATE_RELPATH
            if node
            else "worker-node/mpt/config.template.toml",
        )
    if not args.out:
        if node:
            args.out = NODE_OUT
        else:
            args.out = os.path.join(
                home,
                "src/mpt/config.spike.toml" if args.profile == "spike" else "src/mpt/config.toml",
            )
    if not args.state:
        if node:
            args.state = os.path.join(
                os.path.dirname(args.out) or ".", ".mpt-config.node.last.redacted.toml"
            )
        else:
            args.state = os.path.join(
                home,
                "mpt-config.spike.last.redacted.toml"
                if args.profile == "spike"
                else "mpt-config.last.redacted.toml",
            )

    if args.self_test:
        sys.exit(self_test())
    if args.check:
        sys.exit(check(args))
    sys.exit(render(args))


if __name__ == "__main__":
    main()
