#!/usr/bin/env python3
"""worker-node/proxy/tools/make_previews.py — 04.2.3-T1 voice preview tool.

Generates one loudness-matched MP3 preview per *active* catalog voice through
the running proxy (127.0.0.1:8001, D2/D13), tracks them in
`worker-node/proxy/previews.manifest.json`, and (M1) uploads each under an
immutable versioned key `voice-previews/<slug>.v<n>.mp3` before writing
`previewStorageKey` back into the catalog (D12, D9).

Imports `nts_vieneu_proxy.catalog` — run in the proxy venv:

  PROXY_API_KEY=… UV_PROJECT_ENVIRONMENT="$NTS_HOME/src/proxy-venv" \
    uv run --frozen --project worker-node/proxy \
    python worker-node/proxy/tools/make_previews.py --generate|--upload …

Exit 0 ok · 1 a voice/upload failed · 2 usage/config.

`PROXY_API_KEY` is read from the environment only — never argv, never logged,
never inherited by child processes. Env overrides (test seams, not contract):
`PROXY_BASE_URL` (default http://127.0.0.1:8001), `NTS_PREVIEW_OUT`,
`NTS_PREVIEW_TEXTS_DIR`, `NTS_PREVIEW_MANIFEST`, `FFMPEG_PATH`, `FFPROBE_PATH`,
`NTS_STORAGE_CLI` (S3-compatible CLI name; ⚠️ `aws s3api` until 11.4.1 pins it),
`NTS_PREVIEW_WRITE_KEY`/`_SECRET` (mapped onto the storage child's env — the
operator key, Keychain `nts.preview.write`; never argv).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

try:
    from nts_vieneu_proxy.catalog import load_catalog
    from nts_vieneu_proxy.errors import CatalogError
except ImportError:  # direct run outside the proxy venv (self-test, pytests)
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
    from nts_vieneu_proxy.catalog import load_catalog
    from nts_vieneu_proxy.errors import CatalogError

PROXY_ROOT = Path(__file__).resolve().parent.parent
REPO_ROOT = PROXY_ROOT.parent.parent

DEFAULT_BASE_URL = "http://127.0.0.1:8001"
DEFAULT_CATALOG = REPO_ROOT / "seed" / "voices.v1.json"
DEFAULT_TEXTS_DIR = REPO_ROOT / "seed" / "voice-previews"
DEFAULT_MANIFEST = PROXY_ROOT / "previews.manifest.json"

CONTENT_TYPE = "audio/mpeg"
CACHE_CONTROL = "public, max-age=31536000, immutable"
LOUDNORM_FILTER = "loudnorm=I=-16:TP=-1.5:LRA=11"
LOUD_TARGET_I = -16.0
LOUD_TOLERANCE = 1.5
MAX_SECONDS = 12.0
MAX_BYTES = 200_000  # 200 KB = 200,000 (decision, task Design §5)
HTTP_TIMEOUT_S = 120
BUCKET_FMT = "nt-public-{env}"
_MANIFEST_KEYS = {
    "id",
    "slug",
    "key",
    "sha256",
    "bytes",
    "duration_s",
    "text_sha256",
    "catalog_revision",
}
_KEY_RE = re.compile(r"^voice-previews/[a-z0-9-]+\.v(\d+)\.mp3$")
# Only a genuine not-found may be read as "key absent" (SEC-NTS-M0-01 follow-up,
# W1 code review P2): a storage CLI failure (network, 5xx, credentials, region)
# must never turn into a PUT over an existing immutable key.
_NOT_FOUND_RE = re.compile(r"(?i)\b404\b|not[ _-]?found|nosuchkey")


def _log(msg: str) -> None:
    print(msg, file=sys.stderr)


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _read_text(path: Path) -> bytes:
    """Exact bytes sent to the proxy: file minus one final ``\\n`` (D12)."""
    raw = path.read_bytes()
    return raw[:-1] if raw.endswith(b"\n") else raw


def _encode_argv(ffmpeg: str, src: Path, dst: Path) -> list[str]:
    return [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-i",
        str(src),
        "-af",
        LOUDNORM_FILTER,
        "-ac",
        "1",
        "-ar",
        "48000",
        "-c:a",
        "libmp3lame",
        "-b:a",
        "128k",
        "-y",
        str(dst),
    ]


def _measure_argv(ffmpeg: str, src: Path) -> list[str]:
    return [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "info",
        "-i",
        str(src),
        "-af",
        "loudnorm=print_format=json",
        "-f",
        "null",
        "-",
    ]


def _duration_argv(ffprobe: str, src: Path) -> list[str]:
    return [
        ffprobe,
        "-v",
        "error",
        "-show_entries",
        "format=duration",
        "-of",
        "csv=p=0",
        str(src),
    ]


def _parse_loudness(stderr: str) -> float | None:
    """`input_i` of the last loudnorm `print_format=json` block on stderr."""
    i = stderr.rfind("{")
    j = stderr.rfind("}")
    if i == -1 or j == -1 or j <= i:
        return None
    try:
        return float(json.loads(stderr[i : j + 1])["input_i"])
    except (json.JSONDecodeError, KeyError, TypeError, ValueError):
        return None


def _key_version(key: str) -> int | None:
    m = _KEY_RE.match(key)
    return int(m.group(1)) if m else None


def _next_version(row: dict | None, text_sha: str, bumped: bool) -> int:
    """D12 versioning: same text + no --bump keeps n; else n+1 (first = 1)."""
    if row is None:
        return 1
    n = _key_version(row.get("key", "")) or 0
    if not bumped and row.get("text_sha256") == text_sha:
        return n or 1
    return n + 1


def _read_manifest(path: Path) -> list[dict]:
    if not path.exists():
        return []
    rows = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(rows, list) or any(set(r) != _MANIFEST_KEYS for r in rows):
        raise ValueError(f"bad manifest shape: {path}")
    return rows


def _atomic_write(path: Path, data: str | bytes) -> None:
    """Write `data` to `path` atomically and without following a symlink.

    Same-directory `mkstemp` (unpredictable name, CWE-377), no follow of a
    pre-existing link (CWE-59), `os.replace` for atomicity (CWE-367); the temp
    is removed on every failure path. An existing target keeps its mode
    (SEC-NTS-M0-01, 2026-09-26).
    """
    if path.is_symlink():
        raise ValueError(f"refusing to write through symlink: {path}")
    payload = data.encode("utf-8") if isinstance(data, str) else data
    try:
        mode = stat.S_IMODE(path.stat().st_mode)
    except OSError:  # new file: the usual tracked-file mode
        mode = 0o644
    fd, tmp_name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    tmp = Path(tmp_name)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, "wb") as fh:
            fh.write(payload)
            fh.flush()
            os.fsync(fh.fileno())
        os.replace(tmp, path)
        dfd = os.open(path.parent, os.O_RDONLY)
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


def _write_manifest(path: Path, rows: list[dict]) -> None:
    rows = sorted(rows, key=lambda r: r["slug"])
    _atomic_write(path, json.dumps(rows, ensure_ascii=False, indent=2) + "\n")


def _write_catalog(path: Path, doc: dict) -> None:
    """Same formatting as 04.2.2-T1: UTF-8 NFC, 2-space, no ascii escapes."""
    _atomic_write(path, json.dumps(doc, ensure_ascii=False, indent=2) + "\n")


def _opener():
    # No proxies: HTTP_PROXY must not carry the bearer key (01 protocol).
    return urllib.request.build_opener(urllib.request.ProxyHandler({}))


def _probe_health(opener, base_url: str) -> bool:
    try:
        with opener.open(f"{base_url}/health", timeout=10) as resp:
            return resp.status == 200
    except Exception:
        return False


def _speech_post(opener, base_url: str, api_key: str, voice_id: str, text: bytes):
    """POST /v1/audio/speech → (status, content_type, body). Never raises."""
    req = urllib.request.Request(
        f"{base_url}/v1/audio/speech",
        data=json.dumps({"input": text.decode("utf-8"), "voice": voice_id}).encode(
            "utf-8"
        ),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    req.add_unredirected_header("Authorization", f"Bearer {api_key}")
    try:
        with opener.open(req, timeout=HTTP_TIMEOUT_S) as resp:
            return resp.status, resp.headers.get("Content-Type", ""), resp.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.headers.get("Content-Type", ""), b""
    except Exception as exc:  # URLError, timeout mid-run → voice failure
        return 0, f"{type(exc).__name__}", b""


def _check_preview(runner, ffmpeg: str, ffprobe: str, path: Path):
    """Return (duration_s, input_i) or raise ValueError with the failed rule."""
    cp = runner(_duration_argv(ffprobe, path))
    dur = float(cp.stdout.strip())
    if dur > MAX_SECONDS:
        raise ValueError(f"duration {dur:.2f}s > {MAX_SECONDS}s")
    size = path.stat().st_size
    if size > MAX_BYTES:
        raise ValueError(f"size {size}B > {MAX_BYTES}B")
    meas = runner(_measure_argv(ffmpeg, path))
    loud = _parse_loudness(meas.stderr or "")
    if loud is None or not (
        LOUD_TARGET_I - LOUD_TOLERANCE <= loud <= LOUD_TARGET_I + LOUD_TOLERANCE
    ):
        raise ValueError(f"loudness {loud} outside {LOUD_TARGET_I}±{LOUD_TOLERANCE}")
    return dur, loud


class CliStorage:
    """Storage via the S3-compatible CLI 11.4.1 names (⚠️ not fixed — the
    contract is). Credentials flow through that CLI's env/profile only."""

    def __init__(self, cli: str | None = None) -> None:
        self._cli = shlex.split(cli or os.environ.get("NTS_STORAGE_CLI", "aws"))

    def _env(self) -> dict:
        env = dict(os.environ)
        key = os.environ.get("NTS_PREVIEW_WRITE_KEY")
        secret = os.environ.get("NTS_PREVIEW_WRITE_SECRET")
        if key and secret:
            env["AWS_ACCESS_KEY_ID"] = key
            env["AWS_SECRET_ACCESS_KEY"] = secret
        return env

    def head_object(self, bucket: str, key: str):
        cp = subprocess.run(
            [*self._cli, "s3api", "head-object", "--bucket", bucket, "--key", key],
            capture_output=True,
            text=True,
            env=self._env(),
        )
        if cp.returncode != 0:
            if _NOT_FOUND_RE.search(f"{cp.stdout}\n{cp.stderr}"):
                return (404, None, None)
            # "could not ask" is not "absent": raising keeps upload() from
            # PUTting over an existing immutable key (W1 code review P2).
            raise RuntimeError(
                f"head-object {key} exited {cp.returncode}: {cp.stderr.strip()[:200]}"
            )
        meta = json.loads(cp.stdout)
        return (200, int(meta.get("ContentLength", 0)), meta.get("ContentType"))

    def put_object(
        self,
        bucket: str,
        key: str,
        path: Path,
        content_type: str,
        cache_control: str,
    ) -> None:
        cp = subprocess.run(
            [
                *self._cli,
                "s3api",
                "put-object",
                "--bucket",
                bucket,
                "--key",
                key,
                "--body",
                str(path),
                "--content-type",
                content_type,
                "--cache-control",
                cache_control,
            ],
            capture_output=True,
            text=True,
            env=self._env(),
        )
        if cp.returncode != 0:
            raise RuntimeError(f"put-object {key} exited {cp.returncode}")


