# trial/measurements.csv — schema v2

Column dictionary, format rules, word-count rule, cost formulas and stage windows
for the trial measurement sheet. Writers: `finish_run.py` (01.3.1-T3). Consumers:
02 (WPM), 03.3.1, 12.1.1 — names are frozen; a v3 appends only, never renames or
reorders (D8). Spec: `plans/features/01-environment-setup/01.3.1-measurement/01.3.1-T1-schema-v2-dictionary-prices.md`.

## Format rules

- RFC 4180; UTF-8 without BOM; CRLF; comma separator; fields containing `,` `"` or line breaks are quoted by the `csv` module; `topic` and `issues` MUST NOT contain line breaks (collector replaces `\r\n`/`\n` with a space).
- Decimals with `.`; no thousands separators. USD columns: 6 decimals (`0.012345`). Seconds: 1 decimal. MB = MiB = 2^20 bytes, 1 decimal (`output_size_mb` 2 decimals). Token counts: integers.
- Timestamps ISO-8601 UTC with `Z` (`2026-09-25T03:30:12Z`).
- No blank cell. Missing value = `n/a:<reason>`, reason `[a-z0-9-]+` (e.g. `n/a:usage-absent`). Codes used: `usage-absent`, `usage-split-absent`, `no-llm-calls`, `story-source-plan02`, `no-price`, `no-proxy-log`, `not-echoed`, `marker-missing`, `run-failed-at-<stage>`, `no-samples-in-window`, `pressure-unavailable`, `pending-dashboard`, `not-given`, `no-tap`, `no-images`, `zero-duration`, `component-na`. This form applies to the **sheet**; the auxiliary `rss.csv` uses bare `NA` instead (see Auxiliary run files).
- `issues` (col 31) closed vocabulary of collector codes (`validate_row` rejects any other collector code; free text only via `--issue`, which the collector prefixes with `note:`): `story-cost-terms-only` (every row with `story_source = plan02`, whatever the chat-line count), `cached-tokens-field-absent`, `quality-not-echoed`, `image-count-mismatch:<tap>/<log>`, `tap-no-lines`, `tap-crash`, `tap-busy`, `tap-foreign-call` (NTS01-SEC-10: any JSONL line in the run with `http_status = 401` or `model = null` for a `/chat/completions`|`/images/generations` endpoint — a caller other than the configured MPT, since MPT always sends a valid key and a `model`), `swap-used`, `pressure-warn`, `stage-gap:<seconds>`, `dashboard-delta-<n>pct`. Free-text notes: `note:<text>` (no `;`, no newline); an `--issue` text that already starts with `note:` is stored as given, not prefixed twice.
- `schema_version` column = `2`. A future v3 appends columns only; never renames or reorders (02 and 03 consume names).

## Column dictionary (schema v2)

Columns 1–31 = 01 §5.3 (names unchanged). Columns 32–58 appended.

