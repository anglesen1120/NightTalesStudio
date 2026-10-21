# Run notes — 20260926-0818-en-landscape-01

- Task: 01.2.2-T6 wiring render + 01.3.1-T4 calibration (plan 01).
- MPT task id: `35ab583d-35d9-489a-89b6-ee8b89e28c8b` · `run_status = calibration`
- Story: `story_source = plan02` (script = `trial/fixtures/wiring-en.txt`, 102 words,
  6 sentences), `video_terms` omitted, `video_count = 1`, `video_aspect = 16:9`,
  `video_language = en`, `voice_name = kokoro:Ngọc Huyền`, `bgm_type = random`,
  `subtitle_enabled = true`.
- Output: `$NTS_HOME/src/mpt/storage/tasks/35ab583d-35d9-489a-89b6-ee8b89e28c8b/final-1.mp4`
  (copy at `$NTS_HOME/runs/<run_id>/final-1.mp4`) — 1920x1080 h264 + aac, 31.83 s,
  10.85 MB, `ffmpeg -i … -f null -` exit 0.
- MPT log: `$NTS_HOME/logs/mpt-api-20260926-080321.log`; proxy log:
  `$NTS_HOME/logs/proxy-20260926-080317.log`; tap log/JSONL: `$NTS_HOME/logs/tap-*.log`,
  `trial/runs/<run_id>/openai_usage.jsonl`.

## D11 conditions (machine not idle — read with care)

- Other agent sessions were active during the render (one read-only code review plus
  peer subagents); this is **not** a D11-clean machine.
- **Structural calibration only (performance review, 2026-09-26).** The box was
  already 723.2 MB into swap and in `warn` pressure before the first stage
  marker, and 403 of the 666 samples fall **outside** the task window
  01:18:57–01:23:33 (370 after the end record, 33 before the first marker) when
  the MPT tree was back to ~318 MB, so the RAM/wall columns (52–54 and the
  `swap-used`/`pressure-warn` issues) encode other consumers, not this run
  (gate fix 2026-09-26: `finish_run.py` now bounds cols 52–54 — and the
  `rss.csv` `all` row it writes — to the task window; **this** row keeps the
  pre-fix sampler-lifetime values).
  They MUST NOT be used as the 05.5.2 / 03.3.1 RAM/wall baseline: that needs a
  re-run of the 1-minute render on a quiesced machine, gated (01.3.1-T4 step 2)
  on `memory_pressure` reporting `normal` and `sysctl -n vm.swapusage` used
  within ~128 MB of the idle reading. The orchestrator re-runs next and appends
  a **second** calibration row; this row stays unchanged (D8 append-only) as
  structural calibration.
- Sampler: swap 723.2 MB already used at run start, peak 1800.0 MB; memory pressure
  `warn` for the whole run; free % min 36 → issues `swap-used`, `pressure-warn`.
- VieNeu idle RSS ~395–560 MB, proxy ~13–43 MB, MPT API peak 2056.8 MB (materials),
  total tree peak 2464.7 MB. Sampler overhead p95 59 ms per 1 s sample.

## Cost (O8 rates; relay path = DEC-9/DEC-11)

- Chat (gpt-6-luna), 1 call, `http_status 200`: 319 in / 35 out →
  (319×0.10 + 35×0.50)/1e6 = **$0.000049** (row value 0.000049) — below the
  expected < $0.001.
- Images (gpt-image-2 via the relay): 5 calls, all 200, no 429/5xx, no
  `openai image generation failed` line. Tap usage per call:
  `input_tokens` 6641–6719, `input_tokens_details.cached_tokens` 3712,
  `output_tokens` 74–137 (Σ 545), `total_tokens` 6715–6856.
  `image_cost_usd = n/a:no-price` (CR-17): `gpt-image-2` has no entry in
  `trial/prices.json` and `via = responses` is the relay host model (DEC-11).
- Hand-computed **luna-rate** value of the relay host tokens (informational only;
  the row is `n/a:no-price`, CR-17): input Σ 33,399 tokens, output Σ 545.
  - all input at the luna text rate $0.10/1M: 33399×0.10/1e6 + 545×0.50/1e6 = **$0.003612**
  - the same tokens valued at the O8 image rates would be $0.135–0.284, but that
    is **not a bound**: under DEC-11 the `via = responses` usage holds the
    **luna host model's** tokens, the relay reports no gpt-image-2 image-output
    tokens, and `trial/schema.md` forbids pricing host tokens at image rates.
