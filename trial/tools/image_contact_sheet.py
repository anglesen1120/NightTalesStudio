#!/usr/bin/env python3
"""Blind image-quality contact sheet + per-quality tokens (03.3.2-T1), stdlib only.

    trial/tools/run-contact-sheet.sh --mode sheet --out "$NTS_HOME/runs/contact-sheet-YYYYmmdd"
    uv run --no-project --python 3.12 trial/tools/image_contact_sheet.py --self-test

One `POST {base}/responses` per cell through the usage tap (01 D6, 01 DEC-11/03 CR-15) with the
`image_generation` tool; `quality` is omitted for the `auto` cell (I1). No retries: a failed cell is
recorded and left empty. The key comes from env `OPENAI_API_KEY` only (set by the wrapper from the
Keychain, 01 D3); it is never printed, logged or written. Redirects are not followed.

Cost per call = text_tokens x 5 + image_tokens x 8 + output_tokens x 30 per 1M (03 O8 reference rates);
a call without `usage` is charged at the plan section 7 worst case (USAGE_ABSENT_BOUND_USD) for the
budget guard and gets cost_usd `n/a`. The guard counts every row already in the results CSV, so the
second batch gets the remaining budget; running total >= --max-usd stops before the next call (exit 3).

Exit codes: 0 ok / 1 some cells failed or check failed / 2 usage error / 3 budget reached.
"""
from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import html
import http.server
import json
import ipaddress
import os
import random
import re
import shutil
import struct
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
QUALITIES = ("low", "medium", "high", "auto")
ASPECTS = {"landscape": "1536x1024", "portrait": "1024x1536"}
LETTERS = "ABCD"
IMAGE_MODEL = "gpt-image-2"
RATE_TEXT_IN, RATE_IMAGE_IN, RATE_IMAGE_OUT = 5.0, 8.0, 30.0  # USD per 1M tokens (O8)
USAGE_ABSENT_BOUND_USD = 0.08  # plan section 7 worst case per image
TIMEOUT_S = 300
NEGATIVE_CLAUSE_V1 = ("No gore, no blood, no text, no letters, no logos, no watermark, "
                      "no real person likeness.")
NEGATIVE_CLAUSE_SHA256 = "e1ba7fa9b85b7a46fc53e2a3110f1a88439a665df1553ee4e7f4238ddf1979a7"
# Owner 2026-09-28 (05 O-9:16 option A): MPT cover-crops a landscape image to 9:16, keeping ~37 % of its width.
PORTRAIT_HINT = "Main subject centred in the middle third of the frame, empty margins on the left and right."
PREVIEW_KEYWORDS = {  # 03.1.2-T1 Design
    "ghost": "old wooden door with a brass handle, night",
    "spiritual": "small roadside shrine with incense smoke at dawn",
    "true_crime": "rural road at night, single streetlight, wet asphalt",
}
RESULT_COLUMNS = ["cell_id", "keyword_id", "template", "aspect", "quality_requested", "quality_echoed",
                  "size", "http_status", "error_code", "text_tokens", "image_tokens", "output_tokens",
                  "cost_usd", "latency_ms", "size_actual", "note"]
RATING_COLUMNS = ["cell_id", "letter", "rating_1_5", "text_visible", "face_realistic", "gore",
                  "off_prompt", "note"]


class UsageError(Exception):
    pass


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):  # noqa: D102 - urllib hook
        return None


_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())


def build_prompt(template: str, keyword: str, portrait_hint: bool = False) -> str:
    if template.count("{term}") != 1:
        raise UsageError("template must contain exactly one {term}")
    styled = template.replace("{term}", keyword)
    return styled + (" " + PORTRAIT_HINT if portrait_hint else "") + " " + NEGATIVE_CLAUSE_V1


def request_body(prompt: str, size: str, quality: str, host_model: str, effort: str) -> dict:
    tool = {"type": "image_generation", "model": IMAGE_MODEL, "size": size}
    if quality != "auto":
        tool["quality"] = quality
    return {"model": host_model, "input": prompt, "reasoning": {"effort": effort}, "store": False,
            "tools": [tool], "tool_choice": {"type": "image_generation"}}


