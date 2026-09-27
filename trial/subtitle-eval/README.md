# 05.2.1-T1 — subtitle fidelity and sync evaluation

Machine leg of task 05.2.1-T1 (plan 05, story 05.2.1): four stub renders (`E1`–`E4`) measured with
`worker-node/checks/render_check.py` (`render-check.v1`, D9) and sampled for the owner's sync labels.
**Status: machine leg done for E1–E4; the task is NOT done.** The `L1`/`L2` rows (05.5.2-T1) and every
human column (the owner's labels and readability scores) are still open, so the task's first acceptance
check (6 rows, `on_time_share ≥ 0.95`, label counts summing to `sampled_cues`) **cannot pass yet** —
`results.csv` holds 4 rows and every human column is empty on purpose (labels are the owner's; this leg
never fills them from a heuristic).

## 1. Machine and pins

| | |
|---|---|
| Machine | owner Mac, macOS 27, arm64 (plan 01 DEC-2 machine) |
| MPT | pin `3d5f4e421927d61f3eac729cf4b711ac0b69d688`, `pyproject.toml` `1.3.7` (`worker-node/pins.env`) |
| MPT config | `--profile spike` (`render-mpt-config.py`), `127.0.0.1:18080`, `subtitle_provider = whisper`, `[whisper] large-v3-turbo / cpu / int8`, `max_concurrent_tasks = 1`, LLM keys empty (C19) |
| Fakes | `trial/tools/image_stub.py` on `127.0.0.1:18003`, `trial/tools/tts_stub.py` on `127.0.0.1:18004` → **zero spend** (03 D14) |
| Client | `trial/tools/mpt_client.py run --spec …` (D13/T79: no `--run-dir`, output under `$NTS_HOME/runs/<run_id>/`, copied to `$NTS_HOME/runs/subtitle-eval/<id>/`) |
| Check | `render_check.py run --task-dir … --aspect … --subtitles on --out …` — exit 0 for all four |
| Subtitles | `seed/subtitle-presets.v1.json` revision 1, `BeVietnamPro-Bold.ttf`, `sentence` mode, `none` animation |
| Log | `.nts/logs/mpt-spike-api-20260927-153603.log` (this session only) |
| Tools | `uv run --no-project --python 3.12`; ffmpeg/ffprobe 9.0.2 |

Runtime only, never committed: `$NTS_HOME/runs/subtitle-eval/` (`$NTS_HOME` = `<repo>/.nts`, 01 CR-11;
specs, run-dir copies, `sample.csv`, per-render `render-check.json`, the superseded E4 story files, the two
stub fixture dirs). Committed: `results.csv`, `README.md`, `trial/tools/subtitle_sample.py`,
`trial/fixtures/subtitle-eval-vi-names.txt` and `trial/fixtures/subtitle-eval-en-longclause.txt` (the last
one per T84/QA-0521-4).

## 2. The four renders

| id | lang | aspect | fixture (spec `story_file`) | run_id | MPT `task_id` | container | narration | cues | whisper |
|---|---|---|---|---|---|---|---|---|---|
| `E1-vi-portrait` | vi | portrait | `trial/fixtures/subtitle-preview-vi.txt` | `E1-vi-portrait` | `cc0a4ea8-4caa-4c2f-9bc6-486e72962385` | 34.11 s | 34.11 s | 10 | `vi` 1.00 |
| `E2-en-portrait` | en | portrait | `trial/fixtures/subtitle-preview-en.txt` | `E2-en-portrait` | `9739787e-b4d1-49f4-bc82-47b9ac573ac6` | 26.167 s | 26.159 s | 6 | `en` 1.00 |
| `E3-vi-landscape-names` | vi | landscape | `trial/fixtures/subtitle-eval-vi-names.txt` | `E3-vi-landscape-names` | `ab310bea-34dc-4800-a943-89c24834d8e2` | 34.11 s | 34.11 s | 8 | `vi` 1.00 |
| `E4-en-landscape-longclause` | en | landscape | `trial/fixtures/subtitle-eval-en-longclause.txt` | `E4-en-landscape-longclause` | `9b51cc15-7822-4394-882b-b0d8a5a7e85c` | 26.167 s | 26.159 s | 4 | `en` 1.00 |

All four reached `state = succeeded` with a non-null `output.subtitles` and `render_check.py` exit 0.
`source` names the **producer** of a row's media (the design table's column): `here` = produced by this
task with `mpt_client.py` + the stubs, `05.5.2-T1` = the trial render, `substitute` = a stand-in story
(T14) — so `here` is expected on all four E rows and is not a claim about the language. `L1-vi-landscape-5m`
and `L2-en-landscape-5m` (05.5.2-T1) do not exist yet, so the four short renders are the whole machine leg
of the six. The `E1`/`E2` source text is the **substitution** recorded in §7.2; `E4`'s story file is the
committed `trial/fixtures/subtitle-eval-en-longclause.txt`, byte-identical to the story the render used
(§7.10, T84/QA-0521-4).

