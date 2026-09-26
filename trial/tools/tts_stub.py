#!/usr/bin/env python3
"""tts_stub.py — scripted fake OpenAI-compatible speech API for the 05.4.1-T1 MPT TTS spike.

Serves only `POST /v1/audio/speech` on 127.0.0.1 — the path MoneyPrinterTurbo's
`app/services/voice.py:_openai_compatible_tts` builds from `[kokoro] base_url`
(`url = f"{base_url}/audio/speech"`, voice.py:2161) — so the pinned MPT can be run
against a fake Kokoro/OpenAI speech server with NO real TTS call. Every other path
or method answers 404 (voice.py:2163-2164 sends `Authorization: Bearer <key>` only
when `[kokoro] api_key` is non-empty; voice.py:2177-2190 retries up to 3 attempts
and really decodes the body with MoviePy `AudioFileClip`).

Run:  uv run --no-project --python 3.12 trial/tools/tts_stub.py [flags]
Exit codes (01 D9): 0 ok · 1 self-test failed · 2 usage/environment error.

Scripting (`--script FILE`, re-read on every request so a running stub can be
re-scripted between spike cases):

    {
      "case": "S3",                                     # optional; a change resets the call counter
      "default": "ok",                                  # mode when nothing else matches
      "by_call": {"3": "e500"},                         # mode for call number 3
      "by_input_contains": {"__tone__": "sine"},        # mode when `input` contains the text
      "slow_seconds": 130                               # delay of the `slow` mode
    }

A changed `case` resets the counter to 0, so re-running the same case number after
a killed run starts from call 1 again (the counter is per tag, never per process).

Modes: `ok` · `e500` · `abort` · `empty` · `sine` · `slow`.

  ok     200 audio/mpeg, the whole speech fixture
  e500   500 {"error":{"code":"server_error", …}}
  abort  200 audio/mpeg with Content-Length set to the *full* fixture size, then
         exactly 4096 body bytes and a closed socket (the client sees a truncated
         / incomplete read, the way a dying upstream does)
  empty  200 Content-Length: 0 with no body (MPT: `if not response.content` →
         ValueError → retry → None, voice.py:2190)
  sine   200 audio/mpeg, a 440 Hz tone fixture (no speech)
  slow   200 like `ok`, but the first byte is written only after `--slow-delay`
         seconds (default 130 > MPT's `timeout=120`, voice.py:2183)

The request body is the OpenAI speech shape
`{model,input,voice,response_format,speed}` (voice.py:2165-2176); the stub reads
only `input`. The narration text is NEVER logged or stored — each audio-speech
request appends one row with hash and length only:

    {"n", "ts", "input_sha256", "input_len", "mode", "status", "bytes_written",
     "aborted", "case"}

`mode` is the dispatched mode, or `"denied"` when the Authorization check rejected
the request before it reached the endpoint (the call counter is not advanced —
mirroring a server that rejects unauthenticated work — and such a row keeps
`n` at its current value).

Fixtures (`--fixtures DIR`, default `$NTS_HOME/runs/spike-05/fixtures/`, `NTS_HOME`
defaulting to `<repo>/.nts`): `speech-en.mp3`, `speech-vi.mp3`, `sine-60s.mp3`,
made on this Mac with `say` + `ffmpeg` and NEVER committed. A required fixture that
is missing exits 2 and prints its exact command. `-id3v2_version 0` keeps the MPEG
sync word (`0xFF 0xFB` / `0xFF 0xF3`) at bytes 0-1, which `--self-test` asserts on
every fixture it serves (plain ffmpeg MP3 output starts with an "ID3" tag instead).

  say -v Samantha -o /tmp/speech-en.aiff "<english narration>"
  /opt/homebrew/bin/ffmpeg -y -i /tmp/speech-en.aiff -c:a libmp3lame -b:a 192k -ar 44100 -id3v2_version 0 \\
      "$NTS_HOME/runs/spike-05/fixtures/speech-en.mp3"
  say -v Linh -o /tmp/speech-vi.aiff "<vietnamese narration>"
  /opt/homebrew/bin/ffmpeg -y -i /tmp/speech-vi.aiff -c:a libmp3lame -b:a 192k -ar 44100 -id3v2_version 0 \\
      "$NTS_HOME/runs/spike-05/fixtures/speech-vi.mp3"
  /opt/homebrew/bin/ffmpeg -y -f lavfi -i sine=frequency=440:duration=60 -c:a libmp3lame -b:a 192k -ar 44100 \\
      -id3v2_version 0 "$NTS_HOME/runs/spike-05/fixtures/sine-60s.mp3"
"""

