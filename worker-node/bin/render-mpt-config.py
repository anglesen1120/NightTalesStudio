#!/usr/bin/env python3
"""render-mpt-config.py — render worker-node/mpt/config.template.toml to
$NTS_HOME/src/mpt/config.toml (0600, atomic) after every guard passes (01.2.2-T2).

Run:  uv run --no-project --python 3.12 worker-node/bin/render-mpt-config.py [flags]

Exit codes (D9): 0 ok · 1 guard/substitution/value check failed (nothing written)
                 2 usage/environment error.
Secret values are read only from the four NTS_* environment variables, are never
printed and never appear in argv, diffs or the state file.
"""

import argparse
import difflib
import json
import os
import re
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
VALUE_RE = re.compile(r"^[A-Za-z0-9._-]{16,512}$")
SECRET_NAME_RE = re.compile(r"(^|_)(api_key|api_keys|key|keys|token|secret|password)$")
TAP_URL = "http://127.0.0.1:8002/v1"
DIRECT_URL = "https://api.openai.com/v1"


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


def env_checks(out):
    """Exit-2 environment checks. Returns realpath(out)."""
    real = os.path.realpath(out)
    nts_real = os.path.realpath(default_nts_home(repo_root()))
    if real.startswith("/Volumes/") and not (
        real == nts_real or real.startswith(nts_real.rstrip("/") + "/")
    ):
        eprint(f"FAIL env: output path resolves under /Volumes outside NTS_HOME: {out}")
        sys.exit(2)
    outdir = os.path.dirname(out) or "."
    if not os.path.isdir(outdir) or not os.path.exists(os.path.join(outdir, "main.py")):
        eprint(f"FAIL env: no MPT root with main.py at {outdir}")
        sys.exit(2)
    if os.path.islink(out) or os.path.isdir(out):
        eprint(f"FAIL env: {out} is a symlink or directory")
        sys.exit(2)
    return real


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


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


