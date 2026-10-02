# 05.5 — Quality checks and M0 trial (render check tool, quality report, trial renders)

> Refines: `../../../05-video-assembly-subtitles.md` §4 epic **05.5** (stories **05.5.1** automated render checks, **05.5.2** M0 trial run), FR-05-4, NFR-05-1, NFR-05-2, §9 exit criteria, the plan 03 hand-over (05.5.2-T1 ↔ 03.3.1-T1), 04 CR-13 (en rule O2)
> Contract: `../README.md` — **C13**–**C17**, **X3**–**X5**, **L3**, **P2**–**P4**, **D5**, **D9**, **D10**, **D11**, **D13**, **D14**, **F02**, **F03**, **F05**, **F17**, **F20**, **F24**, **O3**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.** 05.5.1 *Automated render checks* — "As the platform I want every finished render measured by one tool, and the pass/fail policy applied in C#, so that a video nobody can watch never reaches publication." Must · M → **detailed 1.5 d** (05.5.1-T1 M 1.0 + 05.5.1-T2 S 0.5). 05.5.2 *M0 trial run* — "As the owner I want two real trial videos (vi, and en when it launches) that I and a native speaker watched and rated, with the numbers the M0 gates need." Must · M → **detailed 1.0 d**.

**In scope.** `worker-node/checks/render_check.py` (D9): `ffprobe`, narration duration, two-pass `loudnorm`, `ebur128` re-measure, `silencedetect`, SRT parsing, the pinned port of MPT's chunking functions, `render-check.v1`, `--self-test` (05.5.1-T1). The policy in C#: `RenderQualityPolicy`, `RenderExpectations`, `RenderQualityEvaluator`, `RenderQualityReportV1` = `render-quality.v1` (D10), written into `render_jobs.quality_report` **by the 09.6.3-T2 ingest** (05.5.1-T2; 09 W2 CR-12). The M0 trial: L1 (vi) and L2 (en, gated by 04 O2) 5-minute landscape ghost stories on the Mac harness, native ratings, NFR-05-1 numbers, artefacts for Gate 1 and 03.3.1-T1 (05.5.2-T1).

**Out of scope.** The adapter's call into the tool (`mpt-adapter` = 11.3.5-T1) and the 09.6.3 ingest job with its download/verify steps (09.6.3-T2, CR-3; 09 W2 CR-12). `quality_report.images` is **03.4.2-T1's** object (P3). Fidelity/sync scores and the subtitle look (05.2.1-T1, 05.2.3-T1). A Worker-side sandbox: checks do **not** run next to the .NET Worker (F20, O3a).

## 2. Confirmed facts

All rows are README §1 (C/X/L/P ids), §2 (F ids) or §3 (D ids) ✅.