import argparse
import hashlib
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
DEFAULT_PORT = 18004
# Cap on a request body (SEC-NTS-M0-10): the stub reads Content-Length bytes into
# memory, and 1 MiB is far above any narration (a 60 s voice line is a few KB).
MAX_BODY = 1 << 20
SPEECH_PATH = "/v1/audio/speech"
MODES = ("ok", "e500", "abort", "empty", "sine", "slow")
DEFAULT_SLOW_DELAY = 130.0
# Body bytes written by `abort` before the socket is closed.
ABORT_BYTES = 4096
SPEECH_FIXTURE = "speech-en.mp3"
TONE_FIXTURE = "sine-60s.mp3"
REQUIRED_FIXTURES = (SPEECH_FIXTURE, "speech-vi.mp3", TONE_FIXTURE)
# 0xFF 0xFB (MPEG-1 layer III) / 0xFF 0xF3 (MPEG-2 layer III).
MPEG_SYNC = (b"\xff\xfb", b"\xff\xf3")
FIXTURES_SUBPATH = ("runs", "spike-05", "fixtures")
FFMPEG = "/opt/homebrew/bin/ffmpeg"
MP3_ARGS = "-c:a libmp3lame -b:a 192k -ar 44100 -id3v2_version 0"
EN_TEXT = "NightTales spike voice check: the quick brown fox jumps over the lazy dog."
VI_TEXT = "Kiểm tra giọng đọc cho NightTales: chú cáo nâu nhanh nhẹn nhảy qua con chó lười biếng."
# Set to "1" inside the self-test's own child process so the child does not spawn
# a grandchild (that recursion would never terminate).
SELFTEST_CHILD_ENV = "NTS_TTS_STUB_SELFTEST_CHILD"

# The exact commands that produce each fixture; printed when one is missing.
FIXTURE_RECIPES = {
    SPEECH_FIXTURE: ('say -v Samantha -o /tmp/speech-en.aiff "{0}"'.format(EN_TEXT), "/tmp/speech-en.aiff"),
    "speech-vi.mp3": ('say -v Linh -o /tmp/speech-vi.aiff "{0}"'.format(VI_TEXT), "/tmp/speech-vi.aiff"),
    TONE_FIXTURE: ("/opt/homebrew/bin/ffmpeg -y -f lavfi -i sine=frequency=440:duration=60", None),
}

E500_BODY = {
    "error": {
        "code": "server_error",
        "message": "The server had an error while processing your request.",
        "type": "server_error",
    }
}
NOT_FOUND_BODY = {
    "error": {
        "code": "not_found",
        "message": "not found: the stub serves only POST /v1/audio/speech",
        "type": "invalid_request_error",
    }
}
INVALID_KEY_BODY = {
    "error": {
        "code": "invalid_api_key",
        "message": "Incorrect API key provided.",
        "type": "invalid_request_error",
    }
}


def eprint(*a):
    print(*a, file=sys.stderr)


def repo_root():
    """The repo containing this file (`<repo>/trial/tools/tts_stub.py`)."""
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.dirname(os.path.dirname(here))


def nts_home():
    """`$NTS_HOME`, defaulting to `<repo>/.nts` (gitignored)."""
    value = os.environ.get("NTS_HOME") or ""
    if value.strip():
        return os.path.abspath(os.path.expanduser(value.strip()))
    return os.path.join(repo_root(), ".nts")


def default_fixtures_dir():
    return os.path.join(nts_home(), *FIXTURES_SUBPATH)


def missing_fixtures(fixtures_dir):
    return [name for name in REQUIRED_FIXTURES if not os.path.isfile(os.path.join(fixtures_dir, name))]


def fixture_commands_text(name, fixtures_dir):
    """The exact `say` + `ffmpeg` commands producing fixture `name` in `fixtures_dir`."""
    target = os.path.join(fixtures_dir, name)
    producer, source = FIXTURE_RECIPES[name]
    if source is None:
        return [f"{producer} {MP3_ARGS} \\\n    \"{target}\""]
    return [producer, f"{FFMPEG} -y -i {source} {MP3_ARGS} \\\n    \"{target}\""]


def print_fixture_commands(missing, fixtures_dir):
    eprint(f"tts_stub.py: missing fixture(s) in {fixtures_dir}: {', '.join(missing)}")
    eprint("tts_stub.py: create them with (macOS `say` + ffmpeg; these files are never committed):")
    for name in missing:
        for line in fixture_commands_text(name, fixtures_dir):
            for chunk in line.split("\n"):
                eprint(f"  {chunk}")


