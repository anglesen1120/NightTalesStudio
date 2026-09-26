#!/usr/bin/env python3
"""worker-node/proxy/tools/audition.py — 04.2.1-T1 blind voice audition tool.

Stdlib only (urllib.request, json, subprocess, secrets, random, csv,
unicodedata, argparse). Run:
  uv run --no-project --python 3.12 worker-node/proxy/tools/audition.py <cmd> …

Exit 0 ok · 1 runtime failure · 2 usage/config.
VIENEU_API_KEY is read from the environment only (Authorization: Bearer built
in-process); it is never printed, logged, or placed on any argv.
"""
from __future__ import annotations

import argparse
import os
import csv
import json
import random
import secrets
import subprocess
import sys
import time
import unicodedata
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
SNAPSHOT = REPO / "worker-node" / "vieneu" / "voices.snapshot.json"
TEXTS = {
    "vi": REPO / "trial" / "fixtures" / "voice-audition.vi.txt",
    "en": REPO / "trial" / "fixtures" / "voice-audition.en.txt",
}
URL = "http://127.0.0.1:8000/v1/audio/speech"
MODEL = "vieneu-v3-turbo"
SAMPLE_RATE = 48000
EXCLUDED_IDS = {"Adam bựa"}  # O4
MAX_429_RETRIES = 5
RATER_TAGS = {"deep", "warm", "calm", "bright", "whisper"}  # D9 vocabulary
SHEET_FIELDS = ["sample_id", "rater", "naturalness", "intelligibility",
                "night_fit", "mispronunciations", "rater_tags", "notes"]
RESULT_FIELDS = ["language", "sample_id", "voice_id", "gender", "region", "style",
                 "rater", "naturalness", "intelligibility", "night_fit",
                 "mispronunciations", "rater_tags", "notes"]

# SecRev P1: no env/system proxy may see the VieNeu key — empty ProxyHandler.
_PROXY_HANDLER = urllib.request.ProxyHandler({})
_OPENER = urllib.request.build_opener(_PROXY_HANDLER)



def die(code: int, msg: str):
    print(f"audition: {msg}", file=sys.stderr)
    sys.exit(code)


def load_snapshot(path: str) -> dict:
    try:
        snap = json.loads(Path(path).read_text(encoding="utf-8"))
    except Exception as e:  # noqa: BLE001
        die(2, f"snapshot unreadable: {path}: {e}")
    return snap


def candidates_list(snapshot: dict) -> list:
    out = []
    for v in snapshot.get("data", []):
        vid = v.get("id", "")
        if vid in EXCLUDED_IDS:
            continue
        if vid != unicodedata.normalize("NFC", vid):
            die(2, f"voice id not NFC: {vid!r}")
        out.append(v)
    if not out:
        die(2, "snapshot has 0 candidates")
    return out


def api_key() -> str:
    k = os.environ.get("VIENEU_API_KEY", "")
    if not k:
        die(2, "VIENEU_API_KEY not set")
    return k


def ffmpeg_bin() -> str:
    return os.environ.get("FFMPEG_PATH", "ffmpeg")


def read_text(lang: str) -> str:
    t = TEXTS[lang].read_text(encoding="utf-8").strip()
    if t != unicodedata.normalize("NFC", t):
        die(2, f"audition text not NFC: {TEXTS[lang]}")
    return t


def cmd_candidates(a) -> int:
    for v in candidates_list(load_snapshot(a.snapshot)):
        print(v["id"])
    return 0


def _mint_ids(key: dict, n: int) -> list:
    used = set(key)
    out = []
    while len(out) < n:
        sid = "s-" + secrets.token_hex(3)
        if sid not in used and sid not in out:
            out.append(sid)
    return out


def _open_vieneu(key: str, voice: str, text: str):
    body = json.dumps({
        "model": MODEL, "input": text, "voice": voice,
        "response_format": "pcm", "stream_format": "audio",
        "sample_rate": SAMPLE_RATE,
    }).encode("utf-8")
    req = urllib.request.Request(URL, data=body, method="POST", headers={
        "Content-Type": "application/json",
        "Authorization": f"Bearer {key}",
    })
    for attempt in range(MAX_429_RETRIES + 1):
        try:
            return _OPENER.open(req, timeout=300)
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt < MAX_429_RETRIES:
                try:
                    wait_s = min(float(e.headers.get("Retry-After") or 2), 5.0)
                except ValueError:
                    wait_s = 2.0
                time.sleep(wait_s)
                continue
            raise


