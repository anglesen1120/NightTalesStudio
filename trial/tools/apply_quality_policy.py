#!/usr/bin/env python3
"""apply_quality_policy.py — stdlib mirror of 05 README D10 for the M0 trial (05.5.2-T1).

Usage
    uv run --no-project --python 3.12 trial/tools/apply_quality_policy.py \\
        --check FILE --aspect landscape|portrait (--subtitles-enabled|--subtitles-disabled) \\
        [--music-selected] [--out FILE]
    uv run --no-project --python 3.12 trial/tools/apply_quality_policy.py --self-test \\
        [--fixtures DIR]

Turns a measured `render-check.v1` (worker-node/checks/render_check.py, D9) into the
`render-quality.v1` verdict D10 defines: the nine blocking checks in D10's order, the
advisory hints, and the top-level names/types of the D14 fixture
`worker-node/contract/fixtures/render-quality.v1.pass.json`. The verdict is printed on
stdout as one JSON object and, with `--out`, also written there; diagnostics go to
stderr. Exit 0 = verdict written (pass or fail, the verdict is not the exit code) ·
1 = `--check` unreadable, larger than 1 MiB, or not `render-check.v1` · 2 = usage/environment.
`--self-test` prints its human-readable case report to stdout and exits 0 when every
case passes, 1 when a fixture is unreadable or any assertion fails.

This is a **manual M0 mirror only**: the C# `RenderQualityEvaluator` (05.5.1-T2) is
normative and the fixtures are frozen (D14). Where D10 leaves a detail open this mirror
follows the fixture and `--self-test` reports every D10/fixture disagreement it finds
(it exits 0 on the disagreements, and never edits a fixture):

1. `duration` value precision: the drift ratio is rounded to 4 decimals (fixture
   `0.0024`); `sync_suspect_share` to 3 (fixture `0.009`). D10 names neither.
2. `container_valid`/`streams`/`resolution`/`subtitle_text` report `value = 1`,
   `threshold = 1` (a pass/fail encoding) in the fixture, while 05.5.1-T2's table
   describes `errors.Count`, the codec pair, `"<w>x<h>"` and `text_matches_script`.
   This mirror follows the fixture the C# DTOs round-trip.
3. `loudness` reports `threshold = MaxLufs` (`-14.0`) in the fixture although D10's
   check is the inclusive band `[-16, -14]`.
4. `render-quality.v1.fail-loudness.json` omits the `loudnorm_dynamic` hint that its
   own source `render-check.v1.loud.json` implies.

Policy thresholds and check ids/order are D10's exactly; a missing check or a null
`text_matches_script` (with subtitles enabled) fails closed.
"""

import argparse
import contextlib
import copy
import io
import json
import math
import os
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

SCHEMA_CHECK = "render-check.v1"
SCHEMA = "render-quality.v1"
RESOLUTION = {"landscape": (1920, 1080), "portrait": (1080, 1920)}
# Fixture-pinned precisions (disagreement 1 in the module docstring).
DURATION_DRIFT_DIGITS = 4
SYNC_SUSPECT_SHARE_DIGITS = 3
CHECK_IDS = ("container_valid", "streams", "resolution", "duration", "loudness",
             "true_peak", "silence", "subtitles_present", "subtitle_text")
HINT_IDS = ("subtitle_sync_suspect", "subtitle_cues_long", "loudnorm_dynamic", "music_none")
PASS_FIXTURE = "render-quality.v1.pass.json"
FAIL_LOUDNESS_FIXTURE = "render-quality.v1.fail-loudness.json"
# A render-check.v1 of this size is already suspicious; a bigger `--check` is refused
# before `json.load` (bounded read, so an unbounded file never enters memory).
MAX_CHECK_BYTES = 1024 * 1024
CHECK_FIXTURES = ("render-check.v1.ok.json", "render-check.v1.loud.json",
                  "render-check.v1.silent.json", "render-check.v1.no-subtitles.json",
                  "render-check.v1.zero-cues.json", "render-check.v1.truncated.json")


@dataclass(frozen=True)
class Policy:
    """D10 `RenderQualityPolicy` defaults (`RenderQuality:*`)."""

    max_duration_drift: float = 0.05
    min_lufs: float = -16.0
    max_lufs: float = -14.0
    max_true_peak_dbtp: float = -1.0
    max_longest_silence_seconds: float = 10
    min_subtitle_coverage: float = 0.50
    min_cues: int = 1
    sync_suspect_hint_share: float = 0.10
    policy_version: int = 1


