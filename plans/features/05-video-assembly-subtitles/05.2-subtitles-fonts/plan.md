# 05.2 — Subtitles and fonts (fidelity and sync evaluation, Vietnamese font, style presets)

> Refines: `../../../05-video-assembly-subtitles.md` §4 epic 05.2 (stories **05.2.1**, **05.2.2**, **05.2.3**), FR-05-3, F5–F8
> Contract: `../README.md` — **C15**, **C16**, **X1**, **X2**, **L1** · **D3**, **D6**, **D7**, **D9** (SRT part), **D10** (subtitle checks), **D12**, **D13** · **F01**, **F02**, **F04**, **F15**, **F16**, **F23** · **O4**, **O5**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.** 05.2.1 *Subtitle fidelity and sync evaluation* — measured proof that burned-in subtitles match the script and land on the spoken words, so the M0 gate rests on numbers · Must · **M (1.0 d)**. 05.2.2 *Vietnamese-capable font* — a registered, licence-checked font whose coverage is verified with MPT's own function · Must · **S (0.5 d)**. 05.2.3 *Subtitle style presets* — style, size and position per aspect, readable on phone and TV without per-video edits · Should · **S (0.5 d)**.

**In scope.** (a) The font register `FONTS.csv`, `OFL-BeVietnamPro.txt`, the upstream artefact comparison, `vi-glyphs.txt`, `font_glyph_check.py`, `check-fonts.sh` (05.2.2-T1, D7). (b) `seed/subtitle-presets.v1.json` (D6), `subtitle_fit.py`, the 30 s preview renders + frames + Shorts overlay mock, owner approval (05.2.3-T1, O4, F23). (c) The M0 evaluation: 6 videos, cue-text fidelity, human sync labels (20 cues per video), the heuristics tabulated against them, whisper-language evidence, readability verdict, threshold proposal for D10 (05.2.1-T1, F01, F02, F16, O5). (d) `trial/tools/subtitle_sample.py` (new, owned here).

**Out of scope.** `render_check.py` / `render-check.v1` (05.5.1-T1); the L1/L2 trial renders (05.5.2-T1, F24); `mpt_client.py` / `tts_stub.py` / `image_stub.py` (05.4.1-T1, 03.4.1-T1); all C# (05.1.2-T1, 05.5.1-T2); the adapter (11.3.5-T1), mock-mpt (11.1.2-T1), web UI (10, hints via CR-9), music (05.3). No whisper replacement, no MPT fork, no subtitle stream: subtitles stay burned in (C16 ✅).