def cmd_generate(a) -> int:
    key = api_key()
    langs = ["vi", "en"] if a.lang == "both" else [a.lang]
    texts = {l: read_text(l) for l in langs}
    snap = load_snapshot(a.snapshot)
    cands = candidates_list(snap)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    key_path = out / "key.json"
    key_map = {}
    if key_path.exists():
        key_map = json.loads(key_path.read_text(encoding="utf-8"))
    log_path = out / "generate.jsonl"
    failures = []
    bad_voice = []
    done = skipped = 0
    for lang in langs:
        for v in cands:
            vid = v["id"]
            existing = next((s for s, m in key_map.items()
                             if m.get("voice_id") == vid
                             and m.get("language") == lang), None)
            if existing and not a.force and (out / f"{existing}.mp3").exists():
                skipped += 1
                continue
            sid = existing or _mint_ids(key_map, 1)[0]
            t0 = time.monotonic()
            mp3 = out / f"{sid}.mp3"
            try:
                resp = _open_vieneu(key, vid, texts[lang])
            except urllib.error.HTTPError as e:
                if e.code == 400:
                    bad_voice.append(vid)
                failures.append((vid, lang, f"HTTP {e.code}"))
                continue
            except Exception as e:  # noqa: BLE001
                failures.append((vid, lang, type(e).__name__))
                continue
            if resp.headers.get("X-Sample-Rate") != str(SAMPLE_RATE):
                failures.append((vid, lang, "X-Sample-Rate != 48000"))
                continue
            ff = subprocess.Popen([
                ffmpeg_bin(), "-hide_banner", "-loglevel", "error",
                "-f", "s16le", "-ar", str(SAMPLE_RATE), "-ac", "1",
                "-i", "pipe:0", "-vn", "-c:a", "libmp3lame", "-b:a", "192k",
                "-write_xing", "0", "-id3v2_version", "0",
                "-flush_packets", "1", "-f", "mp3", str(mp3),
            ], stdin=subprocess.PIPE, stderr=subprocess.PIPE)
            pcm_bytes = 0
            try:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    pcm_bytes += len(chunk)
                    ff.stdin.write(chunk)
                ff.stdin.close()
            except Exception as e:  # noqa: BLE001
                ff.kill()
                ff.wait()
                mp3.unlink(missing_ok=True)
                failures.append((vid, lang, f"stream: {type(e).__name__}"))
                continue
            ff.wait()
            wall_ms = int((time.monotonic() - t0) * 1000)
            audio_s = round(pcm_bytes / 2 / SAMPLE_RATE, 3)
            ok = ff.returncode == 0 and pcm_bytes > 0 and mp3.exists() and mp3.stat().st_size > 0
            rec = {"sample_id": sid, "voice_id": vid, "language": lang,
                   "pcm_bytes": pcm_bytes, "audio_seconds": audio_s,
                   "wall_ms": wall_ms, "status": "ok" if ok else "failed"}
            if not ok:
                rec["ffmpeg_rc"] = ff.returncode
                tail = (ff.stderr.read() if ff.stderr else b"")[-4096:]
                if tail:
                    rec["ffmpeg_stderr_tail"] = tail.decode("utf-8", "replace")
                mp3.unlink(missing_ok=True)
                failures.append((vid, lang, "empty/ffmpeg"))
            with log_path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            if ok:
                key_map[sid] = {"voice_id": vid, "language": lang}
                key_path.write_text(json.dumps(key_map, ensure_ascii=False, indent=1),
                                    encoding="utf-8")
                done += 1
                print(f"{sid} {lang} {vid} {audio_s}s {wall_ms}ms", file=sys.stderr)
    print(f"generated={done} skipped={skipped} failed={len(failures)}")
    for vid, lang, why in failures:
        print(f"FAILED {lang} {vid}: {why}", file=sys.stderr)
    if bad_voice:
        print("unknown voices (re-take snapshot, 01.2.1-T3): "
              + ", ".join(sorted(set(bad_voice))), file=sys.stderr)
    return 1 if failures else 0