## 3. Automatic measurements (`render-check.json`, copied verbatim into `results.csv`)

| id | zero_duration | overlapping | out_of_range | coverage_share | max_cue_chars | cps_p95 | sync_suspect | long_cues | text_matches_script | text_mismatch_cues |
|---|---|---|---|---|---|---|---|---|---|---|
| E1 | **2** | 1 | 0 | 0.9399 | 58 | **680.0** | 2 | 1 | **true** | 0 |
| E2 | 0 | 0 | 0 | 0.9488 | 115 | 21.23 | 0 | 2 | **true** | 0 |
| E3 | 0 | 0 | 0 | 0.9417 | 58 | 21.21 | 0 | 0 | **true** | 0 |
| E4 | 0 | 0 | 0 | 0.5727 | 112 | 18.75 | 0 | 1 | **true** | 0 |

- **Measure (a) cue-text fidelity holds on all four**: `text_matches_script = true`, `text_mismatch_cues = 0`.
  MPT's sentence mode rewrote every cue with the script chunk (C15), diacritics included — E3's names
  (`Nguyễn Thị Ánh Tuyết`, `Lý Thường Kiệt`, `Đỗ Minh Trí`, `Nguyễn Văn Điềm`) survive byte-for-byte in
  `subtitle.srt`.
- `coverage_share` ≥ 0.50 everywhere. **E4 is the low end (0.5727)**: the script has fewer clauses than the
  stub audio has whisper items, so 4 cues cover only 57 % of the narration — a script *shorter* than the
  audio is the case that squeezes the 0.50 floor. E1–E3 sit at 0.94.
- `cps_p95 = 680.0` on E1 is **not** a reading: it is `chars ÷ max(duration, 0.05)` over the two
  zero-length cues. `cps_p95` (and `sync_suspect`) are only meaningful when `zero_duration = 0`; see §7.6.
- `long_cues` counts cues over `maxCharsPerLine × maxLines` (D6: 42 × 2 = 84 landscape, 28 × 2 = 56
  portrait) and stays advisory (O5). Landscape: E4 carries the intended 112-char 3-line clause, E3 none.
  Portrait: **3 long cues** — E1 one (58 chars > 56) and E2 two (103 and 115 chars) — because the size 64
  portrait cap is much tighter than the preview fixture's clauses.

## 4. Why `E1` shows `zero_duration = 2` (stub artifact, not a render bug)

MPT's sentence mode maps **script chunk *i* onto whisper segment *i*** and rewrites the cue text; a chunk
left over when the whisper segments run out is written as `00:00:00,000 --> 00:00:00,000` (C15,
`subtitle.correct`). With the stubs the audio is a **fixed** mp3 — vi 34.109751 s / en 26.158866 s — so the
whisper segment list is fixed and short:

| stub audio | whisper segments (windows) | last window ends |
|---|---|---|
| `fx-vi/speech-en.mp3` (vi narration) | 10 | 34.08 s |
| `fx-en/speech-en.mp3` (en narration) | 8 | 25.76 s |