def sync_word_ok(blob):
    """True when the MPEG frame sync word sits at bytes 0-1 of `blob`."""
    return bool(blob) and blob[:2] in MPEG_SYNC


class State:
    """Call counter + script + fixtures, shared by the handler threads."""

    def __init__(self, script_path, calls_file, fixtures_dir, require_key=False, key="",
                 slow_delay=DEFAULT_SLOW_DELAY):
        self.script_path = script_path
        self.calls_file = calls_file
        self.fixtures_dir = fixtures_dir
        self.require_key = bool(require_key)
        self.key = key or ""
        self.slow_delay = float(slow_delay)
        self.lock = threading.Lock()
        self.n = 0
        self.case = ""
        self.fixtures = {}

    def load_script(self):
        if not self.script_path:
            return {"default": "ok"}
        try:
            with open(self.script_path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
        except (OSError, ValueError) as exc:
            eprint(f"tts_stub: script unreadable ({type(exc).__name__}): {exc}")
            return {"default": "ok"}
        if not isinstance(data, dict):
            return {"default": "ok"}
        return data

    def next_call(self, script):
        case = str(script.get("case") or "")
        with self.lock:
            if case != self.case:
                self.case = case
                self.n = 0
            self.n += 1
            return self.n

    def fixture(self, name):
        """Fixture bytes, or None when the file is missing/unreadable."""
        with self.lock:
            blob = self.fixtures.get(name)
        if blob is not None:
            return blob
        try:
            with open(os.path.join(self.fixtures_dir, name), "rb") as fh:
                blob = fh.read()
        except OSError as exc:
            eprint(f"tts_stub: fixture {name} unreadable: {exc}")
            return None
        with self.lock:
            self.fixtures[name] = blob
        return blob

    def record(self, row):
        if not self.calls_file:
            return
        with self.lock:
            with open(self.calls_file, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(row, sort_keys=True) + "\n")
                fh.flush()


def pick_mode(script, call_n, text):
    contains = script.get("by_input_contains")
    if isinstance(contains, dict):
        for needle, mode in contains.items():
            if needle and needle in text:
                return str(mode)
    by_call = script.get("by_call")
    if isinstance(by_call, dict):
        mode = by_call.get(str(call_n))
        if mode is None:
            mode = by_call.get(call_n)
        if mode is not None:
            return str(mode)
    return str(script.get("default") or "ok")


def slow_seconds(script, fallback):
    value = script.get("slow_seconds", fallback)
    try:
        return max(0.0, float(value))
    except (TypeError, ValueError):
        return float(fallback)


def call_row(state, n, text, mode, status, bytes_written, aborted):
    return {
        "n": n,
        "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "input_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
        "input_len": len(text),
        "mode": mode,
        "status": status,
        "bytes_written": bytes_written,
        "aborted": aborted,
        "case": state.case,
    }


class Handler(BaseHTTPRequestHandler):
    server_version = "nts-tts-stub/1"
    sys_version = ""
    protocol_version = "HTTP/1.0"

    # Every request line on stdout, so the spike runner can slice raw evidence.
    def log_message(self, fmt, *args):
        print(f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} {self.address_string()} {fmt % args}", flush=True)

    def _send(self, status, body, content_type="application/json"):
        """Write a complete response; returns the body bytes actually written."""
        if isinstance(body, (dict, list)):
            payload = json.dumps(body).encode("utf-8")
        else:
            payload = body
        try:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return len(payload)
        except (BrokenPipeError, ConnectionResetError):
            # The client (MPT) gave up first — normal for the `slow` mode.
            print(
                f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
                f"client gone before the response was written (status {status})",
                flush=True,
            )
            return 0

    def _send_truncated(self, status, blob, first_bytes, content_type="audio/mpeg"):
        """Advertise `len(blob)` but write only `first_bytes`, then close.

        The client sees a body shorter than Content-Length, i.e. an incomplete
        read, which is what a dying upstream produces.
        """
        written = 0
        try:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(blob)))
            self.end_headers()
            self.wfile.write(blob[:first_bytes])
            self.wfile.flush()
            written = min(first_bytes, len(blob))
        except (BrokenPipeError, ConnectionResetError):
            pass
        self.close_connection = True
        return written

    def _reject_route(self):
        self._send(404, NOT_FOUND_BODY)

    def do_GET(self):  # noqa: N802 (BaseHTTPRequestHandler API)
        self._reject_route()

    def do_HEAD(self):  # noqa: N802
        self._reject_route()

    def do_PUT(self):  # noqa: N802
        self._reject_route()

    def do_PATCH(self):  # noqa: N802
        self._reject_route()

    def do_DELETE(self):  # noqa: N802
        self._reject_route()

    def do_OPTIONS(self):  # noqa: N802
        self._reject_route()

    def do_POST(self):  # noqa: N802 (BaseHTTPRequestHandler API)
        if self.path != SPEECH_PATH:
            self._reject_route()
            return
        # QA-0541-11: a malformed or negative Content-Length must never kill the
        # handler thread — answer 400 and record the attempt in the calls file.
        state = self.server.state
        raw_length = self.headers.get("Content-Length")
        try:
            length = int(raw_length) if raw_length is not None else 0
        except (TypeError, ValueError):
            length = -1
        if length < 0:
            print(
                f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
                f"speech rejected: bad Content-Length {raw_length!r} (case={state.case})",
                flush=True,
            )
            written = self._send(400, {
                "error": {
                    "code": "invalid_content_length",
                    "message": "malformed or negative Content-Length",
                    "type": "invalid_request_error",
                }
            })
            state.record(call_row(state, state.n, "", "bad_content_length", 400, written, False))
            return
        if length > MAX_BODY:
            self._send(
                413,
                {
                    "error": {
                        "code": "body_too_large",
                        "message": f"request body over {MAX_BODY} bytes",
                    }
                },
            )
            return
        raw = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            body = {}
        if not isinstance(body, dict):
            body = {}
        text = body.get("input")
        text = text if isinstance(text, str) else ""
        auth = self.headers.get("Authorization") or ""
        if state.require_key and auth != f"Bearer {state.key}":
            print(
                f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
                f"speech rejected: bad Authorization (input_len={len(text)}, case={state.case})",
                flush=True,
            )
            written = self._send(401, INVALID_KEY_BODY)
            state.record(call_row(state, state.n, text, "denied", 401, written, False))
            return

        script = state.load_script()
        call_n = state.next_call(script)
        mode = pick_mode(script, call_n, text)
        if mode not in MODES:
            eprint(f"tts_stub: unknown mode {mode!r} for call {call_n}; using ok")
            mode = "ok"

        delay = state.slow_delay
        if mode == "slow":
            delay = slow_seconds(script, state.slow_delay)

        print(
            f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
            f"tts call n={call_n} mode={mode} input_len={len(text)} "
            f"auth={int(bool(auth))} case={state.case}",
            flush=True,
        )

        if mode == "empty":
            written = self._send(200, b"", content_type="audio/mpeg")
            state.record(call_row(state, call_n, text, mode, 200, written, False))
            return
        if mode == "slow":
            # The row keeps mode "slow": the observable behavior is the delay.
            time.sleep(delay)
        if mode == "e500":
            written = self._send(500, E500_BODY)
            state.record(call_row(state, call_n, text, mode, 500, written, False))
            return

        name = TONE_FIXTURE if mode == "sine" else SPEECH_FIXTURE
        blob = state.fixture(name)
        if blob is None:
            eprint(f"tts_stub: cannot serve mode {mode!r}: fixture {name} unreadable")
            written = self._send(500, E500_BODY)
            state.record(call_row(state, call_n, text, mode, 500, written, False))
            return

        if mode == "abort":
            written = self._send_truncated(200, blob, ABORT_BYTES)
            state.record(call_row(state, call_n, text, mode, 200, written, True))
            return

        written = self._send(200, blob, content_type="audio/mpeg")
        state.record(call_row(state, call_n, text, mode, 200, written, False))