**Order.** `05.2.2-T1` → `05.2.3-T1` → `05.5.2-T1` → `05.2.1-T1` (README §5, §8.1). The first two are independent of the trial; 05.2.1-T1 is last and feeds 05.5.1-T2 (M1) and the 05-M0 gate.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| C15 · F01 · F02 · F16 | Whisper gives **timing only**: sentence mode replaces each cue's text with the matching script chunk (split at every `const.PUNCTUATIONS` char); `create()` sends no `language` and `initial_prompt` is global, so no per-job language hint is possible; a model-load failure writes no file (leftover chunks → `00:00:00,000`). Text = script, so a word-error-rate target measures nothing and the risk is timing; `subtitles_present` is measured, never copied from the request | README C15, F01, F02, F16 ✅ |
| C16 · F04 · X1 · X2 | Burned in (no stream); font = `resource/fonts/<font_name>` (`STHeitiMedium.ttc` when empty); wrap at **90 % frame width**, no line cap; positions `bottom`/`top`/`center`/`two_thirds_bottom`/`custom`; glyphs are checked only in the WebUI, so a job without `font_name` renders broken vi subtitles. MPT's own glyph method: `BeVietnamPro-Bold.ttf` / `-Medium.ttf` → **0 missing**, `STHeitiMedium.ttc` → **92 missing** (snapshot sha256 `2fe6c3f9…30bfe`); `wrap_text` replica → landscape size **84 → ≤ 41** chars/line, portrait size **64 → ≤ 28** | README C16, F04, X1, X2 ✅ |
| L1 · D3 · D6 · D7 · D11 | Be Vietnam Pro = SIL OFL 1.1, and the licence requirement does not apply to a rendered video. Submit subtitle group: `subtitle_enabled true`, `subtitle_display_mode "sentence"`, `subtitle_animation "none"`, `font_name "BeVietnamPro-Bold.ttf"`, the D6 values, no `n_threads` from the backend. `subtitle-presets.v1`: landscape 84 / stroke 3.0 / `bottom` / 42 / 2, portrait 64 / stroke 3.0 / `custom` 62.0 / 28 / 2; camelCase in the file, snake_case in the mapper; a change = `revision + 1`, recorded as `subtitles.preset`. Font = MPT's bundled copy, fallback `BeVietnamPro-Medium.ttf`; `FONTS.csv` register + `check-fonts.sh [--mpt-root DIR]` = sha256 then the glyph check, exit 0/1/2 | README L1, D3, D6, D7, D11 ✅ |
| D9 · D10 | `render_check.py run --task-dir DIR --aspect landscape\|portrait --subtitles on\|off [--normalize] [--out FILE]` · `--self-test`; exit 0 report written, 1 input unreadable, 2 usage/environment; the `subtitles` block carries `cues, zero_duration, coverage_share, max_cue_chars, long_cues, cps_p95, sync_suspect, text_matches_script`. Blocking `subtitles_present` (cues ≥ 1, zero-duration = 0, coverage ≥ 0.50) and `subtitle_text`; hints `subtitle_sync_suspect` (> 0.10) and `subtitle_cues_long` (≥ 1); config `RenderQuality:*`; [target] values come from 05.2.1-T1 / 05.5.2-T1 | README D9, D10 ✅ |
| D12 · D13 · D14 · F15 · F23 · F24 · O4 · O5 | M0 stubs `127.0.0.1:18003` (image) and `127.0.0.1:18004` (TTS, `ok`/`e500`/`abort`/`empty`/`sine`/`slow`; speech fixtures never committed); `mpt_client.py` polls every 5 s, downloads `final-1.mp4` + `subtitle.srt` into `$NTS_HOME/runs/<run_id>/`, runs `render_check.py run --normalize`. Test kit: `render-check.v1.*` fixtures, SRT fixtures as text, `--self-test` via `ffmpeg -f lavfi`, `UV_PROJECT_ENVIRONMENT` under `$NTS_HOME`, stdlib tools via `uv run --no-project --python 3.12`. Each cue is a clause between punctuation marks; > 2-line cues are the advisory hint `subtitle_cues_long`; portrait wraps at 90 % width and Shorts' buttons may cover long lines ⚠️ → overlay mock; L1 (vi) + L2 (en) landscape 5-min come from 05.5.2-T1 and 4 short renders (2 portrait) are added here; the look is approved on the 05.2.3-T1 frames (± 4 px without a new decision) and long cues stay advisory in P | README D12, D13, D14, F15, F23, F24, §10 O4, O5 ✅ |

## 3. Domain and data

No table is created. `subtitle_presets` is a tracked seed + embedded resource (05.1.2-T1); the per-render record is `render-params.v1.subtitles.preset` (D11).

