#!/usr/bin/env python3
"""usage_tap.py — 01.3.1-T2: stdlib-only OpenAI usage tap on 127.0.0.1:8002.

Reverse-proxies MPT's OpenAI calls to the pinned upstream (origin of
OPENAI_BASE_URL in worker-node/pins.env; 01 CR-15 / DEC-9) unchanged and
appends exactly one D6 JSON line per call to
TAP_RUNS_DIR/<run_id>/openai_usage.jsonl. It never stores or logs a header,
key, prompt, message content or image bytes (F02, D6).

Config comes from the environment once at startup; anything invalid exits 2:

  TAP_LISTEN_HOST        127.0.0.1 only (D4)
  TAP_PORT               8002
  TAP_UPSTREAM           default = origin of pinned OPENAI_BASE_URL
                         (https://api.aixingialaire.shop today). The only other
                         accepted value is http://127.0.0.1:<port> (stub tests),
                         and then TAP_RUNS_DIR MUST resolve outside
                         <repo>/trial/runs (NTS01-SEC-04).
  TAP_UPSTREAM_TIMEOUT_S 610 (>= 300; M4 read timeout 300 s). < 300 allowed only
                         against the 127.0.0.1 stub (timeout test).
  TAP_CONNECT_TIMEOUT_S  30
  TAP_MAX_BODY_BYTES     1048576   (request cap)
  TAP_MAX_RESPONSE_BYTES 67108864  (response cap)
  TAP_IMAGE_QUALITY      unset | low|medium|high|auto (O6) — injected into
                         /images/generations bodies that lack `quality`.
  NTS_HOME               ${NTS_HOME:-<repo>/.nts} (tap.json, current_run)
  TAP_RUNS_DIR           <repo>/trial/runs
  TAP_MAX_CONCURRENCY    4 (NTS01-SEC-10)
"""

import argparse
import http.client
import http.server
import json
import os
import re
import socket
import ssl
import sys
import threading
import time
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
REAL_RUNS_DIR = os.path.realpath(str(REPO / "trial" / "runs"))
D10_RUN_ID = re.compile(r"^\d{8}-\d{4}-(vi|en)-(landscape|portrait)-\d{2}$")
STUB_UPSTREAM = re.compile(r"^http://127\.0\.0\.1:\d+/?$")
QUALITIES = ("low", "medium", "high", "auto")
HOP_HEADERS = frozenset(
    (
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
    )
)
# Incoming names we always replace or drop (besides hop-by-hop + Connection tokens).
SKIP_REQUEST = HOP_HEADERS | frozenset(("host", "content-length", "accept-encoding"))


def _under_dir(path, root):
    """True if path (or its nearest existing ancestor chain) is root or lies inside it."""
    try:
        r = os.stat(root)
    except OSError:
        return False
    cur = os.path.realpath(path)
    while True:
        try:
            st = os.stat(cur)
            if (st.st_dev, st.st_ino) == (r.st_dev, r.st_ino):
                return True
        except OSError:
            pass
        parent = os.path.dirname(cur)
        if parent == cur:
            return False
        cur = parent


def die_config(msg):
    """Print a config error (variable names only, never values) and exit 2."""
    print(f"usage_tap.py: {msg}", file=sys.stderr)
    sys.exit(2)


def pinned_upstream():
    """Origin of OPENAI_BASE_URL from worker-node/pins.env (01 CR-15, DEC-9)."""
    pins = REPO / "worker-node" / "pins.env"
    try:
        for raw in pins.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if line.startswith("OPENAI_BASE_URL="):
                url = line.split("=", 1)[1].strip()
                if not (url.startswith("https://") and url.endswith("/v1")):
                    die_config(
                        "pins.env OPENAI_BASE_URL is not of the form https://host/v1"
                    )
                return url[: -len("/v1")]
    except OSError as e:
        die_config(f"cannot read {pins}: {type(e).__name__}")
    die_config("pins.env has no OPENAI_BASE_URL")


def env_int(name, default, lo, hi):
    raw = os.environ.get(name)
    if raw is None:
        return default
    try:
        val = int(raw, 10)
    except ValueError:
        die_config(f"{name} must be an integer")
    if not (lo <= val <= hi):
        die_config(f"{name} must be between {lo} and {hi}")
    return val