def generate(
    *,
    catalog_path: Path,
    out_dir: Path,
    bumps: set[str],
    base_url: str,
    api_key: str,
    manifest_path: Path | None = None,
    texts_dir: Path | None = None,
    opener=None,
    runner=None,
) -> int:
    catalog_path = Path(catalog_path)
    out_dir = Path(out_dir)
    manifest_path = Path(manifest_path or _env_path("NTS_PREVIEW_MANIFEST", DEFAULT_MANIFEST))
    texts_dir = Path(texts_dir or _env_path("NTS_PREVIEW_TEXTS_DIR", DEFAULT_TEXTS_DIR))
    opener = opener or _opener()
    runner = runner or _default_runner
    ffmpeg = os.environ.get("FFMPEG_PATH") or "ffmpeg"
    ffprobe = os.environ.get("FFPROBE_PATH") or "ffprobe"

    try:
        catalog = load_catalog(catalog_path)
        manifest = _read_manifest(manifest_path)
    except (CatalogError, ValueError) as exc:
        _log(f"error: {exc}")
        return 2

    if manifest_path.is_symlink():
        _log(f"error: refusing to write through symlink: {manifest_path}")
        return 2
    try:
        # Fail fast (exit 2, config) before any speech call: --out must be a
        # usable directory. A bad path (parent is a regular file, no permission,
        # read-only volume) must not surface as an uncaught traceback
        # (04.2.3-T1 QA defect D1, 2026-09-26).
        (out_dir / "raw").mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        _log(f"error: --out {out_dir} not usable: {exc}")
        return 2
    if shutil.which(ffmpeg) is None and not Path(ffmpeg).is_file():
        _log(f"error: ffmpeg not found ({ffmpeg})")
        return 2
    if not _probe_health(opener, base_url):
        _log(f"error: proxy not reachable at {base_url} (/health)")
        return 2

    rows_by_id = {r["id"]: r for r in manifest}
    failed: list[str] = []
    for voice in catalog.voices:
        if not voice.active:
            continue  # decision: pickers only show active voices
        text_path = texts_dir / f"preview.{voice.language}.txt"
        try:
            text = _read_text(text_path)
        except OSError as exc:
            _log(f"error: {exc}")
            return 2
        text_sha = _sha256_bytes(text)
        prev = rows_by_id.get(voice.id)
        n = _next_version(prev, text_sha, voice.id in bumps)
        out_file = out_dir / f"{voice.slug}.v{n}.mp3"

        if (
            prev is not None
            and voice.id not in bumps
            and prev.get("text_sha256") == text_sha
            and _key_version(prev.get("key", "")) == n
            and out_file.exists()
            and _sha256_file(out_file) == prev["sha256"]
        ):
            _log(f"skip {voice.id}: unchanged text, v{n} on disk matches manifest")
            continue

        status, ct, body = _speech_post(opener, base_url, api_key, voice.id, text)
        if status == 401:
            _log("error: proxy rejected PROXY_API_KEY (401)")
            return 2
        ct_main = (ct or "").split(";")[0].strip()
        if status != 200 or ct_main != CONTENT_TYPE:
            _log(f"fail {voice.id}: speech call status {status} type {ct_main!r}")
            failed.append(voice.id)
            continue

        raw_path = out_dir / "raw" / f"{voice.slug}.mp3"
        try:
            raw_path.write_bytes(body)
        except OSError as exc:  # ENOSPC/EACCES mid-run: config, not a voice failure
            _log(f"error: --out {out_dir} not writable: {exc}")
            return 2
        try:
            runner(_encode_argv(ffmpeg, raw_path, out_file))
            dur, _loud = _check_preview(runner, ffmpeg, ffprobe, out_file)
        except (ValueError, OSError, RuntimeError) as exc:
            _log(f"fail {voice.id}: {exc}")
            out_file.unlink(missing_ok=True)
            failed.append(voice.id)
            continue

        rows_by_id[voice.id] = {
            "id": voice.id,
            "slug": voice.slug,
            "key": f"voice-previews/{voice.slug}.v{n}.mp3",
            "sha256": _sha256_file(out_file),
            "bytes": out_file.stat().st_size,
            "duration_s": round(dur, 2),
            "text_sha256": text_sha,
            "catalog_revision": catalog.revision,
        }
        _log(f"ok {voice.id}: v{n} {dur:.2f}s")

    try:
        _write_manifest(manifest_path, list(rows_by_id.values()))
    except ValueError as exc:  # symlinked tracked manifest: config error
        _log(f"error: {exc}")
        return 2
    return 1 if failed else 0