def usage_tokens(usage) -> tuple:
    """(text_tokens, image_tokens, output_tokens) or None when usage is absent."""
    if not isinstance(usage, dict) or not isinstance(usage.get("output_tokens"), int):
        return None
    details = usage.get("input_tokens_details") if isinstance(usage.get("input_tokens_details"), dict) else {}
    total_in = usage.get("input_tokens") if isinstance(usage.get("input_tokens"), int) else 0
    image_in = details.get("image_tokens") if isinstance(details.get("image_tokens"), int) else 0
    text_in = details.get("text_tokens") if isinstance(details.get("text_tokens"), int) else total_in - image_in
    return max(text_in, 0), image_in, usage["output_tokens"]


def cost_usd(tokens) -> float:
    text_in, image_in, out = tokens
    return (text_in * RATE_TEXT_IN + image_in * RATE_IMAGE_IN + out * RATE_IMAGE_OUT) / 1e6


def guard_cost(row: dict) -> float:
    """Budget-guard charge of one results row (usage-absent 200 rows at the bound)."""
    try:
        return float(row["cost_usd"])
    except (KeyError, TypeError, ValueError):
        return USAGE_ABSENT_BOUND_USD if str(row.get("http_status")) == "200" else 0.0


def blind_letters(keyword_id: str, aspect: str) -> dict:
    """quality -> letter from a seeded shuffle of A-D (03.3.2-T1 Design)."""
    seed = int(hashlib.sha256((keyword_id + aspect).encode("utf-8")).hexdigest(), 16)
    letters = list(LETTERS)
    random.Random(seed).shuffle(letters)
    return dict(zip(QUALITIES, letters))


def image_format(data: bytes) -> str:
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "png"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return "webp"
    return "jpeg" if data[:3] == b"\xff\xd8\xff" else "unknown"


def image_dims(data: bytes) -> str:
    if image_format(data) == "png" and len(data) >= 24:
        w, h = struct.unpack(">II", data[16:24])
        return f"{w}x{h}"
    return "unknown"


def add_note(row: dict, word: str) -> None:
    row["note"] = ";".join(w for w in (row.get("note"), word) if w)


def _post(url: str, key: str, body: dict) -> tuple:
    raw = json.dumps(body, separators=(",", ":")).encode("utf-8")
    req = urllib.request.Request(url, data=raw, method="POST",
                                 headers={"Content-Type": "application/json"})
    req.add_unredirected_header("Authorization", "Bearer " + key)
    try:
        with _OPENER.open(req, timeout=TIMEOUT_S) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read() if exc.fp else b""


MAX_DOWNLOAD_BYTES = 32 * 1024 * 1024
ID_RE, TEMPLATE_RE = re.compile(r"k\d{2}"), re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*")


def is_loopback(host: str) -> bool:
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return host.lower() == "localhost"


def check_base_url(base_url: str, allow_remote: bool) -> None:
    """SEC-0332-2 (as mpt_client.py SEC-0541-3): http(s) only, loopback unless --allow-remote."""
    try:
        parsed = urllib.parse.urlsplit(base_url)
        host = parsed.hostname or ""
    except ValueError as exc:
        raise UsageError(f"unusable --base-url: {exc}") from exc
    if parsed.scheme not in ("http", "https"):
        raise UsageError("--base-url must be http or https")
    if not allow_remote and not is_loopback(host):
        raise UsageError("--base-url is not loopback; the key would leave this host (--allow-remote)")


def _origin(url: str) -> tuple:
    parsed = urllib.parse.urlsplit(url)
    return (parsed.scheme, (parsed.hostname or "").lower(), parsed.port)


def _download(url: str, base: str) -> bytes:
    """SEC-0332-3: same origin as --base-url only, image content type, body capped at MAX_DOWNLOAD_BYTES."""
    try:
        same = _origin(url) == _origin(base)
    except ValueError:
        same = False
    if not same:
        raise ValueError("image url origin not allowed")
    with _OPENER.open(urllib.request.Request(url), timeout=TIMEOUT_S) as resp:
        if not resp.headers.get("Content-Type", "").startswith("image/"):
            raise ValueError("image url content type not allowed")
        if int(resp.headers.get("Content-Length") or 0) > MAX_DOWNLOAD_BYTES:
            raise ValueError("image url body too large")
        data = resp.read(MAX_DOWNLOAD_BYTES + 1)
    if len(data) > MAX_DOWNLOAD_BYTES:
        raise ValueError("image url body too large")
    return data