| # | Fact |
|---|---|
| C13 | Task dir: `audio.mp3`, `subtitle.srt`, `combined-1.mp4`, `final-1.mp4`, `script.json`; MPT never deletes it |
| C14 | `16:9` → 1920×1080, `9:16` → 1080×1920, fps 30, AAC 192 kb/s at the narration rate; required length = `ceil(mp3) + 0.1 s` |
| C15 | Whisper supplies **timing only**; `correct()` replaces every cue's text by the matching script chunk and appends leftovers with the next cue's times or `00:00:00,000 --> 00:00:00,000`; chunks = `split_string_by_punctuations(normalize_script_for_subtitle_matching(script))` over `const.PUNCTUATIONS`; a model-load failure writes no SRT yet the task succeeds |
| C16 | Subtitles are burned in (no subtitle stream, invisible to `ffprobe`); wrap at 90 % width; **no line cap** |
| C17 | narration × `voice_volume`, music × `bgm_volume`, 3 s fade-out; **no loudness normalisation anywhere** |
| X3 | Two-pass `loudnorm` (`I=-15:TP=-1.5:LRA=11`, pass 2 `linear=true`, `-c:v copy`) −45.85 → −14.9 LUFS; H.264 bitstream sha1 unchanged |
| X4 | Digital silence: `input_i = "-inf"`; `silencedetect=noise=-50dB:d=2` covers the whole duration |
| X5 | Truncated MP4 (`moov` at the end) → `ffprobe` `moov atom not found`, exit 1 |
| L3 | The ≈ −14 LUFS YouTube reference is secondary-source only ⚠️; the −16…−14 band stays a [target] |
| P2 | 02.8.2's video gate reads `pass`, `duration_sec`, `tts_duration_sec`, `integrated_lufs`, `width`, `height`, `subtitles_present`, `sha256`, `images.*`, `params.subtitles.enabled`; fails closed on a missing field |
| P3 | `quality_report.images` belongs to 03.4.2-T1 (03 D7) |
| F02 | A whisper failure is silent: zero-length cues or an empty SRT with a successful task (C15) |
| F03 | "Subtitles present if enabled" cannot be measured with `ffprobe` (C16) → measure the SRT |
| F05 | MPT never normalises loudness (C17) → normalise on the node, then measure |
| F17 | The MP4 is `ceil(audio) + 0.1 s` plus ≈ 30 ms MP3 padding → compare with the **measured** `audio.mp3` |
| F20 | The check and the loudness step run on the node in the adapter's post-processing; the Worker verifies sha256/size/type and applies the policy |
| D9/D10 | CLI, steps 1–7 and the `render-check.v1` key set (§3.1); `render-quality.v1` names, check ids/order, thresholds and hints (§3.2); the trial must feed 03.3.1-T1 (two landscape runs) and 05.2.1-T1 (6 videos, F24) |
| D14 | Fixtures `worker-node/contract/fixtures/render-check.v1.*.json`, `render-quality.v1.*.json`; SRT fixtures `worker-node/checks/fixtures/*.srt`; `--self-test` builds media with `ffmpeg -f lavfi`; stdlib tools run with `uv run --no-project --python 3.12` |

