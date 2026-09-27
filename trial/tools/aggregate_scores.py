"""Aggregate blind scores into `aggregate.csv`, `REPORT.md`, `handoff.json`
(02.1.3-T1 §6 + the handoff extension from 02.1.3-T2 §3).

Joins `scores.csv` with `key.csv` — the only unblinding step, run after all
scoring is filed — and prints the D15 test with the numbers. `n` is never
folded: a mean built on fewer than 2 raters is marked `†`, and if a `†` mean
feeds a deciding comparison the report prints `DECISION NOT READY`.
`--rater-mode ai` (02 T106) swaps the rater population: coverage, `n`, `†`
and `DECISION NOT READY` then count the two AI passes (`rater_id` `ai-*`,
`rater_native` false) instead of native humans, the native count is still
printed per story, and REPORT.md carries the line `AI-rated (02 T106), not
native-human evidence` above the D15 section. In both modes a row whose
`rater_native` disagrees with its `rater_id` (`false` without an `ai-` id, or
`ai-` with `true`) is neither a native human nor an AI pass: it is rejected as
a named input error (exit 2), and the rater population is chosen with the same
predicate the report's coverage section uses, so the printed counts and the
means can never disagree (SEC-0928b-1). In `native` mode the `ai-*` rows are
ignored for coverage and means, so the verdict stays human-only.

Exit codes (01 D9): 0 ok · 1 a check failed · 2 usage / environment error.
Stdlib only: `uv run --no-project --python 3.12 trial/tools/aggregate_scores.py`.

`key.csv`'s `run_id` is validated as a harness cell id before it is ever joined
onto `--runs-dir` (SEC-0928c-3: a traversal or arbitrary name is a named exit-2
input error), and a symlinked cell dir, a dir resolving outside `--runs-dir`, or
a `metrics.json` that is not a JSON object is not a cell record. REPORT.md's
02 T108 section re-validates each rated cell's `story.json` with the current D3
validator (CRV-0928c-4) — the recorded `metrics.json.validator_issues` are only
the run-time snapshot and are the fallback, marked `(recorded)`.
"""

from __future__ import annotations

import argparse
import contextlib
import csv
import io
import json
import math
import re
import statistics
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import story_validator as sv  # noqa: E402

MODEL_LUNA = "gpt-6-luna"
MODEL_SOL = "gpt-6-sol"
DIMENSIONS = ["hook", "pacing", "atmosphere", "natural_language",
              "originality", "length_fit"]
FACTUAL_CARE = "factual_care"          # scored, never folded into D15 (T25)
RATED_TIER = "standard"
VI_NL_FLOOR = 3.5                      # D15 (b)
SOL_MARGIN = 0.9                       # D15 (a): luna < sol * 0.9 -> sol
DEFAULT_LENGTH_TABLE = Path(__file__).with_name("LENGTH.v1.json")

# SEC-0928c-3: a key.csv run_id becomes a path component under --runs-dir, so it
# must be a harness cell id and nothing else — no traversal, no absolute path.
RUN_ID_RE = re.compile(r"^(vi|en)-[a-z_]+-b\d+-(luna|sol)"
                       r"-(short|standard|long)(-r\d+)?$")

KEY_COLUMNS = ["story_id", "run_id", "model", "preset", "language",
               "brief_id", "tier"]
SCORE_COLUMNS = ["story_id", "rater_id", "rater_language", "rater_native",
                 "dimension", "score", "notes", "scored_at"]
VALID_DIMENSIONS = DIMENSIONS + [FACTUAL_CARE]


class InputError(Exception):
    """Unusable input (01 D9): exit 2 with a named reason, never a traceback."""


def _require_columns(path: Path, have: list[str] | None, want: list[str]) -> None:
    missing = [column for column in want if column not in (have or [])]
    if missing:
        raise InputError(f"{path}: missing column(s) {', '.join(missing)}")


def _rater_source(row: dict) -> str | None:
    """Population of one score row: `ai` for an AI pass, `native` for a human.

    SEC-0928b-1: `rater_native` and the `ai-` id must agree — an AI pass is
    `rater_native=false` with an `ai-*` id, a native human is `rater_native=true`
    with any other id; anything else is neither population and returns None.
    `read_scores` rejects the None rows, REPORT.md coverage and `aggregate()`
    both pick the population with this one predicate, so the printed counts and
    the means can never disagree.
    """
    is_ai = (row.get("rater_id") or "").startswith("ai-")
    if (row.get("rater_native") == "true") == is_ai:
        return None
    return "ai" if is_ai else "native"

def read_key(path: Path) -> tuple[int | None, list[dict]]:
    """key.csv: optional `# seed=<n>` line, then the §3.4 columns."""
    seed = None
    header = None
    rows = []
    for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(),
                                  start=1):
        if line.startswith("# seed="):
            raw = line.split("=", 1)[1].strip()
            try:
                seed = int(raw)
            except ValueError as exc:
                raise InputError(f"{path}:{lineno}: bad seed line {line!r}") from exc
            continue
        if line.startswith("#") or not line.strip():
            continue
        cells = next(csv.reader([line]))
        if header is None:
            header = cells
            _require_columns(path, cells, KEY_COLUMNS)
            continue
        if len(cells) != len(header):
            raise InputError(f"{path}:{lineno}: {len(cells)} field(s), "
                             f"expected {len(header)}")
        row = dict(zip(header, cells))
        # SEC-0928c-3: the run_id is joined onto --runs-dir, so a traversal or
        # an arbitrary name is rejected here (exit 2), never resolved later.
        if not RUN_ID_RE.fullmatch(row["run_id"]):
            raise InputError(f"{path}:{lineno}: bad run_id {row['run_id']!r} — "
                             "expected <lang>-<preset>-b<n>-<luna|sol>-"
                             "<short|standard|long>[-rN]")
        rows.append(row)
    return seed, rows


def read_scores(path: Path) -> list[dict]:
    """scores.csv rows with a filed score.

    A blank `score` is an unrated row and is skipped, but a non-numeric score,
    a score outside the rubric range 1-5, an unknown dimension or a row whose
    shape does not match the header is never accepted silently: InputError
    with file:line, exit 2 (QA D3/D4; 01 D9 says never a traceback).

    02 T106 defines the rater ids: an AI pass files `rater_id` = `ai-<lang>-<n>`
    with `rater_native` = false, a native human files `rater_native` = true with
    any other id. A row where the two disagree — `rater_native` = false with no
    `ai-` prefix, or `ai-` with `rater_native` = true — is neither a native
    human nor an AI pass, so it is rejected in either rater mode (named
    InputError, exit 2) instead of being counted by one of them (SEC-0928b-1).
    """
    rows = []
    problems = []
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        _require_columns(path, reader.fieldnames, SCORE_COLUMNS)
        for row in reader:
            where = (f"{path}:{reader.line_num}: story "
                     f"{row.get('story_id')!r} dimension "
                     f"{row.get('dimension')!r} rater {row.get('rater_id')!r}")
            if None in row or any(value is None for value in row.values()):
                problems.append(f"{where}: row shape does not match the header")
                continue
            raw = (row["score"] or "").strip()
            if not raw:
                continue                        # filed but not yet scored
            try:
                value = float(raw)
            except ValueError:
                problems.append(f"{where}: score {raw!r} is not a number")
                continue
            if not math.isfinite(value) or not 1.0 <= value <= 5.0:
                problems.append(f"{where}: score {raw!r} outside "
                                f"the rubric range 1-5")
                continue
            if row["dimension"] not in VALID_DIMENSIONS:
                problems.append(f"{where}: unknown dimension "
                                f"{row['dimension']!r}")
                continue
            if _rater_source(row) is None:
                problems.append(
                    f"{where}: rater_native={row.get('rater_native')!r} "
                    f"disagrees with the rater_id — an AI pass is "
                    f"rater_native=false with an ai-* rater_id, a native human "
                    f"is rater_native=true (SEC-0928b-1)")
                continue
            row["_line"] = reader.line_num
            rows.append(row)
    if problems:
        raise InputError(f"{len(problems)} bad score row(s) in {path}: "
                         + "; ".join(problems))
    return rows

