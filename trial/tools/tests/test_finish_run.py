"""Unit tests for trial/tools/finish_run.py (01.3.1-T3 step D, T-P1..T-P10).

All fixtures are synthetic; tests run in temp dirs and never touch the real
trial/measurements.csv. Run:
    uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests -v
"""

import csv
import hashlib
import io
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stdout
from datetime import datetime, timezone
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import finish_run as fr  # noqa: E402

FIXTURES = Path(__file__).resolve().parent / "fixtures"

# The MPT fixture logs use naive local timestamps; pin TZ so local->UTC is
# deterministic (fixture times are +07:00).
os.environ["TZ"] = "Asia/Ho_Chi_Minh"
time.tzset()

RUN_ID = "20260925-1141-vi-landscape-01"
STARTED_AT = "2026-09-25T04:41:02Z"  # = 11:41:02 fixture-local
STARTED_DT = datetime(2026, 9, 25, 4, 41, 2, tzinfo=timezone.utc)
SCRIPT = "Đêm ấy, trời mưa… rất to!"
# --task-id must be an MPT task UUID (SEC-T6-1); the fixture logs keep short
# ids, so tests substitute a UUID into a copy of the log.
TASK_ID = "35ab583d-35d9-489a-89b6-ee8b89e28c8b"           # mpt-api-sample
TASK_ID_FAILED = "0e6b2c47-9f1a-4d3e-8b57-2c9d4e7a1f60"    # mpt-api-failed


def rewrite_log(fixture, tmp, old="t-0001", task_id=TASK_ID):
    """Copy of a fixture MPT log with `old` replaced by `task_id`."""
    text = (FIXTURES / fixture).read_text(encoding="utf-8")
    out = Path(tmp) / f"mpt-api-{task_id}-{fixture}"
    out.write_text(text.replace(old, task_id), encoding="utf-8")
    return out


FAKE_PROBE = {
    "streams": [{"codec_type": "video"}],
    "format": {"filename": "/tmp/x/final-1.mp4", "duration": "301.375",
               "size": "11534336"},
}


def _run_json(**over):
    run = {
        "schema_version": 1,
        "run_id": RUN_ID,
        "run_status": "trial",
        "started_at": STARTED_AT,
        "finished_at": None,
        "lang": "vi", "aspect": "landscape",
        "topic": "synthetic test topic", "target_minutes": 5,
        "voice_name": "kokoro:test-voice", "story_source": "mpt",
        "pins": {"MPT_COMMIT": "a" * 40, "VIENEU_COMMIT": "b" * 40},
        "heads": {"mpt": "a" * 40, "vieneu": "b" * 40},
        "env": {"hw_model": "Mac16,10", "cpu_brand": "Apple M4",
                "memory_bytes": 16 << 30, "macos_version": "27.0"},
        "tap": {"port": 8002},
        "mpt_task_id": None,
        "sources": {},
    }
    run.update(over)
    return run


def make_env(tmp, *, run=None, jsonl="openai_usage-sample.jsonl",
             no_tap=False):
    """Build a fake trial/ + $NTS_HOME tree under tmp. Returns (trial, nts)."""
    tmp = Path(tmp)
    trial = tmp / "trial"
    nts = tmp / "nts"
    run_dir_trial = trial / "runs" / RUN_ID
    run_dir_nts = nts / "runs" / RUN_ID
    for d in (run_dir_trial, run_dir_nts, nts / "logs"):
        d.mkdir(parents=True)
    with open(trial / "measurements.csv", "w", newline="",
              encoding="utf-8") as f:
        csv.writer(f, lineterminator="\r\n").writerow(fr.COLUMNS)
    r = _run_json(**(run or {}))
    if no_tap:
        r["tap"] = {"disabled": True}
    (run_dir_trial / "run.json").write_text(json.dumps(r), encoding="utf-8")
    if jsonl is not None:
        (run_dir_trial / "openai_usage.jsonl").write_text(
            (FIXTURES / jsonl).read_text(), encoding="utf-8")
    (run_dir_nts / "rss_raw.csv").write_text(
        (FIXTURES / "rss_raw-sample.csv").read_text(), encoding="utf-8")
    (nts / "current_run").write_text(RUN_ID + "\n", encoding="utf-8")
    # task dir with script.json + dummy output
    task_dir = nts / "src" / "mpt" / "storage" / "tasks" / TASK_ID
    task_dir.mkdir(parents=True)
    (task_dir / "script.json").write_text(
        json.dumps({"script": SCRIPT}), encoding="utf-8")
    (task_dir / "final-1.mp4").write_bytes(b"fake-mp4")
    (task_dir / "audio.mp3").write_bytes(b"fake-mp3")
    return trial, nts


