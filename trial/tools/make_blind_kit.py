"""Build the blind rater kit for the 02.1.3-T1 A/B (trial/tools/make_blind_kit.py).

Shuffles the rated run directories into `trial/ab/<round>/stories/S<nn>.md`
plus `key.csv`. The kit file carries only the story title and body — no model,
preset, brief id, `run_id`, `prompt_version` or metric (plan §3.4). `key.csv`
is the only file that unblinds and keeps the seed so the mapping is
reproducible but not guessable from the kit.

Exit codes (01 D9): 0 ok · 1 a check failed · 2 usage / environment error.
A run dir that looks rated but cannot be turned into a kit file (metrics.json
without `story.json`/`video_script.txt`, an unreadable metrics.json) is an
environment error: exit 2 with one named line per dir, never a traceback.
Stdlib only. Run: `uv run --no-project --python 3.12 trial/tools/make_blind_kit.py`.
"""

from __future__ import annotations

import argparse
import contextlib
import csv
import io
import json
import random
import re
import sys
import tempfile
from pathlib import Path

DEFAULT_SEED = 20261001
KEY_COLUMNS = ["story_id", "run_id", "model", "preset", "language", "brief_id", "tier"]
REQUIRED_CELL_FILES = ("story.json", "video_script.txt")


class InputError(Exception):
    """Unusable kit input (01 D9): exit 2 with a named reason, never a traceback."""

# Identity strings that must never reach a rater. Structural keys and model
# slugs only — topic words like "ghost" can legitimately appear inside a
# story's own text, so T1's wider leak grep stays a manual step.
FORBIDDEN_RE = re.compile(r"gpt-6-luna|gpt-6-sol|\bluna\b|\bsol\b|run_id|"
                          r"prompt_version|brief_id|preset|tier=|language=", re.IGNORECASE)


def discover_cells(runs_dir: Path, tier: str) -> list[dict]:
    """Rated cells: dirs with a parseable metrics.json of the scored tier.

    A dir that LOOKS rated but cannot be turned into a kit file (unreadable or
    non-object metrics.json, or a missing story.json/video_script.txt) is an
    environment error, so raise InputError naming every such dir (QA D1).
    Dirs with no metrics.json at all, or with another tier, are not rated
    cells and stay silently skipped.
    """
    cells = []
    problems = []
    for path in sorted(runs_dir.iterdir()):
        metrics_path = path / "metrics.json"
        if not path.is_dir() or not metrics_path.is_file():
            continue
        try:
            metrics = json.loads(metrics_path.read_text(encoding="utf-8"))
        except (ValueError, OSError) as exc:
            problems.append(f"{path.name}: metrics.json unusable ({exc})")
            continue
        if not isinstance(metrics, dict):
            problems.append(f"{path.name}: metrics.json is not an object")
            continue
        if metrics.get("tier") != tier:
            continue
        missing = [name for name in REQUIRED_CELL_FILES
                   if not (path / name).is_file()]
        if missing:
            problems.append(f"{path.name}: incomplete cell, "
                            f"missing {', '.join(missing)}")
            continue
        cells.append({"run_id": path.name,
                      "run_dir": path,
                      "model": metrics.get("model", ""),
                      "preset": metrics.get("preset", ""),
                      "language": metrics.get("language", ""),
                      "brief_id": metrics.get("brief_id", ""),
                      "tier": metrics.get("tier", ""),
                      "outcome": metrics.get("outcome", "")})
    if problems:
        raise InputError(f"{len(problems)} unusable run dir(s) under "
                         f"{runs_dir} (tier {tier}): " + "; ".join(problems))
    return cells


def load_story_text(run_dir: Path) -> tuple[str, str]:
    """Return (title, body). Body = video_script.txt (the D3 `body`)."""
    story_path = run_dir / "story.json"
    script_path = run_dir / "video_script.txt"
    try:
        story = json.loads(story_path.read_text(encoding="utf-8"))
        body = script_path.read_text(encoding="utf-8").strip()
    except (ValueError, OSError) as exc:
        raise InputError(f"{run_dir.name}: unreadable story file ({exc})") from exc
    if not isinstance(story, dict):
        raise InputError(f"{run_dir.name}: story.json is not an object")
    title = str(story.get("title", "")).strip()
    return title, body


