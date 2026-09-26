#!/usr/bin/env python3
"""image_stub.py — scripted fake OpenAI Images API for the 03.4.1-T1 MPT image spike.

Serves only `POST /v1/images/generations` (plus `GET /img/<n>.png`, the download
target of the `url` outcome) on 127.0.0.1 so MPT can be run against a fake image
provider without any real OpenAI call (03 README D14). Every other path is 404.

Run:  uv run --no-project --python 3.12 trial/tools/image_stub.py [flags]
Exit codes (01 D9): 0 ok · 1 self-test failed · 2 usage/environment error.

Scripting (`--script FILE`, re-read on every request so a running stub can be
re-scripted between spike cases):

    {
      "case": "P2",                                  # optional; a change resets the call counter
      "default": "ok",                               # outcome when nothing else matches
      "by_call": {"3": "moderated"},                 # outcome for call number 3
      "by_prompt_contains": {"__BLOCK__": "rate"},   # outcome when the prompt contains the text
      "slow_seconds": 320                            # duration of the `slow` outcome
    }

Outcomes: `ok` · `moderated` (400 moderation_blocked) · `rate` (429
rate_limit_exceeded) · `quota` (429 insufficient_quota) · `e500` (500) · `slow`
(sleeps, then answers like `ok`) · `garbage` (200 whose b64 payload is not an
image) · `url` (200 with `data[0].url` pointing at this stub).

Every generation call appends one row to `--calls-log` with the prompt **hash and
length only** — never the prompt text (03 README D12):

    {"n", "ts", "prompt_sha256", "prompt_len", "size", "has_auth_header", "outcome", "case"}

The generated PNG gets a distinct solid colour per call number plus that number
drawn as 5x7 blocks (no font file), so scene order in a rendered video can be read
back from sampled frames.
"""

import argparse
import base64
import hashlib
import json
import os
import re
import struct
import sys
import threading
import time
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
DEFAULT_PORT = 18003
# Cap on a request body (SEC-NTS-M0-10): the stub reads Content-Length bytes into
# memory, and 1 MiB is far above any fixture prompt (the spike sends ~330 chars).
MAX_BODY = 1 << 20
GENERATIONS_PATH = "/v1/images/generations"
IMG_PATH_RE = re.compile(r"^/img/(\d+)\.png$")
DEFAULT_SIZE = (1536, 1024)
MAX_SIZE = 2048

# One solid colour per call number, so a sampled frame identifies the scene.
SLOT_COLOURS = [
    (232, 64, 64),    # 1 red
    (64, 200, 96),    # 2 green
    (72, 96, 240),    # 3 blue
    (240, 200, 40),   # 4 yellow
    (200, 72, 200),   # 5 magenta
    (40, 200, 200),   # 6 cyan
    (240, 132, 24),   # 7 orange
    (128, 72, 240),   # 8 purple
]

# 5x7 digit bitmaps: index by int(digit), rows top to bottom.
DIGITS = {
    0: ("01110", "10001", "10011", "10101", "11001", "10001", "01110"),
    1: ("00100", "01100", "00100", "00100", "00100", "00100", "01110"),
    2: ("01110", "10001", "00001", "00010", "00100", "01000", "11111"),
    3: ("11111", "00010", "00100", "00010", "00001", "10001", "01110"),
    4: ("00010", "00110", "01010", "10010", "11111", "00010", "00010"),
    5: ("11111", "10000", "11110", "00001", "00001", "10001", "01110"),
    6: ("00110", "01000", "10000", "11110", "10001", "10001", "01110"),
    7: ("11111", "00001", "00010", "00100", "01000", "01000", "01000"),
    8: ("01110", "10001", "10001", "01110", "10001", "10001", "01110"),
    9: ("01110", "10001", "10001", "01111", "00001", "00010", "01100"),
}

USAGE = {
    "input_tokens": 90,
    "input_tokens_details": {"text_tokens": 90, "image_tokens": 0},
    "output_tokens": 1000,
    "total_tokens": 1090,
}