| # | Column | Type | Unit | Source | Formula / definition | n/a rules |
|---|---|---|---|---|---|---|
| 1 | `run_id` | string | — | run.json | D10 `YYYYMMDD-HHMM-<lang>-<aspect>-<nn>` | never |
| 2 | `date` | timestamp | UTC | run.json | `started_at` (run start, written by `start_run.sh`) | never |
| 3 | `mpt_commit` | string | 40 hex | run.json | `pins.MPT_COMMIT`; collector asserts `heads.mpt` equals it | never (mismatch → start refused) |
| 4 | `vieneu_commit` | string | 40 hex | run.json | `pins.VIENEU_COMMIT`; asserts `heads.vieneu` | never |
| 5 | `language` | enum | — | run.json | `vi` \| `en` (D10) | never |
| 6 | `topic` | string | — | manual | owner-entered topic/title given to `start_run.sh`; no personal data | never |
| 7 | `target_minutes` | decimal | min | manual | `start_run.sh` argument | never |
| 8 | `story_model` | string | — | tap | distinct `model` of `endpoint = /chat/completions` lines, sorted, joined `+`; with `story_source = plan02` these are the terms call(s) only | `n/a:story-source-plan02` if `story_source = plan02` and 0 chat lines; else `n/a:no-llm-calls` (0 chat lines) |
| 9 | `story_input_tokens` | int | tokens | tap | Σ `usage.prompt_tokens` over chat lines with `http_status = 200` (all MPT calls: script, terms, retries — M5) | as col 8; `n/a:usage-absent` if any 200 chat line lacks `usage` |
| 10 | `story_output_tokens` | int | tokens | tap | Σ `usage.completion_tokens` (200 lines); includes reasoning tokens ⚠️ (12 §5.7) | as col 9 |
| 11 | `story_cost_usd` | decimal(6) | USD | tap + prices.json | `((in − cached) × r_in + cached × r_cached + out × r_out) / 1e6` with `in` = col 9, `cached` = col 39, `out` = col 10, rates of `gpt-6-luna` (0.10 / 0.01 / 0.50) | as col 9; `n/a:no-price` if a model has no price entry |
| 12 | `image_model` | string | — | tap | distinct `model` of `endpoint = /images/generations` lines, joined `+` | `n/a:no-images` (0 image lines) |
| 13 | `image_quality` | string | — | tap | **effective** quality: the single `response.quality` value of 200 image lines; if several, the most frequent (full distribution in col 42); if no echo: the requested value, or `auto` when omitted (O6 default) + issue `quality-not-echoed` | `n/a:no-images` |
| 14 | `image_size` | string | px | tap | distinct `response.size` (fallback `request.size`) of 200 image lines, joined `+` (e.g. `1536x1024`) | `n/a:no-images` |
| 15 | `image_count` | int | images | tap | count of image lines with `http_status = 200` (billed images [INFERENCE Q5]); cross-check vs mpt-log `image material rendered:` lines → mismatch adds issue `image-count-mismatch:<tap>/<log>` | never (0 is a value) |
| 16 | `image_output_tokens_total` | int | tokens | tap | Σ `usage.output_tokens` over 200 image lines | `n/a:usage-absent` if any 200 image line lacks `usage`; `n/a:no-images` |
| 17 | `image_cost_usd` | decimal(6) | USD | tap + prices.json | `(Σ text_in × 5 + Σ image_in × 8 + Σ out × 30) / 1e6` with `text_in` = col 43, `image_in` = col 44, `out` = col 16 (O8 rates from prices.json); cached-input rates unused (image `usage` reports no cached field, O6); per-image models (`unit: per_image`): `Σ n × per_image`, VND rows ÷ `fx_vnd_per_usd` (02 CR-27) | token-priced lines: `n/a:usage-absent` as col 16, `n/a:usage-split-absent` when the model has a price but the input split is missing (would silently drop the input component); per-image lines need neither usage nor the split, only `request.n` (QA-0928-3, CRV-0928-3); `n/a:no-price` when any line has no price, or a non-finite / missing fx or per-image amount (SEC-0928-2) |
| 18 | `moderation_blocked_count` | int | calls | tap | image lines with `error_code = "moderation_blocked"` (O6) | never |
| 19 | `tts_seconds_audio` | decimal(1) | s | proxy-log → ffprobe | Σ `audio_seconds` of proxy lines with `status = 200` inside the audio window (CR-1); fallback: `ffprobe` duration of the MPT task audio file (Q2); source in `run.json.sources` | `n/a:no-proxy-log` if neither available |
| 20 | `tts_wall_seconds` | decimal(1) | s | proxy-log → mpt-log | Σ (`vieneu_ms` + `ffmpeg_ms`) / 1000 of those proxy lines; fallback: audio window length | never (window always exists unless failed: `n/a:run-failed-at-<stage>`) |
| 21 | `whisper_wall_seconds` | decimal(1) | s | mpt-log | subtitle window (`## generating subtitle` → next marker); includes model load; whisper `complete, elapsed: <s> s` also stored in `stages.json` | `n/a:marker-missing`, `n/a:run-failed-at-<stage>` |
| 22 | `render_wall_seconds` | decimal(1) | s | mpt-log | `## combining video` → `task <id> finished` (combine + final video; per-image zoom clips are in col 50) | as col 21 |
| 23 | `total_wall_seconds` | decimal(1) | s | mpt-log | first stage marker of the task → `task <id> finished` (or `task failed`) | never |
| 24 | `ram_peak_vieneu_mb` | decimal(1) | MiB | sampler | max `vieneu_rss_kb` / 1024 over the audio window (VieNeu process tree, port 8000) | `n/a:no-samples-in-window` |
| 25 | `ram_peak_whisper_mb` | decimal(1) | MiB | sampler | max `mpt_rss_kb` / 1024 over the subtitle window (whisper runs in the MPT process, M3) | as col 24 |
| 26 | `ram_peak_render_mb` | decimal(1) | MiB | sampler | max `mpt_rss_kb` / 1024 over combine + final-video windows (MPT tree incl. ffmpeg children, F04) | as col 24 |
| 27 | `output_duration_sec` | decimal(3) | s | ffprobe | `format.duration` of the final video | `n/a:run-failed-at-<stage>` |
| 28 | `output_size_mb` | decimal(2) | MiB | ffprobe | `format.size` / 1048576 | as col 27 |
| 29 | `words_in_story` | int | words | script | word-count rule below on the narrated script | never for a finished run |
| 30 | `words_per_minute_measured` | decimal(1) | words/min | script + col 19 | `words_in_story / (tts_seconds_audio / 60)`; vi counts syllables, en counts words — **not comparable across languages** | `n/a:component-na` if col 19 is n/a; `n/a:zero-duration` |
| 31 | `issues` | string | — | collector + manual | `;`-joined issue codes and `--issue` texts, sorted; `none` if empty; never story text | never (`none`) |
| 32 | `schema_version` | int | — | constant | `2` | never |
| 33 | `run_status` | enum | — | run.json / mpt-log | `calibration` \| `trial` \| `failed` (failed if `task failed` marker or `--status failed`) | never |
| 34 | `machine` | string | — | env | `<hw_model>/<cpu_brand>/<round(memory_bytes/2^30)>GB/macOS <macos_version>` e.g. `Mac16,10/Apple M4/16GB/macOS 27.0` | never |
| 35 | `video_aspect` | enum | — | run.json | `landscape` \| `portrait` (D10 label; MPT `VideoAspect` value ⚠️ in `run.json`) | never |
| 36 | `voice_name` | string | — | run.json | `kokoro:<VieNeu voice>` given with `--voice` | `n/a:not-given` |
| 37 | `story_source` | enum | — | run.json | `mpt` (MPT LLM wrote the script) \| `plan02` (script supplied as `video_script`) | never |
| 38 | `terms_count` | int | terms | mpt-log / manual | length of the list logged after `## generating video terms` (`completed: \n[...]`, M9; parsed with `ast.literal_eval`); if terms were supplied: `--terms-count` | `n/a:marker-missing` |
| 39 | `story_cached_input_tokens` | int | tokens | tap | Σ `usage.prompt_tokens_details.cached_tokens` (200 chat lines); absent field → 0 + issue `cached-tokens-field-absent` (Q3 closed 2026-09-26: the relay answers with `prompt_tokens` only, no details object) | as col 9 |
| 40 | `story_call_count` | int | calls | tap | count of chat lines, all statuses (shows MPT retries, M5) | never |
| 41 | `image_quality_requested` | string | — | tap | `omitted` if every image line has `request.quality = null` (MPT default, M4); else the injected value (`TAP_IMAGE_QUALITY`, CR-3) | `n/a:no-images` |
| 42 | `image_quality_returned` | string | — | tap | distribution of `response.quality` over 200 image lines, `value:count` joined `\|`, by count desc (e.g. `low:3\|medium:2`); MPT omits `quality` → `auto` (O6, CR-3) and the resolved value varies per call inside one run (Q4 closed 2026-09-26) | `n/a:not-echoed`, `n/a:no-images` |
| 43 | `image_input_text_tokens_total` | int | tokens | tap | Σ `usage.input_tokens_details.text_tokens` (200 image lines) | as col 16; `n/a:usage-split-absent` when any 200 image line lacks the O6 split |
| 44 | `image_input_image_tokens_total` | int | tokens | tap | Σ `usage.input_tokens_details.image_tokens` (200 image lines; expected 0 for generations [INFERENCE]) | as col 43 |
| 45 | `image_tokens_per_image_avg` | decimal(1) | tokens/image | tap | `image_output_tokens_total / image_count` (one aspect per run → per-aspect value for 03.3.1) | `n/a:no-images`, `n/a:component-na` |
| 46 | `image_cost_per_image_usd` | decimal(6) | USD/image | derived | `image_cost_usd / image_count` | as col 45 |
| 47 | `image_failed_count` | int | keywords | mpt-log | count of `openai image generation failed` lines of the task (keyword skipped, M4, F11) | never |
| 48 | `image_http_429_count` | int | calls | tap | image lines with `http_status = 429` (incl. retried ones; Tier 1 = 5 images/min, O8) | never |
| 49 | `story_wall_seconds` | decimal(1) | s | mpt-log | `## generating video script` → `## generating audio` (script + terms windows) | `n/a:marker-missing` (Q9 closed 2026-09-26: the script marker is logged even with a supplied `video_script`, only its LLM call is skipped) |
| 50 | `image_wall_seconds` | decimal(1) | s | mpt-log | `## downloading videos from openai_image` → `## combining video` (API calls + per-image zoom clips) | as col 21 |
| 51 | `subtitle_detected_language` | string | — | mpt-log | `<lang>@<prob 2 dec>` from `detected language: '<l>', probability: <p>` (M3) e.g. `vi@0.97` | `n/a:marker-missing` |
| 52 | `ram_peak_total_mb` | decimal(1) | MiB | sampler | max over the **task window** (`coverage.first_marker` → `coverage.end`) of (vieneu + proxy + mpt + webui) tree RSS / 1024 | `n/a:no-samples-in-window` |
| 53 | `swap_peak_mb` | decimal(1) | MiB | sampler | max `swap_used_mb` (from `vm.swapusage`) over the task window; start value in `rss.csv` | as col 52 |
| 54 | `memory_pressure_max` | enum | — | sampler | worst level over the task window: `normal` \| `warn` \| `critical` (`kern.memorystatus_vm_pressure_level` 1/2/4 ⚠️ Q6); min free % in `rss.csv` | `n/a:pressure-unavailable` |
| 55 | `usd_per_output_minute` | decimal(6) | USD/min | derived | `(story_cost_usd + image_cost_usd) / (output_duration_sec / 60)`; `story_cost_usd` = `n/a:story-source-plan02` counts as 0 here; every `story_source = plan02` row carries issue `story-cost-terms-only` (the value excludes the script call; 12.1.1 MUST NOT use it as a full-story cost) | `n/a:component-na` if `image_cost_usd` or `output_duration_sec` is n/a, or story cost is n/a for another reason |
| 56 | `local_compute_seconds_per_output_minute` | decimal(1) | s/min | derived | `(tts_wall_seconds + whisper_wall_seconds + render_wall_seconds) / (output_duration_sec / 60)`; Mac baseline for 12.1.1 `gpu_s_per_video_min` (R4: does not transfer to GPU) | as col 55 |
| 57 | `dashboard_cost_usd_check` | decimal(6) | USD | manual | OpenAI dashboard cost of projects `nts-trial-story` + `nts-trial-image` for the run's UTC day (O7 1-day buckets); written next day via `finish_run.py --dashboard-cost`; compared with Σ tap cost of all lines that day (all runs + `no-run`); delta > 5 % → issue | `n/a:pending-dashboard` until filled (MUST be filled before 01-closed; while the relay has no dashboard, the owner-accepted tap cost stands in, 01 DEC-12) |
| 58 | `tts_chars` | int | chars | proxy-log → script | Σ `chars` of proxy lines with `status = 200` (CR-1); fallback: character count (NFC) of the script | never |