def guards(cfg, allow_direct=False):
    """Every guard prints PASS/FAIL (names only). Returns list of failed names."""
    app = cfg.get("app", {})
    whisper = cfg.get("whisper", {})
    kokoro = cfg.get("kokoro", {})
    url_ok = (TAP_URL, DIRECT_URL) if allow_direct else (TAP_URL,)

    checks = [
        ("listen_host", cfg.get("listen_host") == "127.0.0.1", '"127.0.0.1"'),
        ("listen_port", cfg.get("listen_port") == 8080, "8080"),
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
        ("openai_base_url", app.get("openai_base_url") in url_ok, " ".join(url_ok)),
        ("openai_image_base_url", app.get("openai_image_base_url") in url_ok, " ".join(url_ok)),
        (
            "openai_image_api_keys",
            isinstance(app.get("openai_image_api_keys"), list)
            and len(app["openai_image_api_keys"]) == 1
            and isinstance(app["openai_image_api_keys"][0], str)
            and len(app["openai_image_api_keys"][0]) > 0,
            "exactly 1 non-empty",
        ),
        ("subtitle_provider", app.get("subtitle_provider") == "whisper", '"whisper"'),
        ("whisper.model_size", whisper.get("model_size") == "large-v3-turbo", '"large-v3-turbo"'),
        ("enable_redis", app.get("enable_redis") is False, "false"),
        ("max_concurrent_tasks", app.get("max_concurrent_tasks") == 1, "1"),
        ("upload_post_enabled", app.get("upload_post_enabled") is False, "false"),
        ("upload_post_auto_upload", app.get("upload_post_auto_upload") is False, "false"),
        ("upload_post_api_key", app.get("upload_post_api_key") == "", '""'),
        ("kokoro.base_url", kokoro.get("base_url") == "http://127.0.0.1:8001/v1", "http://127.0.0.1:8001/v1"),
        ("kokoro.api_key", isinstance(kokoro.get("api_key"), str) and len(kokoro["api_key"]) > 0, "set"),
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

def render(args):
    env_checks(args.out)
    if not os.path.isfile(args.template):
        eprint(f"FAIL env: template missing: {args.template}")
        sys.exit(2)
    text = open(args.template, "r", encoding="utf-8").read()
    tpl = string.Template(text)
    ids = set(tpl.get_identifiers())
    if ids != set(REQUIRED_VARS):
        eprint(f"FAIL template placeholders: expected {sorted(REQUIRED_VARS)}, found {sorted(ids)}")
        sys.exit(1)
    mapping, failures = gather_mapping()
    for f in failures:
        eprint(f)
    if failures:
        sys.exit(1)
    rendered = tpl.substitute(mapping)
    try:
        cfg = tomllib.loads(rendered)
    except tomllib.TOMLDecodeError as exc:
        eprint(f"FAIL toml: rendered config does not parse: {exc}")
        sys.exit(1)
    failed = guards(cfg, allow_direct=args.allow_direct_openai)
    if failed:
        sys.exit(1)

    mapping_values = {v for v in mapping.values() if v}
    new_canon = canonical(rendered, mapping_values, from_text=True)

    # Config drift (report only, never blocks).
    if os.path.exists(args.out):
        try:
            old_out = canonical(args.out, mapping_values)
        except (tomllib.TOMLDecodeError, OSError, UnicodeDecodeError):
            eprint("WARN existing config.toml unparseable")
            old_out = None
        if os.path.exists(args.state) and old_out is not None:
            try:
                old_state = open(args.state, "r", encoding="utf-8").read().splitlines()
            except OSError:
                old_state = []
            if old_state != old_out:
                eprint("WARN config.toml changed since last render (config drift)")
                for line in diff_lines(old_state, old_out, "last-render", "current"):
                    eprint(line)
            if old_state != new_canon:
                eprint("INFO template change")
                for line in diff_lines(old_state, new_canon, "last-render", "new-render"):
                    eprint(line)
    atomic_write(args.out, rendered)
    atomic_write(args.state, "\n".join(new_canon) + "\n")
    eprint(f"rendered {args.out} (0600)")
    return 0


def check(args):
    env_checks(args.out)
    failed_names = []
    try:
        st = os.lstat(args.out)
    except OSError:
        eprint(f"FAIL exists: {args.out} missing")
        return 1
    if not stat.S_ISREG(st.st_mode):
        eprint(f"FAIL exists: {args.out} not a regular file")
        return 1
    if stat.S_IMODE(st.st_mode) != 0o600:
        eprint(f"FAIL mode: expected 600")
        failed_names.append("mode")
    try:
        cfg = tomllib.loads(open(args.out, "r", encoding="utf-8").read())
    except (tomllib.TOMLDecodeError, UnicodeDecodeError) as exc:
        eprint(f"FAIL toml: {exc}")
        return 1
    failed_names += guards(cfg, allow_direct=args.allow_direct_openai)
    if failed_names:
        return 1
    eprint("check ok: config.toml passes every guard")
    return 0


def run_render(template, out, state, env_extra, unset=(), allow_direct=False):
    """Self-test helper: run the render logic in-process. Returns exit code."""
    saved = {}
    for k in REQUIRED_VARS:
        saved[k] = os.environ.get(k)
        os.environ.pop(k, None)
    for k, v in env_extra.items():
        if v is not None:
            os.environ[k] = v
    for k in unset:
        os.environ.pop(k, None)
    args = argparse.Namespace(
        template=template, out=out, state=state, allow_direct_openai=allow_direct
    )
    try:
        code = render(args)
    except SystemExit as e:
        code = e.code if isinstance(e.code, int) else 1
    finally:
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
    return code


def self_test():
    import hashlib

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

    return 0 if not failures else 1


def main():
    p = argparse.ArgumentParser(
        description="Render the MPT config template with guards (01.2.2-T2)."
    )
    repo = repo_root()
    home = default_nts_home(repo)
    p.add_argument("--template", default=os.path.join(repo, "worker-node/mpt/config.template.toml"))
    p.add_argument("--out", default=os.path.join(home, "src/mpt/config.toml"))
    p.add_argument("--state", default=os.path.join(home, "mpt-config.last.redacted.toml"))
    p.add_argument("--check", action="store_true", help="validate the existing config.toml, write nothing")
    p.add_argument("--self-test", action="store_true", help="run the in-process guard cases")
    p.add_argument(
        "--allow-direct-openai",
        action="store_true",
        help="also accept https://api.openai.com/v1 base URLs (used by start-mpt-api.sh --no-tap)",
    )
    args = p.parse_args()

    if args.self_test:
        sys.exit(self_test())
    if args.check:
        sys.exit(check(args))
    sys.exit(render(args))


if __name__ == "__main__":
    main()