| Path | Owner | Consumers |
|---|---|---|
| `worker-node/fonts/FONTS.csv` (D7 header), `OFL-BeVietnamPro.txt` (verbatim, L1) | 05.2.2-T1 | 05.1.1-T2, 11.3.1-T1, health `assets.font`, licence evidence |
| `worker-node/mpt/tools/font_glyph_check.py`, `worker-node/bin/check-fonts.sh`, `trial/fixtures/vi-glyphs.txt` (178 vi letters NFC + ASCII letters/digits + the FR sentence) | 05.2.2-T1 | 11.3.1-T1 provisioning + adapter health, 05.5.2-T1 |
| `seed/subtitle-presets.v1.json`, `worker-node/mpt/tools/subtitle_fit.py`, `trial/fixtures/subtitle-preview-vi.txt`, `-en.txt`, `shorts-overlay-1080x1920.png` (mock ⚠️ third-party content never committed) | 05.2.3-T1 | `SubtitlePresetCatalog` (05.1.2-T1), `mpt_client.py`, 05.5.2-T1 |
| `trial/fixtures/subtitle-eval-vi-names.txt` (names/diacritics heavy, 60–90 s), `trial/tools/subtitle_sample.py`, `trial/subtitle-eval/results.csv`, `trial/subtitle-eval/README.md`; `$NTS_HOME/runs/font-proof/<font>.png`, `$NTS_HOME/runs/<run_id>/` (runtime only) | 05.2.1-T1 / 05.2.2-T1 / 05.5.2-T1 | videos E3–E5; 05.5.1-T2 thresholds, TL ruling; visual evidence |

### 3.1 Preset file (D6, verbatim)

```json
{ "schema": "subtitle-presets.v1", "revision": 1, "fontName": "BeVietnamPro-Bold.ttf",
  "presets": {
    "landscape": { "fontSize": 84, "strokeWidth": 3.0, "strokeColor": "#000000", "textForeColor": "#FFFFFF",
                   "textBackgroundColor": false, "roundedSubtitleBackground": false,
                   "subtitlePosition": "bottom", "customPosition": 70.0, "maxCharsPerLine": 42, "maxLines": 2 },
    "portrait":  { "fontSize": 64, "strokeWidth": 3.0, "strokeColor": "#000000", "textForeColor": "#FFFFFF",
                   "textBackgroundColor": false, "roundedSubtitleBackground": false,
                   "subtitlePosition": "custom", "customPosition": 62.0, "maxCharsPerLine": 28, "maxLines": 2 } } }
```

`maxCharsPerLine`/`maxLines` are not MPT fields: they drive `subtitle_fit.py` and the advisory `long_cues` count (D6, F15). The mapper converts each camelCase key to its snake_case twin (D6).

### 3.2 Evaluation data

`trial/subtitle-eval/results.csv` — one row per video (6 rows); the columns and the `video_id` set (`L1-vi-landscape-5m`, `L2-en-landscape-5m`, `E1-vi-portrait`, `E2-en-portrait`, `E3-vi-landscape-names`, `E4-en-landscape-longclause`) are defined in 05.2.1-T1 §Outputs; E2/E4 fall back to vi substitutes with `source = substitute` when 04 O2 does not launch en. Automatic columns are copied verbatim from `render-check.v1.subtitles` (D9), human columns come from `subtitle_sample.py` + the reviewer, and the whisper fields come from MPT's log line (C15, 01.3.1 col 51). No cue text and no media in the CSV or the report.

## 4. Use cases

- **UC1 · UC2 — L1/L2 landscape 5-min renders from 05.5.2-T1** (BA): rows `L1-vi-landscape-5m` / `L2-en-landscape-5m`, `text_matches_script = true`, `on_time_share ≥ 0.95`; L2 → a vi substitute (`source = substitute`) when 04 O2 drops en.
- **UC3 · UC4 — portrait shorts** (BA, stubs, 60–90 s, vi and en): rows `E1-vi-portrait`, `E2-en-portrait`; readable frames, no 3-line cue in the sample.
- **UC5 · UC6 — stress texts** (BA): `E3-vi-landscape-names` (0 diacritic defects on `subtitle-eval-vi-names.txt`) and `E4-en-landscape-longclause` (a clause > 84 chars → the 3-line cue is visible and counted in `long_cues`).
- **UC7 · UC8 — operations** (Operator / Worker): `check-fonts.sh` exit 0 with the sha256 matching `FONTS.csv` and health `assets.font`; before a submit, `subtitle_fit.py` prints per-cue chars/lines and the summary max chars/line.
- **UC9 · UC10 — approvals** (Owner / TL): the owner approves the preview frames (O4; any change = a D6 revision bump); the TL reads `trial/subtitle-eval/README.md` for the calibrated thresholds and the `RenderQuality:*` keys.