- **Ceiling check — OPEN (owner-blocked): the T6 acceptance “the actual cost is
  ≤ the ceiling” cannot be marked PASS.** The image charge for this run is
  **unknown** — there is no price for `gpt-image-2` on the relay path ([OPEN]: relay price,
  owner) and no dashboard (DEC-9), so "actual cost ≤ ceiling" cannot be
  confirmed in dollars. What is measurable: the run used **5 image calls**
  against a ceiling of 15 images and an abort threshold of 20 (T6 step 9) →
  the image-count side of the ceiling passes; the chat side is $0.000049
  (≪ $0.001 expected). The dollar comparison stays open with the relay price.
- The 10,000-token-per-image placeholder is a deliberate over-estimate and
  **stays until a relay `gpt-image-2` price or token count is known ([OPEN],
  owner)**: the 137/109 figures measured here are the luna **host** model's
  output tokens from the Responses wrapper, and the relay reports no gpt-image-2
  image-output tokens, so substituting them would underprice every image by
  orders of magnitude.

## Q1–Q10 outcomes

| # | Outcome | Evidence / decision |
|---|---|---|
| Q1 | **changed / closed** | No provider-independent end-of-audio line exists. Candidates: `<provider> tts succeeded: <voice_file>` (`voice.py:2212`, the openai-compatible path used by kokoro) and `completed, output file: <file>` (`voice.py:1357`, edge) — provider-specific, and **absent** on the `custom_audio_file` path. Adopting one would also open a marker gap (the acceptance requires `gaps_seconds = 0`). Decision: `AUDIO_END_RE` stays `None`; the audio window is `## generating audio` → `## generating subtitle` = 6.0 s and also covers the post-TTS duration probe (the TTS end 08:19:06.4 is the proxy log's own `ts + wall_ms`, not an MPT log line; the MPT log has second resolution and shows the subtitle marker at 08:19:06). Recorded in `trial/schema.md` (stage table + change log). |
| Q2 | confirmed | `storage/tasks/<id>/` holds `final-1.mp4`, `audio.mp3`, `script.json`, `subtitle.srt` plus intermediates `combined-1.mp4`, `openai-image-<hash>.png` / `.png.mp4`. `finish_run.py` defaults (`final-1.mp4`, `script.json`) worked with no `--output`; log paths are relative to the MPT cwd. |
| Q3 | confirmed (field absent) | Chat usage = `{prompt_tokens: 319, completion_tokens: 35, total_tokens: 354}` — no `prompt_tokens_details` object at all → issue `cached-tokens-field-absent`, `story_cached_input_tokens = 0`; cost charges all prompt tokens at the uncached rate (conservative). Default rule works as designed; no code change. |
| Q4 | **changed** | `response.quality` / `response.size` **are** echoed. MPT omits `quality` (`request.quality = null` on all 5 lines → `auto` per O6/CR-3) and the resolved value varies per call inside one run: `image_quality_returned = low:3\|medium:2`, `image_quality = low` (most frequent). One call echoed `size = 1370x1148` ≠ requested `1536x1024` → `image_size = 1370x1148+1536x1024` (rule = distinct echoed sizes joined). `n/a:not-echoed` does not apply. For 03 (CR-3): the per-image quality is not pinnable; who resolves `auto` to `low`/`medium` was not traced (relay). |
| Q5 | still ⚠️ | No non-200 image line occurred (0×429, 0×5xx, 0 moderation blocks), so "non-200 calls are not billed" stays [INFERENCE]; it needs the dashboard cross-check, which is owner-blocked on the relay. |
| Q6 | not in scope here | The sampler's macOS 27 sources worked (`pressure_level` 1/2 and `free_pct` parsed; `memory_pressure_max = warn`), which is 01.3.2-T1's check. |
| Q7 | confirmed | MPT venv pins `openai 2.24.0`: `DEFAULT_TIMEOUT = Timeout(connect=5, read=600, write=600, pool=600)`, `DEFAULT_MAX_RETRIES = 2`. `app/services/llm.py:618` builds `OpenAI(api_key=…, base_url=…)` with **no** explicit timeout, so the SDK read timeout 600 s ≤ `TAP_UPSTREAM_TIMEOUT_S` 610 ✓ — no change to `start-tap.sh`. Margin is only 10 s; MPT's own chat loop retries 5× and the image call uses (30, 300) s (M4). |
| Q8 | confirmed | Proxy line `ts 01:19:00.398Z` (voice `Ngọc Huyền`, `chars 557`, `audio_seconds 31.8`, `vieneu_ms 6001`, `ffmpeg_ms 4`, `status 200`) lies inside the audio window 01:19:00–01:19:06 → `run.json.sources.tts_seconds_audio = "proxy-log"`; no ffprobe fallback used. |
| Q9 | confirmed | With `video_script` supplied and `video_terms` omitted, **`## generating video script` is still logged** (`task.py:293`) while the script LLM call is skipped; the terms LLM call runs (`task.py:314` → `llm.py:895/937`, `completed:` list parsed by `ast.literal_eval`, 5 terms). All 7 markers present, `story_wall_seconds = 3.0` numeric, 1 chat call only → story columns hold the terms call and the row carries `story-cost-terms-only`. |
| Q10 | still ⚠️ (owner-blocked) | The OpenAI dashboard is not reachable under DEC-9 (the upstream is a relay, not api.openai.com), so the next-day per-project cross-check cannot be done here; `dashboard_cost_usd_check` stays `n/a:pending-dashboard` and the relay-account usage check is the owner's. The UTC day boundary therefore remains [INFERENCE]. |

## Parser / tooling confirmations (T4 steps 4–10)

- Header regex: **80** records matched by the two `_HEADER_RES` forms (78 + 2) in
  the log as it stood when the run finished, equal to the 80 records carrying a
  level column, and **0** timestamp positions left unmatched → no `LEVEL` padding
  change needed (re-counted 2026-09-26; the draft's 67 was taken mid-run while the
  log was still being written). Of the 80, **55** fall inside the run window
  08:18:22–08:23:33; the other 25 all precede `started_at` (08:03 startup, the
  earlier failed task `3be8f89a` at 08:11:36–40, and five 08:18:05 warnings —
  four 401 `invalid API key` plus a blocked browser origin). A
  concurrent task later appended 14 records (08:52), so the file now holds 94.
- Markers: all 7 found once each; windows contiguous, `gaps_seconds = 0.0`,
  Σ windows = 276.0 = `total_seconds`; first marker 01:18:57 ≥ `run.started_at`
  01:18:22; end 01:23:33 ≤ `finished_at` 01:30:04. `total_wall_seconds = 276.0`
  matches the manual log span (08:18:57 → 08:23:33) exactly.
- `task <id> finished, generated 1 videos.` matched; the failure wording
  `task failed, task_id: <id>, stage: <stage>, error: …` (`task.py:267`) matches
  `MARKERS["failed"]` (verified against a real failed line from the earlier
  `stop_at: audio` task in the same log).
- `image material rendered:` count 5 = tap `image_count` 5 → no
  `image-count-mismatch` issue. `openai image generation failed` count 0.
- whisper: `detected language: 'en', probability: 1.00`,
  `complete, elapsed: 11.35 s`, `loading model: ./models/whisper-large-v3-turbo,
  device: cpu, compute_type: int8` (offline local path, no hub download).
- Terms list: parsed with `ast.literal_eval` → `terms_count = 5`.
- Sampler coverage: 666 samples over the **700 s** between sampler start
  (01:18:23Z) and the last sample (01:30:03Z) → **95.1 % sampler-span density**
  (the `rss.csv` `all` row 01:18:23Z–01:30:04Z, 666 samples). T4 step 10's own
  rule divides the `rss_raw` rows by `stages.json total_seconds`: **263 samples
  in the 276 s task window = 95.3 %** (262 = 94.9 % with end-exclusive windows)
  → **borderline pass**, not comfortable. Every stage window with a non-zero
  width has samples. **`stage:script` has
  `samples = 0`** — its window is 0.0 s wide because `## generating video script`
  and `## generating video terms` land in the same logged second when the script
  LLM is skipped (second-resolution tee'd log); the row therefore shows `NA` in
  that stage's `rss.csv` line (auxiliary file; the sheet's RAM columns are
  unaffected and all numeric). `story_wall_seconds` (col 49) still covers
  script+terms = 3.0 s by definition.
- Plausibility vs the 01.2.3-T2 baseline (`trial/baselines/whisper-mac-20260925.json`):
  `ram_peak_whisper_mb` 1984.6 vs en-1000 2134.4 (−7.0 %) and en-5min 2278.7
  (−12.9 %) → within ±25 %. whisper RTF here 11.35/31.8 = 0.357 vs baseline en
  0.362 — excellent agreement. TTS RTF 6006 ms / 31.8 s = 0.19; `tts_chars` 557
  equals the proxy `chars`.

## Code/default changes made in this task

1. `trial/tools/run_manifest.py` — new `normalize_voice()` used for
   `run.json.voice_name`; it collapses an already-present `kokoro:` prefix (and
   repeated prefixes) instead of blindly adding one. Reason: T6 step 4 passes
   `--voice "kokoro:<id>"` while the CLI contract stores `kokoro:<name>`, so the
   first run recorded `kokoro:kokoro:Ngọc Huyền`, which would have been appended
   to sheet col 36 (D8: rows are never hand-edited). `--voice` help updated.
   Gate fix 2026-09-26: the prefix match is case-insensitive (`KOKORO:x` →
   `kokoro:x`) and a given-but-empty `--voice` (`""`, `"   "`, `"kokoro:"`) is
   now exit 2 (usage error); an omitted `--voice` still records
   `n/a:not-given`.
2. `trial/runs/20260926-0818-en-landscape-01/run.json` — `voice_name` repaired
   once, `kokoro:kokoro:Ngọc Huyền` → `kokoro:Ngọc Huyền`, via `normalize_voice`
   (recorded here; nothing else in the file changed).
3. `trial/tools/finish_run.py` — image input tokens: the relay's `usage` has
   `input_tokens` + `input_tokens_details.cached_tokens` and **no** O6
   `text_tokens`/`image_tokens` split. Cols 43/44 now report
   `n/a:usage-split-absent` instead of a silent `0` (the run had 33,399 input
   tokens), and col 17 uses the same reason when a model **has** a price but the
   split is missing (otherwise the cost would silently drop the input
   component). Unpriced relay rows keep `n/a:no-price` (CR-17). The rule fires
   when **any** 200 image line lacks the split (a mixed batch would otherwise
   sum only the lines that carry it). Gate fix 2026-09-26: it now requires
   **both** O6 keys (`and`, not `or`), so a half-filled `input_tokens_details`
   is also `n/a:usage-split-absent` for cols 43/44 and 17 (L-1 / D-2).
   New fixture `trial/tools/tests/fixtures/openai_usage-relay-split-absent.jsonl`
   (the real relay numbers, no prompt text) + tests
   `test_image_input_split_absent_is_na_not_zero` and
   `test_image_input_split_absent_wins_when_mixed`.
4. `trial/tools/tests/test_run_manifest.py` (new) — 5 cases for
   `normalize_voice` including the doubled-prefix regression.
5. `trial/schema.md` — new n/a reason `usage-split-absent`; cols 17/43/44 rules;
   Q1/Q3/Q4/Q9 notes in the dictionary and stage table; change-log entry for this
   run (including the two limits of the new rule, below). No marker regex
   changed → `MARKER_SET` stays 1.
6. Unit tests: `uv run --no-project --python 3.12 -m unittest discover -s trial/tools/tests`
   → **32 tests, OK** at the time of this run. After the gate fixes (D-1…D-3,
   SEC-T6-1, L-4, L-5) the suite is **42** = 33 `test_finish_run` + 9
   `test_run_manifest`.

Two limits of the new rule, documented in `trial/schema.md`'s change log for
03/05.5.2: (a) cols 16/45 keep summing the relay host model's `output_tokens`
(deliberate T3 behaviour, pinned by T-P5 `test_t_p5_responses_via_no_price_tokens_summed`),
so on a relay row those two columns hold **luna host tokens, not gpt-image-2
image-output tokens** (74–137 per line here, Σ 545, mean 109.0); (b)
`tap_day_cost` still prices every line through `_line_cost`, which drops the
input component of a priced split-less line — a gap in the next-day delta path,
not in this sheet row.

## n/a cells in the appended row (all explained)

| Column | Value | Why |
|---|---|---|
| `image_cost_usd` | `n/a:no-price` | CR-17: the image model `gpt-image-2` has no `trial/prices.json` entry on the relay path (`via = responses`); the relay's image charge is unknown ([OPEN], owner) — see the Cost section |
| `image_input_text_tokens_total` | `n/a:usage-split-absent` | the relay `usage` has no O6 `text_tokens`/`image_tokens` split |
| `image_input_image_tokens_total` | `n/a:usage-split-absent` | same |
| `image_cost_per_image_usd` | `n/a:component-na` | derived from col 17 |
| `usd_per_output_minute` | `n/a:component-na` | derived from col 17 |
| `dashboard_cost_usd_check` | `n/a:pending-dashboard` | owner-blocked: OpenAI dashboard n/a under DEC-9 (relay upstream) |

## Playback evidence (automated stand-in for T6 step 8 "watch once")

`ffmpeg -v error -i final-1.mp4 -f null -` → exit 0 (full decode). Frames at
t = 2, 10, 20, 29 s are 4 distinct images (different md5) of the night-town
scene, and the frame at t = 3 s carries the burned-in English subtitle `and a
single lantern glows above the stone bridge` matching `subtitle.srt` cue 2.
Speech is audible and English: `volumedetect` gives mean −22.5 dB / max −7.2 dB
for the final mix (voice-only track mean −19.5 dB / max −4.3 dB), and the mix
differs from the voice-only track, so a music/normalisation step ran.
**"Music is quiet under the voice" is NOT verified**: `volumedetect`/`astats`
measure the combined track, and the subtitle gap 1.94–2.22 s is non-silent in
both the voice-only file (−25.1 dB RMS) and the mix (−26.8 dB RMS), so no
measurement separates the bed from the voice. Known facts only: the placeholder
`resource/songs/nts-ambient-pad-cc0.mp3` is present, `bgm_type = random`,
`bgm_volume = 0.2`, `grep -c 'no background music files found'` = 0, and MPT logs
no line for the `random` BGM pick → **owner listen-check needed**.

## Tap observation (01.3.1-T2, for the record)

- The tap log line's `ts` is the **response completion** time and its third field
  is `latency_ms` (chat: JSONL `ts` 01:18:57.760Z + 2623 ms = 01:19:00.383Z vs
  tap 01:19:00.385Z; image 1: 01:19:25.931Z + 37908 ms = 01:20:03.839Z vs tap
  01:20:03.840Z). The jsonl ts + `latency_ms` is therefore within 1–2 ms of the
  tap ts on 5 of the 6 lines and **not exact**; the residual is `latency_ms`
  rounding. The
  JSONL `ts` (the D6 field the collector uses for run attribution and
  `tap_day_cost`) is the **request arrival**. Reading the tap log live therefore
  shows each call only when it finishes.
- 6 request lines, all `200`; the tap log carries no credential header text
  (key/header grep = 0) and there is no `trial/runs/no-run/` JSONL, i.e. no call
  happened outside this run's window (relevant to the Q5/Q10 delta
  investigation).