@dataclass(frozen=True)
class Expectations:
    """D10 `RenderExpectations` (`MusicSelected` = `render-params.v1.music != null`, T12)."""

    aspect: str
    subtitles_enabled: bool
    music_selected: bool


DEFAULT_POLICY = Policy()


def block(check, *path):
    """Nested lookup that never raises: the block at `path`, else None."""
    node = check
    for key in path:
        if not isinstance(node, dict):
            return None
        node = node.get(key)
    return node


def num(value):
    """`value` as a float-representable finite number, else None (bools/NaN/±inf/huge ints)."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    try:
        number = float(value)
    except OverflowError:  # an int too large for a float (JSON allows arbitrary precision)
        return None
    return value if math.isfinite(number) else None


def whole(value):
    """`value` as an int, else None (bools and JSON floats are not ints, not even `1.0`)."""
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return num(value)


def text(value):
    """`value` as a non-empty str, else None."""
    return value if isinstance(value, str) and value else None


def loudness_metric(check, name):
    """`loudness.after.<name> ?? loudness.before.<name>` (05.5.1-T2)."""
    # 05.5.1-T2 `after ?? before`: the post-loudnorm metric wins; `before` is the
    # fallback only when `after` is absent (a missing `before` too yields None).
    for side in ("after", "before"):
        value = num(block(check, "loudness", side, name))
        if value is not None:
            return value
    return None


def entry(id_, value, threshold, ok):
    """One `checks[]` row, fixture key order (id, value, threshold, pass)."""
    return {"id": id_, "value": value, "threshold": threshold, "pass": bool(ok)}


def fail_closed_report(policy):
    """D10/05.5.1-T2 `Evaluate(null, …)`: every check false with a null value, no hints."""
    rows = [
        entry("container_valid", None, 1, False),
        entry("streams", None, 1, False),
        entry("resolution", None, 1, False),
        entry("duration", None, policy.max_duration_drift, False),
        entry("loudness", None, policy.max_lufs, False),
        entry("true_peak", None, policy.max_true_peak_dbtp, False),
        entry("silence", None, policy.max_longest_silence_seconds, False),
        entry("subtitles_present", None, policy.min_cues, False),
        entry("subtitle_text", None, 1, False),
    ]
    return {
        "schema": SCHEMA,
        "pass": False,
        "policy_version": policy.policy_version,
        "check_tool_version": None,
        "duration_sec": None,
        "tts_duration_sec": None,
        "integrated_lufs": None,
        "true_peak_dbtp": None,
        "width": None,
        "height": None,
        "subtitles_present": False,
        "sha256": None,
        "normalized": False,
        "subtitles": {
            "cues": 0,
            "coverage_share": 0.0,
            "sync_suspect_share": 0.0,
            "long_cues": 0,
            "text_matches_script": None,
        },
        "checks": rows,
        "hints": [],
        "images": None,
    }


def evaluate(check, expect, policy=DEFAULT_POLICY):
    """D10 `RenderQualityEvaluator.Evaluate` — pure and total, fails closed."""
    if not isinstance(check, dict):
        return fail_closed_report(policy)
    sub = block(check, "subtitles")
    sub = sub if isinstance(sub, dict) else None
    video = block(check, "video")
    video = video if isinstance(video, dict) else None
    audio = block(check, "audio")
    output = block(check, "output")

    errors = block(check, "errors")
    container_ok = isinstance(errors, list) and not errors

    streams_ok = (video is not None and video.get("codec") == "h264"
                  and audio is not None and isinstance(audio, dict) and audio.get("codec") == "aac")

    width, height = whole(block(check, "video", "width")), whole(block(check, "video", "height"))
    resolution_ok = (width, height) == RESOLUTION.get(expect.aspect)

    duration_s = num(block(check, "container", "duration_s"))
    tts_s = num(block(check, "narration", "duration_s"))
    drift = None
    ratio = None
    if duration_s is not None and tts_s is not None and tts_s > 0:
        ratio = abs(duration_s - tts_s) / tts_s
        if not math.isfinite(ratio):  # extreme finite inputs can overflow the ratio
            ratio = None
    drift = None if ratio is None else round(ratio, DURATION_DRIFT_DIGITS)
    # The threshold is compared on the raw ratio; `drift` is only the reported value.
    duration_ok = ratio is not None and 0.0 <= ratio <= policy.max_duration_drift

    lufs = loudness_metric(check, "integrated_lufs")
    loudness_ok = lufs is not None and policy.min_lufs <= lufs <= policy.max_lufs
    peak = loudness_metric(check, "true_peak_dbtp")
    peak_ok = peak is not None and peak <= policy.max_true_peak_dbtp
    longest = num(block(check, "silence", "longest_s"))
    silence_ok = longest is not None and longest <= policy.max_longest_silence_seconds

    present = block(sub, "present") is True
    cues = whole(block(sub, "cues"))
    zero_duration = whole(block(sub, "zero_duration"))
    coverage = num(block(sub, "coverage_share"))
    subs_ok = (present and cues is not None and cues >= policy.min_cues
               and zero_duration == 0
               and coverage is not None and coverage >= policy.min_subtitle_coverage)
    matches = block(sub, "text_matches_script")
    if expect.subtitles_enabled:
        subs_row = entry("subtitles_present", cues, policy.min_cues, subs_ok)
        text_row = entry("subtitle_text", 1 if matches is True else 0 if matches is False else None,
                         1, matches is True)
    else:
        # D10: both subtitle checks are skipped when subtitles are off (05.5.1-T2).
        subs_row = entry("subtitles_present", None, None, True)
        text_row = entry("subtitle_text", None, None, True)

    checks = [
        entry("container_valid", 1 if container_ok else 0, 1, container_ok),
        entry("streams", 1 if streams_ok else 0, 1, streams_ok),
        entry("resolution", 1 if resolution_ok else 0, 1, resolution_ok),
        entry("duration", drift, policy.max_duration_drift, duration_ok),
        entry("loudness", lufs, policy.max_lufs, loudness_ok),
        entry("true_peak", peak, policy.max_true_peak_dbtp, peak_ok),
        entry("silence", longest, policy.max_longest_silence_seconds, silence_ok),
        subs_row,
        text_row,
    ]

    sync = whole(block(sub, "sync_suspect")) or 0
    long_cues = whole(block(sub, "long_cues")) or 0
    share = sync / max(cues, 1) if cues is not None else 0.0
    warnings = block(check, "warnings")
    hints = []
    if share > policy.sync_suspect_hint_share:
        hints.append("subtitle_sync_suspect")
    if long_cues >= 1:
        hints.append("subtitle_cues_long")
    if isinstance(warnings, list) and "loudnorm_dynamic" in warnings:
        hints.append("loudnorm_dynamic")
    if not expect.music_selected:
        hints.append("music_none")

    sha256 = text(block(output, "sha256")) or text(block(check, "input", "sha256"))

    return {
        "schema": SCHEMA,
        "pass": all(row["pass"] for row in checks),
        "policy_version": policy.policy_version,
        "check_tool_version": text(block(check, "tool_version")),
        "duration_sec": duration_s,
        "tts_duration_sec": tts_s,
        "integrated_lufs": lufs,
        "true_peak_dbtp": peak,
        "width": width,
        "height": height,
        "subtitles_present": present,
        "sha256": sha256,
        "normalized": block(output, "normalized") is True,
        "subtitles": {
            "cues": cues if cues is not None else 0,
            "coverage_share": coverage if coverage is not None else 0.0,
            "sync_suspect_share": round(share, SYNC_SUSPECT_SHARE_DIGITS),
            "long_cues": long_cues,
            "text_matches_script": matches if isinstance(matches, bool) else None,
        },
        "checks": checks,
        "hints": hints,
        "images": None,
    }


# --- self-test helpers -------------------------------------------------------------

def differences(mine, want, path="$"):
    """Human-readable leaf differences between two parsed JSON values (strict types)."""
    out = []
    if isinstance(mine, dict) and isinstance(want, dict):
        for key in sorted(set(mine) | set(want)):
            if key not in mine:
                out.append(f"{path}.{key}: missing in output (fixture {want[key]!r})")
            elif key not in want:
                out.append(f"{path}.{key}: extra in output ({mine[key]!r})")
            else:
                out.extend(differences(mine[key], want[key], f"{path}.{key}"))
    elif isinstance(mine, list) and isinstance(want, list):
        if len(mine) != len(want):
            out.append(f"{path}: {len(mine)} entries != fixture's {len(want)}")
        else:
            for index, (a, b) in enumerate(zip(mine, want)):
                out.extend(differences(a, b, f"{path}[{index}]"))
    elif type(mine) is not type(want) or mine != want:
        out.append(f"{path}: {mine!r} != {want!r}")
    return out


def set_path(doc, path, value):
    """In-place `doc[path[0]]...[path[-1]] = value` on a fresh copy's nested dicts."""
    node = doc
    for key in path[:-1]:
        node = node[key]
    node[path[-1]] = value
    return doc