def upload(
    *,
    catalog_path: Path,
    envs: list[str],
    out_dir: Path,
    manifest_path: Path | None = None,
    storage=None,
) -> int:
    catalog_path = Path(catalog_path)
    out_dir = Path(out_dir)
    manifest_path = Path(manifest_path or _env_path("NTS_PREVIEW_MANIFEST", DEFAULT_MANIFEST))
    storage = storage or CliStorage()

    try:
        manifest = _read_manifest(manifest_path)
        old_bytes = catalog_path.read_bytes()
        doc = json.loads(old_bytes.decode("utf-8"))
        load_catalog(catalog_path)  # sanity: never write a key into a bad file
    except (CatalogError, ValueError, OSError, json.JSONDecodeError) as exc:
        _log(f"error: {exc}")
        return 2

    if catalog_path.is_symlink():
        _log(f"error: refusing to write through symlink: {catalog_path}")
        return 2  # before any bucket traffic, not after the PUTs
    by_id = {v["id"]: v for v in doc["voices"]}
    failed: list[str] = []
    changed = False
    for row in manifest:
        entry = by_id.get(row["id"])
        if entry is None:
            _log(f"fail {row['id']}: not in catalog")
            failed.append(row["id"])
            continue
        if entry["previewStorageKey"] == row["key"]:
            continue  # already recorded
        local = out_dir / Path(row["key"]).name
        if not local.is_file() or _sha256_file(local) != row["sha256"]:
            _log(f"fail {row['id']}: local {local.name} missing or sha mismatch")
            failed.append(row["id"])
            continue
        ok = True
        for env in envs:
            bucket = BUCKET_FMT.format(env=env)
            try:
                st, length, _ct = storage.head_object(bucket, row["key"])
                if st == 200:
                    if length == row["bytes"]:
                        continue  # already uploaded, immutable key (skip)
                    _log(f"fail {row['id']}: refusing to overwrite {row['key']}")
                    ok = False
                elif st == 404:
                    storage.put_object(
                        bucket, row["key"], local, CONTENT_TYPE, CACHE_CONTROL
                    )
                    st2, length2, ct2 = storage.head_object(bucket, row["key"])
                    if st2 != 200 or length2 != row["bytes"] or ct2 != CONTENT_TYPE:
                        _log(f"fail {row['id']}: post-put HEAD mismatch in {bucket}")
                        ok = False
                else:
                    _log(f"fail {row['id']}: HEAD {bucket}/{row['key']} → {st}")
                    ok = False
            except Exception as exc:
                _log(f"fail {row['id']}: storage error in {bucket}: {exc}")
                ok = False
            if not ok:
                break
        if ok:
            entry["previewStorageKey"] = row["key"]
            changed = True
        else:
            failed.append(row["id"])

    if changed:
        doc["revision"] = int(doc["revision"]) + 1  # once per run
        try:
            _write_catalog(catalog_path, doc)
            load_catalog(catalog_path)  # sanity: never leave a bad file
        except ValueError as exc:
            _log(f"error: {exc}")
            return 2
        except CatalogError as exc:
            _atomic_write(catalog_path, old_bytes)  # exact previous bytes
            _log(f"error: catalog re-validation failed, restored ({exc})")
            return 1
    return 1 if failed else 0