## Open items handed on

- Q5 and Q10 stay open: the dashboard cross-check is unavailable (DEC-9 relay
  upstream), so the OpenAI dashboard view and the relay-account usage check are
  the owner's, and the image charge for this run is unknown until a relay price
  for `gpt-image-2` is supplied.
- Next: **re-run 01.3.1-T4 on a quiesced machine** (T4 step 2 gate:
  `memory_pressure` normal, swap used within ~128 MB of the idle reading) and
  append a **second** calibration row (D8); this row stays as structural
  calibration only.
- 03 (CR-3 → **01 CR-19**): quality/size on the relay are per-call
  `auto`/variable, and the image usage shape differs from O6 (`cached_tokens`
  instead of `text_tokens`/`image_tokens`) → pricing/rate pacing must be
  re-derived. Also cols 16/45 on a relay row hold luna **host** tokens
  (Σ 545 here), not gpt-image-2 image-output tokens.
- 05 (Q1/Q9 → **01 CR-18**): no end-of-audio marker; `## generating video script`
  is logged even with a supplied script; `data.task_id`, `final-1.mp4`,
  `video_aspect "16:9"`, `video_language "en"` and `video_terms: null` (omitted)
  all behaved as the 05.4 notes predicted — first sample for 05.4.1.
- 04 (CR-1 → **01 CR-20**): proxy log line matched the audio window exactly; no
  change.
- 01 (**01 CR-21**): the local text fixes in `01.2.2-T6` / `01.3.1-T4` /
  `01.3.1 plan.md` §2 (bare `--voice` id, `TASK_ID` UUID validation, the observed
  ✅ flips, the quiesced-machine gate, the corrected counts in this file).