def _error_code(payload, status: int) -> str:
    if isinstance(payload, dict) and isinstance(payload.get("error"), dict):
        err = payload["error"]
        code = err.get("code") or err.get("type")
        if isinstance(code, str) and code:
            return code[:64]
    return f"http_{status}" if status else "transport_error"


def extract_image(payload) -> tuple:
    """(image_bytes_or_url, echoed_quality, echoed_size) from a Responses or Images payload."""
    if not isinstance(payload, dict):
        return None, None, None
    if isinstance(payload.get("output"), list):
        for item in payload["output"]:
            if isinstance(item, dict) and item.get("type") == "image_generation_call" \
                    and isinstance(item.get("result"), str) and item["result"]:
                return base64.b64decode(item["result"]), item.get("quality"), item.get("size")
    if isinstance(payload.get("data"), list) and payload["data"]:
        d = payload["data"][0]
        if isinstance(d, dict):
            if isinstance(d.get("b64_json"), str):
                return base64.b64decode(d["b64_json"]), payload.get("quality"), payload.get("size")
            if isinstance(d.get("url"), str):
                return d["url"], payload.get("quality"), payload.get("size")
    return None, None, None


def call_cell(base: str, key: str, body: dict, dest: Path) -> dict:
    """One call, no retries. Returns the results-row fields (without the cell identity)."""
    t0 = time.monotonic()
    try:
        status, raw = _post(base.rstrip("/") + "/responses", key, body)
    except (urllib.error.URLError, OSError, TimeoutError) as exc:
        return {"http_status": 0, "error_code": f"transport_{type(exc).__name__}"[:64],
                "latency_ms": int((time.monotonic() - t0) * 1000)}
    latency = int((time.monotonic() - t0) * 1000)
    try:
        payload = json.loads(raw) if raw else None
    except ValueError:
        payload = None
    row = {"http_status": status, "latency_ms": latency, "error_code": ""}
    tokens = usage_tokens(payload.get("usage")) if isinstance(payload, dict) else None
    if tokens:
        row.update(text_tokens=tokens[0], image_tokens=tokens[1], output_tokens=tokens[2],
                   cost_usd=f"{cost_usd(tokens):.6f}")
    else:
        row.update(cost_usd="n/a", note="usage_absent" if status == 200 else "")
    if status != 200:
        row["error_code"] = _error_code(payload, status)
        return row
    image, q_echo, s_echo = extract_image(payload)
    row["quality_echoed"] = q_echo if isinstance(q_echo, str) else "not_echoed"
    row["size"] = s_echo if isinstance(s_echo, str) else "not_echoed"
    if isinstance(image, str):
        try:
            image = _download(image, base)
        except (urllib.error.URLError, OSError, ValueError) as exc:
            row["error_code"] = f"download_{type(exc).__name__}"[:64]
            return row
    if not image:
        row["error_code"] = "image_missing"
        return row
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(".part")
    tmp.write_bytes(image)
    os.replace(tmp, dest)
    row["size_actual"] = image_dims(image)
    if image_format(image) != "png":
        add_note(row, "format_" + image_format(image))
    return row


def read_rows(path: Path) -> list:
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8") as fh:
        return list(csv.DictReader(fh))


def append_row(path: Path, columns: list, row: dict) -> None:
    new = not path.exists()
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=columns, extrasaction="ignore")
        if new:
            w.writeheader()
        w.writerow({c: row.get(c, "") for c in columns})
        fh.flush()
        os.fsync(fh.fileno())


