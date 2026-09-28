#!/usr/bin/env python3
"""AI rater pass for the blind contact sheet (03.3.2-T1, owner 2026-09-28: "AI chấm", 03 T-AI).

One `POST {base}/responses` per keyword x aspect group: the 4 blind images (letters A-D, from
`<run>/blind/`, downscaled copies) + keyword + template text; the model returns one JSON object
per letter (rating 1-5, text_visible, face_realistic, gore, off_prompt, note) under a strict
json_schema. The rater never sees `contact-sheet-key.json` or the `img/` names.

    uv run --no-project --python 3.12 trial/tools/image_rater.py --run DIR --rater-id ai-1 \
        --model gpt-6-luna --out trial/images/contact-sheet-ratings.ai-1.csv
    uv run --no-project --python 3.12 trial/tools/image_rater.py --merge A.csv B.csv \
        --out trial/images/contact-sheet-ratings.csv
    uv run --no-project --python 3.12 trial/tools/image_rater.py --self-test

Key from env `OPENAI_API_KEY` only; never printed. No retries (a failed group is reported, exit 1);
a rerun skips groups already in --out. Exit codes: 0 ok / 1 group failed / 2 usage error.
"""
from __future__ import annotations

import argparse
import base64
import csv
import ipaddress
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
LETTERS = "ABCD"
FLAGS = ("text_visible", "face_realistic", "gore", "off_prompt")
COLUMNS = ["cell_id", "letter", "rating_1_5", *FLAGS, "note", "rater_id"]
MERGED_COLUMNS = ["cell_id", "letter", "rating_1_5", *FLAGS, "note"]
CELL_RE = re.compile(r"^(k\d{2}-(?:landscape|portrait))-([A-D])\.png$")
TIMEOUT_S = 300
MAX_EDGE = 768
NOTE_MAX = 240
SMALL_EDGE, SMALL_JPEG_Q = 640, 60  # retry size when a group's body passes MAX_BODY_BYTES
MAX_BODY_BYTES = 900_000  # the tap refuses bodies over 1 MiB (usage_tap.py TAP_MAX_BODY_BYTES)

RUBRIC = """You rate AI-generated still images for a narrated night-story video channel.
Each image is one scene illustration. Rate each of the 4 images (letters A-D) independently
against the scene keyword and the style template text. Scale:
5 = excellent: matches keyword and style, clean composition, usable as-is in a video;
4 = good: small flaws a viewer would not notice;
3 = acceptable: visible flaws or partial style/keyword mismatch, usable in a pinch;
2 = poor: clear defects (anatomy, artefacts, wrong style) or weak keyword match;
1 = unusable.
Flags (true/false): text_visible = any readable or pseudo text, letters, logos, watermark;
face_realistic = a realistic, identifiable human face; gore = blood, wounds or gore;
off_prompt = the main subject of the keyword is missing or replaced.
Note: at most 25 words, concrete. Judge only what is visible. Do not guess how the images were
made. Return JSON only."""

SCHEMA = {
    "type": "object", "additionalProperties": False, "required": ["ratings"],
    "properties": {"ratings": {
        "type": "array", "minItems": 4, "maxItems": 4,
        "items": {
            "type": "object", "additionalProperties": False,
            "required": ["letter", "rating_1_5", *FLAGS, "note"],
            "properties": {
                "letter": {"type": "string", "enum": list(LETTERS)},
                "rating_1_5": {"type": "integer", "enum": [1, 2, 3, 4, 5]},
                **{f: {"type": "boolean"} for f in FLAGS},
                "note": {"type": "string", "maxLength": NOTE_MAX},
            },
        },
    }},
}


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):  # noqa: D102 - urllib hook
        return None


def groups(blind: Path) -> dict:
    out: dict = {}
    for p in sorted(blind.iterdir()):
        m = CELL_RE.match(p.name)
        if m:
            out.setdefault(m.group(1), {})[m.group(2)] = p
    return out


def load_matrix(path: Path) -> dict:
    doc = json.loads(path.read_text(encoding="utf-8"))
    return {r["id"]: r for r in doc["rows"]}