Deviations from the README-proposed appended list (justification):

| Change | Why |
|---|---|
| `image_input_tokens_total` → split into cols 43 + 44 | Text input ($5) and image input ($8) are priced differently (O8); one sum cannot be costed |
| + `story_cached_input_tokens` (39) | Needed by the `gpt-6-luna` cost formula (cached $0.01) and 12 §5.7 text formula |
| + `story_call_count` (40) | MPT retries 5× (M5); explains token spikes |
| + `image_cost_per_image_usd` (46) | 03.3.1 asks for cost per image per aspect/quality |
| + `ram_peak_total_mb` (52) | 16 GB co-residency question (R1) needs the sum, not per-service peaks |
| + `local_compute_seconds_per_output_minute` (56) | 12.1.1 needs seconds per finished minute; wall time of the whole run includes image-API waits, which 12 §5.7 excludes |
| + `tts_chars` (58) | Chars per second of audio for 04/12 sizing; CR-1 already provides `chars` |

## Cost formulas

All rates come from `trial/prices.json` (per 1M tokens, USD, `model_match` = exact
key or key + `-YYYY-MM-DD` snapshot suffix). `n/a` propagation follows each
column's n/a rules.

- `story_cost_usd` (col 11) = `((in − cached) × r_in + cached × r_cached + out × r_out) / 1e6`
  with `in` = col 9, `cached` = col 39, `out` = col 10, rates of `gpt-6-luna`
  (0.10 / 0.01 / 0.50). `n/a:no-price` when the model has no price entry.