def load_inputs(matrix_path: Path, templates_path: Path) -> tuple:
    try:
        matrix = json.loads(matrix_path.read_text(encoding="utf-8"))
        seed = json.loads(templates_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise UsageError(f"cannot read inputs: {exc}") from exc
    if hashlib.sha256(NEGATIVE_CLAUSE_V1.encode("utf-8")).hexdigest() != NEGATIVE_CLAUSE_SHA256:
        raise UsageError("NegativeClause.V1 text does not match its sha256")
    templates = {t["name"]: t for t in seed.get("templates", []) if t.get("active")}
    bad = sorted(n for n in templates if not isinstance(n, str) or not TEMPLATE_RE.fullmatch(n))
    if bad:
        raise UsageError(f"template names must be lowercase slugs: {bad}")
    rows = matrix.get("rows", [])
    for r in rows:
        if not isinstance(r.get("id"), str) or not ID_RE.fullmatch(r["id"]):
            raise UsageError(f"matrix id must be k<nn>: {r.get('id')!r}")
        if r.get("template") not in templates:
            raise UsageError(f"{r.get('id')}: unknown or inactive template {r.get('template')!r}")
    return rows, templates


def write_key(path: Path, rows: list, aspects: list) -> None:
    key = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    for r in rows:
        for aspect in aspects:
            letters = blind_letters(r["id"], aspect)
            key[f"{r['id']}-{aspect}"] = {letters[q]: q for q in QUALITIES}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(dict(sorted(key.items())), indent=2) + "\n", encoding="utf-8")


def write_blind_page(out: Path, rows: list, aspects: list) -> None:
    """index.html + blind/<cell>-<letter>.png; no quality word anywhere in the page."""
    blind = out / "blind"
    blind.mkdir(parents=True, exist_ok=True)
    sections = []
    for aspect in aspects:
        for r in rows:
            cell = f"{r['id']}-{aspect}"
            letters = blind_letters(r["id"], aspect)
            figs = []
            for q in QUALITIES:
                src = out / "img" / f"{cell}-{q}.png"
                name = f"{cell}-{letters[q]}.png"
                if src.exists():
                    dst = blind / name
                    if dst.exists():
                        dst.unlink()
                    try:
                        os.link(src, dst)
                    except OSError:
                        shutil.copyfile(src, dst)
                figs.append((letters[q], name if src.exists() else None))
            figs.sort()
            cells = "".join(
                f'<figure><figcaption>{html.escape(cell)} &middot; {letter}</figcaption>'
                + (f'<img src="blind/{html.escape(name)}" alt="{html.escape(cell)} {letter}" loading="lazy">'
                   if name else "<p>(empty)</p>")
                + f'<div class="r" data-cell="{html.escape(cell)}" data-letter="{letter}">'
                  '<label>rating <select><option></option>'
                  + "".join(f"<option>{n}</option>" for n in range(1, 6))
                + '</select></label> <label><input type="checkbox" value="text_visible">text</label>'
                  ' <label><input type="checkbox" value="face_realistic">face</label>'
                  ' <label><input type="checkbox" value="gore">gore</label>'
                  ' <label><input type="checkbox" value="off_prompt">off prompt</label>'
                  ' <input type="text" placeholder="note"></div></figure>'
                for letter, name in figs)
            sections.append(f"<h2>{html.escape(cell)}: {html.escape(r['keyword'])} "
                            f"<small>({html.escape(r['template'])})</small></h2><div class=g>{cells}</div>")
    page = f"""<!doctype html><meta charset="utf-8"><title>Contact sheet (blind)</title>
<style>body{{font:14px system-ui;margin:1em}}.g{{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}}
figure{{margin:0}}img{{width:100%}}.r{{font-size:12px}}button{{position:sticky;top:0;font-size:16px}}</style>
<p>Rate each image 1-5 and tick the flags, then press the button and save the file as
<code>trial/images/contact-sheet-ratings.csv</code>. Open <code>contact-sheet-key.json</code> only after rating.</p>
<button id=dl>Download ratings CSV</button>
{''.join(sections)}
<script>
document.getElementById('dl').onclick=()=>{{
 const q=s=>'"'+String(s).replace(/"/g,'""')+'"';
 const rows=[{json.dumps(",".join(RATING_COLUMNS))}];
 document.querySelectorAll('.r').forEach(d=>{{
  const f=v=>d.querySelector('input[value="'+v+'"]').checked?1:0;
  rows.push([d.dataset.cell,d.dataset.letter,d.querySelector('select').value,
   f('text_visible'),f('face_realistic'),f('gore'),f('off_prompt'),q(d.querySelector('input[type=text]').value)].join(','));
 }});
 const a=document.createElement('a');
 a.href=URL.createObjectURL(new Blob([rows.join('\\n')+'\\n'],{{type:'text/csv'}}));
 a.download='contact-sheet-ratings.csv';a.click();
}};
</script>
"""
    (out / "index.html").write_text(page, encoding="utf-8")


