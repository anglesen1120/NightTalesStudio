"""Unit tests for trial/tools/run_manifest.py (voice id normalization).

Regression for 01.3.1-T4 / 01.2.2-T6: `start_run.sh --voice kokoro:X` (the
prefixed form in the T6 step-4 recipe) produced `kokoro:kokoro:X` in
run.json.voice_name, which is what finishes into sheet col 36.
D-1 adds the case-insensitive prefix collapse and makes a given-but-empty
name a usage error (exit 2) instead of writing an unusable id.
Run:
    uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests -v
"""

import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import run_manifest as rm  # noqa: E402

RUN_ID = "20260926-2359-en-landscape-99"   # no such run dir on disk


def start_argv(voice):
    argv = ["start", "--run-id", RUN_ID, "--lang", "en",
            "--aspect", "landscape", "--topic", "t", "--target-minutes", "5"]
    if voice is not None:
        argv += ["--voice", voice]
    return argv


class VoiceNameTest(unittest.TestCase):
    def test_bare_name_gets_kokoro_prefix(self):
        self.assertEqual(rm.normalize_voice("Ngọc Huyền"),
                         "kokoro:Ngọc Huyền")

    def test_prefixed_name_is_not_doubled(self):
        self.assertEqual(rm.normalize_voice("kokoro:Ngọc Huyền"),
                         "kokoro:Ngọc Huyền")

    def test_doubled_prefix_collapses(self):
        # run.json of the 2026-09-26 calibration run (T6 step 4 recipe).
        self.assertEqual(rm.normalize_voice("kokoro:kokoro:Ngọc Huyền"),
                         "kokoro:Ngọc Huyền")

    def test_prefix_is_case_insensitive_and_canonicalised(self):
        self.assertEqual(rm.normalize_voice("KOKORO:x"), "kokoro:x")
        self.assertEqual(rm.normalize_voice("Kokoro:x"), "kokoro:x")
        self.assertEqual(rm.normalize_voice("KOKORO:kokoro:x"), "kokoro:x")

    def test_other_provider_id_is_kept_verbatim(self):
        self.assertEqual(rm.normalize_voice("edge:vi-VN-HoaiMyNeural"),
                         "edge:vi-VN-HoaiMyNeural")

    def test_missing_voice(self):
        # None = the flag was not given (argparse default).
        self.assertEqual(rm.normalize_voice(None), "n/a:not-given")

    def test_given_but_empty_name_is_rejected(self):
        for bad in ("", "   ", "kokoro:", "KOKORO:", "kokoro:  ",
                    "kokoro:kokoro:"):
            with self.subTest(voice=bad):
                with self.assertRaises(ValueError):
                    rm.normalize_voice(bad)

    def test_cli_rejects_empty_voice_with_exit_2(self):
        # A usage error before any side effect: cmd_start validates right
        # after --run-id, so nothing is read or written.
        for bad in ("kokoro:", "KOKORO:  ", "   ", ""):
            with self.subTest(voice=bad):
                with self.assertRaises(SystemExit) as ctx:
                    rm.main(start_argv(bad))
                self.assertEqual(ctx.exception.code, 2)

    def test_cli_rejects_bad_run_id_with_exit_2(self):
        with self.assertRaises(SystemExit) as ctx:
            rm.main(["start", "--run-id", "not-a-run-id", "--lang", "en",
                     "--aspect", "landscape", "--topic", "t",
                     "--target-minutes", "5", "--voice", "x"])
        self.assertEqual(ctx.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
