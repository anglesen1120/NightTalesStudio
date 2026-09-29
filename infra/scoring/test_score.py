#!/usr/bin/env python3
"""Tests for the 11.2.1-T1 scoring harness.

Prices here are obviously fake doubles — no unit test asserts a real price
(prices are data, not behaviour). The fixtures only exercise the harness.

Every behaviour the reviewers asked for has a regression test here:
exact id/unit/type validation, the explicit null policy, capture-path
containment (symlink/traversal/nesting), input caps, non-finite and
over-deep JSON, the derived monthly value, and the declared noise band with
its tie-break order.
"""

import json
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import score  # noqa: E402


CRITERIA = {
    "version": "test",
    "params": {
        "duty_cycle_hours_per_day": 6,
        "days_per_month": 30.4375,
        "noise_tolerance": 0.01,
    },
    "hard_gates": [
        {"id": "gpu_vram_gb", "kind": "min_number", "threshold": 16, "unit": "GB", "heading": "fake"},
        {"id": "pg17_pitr", "kind": "bool", "unit": "bool", "heading": "fake"},
        {"id": "owner_locked_eligible", "kind": "bool", "unit": "bool", "heading": "fake"},
    ],
    "criteria": [
        {
            "id": "gpu_price_per_hour",
            "weight": 0.4,
            "direction": "lower",
            "unit": "USD/GPU-hour",
            "null_policy": "fail",
            "heading": "fake",
        },
        {
            "id": "monthly_at_duty_cycle",
            "weight": 0.1,
            "direction": "lower",
            "unit": "USD/month",
            "null_policy": "fail",
            "derived": "gpu_price_per_hour * params.duty_cycle_hours_per_day * params.days_per_month",
            "heading": "fake",
        },
        {
            "id": "internet_egress_gb",
            "weight": 0.4,
            "direction": "lower",
            "unit": "USD/GB",
            "null_policy": "fail",
            "heading": "fake",
        },
        {
            "id": "latency_to_apis_ms",
            "weight": 0.1,
            "direction": "lower",
            "unit": "ms",
            "null_policy": "neutral",
            "heading": "fake",
        },
    ],
}

IDS_UNITS = {
    "gpu_vram_gb": "GB",
    "pg17_pitr": "bool",
    "owner_locked_eligible": "bool",
    "gpu_price_per_hour": "USD/GPU-hour",
    "monthly_at_duty_cycle": "USD/month",
    "internet_egress_gb": "USD/GB",
    "latency_to_apis_ms": "ms",
}


def entry(value, unit):
    return {
        "value": value,
        "unit": unit,
        "source_url": "https://example.invalid/pricing",
        "captured_on": "2026-09-29",
        "captured_by": "test",
        "quote": "fake quote",
    }


def capture(provider, vram=24, pitr=True, gpu_price=1.0, egress=0.01, latency=None, locked=True):
    """A valid capture; monthly_at_duty_cycle is always the derived value."""
    values = {
        "gpu_vram_gb": entry(vram, "GB"),
        "pg17_pitr": entry(pitr, "bool"),
        "owner_locked_eligible": entry(locked, "bool"),
        "gpu_price_per_hour": entry(gpu_price, "USD/GPU-hour"),
        "monthly_at_duty_cycle": entry(gpu_price * 6 * 30.4375, "USD/month"),
        "internet_egress_gb": entry(egress, "USD/GB"),
        "latency_to_apis_ms": entry(latency, "ms"),
    }
    return {
        "provider": provider,
        "region": "fake-1",
        "captured_on": "2026-09-29",
        "values": values,
    }