## 5. API and events

No HTTP route is added, changed or owned here; the surfaces are CLIs plus two JSON artefacts. Exit codes are 0 ok / 1 check failed / 2 usage-environment (01 D9) unless stated.

- `worker-node/bin/check-fonts.sh [--mpt-root DIR]` — `$MPT_ROOT` default `$NTS_HOME/src/mpt`; verifies both `FONTS.csv` sha256 rows, then runs the glyph check.
- `worker-node/mpt/tools/font_glyph_check.py --font FILE --text FILE [--proof-out FILE]` — run with `$MPT_ROOT/.venv/bin/python`; imports `app.services.video.subtitle_font_supports_text`; exit 1 lists the missing glyphs or reports the hash mismatch.
- `worker-node/mpt/tools/subtitle_fit.py --script FILE --preset landscape\|portrait --aspect landscape\|portrait [--presets FILE]` — run with `$MPT_ROOT/.venv/bin/python`; imports `app.services.video.wrap_text` and MPT's `utils` split helpers; exits 0 (report printed) except on usage.
- `trial/tools/subtitle_sample.py --srt FILE --n 20 --seed <run_id> [--csv FILE]` — stdlib; prints `cue_id, start, end, duration, chars, text`; exit 1 on an unreadable SRT.
- `render_check.py run --task-dir DIR --aspect landscape\|portrait --subtitles on\|off [--normalize] [--out FILE]` (05.5.1-T1, consumed; 0 report written / 1 input unreadable / 2 usage) and `trial/tools/mpt_client.py` (05.4.1-T1, run spec per D13).

Consumed artefact interfaces: `render-check.v1` (`subtitles.*`, `warnings`, `errors`, `narration.duration_s`) and `render-quality.v1` (`subtitles.*`, `hints`, `checks[]`, written by 05.5.1-T2 from these numbers). Supplied: the D3 subtitle group and the D6/D7 values. **No events.**

## 6. UI

- **Web UI: none** — `subtitle_sync_suspect` / `subtitle_cues_long` reach plan 10 via CR-9; no key or component is owned here.
- **Owner-facing artefact only:** the 05.2.3-T1 preview frames (landscape + portrait, with and without the Shorts overlay mock) are the O4 approval surface; they stay in `$NTS_HOME/runs/subtitle-preview/` — only the fixture texts and the overlay provenance note are tracked.
- **vi + en:** the subtitle text language always equals the script language (the text is copied from the script, C15/F01) and Be Vietnam Pro (D7) covers both; whisper's detected language is recorded (F16) but never selects the subtitle language. Readability is judged by the native reviewer per language (phone + desktop) in 05.2.1-T1, not in a UI.

## 7. Jobs and integrations

| # | Where | Gives | Consumes |
|---|---|---|---|
| 1 | M0 Mac `mpt_client.py` (D13) and the trial (05.5.2-T1) | run-spec fields (aspect, preset, font, script); preset revision 1 and `check-fonts.sh` green | D3 body, `subtitle.srt`, `final-1.mp4`; L1/L2 renders, trial-sheet rows |
| 2 | M0 evaluation (05.2.1-T1) | `results.csv`, calibrated thresholds | `render_check.py`, `mpt_client.py`, MPT log |
| 3 | M1 node (11.3.1-T1) / adapter (11.3.5-T1) | `check-fonts.sh` in provisioning; the D3 subtitle group + D6 values; the SRT output | health `assets.font` and `reasons ⊇ {font_missing}` (D4); `render_check.py` post-processing (D9) |
| 4 | M1 backend (05.1.2-T1) / policy (05.5.1-T2) | `SubtitlePresetCatalog` embedded resource, `subtitles.preset`; calibrated `RenderQuality:*` thresholds | `render-params.v1` (D11); `render-quality.v1` (D10) |
| 5 | Gate 05-M0 | `check-fonts.sh` exit 0, rev 1 with approved frames, evaluation report | README §7 |