def cmd_sheet(a) -> int:
    out = Path(a.out)
    key_path = out / "key.json"
    if not key_path.exists():
        die(2, "key.json missing: run generate first")
    key_map = json.loads(key_path.read_text(encoding="utf-8"))
    by_lang = {}
    for sid, m in key_map.items():
        by_lang.setdefault(m["language"], []).append(sid)
    for lang, sids in by_lang.items():
        sids = sids[:]
        random.SystemRandom().shuffle(sids)
        with (out / f"sheet.{lang}.csv").open("w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=SHEET_FIELDS)
            w.writeheader()
            for sid in sids:
                w.writerow({"sample_id": sid})
        (out / f"playlist.{lang}.m3u").write_text(
            "".join(f"{sid}.mp3\n" for sid in sids), encoding="utf-8")
        print(f"{lang}: {len(sids)} rows -> sheet.{lang}.csv, playlist.{lang}.m3u")
    return 0


def _voice_meta(snapshot: dict) -> dict:
    meta = {}
    for v in snapshot.get("data", []):
        parts = [p.strip() for p in v.get("description", "").split("·")]
        gender = {"Nam": "male", "Nữ": "female"}.get(parts[0], "") if parts else ""
        region = {"Bắc": "northern", "Trung": "central", "Nam": "southern"
                  }.get(parts[1], "") if len(parts) > 1 else ""
        style_raw = parts[2] if len(parts) > 2 else ""
        s = style_raw.lower()
        # `style` (results.csv column) keeps the raw description word;
        # `style_tag` (D9 vocabulary) is empty for unmapped styles.
        style_tag = {"kể chuyện": "storyteller", "đọc truyện": "storyteller",
                     "tự nhiên": "natural", "giọng đọc tự nhiên": "natural",
                     "tin tức": "news"}.get(s, "")
        meta[v["id"]] = {"gender": gender, "region": region,
                         "style": style_raw, "style_tag": style_tag,
                         "featured": bool(v.get("featured"))}
    return meta


def cmd_unblind(a) -> int:
    key_map = json.loads(Path(a.key).read_text(encoding="utf-8"))
    meta = _voice_meta(load_snapshot(a.snapshot))
    rows = []
    bad = 0
    for sheet in a.sheets:
        with Path(sheet).open(newline="", encoding="utf-8") as f:
            for r in csv.DictReader(f):
                if not (r.get("naturalness") or "").strip():
                    continue  # unfilled template row
                sid = (r.get("sample_id") or "").strip()
                if sid not in key_map:
                    print(f"unblind: unknown sample_id {sid!r} in {sheet}", file=sys.stderr)
                    bad += 1
                    continue
                try:
                    nat, int_, fit = (int(r[c]) for c in
                                      ("naturalness", "intelligibility", "night_fit"))
                except (ValueError, TypeError):
                    print(f"unblind: non-integer score in {sheet} row {sid}", file=sys.stderr)
                    bad += 1
                    continue
                if not all(1 <= s <= 5 for s in (nat, int_, fit)):
                    print(f"unblind: score out of 1..5 in {sheet} row {sid}", file=sys.stderr)
                    bad += 1
                    continue
                m = key_map[sid]
                vm = meta.get(m["voice_id"], {})
                rtags = [t.strip() for t in r.get("rater_tags", "").split(";")
                         if t.strip()]
                off = [t for t in rtags if t not in RATER_TAGS]
                if off:
                    print(f"unblind: off-vocabulary tags dropped {off} "
                          f"({sheet} row {sid})", file=sys.stderr)
                rows.append({"language": m["language"], "sample_id": sid,
                             "voice_id": m["voice_id"], "gender": vm.get("gender", ""),
                             "region": vm.get("region", ""), "style": vm.get("style", ""),
                             "rater": r.get("rater", "").strip(),
                             "naturalness": nat, "intelligibility": int_,
                             "night_fit": fit,
                             "mispronunciations": r.get("mispronunciations", "").strip(),
                             "rater_tags": ";".join(t for t in rtags if t in RATER_TAGS),
                             "notes": r.get("notes", "").strip()})
    with Path(a.out).open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=RESULT_FIELDS)
        w.writeheader()
        w.writerows(rows)
    print(f"wrote {len(rows)} rows -> {a.out} (rejected {bad})")
    return 0