def read_metrics(runs_dir: Path, run_id: str) -> dict:
    """One cell's metrics.json as a dict, or `{}` when it is not a cell record.

    SEC-0928c-3: the cell dir is never read through a symlink or from outside
    `--runs-dir`, a metrics.json that is not a JSON object (a list, a scalar)
    is not a metrics record, and a pathological JSON (deep nesting →
    RecursionError) is an unusable input like a plain parse error — all of them
    return `{}` instead of raising inside the aggregate.
    """
    cell_dir = runs_dir / run_id
    try:
        if cell_dir.is_symlink() or not cell_dir.is_dir():
            return {}
        if not cell_dir.resolve().is_relative_to(runs_dir.resolve()):
            return {}
        data = json.loads((cell_dir / "metrics.json").read_text(encoding="utf-8"))
    except (ValueError, OSError, RecursionError):
        return {}
    return data if isinstance(data, dict) else {}


def percentile(values: list[float], p: float) -> float:
    """Nearest-rank percentile."""
    ordered = sorted(values)
    index = max(1, math.ceil(p / 100.0 * len(ordered))) - 1
    return ordered[index]


def mean(values: list[float]) -> float | None:
    return statistics.fmean(values) if values else None


def aggregate(key_rows: list[dict], scores: list[dict], runs_dir: Path,
              rater_mode: str = "native") -> dict:
    """Build the grouped aggregate: one row per (model, preset, language,
    dimension). `n` = the fewest distinct raters on any story of that row
    (QA-0928c-3: a story that lost one rater makes the row `n=1`, never hidden
    by the other stories' raters; a story with no rows at all counts 0).

    A score row whose `story_id` is absent from `key.csv` is a named
    InputError (exit 2), never a silent drop: a rater typo or a stale key file
    would otherwise shrink the sample unnoticed.

    `rater_mode` (02 T106) selects the rater population that counts for `n`,
    `mean_rating` and therefore the `†` / `DECISION NOT READY` logic:
    `native` counts human rows (`rater_native` = true, id not `ai-*`), `ai`
    counts the two AI passes (`ai-*`, `rater_native` = false) — the same
    `_rater_source` predicate REPORT.md's coverage section uses (SEC-0928b-1),
    so the two can never disagree. Rows of the other population stay in
    `scores` for the coverage section but never enter a mean — an `ai-*` row
    can never inflate the native-human `n`, and vice versa.
    """
    groups = {}
    for key in key_rows:
        groups.setdefault((key["model"], key["preset"], key["language"]), []).append(key["run_id"])

    key_by_story = {key["story_id"]: key for key in key_rows}
    dim_scores = {}
    unknown = {}
    for row in scores:
        key = key_by_story.get(row["story_id"])
        if key is None:
            unknown.setdefault(row["story_id"], []).append(row.get("_line"))
            continue
        gkey = (key["model"], key["preset"], key["language"], row["dimension"])
        if _rater_source(row) != rater_mode:
            continue                        # other population: coverage only (02 T106)
        dim_scores.setdefault(gkey, []).append(row)
    if unknown:
        detail = "; ".join(
            "%r at line %s" % (story_id, ", ".join(str(line) for line in lines))
            for story_id, lines in sorted(unknown.items()))
        raise InputError(
            "score row(s) whose story_id is not in key.csv: " + detail
            + " — a rater typo or a stale key file would shrink the sample silently")

    rows = []
    for (model, preset, language), run_ids in sorted(groups.items()):
        dims = DIMENSIONS + ([FACTUAL_CARE] if preset == "true_crime" else [])
        metrics = [read_metrics(runs_dir, rid) for rid in run_ids]
        valid = [m for m in metrics if m]
        stats = {
            "json_valid_rate": mean([1.0 if m.get("json_valid") else 0.0 for m in metrics]),
            "tokens_mean": mean([m["tokens"]["body_tokens"] for m in valid
                                 if (m.get("tokens") or {}).get("body_tokens") is not None]),
            "latency_p50_ms": percentile([m["latency_ms"] for m in valid
                                          if m.get("latency_ms") is not None], 50)
                              if any(m.get("latency_ms") is not None for m in valid) else None,
            "latency_p95_ms": percentile([m["latency_ms"] for m in valid
                                          if m.get("latency_ms") is not None], 95)
                              if any(m.get("latency_ms") is not None for m in valid) else None,
            "cost_usd_mean": mean([m["cost_usd"] for m in valid
                                   if m.get("cost_usd") is not None]),
        }
        for dim in dims:
            scored = dim_scores.get((model, preset, language, dim), [])
            values = [float(r["score"]) for r in scored]
            per_story = {}
            for r in scored:
                per_story.setdefault(r["story_id"], set()).add(r["rater_id"])
            stories = [k["story_id"] for k in key_rows
                       if (k["model"], k["preset"], k["language"]) == (model, preset, language)]
            rows.append({
                "model": model, "preset": preset, "language": language,
                "dimension": dim,
                "n": min((len(per_story.get(s, ())) for s in stories), default=0),
                "mean_rating": mean(values),
                **stats})
    return {"rows": rows, "groups": groups}


def fmt(value, digits=2):
    return "" if value is None else (f"{value:.{digits}f}" if isinstance(value, float) else str(value))

def _num(value, digits=2):
    """Rounded number or None — handoff.json keeps numeric types."""
    return round(value, digits) if value is not None else None


def write_aggregate_csv(rows: list[dict], path: Path) -> None:
    columns = ["model", "preset", "language", "dimension", "n", "mean_rating",
               "json_valid_rate", "tokens_mean", "latency_p50_ms",
               "latency_p95_ms", "cost_usd_mean"]
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=columns)
        writer.writeheader()
        for row in rows:
            out = dict(row)
            for field in ("mean_rating", "json_valid_rate", "tokens_mean",
                          "latency_p50_ms", "latency_p95_ms", "cost_usd_mean"):
                out[field] = fmt(row.get(field), 4 if field in ("json_valid_rate", "cost_usd_mean") else 2)
            writer.writerow(out)