No job, queue or background service is owned here.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Secret leakage in evidence | no API key, MPT key or path outside `$NTS_HOME` in `FONTS.csv`, `results.csv`, the report or preview logs; commands print hashes, counts and `$NTS_HOME` paths only |
| Committed media / third-party UI | no audio, video or rendered frame is committed (D12, D14); an owner-supplied Shorts screenshot may contain third-party content ⚠️ → not committed, the tracked fallback is a rectangle-only mock (bottom 25 %, right 15 %) [INFERENCE] |
| Font licence | OFL 1.1 (L1): the licence text is kept verbatim; the register records `license`, `license_url`, `source_url`, `sha256`, `checked_at`; a rendered video is exempt; font files are never redistributed alone |
| Story text and log content | `results.csv` holds counts, shares, language codes and cue numbers, not cue text; fixtures are BA-written, no personal data. MPT logs the whole submit body (C4), so only the whisper language line (01.3.1 col 51) is read and no script text is copied out |
| Script execution and runtime state | `set -euo pipefail`, list-argument `ffmpeg`/`ffprobe` only, no `eval`, no `jq`; `UV_PROJECT_ENVIRONMENT` under `$NTS_HOME` (D14); all runtime state under `$NTS_HOME` (FileVault) — `/Volumes/Project` is unencrypted with owners disabled, so no secret or runtime state lives in the repo |

## 9. Test plan

- **Font register (05.2.2-T1):** `check-fonts.sh` exit 0 on the MPT checkout; negative `STHeitiMedium.ttc` → exit 1 listing the missing letters with count **92** (X1); negative corrupted copy → exit 1 hash mismatch; the `vi-glyphs.txt` one-liner asserts **178** unique NFC letters; the size-84 PNG proof renders the FR sentence with no tofu box.
- **Presets and fit (05.2.3-T1):** the preset file parses and matches D6 key-for-key; `subtitle_fit.py` on `subtitle-preview-vi.txt` prints ≤ 42 chars/line landscape and ≤ 28 portrait (X2, D6); a cue longer than `maxCharsPerLine × maxLines` and a 4-line cue are both flagged; exit stays 0 on a long script.
- **Evaluation harness (05.2.1-T1):** `subtitle_sample.py --seed` twice → identical cue ids; never fewer than 20 cues when the SRT has ≥ 20; a synthetic SRT with a zero-length cue yields `zero_duration > 0`; `results.csv` has exactly 6 rows and the exact header; `on_time_share` derives from the four labels and their sum equals `sampled_cues`. The D14 fixtures `render-check.v1.{ok,zero-cues,no-subtitles}.json` deserialize (05.5.1-T2) and the calibrated numbers must surface as `checks[]` ids `subtitles_present` / `subtitle_text` and hints `subtitle_sync_suspect` / `subtitle_cues_long` (D10). No permanent test file is added by 05.2.1-T1; the tools carry the negative cases as scripted runs recorded in their acceptance checks.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 05.2.1-T1 | Subtitle fidelity and sync evaluation (6 videos) + threshold calibration | BA | M (1.0) | 05.5.2-T1, 05.5.1-T1, 05.4.1-T1 | 05.5.1-T2, 05-M0 gate |
| 05.2.2-T1 | Font register, licence, check-fonts.sh, glyph check | Worker | S (0.5) | 01.2.2-T1 | 05.2.3-T1, 05.1.1-T2, 05.5.2-T1, 11.3.1-T2, 11.3.5-T4 (11 CR-4) |
| 05.2.3-T1 | Subtitle presets, fit tool, preview frames | Worker | S (0.5) | 05.2.2-T1, 05.4.1-T1 | 05.1.2-T1, 05.5.2-T1 |

Total **2.0 d** (high-level 2.0 d; README §9 — the WER work is replaced by fidelity + sync at the same size, F01).