class StubHTTPServer(ThreadingHTTPServer):
    """ThreadingHTTPServer with a large accept backlog.

    socketserver's default `request_queue_size` is 5 and `listen()` runs inside
    `TCPServer.__init__`, so it can only be raised on a subclass. A driver that
    opens many connections at once otherwise gets ECONNRESET on the excess and
    silently loses evidence rows.
    """

    daemon_threads = True
    request_queue_size = 512


def make_server(port, state):
    server = StubHTTPServer((HOST, port), Handler)
    server.state = state
    return server


def serve(port, script_path, calls_file, fixtures_dir, require_key, key, slow_delay):
    state = State(script_path, calls_file, fixtures_dir, require_key=require_key, key=key,
                  slow_delay=slow_delay)
    server = make_server(port, state)
    eprint(f"tts_stub: listening on http://{HOST}:{server.server_port}{SPEECH_PATH}")
    eprint(f"tts_stub: fixtures {fixtures_dir}")
    if script_path:
        eprint(f"tts_stub: script {script_path}")
    if calls_file:
        eprint(f"tts_stub: calls file {calls_file}")
    if require_key:
        eprint(f"tts_stub: requiring {len(key)}-char Bearer key")
    eprint(f"tts_stub: slow delay {slow_delay:g}s; MPT [kokoro] base_url = http://{HOST}:{server.server_port}/v1")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