def evaluate_d15(rows: list[dict]) -> dict:
    """D15 applied mechanically to the aggregate rows.

    Means use only the six dimensions scored everywhere (T25: factual_care is
    excluded). Rows are pooled across presets within (model, lang, dim) — D15
    is a per-language call, not per-preset. A mean whose constituent rows
    include a `†` (n < 2) marks the language's verdict not ready when it feeds
    the deciding comparison.
    """
    def dim_cells(model, lang, dim):
        return [row for row in rows
                if (row["model"], row["language"], row["dimension"])
                == (model, lang, dim)]

    def dim_mean(model, lang, dim):
        cells = dim_cells(model, lang, dim)
        vals = [c["mean_rating"] for c in cells if c["mean_rating"] is not None]
        return mean(vals), (min((c["n"] for c in cells), default=0))

    def lang_mean(model, lang):
        means, ns = [], []
        for d in DIMENSIONS:
            m, n = dim_mean(model, lang, d)
            if m is not None:
                means.append(m)
            ns.append(n)
        return mean(means), min(ns)

    result = {"languages": {}}
    ready = True
    for lang in sorted({r["language"] for r in rows}):
        luna_mean, luna_n = lang_mean(MODEL_LUNA, lang)
        sol_mean, sol_n = lang_mean(MODEL_SOL, lang)
        luna_nl_mean, luna_nl_n = dim_mean(MODEL_LUNA, lang, "natural_language")
        rule_a = (luna_mean is not None and sol_mean is not None
                  and luna_mean < sol_mean * SOL_MARGIN)
        rule_b = (lang == "vi" and luna_nl_mean is not None
                  and luna_nl_mean < VI_NL_FLOOR)
        # A deciding comparison fed by a `†` (n<2) mean blocks the verdict (T25).
        if rule_a and min(luna_n, sol_n) < 2:
            ready = False
        if rule_b and luna_nl_n < 2:
            ready = False
        if luna_mean is None or sol_mean is None:
            ready = False
        choice = MODEL_SOL if (rule_a or rule_b) else MODEL_LUNA
        rules = [r for r, fired in (("a", rule_a), ("b", rule_b)) if fired]
        result["languages"][lang] = {
            "luna_mean": luna_mean, "sol_mean": sol_mean,
            "sol_mean_x0_9": sol_mean * SOL_MARGIN if sol_mean is not None else None,
            "luna_natural_language": luna_nl_mean,
            "luna_n": luna_n, "sol_n": sol_n, "nl_n": luna_nl_n,
            "rule_a": rule_a, "rule_b": rule_b,
            "choice": choice, "applied_rule": "+".join(rules) if rules else "none"}
    result["verdict_ready"] = ready
    return result


def load_release(prompts_dir: Path) -> str:
    try:
        return json.loads((prompts_dir / "manifest.json").read_text(encoding="utf-8"))["release"]
    except (ValueError, KeyError, OSError):
        return "unfrozen"


def build_handoff(rows, decision, runs_dir: Path, length_table: dict,
                  release: str, evidence: str,
                  rater_mode: str = "native") -> dict:
    """The T2 §3 machine-readable twin of REPORT.md.

    SEC-0928b-2: `rater_source` records which rater population the verdict
    rests on (`native` humans or the two `ai` passes, 02 T106), so a consumer
    of handoff.json alone can tell AI-rated evidence from native-human
    evidence — the same thing REPORT.md's banner line says.

    CRV-0928c-1: calibration (`d4_calibration`) and the price bounds pool only
    `outcome == "ok"` + `json_valid is True` cells, while `d15.wi` counts every
    parsed rated-tier cell whose `json_valid` is false — a failed cell is
    exactly the case the first list must drop and the second must keep.
    """
    metrics = []
    all_metrics = []
    for cell_dir in sorted(runs_dir.iterdir()):
        # SEC-0928c-3: a symlinked cell dir, a dir resolving outside --runs-dir
        # or a metrics.json that is not a JSON object is never a cell record.
        if not cell_dir.is_dir() or cell_dir.is_symlink():
            continue
        m = read_metrics(runs_dir, cell_dir.name)
        if not m:
            continue
        all_metrics.append(m)
        # Only successful cells calibrate D4/price bounds; a failed cell
        # (body 0, retry latency) is disclosed in REPORT.md's invalid list.
        if m.get("outcome") == "ok" and m.get("json_valid") is True:
            metrics.append(m)

    tiers = {}
    for tier in ("short", "standard", "long"):
        tier_cells = [m for m in metrics if m.get("tier") == tier]
        if not tier_cells:
            continue
        tiers[tier] = {"tokens": {}, "writer_max_output_headroom": {}}
        for lang in ("vi", "en"):
            lang_cells = [m for m in tier_cells if m.get("language") == lang]
            bodies = [m["tokens"]["body_tokens"] for m in lang_cells
                      if (m.get("tokens") or {}).get("body_tokens") is not None]
            outs = [(m.get("usage") or {}).get("output_tokens") for m in lang_cells]
            outs = [o for o in outs if o is not None]
            tiers[tier]["tokens"][lang] = round(mean(bodies) or 0)
            cap = ((length_table.get(tier) or {}).get(lang) or {}).get("writer_max_output")
            if outs and cap:
                tiers[tier]["writer_max_output_headroom"][lang] = round(max(outs) / cap, 2)

    segments = [m["tokens"]["segments"] for m in metrics
                if (m.get("tokens") or {}).get("segments") is not None]
    cost = {}
    latency = {}
    for tier in ("short", "standard", "long"):
        for model in (MODEL_LUNA, MODEL_SOL):
            cells = [m for m in metrics if m.get("tier") == tier and m.get("model") == model]
            costs = [m["cost_usd"] for m in cells if m.get("cost_usd") is not None]
            lats = [m["latency_ms"] for m in cells if m.get("latency_ms") is not None]
            if costs:
                cost.setdefault(tier, {})[model] = round(mean(costs), 5)
            if lats:
                latency.setdefault(tier, {})[model] = {
                    "p50": int(percentile(lats, 50)), "p95": int(percentile(lats, 95))}

    langs = decision["languages"]
    d15 = {
        "luna_mean_vi": _num(langs.get("vi", {}).get("luna_mean")),
        "sol_mean_vi": _num(langs.get("vi", {}).get("sol_mean")),
        "sol_mean_x0_9_vi": _num(langs.get("vi", {}).get("sol_mean_x0_9"), 3),
        "luna_natural_language_vi": _num(langs.get("vi", {}).get("luna_natural_language")),
        "luna_mean_en": _num(langs.get("en", {}).get("luna_mean")),
        "sol_mean_en": _num(langs.get("en", {}).get("sol_mean")),
        # CRV-0928c-1: wi = every parsed rated-tier cell whose json_valid is
        # false (invalid/moderated) — a failed cell is exactly the case the
        # calibration list above drops, so it must be counted from the
        # unfiltered list, never from `metrics` (which would always give 0).
        "wi": sum(1 for m in all_metrics
                  if m.get("tier") == RATED_TIER and m.get("json_valid") is False),
        "verdict_ready": decision["verdict_ready"],
    }
    return {
        "model_choice": {
            "vi": langs.get("vi", {}).get("choice"),
            "en": langs.get("en", {}).get("choice"),
            "applied_rule": ",".join(
                sorted({l["applied_rule"] for l in langs.values()}
                       - {"none"})) or "none",
        },
        "d15": d15,
        "d4_calibration": {
            "words_per_minute_measured": None,
            "source": "01.3.1-T4 not landed (T26: accepted; D4 rates = T103 VieNeu-baseline rates)",
            "tiers": tiers,
            "segments_observed": {
                "min": min(segments) if segments else None,
                "max": max(segments) if segments else None,
                "median": int(statistics.median(segments)) if segments else None},
        },
        "price_bounds_12_1_1": {
            "cost_per_story_usd": cost,
            "latency_ms": latency,
            "rated_tier": RATED_TIER},
        # SEC-0928b-2: which population the d15 numbers rest on (02 T106).
        "rater_source": "ai" if rater_mode == "ai" else "native",
        "release": release,
        "evidence": evidence,
    }


