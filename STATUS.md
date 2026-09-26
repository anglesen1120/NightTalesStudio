# Implementation status

The single place to see what is **done**, **in progress**, **blocked** and **next**. Update it in the same commit as the work. Plans stay the contract (`plans/`); this file only tracks execution.

Legend: `done` (acceptance checks run and passed) · `doing` · `blocked (<who>)` · `todo` · `partial` (see note).

## Resume here

- **Last session:** 2026-09-26. **M0 wave 1** (8 slices) implemented on branch `feature/m0-wave-1`: `03.4.1-T1` (MPT image-failure spike → `worker-node/contract/images.md`), `03.1.1-T1` (7 style templates), `05.2.2-T1` (fonts), `05.3.1-T1` + `05.3.2-T1` (music tooling), `02.1.3-T1/T2` offline tooling + **02 T99** (length findings advisory), `04.2.2-T1` fixtures + `04.2.3-T1` tool leg, `02.5.2-T0` Python half + `01.3.3-T1` (`worker-node/CARRY-OVER.md`). Each slice: implement → independent QA → code/performance/security reviews → fixes → re-test → final integration review (verdict *mergeable-with-fixes*; its 4 findings fixed, incl. a real narration leak in the committed spike samples). **Preset gate T34 now PASSES**: 18/18 cells, 0 blocking, $0.0255 — evidence `.nts/qa-02e/summary.md`.
- **Next actions:**
  1. ~~Owner: review and merge PR #6~~ → merged 2026-09-26 (`e1046ff`). ~~PR #7~~ → merged 2026-09-26 (`dda2b56`).
  2. ~~Owner: merge the PR #7 gate-fix PR (#9) and PR #10~~ → merged 2026-09-26 (`e5d9129`, `9057327`). #10 also carried two post-gate commits, re-tested only: `887a554` (`start_run.sh` rejects an empty `--voice` with exit 2; shellcheck + 4 cases) and `721d810` (CR-22 wording). ~~#11, #12, #13~~ → merged 2026-09-26 (`d746696`, `f84c415`, `884210a`).
  3. Owner/TL: decide **04 CR-17** `[OPEN]` (NFR-04-3 evidence: RTF-normalised and/or in-request overhead instead of raw wall; the pass rule is unchanged until then). Evidence: re-run 2026-09-26T01:47Z raw vs RTF-normalised — vi-5min 0.76 % / 0.49 %, en-5min 3.76 % / −1.34 %, vi-1000 2.69 % / 4.46 %, en-1000 9.04 % / 6.33 % (1000-char rows informational); in-request `(wall − vieneu)/vieneu` 0.008–0.031 % on every fixture. The en denominator is a `Hải Đăng` (vi voice) direct run (`direct_baseline_voice`).
  4. Owner + raters: fill the 04.2.1-T1 rater sheets (`trial/voice-audition/README.md`), then pick the voice set → unblocks 04.2.2-T1.
  5. ~~01.2.2-T6 wiring render, then 01.3.1-T4 calibration~~ → done 2026-09-26 (branch `feature/01-wiring-calibration`, stacked on `fix/04-pr7-gates`). Owner, remaining for 01.3.1-T4: **(a)** quiet the Mac (close the Virtualization VM, Chrome, `VBCSCompiler`; at 09:15 local, pressure level was 2 (warn) and swap 3.98 GB), then re-run the 1-minute render so the sheet gets a D11-clean row; the first row is structural calibration only (perf review). **(b)** The relay has no dashboard (DEC-9), so `dashboard_cost_usd_check` stays `n/a:pending-dashboard` until the owner reads the relay's cost for 2026-09-26 UTC.
  6. ~~Ask the relay to enable image generation (DEC-10)~~ → proven by run `20260926-0818-en-landscape-01`: 5 image calls through tap → relay, all 200 (`via=responses`), 5 `image material rendered:` lines. Still owner: **set a spend cap** in the relay account, and the relay's `gpt-image-2` price (image cost stays `n/a:no-price`, so the T6 "cost ≤ ceiling" check stays open).
  7. **Owner/TL decision — plan 02 D4 `standard` length (no longer blocks the preset gate; still decides the 02.1.3-T1 A/B's `length_fit` band):** the gate now **passes** under **02 T99** (length/segment findings advisory for M0) — 18/18 cells, 0 blocking, $0.0255, evidence `.nts/qa-02e/summary.md`. The measured reality (same run, D4 token rule): **en 570–831 units, median 732** against the 725–1,160 band (5/9 in band); **vi 573–859, median 749** against 800–1,280 (3/9). This **corrects** the earlier "en median ≈ 650" figure — the T93 floor sentence and the T97 `units_per_segment` budget moved luna to the floor. D4's rates (vi 160 / en 145 tokens/min) remain unmeasured defaults against the measured VieNeu rates (vi 263 syllables/min, en 200 words/min), so at the real voice rate the current `standard` band is ~3.6–5.8 min (en) / ~3.0–4.9 min (vi), not the declared 5–8 min. Decision after the A/B: recalibrate the band to measured rates (needs a length mechanism for the top of the range), or re-anchor `standard` to a shorter duration. Full arithmetic: `.nts/qa-02e/summary.md`.
  8. Owner: add official `gpt-6-sol` (and `gpt-6-luna` cache-write) prices to `trial/prices.json` (02 CR-27) before the 02.1.3-T1 `--matrix` run; add `gpt-6-sol` to the story allow-list (02 README §12 step 6); name 2 native raters per language (step 7).
  9. Owner: `.NET 10 SDK` is not installed on this Mac, so 02.5.2-T0 (SmartReader vs trafilatura spike) is blocked; it arrives with 09.1 (`global.json` 10.0.401) unless installed earlier.
- **Environment (DEC-5, DEC-6):**
  - Implementation machine = this Mac (M4, 16 GB, macOS 27.0).
  - `NTS_HOME=<repo>/.nts` (gitignored; `lib.sh` default).
  - D13 mode `accepted-risk`: no volume encryption. Keys must never be committed or pushed: `.gitignore` plus the gitleaks hooks.
- **Before any work in a fresh clone:** `git config core.hooksPath .githooks`, then `worker-node/bin/bootstrap.sh all`, then `worker-node/bin/smoke.sh`.

## Plan 01 — Environment setup (29 tasks)

| Task | Status | Evidence / note |
|---|---|---|
| 01.1.1-T1 Tools + version manifest | done | Installed uv 0.12.18, gitleaks 8.30.1, shellcheck 0.11.0. `env-versions.sh`: 16 keys; `mpt_commit` shows the clone SHA once cloned; exit 2 without tools; no user/host/paths in output. D13 recorded in `worker-node/README.md` |
| 01.1.1-T2 Pins | done | `worker-node/pins.env`: 13 keys (D2 incl. `PROXY_PYTHON`). MPT kept at candidate `3d5f4e42` (main is ahead by 2 commits: an LLM provider and subtitle-font validation). VieNeu `c1390abb` (3.8.3, `uv.lock` tracked). Codec re-pinned to `…-Nano-ONNX` (CR-12) |
| 01.1.1-T3 Runbook + clean-account rehearsal | todo | Runbook sections accumulate in `worker-node/README.md`. The rehearsal runs last, after 05.5.2 |
| 01.1.2-T1 Layout, `.gitignore`, `$NTS_HOME` | done | Check: 11 paths ignored, 7 kept. Added `.nts/` and `!worker-node/pins.env` (`*.env` hid `pins.env`, CR-11). `tmutil isexcluded` → `[Excluded]` (no Time Machine destination is configured) |
| 01.1.2-T2 `lib.sh` + `secrets.sh` | done | `init-local`: created ×3, then unchanged ×3. `check` shows the OpenAI keys missing. Value length 64. `secret` distinguishes a missing item from an unknown service. `security -i` store and verify pass. `check --local --acl` → 3 × `acl ok`. No value appears in any output |
| 01.1.2-T3 `bootstrap.sh` | done | Fresh run 42 s: `mpt … sync=frozen strip=skipped` (the strip script arrives with 01.2.2-T3), `vieneu … sync=frozen`. Second run 2 s with no clone or fetch. HEAD is detached. Python 3.11 / 3.12. Untracked files survive. Wrong pin → exit 2. Dirty tree → exit 1 |
| 01.1.2-T4 gitleaks hooks | done | Tree scan and history scan exit 0. One reviewed false positive is recorded in `.gitleaksignore` (`plans/00-overview.md:412`). Throwaway repo: commit with a secret blocked, clean commit ok, pre-push blocks a `--no-verify` leak. Both hooks fail closed |
| 01.2.1-T1 VieNeu install + weight pins | done | Facts V1/V3/V5 confirmed at the pin. Licences apache-2.0 (model and codec). First download + load took 104 s. `weights.sha256`: 15 files, 7 under `onnx_update/`, 0 under `onnx_int8/`. Offline start: ready in 1.4 s, 0 Hub requests. `--fetch` and `--verify` → `OK 15 files` |
| 01.2.1-T2 `start-vieneu.sh` | done | READY after 2–4 s. Listens only on `127.0.0.1:8000`. The key is in the server env and in no process argv: the child env is built with builtins after the review finding. Caller variables are dropped (canary and `HF_TOKEN` absent). Second start → exit 2. `HOST=0.0.0.0` from the caller is ignored. Env-file guards (HOST, int8, key in file) → exit 2. Ctrl-C → exit 0, port freed. The Wi-Fi-off check was replaced by `HF_HUB_OFFLINE=1` with 0 Hub requests in the log |
| 01.2.1-T3 Smoke scripts | done | `smoke-vieneu.sh`: every line PASS/INFO. Voices 25. `POST /v1/voices` unauthenticated → 401 ×2. `GET /v1/voices` without a key → 401. RIFF size `ffffffff` (04 F3). `pcm_s16le,48000,1`. Snapshot committed; the drift test FAILs as intended. `check-binds.sh` flags `*:8001` and 8505. `smoke.sh` → exit 0 |
| 01.2.1-T4 Mac baseline, 120 s gate, watermark | done | `trial/baselines/vieneu-mac-20260925.json`: RTF 0.176–0.186, **gate 120 s pass** (projected 55.7 s per 300 s audio; informational, 04 CR-2), cold start 4 s / 2 s, idle RSS 512 MB, peak RSS ≈ 1.44 GB, 9 runs all 200. Narration rate: vi 263 syllables/min, en 200 words/min (01.2.1-T4 step 1 assumed 170 / 150 [INFERENCE]). Four WAV samples in `$NTS_HOME/runs/samples/` (RIFF/data `ffffffff`, decode ok). `-2.json` = the `--extra watermark` run. Watermark: DEC-7 / CR-13 |
| 01.2.2-T1 MPT install at pin | done | HEAD `3d5f4e42` = pin, Python 3.11.16, `uv sync --frozen` idempotent, ffmpeg 9.0.2 via `IMAGEIO_FFMPEG_EXE`, MIT. Pinned example snapshot + sha256 in `worker-node/mpt/`. M1 line anchors shifted at the pin (log_level L9, listen_host L11, port L12, [app] L14, [whisper] L500, [kokoro] L576); keys and defaults match |
| 01.2.2-T2 Config template + renderer | done | `render-mpt-config.py --self-test` 7/7, `--check` all guards PASS, output 0600, drift diff redacted, unexpected-key shows `<set>`. SEC-09 with the live OpenAI values: partial (keys missing; proven with the real MPT/proxy keys + in-process test values). Added `--allow-direct-openai` for `start-mpt-api.sh --no-tap` |
| 01.2.2-T3 Strip music + CC0 placeholder | done | 29 bundled tracks removed, idempotent; plant/tamper/symlink tests removed; `bootstrap.sh mpt` prints `strip=ok`. Placeholder `nts-ambient-pad-cc0.mp3` generated in-house with ffmpeg (90 s), row in `LICENSES.csv`; header upgraded to the T3 12-column form. A BA-picked track can replace it |
| 01.2.2-T4 `start-mpt-api.sh` | done | All guards PASS: no tap → 2, port busy → 2, foreign 8002 listener → 2 before any Keychain read, edited `tap.json` → 2, missing `nts.openai.story` → 2 naming the service only, no WebUI script. **Live start with the real tap and keys → `mpt-api ready on 127.0.0.1:8080` in ≈ 5 s.** WARN only for 8001 (plan 04 proxy not landed). Fix vs spec: secrets are read as standalone assignments; inside `VAR="$(secret …)" cmd` a failing `secret` doesn't stop the script |
| 01.2.2-T5 `smoke-mpt.sh` | done | With tap + VieNeu + MPT running, **`smoke.sh` → exit 0, 39 PASS, 0 FAIL**. That includes the auth 401/403 rows, origin block, LAN refused and SEC-09 no-value-leak over the 4 real Keychain values. `ps` shows 0 `Authorization: Bearer` in any argv. API down → `FAIL mpt.listening`, exit 2 |
| 01.2.2-T6 Wiring render | done (cost check owner-blocked) | Run `20260926-0818-en-landscape-01`, MPT task `35ab583d-…`: `task … finished, generated 1 videos.`, `final-1.mp4` h264 1920×1080 + aac, 31.83 s (TTS 31.82 s), full decode exit 0. Tap: 1 chat + 5 images, all 200, `via=responses` for images. 0 `openai image generation failed`, subtitle 11 cues, `detected language: 'en'` p = 1.00, whisper loaded from `./models/whisper-large-v3-turbo`, 0 secrets in the run files. Cost: chat $0.000049; the image charge can't be priced on the relay (`n/a:no-price`), so the "≤ ceiling" check stays OPEN; the image count 5 ≤ 15 holds. Fixture `trial/fixtures/wiring-en.txt` (102 words). Voice `kokoro:Ngọc Huyền` |
| 01.2.3-T1 Whisper model fetch | done (MPT-API proof in the T6 run) | `fetch-whisper-model.sh`: 7 files, 1.5 GB, `--verify` OK, re-run 3 s, tamper → 1, no symlinks, ignored by upstream `.gitignore`, MIT, survives `bootstrap.sh mpt`. Offline load proven directly with faster-whisper + `HF_HUB_OFFLINE=1`, and through the MPT API in the 01.2.2-T6 run (`loading model: ./models/whisper-large-v3-turbo`, no hub download, 2026-09-26). This Mac drops IPv6, so the script forces IPv4 when a v6 probe fails. `mobiuslabsgmbh/…` now redirects to `dropbox-dash/…`; the pin SHA is still HEAD |
| 01.2.3-T2 Whisper baseline | done | `trial/baselines/whisper-mac-20260925.json`: RTF vi 0.46–0.66, en 0.31–0.36; peak RSS 2.43 GB; language detection is correct for all 4 (p ≥ 0.999), so CR-5 isn't needed. Params = MPT `subtitle.py:62-68` |
| 01.2.4-T1 Org verification + billing guard | n/a (DEC-9) | No OpenAI org: calls go through the relay. The owner sets the spend cap in the relay account |
| 01.2.4-T2 Keys | done (relay) | Owner stored `nts.openai.story` and `nts.openai.image` via `secrets.sh set`; `check` → present |
| 01.2.4-T3 Access checks | done (01 DEC-11) | `openai-access-check.sh` → exit 0, 8/8 PASS. C1/C4/C7 send `reasoning_effort` = `OPENAI_EFFORT_CHAT` (now `medium`; `--only story` re-run 2026-09-25 → C1/C7 200 at medium). **C3 = one paid call to `/v1/responses` with the `image_generation` tool (`gpt-image-2`, host `gpt-6-luna`, effort low) → 200**, image 1536x1024 medium. Relay-side `/images/generations` is still broken (see Findings). Record: `trial/baselines/openai-access-mac-20260925.json` |
| 01.3.1-T1 Schema v2, dictionary, prices | done | `trial/measurements.csv` 58 columns (first 31 = 01 §5.3), CRLF, no BOM; `trial/schema.md` 58 rows; `trial/prices.json` checked 2026-09-25 against the official OpenAI pricing pages |
| 01.3.2-T1 RSS sampler | done | `trial/tools/rss_sampler.sh --selftest 60` passes: 57 rows, p95 79 ms, CPU 1.3 %. `ps rss` is in KiB (checked against `top`). TERM stop 75 ms. Roots and pressure are sampled on staggered ticks to stay under the 100 ms p95 |
| 01.3.1-T2 Usage tap | done | `trial/tools/usage_tap.py` + `start-tap.sh`. Every stub check passes: status order, D6 10-key lines, no secret or content in runs/logs, 429/moderation/timeout/busy mapping, quality injection, stub-upstream guard. Real calls: `GET /v1/models` 200 and one `gpt-6-luna` chat 200 with usage 8+5 tokens. `tap.json` upstream = `https://api.aixingialaire.shop`. Fix after integration: `start-tap.sh` did not export `NTS_HOME`, so the tap wrote `~/.nts/tap.json`. Now exported, and the tap's default is `<repo>/.nts` |
| 01.3.1-T3 Run collector | done (live row in T4) | `trial/tools/start_run.sh`, `run_manifest.py`, `finish_run.py`, 25 unittest cases (T-P1…T-P10 + regressions) OK, shellcheck clean, stdlib only. Plan review first: 01 CR-17 (markers `## generating video: <n> =>` / `## combining video: <n> =>`, Q2 closed, glued log records, `via=responses` → image cost `n/a:no-price`). QA (QaCollector): every runnable acceptance check PASS in a throwaway worktree with decoy listeners (8001 proxy not built yet): guards 2/1, run.json no secret/home path, dry-run 58 lines, real finish +1 row, re-finish exit 1 byte-identical, abort, dashboard. Review (ReviewCollector; its output failed schema validation but the findings payload was complete and was used): 15 findings, 14 fixed and re-tested, 1 rejected (LoomLoom marker: trials use `## downloading videos from`) |
| 01.3.1-T4 Calibration | partial (owner: quiet re-run + relay cost) | First sheet row, `run_status=calibration`: 58 fields, 6 `n/a` values each explained in `notes.md`, stage gaps 0 s, Σ windows = total 276 s, `image_count` 5 = rendered lines, wpm 192.5. Q1–Q10 recorded in `trial/runs/<id>/notes.md` and the 01.3.1 `plan.md` §2: no end-of-audio line, so `AUDIO_END_RE` stays `None` (Q1); the script marker is logged under `plan02` (Q9); the relay forces image quality/size (Q4); CR-1 proxy line present (Q8). Hand-offs **01 CR-18** (→ 05), **CR-19** (→ 03), **CR-20** (→ 04), **CR-21** (01 text). Not D11-clean (peer sessions, swap 1.8 GB), so the row can't serve as a RAM/wall baseline yet |
| 01.3.3-T1/T2 Carry-over | todo | After 05.5.2. Interim whisper notes: `.nts/runs/whisper-baseline/carryover-notes.md` |

## Plan 04 — Voice proxy (12 tasks)

| Task | Status | Evidence / note |
|---|---|---|
| 04.1.1-T1 Spike | done (`9d51285`) | `worker-node/proxy/SPIKE.md`: **D4 GO**. PCM→MP3 +36 ms, first MP3 272 ms, max gap 441 ms, RTF 0.193, MPT `requests` sees `ChunkedEncodingError` on abort. VieNeu slot release after a client disconnect: 81.8 s · > 90 s · ≈ 210 s · > 400 s → TL ruling T15 (drain the upstream, `ADMISSION_WAIT_S` 80 s) |
| 04.1.2-T1 Speech endpoint | done (`9d51285`) | `POST /v1/audio/speech`, streaming ffmpeg transcode, T15 drain, catalog revision 0 (`seed/voices.v1.json`, 2 entries), request log |
| 04.1.2-T2 Errors, admission, allow-list | done (`8bd9b76`) | Error mapping, admission wait, limits, route allow-list, wire-semantics tests |
| 04.1.2-T3 MPT-side baseline | done (gates closed on `fix/04-pr7-gates`) | Re-run on the fixed tree 2026-09-26T01:47Z → `trial/baselines/proxy-mac-20260926.json` overwritten (`--force`): `result: pass`, 8 runs, 7 checks true, failure injection 3 × 502 → MPT task failed at stage `audio`, 0 absolute paths. Pass rule (5-min fixtures, ≤ 5 %): vi-5min 0.76 %, en-5min 3.76 %. New additive fields (04 CR-17 `[OPEN]`): `proxy_rtf`, `direct_rtf`, `rtf_normalised_overhead_pct`, `in_request_overhead_pct`, `direct_baseline_voice`, `test_ids`, `finished_at_utc`, `skipped_checks`, `paths_relative_to`. A subset run without a `-5min` fixture now prints `INCOMPLETE 04.1.2-T3` and exits 1 (was a vacuous pass) |
| 04.1.3-T1 Voices endpoint + cache | done (`8bd9b76`) | `GET /v1/voices`, `voice_cache.py` |
| 04.1.4-T1 Health | done (`8bd9b76`) | `/health` → `proxy-health.v1` |
| 04.1.5-T1 Packaging | done (`8bd9b76`) | `start-proxy.sh`, `smoke-proxy.sh` (wired into `smoke.sh`), systemd unit, `LICENSES.md`, `env.example` |
| 04.3.1-T1 Option B note | done (`9d51285`) | `worker-node/proxy/FALLBACK-voxcpm.md` |
| 04.2.1-T1 Audition + selection | partial | Machine part done: 48/48 samples, mean RTF 0.209. Rater sheets not collected (`results.csv` header only); selection is owner + raters (D11). `audition.py` child env = D13.1 allow-list (gated 2026-09-26; missing ffmpeg now exits 2 before any synthesis) |
| 04.2.2-T1 Catalog revision 1 + fixtures | todo | After the 04.2.1-T1 selection |
| 04.2.2-T2 Catalog rules + seeder (C#) | todo | Needs 09.2.2-T1 and 09.2.5-T1 |
| 04.2.3-T1 Preview generation + upload | todo | Needs 04.2.2-T1 and 11.4.1-T1 (bucket) |

## Plan 02 — Story writing (M0 slice)

Branch `feature/02-m0-prompts` merged into `develop` via PR #12 (`f84c415`, 2026-09-26); branch deleted locally. Gates per task: QA agent → code, performance and security reviews → fixes → re-test. Plan text amendments: 02 README §10 **T65–T98**, §4 **CR-25…CR-28**; T73 minors wording applied to `plans/02-story-writing.md` :57, :152 as CR-28.

| Task | Status | Evidence / note |
|---|---|---|
| 02.1.4-T1 `prompts/` skeleton | done | 13 D5 directories (9 `.gitkeep`), `manifest.json` release `2026.10.1` (15 runtime files, sha256 verified; excludes `fixtures/**`, `tools/**`, `REFERENCES.md`, dotfiles, T75), `REFERENCES.md` (InkOS AGPL: idea only), `.github/pull_request_template.md` provenance checkboxes. QA-02A-1..8, CR-02A-1..4, SEC-02A-1..6, PERF-02A-1..5 fixed |
| 02.1.1-T0 Preset spec | done | `system/writer.v1.md` (two-tier data rule T69, metadata rule T70, minors T73, self-harm T74), `schemas/story.v1.json` (strict), `shared/structure-vocab.v1.json`, `shared/preset-spec.v1.md`. Short tier = setup → twist → reveal (T65), short writer cap 2,500 (T66), cache head = schema + system + preset + shared (T67, supersedes T36) |
| 02.1.2-T1 Keyword rules | done | `shared/keyword-rules.v1.md` R1–R13 (R3 no punctuation/digits T76, R6 widened T78, R7 text-bearing objects T79, R12 place identifiers T80, R13 no instructions T81), 30 good/30 bad examples, only model-facing text injected (T83). `test_prompt_shared_rules.py` 21 tests |
| 02.1.5-T1 Ear rules + overused lists | done | `shared/ear-rules.{vi,en}.v1.md` E1–E10, `shared/overused.{vi,en}.v1.txt` 30 lines each. Q1–Q6 naturalness questions live in the task file (for 02.6.1-T1) |
| 02.1.3-T0 M0 story harness | done | `trial/tools/story_harness.py` + `story_validator.py` + `LENGTH.v1.json`, 118 offline tests OK (2 skipped: manual). Live: 19/19 cells ok and schema-valid after T91 (`--timeout` 240 s, no retry on transport timeout, every attempt charged), cost exact from the response body. Fixes: redirects not followed (T95), manifest sha256 check (T96), per-brief facts + `--validate-only --brief-id` (T92), safe run ids, atomic metrics, D6 `units_per_segment` (T97), cell language from the brief (FR-1), person-name check narrowed to mid-phrase + honorifics (FR-2). Usage: `trial/README.md` |
| 02.1.1-T1/T2/T3 Presets (ghost, spiritual, true crime; vi + en) | done (gate T34 passes; length measured advisory per T99) | Six presets on `preset-spec`: all four shared-block placeholders (T49), two-tier data rule, tone rules, CTA. Gate T34 **PASS** 2026-09-26: 18/18 cells (3 presets × 3 briefs × 2 languages), 0 blocking, $0.0255. Length is advisory per **02 T99** — en median 732 units (band 725–1,160, 5/9 in band), vi median 749 (band 800–1,280, 3/9); the D4 recalibration waits for the A/B (next action 7). Evidence `.nts/qa-02e/summary.md` |
| 02.1.3-T1 36-story A/B (offline legs) | partial (owner inputs) | Offline tooling done on `feature/m0-wave-1`: `prompts/fixtures/rubric.md` (D16, 7 dims), `trial/tools/make_blind_kit.py`, `aggregate_scores.py` (+`--handoff`), `freeze_manifest.py`, with self-tests. Blocked on owner inputs only: `gpt-6-sol` price row (02 CR-27), the temporary allow-list entry, 2 native raters per language |
| 02.5.2-T0 Extraction/robots spike (Python half) | partial (owner) | `trial/research/corpus/` 20 pages + `corpus.sha256` (20/20), trafilatura 2.2.0 measured over all 20 (deterministic, vi diacritics NFC-clean, p95 ≤ 304 ms), RFC 9309 subset spec + 6 examples in `02.5.1-sources-fetcher/spike-report.md`. The .NET/SmartReader half stays `[OPEN]` (no .NET SDK, next action 9); corpus HTML is gitignored + regenerable |

## Plan 03 — Image generation (M0 slice, branch `feature/m0-wave-1`)

| Task | Status | Evidence / note |
|---|---|---|
| 03.1.1-T1 Style spec + 7 templates | done (owner review `[OPEN]`) | `seed/style-templates.v1.json` (7 rows, 1 `{term}` each, slot order + people rules), `seed/style-templates.md` (NegativeClause V1 + sha256), `seed/i18n-style-labels.v1.json` (15 keys × vi/en), `trial/tools/check_style_templates.py` (`7 templates ok`, 11-case self-test). Owner reviews the texts (~15 min) before 03.3.2-T1 spends |
| 03.4.1-T1 MPT image-failure spike | done | `worker-node/contract/images.md` (P0 I1–I17 at pin `3d5f4e42`; P1–P11 measured with committed redacted samples, incl. P8 read-timeout not retried, P9 garbage-skip + single url fetch, P11 random concat order = I10/F02). `trial/tools/{image_stub.py,redact_prompts.py}`; `--profile spike` in `render-mpt-config.py` + `start-mpt-api.sh` (config swap + restore proven, normal profile refuses a spike config). This is the artefact 05.4.1-T1 consumes. No OpenAI spend |
| 03.3.2-T1 Quality contact sheet | todo (owner review first) | Needs the owner's approval of the 7 texts; ~80 cells, `--max-usd 8`, owner rates ~30 min. On this relay `size`/`quality` are forced to `auto`/`gpt-image-2-codex`, so the quality axis may be unmeasurable |
| 03.1.2-T1, 03.2.x, 03.4.2/03.4.3 | todo (M1) | All need the .NET SDK / 09 wave 2 / 11.3.5 |

## Plan 05 — Video assembly (partial, branch `feature/m0-wave-1`)

| Task | Status | Evidence / note |
|---|---|---|
| 05.2.2-T1 Font licence + glyph check | done | `worker-node/fonts/FONTS.csv` (measured sha256 at the pin, OFL-1.1), `OFL-BeVietnamPro.txt` byte-identical to upstream, `worker-node/mpt/tools/font_glyph_check.py` (NFC input, fails closed when the font cannot be loaded — a wrong "ok" is worse than a wrong "fail"), `worker-node/bin/check-fonts.sh` (4 lines: 2 × `hash ok`, 2 × `glyphs ok 196/196`), `trial/fixtures/vi-glyphs.txt` (197 lines). STHeitiMedium misses 92/196 (measured) |
| 05.3.1-T1 `check-music.sh` | done (gate row `[OPEN]`) | 15-case hermetic suite; exit-code matrix 0/1/2; `--json` 5-key single line incl. the exit-2 shape (`catalog_revision: null`). The 05-M0 gate row stays `[OPEN]`: the catalog is legally empty (`tracks: []`), so the gate passes vacuously until the owner curates 20–30 tracks |
| 05.3.2-T1 Music curation (tool leg) | partial (owner) | `seed/music-tracks.v1.json` schema + `worker-node/music/tools/check_catalog.py` (56-case self-test, 23 rule codes) + 10 fixtures + the 16-column `LICENSES.csv` header. **Owner: choose 20–30 tracks** with licence evidence + the listening session |
| 05.4.1-T1/T2, 05.1.1, 05.2.x, 05.5.x | todo | The spine is now unblocked by 03.4.1-T1; 05.5.2 additionally needs 04.2.2-T1 (raters) + 03.3.2-T1 |

## Plan 01 — leftovers advanced

| Task | Status | Evidence / note |
|---|---|---|
| 01.3.3-T1 Carry-over | partial (§11 `[OPEN]`) | `worker-node/CARRY-OVER.md` §1–12: pins, 22 weight files with sha256, Python/locks, system packages, config/env with D5 guards, ports, start scripts → systemd, voices+music, CR-9 Mac→GPU deltas, open issues. §11 waits for the 05.5.2 trial rows; 01.3.3-T2 is the owner walkthrough |
| 01.2.2-T2 renderer | done (+`--profile spike`) | 14-case self-test; plan-01 **CR-23** records the self-test contract change |

Environment findings (02.1.3 `plan.md` open items): relay latency is bimodal (17–58 s vs 102–216 s) with 502s at 154–181 s; the relay bills abandoned calls; it rewrites `safety_identifier`/`prompt_cache_key`, drops `max_output_tokens`, and injects ~21 k chars of its own instructions (billed input ≈ 2.5× our prompt). The tap attributes a call to the run holding `current_run` when the response completes (CR-25 → 01.3.1-T2). The two `trial/runs/20260926-1301-vi-landscape-01` / `-1307-en-landscape-01` dirs (moved to `.nts/qa-02e/tap-runs/`) are **this session's own preset sweeps** — the harness ran without `start_run.sh`, so the tap attributed both halves to a default run label; their cost is the $0.0255 recorded above, nothing is unaccounted for.

Wave-1 process notes:
- **Flake (unreproduced, recorded not hidden):** `FixAbTooling` saw one transient failure in `trial/tools/tests/` ("`finish_run` state race") on one run of `unittest discover`; the same command passed on re-run and on every later run this session (118 OK, 2 skipped). Not quarantined because it does not reproduce — if the 02.5.x/01.3.1 work sees it again, quarantine with `[Trait("Category","Quarantine")]`/skip and file it rather than retrying.
- Gate grouping: the eight slices were QA'd per slice; the performance and security reviews each covered all eight but reported per task id, so no task lost its review. Two slices paired two tasks (`04.2.2-T1`+`04.2.3-T1`, `02.5.2-T0`+`01.3.3-T1`) — one QA and one review pass each, covering both task ids by name.

Other plans: not started. Order: rest of 02 M0 (A/B) → 03 M0 → rest of 04 → 05 → Gate 1 (`plans/features/README.md`).

## Owner checklist — 01.2.4-T1 (OpenAI dashboard, start first: external wait)

Spec: `plans/features/01-environment-setup/01.2.4-openai-access/01.2.4-T1-org-verification-billing-guard.md`. Never paste a key, card or ID data anywhere; write back only the values in step 5.

1. **Verification.** https://platform.openai.com/settings/organization/general → "Verify Organization". Record `verified`, or `pending` + the UTC submit time, or `not_shown`.
2. **Prepaid credit.** Settings → Billing: confirm prepaid, buy **$30** (proposal), turn **auto-reload off** (it is on by default), reload the page and confirm it is off.
3. **Hard limit.** https://platform.openai.com/settings/organization/limits → Spend → monthly limit **$30**, **Enforce a hard limit = on** (not alert only). An optional alert at $15 may be added.
4. **Read** the usage tier (expect Tier 1), the `gpt-image-2.5-flare` rate limit (expect 5 images/min), the `gpt-6-luna` RPM/TPM, and the approved usage limit.
5. **Report back** for T3: `verification`, `verification_submitted_at`, `auto_reload=false`, `hard_limit_usd=30`, `credit_purchased_usd`, `usage_tier`, `image_rate_limit_ipm`, `story_rate_limit`, `usage_limit_usd`, and any dashboard labels that differ from the steps above.

## Findings during implementation (recorded as CRs / notes)

- **CR-11** — `NTS_HOME=<repo>/.nts` (DEC-5). `*.env` hid `worker-node/pins.env` → `.gitignore` adds `!worker-node/pins.env`.
- **CR-12** — The Mac ONNX path uses codec `OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX`, not `…-Nano` (V5 was wrong for ONNX). The PyTorch path (GPU worker) uses `…-Nano`. Plan 11.3.1 must pin it: **[OPEN] for plan 11**.
- **DEC-6** — No volume encryption. The only hard rule: API keys are never published. D13 mode `accepted-risk`.
- **Bootstrap spec gap (01.1.2-T3)** — A fresh `git clone --no-checkout` shows every path as deleted. The cleanliness check therefore skips a clone made in the same run.
- **Watermark (01.2.1-T4 steps 2 and 4; DEC-7, CR-13)** — without the extra: class **C** (silent off). `import perth` → ModuleNotFoundError, and `src/vieneu/base.py:128-131` logs only at DEBUG. **With `--extra watermark` it is still off (class B):** `resemble-perth` 1.0.1 is locked with no dependencies (`uv.lock:3381-3387`), so there is no torch, and the server logs "Watermarker init failed" (`base.py:134-144`). Cost of the extra: RTF +11–25 %, idle RSS 512 → 662 MB. `uv sync --frozen` without the extra removes it again (Q5 answered). **Decision (owner):** keep the watermark; it is required in production, where the node syncs `--extra cuda --extra watermark` and fails closed unless `Audio watermarking initialized (Perth)` is logged (11.3.1-T1). The Mac M0 trial runs without it. **Caveat, verified in source:** Perth `apply_watermark(self, signal, sample_rate, **_)` takes no payload, so every Perth user gets the same mark. It marks audio as AI-made but **cannot prove NightTales ownership**. **Ownership proof decided (owner 2026-09-25, DEC-8, CR-14): option (a)**, a signed per-render hash manifest (sha256 of each rendered file + time + signature, kept by the system). Rejected: a payload watermark (new dependency) and paid Resemble (second paid provider). Design is [OPEN] for plan 05/06 detailing.
- **Cold start** — 1–4 s from the warm cache on this Mac (V2 expected ≈ 19 s).
- **Narration rate** — VieNeu ignores `speed` (04 F4). The measured vi 263 syllables/min and en 200 words/min are faster than the 170 / 150 assumed in 01.2.1-T4 step 1 [INFERENCE], so a 5-minute story needs ≈ 1,300 vi syllables. Follow-up: a CR to the plan 02 length tables (not raised yet).
- **Code review (Day1Review, 2026-09-25)** — 4 findings, all fixed and re-verified:
  - (1) `env -i … KEY=…` put the key in the argv of `/usr/bin/env`; the env is now built with builtins and `exec`.
  - (2) The bench SIGKILL fallback orphaned the listener; it now kills the listener and asserts the port is free (shellcheck clean; the fallback runs only when the server hangs, so it was not exercised).
  - (3) The `xargs tar` tree-scan could drop files; it now uses `tar --null -T -` (606/606 files).
  - (4) Stale "volume" wording in `lib.sh` and the D1 amendment was aligned to DEC-6.
- **bash 3.2** — macOS `/bin/bash` is 3.2. No `mapfile`, `declare -A` or `${x@Q}` in any script.
- **QA pass (QaDay1, independent test agent, 2026-09-25)** — re-ran the acceptance checks of all 10 day-1 tasks plus exploratory negatives. Nothing FAILs in the matrix (items overridden by CR-11/DEC-6 are marked AMENDED/SKIP). Defects:
  - F2 (M): `check --acl` was flaky, one dump per service at ≈ 70 s each. **Fixed:** one dump per run with one retry on a miss, ≈ 24 s.
  - F3 (L): the gate decision fields were missing in `-2.json`. **Fixed.**
  - F4 (L): `init-local` accepted extra args. **Fixed:** exit 2.
  - F7, F8: intended behaviour.
  - Spec-text nits, where the task file stays and the runbook form is authoritative:
    - F1: `gitleaks dir .` walks the ignored `.nts/`; use the runbook tree-scan recipe.
    - F5: the encoder grep counts `aac_at` too, giving 3.
    - F6: bare `shellcheck` prints SC1091; use `shellcheck -x`.
    - The SEC-07 negative test cannot fail: an item created by `/usr/bin/security` always trusts `/usr/bin/security`, even without `-T`.

- **QA pass (QaDay2) and code review (ReviewDay2), day 2, 2026-09-25.** QA reproduced every STATUS claim for the 8 tasks; the live-API rows are legitimately partial. Fixed and re-tested:
  - QA D1 (M): the sampler self-test sampled a foreign tree when 18099 was already taken. It now requires the port free (busy → exit 2) and only accepts its own listener.
  - QA D2 (M): the self-test printed criteria but always exited 0. It now prints `selftest: PASS|FAIL` and exits 1 on any failed criterion.
  - QA D3 (L): the load buffer was compressible. It now uses `os.urandom`.
  - Review (M): `start-mpt-api.sh --no-tap` also skipped the 8001 proxy-listener ownership check, while the proxy key still goes to 8001. Now only the 8002 check is skipped; a foreign 8001 listener → exit 2.
  - Review (L): `smoke-mpt.sh` `mpt.no_value_leak` passed after checking nothing. A missing redacted state file is now a FAIL.
  - Review (L): `--template`, `--mpt-root` and `--source` given with no value fell back to the default. Now exit 2.
  - Noted, not fixed:
    - Review (L): between root refreshes (≤ 10 s), a reused pid could be walked as the service tree.
    - QA D4: the M1 line anchors in the task files are stale; the pin-verified numbers are in the template comments.

- **Owner decisions (2026-09-25, later in the day):**
  - DEC-9: OpenAI calls go through the relay `OPENAI_BASE_URL` in `pins.env`.
  - DEC-10: the relay has no per-key allow-list, so C2/C4/C6/C7 are recorded, not failed. For images, `gpt-image-2.5-flare` is tried first and `gpt-image-2` is the fallback.
  - Story writer effort: `OpenAi:Effort:Writer` defaults to `medium`, and the 02.1.3 harness/matrix also runs at `medium` (plan 02 D7). Every other step stays `low`. MPT doesn't write stories (02 CR-10), so no MPT config changes.

- **QA (QaTap) and review (ReviewTap), tap + access check, 2026-09-25.** QA: every check PASS; the STATUS claims were reproduced (39/0 smoke, MPT ready, tap.json in `.nts`). Fixed and re-tested:
  - Review (M): the stub-upstream guard compared path strings. On case-insensitive APFS, `TAP_RUNS_DIR=<repo>/TRIAL/RUNS` got past it (reproduced). The guard now compares (dev, inode) up the ancestor chain; `TRIAL/RUNS`, `trial/runs` and `Trial/runs/new-sub` → exit 2. The reviewer's `normcase` fix would not have worked: it doesn't lowercase on POSIX.
  - Review (M): an invalid `TAP_UPSTREAM` port crashed with a traceback. It now exits 2 with `invalid port`.
  - Review (L): headers whose names differ only in case were forwarded twice. They are now forwarded once per case-insensitive name. A real `GET /v1/models` through the tap still returns 200.
  - Review (M): a non-JSON flare body would crash the access recorder after a paid call. It is now parsed defensively. Review (L): `000` now records `reason: upstream`. QA-1: the recorder wrote `null` error fields as the string `"None"`; they are now JSON `null`.
  - Review (M): stale `api.openai.com` tap/upstream wording in 01.3.1 `plan.md`, 01.2.4 `plan.md` and 02.1.3 `plan.md` now points at `OPENAI_BASE_URL` (CR-15).

- **TechLead advisories, 2026-09-25.** All of them were checked against the repo.
  - Applied: DEC-10 now says the relay scopes access per key group, not per model within a group. C4 (503, maintenance) is recorded as inconclusive rather than "not denied".
  - `OpenAiOptions.BaseUrl` in 02.2.1-T2 and the moderator in 02.4.1-T1 now use the pinned `OPENAI_BASE_URL` in every environment. Before this change the relay would have been refused in Production.
  - Stale `api.openai.com` upstream wording was also fixed in 01 README D6, 01.2.2 `plan.md` and T4, and 02.1.3-T0 (CR-15).
  - Already covered: the flare→`gpt-image-2` fallback and the stop after 503, the 02.1.3 harness running at `medium`, and MPT auth (`mpt.auth.right_key` PASS).
  - Rejected: the guard "crashes if `trial/runs` is missing". `os.stat(root)` sits inside a `try` and returns False.
  - Open: relay prices in `prices.json` depend on the relay's price list (owner).

- **New image key, 2026-09-25 10:44Z.** `openai-access-check.sh --only image` results:
  - C0i 200. The key is in a "Codex" group with 15 models, including `gpt-image-1/1.5/2`; no flare.
  - C4 chat 200: this key can also chat.
  - C3 `POST /v1/images/generations` → 400 for both flare and the `gpt-image-2` fallback: "The 'gpt-5.4-mini' model is not supported when using Codex with a ChatGPT account". The relay serves images from a ChatGPT/Codex account and wraps the call around an unsupported host model. This is a relay-side defect.
  - Manual probe `POST /v1/responses` (host `gpt-6-luna` + tool `image_generation`, quality low) → 200 with a correct image. The returned size is 1536x1024, although 1024x1024 was asked. Usage is 6652+106 tokens; the tool's image usage is not reported separately.
  - Consequence: MPT generates images only through `images/generations` (01.2.2 M-facts), so the MPT image path did not work on this relay directly. **Closed by 01 DEC-11:** the tap translates to the Responses tool; the MPT image path works end-to-end (01.2.2-T6 run `20260926-0818-en-landscape-01`, 5 × 200, 2026-09-26).
  - Probe `POST /v1/images/generations` with `model: gpt-6-luna` on the image key → 400 "images endpoint requires an image model, got \"gpt-6-luna\"", rejected before any spend. Luna produces images only as the host of the Responses `image_generation` tool.
  - gpt-image probes on the image key, 2026-09-25:
    - `images/generations`: `gpt-image-1` and `gpt-image-1.5` → 400 (the same `gpt-5.4-mini`/Codex error); `gpt-image-2` → 503 "No available compatible accounts".
    - `/v1/responses` (host `gpt-6-luna`, tool `image_generation` with `model` = gpt-image-1/1.5/2): all three → 200, one PNG each. Sizes: 1672x941 for image-1; 1536x1024 for image-1.5 and image-2.
    - The relay ignores the requested `size` (1024x1024) and `quality` (low); every result reports quality `medium`, so cost per image is higher than requested. Usage reports only host tokens (~6.7k in + ~120 out); image tokens are not shown.
    - Whether the tool honours `model` is unverified [INFERENCE: the different size for image-1 suggests it does].

- **01 DEC-11: images via Responses + `gpt-image-2`, effort on every call (owner, 2026-09-25).**
  - Pins: 14 → 18 (`OPENAI_IMAGE_API=responses`, `OPENAI_IMAGE_HOST_MODEL=gpt-6-luna`, `OPENAI_EFFORT_IMAGE_HOST=low`, `OPENAI_EFFORT_CHAT=medium`: owner, story writing must not run at low; MPT `generate_script` writes the trial story via chat).
  - The tap translates MPT's `images/generations` into `/v1/responses` with the image tool and maps the reply back to `data[0].b64_json`. It injects effort into chat and responses calls that do not set one. D6 adds `via` and `request.reasoning_effort`.
  - Live: MPT's own `_parse_openai_image_response` + `_save_openai_image_file` accepted the tap's reply (1370x1148 PNG), and `smoke.sh` stayed at 39/0.
  - The tap now changes chat bodies (effort), so it is no longer a pure pass-through. This is documented in D6 / `worker-node/README.md`.
  - `--no-tap` with `responses` only warns. It is a non-measured test hook; images fail in that mode.
- **Relay image backend = `gpt-image-2-codex`, output not configurable (verified 2026-09-25).** Every Responses reply echoes the tool as `model gpt-image-2-codex, size auto, quality auto`, whatever model, size or quality is asked. Returned sizes vary (1536x1024, 1672x941, 1370x1148). This matches the open upstream bug [openai/codex#28723](https://github.com/openai/codex/issues/28723): the Codex/ChatGPT-OAuth image path ignores size and quality. The official API documents them as configurable. Consequences:
  - frame size is not deterministic;
  - CR-3 quality cannot be enforced on this relay [OPEN];
  - image cost is `n/a:no-price` until the relay prices `gpt-image-2` [OPEN, owner];
  - an OpenAI API key, or a relay backed by an API account, is needed for deterministic output.

- **QA (QaImageResp) and review (ReviewImageResp), DEC-11 change.**
  - QA: PASS on all 5 checks (pins, tap translation/effort vs stub, no leak, shellcheck, spec vs code). Two spec drifts fixed: the `tap.json` key list in T2 and `tap_image_request_invalid` added to T2/CR-16.
  - Review fixes:
    - (M) `plans/01-environment-setup.md` §5.2 `openai_image_base_url` now points at the tap.
    - (L) D6 `reasoning_effort` on `/chat/completions` now reads only `reasoning_effort`, so it matches what was sent.
    - (L) the tap's `read_pins` uses the same rules as `load_pins`: duplicate keys and malformed lines (e.g. `KEY = val`) → exit 2 (TechLead advisory; tested).
    - (L) T3 doc: C3 records `api` + `reasoning_effort`, not `via`.
    - All re-tested against the stub.
  - Not changed: D6 cannot tell injected effort from client-sent effort (informational; the contract says "effort sent").
  - Open for plan 11: the `plans/11-worker-deployment.md:75` egress allow-list still names `api.openai.com`; it needs a CR for the relay host (phase P).

- **Per-task QA + review before push (owner rule, 2026-09-25):** recorded in `WORKFLOW.md` §8.1, `plans/00-overview.md` DoD, 01 README §6 and `AGENTS.md`.
- **01.3.1-T3 review fixes:** the sampler is stopped only when the row is committed (dry-run and a failed validation leave it running); the I2 check and the append share one CSV lock, and so does `--dashboard-cost`; an unfinished task ends at its own last record; run NN = max suffix + 1; log paths in `run.json` are `$NTS_HOME/…`, repo-relative or a basename; the sampler pid is checked before SIGTERM; `terms` parse failure → `n/a:marker-missing`; input errors exit 1/2 without traceback; `rm -rf` guards `${run_id:?}`.
- **Incident (QA'd tree, 2026-09-25):** during development an agent's `rm -rf` with an empty variable removed `trial/runs/` (only the git-ignored `no-run/openai_usage.jsonl` of today's unmetered calls was lost; `.gitkeep` restored, `$NTS_HOME` untouched). A `--dashboard-cost` cross-check for 2026-09-25 would under-count those calls. Guards added (`${run_id:?}`).
- **PR #7 post-merge gates (2026-09-26, branch `fix/04-pr7-gates`).** QA (QaPr7Gates, independent): 04.1.2-T3 PASS on `develop`, D13 child env PASS at all three spawn sites; 8 defects. Code review 6, performance review 4, security review 3 findings. Fixed and re-tested:
  - QA-PR7-1 (M): `overhead_le_5pct` was vacuously true without a `-5min` fixture → now `null` + `skipped_checks`, `result: incomplete`, exit 1 (live: `--fixtures vi-1000 --repeats 1` → `INCOMPLETE`, exit 1).
  - QA-PR7-2 (M): new `--out FILE` / `--force`; the default name keeps the `-2`/`-3` fallback; an existing `--out` without `--force` → exit 2 (live).
  - QA-PR7-3 (M): `started_at_utc` is now the run start (was assembly time, ~4.5 min late); `finished_at_utc` added; file date = local start date.
  - QA-PR7-4 (L): `audition.py` checks ffmpeg before synthesis → exit 2, no traceback (new test). QA-PR7-6 (L): MoviePy `__del__` stderr noise suppressed in `mpt_decode_check.py`. QA-PR7-7 (L): empty `--fixtures` → exit 2 (live). QA-PR7-8 (L): `test_ids` map. QA-PR7-5: spec-conformant, comment only.
  - Code review: INT/TERM/HUP traps now exit and restore a bench-killed VieNeu (`VIENEU_KILLED` / `VN_RESTARTING` flags; not exercised live); bad `--direct-baseline` → exit 2 with a message (live); helpers' `--help` → exit 0; audition allow-list equality test vs `transcode._CHILD_ENV_KEYS`; dangling `F-ENV` → 04 README **D13.1**.
  - Security: SEC-PR7-1 (M) temp dir `nts-bench-proxy.XXXXXX`, header files removed explicitly, cleanup matched by basename (the prefix allow-list failed open); SEC-PR7-2 (L) `--noproxy '*'` on every bench curl; SEC-PR7-3 (L) `mp3_path` is `$NTS_HOME`-relative + `paths_relative_to`.
  - Performance: no hot-path defect (`child_env` 1.6 µs/spawn, decode check 0.3 s per 5-min MP3). Raw-wall overhead is confounded by VieNeu audio-length variance → additive RTF-normalised and in-request fields + **04 CR-17 `[OPEN]`**; the pass rule is unchanged.
  - Re-test: `pytest` 178 passed, `shellcheck -x trial/tools/bench-proxy.sh` clean, py_compile clean, full bench `--repeats 2 --inject-failure --force` → exit 0, `PASS 04.1.2-T3`.
  - Process note: the first re-run died with a bash syntax error because a fix agent edited `bench-proxy.sh` while it ran (bash reads scripts incrementally). Never edit a script that a live run is executing.

- **01.2.2-T6 + 01.3.1-T4 gates (2026-09-26, branch `feature/01-wiring-calibration`).** QA (QaWiringCalib, independent) PASS with 10 defects. Code review 5 L; performance 2; security 1 L. Fixed and re-tested (`unittest` 42 OK, py_compile, `shellcheck -x start_run.sh`):
  - `run_manifest.normalize_voice`: the `kokoro:` prefix collapses case-insensitively, and an empty name → exit 2. T6's `VOICE="kokoro:<id>"` had doubled the prefix in `run.json` (repaired once, recorded in `notes.md`); T6 now passes the bare id (01 CR-21).
  - `finish_run.py`:
    - a half-filled O6 split → `n/a:usage-split-absent`, not a silent 0 (`or` → `and`);
    - `rss.csv` is rewritten atomically, so a retried finish no longer duplicates rows;
    - `--task-id` must be an MPT UUID in every mode, else exit 2 (SEC-T6-1; T6 step 6 no longer interpolates it into `bash -c`);
    - cols 52–54 and the `rss.csv` `all` row cover the task window, not the sampler lifetime (perf);
    - `run.json` records the collector file hashes;
    - `note:` is no longer double-prefixed.
  - `notes.md` corrections: 80 header records (55 in the run window), sampler span 700 s (95.1 %), tap ts ±1–2 ms, cost check OPEN.
  - T4 step 2 now gates the run on a quiet machine: `memory_pressure` normal, and swap within ~128 MB of the idle value.

## Session log

| Date | Session | Done |
|---|---|---|
| 2026-09-25 | Day 1, plan 01 | 01.1.1-T1/T2, 01.1.2-T1…T4, 01.2.1-T1…T4 done. DEC-5, DEC-6, DEC-7, CR-11, CR-12, CR-13. Code review: 4 findings fixed. QA pass: 3 defects fixed. Ownership proof = signed manifest (DEC-8, CR-14). Branch `feature/01-env-setup-day1` → PR #1 into `develop` |
| 2026-09-25 | Day 2, plan 01 | 01.2.2-T1…T3, 01.2.3-T2, 01.3.1-T1, 01.3.2-T1 done; 01.2.2-T4/T5 and 01.2.3-T1 partial (OpenAI keys + tap). QA: 3 defects fixed; review: 3 fixed. Branch `feature/01-env-setup-day2` → PR into `develop` |
| 2026-09-25 | Day 2 (cont.), plan 01 | DEC-9 relay, DEC-10. Owner stored the relay keys. 01.2.4-T3 story checks pass, images blocked on the relay side. 01.3.1-T2 tap done. 01.2.2-T4/T5 live: `smoke.sh` 39/0. Writer effort `medium` (plan 02 D7). QA + review: 7 fixes |
| 2026-09-25 | Day 3, plan 01 | 01.3.1-T3 collector done (CR-17 plan review). QA PASS; review: 14 fixed. Chat effort pin `medium` (owner). Per-task QA + review rule recorded |
| 2026-09-25 | 01 DEC-11 | Images via the Responses tool + `gpt-image-2`; effort pins for every call. Access check 8/8. MPT image path live through the tap. Smoke 39/0. Relay forces `gpt-image-2-codex` with size/quality auto (codex#28723). QA + review: 6 fixes |
| 2026-09-26 | Plan 04 | 04.1.1-T1, 04.1.2-T1/T2, 04.1.3-T1, 04.1.4-T1, 04.1.5-T1, 04.3.1-T1 committed; 04.2.1-T1 machine part. Branch `feature/04-voice-proxy` → PR #6 into `develop`. 04.1.2-T3 baseline tools and the ffmpeg child-env fix landed separately in PR #7 |
| 2026-09-26 | Plan 04 (cont.) | PR #6 merged (`e1046ff`). Child-env allow-list (`4b49a9a`) and 04.1.2-T3 baseline tools (`fdb5d90`) → PR #7 (`dda2b56`), opened as a draft before the gates at the owner's request and merged by the owner; QA, code, perf and security reviews are still owed |
| 2026-09-26 | Plan 04 gates | PR #7 post-merge gates closed on `fix/04-pr7-gates`: QA → code/perf/security reviews → 21 fixes → bench re-run PASS. 04 D13.1 (child-env allow-list) and 04 CR-17 `[OPEN]` (overhead metric) recorded. codebase-memory MCP index created for the repo |
| 2026-09-26 | Plan 01 T6/T4 | Wiring render + calibration row on the live stack (1 chat + 5 image calls via the relay). QA → code/perf/security reviews → fixes; 01 CR-18…CR-21. Quiet re-run and relay cost check → owner |
| 2026-09-26 | Plan 02 M0 | 02.1.4-T1, 02.1.1-T0, 02.1.2-T1, 02.1.5-T1, 02.1.3-T0 done; presets 02.1.1-T1..T3 written, gate T34 fails on en `standard` length (owner decision). Each task: QA → code/perf/security reviews → fixes → re-test; README T65–T97, CR-25…CR-27. Live QA spend ≈ $0.084. 02.5.2-T0 blocked (no .NET SDK) |
| 2026-09-26 | Plan 02 M0 merge | PR #11 (`d746696`) and PR #12 (`f84c415`) merged into `develop`; the five plan-02 commits and the T73 high-level edit landed. Branch deleted locally. README T98 + CR-28 added; STATUS refreshed. Preset gate T34 stays open on the D4 length decision (next action 7) |
| 2026-09-26 | M0 wave 1 (`feature/m0-wave-1`) | 8 slices implemented in parallel and each put through independent QA → code/perf/security reviews → fixes → re-test, then a final integration review (verdict *mergeable-with-fixes*; its 4 findings fixed, incl. a real narration leak in committed spike samples). Delivered: 03.4.1-T1 `images.md`, 03.1.1-T1 templates, 05.2.2-T1 fonts, 05.3.1-T1/05.3.2-T1 music tooling, 02.1.3-T1/T2 offline tooling + 02 T99, 04.2.2-T1/04.2.3-T1 tool leg, 02.5.2-T0 Python half, 01.3.3-T1 `CARRY-OVER.md`, 01 CR-23. **Preset gate T34 PASS** (18/18 cells, 0 blocking, $0.0255). New plan README rows: 02 T99/T100/T101, 04 T16/T17, 05 T53/T54 |
