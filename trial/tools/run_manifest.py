#!/usr/bin/env python3
"""trial/tools/run_manifest.py — write/check trial/runs/<run_id>/run.json.

Subcommands (01.3.1-T3 step B, run.json shape = plan.md §3 schema_version 1):
  run_manifest.py start --run-id ID --lang {vi,en} --aspect {landscape,portrait}
      --topic TEXT --target-minutes N [--status calibration|trial]
      [--voice NAME] [--story-source mpt|plan02] [--no-tap]
  run_manifest.py --check-tap [--no-tap]
  run_manifest.py --help

start writes trial/runs/<run_id>/run.json atomically (tmp + os.replace).
--check-tap asserts the rendered MPT config points at (or, with --no-tap, away
from) the usage tap http://127.0.0.1:8002/v1 (A5).
Stdlib only. Exit codes: 0 ok, 1 check failed, 2 usage/env error.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tomllib
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TAP_BASE_URL = "http://127.0.0.1:8002/v1"
RUN_ID_RE = re.compile(r"^\d{8}-\d{4}-(vi|en)-(landscape|portrait)-\d{2}$")
# Redaction: any TOML key whose name ends in one of these words (01.3.1-T3 B).
SECRET_KEY_RE = re.compile(r"(^|_)(api_key|api_keys|key|secret|token|password)$")
TOML_KV_RE = re.compile(r"^\s*([A-Za-z0-9_]+)\s*=\s*(.+)$")
TOML_TABLE_RE = re.compile(r"^\s*\[+\s*([A-Za-z0-9_.\"'-]+)\s*\]+\s*(#.*)?$")
# settings allow-list (plan.md §3): (dotted TOML path, run.json settings name).
SETTINGS_MAP = [
    ("app.openai_base_url", "openai_base_url"),
    ("app.openai_image_base_url", "openai_image_base_url"),
    ("app.openai_model_name", "openai_model_name"),
    ("app.openai_image_model", "openai_image_model"),
    ("app.openai_image_size", "openai_image_size"),
    ("whisper.model_size", "whisper.model_size"),
    ("whisper.device", "whisper.device"),
    ("whisper.compute_type", "whisper.compute_type"),
    ("app.max_concurrent_tasks", "max_concurrent_tasks"),
    ("log_level", "log_level"),
]
VIENEU_KEYS = ("VIENEU_PRECISION", "VIENEU_MAX_STREAMS")

USAGE = """Usage:
  run_manifest.py start --run-id ID --lang {vi,en} --aspect {landscape,portrait}
      --topic TEXT --target-minutes N [--status calibration|trial]
      [--voice NAME] [--story-source mpt|plan02] [--no-tap]
  run_manifest.py --check-tap [--no-tap]
  run_manifest.py --help

