#!/usr/bin/env python3
"""Blind 9:16 crop A/B groups for 03.3.2-T1 phase B (05 O9 option A).

For each cell it writes four images in the rater's `<cell>-<A|B|C|D>.png` layout:
  A = no-hint cropped to 9:16, B = hint cropped to 9:16 (both exactly 1080x1920),
  C = no-hint full frame,  D = hint full frame (original aspect, uncropped).
The crop is MPT `cover`: scale so the image fills 1080x1920, then centre-crop
(owner decision 05 O9; `video_fit_mode` default, mpt/app/services/video.py).

RUN is either a run NAME, resolved as `$NTS_HOME/runs/<name>`, or an existing
run directory (absolute or relative, kept for backwards compatibility). A run
holds `img/<cell>-<suffix>.png`. Every source must resolve inside `<run>/img`
and every destination inside `<out>/blind`; a symlinked source image or `blind/`
is refused before anything is written.

`--out` is rebuilt from scratch: the tool deletes only its own artefacts
(`blind/<cell>-<landscape|portrait>-<A|B|C|D>.png`, contact-sheet-key.json,
provenance.json), so the key and the provenance describe exactly this
invocation. A foreign file in `blind/` stops it (exit 2) instead of being
deleted.

Usage: make_crop_groups.py --no-hint RUN --hint RUN --cells k05-portrait k10-portrait --out DIR
Exit codes (01 D9): 0 ok · 1 crop/build failure · 2 usage or environment error.
"""
import argparse
import contextlib
import io
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
CELL_RE = re.compile(r"^k\d{2}-(landscape|portrait)$")
SUFFIX_RE = re.compile(r"^[a-z0-9-]+$")
# The artefacts this tool owns in --out; every other entry in blind/ is foreign.
OWNED_PNG_RE = re.compile(r"^k\d{2}-(landscape|portrait)-[A-D]\.png$")
OWNED_FILES = ("contact-sheet-key.json", "provenance.json")
PATTERN_RUN = 8  # identical source columns per step in the self-test pattern


class UsageError(Exception):
    """Bad invocation or an unsafe path (exit 2, 01 D9)."""


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


def _inside(path, parent, what):
    """Resolve `path` and require it under `parent` (both symlink-resolved)."""
    resolved, base = Path(path).resolve(), Path(parent).resolve()
    if resolved != base and base not in resolved.parents:
        raise UsageError(f"{what} {path} resolves outside {parent} ({resolved})")
    return resolved


def resolve_run(value, env=None):
    """Run NAME -> `$NTS_HOME/runs/<name>`; an existing directory is used as is."""
    env = os.environ if env is None else env
    value = value or ""
    if not value:
        raise UsageError("empty run (want a run NAME or an existing run folder)")
    if Path(value).is_dir():
        return Path(value)
    if "/" in value or ".." in Path(value).parts:
        raise UsageError(f"{value!r} is not an existing run folder and not a run name "
                         "(a run name has no '/', no '..')")
    home = (env.get("NTS_HOME") or "").strip()
    if not home:
        raise UsageError(f"run name {value!r} needs $NTS_HOME (set it, or pass an existing run folder)")
    run = Path(os.path.expanduser(home)) / "runs" / value
    if not run.is_dir():
        raise UsageError(f"run {value!r} not found: {run} (or pass an existing run folder)")
    return run


def source_path(run, cell, suffix):
    """`<run>/img/<cell>-<suffix>.png`, refused when symlinked or outside <run>/img."""
    img = Path(run) / "img"
    src = img / f"{cell}-{suffix}.png"
    if src.is_symlink():
        raise UsageError(f"{cell}: symlinked source image refused: {src}")
    _inside(src, img, "source image")
    if not src.is_file():
        raise FileNotFoundError(f"{cell}: missing source {src}")
    return src