MODERATED_BODY = {
    "error": {
        "code": "moderation_blocked",
        "message": "Your request was rejected by the safety system.",
        "type": "image_generation_user_error",
    }
}
RATE_BODY = {
    "error": {
        "code": "rate_limit_exceeded",
        "message": "Rate limit reached for images per min.",
        "type": "rate_limit_error",
    }
}
QUOTA_BODY = {
    "error": {
        "code": "insufficient_quota",
        "message": "You exceeded your current quota, please check your plan and billing details.",
        "type": "insufficient_quota",
    }
}
E500_BODY = {
    "error": {
        "code": "server_error",
        "message": "The server had an error while processing your request.",
        "type": "server_error",
    }
}

OUTCOMES = ("ok", "moderated", "rate", "quota", "e500", "slow", "garbage", "url")


def eprint(*a):
    print(*a, file=sys.stderr)


def png_bytes(width, height, slot):
    """A solid-colour PNG of `width`x`height` with `slot` drawn as 5x7 blocks."""
    bg = SLOT_COLOURS[(slot - 1) % len(SLOT_COLOURS)]
    plate = (250, 250, 250)
    ink = (16, 16, 16)
    digits = [int(c) for c in str(slot)]
    scale = max(4, min(width, height) // 90)
    dw, dh = 5 * scale, 7 * scale
    gap = scale
    block_w = len(digits) * dw + (len(digits) - 1) * gap
    x0 = (width - block_w) // 2
    y0 = (height - dh) // 2
    pad = scale * 2

    plate_x0 = max(0, x0 - pad)
    plate_y0 = max(0, y0 - pad)
    plate_x1 = min(width, x0 + block_w + pad)
    plate_y1 = min(height, y0 + dh + pad)

    row_bg = bytes(bg)
    row_plate = bytes(plate)
    rows = []
    for y in range(height):
        in_plate = plate_y0 <= y < plate_y1
        row = bytearray(row_bg * width)
        if in_plate:
            row[plate_x0 * 3 : plate_x1 * 3] = row_plate * (plate_x1 - plate_x0)
            row[plate_x0 * 3 : plate_x0 * 3 + 3] = bytes(ink)
            row[(plate_x1 - 1) * 3 : plate_x1 * 3] = bytes(ink)
        rows.append(bytes(row))
    # plate top/bottom edges
    for y in (plate_y0, plate_y1 - 1):
        if 0 <= y < height:
            row = bytearray(rows[y])
            row[plate_x0 * 3 : plate_x1 * 3] = bytes(ink) * (plate_x1 - plate_x0)
            rows[y] = bytes(row)

    for i, digit in enumerate(digits):
        dx = x0 + i * (dw + gap)
        for ry, bits in enumerate(DIGITS[digit]):
            for rx, bit in enumerate(bits):
                if bit != "1":
                    continue
                for py in range(y0 + ry * scale, y0 + (ry + 1) * scale):
                    if not (0 <= py < height):
                        continue
                    row = bytearray(rows[py])
                    start = (dx + rx * scale) * 3
                    row[start : start + 3 * scale] = bytes(ink) * scale
                    rows[py] = bytes(row)

    raw = b"".join(b"\x00" + row for row in rows)

    def chunk(tag, data):
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(raw, 6))
        + chunk(b"IEND", b"")
    )


def parse_size(size_value):
    text = str(size_value or "").strip().lower()
    m = re.fullmatch(r"(\d{2,4})x(\d{2,4})", text)
    if not m:
        return DEFAULT_SIZE
    w, h = int(m.group(1)), int(m.group(2))
    if not (16 <= w <= MAX_SIZE and 16 <= h <= MAX_SIZE):
        return DEFAULT_SIZE
    return w, h