def load_config():
    pinned = pinned_upstream()
    upstream = os.environ.get("TAP_UPSTREAM") or pinned
    is_stub = bool(STUB_UPSTREAM.match(upstream))
    if upstream != pinned and not is_stub:
        die_config(
            "TAP_UPSTREAM must be the pinned upstream or "
            "http://127.0.0.1:<port> (stub tests only)"
        )
    runs_dir = os.environ.get("TAP_RUNS_DIR") or str(REAL_RUNS_DIR)
    if upstream != pinned:
        # NTS01-SEC-04: a tap started in stub mode forwards the real
        # Authorization header in cleartext to an unverified local port; it
        # must never write into a real run's directory.
        # Compare by (dev, inode), not by string: APFS is case-insensitive, so
        # <repo>/TRIAL/RUNS is the same directory as <repo>/trial/runs.
        if _under_dir(runs_dir, REAL_RUNS_DIR):
            die_config("tap_stub_upstream_forbidden")
    timeout = env_int("TAP_UPSTREAM_TIMEOUT_S", 610, 1, 86400)
    if timeout < 300 and not is_stub:
        die_config(
            "TAP_UPSTREAM_TIMEOUT_S must be >= 300 unless TAP_UPSTREAM is the "
            "127.0.0.1 stub (M4 read timeout is 300 s)"
        )
    listen_host = os.environ.get("TAP_LISTEN_HOST", "127.0.0.1")
    if listen_host != "127.0.0.1":
        die_config("TAP_LISTEN_HOST must be 127.0.0.1 (D4)")
    quality = os.environ.get("TAP_IMAGE_QUALITY")
    if quality is not None and quality not in QUALITIES:
        die_config("TAP_IMAGE_QUALITY must be one of low|medium|high|auto")
    # Default = <repo>/.nts like lib.sh nts_home (CR-11/DEC-5); start-tap.sh exports the resolved value.
    nts_home = Path(os.path.expanduser(os.environ.get("NTS_HOME") or str(Path(__file__).resolve().parents[2] / ".nts")))
    parts = urllib.parse.urlsplit(upstream)
    if not parts.hostname or parts.scheme not in ("http", "https"):
        die_config("TAP_UPSTREAM must be an http(s) origin")
    try:
        up_port = parts.port
    except ValueError:
        die_config("TAP_UPSTREAM has an invalid port")
    return {
        "listen_host": listen_host,
        "port": env_int("TAP_PORT", 8002, 1, 65535),
        "upstream": upstream.rstrip("/"),
        "upstream_host": parts.hostname,
        "upstream_port": up_port or (443 if parts.scheme == "https" else 80),
        "upstream_tls": parts.scheme == "https",
        "timeout": timeout,
        "connect_timeout": env_int("TAP_CONNECT_TIMEOUT_S", 30, 1, 600),
        "max_body": env_int("TAP_MAX_BODY_BYTES", 1048576, 1, 1 << 40),
        "max_response": env_int("TAP_MAX_RESPONSE_BYTES", 67108864, 1, 1 << 40),
        "quality": quality,
        "nts_home": nts_home,
        "runs_dir": Path(runs_dir),
        "concurrency": env_int("TAP_MAX_CONCURRENCY", 4, 1, 1024),
    }


CFG = {}  # filled in main()
BUSY = None  # threading.Semaphore
JSONL_LOCK = threading.Lock()


def now_iso_ms():
    return (
        datetime.now(timezone.utc)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z")
    )


def numeric_leaves(value):
    """Recursive dict of int/float leaves only; strings/lists/bools dropped."""
    if not isinstance(value, dict):
        return None
    out = {}
    for key, val in value.items():
        if isinstance(val, bool):
            continue
        if isinstance(val, (int, float)):
            out[key] = val
        elif isinstance(val, dict):
            sub = numeric_leaves(val)
            if sub:
                out[key] = sub
    return out or None


def current_run_id():
    try:
        rid = (CFG["nts_home"] / "current_run").read_text(encoding="utf-8").strip()
    except OSError:
        return "no-run"
    return rid if D10_RUN_ID.match(rid) else "no-run"


def write_jsonl(rec):
    """One D6 line, append+fsync; failures go to stderr, never raise."""
    try:
        run_dir = CFG["runs_dir"] / rec["run_id"]
        os.makedirs(run_dir, mode=0o755, exist_ok=True)
        path = run_dir / "openai_usage.jsonl"
        line = json.dumps(rec, ensure_ascii=False, separators=(",", ":")) + "\n"
        with JSONL_LOCK:
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o644)
            try:
                os.write(fd, line.encode("utf-8"))
                os.fsync(fd)
            finally:
                os.close(fd)
    except Exception as e:
        print(f"jsonl_write_failed {type(e).__name__}", file=sys.stderr)


class UpstreamError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def _empty_req_params():
    return {"size": None, "quality": None, "moderation": None, "n": None}


def _empty_resp_params():
    return {"quality": None, "size": None}