def blind_dir(out):
    """`<out>/blind`, refused when symlinked or resolving outside <out>."""
    blind = Path(out) / "blind"
    if blind.is_symlink():
        raise UsageError(f"symlinked output folder refused: {blind}")
    _inside(blind, out, "blind folder")
    return blind


def reset_out(out, blind):
    """Delete only this tool's artefacts; a foreign entry in blind/ is a refusal."""
    if blind.is_dir():
        foreign = sorted(p.name for p in blind.iterdir()
                         if not (p.is_file() and OWNED_PNG_RE.match(p.name)))
        if foreign:
            raise UsageError(f"{blind}: refusing to delete foreign file(s): {', '.join(foreign)}")
        for p in blind.iterdir():
            p.unlink()
    for name in OWNED_FILES:
        stale = Path(out) / name
        if stale.is_file() or stale.is_symlink():
            stale.unlink()
    blind.mkdir(parents=True, exist_ok=True)


def build_cell(cell, sources, blind, provenance):
    """One cell's A-D files; `sources` maps no_hint/hint -> validated source path."""
    rows = {}
    for letter, (side, mode) in SPEC.items():
        src = Path(sources[side])
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
    blind = blind_dir(out)
    plan = []
    for cell in cells:
        sources = {"no_hint": source_path(no_hint, cell, suffix),
                   "hint": source_path(hint, cell, suffix)}
        for letter in SPEC:
            _inside(blind / f"{cell}-{letter}.png", blind, "output image")
        plan.append((cell, sources))
    reset_out(out, blind)
    key, provenance = {}, []
    for cell, sources in plan:
        key[cell] = build_cell(cell, sources, blind, provenance)
    (Path(out) / "contact-sheet-key.json").write_text(
        json.dumps(dict(sorted(key.items())), indent=2) + "\n", encoding="utf-8")
    (Path(out) / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    for p in provenance:
        kept = "" if p["kept_width"] is None else f" kept_width={p['kept_width']:.3f}"
        print(f"{p['cell']}-{p['letter']} {p['label']:<12} {p['source_size'][0]}x{p['source_size'][1]}"
              f" -> {p['out_size'][0]}x{p['out_size'][1]}{kept}")
    print(f"{len(provenance)} images in {blind}")
    return 0


def pattern(x, run=PATTERN_RUN):
    """Self-test source pattern: `run` identical columns of a step value. Pure."""
    return ((x // run) * 7) % 256


def png_stub(path, w, h, run=1):
    """Minimal greyscale PNG (stdlib); the crop self-test needs a real file for sips."""
    rows = b"".join(b"\x00" + bytes(pattern(x, run) for x in range(w)) for _ in range(h))
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


def png_columns(path, xs):
    """Channel-0 values of columns `xs`: one list per column (self-test only)."""
    data = Path(path).read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path}: not a PNG")
    pos, idat, ihdr = 8, b"", None
    while pos < len(data):
        (size,) = struct.unpack(">I", data[pos:pos + 4])
        tag, body = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + size]
        pos += 12 + size
        if tag == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
    if ihdr is None or ihdr[2] != 8 or ihdr[6] != 0 or ihdr[3] not in (0, 2, 4, 6):
        raise ValueError(f"{path}: unsupported PNG header {ihdr}")
    width, height, _, ctype = ihdr[0], ihdr[1], ihdr[2], ihdr[3]
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[ctype]
    raw, stride = zlib.decompress(idat), width * channels
    pos, prev, lines = 0, bytearray(stride), []
    for _ in range(height):
        filt, line = raw[pos], bytearray(raw[pos + 1:pos + 1 + stride])
        pos += 1 + stride
        for i in range(stride):
            left = line[i - channels] if i >= channels else 0
            up = prev[i]
            upleft = prev[i - channels] if i >= channels else 0
            if filt == 1:
                line[i] = (line[i] + left) & 0xFF
            elif filt == 2:
                line[i] = (line[i] + up) & 0xFF
            elif filt == 3:
                line[i] = (line[i] + (left + up) // 2) & 0xFF
            elif filt == 4:
                p = left + up - upleft
                pa, pb, pc = abs(p - left), abs(p - up), abs(p - upleft)
                pred = left if (pa <= pb and pa <= pc) else (up if pb <= pc else upleft)
                line[i] = (line[i] + pred) & 0xFF
        lines.append(line)
        prev = line
    return [[line[x * channels] for line in lines] for x in xs]


def crop_edge_source_columns(sw, sh):
    """Source columns shown at the crop's left/right edge, for `cover` centre-crop. Pure.

    A width-bound source keeps its whole width; otherwise the scaled width is
    `round(sw * TARGET_H / sh)` and the window starts at (excess / 2), a half
    column when the excess is odd — hence the +-1 tolerance in the caller.
    """
    if fill_dim(sw, sh)[0] == "width":
        k = TARGET_W / sw
    else:
        k = TARGET_H / sh
    start = (round(sw * k) - TARGET_W) / 2
    return [(start + i + 0.5) / k for i in (0, TARGET_W - 1)]


def self_test():
    fails = []

    def check(name, ok):
        print(f"{'ok  ' if ok else 'FAIL'} {name}")
        if not ok:
            fails.append(name)

    def call_main(args):
        """main() with stderr captured (self-test only)."""
        buf = io.StringIO()
        with contextlib.redirect_stderr(buf):
            rc = main(args)
        return rc, buf.getvalue()

    def quiet(fn, *args):
        with contextlib.redirect_stdout(io.StringIO()):
            return fn(*args)

    def edge_columns_match(path, expected, tol=1):
        """Both edge columns uniform top-to-bottom and within `tol` of `expected`."""
        left, right = png_columns(path, [0, TARGET_W - 1])
        return (len(set(left)) == 1 and len(set(right)) == 1
                and all(abs(col[0] - want) <= tol for col, want in zip((left, right), expected)))

    def raised(fn, *args):
        """Message of the UsageError `fn(*args)` raises, else None."""
        try:
            fn(*args)
        except UsageError as exc:
            return str(exc)
        return None

    check("labels_match_spec", set(LABELS) == set(SPEC) == set("ABCD")
          and all(LABELS[l] == SPEC[l][0].replace("_", "-") + "-" + SPEC[l][1] for l in "ABCD"))
    check("A_B_are_crops_C_D_are_full", [SPEC[l][1] for l in "ABCD"] == ["crop", "crop", "full", "full"])
    check("landscape_fills_by_height", fill_dim(1370, 1148) == ("height", 1920)
          and fill_dim(1536, 1024) == ("height", 1920) and fill_dim(1774, 887) == ("height", 1920))
    check("tall_fills_by_width", fill_dim(900, 2000) == ("width", 1080)
          and fill_dim(400, 1600) == ("width", 1080))
    check("odd_excess_fills_by_height", fill_dim(480, 840) == ("height", 1920))
    box = cover_box(1370, 1148)
    check("cover_box_1370x1148", box["scaled"] == (2291, 1920) and box["box"] == (605, 0, 1080, 1920)
          and abs(box["kept_width"] - 0.4714) < 0.0001)
    check("cover_box_1536x1024", cover_box(1536, 1024)["kept_width"] == round(1080 / 2880, 4))
    check("cover_box_1097_excess_is_odd", cover_box(480, 840) == {"scaled": (1097, 1920),
                                                                 "box": (8, 0, 1080, 1920),
                                                                 "kept_width": round(1080 / 1097, 4)})
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
    outs = []
    # (a) tall source: `--resampleWidth` path, crop keeps the full width.
    # (b) odd-excess source: scaled width 1097 -> centre offset 8.5, a half column.
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        for name, (sw, sh) in (("tall", (400, 1600)), ("odd_excess", (480, 840))):
            src = td / f"{name}.png"
            png_stub(src, sw, sh, run=PATTERN_RUN)
            dst = td / f"{name}-crop.png"
            info = crop_cover(src, dst)
            scaled = cover_box(sw, sh)["scaled"]
            want = [pattern(int(x)) for x in crop_edge_source_columns(sw, sh)]
            outs.append((name, info))
            check(f"{name}_crop_is_1080x1920_at_scaled_{scaled[0]}x{scaled[1]}",
                  pixel_size(dst) == (TARGET_W, TARGET_H) and info["scaled"] == scaled
                  and info["kept_width"] == round(TARGET_W / scaled[0], 4))
            check(f"{name}_crop_edges_are_the_centred_slice",
                  edge_columns_match(dst, want))
    check("tall_crop_keeps_full_width", outs[0][1]["kept_width"] == 1.0)

    with tempfile.TemporaryDirectory() as td:
        home = Path(td) / "home"
        run = home / "runs" / "contact-sheet-x"
        (run / "img").mkdir(parents=True)
        env = {"NTS_HOME": str(home)}
        check("run_name_resolves_under_nts_home", resolve_run("contact-sheet-x", env) == run)
        check("run_existing_folder_used_as_is", resolve_run(str(run), env) == run)
        refused = [bad for bad in ("", "../x", "a/b", "x/..")
                   if raised(resolve_run, bad, env) is not None]
        check("bad_run_names_refused", refused == ["", "../x", "a/b", "x/.."])
        no_home = raised(resolve_run, "contact-sheet-x", {}) or ""
        check("bare_name_without_nts_home", "NTS_HOME" in no_home)
        not_found = raised(resolve_run, "nope", env) or ""
        check("unknown_run_name_refused", "nope" in not_found and "runs/nope" in not_found)

    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        run = td / "home" / "runs" / "contact-sheet-x"
        (run / "img").mkdir(parents=True)
        png_stub(run / "img" / "k05-portrait-auto.png", 200, 100)
        out = td / "out"
        (out / "blind").mkdir(parents=True)
        stale = out / "blind" / "k09-portrait-C.png"
        stale.write_bytes(b"stale")
        key_file = out / "contact-sheet-key.json"
        key_file.write_text('{"k09-portrait": {"A": "old"}}\n', encoding="utf-8")
        (out / "provenance.json").write_text("[]\n", encoding="utf-8")
        foreign = out / "blind" / "notes.txt"
        foreign.write_text("keep me", encoding="utf-8")
        named = raised(quiet, build, run, run, ["k05-portrait"], out, "auto") or ""
        check("foreign_file_refused_by_name", "notes.txt" in named)
        check("foreign_refusal_changes_nothing",
              foreign.read_text(encoding="utf-8") == "keep me" and stale.read_bytes() == b"stale"
              and key_file.read_text(encoding="utf-8") == '{"k09-portrait": {"A": "old"}}\n')
        foreign.unlink()
        quiet(build, run, run, ["k05-portrait"], out, "auto")
        key = json.loads(key_file.read_text(encoding="utf-8"))
        prov = json.loads((out / "provenance.json").read_text(encoding="utf-8"))
        check("clean_rebuild_replaces_own_artefacts",
              sorted(p.name for p in (out / "blind").iterdir())
              == [f"k05-portrait-{l}.png" for l in "ABCD"] and list(key) == ["k05-portrait"]
              and key["k05-portrait"] == LABELS and len(prov) == 4)

    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        run = td / "run"
        (run / "img").mkdir(parents=True)
        real = td / "real.png"
        png_stub(real, 200, 100)
        link = run / "img" / "k05-portrait-auto.png"
        os.symlink(real, link)
        check("symlinked_source_refused", "symlink" in (raised(source_path, run, "k05-portrait", "auto") or ""))
        link.unlink()
        png_stub(link, 200, 100)
        out = td / "out"
        (td / "elsewhere").mkdir()
        out.mkdir()
        os.symlink(td / "elsewhere", out / "blind")
        check("symlinked_blind_refused",
              "symlink" in (raised(quiet, build, run, run, ["k05-portrait"], out, "auto") or "")
              and not list((td / "elsewhere").iterdir()))
        check("path_containment_enforced",
              "resolves outside" in (raised(_inside, td / "outside.png", run / "img", "source image") or ""))

    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        out = td / "out"
        saved = os.environ.pop("NTS_HOME", None)

        def run_cli(cells, suffix="auto", home=None):
            if home is None:
                os.environ.pop("NTS_HOME", None)
            else:
                os.environ["NTS_HOME"] = str(home)
            try:
                return call_main(["--no-hint", "contact-sheet-x", "--hint", "contact-sheet-x",
                                  "--cells", *cells, "--out", str(out), "--suffix", suffix])
            finally:
                if saved is None:
                    os.environ.pop("NTS_HOME", None)
                else:
                    os.environ["NTS_HOME"] = saved

        try:
            cell_rc, cell_err = run_cli(["../x"])
            path_cell_rc, path_cell_err = run_cli(["k05-portrait/../../y"])
            bad_suffix_rc, bad_suffix_err = run_cli(["k05-portrait"], suffix="Bad/Suffix")
            no_home_rc, no_home_err = run_cli(["k05-portrait"])
            check("bad_cell_exit_2", cell_rc == 2 and cell_err.startswith("error:"))
            check("cell_with_path_exit_2", path_cell_rc == 2 and path_cell_err.startswith("error:"))
            check("bad_suffix_exit_2", bad_suffix_rc == 2 and bad_suffix_err.startswith("error:"))
            check("missing_nts_home_exit_2", no_home_rc == 2 and "NTS_HOME" in no_home_err)
            check("nothing_written_outside_out", not out.exists() and not (td / "x").exists()
                  and not (td / "y").exists())
        finally:
            if saved is not None:
                os.environ["NTS_HOME"] = saved
    print(f"self-test {'FAILED' if fails else 'ok'} ({len(fails)} failed)")
    return 1 if fails else 0


def main(argv):
    p = argparse.ArgumentParser(description="Blind 9:16 crop A/B groups (03.3.2-T1 phase B).")
    p.add_argument("--no-hint", help="no-hint run: NAME under $NTS_HOME/runs, or a run folder path")
    p.add_argument("--hint", help="portrait-hint run: NAME under $NTS_HOME/runs, or a run folder path")
    p.add_argument("--cells", nargs="+", default=[], help="cell ids, e.g. k05-portrait")
    p.add_argument("--suffix", default="auto", help="image variant suffix (default auto)")
    p.add_argument("--out", help="output folder: blind/<cell>-<letter>.png + contact-sheet-key.json")
    p.add_argument("--self-test", action="store_true")
    args = p.parse_args(argv)
    if args.self_test:
        return self_test()
    if not (args.no_hint and args.hint and args.cells and args.out):
        print("usage: --no-hint RUN --hint RUN --cells CELL... --out DIR (or --self-test)", file=sys.stderr)
        return 2
    try:
        bad = [cell for cell in args.cells if not CELL_RE.match(cell)]
        if bad:
            raise UsageError(f"bad --cells value(s): {', '.join(bad)} (want k<NN>-<landscape|portrait>)")
        if not SUFFIX_RE.match(args.suffix):
            raise UsageError(f"bad --suffix {args.suffix!r} (want [a-z0-9-]+)")
        return build(resolve_run(args.no_hint), resolve_run(args.hint), args.cells, args.out, args.suffix)
    except UsageError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    except subprocess.CalledProcessError as exc:
        detail = next((line.strip() for line in (exc.stderr or b"").decode(errors="replace").splitlines()
                       if line.strip()), "")
        print(f"error: {exc.cmd[0]} failed (exit {exc.returncode})" + (f": {detail}" if detail else ""),
              file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(f"error: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