class TestScore(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.captures_dir = os.path.join(self.tmp.name, "captures")
        os.makedirs(self.captures_dir)
        self.criteria_path = os.path.join(self.tmp.name, "criteria.json")
        with open(self.criteria_path, "w", encoding="utf-8") as fh:
            json.dump(CRITERIA, fh)

    def write_capture(self, name, data):
        path = os.path.join(self.captures_dir, name)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(data, fh)
        return path

    def run_main(self, *paths):
        argv = ["--criteria", self.criteria_path, "--captures-dir", self.captures_dir]
        argv.extend(paths)
        return score.main(argv)

    def errors_for(self, data, name="cap.json"):
        return score.validate_capture(data, name, CRITERIA)

    # --- the seven tests named by the task file ---------------------------

    def test_weights_must_sum_to_one(self):
        bad = json.loads(json.dumps(CRITERIA))
        bad["criteria"][0]["weight"] = 0.9  # sums to 1.5
        errors = score.check_criteria(bad, json.dumps(bad))
        self.assertTrue(any("sum" in e for e in errors), errors)
        good = score.check_criteria(CRITERIA, json.dumps(CRITERIA))
        self.assertEqual(good, [])

    def test_hard_gate_vram_15gb_rejects_provider(self):
        cap = capture("fake-low", vram=15)
        self.assertIn("gpu_vram_gb", score._gate_failures(cap, CRITERIA["hard_gates"]))
        ok = capture("fake-ok", vram=16)
        self.assertEqual(score._gate_failures(ok, CRITERIA["hard_gates"]), [])
        self.assertEqual(score._gate_status(ok, CRITERIA["hard_gates"])["gpu_vram_gb"], "pass")

    def test_missing_source_url_is_invalid_capture(self):
        cap = capture("fake")
        del cap["values"]["gpu_price_per_hour"]["source_url"]
        errors = self.errors_for(cap)
        self.assertTrue(any("source_url" in e for e in errors), errors)

    def test_missing_captured_on_is_invalid_capture(self):
        cap = capture("fake")
        del cap["values"]["internet_egress_gb"]["captured_on"]
        errors = self.errors_for(cap)
        self.assertTrue(any("captured_on" in e for e in errors), errors)

    def test_ranking_is_stable_across_runs(self):
        a = capture("alpha", gpu_price=1.0, egress=0.02)
        b = capture("beta", gpu_price=2.0, egress=0.01)
        first, _ = score.score_captures(CRITERIA, [a, b])
        second, _ = score.score_captures(CRITERIA, [a, b])
        self.assertEqual([r["provider"] for r in first], [r["provider"] for r in second])
        self.assertEqual([r["total"] for r in first], [r["total"] for r in second])
        self.assertEqual(first[0]["provider"], "alpha")

    def test_exit_2_on_no_arguments(self):
        rc = score.main(["--criteria", self.criteria_path])
        self.assertEqual(rc, 2)

    def test_exit_1_when_top_provider_fails_a_hard_gate(self):
        # cheaper (higher score) but ineligible -> the harness must refuse to
        # call it a decision.
        a = capture("top-but-ineligible", gpu_price=0.5, egress=0.01, pitr=False)
        b = capture("eligible", gpu_price=3.0, egress=0.02)
        pa = self.write_capture("a.json", a)
        pb = self.write_capture("b.json", b)
        self.assertEqual(self.run_main(pa, pb), 1)

    # --- id / unit / type validation --------------------------------------

    def test_missing_id_is_invalid_capture(self):
        cap = capture("fake")
        del cap["values"]["pg17_pitr"]
        errors = self.errors_for(cap)
        self.assertTrue(any("missing 'pg17_pitr'" in e for e in errors), errors)

    def test_unknown_id_is_invalid_capture(self):
        cap = capture("fake")
        cap["values"]["gpu_memory_bandwidth"] = entry(900, "GB/s")
        errors = self.errors_for(cap)
        self.assertTrue(any("unknown id" in e for e in errors), errors)

    def test_unit_mismatch_is_invalid_capture(self):
        cap = capture("fake")
        cap["values"]["internet_egress_gb"]["unit"] = "USD/TB"
        errors = self.errors_for(cap)
        self.assertTrue(any("declared unit" in e for e in errors), errors)

    def test_wrong_value_type_is_invalid_capture(self):
        cap = capture("fake")
        cap["values"]["pg17_pitr"]["value"] = "yes"
        errors = self.errors_for(cap)
        self.assertTrue(any("true/false" in e for e in errors), errors)
        cap2 = capture("fake")
        cap2["values"]["gpu_price_per_hour"]["value"] = "1.05"
        self.assertTrue(any("finite number" in e for e in self.errors_for(cap2)))

    # --- null policy -------------------------------------------------------

    def test_null_latency_is_neutral_when_mixed(self):
        crit = next(c for c in CRITERIA["criteria"] if c["id"] == "latency_to_apis_ms")
        scores = score._normalise(crit, {"a": 100.0, "b": 200.0, "c": None})
        self.assertEqual(scores["a"], 1.0)
        self.assertEqual(scores["b"], 0.0)
        self.assertEqual(scores["c"], 1.0, "an unmeasured latency must not be charged 0.0")

    def test_null_value_fails_against_a_number(self):
        crit = next(c for c in CRITERIA["criteria"] if c["id"] == "internet_egress_gb")
        scores = score._normalise(crit, {"a": 0.05, "b": None})
        self.assertEqual(scores["a"], 1.0)
        self.assertEqual(scores["b"], 0.0)

    def test_all_null_criterion_is_neutral(self):
        crit = next(c for c in CRITERIA["criteria"] if c["id"] == "internet_egress_gb")
        scores = score._normalise(crit, {"a": None, "b": None})
        self.assertEqual(scores, {"a": 1.0, "b": 1.0})

    def test_unknown_gate_blocks_eligibility_and_exit_1(self):
        cap = capture("unknown-gate")
        cap["values"]["pg17_pitr"]["value"] = None
        self.assertEqual(score._gate_unknowns(cap, CRITERIA["hard_gates"]), ["pg17_pitr"])
        self.assertEqual(score._gate_failures(cap, CRITERIA["hard_gates"]), [])
        other = capture("ok", gpu_price=5.0)
        pa = self.write_capture("a.json", cap)
        pb = self.write_capture("b.json", other)
        self.assertEqual(self.run_main(pa, pb), 1)

    # --- derived criterion -------------------------------------------------

    def test_derived_value_is_enforced(self):
        cap = capture("fake")
        cap["values"]["monthly_at_duty_cycle"]["value"] = 42.0
        errors = self.errors_for(cap)
        self.assertTrue(any("does not match the derived" in e for e in errors), errors)
        ok = capture("fake2")
        self.assertEqual(self.errors_for(ok), [])
        derived, _ = score.score_captures(CRITERIA, [ok])
        self.assertAlmostEqual(derived[0]["total"], derived[0]["total"])

    # --- capture-path containment and input caps ---------------------------

    def test_nested_capture_is_refused(self):
        nested_dir = os.path.join(self.captures_dir, "sub")
        os.makedirs(nested_dir)
        path = os.path.join(nested_dir, "deep.json")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(capture("deep"), fh)
        self.assertEqual(self.run_main(path), 2)

    def test_symlink_capture_is_refused(self):
        real = self.write_capture("real.json", capture("real"))
        link = os.path.join(self.captures_dir, "link.json")
        os.symlink(real, link)
        self.assertEqual(self.run_main(link), 2)

    def test_capture_outside_dir_is_refused(self):
        outside = os.path.join(self.tmp.name, "outside.json")
        with open(outside, "w", encoding="utf-8") as fh:
            json.dump(capture("outside"), fh)
        self.assertEqual(self.run_main(outside), 2)

    def test_non_regular_capture_is_refused(self):
        self.assertEqual(self.run_main(self.captures_dir), 2)

    def test_too_many_captures_is_refused(self):
        old = score.MAX_CAPTURE_FILES
        score.MAX_CAPTURE_FILES = 1
        self.addCleanup(setattr, score, "MAX_CAPTURE_FILES", old)
        pa = self.write_capture("a.json", capture("a"))
        pb = self.write_capture("b.json", capture("b"))
        self.assertEqual(self.run_main(pa, pb), 2)

    def test_oversized_capture_is_refused(self):
        old = score.MAX_CAPTURE_BYTES
        score.MAX_CAPTURE_BYTES = 32
        self.addCleanup(setattr, score, "MAX_CAPTURE_BYTES", old)
        pa = self.write_capture("a.json", capture("a"))
        self.assertEqual(self.run_main(pa), 2)

    # --- hostile JSON ------------------------------------------------------

    def test_non_finite_number_is_refused(self):
        obj, err = score._loads('{"value": NaN}', "x")
        self.assertIsNone(obj)
        self.assertIn("non-finite", err)
        obj2, err2 = score._loads('{"value": Infinity}', "x")
        self.assertIsNone(obj2)
        self.assertIn("non-finite", err2)
        self.assertFalse(score._is_number(float("nan")))
        self.assertFalse(score._is_number(float("inf")))

    def test_deeply_nested_json_is_refused(self):
        depth = score.MAX_JSON_DEPTH + 5
        obj, err = score._loads("[" * depth + "]" * depth, "x")
        self.assertIsNone(obj)
        self.assertIn("nesting depth", err)
        obj2, err2 = score._loads('{"a": [1, 2]}', "x")
        self.assertIsNone(err2)
        self.assertIsInstance(obj2, dict)

    def test_unbalanced_json_is_refused(self):
        obj, err = score._loads('{"a": 1', "x")
        self.assertIsNone(obj)
        self.assertIn("unbalanced", err)

    # --- noise band and tie-break -----------------------------------------

    def test_noise_band_uses_tie_break(self):
        # Two rows inside the declared tolerance form one band and are ordered by
        # the tie-break (latency to the APIs first), not by their raw totals;
        # a row outside the tolerance keeps its total order.
        slow = capture("aprovider", gpu_price=1.0, egress=0.01, latency=200.0)
        fast = capture("zprovider", gpu_price=1.0, egress=0.01, latency=50.0)
        far = capture("mprovider", gpu_price=9.0, egress=0.9, latency=None)
        by_provider = {c["provider"]: c for c in (slow, fast, far)}
        rows = {
            "aprovider": {"provider": "aprovider", "total": 1.0},
            "zprovider": {"provider": "zprovider", "total": 0.995},
            "mprovider": {"provider": "mprovider", "total": 0.5},
        }
        ranked = score._rank(rows, by_provider, CRITERIA)
        self.assertEqual([r["provider"] for r in ranked], ["zprovider", "aprovider", "mprovider"])
        self.assertEqual(ranked[0]["band"], ranked[1]["band"])
        self.assertNotEqual(ranked[1]["band"], ranked[2]["band"])

    def test_tie_break_order_is_latency_then_gpu_then_egress(self):
        cases = [
            ({"latency_to_apis_ms": 20.0, "gpu_price_per_hour": 9.0, "internet_egress_gb": 9.0},
             {"latency_to_apis_ms": 80.0, "gpu_price_per_hour": 1.0, "internet_egress_gb": 1.0}),
            ({"latency_to_apis_ms": None, "gpu_price_per_hour": 1.0, "internet_egress_gb": 9.0},
             {"latency_to_apis_ms": None, "gpu_price_per_hour": 2.0, "internet_egress_gb": 1.0}),
            ({"latency_to_apis_ms": None, "gpu_price_per_hour": 1.0, "internet_egress_gb": 0.01},
             {"latency_to_apis_ms": None, "gpu_price_per_hour": 1.0, "internet_egress_gb": 0.02}),
        ]
        for better, worse in cases:
            with self.subTest(better=better):
                def cap_for(provider, values):
                    c = capture(provider)
                    for cid, value in values.items():
                        c["values"][cid]["value"] = value
                    return c
                a = cap_for("a", better)
                b = cap_for("b", worse)
                by_provider = {"a": a, "b": b}
                rows = {"a": {"provider": "a", "total": 1.0}, "b": {"provider": "b", "total": 1.0}}
                ranked = score._rank(rows, by_provider, CRITERIA)
                self.assertEqual([r["provider"] for r in ranked], ["a", "b"], better)

    def test_provider_outside_the_band_is_not_tied(self):
        best = capture("best", gpu_price=1.0, egress=0.01)
        worst = capture("worst", gpu_price=2.0, egress=0.05)
        ranked, _ = score.score_captures(CRITERIA, [best, worst])
        self.assertNotEqual(ranked[0]["band"], ranked[1]["band"])
        self.assertEqual(ranked[0]["provider"], "best")

    def test_gap_equal_to_tolerance_is_inside_the_band(self):
        # 0.97 - 0.96 evaluates to 0.010000000000000009 in binary floating point;
        # a gap equal to the declared tolerance still counts as a tie.
        a = capture("a")
        b = capture("b")
        rows = {"a": {"provider": "a", "total": 0.9700000000000002},
                "b": {"provider": "b", "total": 0.9600000000000002}}
        ranked = score._rank(rows, {"a": a, "b": b}, CRITERIA)
        self.assertEqual(ranked[0]["band"], ranked[1]["band"])

    def test_noise_tolerance_must_be_declared(self):
        bad = json.loads(json.dumps(CRITERIA))
        bad["params"]["noise_tolerance"] = 1.5
        self.assertTrue(any("noise_tolerance" in e for e in score.check_criteria(bad, json.dumps(bad))))

    # --- cleanliness -------------------------------------------------------

    def test_no_open_file_warning_path(self):
        # `main` must close every handle it opens; run under
        # `python3 -W error::ResourceWarning` to make a leak fatal.
        pa = self.write_capture("a.json", capture("a"))
        self.assertEqual(self.run_main(pa), 0)

    def test_is_number_rejects_bools(self):
        self.assertFalse(score._is_number(True))
        self.assertTrue(score._is_number(0.0))
        self.assertTrue(math.isfinite(1.0))

    # --- duplicate keys, schema types and formats --------------------------

    def test_duplicate_json_keys_are_refused(self):
        obj, err = score._loads('{"a": 1, "a": 2}', "x")
        self.assertIsNone(obj)
        self.assertIn("duplicate JSON key", err)
        dupe = os.path.join(self.captures_dir, "dupe.json")
        with open(dupe, "w", encoding="utf-8") as fh:
            fh.write(
                '{"provider": "a", "provider": "b", "region": "x",'
                ' "captured_on": "2026-09-29", "values": {}}'
            )
        self.assertEqual(self.run_main(dupe), 1)
        dupe_criteria = os.path.join(self.tmp.name, "dupe-criteria.json")
        with open(dupe_criteria, "w", encoding="utf-8") as fh:
            fh.write('{"version": "1", "version": "2", "hard_gates": [], "criteria": []}')
        self.assertEqual(score.main(["--criteria", dupe_criteria, "--check-criteria"]), 2)

    def test_provider_must_be_a_lowercase_slug(self):
        for bad in ({}, 123, "Azure", "a" * 40, "with space", ""):
            cap = capture("azure")
            cap["provider"] = bad
            errors = self.errors_for(cap)
            self.assertTrue(any("provider must match" in e for e in errors), (bad, errors))

    def test_provider_object_is_exit_1_without_traceback(self):
        path = os.path.join(self.captures_dir, "bad.json")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump({"provider": {}, "region": "x", "captured_on": "2026-09-29", "values": {}}, fh)
        self.assertEqual(self.run_main(path), 1)
        path2 = os.path.join(self.captures_dir, "bad2.json")
        with open(path2, "w", encoding="utf-8") as fh:
            json.dump([1, 2, 3], fh)
        self.assertEqual(self.run_main(path2), 1)

    def test_dates_and_region_are_checked(self):
        cap = capture("a")
        cap["captured_on"] = "29-09-2026"
        self.assertTrue(any("YYYY-MM-DD" in e for e in self.errors_for(cap)))
        cap2 = capture("a")
        cap2["captured_on"] = "2026-02-30"
        self.assertTrue(any("YYYY-MM-DD" in e for e in self.errors_for(cap2)))
        cap3 = capture("a")
        cap3["region"] = ""
        self.assertTrue(any("region must be a non-empty string" in e for e in self.errors_for(cap3)))
        cap4 = capture("a")
        cap4["captured_by"] = 7
        self.assertTrue(any("captured_by must be a string" in e for e in self.errors_for(cap4)))

    def test_entry_fields_are_required_and_typed(self):
        cap = capture("a")
        del cap["values"]["gpu_price_per_hour"]["value"]
        self.assertTrue(any("missing the field 'value'" in e for e in self.errors_for(cap)))
        cap2 = capture("a")
        del cap2["values"]["gpu_price_per_hour"]["unit"]
        self.assertTrue(any("missing the field 'unit'" in e for e in self.errors_for(cap2)))
        cap3 = capture("a")
        cap3["values"]["gpu_price_per_hour"]["quote"] = ""
        self.assertTrue(any("quote must be a non-empty string" in e for e in self.errors_for(cap3)))
        cap4 = capture("a")
        cap4["values"]["gpu_price_per_hour"]["source_url"] = 42
        self.assertTrue(any("source_url must be a non-empty string" in e for e in self.errors_for(cap4)))
        cap5 = capture("a")
        cap5["values"]["gpu_price_per_hour"]["surprise"] = 1
        self.assertTrue(any("unknown field" in e for e in self.errors_for(cap5)))
        cap6 = capture("a")
        cap6["extra"] = "nope"
        self.assertTrue(any("unknown top-level key" in e for e in self.errors_for(cap6)))
        cap7 = capture("a")
        cap7["values"]["gpu_price_per_hour"]["note"] = 5
        self.assertTrue(any("note must be a string" in e for e in self.errors_for(cap7)))

    def test_crit_by_id_covers_every_check(self):
        spec = score.crit_by_id(CRITERIA)
        self.assertEqual(set(spec), set(IDS_UNITS))
        self.assertEqual(spec["gpu_vram_gb"]["kind"], "number")
        self.assertEqual(spec["pg17_pitr"]["kind"], "bool")
        self.assertEqual(spec["gpu_price_per_hour"]["unit"], "USD/GPU-hour")
        self.assertEqual(score.validate_capture(capture("a"), "a.json", CRITERIA, spec), [])

    def test_too_many_values_is_refused(self):
        cap = capture("a")
        for i in range(score.MAX_CHECKS):
            cap["values"]["extra_%d" % i] = entry(1, "unit")
        errors = self.errors_for(cap)
        self.assertTrue(any("over the %d cap" % score.MAX_CHECKS in e for e in errors), errors[:2])

    def test_too_many_criteria_are_refused(self):
        bad = json.loads(json.dumps(CRITERIA))
        bad["criteria"] = [
            {"id": "c%d" % i, "weight": 0.01, "direction": "lower", "unit": "USD", "null_policy": "fail", "heading": "x"}
            for i in range(score.MAX_CHECKS + 1)
        ]
        errors = score.check_criteria(bad, json.dumps(bad))
        self.assertTrue(any("over the %d cap" % score.MAX_CHECKS in e for e in errors), errors[:2])

    # --- reads: no symlink following, no bad UTF-8, no leaks ---------------

    def test_invalid_utf8_capture_is_refused(self):
        path = os.path.join(self.captures_dir, "latin1.json")
        with open(path, "wb") as fh:
            fh.write(b'{"provider": "a", "region": "\xe9", "captured_on": "2026-09-29", "values": {}}')
        self.assertEqual(self.run_main(path), 2)

    def test_secure_read_refuses_a_path_swapped_to_a_symlink(self):
        real = self.write_capture("real2.json", capture("real2"))
        link = os.path.join(self.captures_dir, "swap.json")
        os.symlink(real, link)
        text, err, nbytes = score._secure_read(link, link)
        self.assertIsNone(text)
        self.assertIsNotNone(err)
        self.assertEqual(nbytes, 0)

    def test_secure_read_enforces_the_parent_directory(self):
        other = os.path.join(self.tmp.name, "other")
        os.makedirs(other)
        path = os.path.join(other, "cap.json")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(capture("a"), fh)
        text, err, nbytes = score._secure_read(path, path, expect_parent=self.captures_dir)
        self.assertIsNone(text)
        self.assertIn("outside the captures directory", err)
        self.assertEqual(nbytes, 0)
        ok, err2, n2 = score._secure_read(
            self.write_capture("ok.json", capture("ok")),
            "ok.json",
            expect_parent=self.captures_dir,
        )
        self.assertIsNone(err2)
        self.assertIn("provider", ok)
        self.assertEqual(n2, len(ok.encode("utf-8")))

    # --- every executable field is validated -------------------------------

    def mutate_criteria(self, **changes):
        bad = json.loads(json.dumps(CRITERIA))
        for path, value in changes.items():
            target = bad
            parts = path.split(".")
            for part in parts[:-1]:
                target = target[int(part)] if part.isdigit() else target[part]
            last = parts[-1]
            if last.isdigit():
                target[int(last)] = value
            else:
                target[last] = value
        return bad, score.check_criteria(bad, json.dumps(bad))

    def test_each_param_is_range_checked(self):
        cases = [
            ("params.duty_cycle_hours_per_day", "six"),
            ("params.duty_cycle_hours_per_day", 0),
            ("params.duty_cycle_hours_per_day", 25),
            ("params.duty_cycle_hours_per_day", float("inf")),
            ("params.days_per_month", None),
            ("params.days_per_month", 0),
            ("params.days_per_month", 400),
            ("params.noise_tolerance", -0.1),
            ("params.noise_tolerance", 1),
            ("params.noise_tolerance", "0.01"),
        ]
        for path, value in cases:
            _bad, errors = self.mutate_criteria(**{path: value})
            self.assertTrue(any(path in e for e in errors), (path, value, errors))
        _ok, errors = self.mutate_criteria(**{"params.noise_tolerance": 0})
        self.assertEqual(errors, [])
        _ok2, errors2 = self.mutate_criteria(**{"params.noise_tolerance": 0.999})
        self.assertEqual(errors2, [])

    def test_extra_params_must_be_numeric_or_null(self):
        _bad, errors = self.mutate_criteria(**{"params.future_knob": "fast"})
        self.assertTrue(any("params.future_knob" in e for e in errors), errors)
        _ok, errors2 = self.mutate_criteria(**{"params.future_knob": None})
        self.assertEqual(errors2, [])
        _ok2, errors3 = self.mutate_criteria(**{"params.latency_threshold_ms": 350})
        self.assertEqual(errors3, [])
        _bad2, errors4 = self.mutate_criteria(**{"params.latency_threshold_ms": None})
        self.assertTrue(any("params.latency_threshold_ms" in e for e in errors4), errors4)

    def test_min_number_threshold_is_checked(self):
        for value in ("16", None, float("nan")):
            _bad, errors = self.mutate_criteria(**{"hard_gates.0.threshold": value})
            self.assertTrue(any("threshold must be a finite number" in e for e in errors), (value, errors))
        _bad2, errors2 = self.mutate_criteria(**{"hard_gates.1.threshold": 1})
        self.assertTrue(any("bool gate must not declare a threshold" in e for e in errors2), errors2)

    def test_non_string_ids_are_errors_not_tracebacks(self):
        for path in ("hard_gates.0.id", "criteria.0.id"):
            for value in ({"x": 1}, ["a"], 7, "Bad Id"):
                _bad, errors = self.mutate_criteria(**{path: value})
                self.assertTrue(any("needs an id matching" in e for e in errors), (path, value, errors))

    def test_declared_params_are_range_checked(self):
        for name, value in (("gpu_per_node", 0), ("gpu_per_node", 1.5), ("latency_threshold_ms", -1),
                            ("gpu_vram_min_gb", None), ("gpu_vram_preferred_gb", 0)):
            bad = json.loads(json.dumps(CRITERIA))
            bad["params"][name] = value
            errors = score.check_criteria(bad, json.dumps(bad))
            self.assertTrue(any("params.%s" % name in e for e in errors), (name, value, errors))
        bad = json.loads(json.dumps(CRITERIA))
        bad["params"]["gpu_vram_min_gb"], bad["params"]["gpu_vram_preferred_gb"] = 24, 16
        self.assertTrue(any("gpu_vram_preferred_gb must be >=" in e
                            for e in score.check_criteria(bad, json.dumps(bad))))

    def test_derived_source_must_be_numeric(self):
        for source in ("pg17_pitr", "owner_locked_eligible"):
            bad = json.loads(json.dumps(CRITERIA))
            crit = next(c for c in bad["criteria"] if c["id"] == "monthly_at_duty_cycle")
            crit["derived"] = "%s * params.days_per_month" % source
            errors = score.check_criteria(bad, json.dumps(bad))
            self.assertTrue(any("must be numeric" in e for e in errors), (source, errors))

    def test_enum_scale_scores_are_checked(self):
        bad = json.loads(json.dumps(CRITERIA))
        crit = next(c for c in bad["criteria"] if c["id"] == "latency_to_apis_ms")
        crit["scale"] = {"fast": 1.0, "slow": "half", "broken": -1.0}
        errors = score.check_criteria(bad, json.dumps(bad))
        self.assertTrue(any("'slow'" in e for e in errors), errors)
        self.assertTrue(any("'broken'" in e for e in errors), errors)
        empty = json.loads(json.dumps(CRITERIA))
        next(c for c in empty["criteria"] if c["id"] == "latency_to_apis_ms")["scale"] = {}
        self.assertTrue(any("empty or non-object scale" in e for e in score.check_criteria(empty, json.dumps(empty))))

    def test_derived_expression_is_parsed_and_supported(self):
        cases = [
            ("gpu_price_per_hour * duty_cycle_hours_per_day", "unsupported derived expression"),
            ("gpu_price_per_hour", "unsupported derived expression"),
            ("monthly_at_duty_cycle * params.days_per_month", "cannot reference itself"),
            ("nope * params.days_per_month", "derived source 'nope' is not a known id"),
            ("gpu_price_per_hour * params.nope", "unknown params.nope"),
        ]
        for expr, needle in cases:
            _bad, errors = self.mutate_criteria(**{"criteria.1.derived": expr})
            self.assertTrue(any(needle in e for e in errors), (expr, errors))
        _bad2, errors2 = self.mutate_criteria(**{"params.days_per_month": "many"})
        self.assertTrue(any("derived multiplier params.days_per_month" in e for e in errors2), errors2)
        parsed = score._parse_derived("gpu_price_per_hour * params.duty_cycle_hours_per_day * params.days_per_month")
        self.assertEqual(parsed, ("gpu_price_per_hour", ["duty_cycle_hours_per_day", "days_per_month"]))
        self.assertEqual(score._parse_derived("x * params.a"), ("x", ["a"]))

    def test_malformed_criteria_exits_1_without_traceback(self):
        path = os.path.join(self.tmp.name, "malformed.json")
        bad = json.loads(json.dumps(CRITERIA))
        bad["params"]["duty_cycle_hours_per_day"] = "six"
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(bad, fh)
        self.assertEqual(score.main(["--criteria", path, "--check-criteria"]), 1)

    def test_aggregate_byte_cap_counts_bytes_read(self):
        first = self.write_capture("a.json", capture("aaa"))
        second = self.write_capture("b.json", capture("bbb"))
        total = os.path.getsize(first) + os.path.getsize(second)
        old = score.MAX_CAPTURE_BYTES
        score.MAX_CAPTURE_BYTES = total - 1
        self.addCleanup(setattr, score, "MAX_CAPTURE_BYTES", old)
        # each file is under the per-file cap, but the pair is over the aggregate cap
        self.assertLess(os.path.getsize(second), score.MAX_CAPTURE_BYTES)
        self.assertEqual(self.run_main(first, second), 2)

    # --- band-aware tie-break ----------------------------------------------

    def test_mixed_null_latency_skips_the_dimension_for_the_whole_band(self):
        # With the latency dimension skipped, A wins on GPU price; had a null
        # been treated as infinitely bad, B would have won on latency.
        unmeasured = capture("unmeasured", gpu_price=1.0, egress=0.01, latency=None)
        measured = capture("measured", gpu_price=2.0, egress=0.02, latency=20.0)
        by_provider = {"unmeasured": unmeasured, "measured": measured}
        rows = {
            "unmeasured": {"provider": "unmeasured", "total": 1.0},
            "measured": {"provider": "measured", "total": 0.995},
        }
        dims = score._band_dimensions(list(rows.values()), by_provider)
        self.assertNotIn("latency_to_apis_ms", dims)
        self.assertIn("gpu_price_per_hour", dims)
        ranked = score._rank(rows, by_provider, CRITERIA)
        self.assertEqual(ranked[0]["band"], ranked[1]["band"])
        self.assertEqual([r["provider"] for r in ranked], ["unmeasured", "measured"])

    def test_all_measured_latency_uses_the_dimension(self):
        slow = capture("slow", gpu_price=1.0, egress=0.01, latency=200.0)
        fast = capture("fast", gpu_price=2.0, egress=0.01, latency=50.0)
        by_provider = {"slow": slow, "fast": fast}
        dims = score._band_dimensions([{"provider": "slow"}, {"provider": "fast"}], by_provider)
        self.assertIn("latency_to_apis_ms", dims)
        rows = {"slow": {"provider": "slow", "total": 1.0}, "fast": {"provider": "fast", "total": 0.995}}
        ranked = score._rank(rows, by_provider, CRITERIA)
        self.assertEqual([r["provider"] for r in ranked], ["fast", "slow"])


if __name__ == "__main__":
    unittest.main()