- `image_cost_usd` (col 17) = `(Σ text_in × 5 + Σ image_in × 8 + Σ out × 30) / 1e6`
  with `text_in` = col 43, `image_in` = col 44, `out` = col 16, rates of
  `gpt-image-2.5-flare` (cached-input rates unused: image `usage` reports no
  cached field, O6).
  (01 DEC-11) With `OPENAI_IMAGE_API=responses` the image model is `gpt-image-2` and D6 image
  lines carry `via = "responses"`: their `usage` holds only the host model's tokens
  (`OPENAI_IMAGE_HOST_MODEL`, luna rates), and the relay reports no image tokens. So
  a model priced `per_image` in `prices.json` (today `gpt-image-2`: 184 VND/image, relay account, owner
  2026-09-28, 02 CR-27) gives `image_cost_usd = Σ n × per_image`, converted with `fx_vnd_per_usd` when the
  row's currency is VND (`n/a:no-price` without that rate or without a row), and the host tokens are reported apart, never priced at image rates. D6 lines also carry
  `request.reasoning_effort` (the effort sent, or null).
- `image_cost_per_image_usd` (col 46) = `image_cost_usd / image_count`.
- `usd_per_output_minute` (col 55) = `(story_cost_usd + image_cost_usd) / (output_duration_sec / 60)`;
  `story_cost_usd = n/a:story-source-plan02` counts as 0 (issue
  `story-cost-terms-only` always present on those rows; not a full-story cost).
