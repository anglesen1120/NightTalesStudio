#!/usr/bin/env python3
"""Blind 9:16 crop A/B groups for 03.3.2-T1 phase B (05 O9 option A).

For each cell it writes four images in the rater's `<cell>-<A|B|C|D>.png` layout:
  A = no-hint cropped to 9:16, B = hint cropped to 9:16 (both exactly 1080x1920),
  C = no-hint full frame,  D = hint full frame (original aspect, uncropped).
The crop is MPT `cover`: scale so the image fills 1080x1920, then centre-crop
(owner decision 05 O9; `video_fit_mode` default, mpt/app/services/video.py).
Usage: make_crop_groups.py --no-hint RUN --hint RUN --cells k05-portrait k10-portrait --out DIR
"""
import argparse
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

TARGET_W, TARGET_H = 1080, 1920
LABELS = {"A": "no-hint-crop", "B": "hint-crop", "C": "no-hint-full", "D": "hint-full"}
SPEC = {"A": ("no_hint", "crop"), "B": ("hint", "crop"), "C": ("no_hint", "full"), "D": ("hint", "full")}
SIPS_ENV = {"PATH": "/usr/bin:/bin"}


def fill_dim(sw, sh, tw=TARGET_W, th=TARGET_H):
    """`cover` scales by height unless a tall source would leave the width short. Pure."""
    return ("height", th) if sw * th >= sh * tw else ("width", tw)