def downscale(src: Path, tmp: Path, edge: int = MAX_EDGE, jpeg_q: int = 80) -> bytes:
    dst = tmp / (src.stem + ".jpg")
    subprocess.run(["sips", "-Z", str(edge), "-s", "format", "jpeg", "-s", "formatOptions", str(jpeg_q),
                    str(src), "--out", str(dst)], check=True, capture_output=True,
                   env={"PATH": "/usr/bin:/bin"})
    return dst.read_bytes()


def request_body(model: str, effort: str, keyword: str, template: str, images: dict) -> dict:
    content = [{"type": "input_text",
                "text": f"Scene keyword: {keyword}\nStyle template: {template}\n"
                        "Rate images A, B, C, D in that order."}]
    for letter in LETTERS:
        content.append({"type": "input_text", "text": f"Image {letter}:"})
        content.append({"type": "input_image", "detail": "low",
                        "image_url": "data:image/jpeg;base64," + base64.b64encode(images[letter]).decode()})
    return {"model": model, "reasoning": {"effort": effort}, "store": False,
            "input": [{"role": "system", "content": [{"type": "input_text", "text": RUBRIC}]},
                      {"role": "user", "content": content}],
            "text": {"format": {"type": "json_schema", "name": "ratings", "schema": SCHEMA, "strict": True}}}


def parse_ratings(payload) -> list:
    """The 4 rating objects, validated; raises ValueError."""
    text = None
    if isinstance(payload, dict):
        text = payload.get("output_text")
        for item in payload.get("output") or []:
            if isinstance(item, dict) and item.get("type") == "message":
                for c in item.get("content") or []:
                    if isinstance(c, dict) and c.get("type") == "output_text":
                        text = c.get("text")
    if not isinstance(text, str):
        raise ValueError("no output_text")
    rows = json.loads(text)["ratings"]
    if sorted(r["letter"] for r in rows) != list(LETTERS):
        raise ValueError("letters are not exactly A-D")
    for r in rows:
        if type(r["rating_1_5"]) is not int or r["rating_1_5"] not in (1, 2, 3, 4, 5) \
                or any(not isinstance(r[f], bool) for f in FLAGS):
            raise ValueError("bad rating or flag")
    return rows


def check_base_url(base_url: str, allow_remote: bool) -> str:
    """SEC-0332-2 (as mpt_client.py SEC-0541-3): '' if ok, else the usage error."""
    try:
        parsed = urllib.parse.urlsplit(base_url)
        host = parsed.hostname or ""
    except ValueError as exc:
        return f"unusable --base-url: {exc}"
    if parsed.scheme not in ("http", "https"):
        return "--base-url must be http or https"
    if allow_remote:
        return ""
    try:
        loopback = ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = host.lower() == "localhost"
    return "" if loopback else "--base-url is not loopback; the key would leave this host (--allow-remote)"


def clean_note(note: str) -> str:
    return " ".join(note.split())[:NOTE_MAX]


def post(base: str, key: str, body: dict) -> tuple:
    req = urllib.request.Request(base.rstrip("/") + "/responses", method="POST",
                                 data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    req.add_unredirected_header("Authorization", "Bearer " + key)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())
    try:
        with opener.open(req, timeout=TIMEOUT_S) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read() if exc.fp else b""


def done_cells(path: Path) -> set:
    if not path.exists():
        return set()
    with path.open(newline="", encoding="utf-8") as fh:
        return {r["cell_id"] for r in csv.DictReader(fh)}


def append(path: Path, columns: list, rows: list) -> None:
    new = not path.exists()
    with path.open("a", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=columns)
        if new:
            w.writeheader()
        w.writerows(rows)


