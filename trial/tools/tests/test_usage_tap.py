"""Unit tests for trial/tools/usage_tap.py (01.3.1-T2).

Regression for the 03.3.2-T1 finding: `numeric_leaves` kept every numeric JSON
leaf, so the upstream response ids under `usage.attribution.items` landed in the
JSONL as *keys* (`msg_…`/`rs_…`), leaking foreign ids into committed run files.
The fix rewrites those keys to `item_1..n` (insertion order) and leaves every
other key untouched (`request_fields` carries meaning, e.g. `text.format`).

Also covers `_write_all`: a single `os.write` may write fewer bytes than asked,
which would truncate a JSONL line.

Run:
    uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests -v
"""

import os
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import usage_tap as ut  # noqa: E402


class NumericLeavesTest(unittest.TestCase):
    def test_attribution_item_ids_become_item_n(self):
        usage = {
            "input_tokens": 319,
            "attribution": {
                "items": {
                    "msg_0a9ae80604119b08016ab987b5180087d0a4afb37786473bd6": {
                        "cache_write_tokens": 0,
                        "cached_tokens": 0,
                        "input_tokens": 119,
                        "output_tokens": 0,
                    },
                    "rs_0a9ae80604119b08016ab987b7739087d08723295fd3006a76": {
                        "input_tokens": 2,
                        "output_tokens": 516,
                    },
                },
                "request_fields": {"text.format": {"input_tokens": 7}},
            },
        }
        out = ut.numeric_leaves(usage)
        self.assertEqual(
            out["attribution"]["items"],
            {
                "item_1": {"cache_write_tokens": 0, "cached_tokens": 0,
                           "input_tokens": 119, "output_tokens": 0},
                "item_2": {"input_tokens": 2, "output_tokens": 516},
            },
        )
        # Sibling keys keep their names: only attribution.items is opaque.
        self.assertEqual(out["attribution"]["request_fields"],
                         {"text.format": {"input_tokens": 7}})
        self.assertEqual(out["input_tokens"], 319)
        self.assertNotIn("msg_", repr(out))
        self.assertNotIn("rs_", repr(out))

    def test_item_numbering_has_no_gaps(self):
        usage = {"attribution": {"items": {
            "msg_a": {"output_tokens": 1},
            "msg_b": {"output_tokens": 2},
            "msg_c": {"output_tokens": 3},
        }}}
        items = ut.numeric_leaves(usage)["attribution"]["items"]
        self.assertEqual(sorted(items), ["item_1", "item_2", "item_3"])

    def test_plain_usage_is_unchanged(self):
        usage = {"prompt_tokens": 10, "completion_tokens": 4, "total_tokens": 14,
                 "completion_tokens_details": {"reasoning_tokens": 2}}
        self.assertEqual(ut.numeric_leaves(usage), usage)

    def test_non_numeric_and_bool_dropped(self):
        self.assertEqual(ut.numeric_leaves({"a": 1, "b": "x", "c": True,
                                            "d": [1], "e": {"f": None}}),
                         {"a": 1})

    def test_non_dict_returns_none(self):
        self.assertIsNone(ut.numeric_leaves(None))
        self.assertIsNone(ut.numeric_leaves("usage"))


class WriteAllTest(unittest.TestCase):
    def test_short_writes_are_completed(self):
        """A writer that accepts 3 bytes per call must still emit every byte."""
        calls = []

        def fake_write(fd, view):
            calls.append(len(view))
            return os.write(fd, bytes(view[:3]))

        payload = b'{"run_id":"20260928-1022-en-landscape-01"}\n'
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "openai_usage.jsonl"
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o644)
            try:
                ut._write_all(fd, payload, fake_write)
            finally:
                os.close(fd)
            self.assertEqual(path.read_bytes(), payload)
        self.assertGreater(len(calls), 1)

    def test_zero_write_raises(self):
        with self.assertRaises(OSError):
            ut._write_all(1, b"x", lambda fd, view: 0)


if __name__ == "__main__":
    unittest.main()