def run(args, key: str, host_model: str, effort: str) -> int:
    rows, templates = load_inputs(Path(args.matrix), Path(args.templates))
    if getattr(args, "keywords", None):
        wanted = [k for k in args.keywords.split(",") if k]
        unknown = sorted(set(wanted) - {r["id"] for r in rows})
        if unknown:
            raise UsageError(f"--keywords: unknown ids {unknown}")
        rows = [r for r in rows if r["id"] in wanted]
    hint = bool(getattr(args, "portrait_hint", False))
    out, tracked = Path(args.out), Path(args.tracked_dir)
    aspects = [a for a in args.aspects.split(",") if a]
    qualities = [q for q in args.qualities.split(",") if q]
    if any(a not in ASPECTS for a in aspects) or any(q not in QUALITIES for q in qualities):
        raise UsageError(f"aspects must be in {tuple(ASPECTS)}, qualities in {QUALITIES}")
    if args.mode == "previews":
        if len(qualities) != 1:
            raise UsageError("--mode previews takes a single --qualities value")
        results = out / "previews-results.csv"
        cells = [(name, name, t["template"], PREVIEW_KEYWORDS[t["topicHint"]], "landscape", qualities[0],
                  out / "previews" / f"{name}.png") for name, t in templates.items()]
    else:
        results = tracked / "contact-sheet-results.csv"
        cells = [(f"{r['id']}-{a}", r["id"], templates[r["template"]]["template"], r["keyword"], a, q,
                  out / "img" / f"{r['id']}-{a}-{q}.png")
                 for a in aspects for r in rows for q in qualities]
    spent = sum(guard_cost(r) for r in read_rows(results))
    failed = calls = 0
    for cell_id, kid, template, keyword, aspect, quality, dest in cells:
        if dest.exists():
            continue
        if spent >= args.max_usd:
            print(f"budget reached: ${spent:.4f} >= --max-usd {args.max_usd}; stopping before {cell_id} "
                  f"{quality}", file=sys.stderr)
            return 3
        if calls and args.pace_seconds:
            time.sleep(args.pace_seconds)
        body = request_body(build_prompt(template, keyword, hint and aspect == "portrait"), ASPECTS[aspect],
                            quality, host_model, effort)
        row = call_cell(args.base_url, key, body, dest)
        calls += 1
        row.update(cell_id=cell_id, keyword_id=kid, template=kid if args.mode == "previews" else
                   next(r["template"] for r in rows if r["id"] == kid),
                   aspect=aspect, quality_requested=quality)
        if hint and aspect == "portrait":
            add_note(row, "portrait_hint")
        append_row(results, RESULT_COLUMNS, row)
        spent += guard_cost(row)
        failed += bool(row.get("error_code"))
        print(f"{cell_id} {quality}: status {row['http_status']} {row.get('error_code') or 'ok'} "
              f"cost {row.get('cost_usd')} total ${spent:.4f}", flush=True)
    if args.mode == "sheet":
        write_key(tracked / "contact-sheet-key.json", rows, aspects)
        done = sorted({a for a in ASPECTS if any((out / "img").glob(f"*-{a}-*.png"))}) if (out / "img").exists() else []
        write_blind_page(out, rows, [a for a in ASPECTS if a in done or a in aspects])
    print(f"calls {calls} failed {failed} total ${spent:.4f}")
    return 1 if failed else 0


# ---------------------------------------------------------------- self-test (local stub, no key, no spend)

PNG_1x1 = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")