- `local_compute_seconds_per_output_minute` (col 56) =
  `(tts_wall_seconds + whisper_wall_seconds + render_wall_seconds) / (output_duration_sec / 60)`.

## Stage windows

| Stage key | Start marker (M9) | End = start of next present stage |
|---|---|---|
| `script` | `## generating video script` | `terms` |
| `terms` | `## generating video terms` | `audio` |
| `audio` | `## generating audio` | `subtitle` — no provider-independent end-of-audio line (Q1 closed 2026-09-26: the window also covers the post-TTS duration probe, < 1 s; the log has second resolution) |
| `subtitle` | `## generating subtitle` (first occurrence) | `materials` |
| `materials` | `## downloading videos from <source>` | `combine` |
| `combine` | `## combining video` | `final` |
| `final` | `## generating video` (not `## generating video script/terms`) | `task <id> finished` / `task failed` |

## Word-count rule

1. Text = the narrated script (`video_script`), Unicode NFC.
2. Replace every character of Unicode category `P*` (punctuation) or `S*` (symbol) with a space, **except** `'` and `’` between two letters (keeps `It's` as one word).
3. Split on whitespace (`str.split()`); count non-empty tokens. Digit groups count (`1990` = 1).
4. vi: every space-separated syllable counts as one word (Vietnamese orthography separates syllables) — `words_per_minute_measured` for vi is **syllables per minute**. en: words per minute. The two values are not comparable; 02 uses the value of its own language.
5. Examples (unit tests in 01.3.1-T3): `Đêm ấy, trời mưa… rất to!` → 6 · `It's late—very late.` → 4 · `— 1990 —` → 1.

## Auxiliary run files

Not the sheet; per-run artefacts in `trial/runs/<run_id>/`.

- `rss.csv` — writers: `rss_sampler.sh` (its own `all` row on TERM) and
  `finish_run.py`, which rewrites the whole file at finish. Columns:
  `scope,start_utc,end_utc,samples,vieneu_peak_mb,proxy_peak_mb,mpt_peak_mb,webui_peak_mb,total_peak_mb,swap_start_mb,swap_peak_mb,pressure_max,free_pct_min,mpt_nproc_max,sample_ms_p95`.
  `scope` is `all` or `stage:<key>`; the `all` row is bounded by the **task
  window** (`stages.json` `coverage.first_marker` → `coverage.end`), not by
  the sampler's lifetime, and carries the same aggregates as cols 52–54;
  each `stage:<key>` row carries the same per-window aggregates as cols
  24–26. A window with 0 samples has `samples = 0` and a **bare `NA`** in
  every metric (D-10): the sheet's `n/a:<reason>` form is NOT used in this
  file, and those `NA` cells are strings, not numbers. `finish_run.py`
  writes it via tmp + `os.replace`, so a retried finish replaces the rows
  instead of appending duplicates.