**Closed here.** How "subtitles present" is measured (SRT, not the container — F02/F03); where loudness is normalised (node, D9/F05/O3a); where the policy lives (C# Domain, D10/F20); what the trial must produce for 03.3.1-T1 (F24).

## 3. Domain and data

### 3.1 `render-check.v1` — the measuring artefact (node and Mac)

Produced by `worker-node/checks/render_check.py`, embedded by the adapter as `check` in `render-status.v1` (D4), consumed by 05.5.1-T2 and by `mpt_client.py` at M0. Exact key set (D9):

`schema`, `tool_version`, `ffmpeg_version`, `input {file, bytes, sha256}`, `output {file, bytes, sha256, normalized} | null`, `container {format, duration_s, faststart}`, `video {codec, pix_fmt, width, height, fps}`, `audio {codec, sample_rate, channels}`, `narration {duration_s}`, `loudness {before {integrated_lufs, true_peak_dbtp, lra}, after {…}, normalization_type}`, `silence {longest_s, total_s}`, `subtitles {present, cues, zero_duration, overlapping, non_monotonic, out_of_range, coverage_share, max_cue_chars, long_cues, cps_p95, sync_suspect, text_matches_script, text_mismatch_cues}`, `warnings`, `errors`. Worked example: README §3 (D9); fixture `render-check.v1.ok.json`.

- `output` is `null` without `--normalize` (M0 comparison runs); with it the adapter serves `final-1.norm.mp4` as the video output and `output.video.sha256` is its hash.
- The tool **measures only**; every threshold lives in C# (§3.2). Its own parameters are the SRT heuristic constants `MAX_CPS = 25` and `MAX_CUE_S = 12` (changed only with a `tool_version` bump after 05.2.1-T1 calibrates them) and the preset's `maxCharsPerLine × maxLines` read from `--presets FILE` (default `<repo>/seed/subtitle-presets.v1.json`) for `--aspect`, which defines a "long" cue (T3).

### 3.2 `render-quality.v1` — the policy result (`render_jobs.quality_report`)

Written by the **09.6.3-T2** ingest (09 W2 CR-12) from `RenderQualityEvaluator.Evaluate` (05.5.1-T2) + `images` from 03.4.2-T1, in the same transaction. Top-level names are 02.8.2's read list (P2, D10):

`schema` `render-quality.v1`, `pass`, `policy_version`, `check_tool_version`, `duration_sec`, `tts_duration_sec`, `integrated_lufs`, `true_peak_dbtp`, `width`, `height`, `subtitles_present`, `sha256`, `normalized`, `subtitles {cues, coverage_share, sync_suspect_share, long_cues, text_matches_script}`, `checks [ {id, value, threshold, pass} ]`, `hints []`, `images` (03.4.2-T1). Example: README §3 (D10).

- Blocking checks, in this order (D10): `container_valid` (no `errors`), `streams` (h264 video + aac audio), `resolution` (1920×1080 landscape / 1080×1920 portrait), `duration` (`|duration_sec − tts_duration_sec| ÷ tts_duration_sec ≤ 0.05`), `loudness` (after ∈ [−16, −14] LUFS), `true_peak` (≤ −1.0 dBTP), `silence` (longest ≤ 10 s), `subtitles_present` (when enabled: `cues ≥ 1`, `zero_duration = 0`, `coverage_share ≥ 0.50`), `subtitle_text` (`text_matches_script = true`). Advisory hints, never blocking: `subtitle_sync_suspect` (`sync_suspect_share > 0.10`), `subtitle_cues_long` (`long_cues ≥ 1`), `loudnorm_dynamic`, `music_none`. The two derived ratios are stored rounded — `duration` drift to 4 decimals, `sync_suspect_share` to 3 — because the D14 fixtures pin those exact values (05.5.1-T2 implementation note).
- `pass` = every **blocking** check passes; a skipped check (subtitles off) is `pass = true` with `value = null`. `sha256` = the normalised output's hash (`input.sha256` when `--normalize` was off, [INFERENCE]).
- `RenderQualityPolicy` (config `RenderQuality:*`) holds every threshold and validates its range at startup. Key suffixes are [INFERENCE] (README names the prefix only): `MaxDurationDrift` 0.05, `MinLufs` −16, `MaxLufs` −14, `MaxTruePeakDbtp` −1.0, `MaxLongestSilenceSeconds` 10, `MinSubtitleCoverage` 0.50, `MinCues` 1, `SyncSuspectHintShare` 0.10, `PolicyVersion` 1. Thresholds marked [target] (D10, L3) are finalised after the 05.2.1-T1 / 05.5.2-T1 measurements (TL ruling). Placement note (05.5.1-T2): the policy record, the expectations, the check records and the evaluator live in `NightTales.Application` (`Rendering/Quality/`) rather than in the Domain assembly — the Domain reference set is an allow-list that permits `ErrorOr` only (09.1.2-T1), while the evaluator takes `RenderCheckV1` from `NightTales.RenderNode.Contract` and returns an Application DTO. The `IValidateOptions<RenderQualityPolicy>` validator and the `RenderQuality` binding live in the Infrastructure composition for the same reason.
- `pass = false` → the caller (09.6.3-T1) ends the job with outcome `render_quality_failed` (D5), **no** auto-retry: a deterministic output fails again.

### 3.3 Ownership, storage, dependencies

| Part of `quality_report` | Writer | Note |
|---|---|---|
| every field except `images` | `RenderQualityEvaluator` (05.5.1-T2), called by 09.6.3-T2's ingest (09 W2 CR-12) | one object, no partial writes |
| `images` | 03.4.2-T1 (`images.v1`, 03 D7) | 05.5.1 never writes it (P3); the ingest merges both in one transaction |

No new table or migration and no EF in this code: `quality_report` is existing `jsonb` (00 §6). `RenderCheckV1` lives in `NightTales.RenderNode.Contract` (11 README D4 owns the project, 05.5.1-T2 owns the type), and every render document is read and written with that project's `RenderNodeJson.Strict`; `RenderQualityReportV1` lives in `NightTales.Application/Abstractions/Rendering/` and carries the §3.2 snake_case names as `[JsonPropertyName]` members (05.5.1-T2).

## 4. Use cases

| # | Actor / trigger | Behaviour |
|---|---|---|
| UC1 | Adapter, MPT task succeeds (`stage = checking`) | run `render_check.py run --task-dir <dir> --aspect <a> --subtitles on --normalize`; embed the report as `check`; serve `final-1.norm.mp4`; a tool exit 1 leaves `errors` non-empty and `container_valid` fails |
| UC2 | Worker ingest of a terminal succeeded task (09.6.3-T2, 09 W2 CR-12) | verify the bytes against `output.video.sha256` + size/type, call `RenderQualityEvaluator.Evaluate(check, RenderExpectations(aspect, subtitlesEnabled, musicSelected), policy)`, write `quality_report` with `images`; `pass = false` → job `failed`, `render_quality_failed`, no retry, episode back to `story_approved` |
| UC3 | Owner / BA, M0 | render L1 (vi) and L2 (en when 04 O2 launches en, else a second vi story, T14), run the check with `--normalize`, apply the policy by hand (`trial/tools/apply_quality_policy.py`), rate voice + subtitles, record the numbers (05.5.2-T1) |
| UC4 | Autopilot video gate (02.8.2-T1) | reads the P2 fields, fails closed on a missing field; its check 10 `subtitle_sync` uses our hint (CR-6) |
| UC5 | 03.3.1-T1, 05.2.1-T1 | consume the trial runs: L1/L2 run ids, tap rows (image tokens, skipped images) and the 6-video fidelity/sync set (F24) |

## 5. API and events

- **No new HTTP route, no new domain event.** Everything happens inside 09.6.3-T2's ingest (09 W2 CR-12) and the adapter's post-processing (11.3.5-T1).
- Adapter → Worker: the `check` object of a terminal `render-status.v1` is exactly `render-check.v1` (D4). The Worker never re-runs media checks (F20): it verifies `output.video.sha256` against the downloaded bytes and the size/type caps.
- Job outcomes (D5, never HTTP): `render_quality_failed` (this folder), `render_output_invalid` (sha/size mismatch), `render_timeout` (60 min). mock-mpt's `MOCK_FAIL_MODE=quality:<check>` (11.1.2-T1, CR-4) emits a `check` block that fails `<check>`, so the mapping is testable end to end. `render_jobs.params` stays `render-params.v1` (D11); only `subtitles.enabled` gates the checks.

## 6. UI

None here — this folder is worker-side tooling plus a backend evaluator; 10.6.2 renders the hints and the error copy via **CR-9**. Copy owned here:

| Key | en | vi |
|---|---|---|
| `errors.render_quality_failed` | No video was made: the finished render did not pass the quality checks. | Chưa làm được video: bản dựng đã xong nhưng không đạt kiểm tra chất lượng. |
| `render.quality.subtitleSyncSuspect` | Some subtitles may not line up with the voice. | Một số phụ đề có thể chưa khớp với giọng đọc. |
| `render.quality.subtitleCuesLong` | Some subtitles are longer than two lines. | Một số phụ đề dài hơn hai dòng. |

## 7. Jobs and integrations

| Consumer | Uses | Owner |
|---|---|---|
| 11.3.5-T1 (adapter post-processing) | `render_check.py run … --normalize` (soft: it calls the tool, not its internals) | tool here, call there |
| 09.6.3-T1 (poll) / 09.6.3-T2 (ingest) (09 W2 CR-12) | `RenderQualityEvaluator.Evaluate` + `RenderQualityReportV1` (CR-3) | 05.5.1-T2 |
| 02.8.2-T1 (video gate) | the P2 fields, read defensively (soft; CR-6 adds check 10) | 05.5.1-T2 |
| 03.4.2-T1 | supplies `images`; the merge is the 09.6.3-T1 transaction | not here |
| 03.3.1-T1 / 05.2.1-T1 | L1/L2 run ids, tap rows, skipped images; the tool for 6 renders + SRT fields for calibration | 05.5.2-T1 / 05.5.1-T1 |
| 10.6.2-T1 | hint copy + `errors.render_quality_failed` | copy §6 |

**M0 flow (no adapter, D13).** `trial/tools/start_run.sh <lang> landscape "<topic>" 5 --status trial --voice <id> --story-source plan02` (01.3.1-T3 CLI) → `mpt_client.py` builds the D3 body, submits, polls every 5 s, downloads `final-1.mp4`, `subtitle.srt`, `audio.mp3`, `script.json` into `$NTS_HOME/runs/<run_id>/` (T2) → `render_check.py run --task-dir "$NTS_HOME/runs/<run_id>" --aspect landscape --subtitles on --normalize --out "$NTS_HOME/runs/<run_id>/render-check.json"` → `apply_quality_policy.py` → `finish_run.py` (sheet row; the usage tap must be running, 01 D6/D10).

**M1 flow.** The adapter runs the same tool in its post-processing; the Worker evaluates the policy in C#. Field names are identical on both paths (D4/D10) — that is what the D14 fixtures pin.

## 8. Security and privacy

- Content leaking into reports, DB or logs: `render-check.v1` carries counts, durations, hashes and booleans only (`text_matches_script` is a boolean); `script.json` is read locally and never copied into the report.
- Media leaking into the repo: the trial commits no media — `final-1.mp4`, `final-1.norm.mp4`, `subtitle.srt` stay under `$NTS_HOME/runs/<run_id>/` (and on the node); only SRT text fixtures are tracked.
- A tampered node video passing the policy: the Worker verifies the bytes against `output.video.sha256` and applies the policy in C# (F20) → `render_output_invalid` on mismatch.
- Secrets: `render_check.py` needs none; the M0 harness reads the key from the macOS Keychain via `secret nts.mpt.api_key` (never argv, never printed); `UV_PROJECT_ENVIRONMENT` under `$NTS_HOME` for every `uv run --project` (D14).

Retention: task dirs are removed by the adapter `DELETE` (11.3.4-T1, CR-4); `$NTS_HOME/runs/<run_id>/` follows 01's retention; `quality_report` follows 13 retention.

## 9. Test plan

- **`render_check.py --self-test`** (05.5.1-T1): media from `ffmpeg -f lavfi` in a temp dir — good landscape, portrait, silent, wrong resolution 1280×720, truncated, no audio stream, loud −8 LUFS (must normalise into the band); SRT fixtures `worker-node/checks/fixtures/*.srt` — good, zero-cues (all `00:00:00`), overlapping, out-of-range, empty. Each case asserts named report fields and the expected exit code; the run exits 0 only if all assertions hold.
- **Chunking port drift:** `worker-node/checks/fixtures/chunking/*.json` are generated once by `gen_chunk_fixtures.py` with **MPT's own interpreter**; `--check` re-generates and fails on any difference (drift after an MPT upgrade).
- **C# unit tests (05.5.1-T2), one per boundary** (named in the task file): duration 0.05 vs 0.0501; loudness −16.0 / −14.0 inclusive; true peak −1.0 vs −0.9; silence exactly 10 s; coverage 0.50; `zero_duration = 1` → fail; `text_matches_script = false` → fail; subtitles off → skipped check `pass = true, value = null`; non-empty `errors` → `container_valid` fails; a null `check` → fail closed.
- **Fixture round-trip:** `RenderCheckV1` / `RenderQualityReportV1` deserialise every D14 `render-check.v1.*` / `render-quality.v1.*` fixture with `JsonUnmappedMemberHandling.Disallow` and re-serialise to an equal document.
- **In-pipeline (09.6.3-T1, mock-mpt):** `MOCK_FAIL_MODE=quality:loudness` → `render_quality_failed`, `pass = false`, no retry; `quality:ok` → `pass = true` with `images`.
- **M0:** `apply_quality_policy.py --self-test` must reproduce the `render-quality.v1.{pass,fail-loudness}` verdicts from the `render-check.v1.*` fixtures; the two trial runs are the acceptance evidence (check report, policy verdict, ratings, sheet rows).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 05.5.1-T1 | `render_check.py`: probe, loudness normalisation, SRT checks | Worker | M (1.0) | 05.4.1-T1 (real task-dir samples, SRT variants S7/S8) | 05.5.2-T1, 05.2.1-T1, 05.5.1-T2, 11.3.5-T3 (the adapter post-processing calls it; 11 CR-4) |
| 05.5.1-T2 | `RenderQualityPolicy` / `Evaluator`, `render-quality.v1` | BE | S (0.5) | 05.5.1-T1 (`render-check.v1` shape), 05.4.1-T2 (fixtures `render-check.v1.*`, `render-quality.v1.*`), 09.1.1-T1 (solution skeleton); 05.2.1-T1 soft (calibrated thresholds; defaults otherwise) | 09.6.3-T2 (ingest, 09 W2 CR-9), 02.8.2-T1 (soft: reads the report defensively) |
| 05.5.2-T1 | M0 trial renders vi + en, ratings, measurements | BA | M (1.0) | 01.2.2-T6, 04.1.2-T3, 04.2.2-T1, 05.2.2-T1, 05.2.3-T1, 05.5.1-T1, 05.4.1-T1, 03.3.2-T1, 02.1.3-T0; 05.3.2-T1 soft | 03.3.1-T1, 05.2.1-T1, 01.1.1-T3, 05-M0 gate |

Total **2.5 d** (high-level 2.0 d; README §9).

### Open items for the TL

- [RULED — README §10 T35] **D10 thresholds marked [target]** (`duration` ≤ 0.05, loudness [−16, −14], true peak ≤ −1.0 dBTP, silence ≤ 10 s, coverage ≥ 0.50, `sync_suspect_share` > 0.10) are only proven by real renders. 05.2.1-T1 / 05.5.2-T1 measure them; a measurement outside the band is a TL ruling, not a silent threshold change (D10, L3).
- [RULED — README §10 T13] **`RenderQuality:*` key suffixes** are [INFERENCE] — README names the prefix only. Renaming before M1 is free (05.5.1-T2 only).
- [RULED — README §10 T30] **01 D8 sheet schema v2 (58 columns) has no column** for the music track id / `bgmVolume` / `MusicBedLufs`, nor for the `render_check.py` wall time; 05.5.2-T1 records them in `trial/runs/<run_id>/` and in the M0 summary. If the TL wants them in the sheet, 01 appends columns in v3 (P1: append, never rename).
- [RULED — README §10 T3] `sync_suspect` = cps > `MAX_CPS` (25) or cue duration > `MAX_CUE_S` (12 s), constants in the tool; 05.2.1-T1 calibrates them and a change bumps `tool_version` before M1.
- [OWNER — README §10 O2/O3/O5] **O3/O2/O5 are the owner's**; this plan assumes the TL recommendations: normalise on the node (O3a), en launched only when 04 O2's naturalness threshold holds (otherwise L2 is a second vi story, T14; 04 CR-13), long cues advisory only (O5a).
- [RULED — README §10 T26] **D14 has no `render-check.v1` fixture for `--normalize` off** and no `render-quality.v1` fixture for a skipped-subtitles pass; 05.4.1-T2/05.5.1-T2 add them or the self-test covers it ([INFERENCE] that this suffices).
- [RULED — README §10 T11] `submit-body.json` lives in `$NTS_HOME/runs/<run_id>/` (written by `mpt_client.py submit --run-dir`), never under the tracked `trial/runs/`.
- [RULED — README §10 T34] **CR-6 naming:** 02.8.2's new check is `subtitle_sync`, our hint is `subtitle_sync_suspect`; confirm the pair when CR-6 is applied.

## 11. Rollout

- **Order.** M0: 05.5.1-T1 (after 05.4.1-T1, needs real task dirs incl. the SRT variants S7/S8) → 05.5.2-T1 → 05.2.1-T1 (calibration). M1: 05.5.1-T2 after 05.4.1-T2 (fixtures) and 09.1.1-T1, before 09.6.3-T1.
- **Flags:** none. The tool always runs with `--normalize` in the pipeline (O3a). A tool failure never blocks a render by itself — it yields `check.errors`, and the C# policy decides.
- **Config:** `RenderQuality:*` (§3.2, backend); node side has CLI defaults only; `Render:MaxMinutes` 60 lives in 09.
- **Monitoring:** `render_quality_failed_total{check}`, `render_check_duration_seconds`, `render_check_errors_total{error}`, `render_quality_hints_total{hint}`; alert when `render_quality_failed` > 5 % over 24 h.
- **Rollback:** widen `RenderQuality:MinLufs/MaxLufs` (config only) rather than disabling the evaluator — a missing `quality_report` makes 02.8.2's gate fail closed for every render (P2), the intended safe state.