def _blocking_rules(key: dict, runs_dir: Path | None, length_table: dict | None,
                    prompts_dir: Path | None) -> tuple[list[str], bool]:
    """CRV-0928c-4: blocking D3 rules of one key row's story, re-validated now.

    `metrics.json`'s `validator_issues` is only the run-time snapshot of the
    validator: a rule added since the run never shows up there, so T108 would
    keep calling a now-blocking cell clean. The cell's `story.json` is therefore
    re-validated with the current validator, mirroring `story_harness.run_cell`
    exactly — `sv.validate(story, tier, lang, limits)` with `limits` = the
    `--length-table` row `[tier][lang]` and, for a true_crime cell, the brief's
    own fact ids from the fixtures fact sheet. A missing or unparsable
    story.json (or fact sheet) falls back to the recorded issues.

    Returns `(rules, revalidated)`; `revalidated` is False on the fallback.
    """
    metrics = key.get("_metrics") or {}
    recorded = sorted({i.get("rule", "?")
                       for i in metrics.get("validator_issues") or []
                       if isinstance(i, dict) and i.get("severity") == "blocking"})
    if runs_dir is None or length_table is None or prompts_dir is None:
        return recorded, False
    tier = metrics.get("tier") or key.get("tier") or RATED_TIER
    lang = metrics.get("language") or key.get("language") or ""
    try:
        limits = dict((length_table.get(tier) or {}).get(lang) or {})
        if not limits:
            raise ValueError(f"no {tier}/{lang} row in {length_table}")
        preset = metrics.get("preset") or key.get("preset")
        brief_id = str(metrics.get("brief_id") or key.get("brief_id") or "")
        if preset == "true_crime" and brief_id and brief_id != "inline":
            fixture = prompts_dir / "fixtures" / "fact-sheet.true_crime.v1.json"
            sheet = json.loads(fixture.read_text(encoding="utf-8"))
            limits["fact_ids"] = [
                str(fact.get("id")) for fact in (sheet.get("facts") or [])
                if isinstance(fact, dict)
                and str(fact.get("brief_id")) == brief_id]
        story = json.loads((runs_dir / key["run_id"] / "story.json")
                           .read_text(encoding="utf-8"))
        issues = sv.blocking_issues(sv.validate(story, tier, lang, limits))
    except (ValueError, OSError, KeyError, TypeError, AttributeError,
            RecursionError):
        return recorded, False
    return sorted({i.get("rule", "?") for i in issues}), True


def write_report(path: Path, key_rows, scores, rows, decision, seed, release,
                 rater_mode: str = "native", unkeyed_failed: tuple = (),
                 runs_dir: Path | None = None, length_table: dict | None = None,
                 prompts_dir: Path | None = None) -> None:
    lines = []
    a = lines.append
    a(f"# 02.1.3 A/B report (r1)")
    a("")
    a(f"Run conditions: tier `{RATED_TIER}` · effort `medium` · seed `{seed}` "
      f"· models {MODEL_LUNA} vs {MODEL_SOL} · generated "
      f"{datetime.now(timezone.utc).date().isoformat()} · prompt release `{release}`")
    a("")
    a("## Matrix summary")
    a("")
    a("| story_id | run_id | outcome | json_valid | body_tokens | latency_ms | cost_usd |")
    a("|---|---|---|---|---|---|---|")
    for key in sorted(key_rows, key=lambda k: k["story_id"]):
        metrics = key.get("_metrics", {})
        tokens = (metrics.get("tokens") or {}).get("body_tokens")
        a(f"| {key['story_id']} | {key['run_id']} | {metrics.get('outcome','?')} "
          f"| {metrics.get('json_valid','?')} | {tokens if tokens is not None else ''} "
          f"| {metrics.get('latency_ms','')} | {fmt(metrics.get('cost_usd'),5)} |")
    a("")
    a("## Per-dimension means (n raters; `†` = n < 2)")
    a("")
    a("| language | dimension | luna | sol |")
    a("|---|---|---|---|")
    for lang in sorted({r["language"] for r in rows}):
        for dim in DIMENSIONS + [FACTUAL_CARE]:
            cells = [r for r in rows if r["language"] == lang and r["dimension"] == dim]
            if not cells:
                continue
            parts = {}
            for model in (MODEL_LUNA, MODEL_SOL):
                # Pooled across presets, the same way evaluate_d15 pools them.
                mine = [r for r in cells if r["model"] == model]
                vals = [r["mean_rating"] for r in mine if r["mean_rating"] is not None]
                if not mine:
                    continue
                n = min(r["n"] for r in mine)
                flag = "†" if n < 2 else ""
                parts[model] = f"{fmt(mean(vals))} (n={n}){flag}"
            a(f"| {lang} | {dim} | {parts.get(MODEL_LUNA,'')} | {parts.get(MODEL_SOL,'')} |")
    a("")
    a("## Rater coverage and invalid cells")
    a("")
    for key in sorted(key_rows, key=lambda k: k["story_id"]):
        story_rows = [r for r in scores if r["story_id"] == key["story_id"]]
        # SEC-0928b-1: the same `_rater_source` predicate `aggregate()` uses.
        native = {r["rater_id"] for r in story_rows
                  if _rater_source(r) == "native"}
        ai = {r["rater_id"] for r in story_rows
              if _rater_source(r) == "ai"}
        if rater_mode == "ai":
            # 02 T106: coverage counts the AI passes; the native count stays
            # printed so a later human spot-check is visible in the same file.
            flag = " †" if len(ai) < 2 else ""
            a(f"- {key['story_id']} ({key['run_id']}): {len(ai)} AI rater(s), "
              f"{len(native)} native rater(s){flag}")
        else:
            flag = " †" if len(native) < 2 else ""
            a(f"- {key['story_id']} ({key['run_id']}): {len(native)} native rater(s){flag}")
    invalid = [k["run_id"] for k in key_rows if k.get("_metrics", {}).get("json_valid") is False]
    # QA-0928c-6 + CRV-0928c-5: failed rated-tier cells not in key.csv are named
    # too, each with its own label ('superseded by a rerun' only when a keyed
    # rerun exists, otherwise 'failed, no rerun').
    invalid += list(unkeyed_failed)
    a(f"\nInvalid / moderated cells ({len(invalid)}): {', '.join(invalid) or 'none'}")
    # 02 T108: rated cells whose D3 validator found a blocking issue (the
    # outcome stays `ok`); never a hand-off candidate. CRV-0928c-4: the recorded
    # `validator_issues` are a run-time snapshot, so the cell is re-validated
    # from its story.json with the current rules; the record is the fallback.
    blocked = []
    for k in sorted(key_rows, key=lambda k: k["story_id"]):
        rules, revalidated = _blocking_rules(k, runs_dir, length_table, prompts_dir)
        if rules:
            entry = f"{k['story_id']} ({k['run_id']}: {', '.join(rules)})"
            blocked.append(entry if revalidated else entry + " (recorded)")
    a(f"\nRated cells with blocking validator issues ({len(blocked)}, 02 T108): "
      f"{'; '.join(blocked) or 'none'}")
    a("")
    if rater_mode == "ai":
        a("AI-rated (02 T106), not native-human evidence")
        a("")
    a("## D15 decision")
    a("")
    for lang in sorted(decision["languages"]):
        d = decision["languages"][lang]
        a(f"- `{lang}`: luna_mean={fmt(d['luna_mean'],3)} · sol_mean={fmt(d['sol_mean'],3)} "
          f"· sol_mean*0.9={fmt(d['sol_mean_x0_9'],3)} "
          f"· luna natural_language={fmt(d['luna_natural_language'],3)} (vi floor {VI_NL_FLOOR}) "
          f"· rule (a)={'fired' if d['rule_a'] else 'no'} (b)={'fired' if d['rule_b'] else 'no'} "
          f"→ **{d['choice']}** ({d['applied_rule']})")
    a("")
    if decision["verdict_ready"]:
        for lang in sorted(decision["languages"]):
            a(f"**verdict {lang}: {decision['languages'][lang]['choice']}**")
    else:
        a("**DECISION NOT READY — rater missing**")
    a("")
    path.write_text("\n".join(lines), encoding="utf-8")