- `run.json` → `collector` — written by `finish_run.py` at finish:
  `{"finish_run_sha256": <hex|null>, "run_manifest_sha256": <hex|null>}`,
  the sha256 of the two collector sources as they were on disk at finish
  time (`null` if unreadable), so a recorded row is attributable to a tool
  revision without git metadata (L-4).

## Measurement limits (sampler, 01.3.2-T1)

- RSS counts shared pages (shared libraries, mmap'd model files) in every process that maps them → tree sums and `total_peak_mb` overstate private use; they are upper bounds [INFERENCE].
- whisper and render run in the same MPT process (M3) → attribution only by stage window from the MPT log; model load time falls into the subtitle window.
- 1 s sampling misses sub-second spikes (ffmpeg bursts) → peaks are lower bounds of the true max for short spikes.
- Alternative for private memory: `footprint <pid>` / `vmmap --summary` ⚠️ (availability without sudo on macOS 27 unconfirmed) — optional one-off comparison in T4, not in the loop.
- Sampler overhead negligible [INFERENCE] — **measured** in the self-test (`sample_ms` p95, CPU %).

## Change log

- v2 — 2026-09-26 — 01.3.1-T3 gate fixes — cols 52–54 (`ram_peak_total_mb`,
  `swap_peak_mb`, `memory_pressure_max`) and the `rss.csv` `all` row are now
  bounded by the **task window** (`stages.coverage.first_marker` →
  `coverage.end`) instead of the sampler's whole lifetime (perf review P3):
  the sampler outlives the task, so a heavier post-run tail (a second task in
  the shared MPT log, a peer bench) must not be attributed to this run.
  `finish_run.py` now rewrites `rss.csv` atomically (`all` + `stage:*` rows,
  tmp + `os.replace`), which makes a retried finish replace instead of append,
  and records `run.json.collector`. The already-recorded calibration row is
  **unchanged**: its inside-window peaks coincide with the lifetime values.
  Also fixed/documented here: the bare `NA` convention of the auxiliary
  `rss.csv` (Auxiliary run files), `usage-split-absent` now requiring **both**
  O6 keys (a half-filled `input_tokens_details` is split-less, never a silent
  0 in cols 43/44 or a cost that drops the input component in col 17),
  `--task-id` validated as an MPT UUID before any path join, and `--issue`
  text already starting with `note:` stored without a second prefix.

- v2 — 2026-09-26 — 01.3.1-T4 — calibration run `20260926-0818-en-landscape-01`
  closed Q1/Q3/Q4/Q9 (notes in the column dictionary and stage table): the
  audio window keeps ending at `## generating subtitle` (no
  provider-independent end-of-audio line), the relay chat usage carries no
  `prompt_tokens_details`, image `response.quality` **is** echoed while MPT
  omits `quality` → `auto` (O6, CR-3) and the resolved value varies per call
  (mixed distribution in one run), and the `## generating video
  script` marker is logged even with a supplied `video_script`. New n/a reason
  `usage-split-absent` for cols 43/44 and, for a priced model, col 17, because
  the relay's image `usage` has no O6 `text_tokens`/`image_tokens` split (a 0
  would silently drop the input component of the cost); the reason fires when
  **any** 200 image line lacks the split, since summing only the others would
  under-report. Two known limits of that rule, for 03/05.5.2: cols 16/45 keep
  summing the relay host model's `output_tokens` (deliberate T3 behaviour,
  pinned by T-P5), so they are host tokens, not image-output tokens; and
  `tap_day_cost` still prices each line through `_line_cost`, which drops the
  input component of a priced split-less line (the run-attribution path, not the
  sheet).
- v2 — 2026-09-25 — 01.3.1-T1 — schema v2: columns 1–31 unchanged and in order
  from 01 §5.3, columns 32–58 appended (dictionary above; deviations from the
  README-proposed list justified in the table). `schema_version = 2`.
- v1 — 01 §5.3 — 31 columns, superseded by v2.