def self_test(fixtures_dir):
    """Drive real HTTP against the stub on an ephemeral port. 0 ok · 1 failed · 2 no fixtures."""
    import http.client
    import shutil
    import subprocess
    import tempfile

    failures = []

    def expect(name, cond, detail=""):
        if cond:
            print(f"PASS selftest:{name}")
        else:
            print(f"FAIL selftest:{name}{': ' + detail if detail else ''}")
            failures.append(name)

    def request(port, text="", key=None, path=SPEECH_PATH, method="POST", raw_body=None,
                with_auth=True, timeout=30):
        payload = raw_body if raw_body is not None else json.dumps(
            {
                "model": "kokoro",
                "input": text,
                "voice": "af_heart",
                "response_format": "mp3",
                "speed": 1.0,
            }
        )
        headers = {"Content-Type": "application/json"}
        if key is not None:
            headers["Authorization"] = f"Bearer {key}"
        elif with_auth:
            headers["Authorization"] = "Bearer selftest-key"
        conn = http.client.HTTPConnection(HOST, port, timeout=timeout)
        try:
            conn.request(method, path, body=payload, headers=headers)
            resp = conn.getresponse()
            try:
                data = resp.read()
                err = None
            except Exception as exc:  # noqa: BLE001 (IncompleteRead / connection errors)
                data = getattr(exc, "partial", b"")
                err = exc
            return resp.status, resp.headers, data, err
        finally:
            conn.close()

    empty_dir = tempfile.mkdtemp(prefix="tts-stub-empty-")
    try:
        # (0) empty --fixtures dir: the *serve* path exits 2 with the exact
        # say/ffmpeg commands. Asserted in-process plus against a second real
        # instance (spawned as the serve path, so it exits instead of nesting).
        expect("fixtures.empty-missing", missing_fixtures(empty_dir) == list(REQUIRED_FIXTURES),
               str(missing_fixtures(empty_dir)))
        commands = "\n".join(
            line for name in REQUIRED_FIXTURES for line in fixture_commands_text(name, empty_dir)
        )
        expect("fixtures.commands-have-say", "say -v " in commands)
        expect("fixtures.commands-have-ffmpeg", "/opt/homebrew/bin/ffmpeg" in commands)
        expect("fixtures.commands-have-encoder", "libmp3lame" in commands)
        expect("fixtures.commands-have-sine", "sine=frequency=440:duration=60" in commands)
        if os.environ.get(SELFTEST_CHILD_ENV) != "1":
            try:
                env = dict(os.environ, **{SELFTEST_CHILD_ENV: "1"})
                proc = subprocess.run(
                    [sys.executable, os.path.abspath(__file__), "--self-test",
                     "--fixtures", empty_dir],
                    capture_output=True, text=True, timeout=120, env=env,
                )
                expect("fixtures.empty-dir-exit-2", proc.returncode == 2, f"got {proc.returncode}")
                expect("fixtures.empty-dir-prints-commands",
                       "libmp3lame" in (proc.stdout + proc.stderr),
                       (proc.stdout + proc.stderr)[-200:])
            except (OSError, subprocess.SubprocessError) as exc:
                expect("fixtures.empty-dir-exit-2", False, f"{type(exc).__name__}: {exc}")
    finally:
        shutil.rmtree(empty_dir, ignore_errors=True)

    # (1) real fixtures must exist for the rest of the self-test.
    real_missing = missing_fixtures(fixtures_dir)
    if real_missing:
        print_fixture_commands(real_missing, fixtures_dir)
        print(f"FAIL selftest:fixtures.present{': ' + ', '.join(real_missing)}")
        return 2

    sizes = {name: os.path.getsize(os.path.join(fixtures_dir, name)) for name in REQUIRED_FIXTURES}
    for name in REQUIRED_FIXTURES:
        with open(os.path.join(fixtures_dir, name), "rb") as fh:
            head = fh.read(2)
        expect(f"fixture.{name}.sync-word", head in MPEG_SYNC, head.hex())
        expect(f"fixture.{name}.not-empty", sizes[name] > 10_000, str(sizes[name]))

    with tempfile.TemporaryDirectory() as td:
        script_path = os.path.join(td, "script.json")
        calls_file = os.path.join(td, "calls.jsonl")
        tiny_dir = os.path.join(td, "tiny")
        os.makedirs(tiny_dir)
        tiny_sizes = {}
        for name in REQUIRED_FIXTURES:
            with open(os.path.join(fixtures_dir, name), "rb") as src:
                head = src.read(8192)
            with open(os.path.join(tiny_dir, name), "wb") as dst:
                dst.write(head)
            tiny_sizes[name] = len(head)
        with open(os.path.join(tiny_dir, SPEECH_FIXTURE), "rb") as fh:
            tiny_head = fh.read(2)
        expect("fixture.tiny.sync-word", tiny_head in MPEG_SYNC, tiny_head.hex())

        def write_script(obj, path=script_path):
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(obj, fh)

        case_a = f"selftest#{int(time.time())}"
        case_b = f"selftest-b#{int(time.time())}"
        write_script(
            {
                "case": case_a,
                "default": "ok",
                "by_call": {"3": "e500", "4": "abort", "5": "empty"},
                "by_input_contains": {"__tone__": "sine"},
            }
        )

        # Server A: real fixtures, no auth, calls file recorded.
        state_a = State(script_path, calls_file, fixtures_dir, require_key=False, slow_delay=1.0)
        server_a = make_server(0, state_a)
        port_a = server_a.server_port
        thread_a = threading.Thread(target=server_a.serve_forever, daemon=True)
        thread_a.start()

        # Server B: tiny fixtures, auth required, slow delay 1 s (cheap + fast).
        script_b = os.path.join(td, "script-b.json")
        calls_b = os.path.join(td, "calls-b.jsonl")
        write_script({"case": case_b, "default": "slow", "slow_seconds": 1.0}, script_b)
        state_b = State(script_b, calls_b, tiny_dir, require_key=True, key="selftest-key", slow_delay=1.0)
        server_b = make_server(0, state_b)
        port_b = server_b.server_port
        thread_b = threading.Thread(target=server_b.serve_forever, daemon=True)
        thread_b.start()

        try:
            # (a) ok -> 200 audio/mpeg, the whole speech fixture, valid sync word
            head_text = "selftest narration one"
            status, headers, data, err = request(port_a, head_text)
            expect("ok.status", status == 200, f"got {status}")
            expect("ok.content-type", headers.get("Content-Type") == "audio/mpeg", str(headers.get("Content-Type")))
            expect("ok.bytes", len(data) == sizes[SPEECH_FIXTURE], f"got {len(data)} of {sizes[SPEECH_FIXTURE]}")
            expect("ok.sync-word", sync_word_ok(data), data[:2].hex())
            expect("ok.no-read-error", err is None, repr(err))

            # (b) by_input_contains -> the tone fixture
            status, headers, data, err = request(port_a, "a __tone__ marker")
            expect("sine.status", status == 200, f"got {status}")
            expect("sine.bytes", len(data) == sizes[TONE_FIXTURE], f"got {len(data)} of {sizes[TONE_FIXTURE]}")
            expect("sine.sync-word", sync_word_ok(data), data[:2].hex())

            # (c) by_call 3 -> 500 server_error
            status, headers, data, err = request(port_a, "selftest narration three")
            expect("e500.status", status == 500, f"got {status}")
            expect("e500.code", json.loads(data)["error"]["code"] == "server_error")

            # (d) by_call 4 -> abort: full-length Content-Length, 4096 bytes, incomplete read
            status, headers, data, err = request(port_a, "selftest narration four")
            expect("abort.status", status == 200, f"got {status}")
            expect("abort.content-type", headers.get("Content-Type") == "audio/mpeg")
            expect("abort.content-length-full",
                   int(headers.get("Content-Length") or -1) == sizes[SPEECH_FIXTURE],
                   str(headers.get("Content-Length")))
            expect("abort.incomplete-read", isinstance(err, http.client.IncompleteRead), repr(err))
            expect("abort.bytes", len(data) == ABORT_BYTES, f"got {len(data)}")
            expect("abort.sync-word", sync_word_ok(data), data[:2].hex())

            # (e) by_call 5 -> empty: 200, Content-Length 0, no body
            status, headers, data, err = request(port_a, "selftest narration five")
            expect("empty.status", status == 200, f"got {status}")
            expect("empty.bytes", data == b"", f"got {len(data)}")
            expect("empty.content-length", headers.get("Content-Length") == "0", str(headers.get("Content-Length")))

            # (f) case change resets the counter (n=1 on the first call of a new case)
            write_script({"case": f"{case_a}-next", "default": "ok", "by_call": {"1": "empty"}})
            status, _, data, _ = request(port_a, "selftest narration six")
            expect("case-reset.n", state_a.n == 1, f"got {state_a.n}")
            expect("case-reset.status", status == 200 and data == b"", f"{status} {len(data)}")

            # (g) unknown route -> 404 JSON on every verb
            for method, path, label in (
                ("GET", SPEECH_PATH, "get"),
                ("POST", "/v1/audio/speechs", "post-sibling"),
                ("POST", "/v1/images/generations", "post-other"),
                ("PUT", SPEECH_PATH, "put"),
                ("DELETE", SPEECH_PATH, "delete"),
            ):
                status, headers, data, _ = request(port_a, "x", method=method, path=path)
                expect(f"404.{label}", status == 404, f"got {status}")
                expect(f"404.{label}.code", json.loads(data)["error"]["code"] == "not_found")

            # (h) auth: absent + wrong header -> 401, correct key -> 200 after 1 s delay
            status, headers, data, _ = request(port_b, "selftest auth absent", key=None, with_auth=False)
            expect("auth.absent-401", status == 401, f"got {status}")
            expect("auth.absent-code", json.loads(data)["error"]["code"] == "invalid_api_key")
            status, _, data, _ = request(port_b, "selftest auth wrong", key="nope")
            expect("auth.wrong-401", status == 401, f"got {status}")
            expect("auth.counter-untouched", state_b.n == 0, f"got {state_b.n}")
            t0 = time.time()
            status, headers, data, err = request(port_b, "selftest auth ok", key="selftest-key")
            elapsed = time.time() - t0
            expect("slow.status", status == 200, f"got {status}")
            expect("slow.delays", elapsed >= 1.0, f"took {elapsed:.3f}s")
            expect("slow.bytes", len(data) == tiny_sizes[SPEECH_FIXTURE], f"got {len(data)}")
            expect("slow.sync-word", sync_word_ok(data), data[:2].hex())

            # (i) unknown mode falls back to ok
            write_script({"case": "selftest-bad-mode", "default": "not-a-mode"})
            status, _, data, _ = request(port_a, "selftest narration seven")
            expect("unknown-mode.ok", status == 200 and len(data) == sizes[SPEECH_FIXTURE], f"got {status}")

            # (j) calls file: exact row shape, hashes only, never the narration text
            rows = [json.loads(line) for line in open(calls_file, encoding="utf-8")]
            expect("log.row-shape", sorted(rows[0]) == sorted(
                ["n", "ts", "input_sha256", "input_len", "mode", "status", "bytes_written", "aborted", "case"]
            ), str(sorted(rows[0])))
            expect("log.row-count", len(rows) == 7, f"got {len(rows)}")
            log_text = open(calls_file, encoding="utf-8").read()
            expect("log.no-input-text", head_text not in log_text and "narration" not in log_text)
            expect("log.hash", rows[0]["input_sha256"] == hashlib.sha256(head_text.encode("utf-8")).hexdigest())
            expect("log.len", rows[0]["input_len"] == len(head_text))
            expect("log.ok-bytes", rows[0]["bytes_written"] == sizes[SPEECH_FIXTURE], str(rows[0]))
            expect("log.modes", [r["mode"] for r in rows[:5]] == ["ok", "sine", "e500", "abort", "empty"],
                   str([r["mode"] for r in rows]))
            expect("log.statuses", [r["status"] for r in rows[:5]] == [200, 200, 500, 200, 200],
                   str([r["status"] for r in rows]))
            abort_row = [r for r in rows if r["mode"] == "abort"][0]
            expect("log.abort-flag", abort_row["aborted"] is True and abort_row["bytes_written"] == ABORT_BYTES,
                   str(abort_row))
            expect("log.abort-not-flag-elsewhere",
                   all(r["aborted"] is False for r in rows if r["mode"] != "abort"))
            expect("log.case-recorded", rows[0]["case"] == case_a, rows[0]["case"])
            expect("log.n-sequence", [r["n"] for r in rows[:7]] == [1, 2, 3, 4, 5, 1, 1],
                   str([r["n"] for r in rows]))

            # (k) auth denials are still recorded (mode "denied"), tiny server only
            rows_b = [json.loads(line) for line in open(calls_b, encoding="utf-8")]
            expect("log.denied-rows", [r["mode"] for r in rows_b[:2]] == ["denied", "denied"],
                   str([r["mode"] for r in rows_b]))
            expect("log.denied-status", all(r["status"] == 401 for r in rows_b[:2]))
            expect("log.denied-then-served", rows_b[-1]["mode"] == "slow" and rows_b[-1]["n"] == 1,
                   str(rows_b[-1]))

            # (l) QA-0541-11: malformed / negative Content-Length -> 400 + logged
            def raw_post(port, cl):
                import socket
                sock = socket.create_connection((HOST, port), timeout=10)
                try:
                    sock.sendall((
                        f"POST {SPEECH_PATH} HTTP/1.1\r\nHost: {HOST}:{port}\r\n"
                        f"Content-Type: application/json\r\nContent-Length: {cl}\r\n"
                        f"Connection: close\r\n\r\n"
                    ).encode("ascii"))
                    chunks = []
                    while True:
                        part = sock.recv(65536)
                        if not part:
                            break
                        chunks.append(part)
                    return b"".join(chunks)
                finally:
                    sock.close()

            bad_rows_before = sum(
                1 for line in open(calls_file, encoding="utf-8")
                if '"bad_content_length"' in line
            )
            for label, cl in (("alpha", "abc"), ("negative", "-5")):
                resp = raw_post(port_a, cl)
                expect(f"badcl.{label}-400",
                       resp.split(b"\r\n", 1)[0].startswith(b"HTTP/") and b" 400 " in resp.split(b"\r\n", 1)[0],
                       resp.split(b"\r\n", 1)[0][:80].decode("ascii", "replace"))
                expect(f"badcl.{label}-code", b"invalid_content_length" in resp)
            bad_rows = [json.loads(line) for line in open(calls_file, encoding="utf-8")
                        if '"bad_content_length"' in line]
            expect("badcl.logged", len(bad_rows) - bad_rows_before == 2
                   and all(r["status"] == 400 for r in bad_rows), str(len(bad_rows)))
        finally:
            server_a.shutdown()
            server_a.server_close()
            server_b.shutdown()
            server_b.server_close()

    return 0 if not failures else 1