def build_kit(cells: list[dict], out_dir: Path, seed: int) -> list[dict]:
    """Write stories/S<nn>.md + key.csv. Returns the key rows in kit order."""
    stories_dir = out_dir / "stories"
    stories_dir.mkdir(parents=True, exist_ok=True)
    run_ids = sorted(cell["run_id"] for cell in cells)
    shuffled = run_ids[:]
    random.Random(seed).shuffle(shuffled)
    by_id = {cell["run_id"]: cell for cell in cells}

    rows = []
    for index, run_id in enumerate(shuffled, start=1):
        story_id = f"S{index:02d}"
        cell = by_id[run_id]
        title, body = load_story_text(cell["run_dir"])
        kit_text = f"{title}\n\n{body}\n"
        # Identity guard: refuse to write a kit file that leaks the run. The
        # refusal names what matched, so a legit body word (say "Luna") that
        # trips the regex is distinguishable from a real run_id leak.
        leaked = FORBIDDEN_RE.search(kit_text)
        if leaked or cell["run_id"] in kit_text:
            token = (f"token {leaked.group(0)!r}" if leaked
                     else f"run_id {cell['run_id']}")
            raise SystemExit(f"kit file for {story_id} would leak {token} "
                             f"(run {cell['run_id']})")
        (stories_dir / f"{story_id}.md").write_text(kit_text, encoding="utf-8")
        rows.append({"story_id": story_id, "run_id": run_id, "model": cell["model"],
                     "preset": cell["preset"], "language": cell["language"],
                     "brief_id": cell["brief_id"], "tier": cell["tier"]})

    # key.csv: a `# seed=<n>` comment line (parsed by aggregate_scores.py) then
    # the plan §3.4 columns. The seed is "stored in key.csv" per T1 §4.
    key_path = out_dir / "key.csv"
    with key_path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(f"# seed={seed}\n")
        writer = csv.DictWriter(handle, fieldnames=KEY_COLUMNS)
        writer.writeheader()
        writer.writerows(rows)
    return rows


