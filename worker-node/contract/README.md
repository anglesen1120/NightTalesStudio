# `worker-node/contract/` — render-node contract files

Frozen by 05.4.1-T2, 2026-09-27. Contract sources: `plans/features/05-video-assembly-subtitles/README.md`
D3/D4/D5/D9/D10/D14/D15 and its §10 rulings; spike evidence `mpt-api.md` + `samples/` measured at
`MPT_COMMIT` `3d5f4e421927d61f3eac729cf4b711ac0b69d688` (`worker-node/pins.env`).

Consumers: **11.3.5-T1** implements (`mpt-adapter`), **11.1.2-T1** mocks (mock-mpt),
**09.6.2-T1** calls (`MptRenderClient`), **12.8.3-T3** probes (`POST /v1/probe-image`).

## Index

| File | Owner |
|---|---|
| `render-node.v1.md` | 05.4.1-T2 — the frozen route/error/status/health/outcome contract (D4, D5, D15) |
| `mpt-api.md` | 05.4.1-T1 — MPT API observations at the pin (S0–S16), the evidence surface |
| `images.md` | plan 03 (03.4.1-T1) — the image-relay contract `images.v1`, referenced by `images` |
| `mpt-routes.pinned.json` | 05.4.1-T1 — pinned route inventory snapshot (`mpt-routes.v1`) |
| `mpt-videoparams.pinned.json` | 05.4.1-T1 — pinned `VideoParams` schema snapshot (the D3 authority, T20) |
| `samples/mpt/` | 05.4.1-T1 — redacted task-API samples + log slices (S0–S16) |
| `samples/images/` | 03.4.1-T1 — the P4 `failed_stage = materials` evidence |
| `fixtures/` | 05.4.1-T2 — the 24 D14 golden fixtures (table below) |
| `tools/check_fixtures.py` | 05.4.1-T2 — stdlib fixture checker + `--coverage` + `--scan` (D15 S5) |
| `../../seed/i18n-style-labels.v1.json` | plan 03 — repo-root file (referenced, not in this folder); allow-listed by the checker, not part of D14 |
| `../../seed/music-tracks.v1.json` | 05.3 (D8) — repo-root file (referenced, not in this folder); allow-listed by the checker, not part of D14 |

## Versioning

A **breaking** change — a route removed, a field removed or retyped, a code renamed — ships as
`render-node.v2`; **both** versions are served during the migration (11.3.5-T1) with a deprecation
note on v1. **Additive** changes stay inside v1 and get a changelog row. Any D3/D4/D5 change found
by a spike becomes a README §10 ruling **first**, then the fixtures; `render-node.v1.md` cites the
ruling id on each changed line.

**Doc format the checker depends on** (`--coverage`): `render-node.v1.md` must keep each route as a table row opening with `` `METHOD /path` `` in the first cell, and every contract data token (field names, schema names, outcome codes) in backticks — that is all the parser keys on; it exits 2 rather than silently passing if a doc edit drops the route-row form.

## Changelog