def row(report, id_):
    """The `checks[]` row with `id_` (self-test only)."""
    return next(item for item in report["checks"] if item["id"] == id_)


def first_failure(report):
    """Id of the first failing check, else None (self-test only)."""
    return next((item["id"] for item in report["checks"] if not item["pass"]), None)


def fixture_dir():
    """Default fixture directory: `<repo>/worker-node/contract/fixtures`."""
    return Path(__file__).resolve().parents[2] / "worker-node" / "contract" / "fixtures"


def self_test(policy=DEFAULT_POLICY, directory=None):
    """`--self-test`: the D14 fixture verdicts, the boundaries and the CLI exits."""
    directory = Path(directory) if directory else fixture_dir()
    fails = []
    disagreements = []
    cases = 0

    def check(name, ok, detail=""):
        nonlocal cases
        cases += 1
        mark = "ok  " if ok else "FAIL"
        print(f"{mark} {name}" + (f" — {detail}" if detail else ""))
        if not ok:
            fails.append(name)

    def load(name):
        with open(directory / name, encoding="utf-8") as handle:
            return json.load(handle)

    fixtures = {}
    for name in CHECK_FIXTURES + (PASS_FIXTURE, FAIL_LOUDNESS_FIXTURE):
        try:
            fixtures[name] = load(name)
        except (OSError, ValueError) as exc:
            print(f"FAIL fixture {name}: {type(exc).__name__}: {exc}")
            fails.append(f"fixture:{name}")
    if fails:
        print(f"self-test FAILED ({len(fails)} failed): fixtures unreadable in {directory}")
        return 1

    ok_fix = fixtures["render-check.v1.ok.json"]
    expect_fixture = Expectations("landscape", True, True)
    # The quality fixtures carry no `music_none` hint, so MusicSelected was true.

    def verdict(doc, aspect="landscape", subs=True, music=True):
        return evaluate(doc, Expectations(aspect, subs, music), policy)

    def case(*pairs):
        doc = copy.deepcopy(ok_fix)
        for path, value in pairs:
            set_path(doc, path, value)
        return doc

    # 1. The two quality fixtures reproduce from their render-check sources exactly.
    mine = verdict(ok_fix)
    diffs = differences(mine, fixtures[PASS_FIXTURE])
    check("ok_fixture_matches_pass_fixture_exactly", not diffs, "; ".join(diffs[:6]))

    mine = verdict(fixtures["render-check.v1.loud.json"])
    want = fixtures[FAIL_LOUDNESS_FIXTURE]
    diffs = differences(mine, want)
    known_gap = (len(diffs) == 1 and diffs[0].startswith("$.hints")
                 and "loudnorm_dynamic" in mine["hints"]
                 and "loudnorm_dynamic" not in want["hints"])
    check("loud_fixture_matches_fail_loudness_fixture", not diffs or known_gap,
          ("known fixture gap (reported below): " if known_gap else "") + "; ".join(diffs[:6]))
    if diffs and known_gap:
        disagreements.append(
            f"{FAIL_LOUDNESS_FIXTURE} hints = {want['hints']}, but its source "
            f"render-check.v1.loud.json carries warnings ['loudnorm_dynamic'], so D10 + "
            f"05.5.1-T2 write {mine['hints']} (05.5.1-T2 pins "
            "syncSuspect→cuesLong→loudnormDynamic). The self-test allows exactly this "
            "one field difference; the fixture was not changed.")

    # 2. Check ids, order and thresholds (this is the printed check list).
    print("checks (id, threshold): " + " | ".join(
        f"{item['id']} {item['threshold']}" for item in mine["checks"]))
    check("check_ids_are_d10_order",
          [item["id"] for item in mine["checks"]] == list(CHECK_IDS),
          str([item["id"] for item in mine["checks"]]))
    check("check_thresholds_are_policy_values",
          [item["threshold"] for item in mine["checks"]] == [1, 1, 1, 0.05, -14.0, -1.0, 10, 1, 1],
          str([item["threshold"] for item in mine["checks"]]))

    # 3. Fail-closed fixtures: the expected check id fails first.
    silent = verdict(fixtures["render-check.v1.silent.json"])
    check("silent_first_failure_is_container_valid", first_failure(silent) == "container_valid",
          str(first_failure(silent)))

    no_subs = fixtures["render-check.v1.no-subtitles.json"]
    report = verdict(no_subs)
    check("no_subtitles_first_failure_is_subtitles_present",
          first_failure(report) == "subtitles_present" and report["pass"] is False,
          str(first_failure(report)))

    zero_cues = verdict(fixtures["render-check.v1.zero-cues.json"])
    check("zero_cues_first_failure_is_subtitles_present",
          first_failure(zero_cues) == "subtitles_present"
          and row(zero_cues, "subtitle_text")["pass"] is True,
          str(first_failure(zero_cues)))
    check("zero_cues_hints_sync_suspect_only",
          zero_cues["hints"] == ["subtitle_sync_suspect"], str(zero_cues["hints"]))

    truncated = verdict(fixtures["render-check.v1.truncated.json"])
    check("truncated_first_failure_is_container_valid",
          first_failure(truncated) == "container_valid", str(first_failure(truncated)))
    check("truncated_fails_every_check_and_keeps_metrics_null",
          truncated["pass"] is False and truncated["hints"] == []
          and all(not item["pass"] for item in truncated["checks"])
          and [truncated[key] for key in ("duration_sec", "tts_duration_sec", "integrated_lufs",
                                          "true_peak_dbtp", "width", "height")] == [None] * 6
          and truncated["sha256"] == fixtures["render-check.v1.truncated.json"]["input"]["sha256"]
          and truncated["normalized"] is False
          and truncated["subtitles"] == {"cues": 0, "coverage_share": 0.0,
                                         "sync_suspect_share": 0.0, "long_cues": 0,
                                         "text_matches_script": None})

    # 4. Boundaries (D10 wording: both ends inclusive, `≤`/`≥` thresholds).
    check("duration_drift_exactly_0_05_passes",
          row(verdict(case((("container", "duration_s"), 105.0),
                           (("narration", "duration_s"), 100.0))), "duration")["pass"] is True)
    check("duration_drift_0_0501_fails",
          row(verdict(case((("container", "duration_s"), 105.01),
                           (("narration", "duration_s"), 100.0))), "duration")["pass"] is False)
    # A non-positive narration must fail closed (a negative one used to pass open).
    negative = verdict(case((("container", "duration_s"), 105.0),
                            (("narration", "duration_s"), -100.0)))
    zero = verdict(case((("container", "duration_s"), 105.0),
                        (("narration", "duration_s"), 0.0)))
    check("narration_not_positive_fails_duration_closed",
          row(negative, "duration")["pass"] is False
          and row(negative, "duration")["value"] is None
          and row(zero, "duration")["pass"] is False and negative["pass"] is False)
    # The threshold is compared on the raw ratio, so a ratio that rounds *into* the
    # band still fails (105.004 s vs 100 s = 0.05004 raw); only the value is rounded.
    check("duration_drift_0_05004_fails_before_rounding",
          row(verdict(case((("container", "duration_s"), 105.004),
                           (("narration", "duration_s"), 100.0))), "duration")["pass"] is False
          and row(verdict(case((("container", "duration_s"), 105.0),
                               (("narration", "duration_s"), 100.0))),
                  "duration")["value"] == 0.05)
    # JSON ints only: the DTO's width/cues/zero_duration are ints, so a fractional
    # value is not one and fails closed instead of being truncated.
    fractional = verdict(case((("video", "width"), 1920.9), (("subtitles", "cues"), 1.9),
                              (("subtitles", "zero_duration"), 0.9)))
    check("fractional_int_fields_fail_closed",
          whole(1920.9) is None and whole(True) is None and whole(1080) == 1080
          and fractional["width"] is None
          and row(fractional, "resolution")["pass"] is False
          and row(fractional, "subtitles_present")["pass"] is False)
    for value, ok in ((-16.0, True), (-14.0, True), (-16.1, False), (-13.9, False)):
        check(f"lufs_{value}_is_{'inclusive_pass' if ok else 'fail'}",
              row(verdict(case((("loudness", "after", "integrated_lufs"), value))),
                  "loudness")["pass"] is ok)
    for value, ok in ((-1.0, True), (-0.9, False)):
        check(f"true_peak_{value}_is_{'pass' if ok else 'fail'}",
              row(verdict(case((("loudness", "after", "true_peak_dbtp"), value))),
                  "true_peak")["pass"] is ok)
    for value, ok in ((10, True), (10.1, False)):
        check(f"silence_{value}_is_{'pass' if ok else 'fail'}",
              row(verdict(case((("silence", "longest_s"), value))), "silence")["pass"] is ok)
    for value, ok in ((0.50, True), (0.49, False)):
        check(f"coverage_{value}_is_{'pass' if ok else 'fail'}",
              row(verdict(case((("subtitles", "coverage_share"), value))),
                  "subtitles_present")["pass"] is ok)
    check("cues_zero_fails_min_cues",
          row(verdict(case((("subtitles", "cues"), 0))), "subtitles_present")["pass"] is False)
    check("zero_duration_cue_fails_subtitles_present",
          row(verdict(case((("subtitles", "zero_duration"), 1))),
              "subtitles_present")["pass"] is False)

    # 5. `text_matches_script` and the subtitles-off skip.
    check("text_mismatch_fails_subtitle_text",
          row(verdict(case((("subtitles", "text_matches_script"), False))),
              "subtitle_text")["pass"] is False)
    check("text_matches_script_null_with_subtitles_enabled_fails",
          row(verdict(case((("subtitles", "text_matches_script"), None))),
              "subtitle_text")["pass"] is False)
    disabled = verdict(no_subs, subs=False)
    check("subtitles_disabled_skips_both_subtitle_checks_with_null_values",
          disabled["pass"] is True
          and row(disabled, "subtitles_present") == {"id": "subtitles_present", "value": None,
                                                     "threshold": None, "pass": True}
          and row(disabled, "subtitle_text") == {"id": "subtitle_text", "value": None,
                                                 "threshold": None, "pass": True})

    # 6. Hints.
    check("music_none_hint_when_not_selected",
          verdict(ok_fix, music=False)["hints"] == ["subtitle_cues_long", "music_none"],
          str(verdict(ok_fix, music=False)["hints"]))
    check("hint_order_matches_d10",
          verdict(case((("subtitles", "sync_suspect"), 60),
                       (("subtitles", "long_cues"), 2),
                       (("warnings",), ["loudnorm_dynamic"])),
                  music=False)["hints"]
          == ["subtitle_sync_suspect", "subtitle_cues_long", "loudnorm_dynamic", "music_none"])

    # 7. Fail closed: no check at all.
    empty = evaluate(None, expect_fixture, policy)
    check("missing_check_fails_closed",
          empty["pass"] is False and empty["hints"] == []
          and [item["id"] for item in empty["checks"]] == list(CHECK_IDS)
          and all(not item["pass"] and item["value"] is None for item in empty["checks"]))

    # 8. The CLI itself: verdict written, bad input and usage exits, `--out` symlink.
    def call_main(argv):
        out, err = io.StringIO(), io.StringIO()
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = main(argv)
        except SystemExit as exc:  # argparse exit (bad --aspect, --help)
            code = exc.code if isinstance(exc.code, int) else 2
        return code, out.getvalue(), err.getvalue()

    rc, out, _ = call_main(["--check", str(directory / "render-check.v1.ok.json"),
                            "--aspect", "landscape", "--subtitles-enabled", "--music-selected"])
    try:
        payload = json.loads(out)
    except ValueError:
        payload = None
    check("cli_prints_the_pass_verdict_and_exits_0",
          rc == 0 and payload is not None and not differences(payload, fixtures[PASS_FIXTURE]),
          f"exit {rc}")
    check("cli_portrait_resolution_fails_for_a_landscape_render",
          row(verdict(ok_fix, aspect="portrait"), "resolution")["pass"] is False)
    check("cli_usage_exits_2",
          call_main(["--check", str(directory / "render-check.v1.ok.json"), "--aspect",
                     "landscape"])[0] == 2
          and call_main(["--check", str(directory / "render-check.v1.ok.json"), "--aspect",
                         "landscape", "--subtitles-enabled", "--subtitles-disabled"])[0] == 2
          and call_main(["--check", str(directory / "render-check.v1.ok.json"), "--aspect",
                         "wide", "--subtitles-enabled"])[0] == 2)
    check("cli_missing_check_file_exits_1",
          call_main(["--check", str(directory / "nope.json"), "--aspect", "landscape",
                     "--subtitles-enabled"])[0] == 1
          and call_main(["--check", str(directory), "--aspect", "landscape",
                         "--subtitles-enabled"])[0] == 1)
    with tempfile.TemporaryDirectory() as tmp:
        wrong = Path(tmp) / "wrong.json"
        wrong.write_text(json.dumps(fixtures[PASS_FIXTURE]), encoding="utf-8")
        check("cli_not_render_check_v1_exits_1",
              call_main(["--check", str(wrong), "--aspect", "landscape",
                         "--subtitles-enabled"])[0] == 1)
        target = Path(tmp) / "real.json"
        target.write_text("keep me", encoding="utf-8")
        link = Path(tmp) / "link.json"
        os.symlink(target, link)
        rc, _, _ = call_main(["--check", str(directory / "render-check.v1.ok.json"),
                              "--aspect", "landscape", "--subtitles-enabled",
                              "--out", str(link)])
        check("cli_refuses_a_symlinked_out",
              rc == 2 and target.read_text(encoding="utf-8") == "keep me")
        same = Path(tmp) / "same.json"
        same.write_text(json.dumps(fixtures["render-check.v1.ok.json"]), encoding="utf-8")
        same_rc, _, same_err = call_main(["--check", str(same), "--aspect", "landscape",
                                          "--subtitles-enabled", "--out", str(same)])
        check("cli_refuses_out_same_file_as_check",
              same_rc == 2 and "same file" in same_err
              and json.loads(same.read_text(encoding="utf-8"))
              == fixtures["render-check.v1.ok.json"])
        dest = Path(tmp) / "verdict.json"
        rc, out, _ = call_main(["--check", str(directory / "render-check.v1.ok.json"),
                                "--aspect", "landscape", "--subtitles-enabled",
                                "--music-selected", "--out", str(dest)])
        written = json.loads(dest.read_text(encoding="utf-8"))
        check("cli_writes_out_and_stdout_identically",
              rc == 0 and written == json.loads(out)
              and not differences(written, fixtures[PASS_FIXTURE]))
        big = Path(tmp) / "big.json"
        big.write_text(json.dumps(fixtures["render-check.v1.ok.json"])
                       + " " * (MAX_CHECK_BYTES + 1), encoding="utf-8")
        big_rc, _, big_err = call_main(["--check", str(big), "--aspect", "landscape",
                                        "--subtitles-enabled"])
        check("cli_rejects_oversized_check_exits_1",
              big_rc == 1 and "exceeds" in big_err, f"exit {big_rc}")
        deep = Path(tmp) / "deep.json"
        deep.write_text("[" * 100000, encoding="utf-8")
        deep_rc, _, deep_err = call_main(["--check", str(deep), "--aspect", "landscape",
                                          "--subtitles-enabled"])
        check("cli_deeply_nested_check_exits_1_without_traceback",
              deep_rc == 1 and "not JSON" in deep_err and "Traceback" not in deep_err,
              f"exit {deep_rc}")
        mode_dest = Path(tmp) / "mode.json"
        mode_rc, _, _ = call_main(["--check", str(directory / "render-check.v1.ok.json"),
                                   "--aspect", "landscape", "--subtitles-enabled",
                                   "--music-selected", "--out", str(mode_dest)])
        mode = (os.stat(mode_dest).st_mode & 0o777) if mode_dest.exists() else None
        check("cli_out_file_mode_is_0600",
              mode_rc == 0 and mode == 0o600, oct(mode) if mode is not None else "missing")

    # 9. JSON-valid garbage: a non-finite metric, a NaN `tool_version`, a duration ratio
    #    that overflows and an absurd integer all become null and the report stays strict
    #    JSON (`allow_nan=False` + `parse_constant` must not raise).
    def reject_constant(token):
        raise AssertionError(f"non-strict JSON constant {token!r}")

    def strict(report):
        try:
            json.loads(json.dumps(report, allow_nan=False), parse_constant=reject_constant)
            return True
        except (ValueError, AssertionError):
            return False

    nan_report = verdict(case((("silence", "longest_s"), float("nan")),
                              (("loudness", "after", "integrated_lufs"), float("nan")),
                              (("loudness", "before", "integrated_lufs"), float("nan"))))
    overflow_report = verdict(case((("container", "duration_s"), 1e308),
                                   (("narration", "duration_s"), 1e-308)))
    huge_report = verdict(case((("subtitles", "sync_suspect"), 10 ** 400),
                               (("subtitles", "cues"), 1)))
    version_report = verdict(case((("tool_version",), float("nan"))))
    check("garbage_inputs_stay_null_and_report_stays_strict_json",
          num(float("nan")) is None and num(float("inf")) is None and num(-float("inf")) is None
          and num(10 ** 400) is None
          and nan_report["integrated_lufs"] is None
          and row(nan_report, "loudness")["pass"] is False
          and row(nan_report, "silence")["pass"] is False and nan_report["pass"] is False
          and row(overflow_report, "duration") == {"id": "duration", "value": None,
                                                   "threshold": policy.max_duration_drift,
                                                   "pass": False}
          and huge_report["subtitles"]["sync_suspect_share"] == 0.0
          and huge_report["pass"] is True
          and version_report["check_tool_version"] is None
          and all(strict(report) for report in (nan_report, overflow_report, huge_report,
                                                version_report)))

    print(f"fixture/D10 disagreements: {len(disagreements)}")
    for line in disagreements:
        print("disagreement: " + line)
    print(f"self-test {'FAILED' if fails else 'ok'} ({len(fails)} failed, {cases} cases)")
    return 1 if fails else 0