def cover_box(sw, sh, tw=TARGET_W, th=TARGET_H):
    """`cover`: scaled size then the centred crop box of tw x th. Pure."""
    k = max(tw / sw, th / sh)
    w, h = round(sw * k), round(sh * k)
    return {"scaled": (w, h), "box": ((w - tw) // 2, (h - th) // 2, tw, th), "kept_width": round(tw / w, 4)}


def _sips(*args):
    return subprocess.run(["sips", *args], check=True, capture_output=True, env=SIPS_ENV)


def pixel_size(path):
    out = _sips("-g", "pixelWidth", "-g", "pixelHeight", str(path)).stdout.decode()
    w = re.search(r"pixelWidth:\s*(\d+)", out)
    h = re.search(r"pixelHeight:\s*(\d+)", out)
    if not (w and h):
        raise ValueError(f"{path}: sips printed no pixel size")
    return int(w.group(1)), int(h.group(1))


def crop_cover(src, dst):
    """sips resample + centre-crop; returns the measured pre-crop `scaled` size and kept width."""
    with tempfile.TemporaryDirectory() as td:
        mid = Path(td) / ("mid" + src.suffix)
        flag = "--resampleHeight" if fill_dim(*pixel_size(src))[0] == "height" else "--resampleWidth"
        _sips(flag, str(TARGET_H if flag == "--resampleHeight" else TARGET_W), str(src), "--out", str(mid))
        sw, sh = pixel_size(mid)
        _sips("--cropToHeightWidth", str(TARGET_H), str(TARGET_W), str(mid), "--out", str(dst))
    w, h = pixel_size(dst)
    if (w, h) != (TARGET_W, TARGET_H):
        raise ValueError(f"{dst}: crop is {w}x{h}, expected {TARGET_W}x{TARGET_H}")
    return {"scaled": (sw, sh), "kept_width": round(TARGET_W / sw, 4)}


def build_cell(cell, sources, blind, provenance):
    """One cell's A-D files; `sources` maps no_hint/hint -> source path."""
    rows = {}
    for letter, (side, mode) in SPEC.items():
        src = Path(sources[side])
        if not src.is_file():
            raise FileNotFoundError(f"{cell}: missing source {src}")
        dst = blind / f"{cell}-{letter}.png"
        if mode == "crop":
            info = crop_cover(src, dst)
        else:
            shutil.copyfile(src, dst)
            w, h = pixel_size(dst)
            info = {"scaled": (w, h), "kept_width": None}
        provenance.append({"cell": cell, "letter": letter, "label": LABELS[letter], "mode": mode,
                           "source": str(src), "source_size": list(pixel_size(src)),
                           "out": dst.name, "out_size": list(pixel_size(dst)), **info})
        rows[letter] = LABELS[letter]
    return rows


def build(no_hint, hint, cells, out, suffix):
    blind = Path(out) / "blind"
    blind.mkdir(parents=True, exist_ok=True)
    key_path = Path(out) / "contact-sheet-key.json"
    key = json.loads(key_path.read_text(encoding="utf-8")) if key_path.is_file() else {}
    provenance = []
    for cell in cells:
        sources = {"no_hint": Path(no_hint) / "img" / f"{cell}-{suffix}.png",
                   "hint": Path(hint) / "img" / f"{cell}-{suffix}.png"}
        key[cell] = build_cell(cell, sources, blind, provenance)
    key_path.write_text(json.dumps(dict(sorted(key.items())), indent=2) + "\n", encoding="utf-8")
    (Path(out) / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    for p in provenance:
        kept = "" if p["kept_width"] is None else f" kept_width={p['kept_width']:.3f}"
        print(f"{p['cell']}-{p['letter']} {p['label']:<12} {p['source_size'][0]}x{p['source_size'][1]}"
              f" -> {p['out_size'][0]}x{p['out_size'][1]}{kept}")
    print(f"{len(provenance)} images in {blind}")
    return 0


def png_stub(path, w, h):
    """Minimal greyscale PNG (stdlib); the crop self-test needs a real file for sips."""
    rows = b"".join(b"\x00" + bytes((x * 7) % 256 for x in range(w)) for _ in range(h))
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


def self_test():
    fails = []

    def check(name, ok):
        print(f"{'ok  ' if ok else 'FAIL'} {name}")
        if not ok:
            fails.append(name)

    check("labels_match_spec", set(LABELS) == set(SPEC) == set("ABCD")
          and all(LABELS[l] == SPEC[l][0].replace("_", "-") + "-" + SPEC[l][1] for l in "ABCD"))
    check("A_B_are_crops_C_D_are_full", [SPEC[l][1] for l in "ABCD"] == ["crop", "crop", "full", "full"])
    check("landscape_fills_by_height", fill_dim(1370, 1148) == ("height", 1920)
          and fill_dim(1536, 1024) == ("height", 1920) and fill_dim(1774, 887) == ("height", 1920))
    check("tall_fills_by_width", fill_dim(900, 2000) == ("width", 1080))
    box = cover_box(1370, 1148)
    check("cover_box_1370x1148", box["scaled"] == (2291, 1920) and box["box"] == (605, 0, 1080, 1920)
          and abs(box["kept_width"] - 0.4714) < 0.0001)
    check("cover_box_1536x1024", cover_box(1536, 1024)["kept_width"] == round(1080 / 2880, 4))
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        src = td / "stub.png"
        png_stub(src, 200, 100)
        check("stub_roundtrip", pixel_size(src) == (200, 100))
        dst = td / "out.png"
        info = crop_cover(src, dst)
        check("crop_is_1080x1920", pixel_size(dst) == (1080, 1920))
        check("crop_measured_scaled", info["scaled"] == (3840, 1920) and info["kept_width"] == 0.2812)
        blind = td / "blind"
        blind.mkdir()
        prov = []
        srcs = {"no_hint": src, "hint": src}
        rows = build_cell("k05-portrait", srcs, blind, prov)
        sizes = {(p["mode"], tuple(p["out_size"])) for p in prov}
        check("group_files_and_key_row",
              sorted(p.name for p in blind.iterdir()) == [f"k05-portrait-{l}.png" for l in "ABCD"]
              and rows == LABELS and len(prov) == 4 and sizes == {("crop", (1080, 1920)), ("full", (200, 100))}
              and len({p["source"] for p in prov}) == 1)
    print(f"self-test {'FAILED' if fails else 'ok'} ({len(fails)} failed)")
    return 1 if fails else 0


def main(argv):
    p = argparse.ArgumentParser(description="Blind 9:16 crop A/B groups (03.3.2-T1 phase B).")
    p.add_argument("--no-hint", help="run folder of the no-hint pass (has img/)")
    p.add_argument("--hint", help="run folder of the portrait-hint pass (has img/)")
    p.add_argument("--cells", nargs="+", default=[], help="cell ids, e.g. k05-portrait")
    p.add_argument("--suffix", default="auto", help="image variant suffix (default auto)")
    p.add_argument("--out", help="output folder: blind/<cell>-<letter>.png + contact-sheet-key.json")
    p.add_argument("--self-test", action="store_true")
    args = p.parse_args(argv)
    if args.self_test:
        return self_test()
    if not (args.no_hint and args.hint and args.cells and args.out):
        print("usage: --no-hint DIR --hint DIR --cells CELL... --out DIR (or --self-test)", file=sys.stderr)
        return 2
    try:
        return build(args.no_hint, args.hint, args.cells, args.out, args.suffix)
    except (OSError, ValueError) as exc:
        print(f"error: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