def selftest() -> int:
    with tempfile.TemporaryDirectory() as tmp:
        runs_dir = Path(tmp) / "trial" / "stories"
        out_dir = Path(tmp) / "trial" / "ab" / "r1"
        expected_run_ids = []
        for language in ("vi", "en"):
            topics = {"ghost": (1, 3), "spiritual": (4, 6), "true_crime": (7, 9)}
            for preset, (lo, hi) in topics.items():
                for brief_num in range(lo, hi + 1):
                    for model_slug, model in (("luna", "gpt-6-luna"), ("sol", "gpt-6-sol")):
                        run_id = f"{language}-{preset}-b{brief_num}-{model_slug}-standard"
                        expected_run_ids.append(run_id)
                        cell_dir = runs_dir / run_id
                        cell_dir.mkdir(parents=True)
                        (cell_dir / "story.json").write_text(json.dumps(
                            {"title": f"Title for {run_id[:2]} cell", "paragraphs": []}),
                            encoding="utf-8")
                        (cell_dir / "video_script.txt").write_text(
                            "Body text with no identity inside.", encoding="utf-8")
                        (cell_dir / "metrics.json").write_text(json.dumps(
                            {"run_id": run_id, "model": model, "tier": "standard",
                             "language": language, "preset": preset,
                             "brief_id": f"b{brief_num}", "outcome": "ok"}),
                            encoding="utf-8")

        rows_a = build_kit(discover_cells(runs_dir, "standard"), out_dir, 7)
        assert len(list((out_dir / "stories").glob("S*.md"))) == 36, "want 36 kit files"
        story_ids = [row["story_id"] for row in rows_a]
        assert sorted(story_ids) == [f"S{i:02d}" for i in range(1, 37)], "unique S01..S36"

        # Order must not be the sorted run-id order (shuffle actually applied).
        assert [r["run_id"] for r in rows_a] != sorted(expected_run_ids), "no shuffle?"

        # No kit file leaks its run_id, the model slug, or a language hint.
        for row in rows_a:
            text = (out_dir / "stories" / f"{row['story_id']}.md").read_text(encoding="utf-8")
            assert row["run_id"] not in text, "run_id leaked"
            assert not FORBIDDEN_RE.search(text), f"identity token leaked: {row['story_id']}"

        # Same seed reproduces the identical mapping.
        out_b = Path(tmp) / "trial" / "ab" / "r1-again"
        rows_b = build_kit(discover_cells(runs_dir, "standard"), out_b, 7)
        assert [r["run_id"] for r in rows_a] == [r["run_id"] for r in rows_b], "seed not reproducible"

        # key.csv carries the seed line and the plan columns.
        head = (out_dir / "key.csv").read_text(encoding="utf-8").splitlines()[:2]
        assert head[0] == "# seed=7", "seed line missing"
        assert head[1] == ",".join(KEY_COLUMNS), "key.csv columns drifted"

        # QA D1 regression: a partially populated cell (metrics.json but the
        # story files missing — both variants) must exit 2 with a named reason
        # per dir, never a traceback, and never write a partial kit.
        torn_runs = Path(tmp) / "torn" / "stories"
        for run_id, keep in (("vi-ghost-b1-luna-standard", "story.json"),
                             ("vi-ghost-b2-luna-standard", "video_script.txt")):
            cell_dir = torn_runs / run_id
            cell_dir.mkdir(parents=True)
            (cell_dir / "metrics.json").write_text(json.dumps(
                {"run_id": run_id, "model": "gpt-6-luna", "tier": "standard",
                 "language": "vi", "preset": "ghost", "brief_id": "b1",
                 "outcome": "ok"}), encoding="utf-8")
            (cell_dir / keep).write_text(
                "{}" if keep == "story.json" else "Body text plain.",
                encoding="utf-8")
        torn_out = Path(tmp) / "torn" / "ab" / "r1"
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            code = main(["--runs-dir", str(torn_runs), "--out", str(torn_out)])
        message = stderr.getvalue()
        assert code == 2, f"incomplete cell must exit 2, got {code}"
        assert "Traceback" not in message, message
        for name in ("vi-ghost-b1-luna-standard", "video_script.txt",
                     "vi-ghost-b2-luna-standard", "story.json"):
            assert name in message, f"{name} not named: {message}"
        assert not (torn_out / "stories").is_dir(), "a partial kit was written"
    print("selftest ok")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Build the blind rater kit (trial/ab/<round>/{stories,key.csv}).")
    parser.add_argument("--runs-dir", default="trial/stories",
                        help="directory of harness run dirs (default trial/stories)")
    parser.add_argument("--out", default="trial/ab/r1",
                        help="kit output directory (default trial/ab/r1)")
    parser.add_argument("--tier", default="standard",
                        help="the scored tier; the short/long sweep never enters the kit")
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED,
                        help=f"shuffle seed, recorded in key.csv (default {DEFAULT_SEED})")
    parser.add_argument("--expect", type=int, default=36,
                        help="required rated cell count (default 36)")
    parser.add_argument("--selftest", action="store_true", help="run the offline self-test")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    runs_dir = Path(args.runs_dir)
    out_dir = Path(args.out)
    if not runs_dir.is_dir():
        print(f"error: --runs-dir {runs_dir} does not exist", file=sys.stderr)
        return 2
    if (out_dir / "stories").is_dir() and any((out_dir / "stories").iterdir()):
        print("error: kit already exists — a new seed invalidates filed scores; "
              "regenerate as r2 (T1 failure table)", file=sys.stderr)
        return 2

    try:
        cells = discover_cells(runs_dir, args.tier)
        bad = [c["run_id"] for c in cells if c["outcome"] != "ok"]
        if bad:
            print(f"error: {len(bad)} rated cell(s) not outcome=ok: {bad}",
                  file=sys.stderr)
            return 1
        if len(cells) != args.expect:
            print(f"error: found {len(cells)} rated cells, expected {args.expect}",
                  file=sys.stderr)
            return 1
        rows = build_kit(cells, out_dir, args.seed)
    except InputError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    print(f"seed={args.seed}")
    print(f"{len(rows)} stories -> {out_dir}/stories/, key -> {out_dir}/key.csv")
    return 0


if __name__ == "__main__":
    sys.exit(main())