`E1`'s script (the 05.2.3-T1 preview narration, 10 clauses) consumed 9 of the 10 segments because one
clause merged two segments (`Hai người lặng im nhìn nhau rất lâu`, 9.42 → 17.24 s), so chunks 9 and 10
(`bà chỉ mỉm cười`, `rồi biến mất sau làn mưa trắng xóa`) landed at `00:00:00,000` → `zero_duration = 2`,
and those two cues also make `overlapping`/`non_monotonic` report 1. The same mechanism is visible in the
05.2.3-T1 preview render (12 clauses vs 10 segments → the same two trailing clauses at zero length). On
`L1` (5 min, audio that really narrates the script) the segment pool is far larger than the clause count,
which is exactly what `L1`/`L2` are for.

E3/E4 were sized to the segment budget for this reason (E3: 8 clauses; E4: 4 clauses, see §7.5).

## 5. Whisper language (measure (d))

Read only from the fixed log prefix `detected language: '` (module `./app/services/subtitle.py:71`, C15;
01.3.1 col 51), nothing else copied out of the log (C4):

| render | log line | value |
|---|---|---|
| E1 | 84 | `'vi'`, probability `1.00` |
| E2 | 210 | `'en'`, probability `1.00` |
| E3 | 328 | `'vi'`, probability `1.00` |
| E4 | 573 (and 452 for the superseded first spec) | `'en'`, probability `1.00` |

The detected language is the **audio's** language, never the script's (the script is not fed to whisper),
and no render sets `language`, so `initial_prompt` stays unset (D2/F16) — a misdetection can only move
timing, never the cue text. All four stub audios were detected correctly with probability 1.00.

## 6. Cue timing vs the stub audio

Every cue window in these four renders is a whisper segment window of the **fixed stub mp3**, not of a
narration that reads the script: the vi rows share the same 10 windows (0 → 34.08 s) and the en rows the
same 8 windows (0 → 25.76 s). Consequences to keep in mind when reading the sync columns: the cue windows
are plausible in length (whisper segments 1.66–5.58 s; a cue that merges two of them reaches 7.8 s in E4)
but they carry text the audio never spoke, so per-cue `chars ÷ (end − start)` measures the *mismatch*
between script length and audio length, not delivery speed. The 60–90 s of speech the task asks of E3's
names text cannot be represented by a 34.11 s stub mp3; E3 is therefore sized to the machine's real
budget (§7.3).

## 7. Deviations and open items for the TL (the T84 rulings are cited where they apply)

1. **The task is not done — the `L1`/`L2` rows are missing** (05.5.2-T1 blocked on the voice raters) →
   `results.csv` has 4 rows, the acceptance check that asserts 6 rows fails, and `on_time_share ≥ 0.95`
   cannot be evaluated. Expected for a machine-only leg.
2. **E1/E2 source substitution**: the task's sources ("first ≈ 60–90 s of L1", "L2") do not exist, so the
   05.2.3-T1 narration fixtures are used. `mpt_client.build_body` turns a story file's first non-empty line
   into `video_subject`, which is never subtitled, so the first source sentence is **not** in the subtitles:
   E1's script is the fixture's remaining 4 lines → 10 clauses, E2's 4 lines → 6 clauses (and in E1 two of
   those clauses additionally land at `00:00:00,000`, §4). E3/E4 keep an explicit title line in their story
   files so that every body clause is subtitled.
3. **No short render reaches the spec's 60–90 s, and none reaches the 20-cue sample** (accepted for the
   machine leg by **T84/QA-0521-7**): the stub mp3s are fixed, so the renders last E1 34.11 s, E2 26.167 s,
   E3 34.11 s, E4 26.167 s, and `sampled_cues` is 4–10 instead of 20. E3's names fixture is therefore 345
   body chars (8 clauses, ≈ 35 s of speech) rather than the spec's 60–90 s text, which would exhaust the vi
   stub audio's 10 segment windows and add ~12 zero-length cues. The 60–90 s text and the 20-cue label
   samples are carried by `L1`/`L2`.
4. **`zero_duration = 2` on E1** (§4): by the proposed `subtitles_present` floor (`zero_duration = 0`) the
   E1 stub render would fail the quality gate. The subject of the gate is the `L1`/`L2` render, not this one;
   the machine evidence here only shows the `zero_duration` counter firing exactly when whisper segments run
   out — the F02 detection path.