def selftest() -> int:
    with tempfile.TemporaryDirectory() as tmp:
        runs_dir = Path(tmp) / "trial" / "stories"
        ab_dir = Path(tmp) / "trial" / "ab" / "r1"
        ab_dir.mkdir(parents=True)

        cells = [
            # (run_id, model, preset, brief, body_tokens, latency, cost, json_valid)
            ("vi-ghost-b1-luna-standard", MODEL_LUNA, "ghost", "b1", 800, 10000, 0.001, True),
            ("vi-ghost-b1-sol-standard", MODEL_SOL, "ghost", "b1", 820, 20000, 0.020, True),
            ("vi-true_crime-b7-luna-standard", MODEL_LUNA, "true_crime", "b7", 900, 11000, 0.002, True),
        ]
        for run_id, model, preset, brief, toks, lat, cost, valid in cells:
            d = runs_dir / run_id
            d.mkdir(parents=True)
            (d / "metrics.json").write_text(json.dumps({
                "run_id": run_id, "model": model, "tier": "standard",
                "language": "vi", "preset": preset, "brief_id": brief,
                "outcome": "ok", "json_valid": valid, "latency_ms": lat,
                "cost_usd": cost,
                "usage": {"output_tokens": toks + 100},
                "tokens": {"body_tokens": toks, "segments": 20, "keywords_total": 30},
            }))
        # A failed, unkeyed rated-tier cell: REPORT.md discloses it as failed
        # (no keyed rerun exists in this fixture), it is never pooled into
        # handoff calibration (body 0, huge latency and cost would skew it) and
        # it is the single cell handoff.d15.wi counts (CRV-0928c-1).
        bad = runs_dir / "vi-ghost-b2-luna-standard"
        bad.mkdir()
        (bad / "metrics.json").write_text(json.dumps({
            "run_id": bad.name, "model": MODEL_LUNA, "tier": "standard", "language": "vi",
            "outcome": "invalid_output", "json_valid": False, "latency_ms": 999999,
            "cost_usd": 9.0, "usage": {"output_tokens": 5000},
            "tokens": {"body_tokens": 0, "segments": 0}}))

        # CRV-0928c-4: T108 re-validates each rated cell's story.json with the
        # current validator; a cell with no story.json falls back to its
        # recorded validator_issues, marked '(recorded)'. S01 gets a recorded
        # blocking issue (its story.json is absent), S02 a story.json the
        # current validator itself blocks on title length.
        s01_metrics_path = runs_dir / cells[0][0] / "metrics.json"
        s01_metrics = json.loads(s01_metrics_path.read_text(encoding="utf-8"))
        s01_metrics["validator_issues"] = [
            {"rule": "meta_narration", "severity": "blocking", "segment": "p:0",
             "evidence": "pipeline term"}]
        s01_metrics_path.write_text(json.dumps(s01_metrics), encoding="utf-8")
        (runs_dir / cells[1][0] / "story.json").write_text(
            json.dumps({"title": "x" * 101}), encoding="utf-8")

        # key.csv + scores.csv: two raters on two stories, ONE rater on the third
        # (the † case). factual_care only for the true_crime story.
        with (ab_dir / "key.csv").open("w", newline="") as h:
            h.write("# seed=7\n")
            w = csv.writer(h)
            w.writerow(["story_id", "run_id", "model", "preset", "language", "brief_id", "tier"])
            w.writerow(["S01", cells[0][0], MODEL_LUNA, "ghost", "vi", "b1", "standard"])
            w.writerow(["S02", cells[1][0], MODEL_SOL, "ghost", "vi", "b1", "standard"])
            w.writerow(["S03", cells[2][0], MODEL_LUNA, "true_crime", "vi", "b7", "standard"])
        with (ab_dir / "scores.csv").open("w", newline="") as h:
            w = csv.writer(h)
            w.writerow(["story_id", "rater_id", "rater_language", "rater_native",
                        "dimension", "score", "notes", "scored_at"])
            for story_id, hook_score in (("S01", 4), ("S02", 5)):
                for rater in ("r-vi-1", "r-vi-2"):
                    for dim in DIMENSIONS:
                        w.writerow([story_id, rater, "vi", "true", dim,
                                    hook_score if dim == "hook" else 4, "", "2026-09-26"])
            # S03 single rater → every dim n=1 (†); factual_care scored too.
            # Pooled luna vi mean = (ghost 4s + tc 3s)/2 = 3.5 < sol*0.9 = 3.75
            # → rule (a) fires on a † mean → verdict not ready.
            for dim in DIMENSIONS:
                w.writerow(["S03", "r-vi-1", "vi", "true", dim, 3, "", "2026-09-26"])
            w.writerow(["S03", "r-vi-1", "vi", "true", FACTUAL_CARE, 1, "", "2026-09-26"])

        out = Path(tmp) / "out"
        out.mkdir()
        code = main(["--ab-dir", str(ab_dir), "--runs-dir", str(runs_dir),
                     "--out", str(out), "--handoff"])
        assert code == 0, f"aggregate run failed ({code})"

        agg_rows = list(csv.DictReader((out / "aggregate.csv").open()))
        hook_luna = [r for r in agg_rows
                     if r["model"] == MODEL_LUNA and r["preset"] == "ghost"
                     and r["dimension"] == "hook"]
        assert len(hook_luna) == 1
        assert hook_luna[0]["n"] == "2" and float(hook_luna[0]["mean_rating"]) == 4.0, hook_luna
        tc_dims = {r["dimension"] for r in agg_rows if r["preset"] == "true_crime"}
        ghost_dims = {r["dimension"] for r in agg_rows if r["preset"] == "ghost"}
        assert FACTUAL_CARE in tc_dims and FACTUAL_CARE not in ghost_dims
        tc_hook = [r for r in agg_rows if r["preset"] == "true_crime"
                   and r["dimension"] == "hook"][0]
        assert tc_hook["n"] == "1", "missing-rater row must keep n=1"
        report = (out / "REPORT.md").read_text()
        assert "†" in report, "n=1 must be flagged in REPORT.md"
        assert "DECISION NOT READY" in report, "† mean deciding must block the verdict"
        # CRV-0928c-4: T108 uses the current validator on story.json — S02's
        # one-line story blocks on title length — and the recorded issues only
        # as the '(recorded)' fallback when story.json is absent (S01).
        assert ("S01 (vi-ghost-b1-luna-standard: meta_narration) (recorded); "
                "S02 (vi-ghost-b1-sol-standard: title_too_long)") in report, report
        # The per-dimension table pools a model's presets exactly like
        # evaluate_d15: luna vi hook = mean(ghost 4.0, true_crime 3.0), min n = 1.
        assert "| vi | hook | 3.50 (n=1)† | 5.00 (n=2) |" in report, "per-dim table must pool presets"
        # QA-0928c-3: two stories in one cell, one loses a rater -> n=1, not 2.
        two = [{"story_id": "A", "model": MODEL_LUNA, "preset": "ghost", "language": "vi", "run_id": "a"},
               {"story_id": "B", "model": MODEL_LUNA, "preset": "ghost", "language": "vi", "run_id": "b"}]
        partial = [{"story_id": s, "rater_id": r, "rater_native": "true", "dimension": "hook", "score": "4"}
                   for s, r in (("A", "r-vi-1"), ("A", "r-vi-2"), ("B", "r-vi-1"))]
        hook = [r for r in aggregate(two, partial, runs_dir)["rows"] if r["dimension"] == "hook"][0]
        assert hook["n"] == 1, hook

        # factual_care exclusion: pooled luna vi mean over the six dims is
        # mean(4, 3) = 3.5; folding in factual_care (score 1) would give ~3.14.
        handoff = json.loads((Path(tmp) / "trial" / "ab" / "handoff.json").read_text())
        assert set(handoff) == {"model_choice", "d15", "d4_calibration",
                                "price_bounds_12_1_1", "rater_source", "release",
                                "evidence"}
        # SEC-0928b-2: the machine-readable twin records which population the
        # verdict rests on; this run is the default native mode.
        assert handoff["rater_source"] == "native", handoff["rater_source"]
        assert handoff["d4_calibration"]["words_per_minute_measured"] is None
        assert "source" in handoff["d4_calibration"]
        cps = handoff["price_bounds_12_1_1"]["cost_per_story_usd"]
        assert cps["standard"][MODEL_LUNA] == 0.0015, cps  # mean(0.001, 0.002); failed cell excluded
        assert handoff["d4_calibration"]["segments_observed"]["min"] == 20, handoff["d4_calibration"]
        assert "vi-ghost-b2-luna-standard (failed, no rerun)" in report, \
            "an unkeyed failed cell with no keyed rerun must be disclosed as failed"
        # CRV-0928c-1: wi counts the failed unkeyed rated-tier cell too (its
        # json_valid is false), even though the calibration list keeps only the
        # ok+json_valid cells — the fixture holds exactly one.
        assert handoff["d15"]["wi"] == 1, handoff["d15"]
        assert float(handoff["d15"]["luna_mean_vi"]) == 3.5, handoff["d15"]
        assert handoff["d15"]["verdict_ready"] is False      # the † mean decides (a)

        # d15_verdict_selftest: four synthetic aggregates.
        def rows_for(luna, sol, nl_luna, n=2):
            out = []
            for lang, lm, sm in (("vi", luna, sol), ("en", 4.0, 4.1)):
                for model, m in ((MODEL_LUNA, lm), (MODEL_SOL, sm)):
                    for dim in DIMENSIONS:
                        val = (nl_luna if (model == MODEL_LUNA and lang == "vi"
                                           and dim == "natural_language") else m)
                        out.append({"model": model, "language": lang,
                                    "dimension": dim, "n": n, "mean_rating": val})
            return out

        r = evaluate_d15(rows_for(3.84, 4.0, 3.84))          # (a) luna 4% below
        assert r["verdict_ready"] and r["languages"]["vi"]["choice"] == MODEL_LUNA
        r = evaluate_d15(rows_for(3.56, 4.0, 3.56))          # (b) 11% below
        assert r["languages"]["vi"]["choice"] == MODEL_SOL
        assert r["languages"]["vi"]["applied_rule"] == "a"
        r = evaluate_d15(rows_for(3.9, 4.0, 3.4))            # (c) vi nl < 3.5
        assert r["languages"]["vi"]["applied_rule"] == "b"
        assert r["languages"]["en"]["choice"] == MODEL_LUNA  # sol for vi only
        r = evaluate_d15(rows_for(3.56, 4.0, 3.56, n=1))     # (d) † decides
        assert r["verdict_ready"] is False

        # QA D3/D4 regression: an out-of-range, non-numeric or non-finite
        # score, an unknown dimension and a header missing a column are each a
        # named exit-2 input error — never a silently skewed mean or a
        # traceback, and no aggregate.csv is written.
        base_key = (ab_dir / "key.csv").read_text(encoding="utf-8")
        base_scores = (ab_dir / "scores.csv").read_text(encoding="utf-8")
        cases = [("range", "7"), ("nonnumeric", "nan!"), ("nonfinite", "nan"),
                 ("dim", "4")]          # "dim" rewrites the dimension column
        for label, bad in cases:
            case = Path(tmp) / f"bad-{label}"
            (case / "ab").mkdir(parents=True)
            (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
            row = ("S01,r-vi-1,vi,true,hookx,4,,2026-09-26\n" if label == "dim"
                   else f"S01,r-vi-1,vi,true,hook,{bad},,2026-09-26\n")
            (case / "ab" / "scores.csv").write_text(
                base_scores + row, encoding="utf-8")
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                code = main(["--ab-dir", str(case / "ab"),
                             "--runs-dir", str(runs_dir),
                             "--out", str(case / "out")])
            message = err.getvalue()
            assert code == 2, f"{label}: want exit 2, got {code}"
            assert "Traceback" not in message, message
            expected = "hookx" if label == "dim" else bad
            assert expected in message, f"{label}: value not named: {message}"
            assert not (case / "out" / "aggregate.csv").is_file(), label

        # Same for a scores.csv whose header dropped a required column.
        case = Path(tmp) / "bad-header"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        headerless = "rater_id,rater_language,rater_native,dimension," \
                     "score,notes,scored_at\n"
        (case / "ab" / "scores.csv").write_text(headerless, encoding="utf-8")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(["--ab-dir", str(case / "ab"),
                         "--runs-dir", str(runs_dir),
                         "--out", str(case / "out")])
        assert code == 2 and "story_id" in err.getvalue(), err.getvalue()

        # A score row whose story_id is absent from key.csv used to be dropped
        # silently (a rater typo or a stale key file shrank the sample); it is
        # now a named exit-2 input error and no output is written.
        case = Path(tmp) / "bad-storyid"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(
            base_scores + "S99,r-vi-1,vi,true,hook,4,,2026-09-26\n", encoding="utf-8")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(["--ab-dir", str(case / "ab"),
                         "--runs-dir", str(runs_dir),
                         "--out", str(case / "out")])
        message = err.getvalue()
        assert code == 2, f"unknown story_id: want exit 2, got {code}"
        assert "Traceback" not in message, message
        assert "'S99'" in message and "key.csv" in message, message
        assert not (case / "out" / "aggregate.csv").is_file(), "S99"

        # CRV-0928c-5: an unkeyed failed rated-tier cell is 'superseded by a
        # rerun' only when key.csv names its `<cell>-rN` rerun; without one it
        # is just a failed cell. Both labels are asserted in a scenario of
        # their own, so the numbers above stay untouched.
        sup_runs = Path(tmp) / "sup-stories"
        for name, ok in (("vi-ghost-b2-luna-standard", False),
                         ("vi-ghost-b3-luna-standard", False),
                         ("vi-ghost-b2-luna-standard-r2", True)):
            cell_dir = sup_runs / name
            cell_dir.mkdir(parents=True)
            (cell_dir / "metrics.json").write_text(json.dumps({
                "run_id": name, "model": MODEL_LUNA, "tier": "standard",
                "language": "vi", "preset": "ghost", "brief_id": "b2",
                "outcome": "ok" if ok else "invalid_output", "json_valid": ok,
                "latency_ms": 10000, "cost_usd": 0.001,
                "tokens": {"body_tokens": 800, "segments": 20}}))
        sup_ab = Path(tmp) / "sup-ab"
        sup_ab.mkdir()
        with (sup_ab / "key.csv").open("w", newline="") as h:
            h.write("# seed=7\n")
            w = csv.writer(h)
            w.writerow(["story_id", "run_id", "model", "preset", "language",
                        "brief_id", "tier"])
            for story_id, run_id in (("S01", cells[0][0]), ("S02", cells[1][0]),
                                     ("S03", cells[2][0]),
                                     ("S04", "vi-ghost-b2-luna-standard-r2")):
                w.writerow([story_id, run_id, MODEL_LUNA, "ghost", "vi",
                            "b1", "standard"])
        (sup_ab / "scores.csv").write_text(base_scores, encoding="utf-8")
        code = main(["--ab-dir", str(sup_ab), "--runs-dir", str(sup_runs),
                     "--out", str(Path(tmp) / "sup-out")])
        assert code == 0, f"superseded label scenario: want exit 0, got {code}"
        sup_report = (Path(tmp) / "sup-out" / "REPORT.md").read_text()
        assert "vi-ghost-b2-luna-standard (superseded by a rerun)" in sup_report, \
            sup_report
        assert "vi-ghost-b3-luna-standard (failed, no rerun)" in sup_report, \
            sup_report

        # SEC-0928c-3: the key.csv run_id is a path component under --runs-dir,
        # so a traversal (or any non-cell id) is a named exit-2 input error.
        case = Path(tmp) / "bad-runid"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(
            base_key.replace("vi-true_crime-b7-luna-standard", "../../escape"),
            encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(base_scores, encoding="utf-8")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(["--ab-dir", str(case / "ab"),
                         "--runs-dir", str(runs_dir), "--out", str(case / "out")])
        message = err.getvalue()
        assert code == 2, f"traversal run_id: want exit 2, got {code} — {message}"
        assert "Traceback" not in message, message
        assert "../../escape" in message, message
        assert not (case / "out" / "aggregate.csv").is_file(), "bad-runid"

        # SEC-0928c-3: a metrics.json that is not a JSON object (a list) and a
        # cell dir reached through a symlink are not metrics records — no
        # AttributeError, no traceback, and neither counts toward wi.
        list_runs = Path(tmp) / "list-stories"
        listed = list_runs / "vi-ghost-b1-luna-standard"
        listed.mkdir(parents=True)
        (listed / "metrics.json").write_text("[1, 2, 3]", encoding="utf-8")
        outside = Path(tmp) / "outside" / "vi-ghost-b3-luna-standard"
        outside.mkdir(parents=True)
        (outside / "metrics.json").write_text(json.dumps({
            "run_id": outside.name, "tier": "standard", "language": "vi",
            "outcome": "invalid_output", "json_valid": False}))
        (list_runs / "vi-ghost-b3-luna-standard").symlink_to(
            outside, target_is_directory=True)
        case = Path(tmp) / "bad-metrics"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(base_scores, encoding="utf-8")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(["--ab-dir", str(case / "ab"),
                         "--runs-dir", str(list_runs), "--out", str(case / "out"),
                         "--handoff"])
        assert code == 0, f"list metrics: want exit 0, got {code} — {err.getvalue()}"
        assert "Traceback" not in err.getvalue(), err.getvalue()
        assert json.loads((case / "handoff.json").read_text())["d15"]["wi"] == 0, \
            "a list-valued or symlinked metrics.json is not a cell record"
        # 02 T106 cases: --rater-mode ai counts the ai-* passes; a non-ai
        # rater_id with rater_native=false is a named exit-2 input error; in
        # native mode ai-* rows never inflate the human n (NOT READY).
        def ai_scores(pairs):
            """`pairs`: story_id -> list of (rater_id, hook_score, other_score)."""
            buf = io.StringIO()
            w = csv.writer(buf)
            w.writerow(["story_id", "rater_id", "rater_language", "rater_native",
                        "dimension", "score", "notes", "scored_at"])
            for story_id, raters in pairs.items():
                for rater, hook_score, other in raters:
                    for dim in DIMENSIONS:
                        w.writerow([story_id, rater, "vi", "false", dim,
                                    hook_score if dim == "hook" else other,
                                    "AI: pass 1", "2026-09-28"])
                    if story_id == "S03":
                        w.writerow([story_id, rater, "vi", "false",
                                    FACTUAL_CARE, other, "AI: pass 1",
                                    "2026-09-28"])
            return buf.getvalue()

        case = Path(tmp) / "ai-2raters"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(ai_scores({
            story: [("ai-vi-1", 4, 4), ("ai-vi-2", 4, 4)]
            for story in ("S01", "S02", "S03")}), encoding="utf-8")
        code = main(["--ab-dir", str(case / "ab"), "--runs-dir", str(runs_dir),
                     "--out", str(case / "out"), "--rater-mode", "ai",
                     "--handoff"])
        assert code == 0, f"ai mode: want exit 0, got {code}"
        report = (case / "out" / "REPORT.md").read_text()
        banner = "AI-rated (02 T106), not native-human evidence"
        assert banner in report, "ai mode must carry the T106 banner"
        assert report.index(banner) < report.index("## D15 decision"), \
            "banner must sit above the D15 section"
        assert "**verdict vi:" in report, report
        assert "DECISION NOT READY" not in report
        assert "2 AI rater(s), 0 native rater(s)" in report
        # SEC-0928b-2: handoff.json alone must mark the evidence as AI-rated.
        ai_handoff = json.loads((case / "handoff.json").read_text())
        assert ai_handoff["rater_source"] == "ai", ai_handoff["rater_source"]
        ai_hook = [r for r in csv.DictReader((case / "out" / "aggregate.csv").open())
                   if r["model"] == MODEL_LUNA and r["preset"] == "ghost"
                   and r["dimension"] == "hook"]
        assert ai_hook[0]["n"] == "2", ai_hook  # the two ai-* raters, not humans

        # ai mode, a single AI pass: n=1 marks `†`; with S03 at 3 vs S02 at 5,
        # luna pools to 3.5 < sol*0.9 → rule (a) fires on the † mean.
        case = Path(tmp) / "ai-1rater"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(ai_scores({
            "S01": [("ai-vi-1", 4, 4)],
            "S02": [("ai-vi-1", 5, 5)],
            "S03": [("ai-vi-1", 3, 3)]}), encoding="utf-8")
        code = main(["--ab-dir", str(case / "ab"), "--runs-dir", str(runs_dir),
                     "--out", str(case / "out"), "--rater-mode", "ai"])
        assert code == 0, f"ai mode 1-rater: want exit 0, got {code}"
        report = (case / "out" / "REPORT.md").read_text()
        assert "†" in report, "n=1 AI pass must be flagged"
        assert "DECISION NOT READY" in report
        assert "1 AI rater(s), 0 native rater(s) †" in report, report

        # native mode on ai-only scores: the ai-* rows are ignored for
        # coverage/n, so 0 human raters → † → the verdict stays not ready.
        case = Path(tmp) / "ai-native-mode"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(ai_scores({
            story: [("ai-vi-1", 4, 4), ("ai-vi-2", 4, 4)]
            for story in ("S01", "S02", "S03")}), encoding="utf-8")
        code = main(["--ab-dir", str(case / "ab"), "--runs-dir", str(runs_dir),
                     "--out", str(case / "out")])
        assert code == 0, f"native mode on ai rows: want exit 0, got {code}"
        report = (case / "out" / "REPORT.md").read_text()
        assert banner not in report, "native mode never prints the T106 banner"
        assert "0 native rater(s) †" in report, report
        assert "DECISION NOT READY" in report, \
            "ai-* rows must not satisfy the native n=2 (02 T106)"
        nat_hook = [r for r in csv.DictReader((case / "out" / "aggregate.csv").open())
                    if r["model"] == MODEL_LUNA and r["preset"] == "ghost"
                    and r["dimension"] == "hook"]
        assert nat_hook[0]["n"] == "0", nat_hook

        # ai mode rejects a rater_native=false row whose id is not ai-*
        # (neither a native human nor an AI pass — 02 T106 / T100 shape).
        case = Path(tmp) / "ai-bad-rater"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(
            ai_scores({"S01": [("ai-vi-1", 4, 4), ("ai-vi-2", 4, 4)],
                       "S02": [("ai-vi-1", 5, 5), ("ai-vi-2", 5, 5)],
                       "S03": [("ai-vi-1", 3, 3), ("ai-vi-2", 3, 3)]})
            + "S01,r-vi-3,vi,false,hook,4,,2026-09-28\n", encoding="utf-8")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(["--ab-dir", str(case / "ab"),
                         "--runs-dir", str(runs_dir),
                         "--out", str(case / "out"), "--rater-mode", "ai"])
        message = err.getvalue()
        assert code == 2, f"bad ai rater: want exit 2, got {code}"
        assert "Traceback" not in message, message
        assert "r-vi-3" in message and "rater_native" in message, message
        assert not (case / "out" / "aggregate.csv").is_file(), "ai-bad-rater"

        # SEC-0928b-1: the disagreement rule holds in BOTH modes. Before the
        # fix, a `rater_native=false` row with a non-`ai-` id (a mislabelled AI
        # pass such as 'claude-vi-1') was rejected only under --rater-mode ai,
        # so the default native run counted it toward the human n/means while
        # REPORT.md coverage (rater_native == 'true') showed 0 native raters.
        for label, mode in (("native-bad-rater", None),
                            ("ai-bad-rater-native", None),
                            ("ai-bad-rater-ai", "ai")):
            row = ("S01,ai-vi-9,vi,true,hook,4,,2026-09-28\n"
                   if label == "ai-bad-rater-native" else
                   "S01,r-vi-3,vi,false,hook,4,,2026-09-28\n")
            case = Path(tmp) / label
            (case / "ab").mkdir(parents=True)
            (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
            (case / "ab" / "scores.csv").write_text(
                base_scores + row, encoding="utf-8")
            err = io.StringIO()
            argv = ["--ab-dir", str(case / "ab"), "--runs-dir", str(runs_dir),
                    "--out", str(case / "out")]
            if mode:
                argv += ["--rater-mode", mode]
            with contextlib.redirect_stderr(err):
                code = main(argv)
            message = err.getvalue()
            assert code == 2, f"{label}: want exit 2, got {code} — {message}"
            assert "Traceback" not in message, message
            offender = "ai-vi-9" if label == "ai-bad-rater-native" else "r-vi-3"
            assert offender in message and "rater_native" in message, message
            assert not (case / "out" / "aggregate.csv").is_file(), label

        # SEC-0928b-1: coverage and means use one predicate. An `ai-*` row with
        # rater_native=true (a half-labelled AI pass) is the mirror case: it is
        # rejected in ai mode too, instead of joining the AI means while
        # coverage counted it as a native human.
        case = Path(tmp) / "ai-native-flag-ai-mode"
        (case / "ab").mkdir(parents=True)
        (case / "ab" / "key.csv").write_text(base_key, encoding="utf-8")
        (case / "ab" / "scores.csv").write_text(
            ai_scores({"S01": [("ai-vi-1", 4, 4), ("ai-vi-2", 4, 4)]})
            + "S02,ai-vi-9,vi,true,hook,4,,2026-09-28\n", encoding="utf-8")
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(["--ab-dir", str(case / "ab"),
                         "--runs-dir", str(runs_dir),
                         "--out", str(case / "out"), "--rater-mode", "ai"])
        message = err.getvalue()
        assert code == 2, f"ai-native-flag ai mode: want exit 2, got {code}"
        assert "Traceback" not in message, message
        assert "ai-vi-9" in message and "rater_native" in message, message
        assert not (case / "out" / "aggregate.csv").is_file()
    print("selftest ok")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Aggregate blind scores (trial/ab/<round>) into aggregate.csv + REPORT.md.")
    parser.add_argument("--ab-dir", default="trial/ab/r1",
                        help="directory holding key.csv + scores.csv (default trial/ab/r1)")
    parser.add_argument("--runs-dir", default="trial/stories",
                        help="harness run dirs for metrics.json (default trial/stories)")
    parser.add_argument("--prompts-dir", default="prompts")
    parser.add_argument("--out", default=None,
                        help="output dir for aggregate.csv/REPORT.md (default --ab-dir)")
    parser.add_argument("--length-table", default=str(DEFAULT_LENGTH_TABLE),
                        help="LENGTH.v1.json for writer_max_output headroom")
    parser.add_argument("--handoff", action="store_true",
                        help="also write trial/ab/handoff.json (T2 §3)")
    parser.add_argument("--selftest", action="store_true", help="run the offline self-test")
    parser.add_argument("--rater-mode", choices=("native", "ai"), default="native",
                        help="which raters count for coverage/n/† (02 T106): "
                             "'native' = human raters (default, unchanged); "
                             "'ai' = the two AI passes with rater_id ai-<lang>-<n> "
                             "and rater_native=false — REPORT.md then carries the "
                             "'AI-rated' banner above the D15 section. A row whose "
                             "rater_native disagrees with its rater_id exits 2 in "
                             "either mode (SEC-0928b-1); handoff.json records the "
                             "mode as rater_source (SEC-0928b-2)")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    ab_dir = Path(args.ab_dir)
    runs_dir = Path(args.runs_dir)
    out_dir = Path(args.out) if args.out else ab_dir
    for needed in ("key.csv", "scores.csv"):
        if not (ab_dir / needed).is_file():
            print(f"error: {ab_dir / needed} missing — scoring not filed", file=sys.stderr)
            return 2
    if not runs_dir.is_dir():
        print(f"error: --runs-dir {runs_dir} does not exist", file=sys.stderr)
        return 2
    try:
        length_table = json.loads(Path(args.length_table).read_text(encoding="utf-8"))
    except (ValueError, OSError) as exc:
        print(f"error: --length-table unreadable: {exc}", file=sys.stderr)
        return 2

    try:
        seed, key_rows = read_key(ab_dir / "key.csv")
        for key in key_rows:
            key["_metrics"] = read_metrics(runs_dir, key["run_id"])
        scores = read_scores(ab_dir / "scores.csv")
        rated = {k["run_id"] for k in key_rows}
        # CRV-0928c-5: an unkeyed failed rated-tier cell is only a rerun's
        # predecessor when key.csv names its `<cell>-rN` rerun; without one it
        # is simply a failed cell and must not claim to have been replaced.
        unkeyed_failed = [
            f"{cell.name} (" + ("superseded by a rerun" if any(
                rid.startswith(cell.name + "-r") for rid in rated)
                else "failed, no rerun") + ")"
            for cell in sorted(runs_dir.iterdir())
            if cell.is_dir() and not cell.is_symlink()
            and cell.name not in rated
            and (m := read_metrics(runs_dir, cell.name)).get("tier") == RATED_TIER
            and m.get("outcome") not in (None, "ok")]
        agg = aggregate(key_rows, scores, runs_dir, args.rater_mode)
    except InputError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    decision = evaluate_d15(agg["rows"])
    release = load_release(Path(args.prompts_dir))

    out_dir.mkdir(parents=True, exist_ok=True)
    write_aggregate_csv(agg["rows"], out_dir / "aggregate.csv")
    write_report(out_dir / "REPORT.md", key_rows, scores, agg["rows"],
                 decision, seed, release, args.rater_mode, unkeyed_failed,
                 runs_dir=runs_dir, length_table=length_table,
                 prompts_dir=Path(args.prompts_dir))
    if args.handoff:
        handoff = build_handoff(agg["rows"], decision, runs_dir, length_table,
                                release, (out_dir / "REPORT.md").as_posix(),
                                args.rater_mode)
        handoff_path = ab_dir.parent / "handoff.json"
        handoff_path.write_text(json.dumps(handoff, indent=2, ensure_ascii=False) + "\n",
                                encoding="utf-8")
        print(f"handoff -> {handoff_path}")

    for lang in sorted(decision["languages"]):
        d = decision["languages"][lang]
        print(f"D15 {lang}: luna {fmt(d['luna_mean'],3)} vs sol {fmt(d['sol_mean'],3)} "
              f"→ {d['choice']} ({d['applied_rule']})")
    if not decision["verdict_ready"]:
        print("DECISION NOT READY — rater missing")
    return 0


if __name__ == "__main__":
    sys.exit(main())