def _default_runner(argv: list[str]) -> subprocess.CompletedProcess:
    cp = subprocess.run(
        argv, capture_output=True, text=True, timeout=300, check=False
    )
    if cp.returncode != 0:
        raise RuntimeError(f"{argv[0]} exited {cp.returncode}: {cp.stderr[-200:]}")
    return cp


def _env_path(name: str, default: Path) -> Path:
    v = os.environ.get(name)
    return Path(v) if v else Path(default)


def _parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="make_previews.py")
    p.add_argument("--self-test", action="store_true")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--generate", action="store_true")
    g.add_argument("--upload", action="store_true")
    p.add_argument("--catalog", default=str(DEFAULT_CATALOG))
    p.add_argument("--out", default=None)
    p.add_argument("--bump", nargs="*", default=[])
    p.add_argument("--env", dest="envs", action="append", default=[])
    p.add_argument("--manifest", default=None)
    p.add_argument("--texts", default=None)
    return p


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.self_test:
        return _self_test()
    if not args.generate and not args.upload:
        _log("usage: --generate or --upload required")
        return 2

    out_dir = Path(
        args.out
        or _env_path(
            "NTS_PREVIEW_OUT",
            REPO_ROOT / ".nts" / "runs" / "voice-previews",
        )
    )
    manifest_path = Path(
        args.manifest or _env_path("NTS_PREVIEW_MANIFEST", DEFAULT_MANIFEST)
    )
    texts_dir = Path(
        args.texts or _env_path("NTS_PREVIEW_TEXTS_DIR", DEFAULT_TEXTS_DIR)
    )

    if args.generate:
        api_key = os.environ.get("PROXY_API_KEY")
        if not api_key:
            _log("error: PROXY_API_KEY not set (env only, never argv)")
            return 2
        return generate(
            catalog_path=Path(args.catalog),
            out_dir=out_dir,
            bumps=set(args.bump),
            base_url=os.environ.get("PROXY_BASE_URL") or DEFAULT_BASE_URL,
            api_key=api_key,
            manifest_path=manifest_path,
            texts_dir=texts_dir,
        )

    if not args.envs:
        _log("usage: --upload requires at least one --env <env>")
        return 2
    return upload(
        catalog_path=Path(args.catalog),
        envs=args.envs,
        out_dir=out_dir,
        manifest_path=manifest_path,
    )