def _upstream_error_code(parsed):
    if isinstance(parsed, dict):
        err = parsed.get("error")
        if isinstance(err, dict):
            code = err.get("code")
            if isinstance(code, str):
                return code
            typ = err.get("type")
            if isinstance(typ, str):
                return typ
    return None


def _scalar(value):
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float, str)):
        return value
    return None


class TapHandler(http.server.BaseHTTPRequestHandler):
    server_version = "NTSUsageTap/1.0"
    protocol_version = "HTTP/1.1"
    timeout = 30  # bounds reads from the downstream client, not the upstream

    def setup(self):
        super().setup()
        # Belt-and-braces with the class attribute: explicit read timeout on
        # the downstream socket.
        try:
            self.request.settimeout(self.timeout)
        except OSError:
            pass

    def log_message(self, fmt, *args):
        sys.stderr.write(f"{now_iso_ms()} {fmt % args}\n")

    def log_error(self, fmt, *args):
        # Delegate to log_message; never include a traceback or exception text.
        self.log_message(fmt, *args)

    # --- method dispatch -------------------------------------------------
    def do_GET(self):
        self._handle()

    def do_POST(self):
        self._handle()

    def _reject_method(self):
        t_wall = time.time()
        t0 = time.monotonic()
        endpoint = self.path.partition("?")[0][3:] \
            if self.path.startswith("/v1/") else None
        self._tap_reply(
            405,
            "tap_method_not_allowed",
            f"method {self.command} is not forwarded",
            endpoint=endpoint,
            t0=t0,
            t_wall=t_wall,
        )

    do_PUT = _reject_method
    do_DELETE = _reject_method
    do_PATCH = _reject_method
    do_HEAD = _reject_method
    do_OPTIONS = _reject_method
    do_CONNECT = _reject_method

    # --- reply / logging helpers -----------------------------------------
    def _respond(self, status, reason, headers, body):
        self.send_response_only(status, reason)
        for name, value in headers:
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        if body:
            self.wfile.write(body)

    def _tap_reply(self, status, code, message, *, endpoint=None, t0=None,
                   t_wall=None):
        body = json.dumps(
            {"error": {"type": "tap_error", "code": code, "message": message}},
            ensure_ascii=False,
            separators=(",", ":"),
        ).encode("utf-8")
        try:
            self._respond(
                status,
                self.responses.get(status, ("",))[0],
                [("Content-Type", "application/json")],
                body,
            )
        except Exception as e:  # client already gone — still log the line
            print(f"reply_failed {type(e).__name__}", file=sys.stderr)
        if t0 is not None:
            # Standalone paths (503 busy, 405) that never enter _handle's
            # finally: log the JSONL line here.
            self._finish(endpoint, None, _empty_req_params(),
                         _empty_resp_params(), None, status, code, t0, t_wall)

    def _finish(self, endpoint, model, req_params, resp_params, usage, status,
                error_code, t0, t_wall):
        latency = int((time.monotonic() - t0) * 1000)
        rec = {
            "ts": datetime.fromtimestamp(t_wall, timezone.utc)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z"),
            "run_id": current_run_id(),
            "endpoint": endpoint,
            "model": model,
            "http_status": status,
            "error_code": error_code,
            "latency_ms": latency,
            "request": req_params,
            "response": resp_params,
            "usage": usage,
        }
        write_jsonl(rec)
        shown = endpoint if endpoint else self.path.partition("?")[0]
        sys.stderr.write(
            f"{now_iso_ms()} {self.command} {shown} {status} {latency}\n"
        )

    # --- main flow ---------------------------------------------------------
    def _handle(self):
        t_wall = time.time()
        t0 = time.monotonic()
        if not BUSY.acquire(blocking=False):
            self._tap_reply(503, "tap_busy", "tap concurrency limit reached",
                            t0=t0, t_wall=t_wall)
            return
        endpoint = None
        model = None
        req_params = _empty_req_params()
        resp_params = _empty_resp_params()
        usage = None
        status = 502
        st = {
            "endpoint": None,
            "model": None,
            "request": _empty_req_params(),
            "response": _empty_resp_params(),
            "usage": None,
            "status": 502,
            "error_code": "tap_internal",
        }
        try:
            self._forward(st)
        except Exception as e:
            # NTS01-SEC-10 catch-all: type name only, never the message or a
            # traceback (a repr could carry request material).
            print(f"tap_internal {type(e).__name__}", file=sys.stderr)
            try:
                self._respond(
                    502,
                    "Bad Gateway",
                    [("Content-Type", "application/json")],
                    json.dumps(
                        {
                            "error": {
                                "type": "tap_error",
                                "code": "tap_internal",
                                "message": "internal tap error",
                            }
                        },
                        separators=(",", ":"),
                    ).encode("utf-8"),
                )
            except Exception as e2:
                print(f"reply_failed {type(e2).__name__}", file=sys.stderr)
        finally:
            try:
                self._finish(st["endpoint"], st["model"], st["request"],
                             st["response"], st["usage"], st["status"],
                             st["error_code"], t0, t_wall)
            finally:
                BUSY.release()

    def _request_params(self, parsed):
        params = _empty_req_params()
        if isinstance(parsed, dict):
            for key in ("size", "quality", "moderation", "n"):
                val = parsed.get(key)
                if _scalar(val) is not None or val is None:
                    params[key] = val if _scalar(val) is not None else None
        return params

    def _forward(self, st):
        # Path: must start with /v1/; query forwarded unchanged, never logged.
        path, _, query = self.path.partition("?")
        if not path.startswith("/v1/"):
            self._tap_reply(404, "tap_path_not_allowed",
                            "only /v1/ paths are forwarded")
            st["status"], st["error_code"] = 404, "tap_path_not_allowed"
            return
        st["endpoint"] = path[3:]  # strip "/v1", keep leading "/"
        endpoint = st["endpoint"]

        # Request body.
        body = b""
        if self.command == "POST":
            cl = self.headers.get("Content-Length")
            if cl is None or self.headers.get("Transfer-Encoding") is not None:
                self._tap_reply(411, "tap_length_required",
                                "POST requires Content-Length",
                                endpoint=endpoint)
                st["status"], st["error_code"] = 411, "tap_length_required"
                return
            length = int(cl, 10)  # non-numeric -> catch-all 502 tap_internal
            if length < 0 or length > CFG["max_body"]:
                self._tap_reply(413, "tap_body_too_large",
                                "request body exceeds TAP_MAX_BODY_BYTES",
                                endpoint=endpoint)
                st["status"], st["error_code"] = 413, "tap_body_too_large"
                return
            body = self.rfile.read(length)

        # Parse request JSON for D6 fields (never stored or logged).
        req_ct = (self.headers.get("Content-Type") or "").lower()
        parsed = None
        if "json" in req_ct and body:
            try:
                parsed = json.loads(body)
            except Exception as e:
                print(f"request_json_parse {type(e).__name__}", file=sys.stderr)
        if isinstance(parsed, dict) and isinstance(parsed.get("model"), str):
            st["model"] = parsed["model"]
        st["request"] = self._request_params(parsed)

        # Optional quality injection (CR-3: off unless TAP_IMAGE_QUALITY set).
        if (
            CFG["quality"]
            and endpoint == "/images/generations"
            and "json" in req_ct
            and isinstance(parsed, dict)
            and "quality" not in parsed
        ):
            parsed["quality"] = CFG["quality"]
            st["request"]["quality"] = CFG["quality"]
            body = json.dumps(parsed, ensure_ascii=False,
                              separators=(",", ":")).encode("utf-8")

        upstream_path = path + (("?" + query) if query else "")
        headers = self._forward_headers(len(body))
        try:
            status, reason, rheaders, rbody = self._upstream_call(
                self.command, upstream_path, headers, body
            )
        except UpstreamError as e:
            self._tap_reply(502, e.code, "upstream call failed",
                            endpoint=endpoint)
            st["status"], st["error_code"] = 502, e.code
            return

        if len(rbody) > CFG["max_response"]:
            self._tap_reply(502, "tap_upstream_too_large",
                            "upstream response exceeds TAP_MAX_RESPONSE_BYTES",
                            endpoint=endpoint)
            st["status"], st["error_code"] = 502, "tap_upstream_too_large"
            return

        # Response headers: forward upstream headers minus hop-by-hop and
        # Content-Length (recomputed); response JSON parsed for D6 only.
        conn_tokens = set()
        for name, value in rheaders:
            if name.lower() == "connection":
                conn_tokens.update(
                    t.strip().lower() for t in value.split(",") if t.strip()
                )
        out_headers = []
        resp_ct = ""
        for name, value in rheaders:
            lname = name.lower()
            if lname in HOP_HEADERS or lname in conn_tokens:
                continue
            if lname == "content-length":
                continue
            if lname == "content-type":
                resp_ct = value.lower()
            out_headers.append((name, value))

        rparsed = None
        if "json" in resp_ct and rbody:
            try:
                rparsed = json.loads(rbody)
            except Exception as e:
                rparsed = None
                print(f"response_json_parse {type(e).__name__}",
                      file=sys.stderr)
            if isinstance(rparsed, dict):
                for key in ("quality", "size"):
                    val = _scalar(rparsed.get(key))
                    if val is not None:
                        st["response"][key] = val
                st["usage"] = numeric_leaves(rparsed.get("usage"))

        self._respond(status, reason, out_headers, rbody)
        st["status"] = status
        st["error_code"] = _upstream_error_code(rparsed)

    def _forward_headers(self, body_len):
        conn_tokens = set()
        for value in self.headers.get_all("Connection", ()):
            conn_tokens.update(
                t.strip().lower() for t in value.split(",") if t.strip()
            )
        out = []
        # One pass per case-insensitive name: get_all() already returns every spelling.
        for lname in dict.fromkeys(n.lower() for n in self.headers.keys()):
            if lname in SKIP_REQUEST or lname in conn_tokens:
                continue
            name = next(n for n in self.headers.keys() if n.lower() == lname)
            for value in self.headers.get_all(lname):
                out.append((name, value))
        out.append(("Host", CFG["upstream_host"]))
        out.append(("Accept-Encoding", "identity"))
        out.append(("Content-Length", str(body_len)))
        return out

    def _upstream_call(self, method, path, headers, body):
        timeout = CFG["timeout"]
        if CFG["upstream_tls"]:
            conn = http.client.HTTPSConnection(
                CFG["upstream_host"],
                CFG["upstream_port"],
                timeout=CFG["connect_timeout"],
                context=ssl.create_default_context(),
            )
        else:
            conn = http.client.HTTPConnection(
                CFG["upstream_host"],
                CFG["upstream_port"],
                timeout=CFG["connect_timeout"],
            )
        try:
            try:
                # Connect phase: TLS handshake + request send.
                conn.connect()
                conn.sock.settimeout(timeout)
                conn.putrequest(method, path, skip_host=True,
                                skip_accept_encoding=True)
                for name, value in headers:
                    conn.putheader(name, value)
                conn.endheaders(body if body else None)
            except ssl.SSLError:
                raise UpstreamError("tap_upstream_tls") from None
            except (socket.timeout, TimeoutError):
                raise UpstreamError("tap_upstream_timeout") from None
            except OSError:
                raise UpstreamError("tap_upstream_connect") from None
            try:
                # Read phase: OSError/HTTPException here are not covered by
                # the spec table -> bubble to the catch-all (tap_internal).
                resp = conn.getresponse()
                data = resp.read(CFG["max_response"] + 1)
            except ssl.SSLError:
                raise UpstreamError("tap_upstream_tls") from None
            except (socket.timeout, TimeoutError):
                raise UpstreamError("tap_upstream_timeout") from None
            return resp.status, resp.reason, resp.getheaders(), data
        finally:
            conn.close()