def rate(args, key: str) -> int:
    run = Path(args.run)
    if not (run / "blind").is_dir():
        raise ValueError(f"no blind/ folder in --run {run}")
    matrix = load_matrix(Path(args.matrix))
    templates = {t["name"]: t["template"]
                 for t in json.loads(Path(args.templates).read_text(encoding="utf-8"))["templates"]}
    out = Path(args.out)
    skip = done_cells(out)
    failed = 0
    with tempfile.TemporaryDirectory() as td:
        for cell, files in groups(run / "blind").items():
            if cell in skip:
                continue
            if sorted(files) != list(LETTERS):
                print(f"{cell}: incomplete group {sorted(files)}, skipped", file=sys.stderr)
                failed += 1
                continue
            try:
                row = matrix[cell[:3]]
                images = {l: downscale(files[l], Path(td)) for l in LETTERS}
                body = request_body(args.model, args.effort, row["keyword"], templates[row["template"]], images)
                if len(json.dumps(body).encode("utf-8")) > MAX_BODY_BYTES:
                    images = {l: downscale(files[l], Path(td), SMALL_EDGE, SMALL_JPEG_Q) for l in LETTERS}
                    body = request_body(args.model, args.effort, row["keyword"], templates[row["template"]],
                                        images)
                status, raw = post(args.base_url, key, body)
                if status != 200:
                    raise ValueError(f"http {status}")
                ratings = parse_ratings(json.loads(raw))
            except (ValueError, KeyError, TypeError, OSError, subprocess.CalledProcessError) as exc:
                print(f"{cell}: FAILED {type(exc).__name__}: {exc}", file=sys.stderr)
                failed += 1
                continue
            append(out, COLUMNS, [{"cell_id": cell, "letter": r["letter"], "rating_1_5": r["rating_1_5"],
                                   **{f: int(r[f]) for f in FLAGS}, "note": "AI: " + clean_note(r["note"]),
                                   "rater_id": args.rater_id}
                                  for r in sorted(ratings, key=lambda r: r["letter"])])
            print(f"{cell}: " + " ".join(f"{r['letter']}={r['rating_1_5']}" for r in sorted(ratings, key=lambda r: r["letter"])),
                  flush=True)
    return 1 if failed else 0


def merge(paths: list, out: Path) -> int:
    """80 rows: rating = mean over raters (1 dp), flag = 1 if any rater set it (conservative)."""
    acc: dict = {}
    for p in paths:
        if not Path(p).is_file():
            print(f"merge: no such file {p}", file=sys.stderr)
            return 2
        try:
            with Path(p).open(newline="", encoding="utf-8") as fh:
                for r in csv.DictReader(fh):
                    acc.setdefault((r["cell_id"], r["letter"]), []).append(r)
        except (OSError, UnicodeDecodeError, KeyError, csv.Error) as exc:
            print(f"merge: cannot read {p}: {type(exc).__name__}: {exc}", file=sys.stderr)
            return 2
    counts = {len(v) for v in acc.values()}
    if counts != {len(paths)}:
        print(f"merge: raters per image {sorted(counts)} != {len(paths)}", file=sys.stderr)
        return 1
    rows = []
    try:
        for (cell, letter), rs in sorted(acc.items()):
            ratings = [int(r["rating_1_5"]) for r in rs]
            if any(not 1 <= x <= 5 for x in ratings):
                raise ValueError(f"{cell}-{letter}: rating outside 1-5")
            rows.append({"cell_id": cell, "letter": letter,
                         "rating_1_5": f"{sum(ratings) / len(rs):.1f}",
                         **{f: int(any(r[f] == "1" for r in rs)) for f in FLAGS},
                         "note": " | ".join(f"{r['rater_id']} {r['note']}" for r in rs)})
    except (ValueError, KeyError, TypeError) as exc:
        print(f"merge: bad input row: {exc}", file=sys.stderr)
        return 2
    tmp = out.with_suffix(".tmp")
    with tmp.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=MERGED_COLUMNS)
        w.writeheader()
        w.writerows(rows)
    os.replace(tmp, out)
    print(f"merged {len(rows)} rows from {len(paths)} raters")
    return 0