def shasum(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


# Stage windows / coverage of the mpt-api-sample.log fixture (T-P1).
SAMPLE_WINDOWS = {
    "script": {"start": "2026-09-25T04:41:05Z",
               "end": "2026-09-25T04:41:40Z"},
    "terms": {"start": "2026-09-25T04:41:40Z",
              "end": "2026-09-25T04:42:10Z"},
    "audio": {"start": "2026-09-25T04:42:10Z",
              "end": "2026-09-25T04:44:00Z"},
    "subtitle": {"start": "2026-09-25T04:44:00Z",
                 "end": "2026-09-25T04:46:10Z"},
    "materials": {"start": "2026-09-25T04:46:10Z",
                  "end": "2026-09-25T04:50:00Z"},
    "combine": {"start": "2026-09-25T04:50:00Z",
                "end": "2026-09-25T04:52:00Z"},
    "final": {"start": "2026-09-25T04:52:00Z",
              "end": "2026-09-25T04:53:00Z"},
}
SAMPLE_COVERAGE = {"first_marker": "2026-09-25T04:41:05Z",
                   "end": "2026-09-25T04:53:00Z",
                   "gaps_seconds": 0}


class LogParserTests(unittest.TestCase):
    """T-P1, T-P2 (parser part), T-P3."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.trial, self.nts = make_env(self.tmp.name)

    def parse(self, fixture, task_id=TASK_ID, old="t-0001"):
        log = rewrite_log(fixture, self.tmp.name, old=old)
        return fr.parse_mpt_log(log, task_id, STARTED_DT)

    def test_t_p1_windows_counts_and_coverage(self):
        st = self.parse("mpt-api-sample.log")
        self.assertEqual(st["marker_set"], 1)
        self.assertEqual(st["tz_offset"], "+07:00")
        self.assertEqual(st["task_id"], TASK_ID)
        secs = {k: st["windows"][k]["seconds"] for k in st["windows"]}
        self.assertEqual(secs, {
            "script": 35.0, "terms": 30.0, "audio": 110.0,
            "subtitle": 130.0, "materials": 230.0, "combine": 120.0,
            "final": 60.0})
        self.assertEqual(st["total_seconds"], 715.0)
        self.assertEqual(st["coverage"]["gaps_seconds"], 0)
        self.assertEqual(st["coverage"]["first_marker"],
                         "2026-09-25T04:41:05Z")
        self.assertEqual(st["coverage"]["end"], "2026-09-25T04:53:00Z")
        self.assertEqual(st["counts"],
                         {"image_failed": 2, "image_rendered": 12,
                          "terms_count": 13})
        self.assertEqual(st["whisper"],
                         {"language": "vi", "probability": 0.97,
                          "elapsed_s": 95.4})
        self.assertIsNone(st["failed_stage"])
        # ANSI was stripped (the marker on the glued mid-line record was found)
        self.assertIn("combine", st["windows"])

    def test_t_p3_scoping_ignores_other_tasks(self):
        # Foreign task t-0000 finished before our window; t-0003 after our end.
        st = self.parse("mpt-api-sample.log")
        self.assertEqual(st["coverage"]["end"], "2026-09-25T04:53:00Z")
        # Unknown task id -> CollectorError (exit 1).
        with self.assertRaises(fr.CollectorError):
            self.parse("mpt-api-sample.log", task_id="t-9999")

    def test_t_p2_failed_task_stages(self):
        st = self.parse("mpt-api-failed.log", old="t-0002")
        self.assertEqual(st["failed_stage"], "subtitle")
        self.assertIn("subtitle", st["windows"])
        self.assertNotIn("materials", st["windows"])
        self.assertEqual(st["counts"]["terms_count"], 2)


class TapTests(unittest.TestCase):
    """T-P4, T-P5, T-P10."""

    def agg(self, fixture, story_source="mpt", no_tap=False):
        return fr.aggregate_tap(FIXTURES / fixture, FIXTURES /
                                "prices-test.json", story_source, no_tap)

    def test_t_p4_image_counts(self):
        cols, issues = self.agg("openai_usage-sample.jsonl")
        self.assertEqual(cols["image_count"], "12")
        self.assertEqual(cols["image_http_429_count"], "2")
        self.assertEqual(cols["moderation_blocked_count"], "1")
        self.assertEqual(cols["image_output_tokens_total"], "12672")
        self.assertEqual(cols["image_quality"], "medium")
        self.assertEqual(cols["image_quality_returned"], "medium:12")
        self.assertEqual(cols["image_quality_requested"], "omitted")
        self.assertEqual(cols["image_size"], "1536x1024")
        self.assertEqual(cols["story_call_count"], "3")  # incl. the 500 retry
        self.assertNotIn("tap-foreign-call", issues)

    def test_t_p4_usage_absent_keeps_count(self):
        cols, issues = self.agg("openai_usage-usage-absent.jsonl")
        self.assertEqual(cols["image_count"], "13")
        self.assertEqual(cols["image_output_tokens_total"],
                         "n/a:usage-absent")
        self.assertEqual(cols["image_cost_usd"], "n/a:usage-absent")

    def test_t_p5_costs_snapshot_and_unknown_model(self):
        cols, _ = self.agg("openai_usage-sample.jsonl")
        # chat: ((1200-100)*0.10 + 100*0.01 + 540*0.50)/1e6 = 0.000381
        self.assertEqual(cols["story_cost_usd"], "0.000381")
        self.assertEqual(cols["story_input_tokens"], "1200")
        self.assertEqual(cols["story_cached_input_tokens"], "100")
        # snapshot suffix: gpt-image-2.5-flare-2026-09-08 -> flare rates
        # 12 x (61*5 + 0*8 + 1056*30)/1e6 = 0.383820
        self.assertEqual(cols["image_cost_usd"], "0.383820")
        self.assertEqual(cols["image_model"],
                         "gpt-image-2.5-flare-2026-09-08")
        # unknown model -> n/a:no-price
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "u.jsonl"
            line = json.loads((FIXTURES / "openai_usage-chat-only.jsonl")
                              .read_text().splitlines()[0])
            line["model"] = "gpt-9-unknown"
            p.write_text(json.dumps(line) + "\n")
            cols2, _ = fr.aggregate_tap(p, FIXTURES / "prices-test.json",
                                        "mpt", False)
        self.assertEqual(cols2["story_cost_usd"], "n/a:no-price")

    def test_t_p5_responses_via_no_price_tokens_summed(self):
        cols, _ = self.agg("openai_usage-responses.jsonl")
        self.assertEqual(cols["image_count"], "3")
        self.assertEqual(cols["image_cost_usd"], "n/a:no-price")
        self.assertEqual(cols["image_output_tokens_total"], "330")
        self.assertEqual(cols["image_tokens_per_image_avg"], "110.0")

    def test_image_input_split_absent_is_na_not_zero(self):
        # Relay shape observed on the real run (01.3.1-T4, 2026-09-26):
        # input_tokens + cached_tokens, no O6 text_tokens/image_tokens split.
        cols, _ = self.agg("openai_usage-relay-split-absent.jsonl")
        self.assertEqual(cols["image_count"], "2")
        self.assertEqual(cols["image_output_tokens_total"], "254")
        self.assertEqual(cols["image_input_text_tokens_total"],
                         "n/a:usage-split-absent")
        self.assertEqual(cols["image_input_image_tokens_total"],
                         "n/a:usage-split-absent")
        # unpriced relay host model -> CR-17 reason, not the token shape.
        self.assertEqual(cols["image_cost_usd"], "n/a:no-price")
        # a priced model with the same missing split must not understate cost
        with tempfile.TemporaryDirectory() as tmp:
            lines = (FIXTURES / "openai_usage-relay-split-absent.jsonl"
                     ).read_text(encoding="utf-8").splitlines()
            p = Path(tmp) / "u.jsonl"
            p.write_text("\n".join(
                json.dumps({**json.loads(l),
                            "model": "gpt-image-2.5-flare",
                            "via": None}) for l in lines)
                + "\n", encoding="utf-8")
            cols2, _ = fr.aggregate_tap(p, FIXTURES / "prices-test.json",
                                        "mpt", False)
        self.assertEqual(cols2["image_cost_usd"], "n/a:usage-split-absent")
        self.assertEqual(cols2["image_cost_per_image_usd"],
                         "n/a:component-na")

    def test_image_input_split_absent_wins_when_mixed(self):
        # One line with the O6 split and one without: summing only the first
        # would under-report, so both columns go n/a.
        lines = (FIXTURES / "openai_usage-relay-split-absent.jsonl"
                 ).read_text(encoding="utf-8").splitlines()
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "u.jsonl"
            mixed = [json.loads(lines[0]),
                     json.loads(lines[1])]
            mixed[1]["usage"]["input_tokens_details"] = {
                "text_tokens": 6600, "image_tokens": 0}
            p.write_text("\n".join(json.dumps(d) for d in mixed) + "\n",
                         encoding="utf-8")
            cols, _ = fr.aggregate_tap(p, FIXTURES / "prices-test.json",
                                       "mpt", False)
        self.assertEqual(cols["image_input_text_tokens_total"],
                         "n/a:usage-split-absent")
        self.assertEqual(cols["image_input_image_tokens_total"],
                         "n/a:usage-split-absent")

    def test_half_filled_input_split_is_na_not_zero(self):
        # D-2/L-1: a 200 image line whose input_tokens_details carries only
        # one of the two O6 keys is as split-less as an empty object; the old
        # `or` guard let it through and priced the missing half at 0.
        lines = (FIXTURES / "openai_usage-relay-split-absent.jsonl"
                 ).read_text(encoding="utf-8").splitlines()

        def rewrite(tmp, details):
            p = Path(tmp) / "u.jsonl"
            p.write_text("\n".join(
                json.dumps({**json.loads(l), "model": "gpt-image-2.5-flare",
                            "via": None,
                            "usage": {**json.loads(l)["usage"],
                                      "input_tokens_details": details}})
                for l in lines) + "\n", encoding="utf-8")
            return p

        with tempfile.TemporaryDirectory() as tmp:
            p = rewrite(tmp, {"text_tokens": 6600})
            cols, _ = fr.aggregate_tap(p, FIXTURES / "prices-test.json",
                                       "mpt", False)
        self.assertEqual(cols["image_input_text_tokens_total"],
                         "n/a:usage-split-absent")
        self.assertEqual(cols["image_input_image_tokens_total"],
                         "n/a:usage-split-absent")
        self.assertEqual(cols["image_cost_usd"], "n/a:usage-split-absent")
        self.assertEqual(cols["image_cost_per_image_usd"],
                         "n/a:component-na")
        # contrast: both keys present -> numeric (same lines, complete split)
        with tempfile.TemporaryDirectory() as tmp:
            p = rewrite(tmp, {"text_tokens": 6600, "image_tokens": 0})
            cols2, _ = fr.aggregate_tap(p, FIXTURES / "prices-test.json",
                                        "mpt", False)
        self.assertEqual(cols2["image_input_text_tokens_total"], "13200")
        self.assertEqual(cols2["image_input_image_tokens_total"], "0")
        # (2 x 6600 x 5 + 0 x 8 + 254 x 30) / 1e6
        self.assertEqual(cols2["image_cost_usd"], "0.073620")

    def test_t_p10_branch_matrix(self):
        # chat-only + mpt: story numeric, image side n/a:no-images
        cols, issues = self.agg("openai_usage-chat-only.jsonl", "mpt")
        self.assertEqual(cols["story_cost_usd"], "0.000381")
        self.assertEqual(cols["image_model"], "n/a:no-images")
        self.assertEqual(cols["image_cost_usd"], "n/a:no-images")
        # chat-only + plan02: numeric + issue
        cols, issues = self.agg("openai_usage-chat-only.jsonl", "plan02")
        self.assertEqual(cols["story_cost_usd"], "0.000381")
        self.assertIn("story-cost-terms-only", issues)
        # image-only + plan02: story cols n/a:story-source-plan02 + issue
        cols, issues = self.agg("openai_usage-image-only.jsonl", "plan02")
        for name in ("story_model", "story_input_tokens",
                     "story_output_tokens", "story_cost_usd",
                     "story_cached_input_tokens"):
            self.assertEqual(cols[name], "n/a:story-source-plan02")
        self.assertIn("story-cost-terms-only", issues)
        # image-only + mpt: n/a:no-llm-calls, no plan02 issue
        cols, issues = self.agg("openai_usage-image-only.jsonl", "mpt")
        self.assertEqual(cols["story_cost_usd"], "n/a:no-llm-calls")
        self.assertNotIn("story-cost-terms-only", issues)
        # no-tap: cols 8-18 and 39-48 all n/a:no-tap
        cols, issues = self.agg("openai_usage-sample.jsonl", "mpt",
                                no_tap=True)
        for name in fr.COLUMNS[7:18] + fr.COLUMNS[38:48]:
            self.assertEqual(cols[name], "n/a:no-tap", name)

    def test_t_p10_sec10_and_busy(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "u.jsonl"
            base = json.loads((FIXTURES / "openai_usage-chat-only.jsonl")
                              .read_text().splitlines()[0])
            foreign = dict(base, http_status=401, model="gpt-6-luna",
                           usage=None)
            busy = dict(base, http_status=503, error_code="tap_busy",
                        usage=None)
            p.write_text("\n".join(json.dumps(x) for x in
                                   (base, foreign, busy)) + "\n")
            cols, issues = fr.aggregate_tap(p, FIXTURES / "prices-test.json",
                                            "mpt", False)
        self.assertIn("tap-foreign-call", issues)
        self.assertIn("tap-busy", issues)
        # foreign/busy lines excluded from the sums
        self.assertEqual(cols["story_input_tokens"], "1000")
        self.assertEqual(cols["story_call_count"], "1")


class MiscUnitTests(unittest.TestCase):
    """T-P6, T-P7, T-P8 (validate_row part), issue vocabulary."""

    def test_t_p6_word_count(self):
        self.assertEqual(fr.count_words("Đêm ấy, trời mưa… rất to!"), 6)
        self.assertEqual(fr.count_words("It's late—very late."), 4)
        self.assertEqual(fr.count_words("— 1990 —"), 1)

    def test_t_p7_stage_peaks_windows(self):
        rows = fr._read_rss_raw(FIXTURES / "rss_raw-sample.csv")
        self.assertEqual(len(rows), 60)
        cols, csv_rows = fr.stage_peaks(rows, SAMPLE_WINDOWS,
                                        SAMPLE_COVERAGE)
        self.assertEqual(cols["ram_peak_vieneu_mb"], "300.0")
        self.assertEqual(cols["ram_peak_whisper_mb"], "500.0")
        self.assertEqual(cols["ram_peak_render_mb"], "2048.0")
        self.assertEqual(cols["ram_peak_total_mb"], "2198.0")
        self.assertEqual(cols["swap_peak_mb"], "259.0")
        self.assertEqual(cols["memory_pressure_max"], "warn")
        # rows: the whole-run `all` row first, then one per stage window
        self.assertEqual(len(csv_rows), 8)
        self.assertEqual(csv_rows[0][0], "all")
        self.assertTrue(all(r[0].startswith("stage:")
                            for r in csv_rows[1:]))
        # the `all` row is bounded by the task window and matches cols 52-54
        self.assertEqual(csv_rows[0][1], SAMPLE_COVERAGE["first_marker"])
        self.assertEqual(csv_rows[0][2], SAMPLE_COVERAGE["end"])
        self.assertEqual(csv_rows[0][3], "59")  # 60 rows minus 04:41:00Z
        self.assertEqual(csv_rows[0][8], cols["ram_peak_total_mb"])
        self.assertEqual(csv_rows[0][10], cols["swap_peak_mb"])
        self.assertEqual(csv_rows[0][11], cols["memory_pressure_max"])
        # empty window -> n/a:no-samples-in-window + bare NA in the row
        cols2, rows2 = fr.stage_peaks(rows, {
            "audio": {"start": "2026-09-26T00:00:00Z",
                      "end": "2026-09-26T00:01:00Z"}}, SAMPLE_COVERAGE)
        self.assertEqual(cols2["ram_peak_vieneu_mb"],
                         "n/a:no-samples-in-window")
        self.assertEqual(rows2[1][0], "stage:audio")
        self.assertEqual(rows2[1][3], "0")
        self.assertEqual(rows2[1][4:], ["NA"] * 11)

    def test_whole_run_window_excludes_post_run_tail(self):
        # Perf P3: cols 52-54 and the `all` row must ignore samples taken
        # after the task ended (a peer bench / a second MPT task in the
        # shared log), which the old lifetime aggregate counted.
        raw = (FIXTURES / "rss_raw-sample.csv").read_text(
            encoding="utf-8").splitlines()
        tail = []
        for ts in ("2026-09-25T04:53:12Z", "2026-09-25T04:53:24Z"):
            cells = raw[-1].split(",")
            cells[0] = ts
            cells[2] = str(8 * 1024 * 1024)   # vieneu 8 GiB
            cells[13] = "9999.0"              # swap
            cells[14] = "4"                   # pressure critical
            cells[15] = "8"                   # free %
            tail.append(",".join(cells))
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "rss_raw-tail.csv"
            p.write_text("\n".join(raw + tail) + "\n", encoding="utf-8")
            rows = fr._read_rss_raw(p)
        # the tail really is heavier than the run (non-vacuous check)
        lifetime = fr._window_stats(rows, None, None)
        # 8 GiB vieneu + 50 MiB proxy + 2 GiB mpt
        self.assertEqual(f"{lifetime['total_peak_mb']:.1f}", "10290.0")
        self.assertEqual(lifetime["swap_peak_mb"], 9999.0)
        self.assertEqual(lifetime["pressure_max"], "critical")
        cols, csv_rows = fr.stage_peaks(rows, SAMPLE_WINDOWS,
                                        SAMPLE_COVERAGE)
        self.assertEqual(cols["ram_peak_total_mb"], "2198.0")
        self.assertEqual(cols["swap_peak_mb"], "259.0")
        self.assertEqual(cols["memory_pressure_max"], "warn")
        # the `all` row carries exactly the windowed column values
        self.assertEqual(csv_rows[0][8], cols["ram_peak_total_mb"])
        self.assertEqual(csv_rows[0][10], cols["swap_peak_mb"])
        self.assertEqual(csv_rows[0][11], cols["memory_pressure_max"])

    def test_t_p8_validate_row_rejects(self):
        row = ["x"] * len(fr.COLUMNS)
        row[fr.COLUMNS.index("issues")] = "none"
        row[fr.COLUMNS.index("run_status")] = "trial"
        row[fr.COLUMNS.index("language")] = "vi"
        row[fr.COLUMNS.index("video_aspect")] = "landscape"
        row[fr.COLUMNS.index("story_source")] = "mpt"
        row[fr.COLUMNS.index("memory_pressure_max")] = "normal"
        row[fr.COLUMNS.index("dashboard_cost_usd_check")] = \
            "n/a:pending-dashboard"
        # non-enum string cols
        for n in ("run_id", "date", "mpt_commit", "vieneu_commit", "topic",
                  "story_model", "image_model", "image_quality", "image_size",
                  "machine", "voice_name", "image_quality_requested",
                  "image_quality_returned", "subtitle_detected_language"):
            row[fr.COLUMNS.index(n)] = "x"
        for n in ("target_minutes", "story_input_tokens",
                  "story_output_tokens", "story_cost_usd", "image_count",
                  "image_output_tokens_total", "image_cost_usd",
                  "moderation_blocked_count", "tts_seconds_audio",
                  "tts_wall_seconds", "whisper_wall_seconds",
                  "render_wall_seconds", "total_wall_seconds",
                  "ram_peak_vieneu_mb", "ram_peak_whisper_mb",
                  "ram_peak_render_mb", "output_duration_sec",
                  "output_size_mb", "words_in_story",
                  "words_per_minute_measured", "schema_version",
                  "terms_count", "story_cached_input_tokens",
                  "story_call_count", "image_input_text_tokens_total",
                  "image_input_image_tokens_total",
                  "image_tokens_per_image_avg", "image_cost_per_image_usd",
                  "image_failed_count", "image_http_429_count",
                  "story_wall_seconds", "image_wall_seconds",
                  "ram_peak_total_mb", "swap_peak_mb",
                  "usd_per_output_minute",
                  "local_compute_seconds_per_output_minute", "tts_chars"):
            row[fr.COLUMNS.index(n)] = "0"
        self.assertEqual(fr.validate_row(row, fr.COLUMNS), [])
        # blank cell
        bad = list(row); bad[0] = ""
        self.assertIn("run_id", fr.validate_row(bad, fr.COLUMNS))
        # malformed n/a
        bad = list(row)
        bad[fr.COLUMNS.index("terms_count")] = "n/a:BAD REASON"
        self.assertIn("terms_count", fr.validate_row(bad, fr.COLUMNS))
        # n/a on a never-na column
        bad = list(row); bad[0] = "n/a:no-run"
        self.assertIn("run_id", fr.validate_row(bad, fr.COLUMNS))
        # header drift
        self.assertIn("__header__",
                      fr.validate_row(row, ["wrong"] * len(fr.COLUMNS)))
        # issue outside vocabulary
        bad = list(row)
        bad[fr.COLUMNS.index("issues")] = "bogus-code"
        self.assertIn("issues", fr.validate_row(bad, fr.COLUMNS))
        bad = list(row)
        bad[fr.COLUMNS.index("issues")] = "note:operator note;swap-used"
        self.assertEqual(fr.validate_row(bad, fr.COLUMNS), [])


class EndToEndTests(unittest.TestCase):
    """T-P2 row, T-P8 csv integrity, T-P9 dashboard, abort, dry-run."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.trial, self.nts = make_env(self.tmp.name)
        self.csv = self.trial / "measurements.csv"
        self.sample_log = rewrite_log("mpt-api-sample.log", self.tmp.name)

    def finish_args(self, *extra):
        return [RUN_ID, "--task-id", TASK_ID,
                "--mpt-log", str(self.sample_log),
                "--proxy-log", str(FIXTURES / "proxy-sample.log"),
                *extra]

    def run_main(self, argv):
        return fr.main(argv, trial_dir=self.trial, nts_home=self.nts,
                       prices_path=FIXTURES / "prices-test.json",
                       probe_fn=lambda p: FAKE_PROBE)

    def test_dry_run_writes_nothing(self):
        before = shasum(self.csv)
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = self.run_main(self.finish_args("--dry-run"))
        self.assertEqual(rc, 0)
        pairs = dict(l.split("=", 1) for l in buf.getvalue().splitlines())
        self.assertEqual(len(pairs), 58)
        self.assertTrue(all(v != "" for v in pairs.values()))
        self.assertEqual(pairs["run_id"], RUN_ID)
        self.assertEqual(pairs["story_cost_usd"], "0.000381")
        self.assertEqual(pairs["image_cost_usd"], "0.383820")
        self.assertEqual(pairs["tts_seconds_audio"], "300.8")
        self.assertEqual(pairs["tts_wall_seconds"], "155.2")
        self.assertEqual(pairs["tts_chars"], "4200")
        self.assertEqual(pairs["words_in_story"], "6")
        self.assertEqual(pairs["words_per_minute_measured"], "1.2")
        self.assertEqual(pairs["image_count"], "12")
        self.assertEqual(pairs["image_failed_count"], "2")
        self.assertEqual(pairs["terms_count"], "13")
        self.assertEqual(pairs["subtitle_detected_language"], "vi@0.97")
        self.assertEqual(pairs["ram_peak_vieneu_mb"], "300.0")
        self.assertEqual(pairs["output_duration_sec"], "301.375")
        self.assertEqual(pairs["output_size_mb"], "11.00")
        self.assertEqual(pairs["machine"], "Mac16,10/Apple M4/16GB/macOS 27.0")
        self.assertEqual(pairs["voice_name"], "kokoro:test-voice")
        self.assertEqual(pairs["dashboard_cost_usd_check"],
                         "n/a:pending-dashboard")
        self.assertEqual(pairs["run_status"], "trial")
        # derived: (0.000381+0.383820)/(301.375/60) and walls sum
        self.assertAlmostEqual(float(pairs["usd_per_output_minute"]),
                               0.384201 / (301.375 / 60), places=6)
        self.assertEqual(pairs["local_compute_seconds_per_output_minute"],
                         f"{(155.2 + 130.0 + 180.0) / (301.375 / 60):.1f}")
        # nothing written
        self.assertEqual(shasum(self.csv), before)
        self.assertTrue((self.nts / "current_run").is_file())
        self.assertFalse((self.trial / "runs" / RUN_ID /
                          "stages.json").exists())

    def test_finish_appends_one_row_and_clears_state(self):
        before = shasum(self.csv)
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(self.finish_args())
        self.assertEqual(rc, 0)
        self.assertNotEqual(shasum(self.csv), before)
        with open(self.csv, newline="", encoding="utf-8") as f:
            rows = list(csv.reader(f))
        self.assertEqual(len(rows), 2)
        self.assertEqual(rows[1][0], RUN_ID)
        self.assertTrue(self.csv.read_bytes().endswith(b"\r\n"))
        self.assertFalse((self.nts / "current_run").exists())
        run = json.loads((self.trial / "runs" / RUN_ID / "run.json")
                         .read_text())
        self.assertEqual(run["mpt_task_id"], TASK_ID)
        self.assertEqual(run["run_status"], "trial")
        self.assertIsNotNone(run["finished_at"])
        self.assertEqual(run["sources"]["tts_seconds_audio"], "proxy-log")
        # I4: an absolute --mpt-log outside $NTS_HOME/repo is stored as
        # basename only
        self.assertEqual(run["sources"]["mpt_log"], self.sample_log.name)
        self.assertTrue((self.trial / "runs" / RUN_ID /
                         "stages.json").is_file())
        ffprobe = json.loads((self.trial / "runs" / RUN_ID / "ffprobe.json")
                             .read_text())
        self.assertEqual(ffprobe["format"]["filename"], "final-1.mp4")
        rss = (self.trial / "runs" / RUN_ID / "rss.csv").read_text()
        self.assertIn("stage:audio", rss)
        self.assertIn("stage:final", rss)
        # script + video copied to $NTS_HOME only
        self.assertEqual((self.nts / "runs" / RUN_ID / "script.txt")
                         .read_text(), SCRIPT)
        self.assertTrue((self.nts / "runs" / RUN_ID / "final-1.mp4")
                        .is_file())
        # second finish refused (I2), csv byte-identical
        after = shasum(self.csv)
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(self.finish_args())
        self.assertEqual(rc, 1)
        self.assertEqual(shasum(self.csv), after)

    def test_task_id_must_be_an_mpt_uuid(self):
        # SEC-T6-1: the id is joined into $NTS_HOME/src/mpt/storage/tasks/
        # <id> and written into tracked JSON, so it is rejected before any
        # path join or write — including on the --status failed path, where
        # the log-equality check does not apply. Exit 2 = usage error.
        # --mpt-log + --dry-run make the exit 2 non-vacuous: without the
        # UUID check the same call succeeds (rc 0), so it is not a missing
        # log that refuses.
        before = shasum(self.csv)
        raw_log = FIXTURES / "mpt-api-sample.log"     # holds t-0001
        for bad in ("t-0001", "../../../etc", "/etc/passwd",
                    "35ab583d-35d9-489a-89b6-ee8b89e28c8",
                    "35ab583d35d9489a89b6ee8b89e28c8b",
                    TASK_ID + "\n", ""):
            with self.subTest(task_id=bad):
                with redirect_stdout(io.StringIO()):
                    rc = self.run_main([RUN_ID, "--task-id", bad,
                                        "--status", "failed",
                                        "--mpt-log", str(raw_log),
                                        "--dry-run"])
                self.assertEqual(rc, 2)
        # contrast: the UUID the tree uses gets past the check (rc 0)
        with redirect_stdout(io.StringIO()):
            self.assertEqual(self.run_main(self.finish_args("--dry-run")), 0)
        self.assertEqual(shasum(self.csv), before)
        self.assertFalse(
            (self.trial / "runs" / RUN_ID / "stages.json").exists())

    def test_retried_finish_replaces_rss_rows(self):
        # D-3: the sampler appends its own lifetime `all` row on TERM; the
        # collector then rewrites the whole file (windowed `all` + stage
        # rows), so a retried finish must replace, not append.
        rss = self.trial / "runs" / RUN_ID / "rss.csv"
        rss.write_text(
            fr.RSS_CSV_HEADER + "\n"
            "all,2026-09-25T04:41:00Z,2026-09-25T04:53:00Z,60,"
            "9999.0,0.0,0.0,0.0,9999.0,NA,0.0,normal,NA,0,0\n",
            encoding="utf-8")
        with redirect_stdout(io.StringIO()):
            self.assertEqual(self.run_main(self.finish_args()), 0)
        first = rss.read_text()
        scopes = [r[0] for r in csv.reader(first.splitlines())]
        self.assertEqual(scopes[0], "scope")
        self.assertEqual(scopes.count("all"), 1)
        self.assertEqual(scopes.count("stage:audio"), 1)
        self.assertEqual(len(scopes), 9)  # header + all + 7 stage rows
        self.assertNotIn("9999.0", first)  # lifetime row replaced
        # retried finish (QA repro: the sheet row was lost, run re-armed)
        with open(self.csv, "w", newline="", encoding="utf-8") as f:
            csv.writer(f, lineterminator="\r\n").writerow(fr.COLUMNS)
        (self.nts / "current_run").write_text(RUN_ID + "\n",
                                              encoding="utf-8")
        with redirect_stdout(io.StringIO()):
            self.assertEqual(self.run_main(self.finish_args()), 0)
        self.assertEqual(rss.read_text(), first)

    def test_run_json_records_collector_revision(self):
        # L-4: the row is attributable to a tool revision from run.json.
        with redirect_stdout(io.StringIO()):
            self.assertEqual(self.run_main(self.finish_args()), 0)
        run = json.loads((self.trial / "runs" / RUN_ID / "run.json")
                         .read_text())
        self.assertEqual(run["collector"]["finish_run_sha256"],
                         shasum(Path(fr.__file__).resolve()))
        self.assertEqual(run["collector"]["run_manifest_sha256"],
                         shasum(TOOLS / "run_manifest.py"))

    def test_issue_note_prefix_is_not_doubled(self):
        # L-5: an --issue that already carries note: is stored as given.
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(self.finish_args(
                "--issue", "note:handwritten",
                "--issue", "plain note"))
        self.assertEqual(rc, 0)
        with open(self.csv, newline="", encoding="utf-8") as f:
            rows = list(csv.reader(f))
        issues = rows[1][fr.COLUMNS.index("issues")]
        self.assertIn("note:handwritten", issues)
        self.assertNotIn("note:note:", issues)
        self.assertIn("note:plain note", issues)

    def test_t_p2_failed_run_row(self):
        task_dir = (self.nts / "src" / "mpt" / "storage" / "tasks"
                    / TASK_ID_FAILED)
        task_dir.mkdir(parents=True)
        (task_dir / "script.json").write_text(json.dumps({"script": SCRIPT}))
        (task_dir / "audio.mp3").write_bytes(b"x")
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = fr.main([RUN_ID, "--task-id", TASK_ID_FAILED,
                          "--mpt-log",
                          str(rewrite_log("mpt-api-failed.log", self.tmp.name,
                                          old="t-0002",
                                          task_id=TASK_ID_FAILED)),
                          "--status", "failed", "--dry-run"],
                         trial_dir=self.trial, nts_home=self.nts,
                         prices_path=FIXTURES / "prices-test.json",
                         probe_fn=lambda p: FAKE_PROBE)
        self.assertEqual(rc, 0)
        pairs = dict(l.split("=", 1) for l in buf.getvalue().splitlines())
        self.assertEqual(pairs["run_status"], "failed")
        self.assertEqual(pairs["render_wall_seconds"],
                         "n/a:run-failed-at-subtitle")
        self.assertEqual(pairs["output_duration_sec"],
                         "n/a:run-failed-at-subtitle")
        # audio stage finished before the failure: proxy source is n/a but
        # not run-failed
        # audio.mp3 exists -> ffprobe fallback supplies duration (301.4),
        # wall = audio window (120.0); no proxy -> chars from script length.
        self.assertEqual(pairs["tts_seconds_audio"], "301.4")
        self.assertEqual(pairs["tts_wall_seconds"], "120.0")
        self.assertEqual(pairs["tts_chars"], str(len(SCRIPT)))
        self.assertEqual(pairs["story_wall_seconds"], "55.0")

    def test_t_p8_header_drift_blocks_append(self):
        with open(self.csv, "w", newline="", encoding="utf-8") as f:
            csv.writer(f, lineterminator="\r\n").writerow(
                ["x"] * len(fr.COLUMNS))
        before = shasum(self.csv)
        rc = self.run_main(self.finish_args("--dry-run"))
        self.assertEqual(rc, 1)
        self.assertEqual(shasum(self.csv), before)

    def test_t_p9_dashboard_cost(self):
        # a committed row with pending cell
        with redirect_stdout(io.StringIO()):
            self.assertEqual(self.run_main(self.finish_args()), 0)
        # a no-run JSONL line on the same day contributes to the tap total
        norun = self.trial / "runs" / "no-run"
        norun.mkdir()
        extra = json.loads((FIXTURES / "openai_usage-chat-only.jsonl")
                           .read_text().splitlines()[0])
        extra["run_id"] = "no-run"
        (norun / "openai_usage.jsonl").write_text(json.dumps(extra) + "\n")
        # tap total = sample chat+image cost + one extra chat call
        tap_total = 0.000381 + 0.383820 + (
            (1000 - 100) * 0.10 + 100 * 0.01 + 500 * 0.50) / 1e6
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = self.run_main(
                [RUN_ID, "--dashboard-cost", f"{tap_total * 1.02:.6f}",
                 "--dashboard-day", "2026-09-25"])
        self.assertEqual(rc, 0)
        with open(self.csv, newline="", encoding="utf-8") as f:
            rows = list(csv.reader(f))
        col = fr.COLUMNS.index("dashboard_cost_usd_check")
        self.assertEqual(rows[1][col], f"{tap_total * 1.02:.6f}")
        # non-pending cell refused
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(
                [RUN_ID, "--dashboard-cost", "1.0",
                 "--dashboard-day", "2026-09-25"])
        self.assertEqual(rc, 1)
        # delta > 5% writes value AND adds issue, exit 1 — fresh run row
        rid2 = "20260925-1200-vi-landscape-01"
        row2 = list(rows[1]); row2[0] = rid2
        row2[col] = "n/a:pending-dashboard"
        row2[fr.COLUMNS.index("issues")] = "none"
        with open(self.csv, "a", newline="", encoding="utf-8") as f:
            csv.writer(f, lineterminator="\r\n").writerow(row2)
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(
                [rid2, "--dashboard-cost", f"{tap_total * 1.30:.6f}",
                 "--dashboard-day", "2026-09-25"])
        self.assertEqual(rc, 1)
        with open(self.csv, newline="", encoding="utf-8") as f:
            rows = list(csv.reader(f))
        self.assertEqual(rows[2][col], f"{tap_total * 1.30:.6f}")
        self.assertIn("dashboard-delta-", rows[2]
                      [fr.COLUMNS.index("issues")])

    def test_abort_clears_state_no_row(self):
        before = shasum(self.csv)
        with redirect_stdout(io.StringIO()):
            rc = self.run_main([RUN_ID, "--abort"])
        self.assertEqual(rc, 0)
        self.assertEqual(shasum(self.csv), before)
        self.assertFalse((self.nts / "current_run").exists())
        run = json.loads((self.trial / "runs" / RUN_ID / "run.json")
                         .read_text())
        self.assertEqual(run["run_status"], "failed")
        self.assertIn("aborted", (self.trial / "runs" / RUN_ID /
                                  "notes.md").read_text())

    def test_help_and_columns_invariants(self):
        with self.assertRaises(SystemExit) as ctx:
            fr.build_parser().parse_args(["--help"])
        self.assertEqual(ctx.exception.code, 0)
        # I1: COLUMNS matches the real measurements.csv header (order).
        real = Path(__file__).resolve().parents[2] / "measurements.csv"
        with open(real, newline="", encoding="utf-8") as f:
            header = next(csv.reader([f.readline().rstrip("\r\n")]))
        self.assertEqual(list(header), fr.COLUMNS)


class ReviewRegressionTests(unittest.TestCase):
    """Review fixes: sampler lifecycle, unfinished window, stored paths."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.trial, self.nts = make_env(self.tmp.name)
        self.csv = self.trial / "measurements.csv"
        self.sample_log = rewrite_log("mpt-api-sample.log", self.tmp.name)
        self.proc = None

    def tearDown(self):
        if self.proc is not None and self.proc.poll() is None:
            self.proc.kill()
            self.proc.wait()

    def run_main(self, argv):
        return fr.main(argv, trial_dir=self.trial, nts_home=self.nts,
                       prices_path=FIXTURES / "prices-test.json",
                       probe_fn=lambda p: FAKE_PROBE)

    def start_sampler_stub(self):
        """A live process whose `ps -o command=` shows
        'rss_sampler.sh <run_id>'; writes a pidfile like the real one."""
        run_dir_nts = self.nts / "runs" / RUN_ID
        stub = run_dir_nts / "rss_sampler.sh"
        stub.write_text("#!/bin/sh\nsleep 2\n")
        os.chmod(stub, 0o755)
        self.proc = subprocess.Popen(["/bin/bash", str(stub), RUN_ID])
        (run_dir_nts / "rss_sampler.pid").write_text(
            str(self.proc.pid) + "\n", encoding="utf-8")
        time.sleep(0.2)  # let ps see the script command line

    def test_dry_run_leaves_sampler_alive_and_writes_nothing(self):
        self.start_sampler_stub()
        csv_before = shasum(self.csv)
        run_json = self.trial / "runs" / RUN_ID / "run.json"
        run_json_before = shasum(run_json)
        args = [RUN_ID, "--task-id", TASK_ID,
                "--mpt-log", str(self.sample_log),
                "--proxy-log", str(FIXTURES / "proxy-sample.log")]
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(args + ["--dry-run"])
        self.assertEqual(rc, 0)
        self.assertIsNone(self.proc.poll())       # sampler still alive
        self.assertEqual(shasum(self.csv), csv_before)
        self.assertEqual(shasum(run_json), run_json_before)
        self.assertFalse(
            (self.trial / "runs" / RUN_ID / "rss.csv").exists())
        self.assertTrue((self.nts / "current_run").is_file())
        # a real finish reaches the writes block and DOES stop it
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(args)
        self.assertEqual(rc, 0)
        self.proc.wait(timeout=15)
        self.assertIsNotNone(self.proc.poll())

    def test_validation_failure_leaves_sampler_alive(self):
        self.start_sampler_stub()
        run_path = self.trial / "runs" / RUN_ID / "run.json"
        run = json.loads(run_path.read_text())
        run["lang"] = "xx"            # language enum -> row fails validation
        run_path.write_text(json.dumps(run))
        csv_before = shasum(self.csv)
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(
                [RUN_ID, "--task-id", TASK_ID,
                 "--mpt-log", str(self.sample_log)])
        self.assertEqual(rc, 1)
        self.assertIsNone(self.proc.poll())
        self.assertEqual(shasum(self.csv), csv_before)
        self.assertFalse(
            (self.trial / "runs" / RUN_ID / "rss.csv").exists())

    def test_unfinished_end_ts_is_this_tasks_last_record(self):
        st = fr.parse_mpt_log(
            rewrite_log("mpt-api-unfinished.log", self.tmp.name,
                        old="t-0005"),
            TASK_ID, STARTED_DT, allow_unfinished=True)
        # last task record is 11:41:55 local; foreign-task records at
        # 11:50/11:51 must not stretch the window (old code used last_ts)
        self.assertEqual(st["coverage"]["end"], "2026-09-25T04:41:55Z")
        self.assertEqual(st["total_seconds"], 50.0)
        self.assertEqual(st["counts"]["terms_count"], 2)

    def test_terms_unparseable_list_is_na_not_zero(self):
        log = Path(self.tmp.name) / "mpt-api-termsbad.log"
        log.write_text(
            '2026-09-25 11:41:02 | INFO | "app/api.py:88": submit - '
            f'task {TASK_ID} submitted\n'
            '2026-09-25 11:41:05 | INFO | "app/services/task.py:301": '
            'run - \n\n## generating video script\n'
            '2026-09-25 11:41:40 | INFO | "app/services/task.py:390": '
            'run - \n\n## generating video terms\n'
            '2026-09-25 11:41:55 | INFO | "app/services/task.py:421": '
            'terms - completed:\n[unquoted term]\n'
            '2026-09-25 11:53:00 | SUCCESS | '
            f'"app/services/task.py:1100": run - task {TASK_ID} finished\n')
        st = fr.parse_mpt_log(log, TASK_ID, STARTED_DT)
        self.assertIsNone(st["counts"]["terms_count"])
        # row level: n/a:marker-missing, not 0 (schema.md col 38); the task
        # dir already exists from make_env.
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = self.run_main([RUN_ID, "--task-id", TASK_ID,
                                "--mpt-log", str(log), "--dry-run"])
        self.assertEqual(rc, 0)
        pairs = dict(l.split("=", 1) for l in buf.getvalue().splitlines())
        self.assertEqual(pairs["terms_count"], "n/a:marker-missing")

    def test_topic_starting_with_na_prefix_validates(self):
        run_path = self.trial / "runs" / RUN_ID / "run.json"
        run = json.loads(run_path.read_text())
        run["topic"] = "n/a placeholder topic"
        run_path.write_text(json.dumps(run))
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = self.run_main(
                [RUN_ID, "--task-id", TASK_ID,
                 "--mpt-log", str(self.sample_log),
                 "--proxy-log", str(FIXTURES / "proxy-sample.log"),
                 "--dry-run"])
        self.assertEqual(rc, 0)
        pairs = dict(l.split("=", 1) for l in buf.getvalue().splitlines())
        self.assertEqual(pairs["topic"], "n/a placeholder topic")

    def test_sources_store_nts_home_relative_path(self):
        log = self.nts / "logs" / "mpt-api-x.log"
        log.write_text(self.sample_log.read_text(encoding="utf-8"))
        with redirect_stdout(io.StringIO()):
            rc = self.run_main(
                [RUN_ID, "--task-id", TASK_ID, "--mpt-log", str(log),
                 "--proxy-log", str(FIXTURES / "proxy-sample.log")])
        self.assertEqual(rc, 0)
        run = json.loads(
            (self.trial / "runs" / RUN_ID / "run.json").read_text())
        self.assertEqual(run["sources"]["mpt_log"],
                         "$NTS_HOME/logs/mpt-api-x.log")


if __name__ == "__main__":
    unittest.main()