def main():
    p = argparse.ArgumentParser(description="Scripted fake OpenAI-compatible speech API (05.4.1-T1).")
    p.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"bind port (default {DEFAULT_PORT})")
    p.add_argument("--script", default="", help="JSON script file, re-read on every request")
    p.add_argument("--calls-file", default="tts-stub-calls.jsonl",
                   help="JSONL of call rows (input hash + length only)")
    p.add_argument("--fixtures", default="",
                   help=f"fixture directory (default {default_fixtures_dir()})")
    p.add_argument("--require-key", action="store_true",
                   help="reject requests whose Authorization is not 'Bearer <key>'")
    p.add_argument("--key", default="", help="expected key (default $NTS_TTS_STUB_KEY, else nts-stub-key)")
    p.add_argument("--slow-delay", type=float, default=DEFAULT_SLOW_DELAY,
                   help=f"seconds before the `slow` mode writes its first byte (default {DEFAULT_SLOW_DELAY:g})")
    p.add_argument("--self-test", action="store_true", help="run the in-process mode/auth checks")
    args = p.parse_args()

    fixtures_dir = os.path.abspath(os.path.expanduser(args.fixtures)) if args.fixtures else default_fixtures_dir()

    if args.self_test:
        sys.exit(self_test(fixtures_dir))
    if not (0 <= args.port <= 65535):
        eprint(f"tts_stub.py: invalid --port {args.port}")
        sys.exit(2)
    if args.slow_delay < 0:
        eprint(f"tts_stub.py: invalid --slow-delay {args.slow_delay}")
        sys.exit(2)
    if args.script and not os.path.isfile(args.script):
        eprint(f"tts_stub.py: --script not found: {args.script}")
        sys.exit(2)
    missing = missing_fixtures(fixtures_dir)
    if missing:
        print_fixture_commands(missing, fixtures_dir)
        sys.exit(2)
    key = args.key or os.environ.get("NTS_TTS_STUB_KEY", "") or ("nts-stub-key" if args.require_key else "")
    require_key = args.require_key or bool(key)
    calls_file = args.calls_file
    if calls_file and not os.path.isabs(calls_file):
        calls_file = os.path.abspath(calls_file)
    try:
        sys.exit(serve(args.port, args.script, calls_file, fixtures_dir, require_key, key, args.slow_delay))
    except OSError as exc:
        eprint(f"tts_stub.py: cannot bind {HOST}:{args.port}: {exc}")
        sys.exit(2)


if __name__ == "__main__":
    main()