5. **E4 was rendered twice**: the first spec (7 clauses) left 1 zero-length cue (`cps_p95 1000.0`,
   `sync_suspect 1`, `coverage_share 0.9588`, task `33689e82-5eb2-44a1-b76a-a1b513bbf412`), the second
   (4 clauses) is clean and is the row. The first attempt is kept under
   `$NTS_HOME/runs/subtitle-eval/E4first-en-landscape-longclause/` as the measured example of §4 instead of
   being counted as a row or deleted.
6. **`cps_p95`/`sync_suspect` are unusable while `zero_duration > 0`** (E1: 680.0 / 2). Proposal for
   05.5.1-T2: gate on `zero_duration = 0` first, or compute `cps` over timed cues only, so one artifact
   never poisons the p95.
7. **`task_id = mpt_task_id`** on all four rows: M0 has no `mpt-adapter` yet, so the adapter-side UUIDv7 of
   D4 does not exist (the spike talks to MPT directly).
8. **`duration_s` is the MP4 container duration** (26.167 / 34.11 s) and `narration_seconds` is the
   `coverage_share` reference. Container − narration is **≤ 8 ms** here (34.11/34.11 s and 26.167/26.159 s),
   so **F17's ≈ 1.1 s tail is not exercised by these stub renders** — it belongs to the real 5-minute
   narrations of `L1`/`L2`.
9. **en evidence is stub-only for now** (T84/QA-0521-6): E2/E4 were rendered with the stub TTS serving an en
   mp3, which is the reading taken for this leg; 04 O2 governs the real en **voice** used by `L2`, not the
   stub audio. If en is not launched, `L2` becomes the second vi story (`source = substitute`, T14) and en
   subtitle evidence stays with E2/E4.
10. **E4's fixture path — resolved** (T84/QA-0521-4): the story the render used is committed byte-identical
    as `trial/fixtures/subtitle-eval-en-longclause.txt` (sha256 `78693745…`), the spec's `story_file` and the
    CSV `fixture` column point at it, and no re-render was needed (`cmp` over identical bytes).
11. **Two E1 sample rows are unlabelable**: cues 9–10 are the `00:00:00,000` leftovers the reviewer cannot
    play, and `sampled_cues = 10` counts them anyway. A pragmatic reading is to label them `wrong_window`
    (they are by definition not on the spoken onset); recorded for the calibration rather than patched,
    because the real 20-cue samples come from `L1`/`L2`.

## 8. Calibration — pending the owner's labels (measure (b)/(c)/(e))

`subtitle_sample.py` drew the samples; the label column is empty on every row (never filled from a
heuristic). The reviewer plays each video from `sample.csv`'s `start` column and writes one of `on_time`
(start within ± 0.5 s of the spoken onset), `early`, `late`, `wrong_window`; `on_time_share = on_time ÷
sampled_cues` to 4 decimals. `readability_vi`/`readability_en` (phone + desktop, diacritics, clipped lines)
and `reviewer` are the owner's as well.

Per render: `$NTS_HOME/runs/subtitle-eval/<id>/sample.csv` (`<id>` ∈ E1…E4), 4–10 rows each.

The `--csv` writer prefixes a cue text that starts with `=`, `+`, `-`, `@`, TAB or CR with a single quote
(spreadsheet-formula guard, SEC-0521-1) while stdout and the `chars` count keep the raw text, and `--csv`
refuses a path that is the `--srt` file itself (SEC-0521-3).

The confusion table for the chosen `sync_suspect` pair and its ± 20 % neighbours is **not built yet** — it
needs the labels. When they exist, tabulate precision/recall for `chars ÷ (end − start) > 20 / 25 / 30` and
`(end − start) > 9.6 / 12 / 14.4 s` against them, plus `coverage_share` ≥ 0.40 / 0.50 / 0.60; with fewer
than five positive cues overall the pair stays a [target] with this evidence attached (task method).

## 9. Proposed `RenderQuality:*` parameters (D10 — **a proposal for a TL ruling**)

`render-check.v1` only measures (D9); the numbers below are proposed here and fixed by 05.5.1-T2. The
machine leg gives the ranges, the labels will give the precision:

| proposed key | value | what it gates | evidence from this leg |
|---|---|---|---|
| `RenderQuality:MaxCpsPerCue` | 25 | per-cue `chars ÷ (end − start)` > 25 → cue counts to `sync_suspect` | clean rows 18.75–21.23 (`cps_p95`) |
| `RenderQuality:MaxCueSeconds` | 12 | per-cue `(end − start)` > 12 s → cue counts to `sync_suspect` | longest timed cue 7.82 s (E1), 7.80 s (E2/E4), 7.62 s (E3) |
| `RenderQuality:SyncSuspectShareHint` | 0.10 | `render-quality.v1` hint when `sync_suspect ÷ cues` > 0.10 | 0/4 rows on the clean renders |
| `RenderQuality:MinCues` | 1 | `cues ≥ 1` and `text_matches_script = true` | 4–10 cues, `true` on all four |
| `RenderQuality:MaxZeroDurationCues` | 0 | `zero_duration = 0` | 0 on E2/E3/E4; E1 = 2, a stub artifact (§4) |
| `RenderQuality:MinCoverageShare` | 0.50 | `coverage_share ≥ 0.50` | 0.5727 (E4, script shorter than the audio) … 0.9488 |
| `RenderQuality:LongCueCapLandscape` | 84 | `maxCharsPerLine × maxLines` = 42 × 2 → `long_cues`, advisory (O5) | 1 cue at 112 chars (E4) |
| `RenderQuality:LongCueCapPortrait` | 56 | 28 × 2 → `long_cues`, advisory (O5) | 3 long cues over the two portrait renders (E1 1 × 58; E2 2 × 103/115), cap 56 = D6 28 × 2 |

`subtitles_present` = `cues ≥ 1` ∧ `zero_duration = 0` ∧ `coverage_share ≥ 0.50` (task §Threshold
calibration, D9) is consistent with the table above; the `min(1.0, …)` cap in `coverage_share` and the
0.05 s divisor under `cps` are `render_check.py` internals, unchanged by this task.

**The values above are a proposal for a TL ruling (D10), not a decision.**

## 10. Reproduce

```bash
# 1. stubs + MPT spike (see worker-node/bin/start-mpt-api.sh --profile spike)
uv run --no-project --python 3.12 trial/tools/image_stub.py --port 18003 --calls-log "$R/image-stub-calls.jsonl"
uv run --no-project --python 3.12 trial/tools/tts_stub.py --port 18004 --fixtures "$R/fx-vi" --calls-file "$R/tts-stub-calls-vi.jsonl"
worker-node/bin/start-mpt-api.sh --profile spike          # 127.0.0.1:18080, teed log
# 2. one render at a time (T79: no --run-dir; copy $NTS_HOME/runs/<run_id>/ to $R/<id>/)
source worker-node/bin/lib.sh; NTS_MPT_API_KEY="$(secret nts.mpt.api_key)"; export NTS_MPT_API_KEY
NTS_MPT_BASE_URL=http://127.0.0.1:18080 uv run --no-project --python 3.12 trial/tools/mpt_client.py run --spec "$R/E1-vi-portrait.spec.json"
# 3. check + label sample
uv run --no-project --python 3.12 worker-node/checks/render_check.py run --task-dir "$R/E1-vi-portrait" --aspect portrait --subtitles on --out "$R/E1-vi-portrait/render-check.json"
uv run --no-project --python 3.12 trial/tools/subtitle_sample.py --srt "$R/E1-vi-portrait/subtitle.srt" --n 20 --seed E1-vi-portrait --csv "$R/E1-vi-portrait/sample.csv"
```

The vi renders need the tts stub restarted with `--fixtures "$R/fx-vi"` (its `speech-en.mp3` *is* the vi
narration: the stub serves only `speech-en.mp3`, so the vi audio is renamed into that slot; the reverse
for `fx-en`). Ports 18080/18003/18004 must be free first, and the spike profile refuses to start while a
stale `config.toml.pre-spike` exists (restore it into `config.toml` first). The four runs went
E1 (vi) → E2 (en) → E3 (vi) → E4 (en), one language batch at a time, one task at a time; every spec is
`$R/<id>.spec.json`, and E4's `story_file` is the committed
`trial/fixtures/subtitle-eval-en-longclause.txt` (§7.10).