| Date | Change | README §10 ruling | Fixtures touched |
|---|---|---|---|
| 2026-09-26 | Whisper failure is fail-open with zero-cue output — detect via `subtitle.srt` `zero_duration`, never `failed_stage` | T55 | `render-check.v1.zero-cues` |
| 2026-09-26 | Uncaught MPT exception → `failed_stage = pipeline`; map on `failed_stage` alone; `pipeline` → `render_node_error`, `video` → `render_assembly_failed` | T56 | `render-status.v1.failed-assembly` |
| 2026-09-26 | Every MPT call carries `x-task-id` or a 404 echoes a random `request_id` | T57 | none |
| 2026-09-26 | `custom_audio_file`/`video_materials` fixed `null` — a host path is unreachable over the API | T58 | `render-submit.v1.*` |
| 2026-09-26 | Stub dispatch keys (`by_input_contains`/`by_prompt_contains`) recorded | T59 | none |
| 2026-09-26 | `output.video` = the normalized file's hash when `render_check.py --normalize` ran | T60 | `render-status.v1.succeeded`, `render-check.v1.ok` |
| 2026-09-27 | Contract frozen; 8th contract-only route `POST /v1/probe-image` added (12 CR-19); 09.6 re-estimate requested | T61 | all 24 |
| 2026-09-27 | `percent = 100` allowed at `checking` (MPT complete); `capacity.accepting = false` whenever health is not `ok` | T62 | `render-status.v1.checking`, `render-node-health.v1.{proxy-down,disk-low}` |
| 2026-09-27 | Lost-task status shape fixed: `detail = "mpt_task_missing"`, `mpt.state = null` | T63 | `render-status.v1.lost` |
| 2026-09-27 | Cancelled status shape fixed: `stage = done`, `percent = 100`, `outcome = null` | T64 | `render-status.v1.cancelled` |
| 2026-09-27 | `render-check.v1` null-serialization (`audio_silent`/`container_invalid`); `render-quality.v1.images = null` | T65 | `render-check.v1.{silent,truncated}`, `render-quality.v1.*` |
| 2026-09-27 | `--scan` found a host ffmpeg path leaked into 11 T1 log slices; fix deferred to a T1-touching change (report, don't touch) — **resolved 2026-09-27: redactor fix landed in `ce320fe` (PR #15), `--scan` green after merge** | T66 | none (samples, not fixtures) |
| 2026-09-27 | Probe semantics: one cheapest image through the node's own relay (03 D5/D6); closed `reason` enum; single-flight; 60 s/300 s cache; 10 s upstream timeout; `adapter_probe_throttled` (429 + `Retry-After`); excluded from `capacity` accounting | T67 | none — the probe route is contract-only (request `{}`), no fixture exists |
| 2026-09-27 | Key rules: startup exit on empty/< 32-byte `ADAPTER_API_KEY`; empty/unset NEXT disables rotation and must differ from the other keys; empty `x-api-key` → 401 before any compare; auth before routing; TCP-peer source address (`X-Forwarded-For` ignored); a valid key from a throttled address is still served (`Retry-After: 60`, sliding 60 s) | T68 | none |
| 2026-09-27 | Closed free-text fields: `errors[].field` echoes a key name only if `^[a-z_]{1,64}$` else `<redacted>`; `outcome.detail` = MPT `failed_stage` set ∪ `{mpt_task_missing, mpt_restarted}` ∪ D9 check names; `mpt.warnings` mapped to a closed code set or dropped | T69 | none |
| 2026-09-27 | Freeze corrections: `done` also with `state = cancelled`; `images` `null` while `state = running`; `check.subtitles` lists all 13 D9 keys; lost `detail` split `mpt_task_missing`/`mpt_restarted` | T70 | none (doc-only corrections; the fixtures already carried these shapes) |
| 2026-09-27 | Loud fixture arithmetic: `after.true_peak_dbtp` -0.4 → **-1.6** (≤ the `loudnorm` TP target, below the pre-normalization -0.9), `normalization_type` `linear` → `dynamic` + the D9 warning `loudnorm_dynamic`; integrated -11.2 kept — now consistent with `render-quality.v1.fail-loudness.json` | — (fixture fix, CRV-0542-2) | `render-check.v1.loud` |
| 2026-09-27 | `--scan` hardened: content values must be `<…sha256=…>` markers (leaf-key aware), trimmed key allow-list + value rule, secret-shaped values flag, quoted/traceback/`~`/`$HOME` paths flag, whole-string marker rule, dotfiles scanned, symlinks/non-regular/> 8 MiB fail; usage errors exit 2; `--coverage` fails on a missing mapped fixture; filename→scenario coherence; DIR mode pins `video_subject`/`video_terms` shapes | T71 | none (checker-only) |
| 2026-09-27 | Refreshed values to the pinned D9 formulas (05.5.1-T1): zero-cues `cps_p95` 1200.0 / `sync_suspect` 4; silent `silence.longest_s = total_s = 302.1`. Key sets unchanged; the one exception to the T61 freeze | T77, T78 | `render-check.v1.{zero-cues,silent}` |

## Fixture table (D14 — 24 files)

Submit fixtures carry `mpt.video_script = "NTS_FIXTURE_SCRIPT"` (never the spike text); hashes are
fake but self-consistent.

| File | What it proves | Derived from |
|---|---|---|
| `render-submit.v1.landscape-vi.json` | submit envelope + `image_plan` + the full D3 key set, landscape preset | D3 verbatim + D6 (bottom/70.0/84) + `kokoro:Hải Đăng` (seed/voices.v1.json rev 0) + D8 music example (`dark-ambient-01`, 0.146 — seed catalog empty, marked); `prompt_sha256` = real sha256 of the synthetic terms |
| `render-submit.v1.portrait-en.json` | same, portrait preset + en voice | D3 + D6 (custom/62.0/64) + `kokoro:Ngọc Huyền` |
| `render-submit.v1.no-music.json` | the no-music branch (`bgm_type=""`, `bgm_file=""`, `bgm_volume=0`) | D3/D8 |
| `render-status.v1.queued.json` | `state=queued`, `stage=queued`, `percent=0` | S3 trace 0 tick + D4 |
| `render-status.v1.running-voice.json` | `stage=voice`, `percent=20` | S3 trace + D4 |
| `render-status.v1.running-images.json` | `stage=images`, `percent=40` | S3 trace + D4 |
| `render-status.v1.checking.json` | MPT complete, D9 running: `state=running`, `percent=100`, `mpt.state=1` | S3 trace + D4, T62 |
| `render-status.v1.succeeded.json` | terminal `output`/`check`/`images` shapes | S3 + D4/D9/03 D6, T60 |
| `render-status.v1.failed-tts.json` | `failed_stage=audio` → `tts_failed` | S5 + D5 (04 D6) |
| `render-status.v1.failed-materials.json` | `failed_stage=materials` → `image_generation_failed` | S9 (03 P4) + D5 |
| `render-status.v1.failed-assembly.json` | `video` → `render_assembly_failed`; `pipeline` → `render_node_error` (T56) | S10 + D5/T56 |
| `render-status.v1.lost.json` | `render_lost`, `detail="mpt_task_missing"`, `mpt.state=null` | S12 + T63 |
| `render-status.v1.cancelled.json` | `cancelled`/`done`/100, `outcome=null`, `images` carried | D4 + T64 |
| `render-node-health.v1.ok.json` | healthy health incl. `gpu` block | D4 + 11 CR-4 + 04 D8 |
| `render-node-health.v1.proxy-down.json` | `status=error`, `reasons:[tts_proxy_not_ok]`, `accepting=false` | D4 + 04 D8, T62 (derived) |
| `render-node-health.v1.disk-low.json` | `reasons:[disk_low]`, `accepting=false` | D4 (used ≥ 85 %), derived |
| `render-check.v1.ok.json` | the full measured-report shape | D9 example verbatim |
| `render-check.v1.loud.json` | out-of-band loudness record (illustrates the schema; its integrated -11.2 is outside the D10 band on purpose, not a passing sample) | D9 steps |
| `render-check.v1.silent.json` | `errors:["audio_silent"]` + loudness `null` (never `-Infinity`) | D9 X4 + T65 |
| `render-check.v1.no-subtitles.json` | `subtitles.present=false`, `cues=0` | D9 |
| `render-check.v1.zero-cues.json` | whisper-fail-open detection input (`zero_duration`, `coverage_share=0`) | D9 + S7/S8, T55 |
| `render-check.v1.truncated.json` | `errors:["container_invalid"]`, unmeasurable blocks `null` | D9 X5 + T65 |
| `render-quality.v1.pass.json` | the Worker report shape incl. `checks`/`hints`; `images=null` | D10 example, T65 |
| `render-quality.v1.fail-loudness.json` | `loudness` check `pass=false` → `render_quality_failed` | D10 |