def _self_test() -> int:
    """Pure-function checks; no proxy, no ffmpeg, no bucket."""
    fails = []

    # argv parse
    a = _parser().parse_args(["--generate", "--catalog", "c.json", "--bump", "x", "y"])
    if not (a.generate and a.catalog == "c.json" and a.bump == ["x", "y"]):
        fails.append("parse --generate")
    a = _parser().parse_args(["--upload", "--env", "dev", "--env", "prod"])
    if not (a.upload and a.envs == ["dev", "prod"]):
        fails.append("parse --upload")

    # exact ffmpeg argv (D12)
    want = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-i", "in.mp3",
        "-af", LOUDNORM_FILTER, "-ac", "1", "-ar", "48000",
        "-c:a", "libmp3lame", "-b:a", "128k", "-y", "out.mp3",
    ]
    if _encode_argv("ffmpeg", Path("in.mp3"), Path("out.mp3")) != want:
        fails.append("encode argv")

    # versioning rules
    row = {"key": "voice-previews/x.v2.mp3", "text_sha256": "t1", "id": "x"}
    cases = [
        (_next_version(None, "t1", False), 1),
        (_next_version(row, "t1", False), 2),
        (_next_version(row, "t1", True), 3),
        (_next_version(row, "t2", False), 3),
        (_next_version({"key": "bad", "text_sha256": "t1"}, "t1", False), 1),
    ]
    for got, want_n in cases:
        if got != want_n:
            fails.append(f"version {got}!={want_n}")

    # loudness parser
    sample = 'noise\n{\n"input_i":"-16.4","other":"x"\n}\n'
    if _parse_loudness(sample) != -16.4:
        fails.append("parse loudness")
    if _parse_loudness("no json") is not None:
        fails.append("parse loudness empty")

    # manifest shape + sort
    with tempfile.TemporaryDirectory() as td:
        mp = Path(td) / "m.json"
        rows = [
            {k: 0 for k in _MANIFEST_KEYS} | {"slug": "b"},
            {k: 0 for k in _MANIFEST_KEYS} | {"slug": "a"},
        ]
        _write_manifest(mp, rows)
        loaded = _read_manifest(mp)
        if [r["slug"] for r in loaded] != ["a", "b"]:
            fails.append("manifest sort")

        # catalog rewrite round-trip through the real loader
        cat = Path(td) / "voices.json"
        base = json.loads(
            (REPO_ROOT / "seed" / "fixtures" / "voices" / "valid-rev1.json").read_text(
                encoding="utf-8"
            )
        )
        _write_catalog(cat, base)
        try:
            load_catalog(cat)
        except CatalogError as exc:
            fails.append(f"catalog round-trip: {exc}")

        # unsafe-write guards (SEC-NTS-M0-01): a symlinked target is refused and
        # its destination is untouched; no temp sibling survives a write.
        link = Path(td) / "voices-link.json"
        link.symlink_to(cat)
        before = cat.read_bytes()
        try:
            _write_catalog(link, base)
            fails.append("symlinked target accepted")
        except ValueError:
            pass
        if not link.is_symlink() or cat.read_bytes() != before:
            fails.append("symlinked target clobbered")
        stray = [p.name for p in Path(td).iterdir() if p.name.endswith(".tmp")]
        if stray:
            fails.append(f"stray temp file {stray}")

    # text read: strips exactly one final \n
    with tempfile.TemporaryDirectory() as td:
        p = Path(td) / "t.txt"
        for body, want in [(b"a\n", b"a"), (b"a\n\n", b"a\n"), (b"a", b"a")]:
            p.write_bytes(body)
            if _read_text(p) != want:
                fails.append(f"text read {body!r}")

    for f in fails:
        _log(f"FAIL {f}")
    if not fails:
        print("PASS self-test")
    return 0 if not fails else 1


if __name__ == "__main__":
    raise SystemExit(main())