class _Stub(http.server.BaseHTTPRequestHandler):
    mode = "responses"
    bodies: list = []

    def log_message(self, *a):  # silence
        pass

    def do_GET(self):  # the url-mode download
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.end_headers()
        self.wfile.write(PNG_1x1)

    def do_POST(self):
        n = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(n))
        _Stub.bodies.append(body)
        usage = {"input_tokens": 1000, "input_tokens_details": {"cached_tokens": 0}, "output_tokens": 100}
        tool = body["tools"][0]
        if _Stub.mode == "url":
            port = self.server.server_address[1]
            payload = {"data": [{"url": f"http://127.0.0.1:{port}/img.png"}], "usage": usage}
        else:
            payload = {"output": [{"type": "image_generation_call", "status": "completed",
                                   "result": base64.b64encode(PNG_1x1).decode(),
                                   "quality": tool.get("quality", "medium"), "size": tool["size"]}],
                       "usage": usage}
        raw = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def self_test() -> int:
    failures = []

    def check(name, ok):
        print(f"{'ok  ' if ok else 'FAIL'} {name}")
        if not ok:
            failures.append(name)

    tokens = usage_tokens({"input_tokens": 1000, "input_tokens_details": {"image_tokens": 200},
                           "output_tokens": 100})
    check("cost_from_usage_matches_rates",
          tokens == (800, 200, 100) and abs(cost_usd(tokens) - (800 * 5 + 200 * 8 + 100 * 30) / 1e6) < 1e-12
          and usage_tokens({"input_tokens": 5}) is None)
    base_prompt = build_prompt("Ink of {term}.", "a door")
    hinted = build_prompt("Ink of {term}.", "a door", True)
    check("portrait_hint_between_style_and_negative_clause",
          base_prompt == "Ink of a door. " + NEGATIVE_CLAUSE_V1
          and hinted == f"Ink of a door. {PORTRAIT_HINT} {NEGATIVE_CLAUSE_V1}")

    groups = [blind_letters(f"k{i:02d}", a) for i in range(1, 11) for a in ASPECTS]
    check("blind_letters_are_permutation_per_cell_group",
          all(sorted(g.values()) == list(LETTERS) for g in groups)
          and blind_letters("k01", "landscape") == blind_letters("k01", "landscape")
          and len({tuple(g.values()) for g in groups}) > 1)

    check("auto_cell_omits_quality_field",
          "quality" not in request_body("p", "1536x1024", "auto", "m", "low")["tools"][0]
          and request_body("p", "1536x1024", "low", "m", "low")["tools"][0]["quality"] == "low")

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _Stub)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_address[1]}/v1"
    try:
        with tempfile.TemporaryDirectory() as td:
            td = Path(td)
            matrix = td / "matrix.json"
            matrix.write_text(json.dumps({"rows": [
                {"id": "k01", "topic": "ghost", "keyword": "dim corridor", "template": "t-a"},
                {"id": "k02", "topic": "ghost", "keyword": "empty chair", "template": "t-a"}]}))
            seed = td / "seed.json"
            seed.write_text(json.dumps({"templates": [
                {"name": "t-a", "topicHint": "ghost", "active": True, "template": "Ink of {term}, muted."}]}))

            def args_for(out, max_usd, aspects="landscape"):
                return argparse.Namespace(mode="sheet", matrix=str(matrix), templates=str(seed), out=str(out),
                                          tracked_dir=str(out / "tracked"), aspects=aspects,
                                          qualities=",".join(QUALITIES), base_url=base, max_usd=max_usd,
                                          pace_seconds=0)

            _Stub.mode, _Stub.bodies = "responses", []
            rc = run(args_for(td / "a", 100), "stub-key", "host-m", "low")
            res = read_rows(td / "a" / "tracked" / "contact-sheet-results.csv")
            page = (td / "a" / "index.html").read_text()
            import re
            check("b64_and_url_responses_both_saved[b64]",
                  rc == 0 and len(res) == 8 and all((td / "a" / "img" / f"{r['cell_id']}-{r['quality_requested']}.png")
                                                     .read_bytes() == PNG_1x1 for r in res)
                  and all(r["size_actual"] == "1x1" and r["output_tokens"] == "100" for r in res))
            check("blind_page_has_no_quality_word",
                  not re.search(r"\b(low|medium|high|auto)\b", page, re.I) and page.count("<img ") == 8)
            key = json.loads((td / "a" / "tracked" / "contact-sheet-key.json").read_text())
            check("key_json_round_trip",
                  set(key) == {"k01-landscape", "k02-landscape"}
                  and all({v: k for k, v in key[c].items()} == blind_letters(c[:3], "landscape") for c in key))

            _Stub.mode = "url"
            rc = run(args_for(td / "b", 100), "stub-key", "host-m", "low")
            check("b64_and_url_responses_both_saved[url]",
                  rc == 0 and (td / "b" / "img" / "k01-landscape-low.png").read_bytes() == PNG_1x1)

            _Stub.mode, _Stub.bodies = "responses", []
            per_call = cost_usd((1000, 0, 100))
            rc = run(args_for(td / "c", per_call * 2.5), "stub-key", "host-m", "low")
            n_first = len(_Stub.bodies)
            rc2 = run(args_for(td / "c", per_call * 2.5, "portrait"), "stub-key", "host-m", "low")
            check("max_usd_stops_before_next_call",
                  rc == 3 and n_first == 3 and rc2 == 3 and len(_Stub.bodies) == 3
                  and len(read_rows(td / "c" / "tracked" / "contact-sheet-results.csv")) == 3)
    finally:
        server.shutdown()

    print(f"self-test {'FAILED' if failures else 'ok'} ({len(failures)} failed)")
    return 1 if failures else 0