class TapServer(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        # Protocol-level exceptions: type name only, never a traceback.
        err = sys.exc_info()[1]
        name = type(err).__name__ if err is not None else "unknown"
        print(f"handle_error {name}", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(
        description="NTS usage tap: forwards /v1/* to the pinned OpenAI "
        "upstream and appends one D6 JSONL line per call.",
        epilog="Configuration is by environment only; see the module "
        "docstring or 01.3.1-T2-usage-tap.md.",
    )
    parser.parse_args()

    global CFG, BUSY
    CFG = load_config()
    BUSY = threading.Semaphore(CFG["concurrency"])

    try:
        server = TapServer((CFG["listen_host"], CFG["port"]), TapHandler)
    except OSError as e:
        die_config(f"cannot bind {CFG['listen_host']}:{CFG['port']} "
                   f"({type(e).__name__})")

    # tap.json once bound (NTS01-SEC-04): pid + upstream let start-mpt-api.sh
    # confirm this process owns 8002 before any Keychain read.
    tap_meta = {
        "started_at": now_iso_ms(),
        "host": CFG["listen_host"],
        "port": CFG["port"],
        "upstream": CFG["upstream"],
        "image_quality_injection": CFG["quality"],
        "timeout_s": CFG["timeout"],
        "pid": os.getpid(),
    }
    try:
        os.makedirs(CFG["nts_home"], mode=0o755, exist_ok=True)
        tmp = CFG["nts_home"] / f".tap.json.{os.getpid()}.tmp"
        tmp.write_text(
            json.dumps(tap_meta, separators=(",", ":")) + "\n", encoding="utf-8"
        )
        os.replace(tmp, CFG["nts_home"] / "tap.json")
    except OSError as e:
        die_config(f"cannot write tap.json: {type(e).__name__}")

    print(
        f"{now_iso_ms()} usage tap listening on "
        f"{CFG['listen_host']}:{CFG['port']} -> {CFG['upstream']}",
        file=sys.stderr,
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
