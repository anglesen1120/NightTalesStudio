"""Tests for tools/make_previews.py (04.2.3-T1).

Fake proxy = stdlib `http.server` on an ephemeral loopback port serving a
sine MP3 produced by the real ffmpeg. Fake storage = in-memory
`put_object`/`head_object`. No live proxy calls, no real bucket (the wire and
store behaviours are the ones the task contract pins down).
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

PROXY_TESTS_DIR = Path(__file__).resolve().parent
TOOL = PROXY_TESTS_DIR.parent / "tools" / "make_previews.py"

_spec = importlib.util.spec_from_file_location("make_previews", TOOL)
mk = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mk)

from nts_vieneu_proxy.catalog import load_catalog, slugify  # noqa: E402


# --- fakes ---------------------------------------------------------------


@pytest.fixture(scope="module")
def sine_mp3(tmp_path_factory) -> Path:
    """8 s sine MP3 via the real ffmpeg — feeds the fake proxy."""
    p = tmp_path_factory.mktemp("media") / "sine.mp3"
    subprocess.run(
        [
            os.environ.get("FFMPEG_PATH", "ffmpeg"),
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "lavfi",
            "-i",
            "sine=frequency=440:duration=8:sample_rate=48000",
            "-ac",
            "1",
            "-c:a",
            "libmp3lame",
            "-b:a",
            "128k",
            "-y",
            str(p),
        ],
        check=True,
    )
    return p


class _ProxyHandler(BaseHTTPRequestHandler):
    def log_message(self, *_a) -> None:  # keep test output quiet
        pass

    def _send(self, status: int, ctype: str, body: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        if self.path == "/health":
            self._send(200, "application/json", b'{"ok":true}')
        else:
            self._send(404, "application/json", b"{}")

    def do_POST(self) -> None:
        if self.path != "/v1/audio/speech":
            self._send(404, "application/json", b"{}")
            return
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length) or b"{}")
        voice = body.get("voice")
        self.server.voices.append(voice)
        status = self.server.voice_status.get(voice, 200)
        if status == 200:
            self._send(200, "audio/mpeg", self.server.sine)
        else:
            self._send(
                status, "application/json", json.dumps({"error": "nope"}).encode()
            )


@pytest.fixture
def fake_proxy(sine_mp3: Path):
    srv = ThreadingHTTPServer(("127.0.0.1", 0), _ProxyHandler)
    srv.sine = sine_mp3.read_bytes()
    srv.voices = []  # voice ids received, in order
    srv.voice_status = {}  # voice id → forced HTTP status
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    yield srv
    srv.shutdown()
    srv.server_close()
    t.join(timeout=5)


class MemStore:
    """In-memory `put_object`/`head_object` pair (storage seam)."""

    def __init__(self) -> None:
        self.objects: dict[tuple[str, str], tuple[bytes, str, str]] = {}
        self.broken_envs: set[str] = set()  # bucket names raising on every call
        self.head_len_offset = 0  # corrupt post-put HEAD length check
        self.head_status: int | None = None  # force a non-404/200 HEAD status
        self.puts: list[tuple[str, str]] = []

    def head_object(self, bucket: str, key: str):
        if bucket in self.broken_envs:
            raise RuntimeError("bucket unreachable")
        if self.head_status is not None:  # adapter reports a real error status
            return (self.head_status, None, None)
        obj = self.objects.get((bucket, key))
        if obj is None:
            return (404, None, None)
        data, ct, _cc = obj
        return (200, len(data) + self.head_len_offset, ct)

    def put_object(self, bucket, key, path, content_type, cache_control) -> None:
        if bucket in self.broken_envs:
            raise RuntimeError("bucket unreachable")
        self.puts.append((bucket, key))
        self.objects[(bucket, key)] = (
            Path(path).read_bytes(),
            content_type,
            cache_control,
        )


# --- helpers -------------------------------------------------------------


def _voice(id_: str, lang: str, active: bool = True, key=None) -> dict:
    return {
        "id": id_,
        "slug": slugify(id_),
        "displayName": id_,
        "language": lang,
        "gender": "male",
        "tags": [],
        "active": active,
        "previewStorageKey": key,
    }


def _catalog(tmp_path: Path, voices: list[dict], revision: int = 0) -> Path:
    doc = {
        "schema": "voices.v1",
        "revision": revision,
        "vieneuCommit": "c" * 40,
        "voices": voices,
    }
    p = tmp_path / "voices.json"
    p.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return p


def _texts(tmp_path: Path, vi: str = "Đêm khuya yên bình.", en: str = "The night is calm.") -> Path:
    d = tmp_path / "texts"
    d.mkdir(exist_ok=True)
    (d / "preview.vi.txt").write_bytes(vi.encode("utf-8") + b"\n")
    (d / "preview.en.txt").write_bytes(en.encode("utf-8") + b"\n")
    return d



def _manifest(tmp_path: Path) -> list[dict]:
    return json.loads((tmp_path / "previews.manifest.json").read_text(encoding="utf-8"))


def _manifest_row(tmp_path: Path, voice_id: str, n: int, media: bytes) -> tuple[Path, dict]:
    """Write a fake preview file + manifest row (upload-side fixtures)."""
    slug = slugify(voice_id)
    key = f"voice-previews/{slug}.v{n}.mp3"
    f = tmp_path / "out" / Path(key).name
    f.parent.mkdir(parents=True, exist_ok=True)
    f.write_bytes(media)
    row = {
        "id": voice_id,
        "slug": slug,
        "key": key,
        "sha256": hashlib.sha256(media).hexdigest(),
        "bytes": len(media),
        "duration_s": 8.0,
        "text_sha256": hashlib.sha256(b"t").hexdigest(),
        "catalog_revision": 0,
    }
    return f, row


def _fake_cli(tmp_path: Path, body: str) -> str:
    """Executable stand-in for the storage CLI (11.4.1 names are not fixed)."""
    p = tmp_path / "fake-storage-cli"
    p.write_text("#!/bin/sh\n" + body, encoding="utf-8")
    p.chmod(0o755)
    return str(p)


def _write_manifest(tmp_path: Path, rows: list[dict]) -> Path:
    mp = tmp_path / "previews.manifest.json"
    mp.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return mp

def _gen(fake_proxy, tmp_path, catalog: Path, bumps=(), texts: Path | None = None) -> int:
    if texts is None:
        texts = tmp_path / "texts"
        if not texts.exists():
            _texts(tmp_path)
    return mk.generate(
        catalog_path=catalog,
        out_dir=tmp_path / "out",
        bumps=set(bumps),
        base_url=f"http://127.0.0.1:{fake_proxy.server_port}",
        api_key="k" * 40,
        manifest_path=tmp_path / "previews.manifest.json",
        texts_dir=texts,
    )


# --- tests ---------------------------------------------------------------


def test_previews_loudnorm_command_exact() -> None:
    argv = mk._encode_argv("ffmpeg", Path("in.mp3"), Path("out.mp3"))
    assert argv == [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-i", "in.mp3",
        "-af", "loudnorm=I=-16:TP=-1.5:LRA=11",
        "-ac", "1", "-ar", "48000",
        "-c:a", "libmp3lame", "-b:a", "128k",
        "-y", "out.mp3",
    ]
    # A custom FFMPEG_PATH is honoured positionally, nothing else changes.
    assert mk._encode_argv("/opt/x/ffmpeg", Path("i"), Path("o"))[0] == "/opt/x/ffmpeg"


def test_previews_generate_writes_manifest_fields_exact(fake_proxy, tmp_path) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    assert _gen(fake_proxy, tmp_path, cat) == 0
    rows = _manifest(tmp_path)
    assert [r["id"] for r in rows] == ["Hải Đăng", "Ngọc Huyền"]  # sorted by slug
    for row in rows:
        assert set(row) == {
            "id", "slug", "key", "sha256", "bytes",
            "duration_s", "text_sha256", "catalog_revision",
        }
        assert row["key"] == f"voice-previews/{row['slug']}.v1.mp3"
        f = tmp_path / "out" / Path(row["key"]).name
        assert f.is_file()
        assert hashlib.sha256(f.read_bytes()).hexdigest() == row["sha256"]
        assert row["bytes"] == len(f.read_bytes()) <= 200_000
        assert row["duration_s"] <= 12.0
        assert row["catalog_revision"] == 0
        lang = "vi" if row["id"] == "Hải Đăng" else "en"
        text_bytes = (tmp_path / "texts" / f"preview.{lang}.txt").read_bytes()[:-1]
        assert row["text_sha256"] == hashlib.sha256(text_bytes).hexdigest()
    assert fake_proxy.voices == ["Hải Đăng", "Ngọc Huyền"]


def test_previews_same_text_keeps_version_and_skips(fake_proxy, tmp_path) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    assert _gen(fake_proxy, tmp_path, cat) == 0
    mp = tmp_path / "previews.manifest.json"
    manifest_bytes = mp.read_bytes()
    out_files = sorted(p.name for p in (tmp_path / "out").glob("*.mp3"))
    calls_before = len(fake_proxy.voices)

    assert _gen(fake_proxy, tmp_path, cat) == 0
    assert len(fake_proxy.voices) == calls_before  # nothing re-generated
    assert sorted(p.name for p in (tmp_path / "out").glob("*.mp3")) == out_files
    assert mp.read_bytes() == manifest_bytes  # no manifest diff


def test_previews_text_change_bumps_version(fake_proxy, tmp_path) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    assert _gen(fake_proxy, tmp_path, cat) == 0
    # New vi preview text → new text_sha256 → v2 for vi voices only.
    (tmp_path / "texts" / "preview.vi.txt").write_bytes(
        "Trăng khuya lặng yên.".encode("utf-8") + b"\n"
    )
    assert _gen(fake_proxy, tmp_path, cat) == 0
    rows = {r["id"]: r for r in _manifest(tmp_path)}
    assert rows["Hải Đăng"]["key"].endswith(".v2.mp3")
    assert rows["Ngọc Huyền"]["key"].endswith(".v1.mp3")
    assert (tmp_path / "out" / "hai-dang.v2.mp3").is_file()


def test_previews_bump_flag_bumps_only_that_voice(fake_proxy, tmp_path) -> None:
    cat = _catalog(
        tmp_path,
        [
            _voice("Hải Đăng", "vi"),
            _voice("Giọng Thơ", "vi"),
            _voice("Ngọc Huyền", "en"),
        ],
    )
    assert _gen(fake_proxy, tmp_path, cat) == 0
    assert _gen(fake_proxy, tmp_path, cat, bumps=("Giọng Thơ",)) == 0
    rows = {r["id"]: r for r in _manifest(tmp_path)}
    assert rows["Giọng Thơ"]["key"].endswith(".v2.mp3")
    assert rows["Hải Đăng"]["key"].endswith(".v1.mp3")
    assert rows["Ngọc Huyền"]["key"].endswith(".v1.mp3")
    assert (tmp_path / "out" / "giong-tho.v2.mp3").is_file()


def test_previews_rejects_over_12s(fake_proxy, tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(mk, "MAX_SECONDS", 1.0)
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    assert _gen(fake_proxy, tmp_path, cat) == 1
    assert not list((tmp_path / "out").glob("*.v*.mp3"))  # file deleted
    assert _manifest(tmp_path) == []  # no row for failed voices


def test_previews_rejects_over_200kb(fake_proxy, tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(mk, "MAX_BYTES", 1)
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    assert _gen(fake_proxy, tmp_path, cat) == 1
    assert not list((tmp_path / "out").glob("*.v*.mp3"))
    assert _manifest(tmp_path) == []


def test_previews_inactive_voice_skipped(fake_proxy, tmp_path) -> None:
    cat = _catalog(
        tmp_path,
        [_voice("Hải Đăng", "vi", active=False), _voice("Ngọc Huyền", "en")],
        revision=0,
    )
    assert _gen(fake_proxy, tmp_path, cat) == 0
    assert fake_proxy.voices == ["Ngọc Huyền"]
    rows = _manifest(tmp_path)
    assert [r["id"] for r in rows] == ["Ngọc Huyền"]


def test_previews_missing_key_exits_2(monkeypatch, tmp_path) -> None:
    monkeypatch.delenv("PROXY_API_KEY", raising=False)
    rc = mk.main(
        [
            "--generate",
            "--catalog",
            str(tmp_path / "voices.json"),
            "--out",
            str(tmp_path / "out"),
            "--manifest",
            str(tmp_path / "m.json"),
            "--texts",
            str(tmp_path),
        ]
    )
    assert rc == 2


def test_previews_bad_out_exits_2_without_traceback(
    fake_proxy, tmp_path, capsys, monkeypatch
) -> None:
    """A --out whose parent is a regular file is a config error: exit 2 before
    any speech call, never an uncaught traceback (QA defect D1, 2026-09-26)."""
    monkeypatch.setenv("PROXY_API_KEY", "k" * 40)
    monkeypatch.setenv(
        "PROXY_BASE_URL", f"http://127.0.0.1:{fake_proxy.server_port}"
    )
    blocker = tmp_path / "not-a-dir"
    blocker.write_text("x", encoding="utf-8")
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi")])

    rc = mk.main(
        [
            "--generate",
            "--catalog",
            str(cat),
            "--out",
            str(blocker / "out"),
            "--manifest",
            str(tmp_path / "previews.manifest.json"),
            "--texts",
            str(_texts(tmp_path)),
        ]
    )

    assert rc == 2
    err = capsys.readouterr().err
    assert "--out" in err and "not usable" in err
    assert fake_proxy.voices == []  # rejected before any speech call
    assert not (blocker / "out").exists()  # nothing was created


def test_previews_upload_sets_key_only_after_head_verified(
    tmp_path, sine_mp3
) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, sine_mp3.read_bytes())
    mp = _write_manifest(tmp_path, [row])
    store = MemStore()
    store.head_len_offset = 1  # post-PUT HEAD reports wrong length

    rc = mk.upload(
        catalog_path=cat,
        envs=["dev"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 1
    assert store.puts == [("nt-public-dev", row["key"])]  # PUT attempted
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] is None  # key not written
    assert doc["revision"] == 0


def test_previews_upload_refuses_overwrite_of_different_object(
    tmp_path, sine_mp3
) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, sine_mp3.read_bytes())
    mp = _write_manifest(tmp_path, [row])
    store = MemStore()
    # Bucket already holds different bytes under the same key.
    store.objects[("nt-public-dev", row["key"])] = (b"other", "audio/mpeg", "x")

    rc = mk.upload(
        catalog_path=cat,
        envs=["dev"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 1
    assert store.puts == []  # never overwrites
    assert store.objects[("nt-public-dev", row["key"])][0] == b"other"
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] is None
    assert doc["revision"] == 0


def test_previews_upload_partial_env_failure_leaves_key_null(
    tmp_path, sine_mp3
) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, sine_mp3.read_bytes())
    mp = _write_manifest(tmp_path, [row])
    store = MemStore()
    store.broken_envs.add("nt-public-prod")

    rc = mk.upload(
        catalog_path=cat,
        envs=["dev", "prod"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 1
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] is None
    assert doc["revision"] == 0

    # Re-run is idempotent: dev HEAD-hits (same length → skip), prod uploads,
    # key lands, revision bumps once.
    store.broken_envs.clear()
    rc = mk.upload(
        catalog_path=cat,
        envs=["dev", "prod"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 0
    assert store.puts.count(("nt-public-dev", row["key"])) == 1  # no re-PUT
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] == row["key"]
    assert doc["revision"] == 1


def test_previews_upload_bumps_revision_once_and_catalog_revalidates(
    tmp_path, sine_mp3
) -> None:
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    media = sine_mp3.read_bytes()
    _, row_a = _manifest_row(tmp_path, "Hải Đăng", 1, media)
    _, row_b = _manifest_row(tmp_path, "Ngọc Huyền", 1, media)
    mp = _write_manifest(tmp_path, [row_a, row_b])
    store = MemStore()

    rc = mk.upload(
        catalog_path=cat,
        envs=["dev", "prod"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 0
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["revision"] == 1  # +1 once, not once per key
    assert doc["voices"][0]["previewStorageKey"] == row_a["key"]
    assert doc["voices"][1]["previewStorageKey"] == row_b["key"]
    # The written catalog passes the real D9 loader (rev 1, keys match slugs).
    cat2 = load_catalog(cat)
    assert cat2.revision == 1
    # Bucket side: immutable metadata preserved.
    obj = store.objects[("nt-public-dev", row_a["key"])]
    assert obj[1] == "audio/mpeg"
    assert obj[2] == "public, max-age=31536000, immutable"


# --- storage-CLI adapter: "could not ask" is never "absent" (T17) ---------
# W1 review (SEC low + code P2): head_object used to collapse every non-zero
# CLI exit into 404, so upload() could PUT over an existing immutable key; the
# `HEAD → <status>` branch was unreachable with the real adapter.


def test_previews_cli_head_object_missing_key_is_404(tmp_path) -> None:
    cli = _fake_cli(
        tmp_path,
        'echo "An error occurred (404) when calling the HeadObject operation: '
        'Not Found" >&2\nexit 255\n',
    )
    assert mk.CliStorage(cli=cli).head_object(
        "nt-public-dev", "voice-previews/x.v1.mp3"
    ) == (404, None, None)


def test_previews_cli_head_object_error_is_not_absence(tmp_path) -> None:
    cli = _fake_cli(
        tmp_path,
        'echo "An error occurred (500) when calling the HeadObject operation: '
        'InternalError" >&2\nexit 255\n',
    )
    with pytest.raises(RuntimeError):
        mk.CliStorage(cli=cli).head_object("nt-public-dev", "voice-previews/x.v1.mp3")


def test_previews_upload_cli_head_error_never_puts(tmp_path, sine_mp3) -> None:
    """Real adapter, failing HEAD: no PUT, key stays null, exit 1."""
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi")])
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, sine_mp3.read_bytes())
    mp = _write_manifest(tmp_path, [row])
    log = tmp_path / "cli.log"
    cli = _fake_cli(
        tmp_path,
        f'echo "$*" >> "{log}"\necho "Unable to locate credentials" >&2\nexit 255\n',
    )
    rc = mk.upload(
        catalog_path=cat,
        envs=["dev"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=mk.CliStorage(cli=cli),
    )
    assert rc == 1
    calls = log.read_text(encoding="utf-8")
    assert "head-object" in calls  # it did ask
    assert "put-object" not in calls  # ... and never wrote over an unchecked key
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] is None
    assert doc["revision"] == 0


def test_previews_upload_cli_404_still_puts(tmp_path, sine_mp3) -> None:
    """A genuine 404 still uploads (real adapter, stateful fake CLI)."""
    media = sine_mp3.read_bytes()
    # Two languages: D9 needs >= 1 active voice per language once revision >= 1.
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi"), _voice("Ngọc Huyền", "en")])
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, media)
    mp = _write_manifest(tmp_path, [row])
    state, log = tmp_path / "head-state", tmp_path / "cli.log"
    cli = _fake_cli(
        tmp_path,
        'case "$*" in\n'
        "  *head-object*)\n"
        f'    if [ -f "{state}" ]; then\n'
        f'      echo \'{{"ContentLength": {len(media)}, "ContentType": "audio/mpeg"}}\'\n'
        "    else\n"
        f'      : > "{state}"\n'
        '      echo "An error occurred (404) when calling the HeadObject '
        'operation: Not Found" >&2\n'
        "      exit 255\n"
        "    fi ;;\n"
        f'  *put-object*) echo "$*" >> "{log}" ;;\n'
        "esac\nexit 0\n",
    )
    rc = mk.upload(
        catalog_path=cat,
        envs=["dev"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=mk.CliStorage(cli=cli),
    )
    assert rc == 0
    assert log.read_text(encoding="utf-8").count("put-object") == 1
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] == row["key"]
    assert doc["revision"] == 1


def test_previews_upload_head_error_status_never_puts(tmp_path, sine_mp3) -> None:
    """Any adapter reporting a non-404/200 status fails the voice (no PUT)."""
    cat = _catalog(tmp_path, [_voice("Hải Đăng", "vi")])
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, sine_mp3.read_bytes())
    mp = _write_manifest(tmp_path, [row])
    store = MemStore()
    store.head_status = 503
    rc = mk.upload(
        catalog_path=cat,
        envs=["dev"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 1
    assert store.puts == []
    doc = json.loads(cat.read_text(encoding="utf-8"))
    assert doc["voices"][0]["previewStorageKey"] is None
    assert doc["revision"] == 0


# --- unsafe-write guards (SEC medium, T17) --------------------------------


def test_previews_write_refuses_symlinked_target(tmp_path) -> None:
    real = tmp_path / "real.json"
    real.write_text('{"keep": true}\n', encoding="utf-8")
    link = tmp_path / "voices.json"
    link.symlink_to(real)
    with pytest.raises(ValueError):
        mk._write_catalog(link, {"schema": "voices.v1", "revision": 0})
    assert link.is_symlink()  # the link itself is not replaced
    assert real.read_text(encoding="utf-8") == '{"keep": true}\n'
    assert sorted(p.name for p in tmp_path.iterdir()) == ["real.json", "voices.json"]


def test_previews_write_leaves_no_tmp_sibling_on_failure(tmp_path, monkeypatch) -> None:
    target = tmp_path / "voices.json"
    target.write_text('{"revision": 0}\n', encoding="utf-8")

    def boom(*_a, **_k):
        raise OSError("disk full")

    monkeypatch.setattr(mk.os, "replace", boom)
    with pytest.raises(OSError):
        mk._write_catalog(target, {"schema": "voices.v1", "revision": 1})
    assert target.read_text(encoding="utf-8") == '{"revision": 0}\n'  # untouched
    assert sorted(p.name for p in tmp_path.iterdir()) == ["voices.json"]  # no .tmp


def test_previews_upload_refuses_symlinked_catalog(tmp_path, sine_mp3) -> None:
    """A symlinked tracked catalog exits 2 before any bucket traffic."""
    real = _catalog(tmp_path, [_voice("Hải Đăng", "vi")])
    link = tmp_path / "voices-linked.json"
    link.symlink_to(real)
    _, row = _manifest_row(tmp_path, "Hải Đăng", 1, sine_mp3.read_bytes())
    mp = _write_manifest(tmp_path, [row])
    store = MemStore()
    rc = mk.upload(
        catalog_path=link,
        envs=["dev"],
        out_dir=tmp_path / "out",
        manifest_path=mp,
        storage=store,
    )
    assert rc == 2
    assert store.puts == []  # refused before the PUTs
    assert link.is_symlink()
    assert json.loads(real.read_text(encoding="utf-8"))["revision"] == 0