def self_test() -> int:
    fails = []

    def check(name, ok):
        print(f"{'ok  ' if ok else 'FAIL'} {name}")
        fails.append(name) if not ok else None

    good = {"output": [{"type": "message", "content": [{"type": "output_text", "text": json.dumps({"ratings": [
        {"letter": l, "rating_1_5": 4, **{f: False for f in FLAGS}, "note": "x"} for l in "DCBA"]})}]}]}
    check("parse_valid_ratings", len(parse_ratings(good)) == 4)
    bad = json.loads(json.dumps(good))
    txt = json.loads(bad["output"][0]["content"][0]["text"])
    txt["ratings"][0]["letter"] = "A"
    bad["output"][0]["content"][0]["text"] = json.dumps(txt)
    try:
        parse_ratings(bad)
        check("duplicate_letter_rejected", False)
    except ValueError:
        check("duplicate_letter_rejected", True)
    body = request_body("m", "low", "kw", "tpl", {l: b"\xff\xd8" for l in LETTERS})
    texts = " ".join(c["text"] for m in body["input"] for c in m["content"] if c["type"] == "input_text")
    check("request_has_no_quality_word",
          not re.search(r"\b(low|medium|high|auto)\b", texts, re.I)
          and sum(c["type"] == "input_image" for c in body["input"][1]["content"]) == 4)
    with tempfile.TemporaryDirectory() as td:
        a, b, o = Path(td, "a.csv"), Path(td, "b.csv"), Path(td, "o.csv")
        for p, rid, rating, flag in ((a, "ai-1", 4, 0), (b, "ai-2", 3, 1)):
            append(p, COLUMNS, [{"cell_id": "k01-landscape", "letter": l, "rating_1_5": rating,
                                 "text_visible": flag, "face_realistic": 0, "gore": 0, "off_prompt": 0,
                                 "note": "AI: n", "rater_id": rid} for l in LETTERS])
        rc = merge([a, b], o)
        rows = list(csv.DictReader(o.open()))
        check("merge_mean_and_any_flag", rc == 0 and len(rows) == 4
              and all(r["rating_1_5"] == "3.5" and r["text_visible"] == "1" for r in rows))
        check("merge_rejects_missing_rater", merge([a, b, Path(td, "none.csv")], o) == 1 if Path(td, "none.csv").write_text(",".join(COLUMNS) + "\n") else False)
        check("merge_missing_file_exit_2", merge([a, Path(td, "absent.csv")], o) == 2)
    for v in (True, 4.0):
        nb = json.loads(json.dumps(good))
        t = json.loads(nb["output"][0]["content"][0]["text"])
        t["ratings"][0]["rating_1_5"] = v
        nb["output"][0]["content"][0]["text"] = json.dumps(t)
        try:
            parse_ratings(nb)
            check(f"non_int_rating_rejected[{v!r}]", False)
        except ValueError:
            check(f"non_int_rating_rejected[{v!r}]", True)
    print(f"self-test {'FAILED' if fails else 'ok'} ({len(fails)} failed)")
    return 1 if fails else 0


def main(argv: list) -> int:
    p = argparse.ArgumentParser(description="AI rater pass for the blind contact sheet (03.3.2-T1).")
    p.add_argument("--run", help="contact-sheet run folder (has blind/)")
    p.add_argument("--rater-id")
    p.add_argument("--model")
    p.add_argument("--effort", default="medium", choices=("low", "medium", "high"))
    p.add_argument("--matrix", default=str(REPO_ROOT / "trial/images/contact-sheet-matrix.json"))
    p.add_argument("--templates", default=str(REPO_ROOT / "seed/style-templates.v1.json"))
    p.add_argument("--base-url", default="http://127.0.0.1:8002/v1")
    p.add_argument("--allow-remote", action="store_true", help="allow a non-loopback --base-url")
    p.add_argument("--out")
    p.add_argument("--merge", nargs="+")
    p.add_argument("--self-test", action="store_true")
    args = p.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.merge:
        if not args.out:
            print("usage: --merge A.csv B.csv --out FILE", file=sys.stderr)
            return 2
        return merge(args.merge, Path(args.out))
    if not (args.run and args.rater_id and args.model and args.out):
        print("usage: --run DIR --rater-id ai-N --model M --out FILE (or --merge / --self-test)", file=sys.stderr)
        return 2
    if not re.fullmatch(r"ai-\d+", args.rater_id):
        print("usage: --rater-id must be ai-<n>", file=sys.stderr)
        return 2
    key = os.environ.get("OPENAI_API_KEY", "")
    if not key:
        print("OPENAI_API_KEY is empty", file=sys.stderr)
        return 2
    err = check_base_url(args.base_url, args.allow_remote)
    if err:
        print(f"usage error: {err}", file=sys.stderr)
        return 2
    try:
        return rate(args, key)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print(f"input error: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