class State:
    """Call counter + script, shared by the handler threads."""

    def __init__(self, script_path, calls_log):
        self.script_path = script_path
        self.calls_log = calls_log
        self.lock = threading.Lock()
        self.n = 0
        self.case = ""
        self.png_cache = {}
        self.downloads = 0

    def load_script(self):
        if not self.script_path:
            return {"default": "ok"}
        try:
            with open(self.script_path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
        except (OSError, ValueError) as exc:
            eprint(f"image_stub: script unreadable ({type(exc).__name__}): {exc}")
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

    def image(self, width, height, slot):
        key = (width, height, slot)
        with self.lock:
            blob = self.png_cache.get(key)
        if blob is None:
            blob = png_bytes(width, height, slot)
            with self.lock:
                self.png_cache[key] = blob
        return blob

    def record(self, row):
        if not self.calls_log:
            return
        with self.lock:
            with open(self.calls_log, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(row, sort_keys=True) + "\n")
                fh.flush()


def pick_outcome(script, call_n, prompt):
    contains = script.get("by_prompt_contains")
    if isinstance(contains, dict):
        for needle, outcome in contains.items():
            if needle and needle in prompt:
                return str(outcome)
    by_call = script.get("by_call")
    if isinstance(by_call, dict):
        outcome = by_call.get(str(call_n))
        if outcome is None:
            outcome = by_call.get(call_n)
        if outcome is not None:
            return str(outcome)
    return str(script.get("default") or "ok")


class Handler(BaseHTTPRequestHandler):
    server_version = "nts-image-stub/1"
    sys_version = ""
    protocol_version = "HTTP/1.0"

    # Every request line on stdout, so the spike runner can slice raw evidence.
    def log_message(self, fmt, *args):
        print(f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} {self.address_string()} {fmt % args}", flush=True)

    def _send(self, status, body, content_type="application/json"):
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
        except (BrokenPipeError, ConnectionResetError):
            # The client (MPT) gave up first — normal for the `slow` outcome.
            print(
                f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
                f"client gone before the response was written (status {status})",
                flush=True,
            )

    def do_GET(self):  # noqa: N802 (BaseHTTPRequestHandler API)
        m = IMG_PATH_RE.match(self.path)
        if not m:
            self._send(404, {"error": {"code": "route_not_found", "message": "not found"}})
            return
        slot = int(m.group(1))
        blob = self.server.state.image(*DEFAULT_SIZE, slot)
        with self.server.state.lock:
            self.server.state.downloads += 1
        print(
            f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
            f"imagedownload slot={slot} bytes={len(blob)}",
            flush=True,
        )
        self._send(200, blob, content_type="image/png")

    def do_POST(self):  # noqa: N802 (BaseHTTPRequestHandler API)
        if self.path != GENERATIONS_PATH:
            self._send(404, {"error": {"code": "route_not_found", "message": "not found"}})
            return
        length = int(self.headers.get("Content-Length") or 0)
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
        prompt = body.get("prompt")
        prompt = prompt if isinstance(prompt, str) else ""
        size = str(body.get("size") or "")
        width, height = parse_size(size)
        has_auth = bool(self.headers.get("Authorization"))

        state = self.server.state
        script = state.load_script()
        call_n = state.next_call(script)
        outcome = pick_outcome(script, call_n, prompt)
        if outcome not in OUTCOMES:
            eprint(f"image_stub: unknown outcome {outcome!r} for call {call_n}; using ok")
            outcome = "ok"

        state.record(
            {
                "n": call_n,
                "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "prompt_sha256": hashlib.sha256(prompt.encode("utf-8")).hexdigest(),
                "prompt_len": len(prompt),
                "size": f"{width}x{height}",
                "has_auth_header": has_auth,
                "outcome": outcome,
                "case": state.case,
            }
        )
        print(
            f"{time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())} "
            f"call n={call_n} outcome={outcome} prompt_len={len(prompt)} "
            f"size={width}x{height} auth={int(has_auth)} case={state.case}",
            flush=True,
        )

        if outcome == "slow":
            sleep_s = script.get("slow_seconds", 320)
            try:
                sleep_s = float(sleep_s)
            except (TypeError, ValueError):
                sleep_s = 320.0
            time.sleep(max(0.0, sleep_s))
            outcome = "ok"

        if outcome == "moderated":
            self._send(400, MODERATED_BODY)
            return
        if outcome == "rate":
            self._send(429, RATE_BODY)
            return
        if outcome == "quota":
            self._send(429, QUOTA_BODY)
            return
        if outcome == "e500":
            self._send(500, E500_BODY)
            return

        created = int(time.time())
        size_label = f"{width}x{height}"
        if outcome == "url":
            self._send(
                200,
                {
                    "created": created,
                    "quality": "medium",
                    "size": size_label,
                    "data": [{"url": f"http://{HOST}:{self.server.server_port}/img/{call_n}.png"}],
                    "usage": USAGE,
                },
            )
            return
        if outcome == "garbage":
            self._send(
                200,
                {
                    "created": created,
                    "quality": "medium",
                    "size": size_label,
                    "data": [
                        {
                            "b64_json": base64.b64encode(
                                b"<html><body>upstream error page, not an image</body></html>"
                            ).decode("ascii")
                        }
                    ],
                    "usage": USAGE,
                },
            )
            return
        blob = state.image(width, height, call_n)
        self._send(
            200,
            {
                "created": created,
                "quality": "medium",
                "size": size_label,
                "data": [{"b64_json": base64.b64encode(blob).decode("ascii")}],
                "usage": USAGE,
            },
        )


class StubHTTPServer(ThreadingHTTPServer):
    """ThreadingHTTPServer with a large accept backlog.

    socketserver's default `request_queue_size` is 5 and `listen()` runs inside
    `TCPServer.__init__`, so it can only be raised on a subclass. A driver that
    opens many connections at once (P10: a burst of short prompts) otherwise gets
    ECONNRESET on the excess and silently loses evidence rows — measured with 120
    simultaneous POSTs: 28 accepted at the default vs 120/120 at 512 (03.4.1-T1).
    """

    daemon_threads = True
    request_queue_size = 512


def make_server(port, state):
    server = StubHTTPServer((HOST, port), Handler)
    server.state = state
    return server


def serve(port, script_path, calls_log):
    state = State(script_path, calls_log)
    server = make_server(port, state)
    eprint(f"image_stub: listening on http://{HOST}:{server.server_port}{GENERATIONS_PATH}")
    if script_path:
        eprint(f"image_stub: script {script_path}")
    if calls_log:
        eprint(f"image_stub: calls log {calls_log}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


def _decode_png_dims(blob):
    """Return (width, height) when `blob` is a valid PNG, else None."""
    if not blob.startswith(b"\x89PNG\r\n\x1a\n"):
        return None
    off = 8
    dims = None
    idat = b""
    while off + 8 <= len(blob):
        length = struct.unpack(">I", blob[off : off + 4])[0]
        tag = blob[off + 4 : off + 8]
        data = blob[off + 8 : off + 8 + length]
        crc = struct.unpack(">I", blob[off + 8 + length : off + 12 + length])[0]
        if crc != (zlib.crc32(tag + data) & 0xFFFFFFFF):
            return None
        if tag == b"IHDR":
            w, h, depth, colour, _, _, _ = struct.unpack(">IIBBBBB", data)
            if depth != 8 or colour != 2:
                return None
            dims = (w, h)
        elif tag == b"IDAT":
            idat += data
        elif tag == b"IEND":
            break
        off += 12 + length
    if dims is None or not idat:
        return None
    try:
        raw = zlib.decompress(idat)
    except zlib.error:
        return None
    w, h = dims
    if len(raw) != h * (1 + w * 3):
        return None
    return dims


def self_test():
    import http.client
    import tempfile

    failures = []

    def expect(name, cond, detail=""):
        if cond:
            print(f"PASS selftest:{name}")
        else:
            print(f"FAIL selftest:{name}{': ' + detail if detail else ''}")
            failures.append(name)

    with tempfile.TemporaryDirectory() as td:
        script_path = os.path.join(td, "script.json")
        calls_log = os.path.join(td, "calls.jsonl")
        state = State(script_path, calls_log)
        server = make_server(0, state)
        port = server.server_port
        t = threading.Thread(target=server.serve_forever, daemon=True)
        t.start()
        try:
            def post(prompt, size="1536x1024", auth=False):
                conn = http.client.HTTPConnection(HOST, port, timeout=30)
                headers = {"Content-Type": "application/json"}
                if auth:
                    headers["Authorization"] = "Bearer dummy"
                conn.request(
                    "POST",
                    GENERATIONS_PATH,
                    body=json.dumps({"model": "gpt-image-2", "prompt": prompt, "n": 1, "size": size}),
                    headers=headers,
                )
                resp = conn.getresponse()
                payload = resp.read()
                conn.close()
                try:
                    parsed = json.loads(payload.decode("utf-8"))
                except ValueError:
                    parsed = None
                return resp.status, parsed, payload

            def write_script(obj):
                with open(script_path, "w", encoding="utf-8") as fh:
                    json.dump(obj, fh)

            # (a) ok -> 200, valid 1536x1024 PNG in b64_json, exact usage/quality
            write_script({"case": "T1", "default": "ok"})
            status, body, _ = post("prompt one")
            expect("ok.status", status == 200, f"got {status}")
            expect("ok.usage", body.get("usage") == USAGE, json.dumps(body.get("usage")))
            expect("ok.quality", body.get("quality") == "medium")
            expect("ok.size", body.get("size") == "1536x1024")
            blob = base64.b64decode(body["data"][0]["b64_json"])
            expect("ok.png-dims", _decode_png_dims(blob) == (1536, 1024), str(_decode_png_dims(blob)))
            expect("ok.png-zlib", _decode_png_dims(blob) is not None)

            # (b) portrait size is honoured
            status, body, _ = post("prompt two", size="1024x1536")
            blob = base64.b64decode(body["data"][0]["b64_json"])
            expect("size.portrait", _decode_png_dims(blob) == (1024, 1536))

            # (c) moderated -> 400 with the documented code
            write_script({"case": "T2", "default": "moderated"})
            status, body, _ = post("prompt three")
            expect("moderated.status", status == 400, f"got {status}")
            expect("moderated.code", body["error"]["code"] == "moderation_blocked")
            expect("moderated.type", body["error"]["type"] == "image_generation_user_error")

            # (d) rate / quota -> 429 with distinct codes
            write_script({"case": "T3", "default": "rate"})
            status, body, _ = post("prompt four")
            expect("rate.status", status == 429, f"got {status}")
            expect("rate.code", body["error"]["code"] == "rate_limit_exceeded")
            write_script({"case": "T4", "default": "quota"})
            status, body, _ = post("prompt five")
            expect("quota.status", status == 429, f"got {status}")
            expect("quota.code", body["error"]["code"] == "insufficient_quota")

            # (e) e500 -> 500
            write_script({"case": "T5", "default": "e500"})
            status, body, _ = post("prompt six")
            expect("e500.status", status == 500, f"got {status}")

            # (f) garbage -> 200 whose payload is not an image
            write_script({"case": "T6", "default": "garbage"})
            status, body, _ = post("prompt seven")
            expect("garbage.status", status == 200, f"got {status}")
            expect(
                "garbage.not-image",
                _decode_png_dims(base64.b64decode(body["data"][0]["b64_json"])) is None,
            )

            # (g) url -> the stub serves the download target as a PNG
            write_script({"case": "T7", "default": "url"})
            status, body, _ = post("prompt eight")
            url = body["data"][0]["url"]
            expect("url.points-at-stub", url.startswith(f"http://{HOST}:{port}/img/"), url)
            path = url.split(f"http://{HOST}:{port}", 1)[1]
            conn = http.client.HTTPConnection(HOST, port, timeout=30)
            conn.request("GET", path)
            resp = conn.getresponse()
            img = resp.read()
            conn.close()
            expect("url.download-200", resp.status == 200, f"got {resp.status}")
            expect("url.download-png", _decode_png_dims(img) == (1536, 1024))
            expect("url.download-counted", state.downloads == 1, f"got {state.downloads}")

            # (h) by_call + slow_seconds + case reset
            write_script({"case": "T8", "by_call": {"2": "moderated"}, "default": "ok"})
            expect("by_call.1", post("prompt nine")[0] == 200)
            expect("by_call.2", post("prompt ten")[0] == 400)
            write_script({"case": "T9", "default": "slow", "slow_seconds": 0.05})
            t0 = time.time()
            status, _, _ = post("prompt eleven")
            expect("slow.delays", status == 200 and time.time() - t0 >= 0.05)
            expect("by_call.counter-reset-on-case", state.n == 1, f"got {state.n}")

            # (i) by_prompt_contains wins over default
            write_script({"case": "T10", "default": "ok", "by_prompt_contains": {"__BLOCK__": "moderated"}})
            expect("contains.hit", post("x __BLOCK__ y")[0] == 400)
            expect("contains.miss", post("plain prompt")[0] == 200)

            # (j) unknown path -> 404
            conn = http.client.HTTPConnection(HOST, port, timeout=10)
            conn.request("GET", "/nope")
            resp = conn.getresponse()
            resp.read()
            conn.close()
            expect("other-path.404", resp.status == 404, f"got {resp.status}")

            # (k) unknown outcome falls back to ok; auth flag is recorded
            write_script({"case": "T11", "default": "not-an-outcome"})
            expect("unknown-outcome.ok", post("prompt twelve", auth=True)[0] == 200)
            rows = [json.loads(line) for line in open(calls_log, encoding="utf-8")]
            expect("log.row-shape", sorted(rows[-1]) == sorted(
                ["n", "ts", "prompt_sha256", "prompt_len", "size", "has_auth_header", "outcome", "case"]
            ), str(sorted(rows[-1])))
            expect("log.has-auth-header", rows[-1]["has_auth_header"] is True)
            expect("log.no-prompt-text", "prompt twelve" not in open(calls_log, encoding="utf-8").read())
            expect(
                "log.prompt-sha",
                rows[-1]["prompt_sha256"] == hashlib.sha256(b"prompt twelve").hexdigest(),
            )
            expect("log.prompt-len", rows[-1]["prompt_len"] == len("prompt twelve"))
            expect("log.outcome-recorded", rows[-1]["outcome"] == "ok")
            expect("log.case-updated", rows[-1]["case"] == "T11")
            expect(
                "log.case-reset-column",
                any(r["case"] == "T1" and r["n"] == 1 for r in rows),
                "first call of a case must be n=1",
            )
            expect(
                "log.no-auth-by-default",
                any(r["has_auth_header"] is False for r in rows),
            )

            # (l) parse_size guards
            expect("size_parse.bad", parse_size("nonsense") == DEFAULT_SIZE)
            expect("size_parse.oversize", parse_size("9000x9000") == DEFAULT_SIZE)
        finally:
            server.shutdown()
            server.server_close()

    return 0 if not failures else 1


def main():
    p = argparse.ArgumentParser(description="Scripted fake OpenAI Images API (03.4.1-T1).")
    p.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"bind port (default {DEFAULT_PORT})")
    p.add_argument("--script", default="", help="JSON script file, re-read on every request")
    p.add_argument("--calls-log", default="", help="JSONL of call rows (prompt hash + length only)")
    p.add_argument("--self-test", action="store_true", help="run the in-process outcome checks")
    args = p.parse_args()

    if args.self_test:
        sys.exit(self_test())
    if not (0 <= args.port <= 65535):
        eprint(f"image_stub.py: invalid --port {args.port}")
        sys.exit(2)
    if args.script and not os.path.isfile(args.script):
        eprint(f"image_stub.py: --script not found: {args.script}")
        sys.exit(2)
    try:
        sys.exit(serve(args.port, args.script, args.calls_log))
    except OSError as exc:
        eprint(f"image_stub.py: cannot bind {HOST}:{args.port}: {exc}")
        sys.exit(2)


if __name__ == "__main__":
    main()