### Open items for the TL

- [RULED — README §10 T27] **D6 vs X2:** D6 sets landscape `maxCharsPerLine` 42 while X2 measured ≤ 41 at size 84 — 05.2.3-T1 keeps 42 and flags 42-char cues for review; 41 needs a D6 revision (D6, X2, F15).
- [RULED — README §10 T27] **D6 landscape `customPosition = 70.0` is inert** under `subtitlePosition = "bottom"` (C16/D6): the O4 ± range 55–70 applies to the portrait preset only (D6, O4, F23).
- [RULED — README §10 T28] **Artefacts outside the D1 ownership map:** `trial/tools/subtitle_sample.py`, `trial/fixtures/vi-glyphs.txt` + the proof-PNG flow, `subtitle-preview-*.txt`, `subtitle-eval-vi-names.txt`, `shorts-overlay-1080x1920.png` — proposed D1 rows (D1, D14).
- [RULED — README §10 T29] **`font_glyph_check.py` rests on an unrecorded signature:** C16 cites `subtitle_font_supports_text` only via `webui/Main.py` L7774 ⚠️; the tool reads the call shape at `MPT_COMMIT` and exits 2 rather than guessing (D7, C16).
- [RULED — README §10 T13] **D10 names no `RenderQuality:*` keys** for the `sync_suspect` bounds or `coverage_share`; 05.2.1-T1 proposes them in `trial/subtitle-eval/README.md` as a TL ruling (D10, F01, F02).
- [RULED — README §10 T14] **L2 (en) depends on 04 O2, still pending:** without en, L2 becomes a vi render (`source = substitute`) and the en evidence moves to E2/E4; the 6-video count and ≥ 95 % target are unchanged (F24, 04 O2).
- [OWNER — README §10 O4] **Shorts overlay mock (F23 ⚠️):** the tracked artefact must not contain third-party UI; otherwise the owner supplies their own screenshot, or the rectangle-only mock is used and the overlap finding stays [INFERENCE] (F23, O4).
- [OWNER — README §10 O5] **O5 revisit:** 05.2.1-T1 delivers the `long_cues` counts O5 asks for; many > 2-line cues make option (b) (plan 02 writer prompt ≤ 80 / 52 chars) the follow-up (O5, F15).

## 11. Rollout

- **Order.** `05.2.2-T1` (after the MPT checkout 01.2.2-T1) → `05.2.3-T1` (after 05.4.1-T1's `mpt_client.py` + stubs) → `05.5.2-T1` (trial, F24) → `05.2.1-T1` (evaluation + calibration); the first two can run in parallel with 05.1.1-T1 / 05.5.1-T1.
- **Phase and gate.** All three tasks are **M0**, no flag gates them, no M1 work. The 05-M0 gate needs `check-fonts.sh` exit 0, the presets file at revision 1 with owner-approved frames, and the evaluation report with calibrated SRT thresholds (README §7).
- **Prerequisites and config.** MPT at `MPT_COMMIT` with `resource/fonts` (01.2.2-T1); `$NTS_HOME` exported and `UV_PROJECT_ENVIRONMENT` under it; ffmpeg ≥ 9 (D9); the `spike` profile for preview renders (D12 stubs 18003/18004); no OpenAI key (C19). Preset values are **data**: a change bumps `revision` and `subtitles.preset` becomes `<aspect>@<revision+1>` (D6, D11) with no code change; the only new config names are the `RenderQuality:*` thresholds derived from `results.csv` (05.5.1-T2, D10), proposed by 05.2.1-T1.
- **Monitoring and rollback.** Node: `check-fonts.sh` exit code and `render-node-health.v1.reasons ⊇ {font_missing}` (D4); Worker (M1): the D10 hints per render; Rollback = revert the preset file to the previous revision (the node keeps the embedded copy until redeploy); `BeVietnamPro-Medium.ttf` is the only fallback and `STHeitiMedium.ttc` is never one (F04).