def main(argv: list) -> int:
    p = argparse.ArgumentParser(description="Blind image-quality contact sheet (03.3.2-T1).")
    p.add_argument("--mode", choices=("sheet", "previews"))
    p.add_argument("--matrix", default=str(REPO_ROOT / "trial/images/contact-sheet-matrix.json"))
    p.add_argument("--templates", default=str(REPO_ROOT / "seed/style-templates.v1.json"))
    p.add_argument("--out", help="run folder, e.g. $NTS_HOME/runs/contact-sheet-YYYYmmdd")
    p.add_argument("--tracked-dir", default=str(REPO_ROOT / "trial/images"))
    p.add_argument("--aspects", default="landscape,portrait")
    p.add_argument("--qualities", default=",".join(QUALITIES))
    p.add_argument("--base-url", default="http://127.0.0.1:8002/v1")
    p.add_argument("--max-usd", type=float, default=8.0)
    p.add_argument("--pace-seconds", type=float, default=13.0)
    p.add_argument("--effort", choices=("none", "minimal", "low", "medium", "high", "xhigh"),
                   help="host-model reasoning effort (default: OPENAI_EFFORT_IMAGE_HOST pin)")
    p.add_argument("--keywords", help="comma-separated matrix ids to run (default: all rows)")
    p.add_argument("--portrait-hint", action="store_true",
                   help="append the centred-subject hint to portrait prompts (05 option A)")
    p.add_argument("--allow-remote", action="store_true", help="allow a non-loopback --base-url")
    p.add_argument("--self-test", action="store_true")
    args = p.parse_args(argv)
    if args.self_test:
        return self_test()
    if not args.mode or not args.out:
        print("usage: --mode sheet|previews --out DIR required (or --self-test)", file=sys.stderr)
        return 2
    key = os.environ.get("OPENAI_API_KEY", "")
    host_model = os.environ.get("OPENAI_IMAGE_HOST_MODEL", "")
    effort = args.effort or os.environ.get("OPENAI_EFFORT_IMAGE_HOST", "")
    if not key:
        print("OPENAI_API_KEY is empty; run through trial/tools/run-contact-sheet.sh", file=sys.stderr)
        return 2
    if not host_model or not effort:
        print("OPENAI_IMAGE_HOST_MODEL / OPENAI_EFFORT_IMAGE_HOST unset (load pins.env)", file=sys.stderr)
        return 2
    try:
        check_base_url(args.base_url, args.allow_remote)
        return run(args, key, host_model, effort)
    except UsageError as exc:
        print(f"usage error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