def main(argv):
    parser = argparse.ArgumentParser(
        description="Apply D10's render quality policy to a render-check.v1 verdict (M0 mirror).")
    parser.add_argument("--self-test", action="store_true",
                        help="run the D14 fixture and boundary assertions, then exit")
    parser.add_argument("--fixtures", default=None,
                        help="fixture directory for --self-test "
                             "(default worker-node/contract/fixtures)")
    parser.add_argument("--check", default=None, help="render-check.v1 JSON file")
    parser.add_argument("--aspect", choices=("landscape", "portrait"))
    parser.add_argument("--subtitles-enabled", action="store_true")
    parser.add_argument("--subtitles-disabled", action="store_true")
    parser.add_argument("--music-selected", action="store_true")
    parser.add_argument("--out", default=None, help="also write the verdict here")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test(directory=args.fixtures)

    usage = ("usage: apply_quality_policy.py --check FILE --aspect landscape|portrait "
             "(--subtitles-enabled|--subtitles-disabled) [--music-selected] [--out FILE] "
             "(or --self-test)")
    if not args.check or not args.aspect or args.subtitles_enabled == args.subtitles_disabled:
        print(usage, file=sys.stderr)
        return 2

    try:
        with open(args.check, "rb") as handle:
            if os.fstat(handle.fileno()).st_size > MAX_CHECK_BYTES:
                print(f"apply_quality_policy.py: --check {args.check} exceeds "
                      f"{MAX_CHECK_BYTES} bytes", file=sys.stderr)
                return 1
            raw = handle.read(MAX_CHECK_BYTES + 1)
        if len(raw) > MAX_CHECK_BYTES:  # the file grew between fstat and read
            print(f"apply_quality_policy.py: --check {args.check} exceeds "
                  f"{MAX_CHECK_BYTES} bytes", file=sys.stderr)
            return 1
        check = json.loads(raw)
    except OSError as exc:
        print(f"apply_quality_policy.py: cannot read --check {args.check}: {exc}", file=sys.stderr)
        return 1
    except ValueError as exc:
        print(f"apply_quality_policy.py: --check {args.check} is not JSON: {exc}", file=sys.stderr)
        return 1
    except RecursionError:
        print(f"apply_quality_policy.py: --check {args.check} is not JSON: nested too deeply",
              file=sys.stderr)
        return 1
    if not isinstance(check, dict) or check.get("schema") != SCHEMA_CHECK:
        print(f"apply_quality_policy.py: --check {args.check} is not a {SCHEMA_CHECK} object",
              file=sys.stderr)
        return 1

    expect = Expectations(args.aspect, args.subtitles_enabled, args.music_selected)
    report = evaluate(check, expect)
    payload = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    if args.out:
        if os.path.islink(args.out):
            print(f"apply_quality_policy.py: --out is a symlink, refusing to write through it: "
                  f"{args.out}", file=sys.stderr)
            return 2
        if os.path.exists(args.out):
            try:
                same = os.path.samefile(args.out, args.check)
            except OSError:
                same = False
            if same:
                print(f"apply_quality_policy.py: --out and --check are the same file, refusing "
                      f"to overwrite the input: {args.out}", file=sys.stderr)
                return 2
        try:
            # SEC-01: open WITHOUT O_TRUNC — a same-file refusal must not truncate
            # --check before it is detected. O_NOFOLLOW turns a symlink raced in after
            # the prechecks into ELOOP instead of silently writing through it.
            fd = os.open(args.out, os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        except OSError as exc:
            print(f"apply_quality_policy.py: cannot write --out {args.out}: {exc}", file=sys.stderr)
            return 2
        try:
            out_stat = os.fstat(fd)
            check_stat = os.stat(args.check)
            if (out_stat.st_dev, out_stat.st_ino) == (check_stat.st_dev, check_stat.st_ino):
                print(f"apply_quality_policy.py: --out and --check are the same file, refusing "
                      f"to overwrite the input: {args.out}", file=sys.stderr)
                return 2
            os.ftruncate(fd, 0)
            handle = os.fdopen(fd, "w", encoding="utf-8")
            fd = -1  # the file object owns the descriptor from here on
            with handle:
                handle.write(payload)
        except OSError as exc:
            print(f"apply_quality_policy.py: cannot write --out {args.out}: {exc}", file=sys.stderr)
            return 2
        finally:
            if fd != -1:
                os.close(fd)
        print(f"apply_quality_policy.py: wrote {args.out} (pass={report['pass']})", file=sys.stderr)
    sys.stdout.write(payload)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