start: collect pins, git heads, env, redacted config digest, tap and sampler
settings into trial/runs/<run_id>/run.json (plan.md §3, schema_version 1).
--check-tap: assert MPT config.toml app.openai_base_url and
app.openai_image_base_url equal http://127.0.0.1:8002/v1 (with --no-tap: do NOT).
Exit codes: 0 ok, 1 check failed, 2 usage/env error.
"""


def die(code, msg):
    print(f"run_manifest.py: {msg}", file=sys.stderr)
    sys.exit(code)


def nts_home():
    # Mirrors lib.sh nts_home: $NTS_HOME env wins, default <repo>/.nts.
    h = os.environ.get("NTS_HOME") or str(REPO / ".nts")
    return Path(h).resolve() if not h.startswith("/") else Path(h)


def read_kv_file(path):
    """Line-by-line KEY=value parse (same rule as lib.sh load_pins)."""
    out = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        key, _, val = line.partition("=")
        out[key] = val
    return out


def git_head(path):
    p = subprocess.run(
        ["git", "-C", str(path), "rev-parse", "HEAD"],
        capture_output=True, text=True)
    if p.returncode != 0:
        die(1, f"git rev-parse HEAD failed in {path}: {p.stderr.strip()}")
    return p.stdout.strip()


def redact_config(text):
    """Replace values of secret-named keys with "<redacted>"; return
    (redacted_text, dotted_keys). The redacted text is never written."""
    section = ""
    out_lines = []
    keys = []
    for line in text.splitlines(keepends=True):
        tm = TOML_TABLE_RE.match(line)
        if tm:
            section = tm.group(1)
            out_lines.append(line)
            continue
        km = TOML_KV_RE.match(line)
        if km and SECRET_KEY_RE.search(km.group(1)):
            key = km.group(1)
            indent = line[: len(line) - len(line.lstrip())]
            nl = "\n" if line.endswith("\n") else ""
            out_lines.append(f'{indent}{key} = "<redacted>"{nl}')
            keys.append(f"{section}.{key}" if section else key)
            continue
        out_lines.append(line)
    return "".join(out_lines), keys


def dig(cfg, dotted):
    cur = cfg
    for part in dotted.split("."):
        if not isinstance(cur, dict) or part not in cur:
            return None, False
        cur = cur[part]
    return cur, True


def load_mpt_config():
    cfg_path = nts_home() / "src" / "mpt" / "config.toml"
    if not cfg_path.is_file():
        die(1, f"MPT config missing: $NTS_HOME/src/mpt/config.toml (render it first)")
    try:
        return tomllib.loads(cfg_path.read_text(encoding="utf-8")), cfg_path
    except tomllib.TOMLDecodeError as e:
        die(1, f"config.toml is not valid TOML: {e}")


def check_tap(no_tap):
    cfg, _ = load_mpt_config()
    app = cfg.get("app", {})
    for key in ("openai_base_url", "openai_image_base_url"):
        v = app.get(key)
        if no_tap:
            if v == TAP_BASE_URL:
                die(1, f"config.toml app.{key} points at the usage tap "
                       f"({TAP_BASE_URL}) but --no-tap was given")
        elif v != TAP_BASE_URL:
            die(1, f"config.toml app.{key} is {v!r}, want {TAP_BASE_URL!r} "
                   f"(the usage tap, D6)")
    return 0


def cmd_start(a):
    if not RUN_ID_RE.match(a.run_id):
        die(2, f"bad --run-id {a.run_id!r} (want D10 "
               "YYYYMMDD-HHMM-<vi|en>-<landscape|portrait>-NN)")
    nts = nts_home()
    run_dir = REPO / "trial" / "runs" / a.run_id
    if not run_dir.is_dir():
        die(1, f"run dir missing: trial/runs/{a.run_id} (start_run.sh creates it)")

    pins = read_kv_file(REPO / "worker-node" / "pins.env")

    heads = {
        "mpt": git_head(nts / "src" / "mpt"),
        "vieneu": git_head(nts / "src" / "vieneu"),
    }

    ep = subprocess.run(
        [str(REPO / "worker-node" / "bin" / "env-versions.sh")],
        capture_output=True, text=True)
    if ep.returncode != 0:
        sys.stderr.write(ep.stderr)
        die(1, "worker-node/bin/env-versions.sh failed")
    try:
        env = json.loads(ep.stdout)
    except json.JSONDecodeError as e:
        die(1, f"env-versions.sh stdout is not a JSON object: {e}")

    cfg, cfg_path = load_mpt_config()
    cfg_text = cfg_path.read_text(encoding="utf-8")
    redacted_text, redacted_keys = redact_config(cfg_text)
    missing = [name for dotted, name in SETTINGS_MAP if not dig(cfg, dotted)[1]]
    if missing:
        die(1, "config.toml is missing allow-listed keys: " + ", ".join(missing))
    settings = {name: dig(cfg, dotted)[0] for dotted, name in SETTINGS_MAP}
    config = {
        "path": "$NTS_HOME/src/mpt/config.toml",
        "sha256_redacted": hashlib.sha256(redacted_text.encode("utf-8")).hexdigest(),
        "redacted_keys": redacted_keys,
        "template_sha256": hashlib.sha256(
            (REPO / "worker-node" / "mpt" / "config.template.toml")
            .read_bytes()).hexdigest(),
        "settings": settings,
    }

    if a.no_tap:
        tap = {"disabled": True}
    else:
        tap_path = nts / "tap.json"
        if not tap_path.is_file():
            die(1, "$NTS_HOME/tap.json missing (start the tap or use --no-tap)")
        try:
            tap = json.loads(tap_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            die(1, f"$NTS_HOME/tap.json is not valid JSON: {e}")

    ve = read_kv_file(REPO / "worker-node" / "vieneu" / "env.example")
    vieneu = {k: ve.get(k) for k in VIENEU_KEYS}

    run = {
        "schema_version": 1,
        "run_id": a.run_id,
        "run_status": a.status,
        "started_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "finished_at": None,
        "lang": a.lang,
        "aspect": a.aspect,
        "topic": a.topic,
        "target_minutes": (int(a.target_minutes)
                           if float(a.target_minutes).is_integer()
                           else float(a.target_minutes)),
        "voice_name": f"kokoro:{a.voice}" if a.voice else "n/a:not-given",
        "story_source": a.story_source,
        "pins": pins,
        "heads": heads,
        "env": env,
        "config": config,
        "tap": tap,
        "vieneu": vieneu,
        "sampler": {
            "interval_s": 1,
            "roots": {"vieneu": 8000, "proxy": 8001, "mpt": 8080, "webui": 8501},
        },
        "mpt_task_id": None,
        "sources": {},
    }

    out = run_dir / "run.json"
    tmp = run_dir / "run.json.tmp"
    tmp.write_text(json.dumps(run, indent=2, ensure_ascii=False) + "\n",
                   encoding="utf-8")
    os.replace(tmp, out)
    return 0


def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        sys.stdout.write(USAGE)
        return 0
    cmd, rest = argv[0], argv[1:]
    if cmd == "--check-tap":
        bad = [x for x in rest if x != "--no-tap"]
        if bad:
            sys.stderr.write(f"run_manifest.py: --check-tap: unexpected args: "
                             f"{' '.join(bad)}\n")
            return 2
        return check_tap("--no-tap" in rest)
    if cmd == "start":
        p = argparse.ArgumentParser(prog="run_manifest.py start")
        p.add_argument("--run-id", required=True)
        p.add_argument("--lang", required=True, choices=("vi", "en"))
        p.add_argument("--aspect", required=True,
                       choices=("landscape", "portrait"))
        p.add_argument("--topic", required=True)
        p.add_argument("--target-minutes", required=True)
        p.add_argument("--status", default="trial",
                       choices=("calibration", "trial"))
        p.add_argument("--voice", default="")
        p.add_argument("--story-source", default="mpt",
                       choices=("mpt", "plan02"))
        p.add_argument("--no-tap", action="store_true")
        a = p.parse_args(rest)
        try:
            float(a.target_minutes)
        except ValueError:
            p.error("--target-minutes must be a number")
        if "\n" in a.topic or not (1 <= len(a.topic) <= 200):
            p.error("--topic must be 1-200 chars without newline")
        return cmd_start(a)
    sys.stderr.write(f"run_manifest.py: unknown subcommand {cmd!r} "
                     "(see --help)\n")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