def cmd_summarize(a) -> int:
    with Path(a.results).open(newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    meta = _voice_meta(load_snapshot(a.snapshot))
    groups = {}
    raters = {}
    for r in rows:
        g = groups.setdefault((r["language"], r["voice_id"]), {"n": [], "i": [], "f": [],
                                                              "tags": {}, "mis": set()})
        g["n"].append(int(r["naturalness"]))
        g["i"].append(int(r["intelligibility"]))
        g["f"].append(int(r["night_fit"]))
        if r["mispronunciations"]:
            g["mis"].update(w.strip() for w in r["mispronunciations"].split(";") if w.strip())
        for t in (t.strip() for t in r["rater_tags"].split(";") if t.strip()):
            g["tags"][t] = g["tags"].get(t, 0) + 1
        raters.setdefault(r["language"], set()).add(r["rater"])
    by_lang = {}
    for (lang, vid), g in groups.items():
        k = len(g["n"])
        mn = sum(g["n"]) / k
        mi = sum(g["i"]) / k
        mf = sum(g["f"]) / k
        kept = [t for t, c in sorted(g["tags"].items(), key=lambda kv: (-kv[1], kv[0]))
                if c >= (len(raters.get(lang, ())) or 1) / 2][:2]
        vm = meta.get(vid, {})
        tags = ([vm.get("style_tag", "")] + ([vm.get("region", "")] if lang == "vi" else [])
                + kept)
        tags = [t for t in tags if t][:4]
        by_lang.setdefault(lang, []).append({
            "voice_id": vid, "raters": k, "nat": mn, "int": mi, "fit": mf,
            "mis": len(g["mis"]), "pass": mn >= 3.5 and mi >= 4.0,
            "featured": vm.get("featured", False), "gender": vm.get("gender", ""),
            "tags": tags,
        })
    for lang, items in sorted(by_lang.items()):
        items.sort(key=lambda x: (-x["fit"], -x["nat"], -x["int"],
                                  x["mis"], not x["featured"], x["voice_id"]))
        print(f"\n### {lang} ({len(items)} voices)\n")
        print("| voice | raters | nat | int | fit | mis | pass | tags |")
        print("|---|---|---|---|---|---|---|---|")
        for it in items:
            print(f"| {it['voice_id']} | {it['raters']} | {it['nat']:.2f} | "
                  f"{it['int']:.2f} | {it['fit']:.2f} | {it['mis']} | "
                  f"{'✅' if it['pass'] else '❌'} | {', '.join(it['tags'])} |")

    # Suggested selection (task §Selection rules 1–5; informational — the
    # owner and raters confirm; this table is not the final selection).
    passing = {lang: [it for it in items if it["pass"]]
               for lang, items in by_lang.items()}
    assigned = {lang: {it["voice_id"]: it for it in items}
                for lang, items in passing.items()}
    both = [vid for vid in (assigned.get("vi") or {})
            if vid in (assigned.get("en") or {})]
    for vid in both:
        rank = {lang: [it["voice_id"] for it in items].index(vid)
                for lang, items in passing.items()}
        if len(passing.get("en", ())) <= 3:
            keep = "en"          # en is the scarce language (F04)
        elif len(passing.get("vi", ())) <= 3:
            keep = "vi"          # moving it would leave vi < 3
        else:
            keep = min(rank, key=rank.get)
        drop = "en" if keep == "vi" else "vi"
        del assigned[drop][vid]
    print("\n### Suggested selection (unconfirmed)\n")
    print("| language | voice | gender | nat | int | fit | tags |")
    print("|---|---|---|---|---|---|---|")
    for lang in ("vi", "en"):
        sel = list((assigned.get(lang) or {}).values())
        sel.sort(key=lambda x: (-x["fit"], -x["nat"], -x["int"],
                                x["mis"], not x["featured"], x["voice_id"]))
        sel = sel[:4]
        for want in ("female", "male"):
            if sel and not any(it["gender"] == want for it in sel):
                pool = [it for it in (assigned.get(lang) or {}).values()
                        if it["gender"] == want and it not in sel]
                if pool:
                    pool.sort(key=lambda x: (-x["fit"], -x["nat"], -x["int"],
                                             x["mis"], x["voice_id"]))
                    sel[-1] = pool[0]
        for it in sel:
            print(f"| {lang} | {it['voice_id']} | {it['gender']} | "
                  f"{it['nat']:.2f} | {it['int']:.2f} | {it['fit']:.2f} | "
                  f"{', '.join(it['tags'])} |")
        if len(sel) < 3:
            print(f"\n> ⚠️ only {len(sel)} passing {lang} voice(s) — "
                  f"owner decision required (O2 path).")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(prog="audition.py")
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("candidates")
    c.add_argument("--snapshot", default=str(SNAPSHOT))
    g = sub.add_parser("generate")
    g.add_argument("--snapshot", default=str(SNAPSHOT))
    g.add_argument("--lang", choices=["vi", "en", "both"], default="both")
    g.add_argument("--out", required=True)
    g.add_argument("--force", action="store_true")
    s = sub.add_parser("sheet")
    s.add_argument("--out", required=True)
    u = sub.add_parser("unblind")
    u.add_argument("--key", required=True)
    u.add_argument("--sheets", nargs="+", required=True)
    u.add_argument("--snapshot", default=str(SNAPSHOT))
    u.add_argument("--out", required=True)
    z = sub.add_parser("summarize")
    z.add_argument("--results", required=True)
    z.add_argument("--snapshot", default=str(SNAPSHOT))
    a = p.parse_args()
    return {"candidates": cmd_candidates, "generate": cmd_generate,
            "sheet": cmd_sheet, "unblind": cmd_unblind,
            "summarize": cmd_summarize}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
