"""Aggregate blind scores into `aggregate.csv`, `REPORT.md`, `handoff.json`
(02.1.3-T1 §6 + the handoff extension from 02.1.3-T2 §3).

Joins `scores.csv` with `key.csv` — the only unblinding step, run after all
scoring is filed — and prints the D15 test with the numbers. `n` is never
folded: a mean built on fewer than 2 raters is marked `†`, and if a `†` mean
feeds a deciding comparison the report prints `DECISION NOT READY`.

Exit codes (01 D9): 0 ok · 1 a check failed · 2 usage / environment error.
Stdlib only: `uv run --no-project --python 3.12 trial/tools/aggregate_scores.py`.
"""

from __future__ import annotations

import argparse
import contextlib
import csv
import io
import json
import math
import statistics
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

MODEL_LUNA = "gpt-6-luna"
MODEL_SOL = "gpt-6-sol"
DIMENSIONS = ["hook", "pacing", "atmosphere", "natural_language",
              "originality", "length_fit"]
FACTUAL_CARE = "factual_care"          # scored, never folded into D15 (T25)
RATED_TIER = "standard"
VI_NL_FLOOR = 3.5                      # D15 (b)
SOL_MARGIN = 0.9                       # D15 (a): luna < sol * 0.9 -> sol
DEFAULT_LENGTH_TABLE = Path(__file__).with_name("LENGTH.v1.json")

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
        rows.append(dict(zip(header, cells)))
    return seed, rows


def read_scores(path: Path) -> list[dict]:
    """scores.csv rows with a filed score.

    A blank `score` is an unrated row and is skipped, but a non-numeric score,
    a score outside the rubric range 1-5, an unknown dimension or a row whose
    shape does not match the header is never accepted silently: InputError
    with file:line, exit 2 (QA D3/D4; 01 D9 says never a traceback).
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
            row["_line"] = reader.line_num
            rows.append(row)
    if problems:
        raise InputError(f"{len(problems)} bad score row(s) in {path}: "
                         + "; ".join(problems))
    return rows

def read_metrics(runs_dir: Path, run_id: str) -> dict:
    try:
        return json.loads((runs_dir / run_id / "metrics.json").read_text(encoding="utf-8"))
    except (ValueError, OSError):
        return {}


def percentile(values: list[float], p: float) -> float:
    """Nearest-rank percentile."""
    ordered = sorted(values)
    index = max(1, math.ceil(p / 100.0 * len(ordered))) - 1
    return ordered[index]


def mean(values: list[float]) -> float | None:
    return statistics.fmean(values) if values else None


def aggregate(key_rows: list[dict], scores: list[dict], runs_dir: Path) -> dict:
    """Build the grouped aggregate: one row per (model, preset, language,
    dimension). `n` = distinct raters that scored that row.

    A score row whose `story_id` is absent from `key.csv` is a named
    InputError (exit 2), never a silent drop: a rater typo or a stale key file
    would otherwise shrink the sample unnoticed.
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
            rows.append({
                "model": model, "preset": preset, "language": language,
                "dimension": dim,
                "n": len({r["rater_id"] for r in scored}),
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
                  release: str, evidence: str) -> dict:
    """The T2 §3 machine-readable twin of REPORT.md."""
    metrics = []
    for cell_dir in sorted(runs_dir.iterdir()):
        metrics_path = cell_dir / "metrics.json"
        if cell_dir.is_dir() and metrics_path.is_file():
            try:
                metrics.append(json.loads(metrics_path.read_text(encoding="utf-8")))
            except (ValueError, OSError):
                continue

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
        # wi = rated cells that are not json_valid (invalid/moderated count).
        "wi": sum(1 for m in metrics
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
        "release": release,
        "evidence": evidence,
    }


def write_report(path: Path, key_rows, scores, rows, decision, seed, release) -> None:
    key_by_story = {k["story_id"]: k for k in key_rows}
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
            for r in cells:
                flag = "†" if r["n"] < 2 else ""
                parts[r["model"]] = f"{fmt(r['mean_rating'])} (n={r['n']}){flag}"
            a(f"| {lang} | {dim} | {parts.get(MODEL_LUNA,'')} | {parts.get(MODEL_SOL,'')} |")
    a("")
    a("## Rater coverage and invalid cells")
    a("")
    for key in sorted(key_rows, key=lambda k: k["story_id"]):
        native = {r["rater_id"] for r in scores
                  if r["story_id"] == key["story_id"] and r.get("rater_native") == "true"}
        flag = " †" if len(native) < 2 else ""
        a(f"- {key['story_id']} ({key['run_id']}): {len(native)} native rater(s){flag}")
    invalid = [k["run_id"] for k in key_rows if k.get("_metrics", {}).get("json_valid") is False]
    a(f"\nInvalid / moderated cells ({len(invalid)}): {', '.join(invalid) or 'none'}")
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

        # factual_care exclusion: pooled luna vi mean over the six dims is
        # mean(4, 3) = 3.5; folding in factual_care (score 1) would give ~3.14.
        handoff = json.loads((Path(tmp) / "trial" / "ab" / "handoff.json").read_text())
        assert set(handoff) == {"model_choice", "d15", "d4_calibration",
                                "price_bounds_12_1_1", "release", "evidence"}
        assert handoff["d4_calibration"]["words_per_minute_measured"] is None
        assert "source" in handoff["d4_calibration"]
        cps = handoff["price_bounds_12_1_1"]["cost_per_story_usd"]
        assert cps["standard"][MODEL_LUNA] == 0.0015, cps  # mean(0.001, 0.002)
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
        agg = aggregate(key_rows, scores, runs_dir)
    except InputError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    decision = evaluate_d15(agg["rows"])
    release = load_release(Path(args.prompts_dir))

    out_dir.mkdir(parents=True, exist_ok=True)
    write_aggregate_csv(agg["rows"], out_dir / "aggregate.csv")
    write_report(out_dir / "REPORT.md", key_rows, scores, agg["rows"],
                 decision, seed, release)
    if args.handoff:
        handoff = build_handoff(agg["rows"], decision, runs_dir, length_table,
                                release, (out_dir / "REPORT.md").as_posix())
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
