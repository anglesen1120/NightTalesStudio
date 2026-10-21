# 05.4.1 — Worker contract spike and render-node.v1 (hard gate for 09.6.2-T1, 11.1.2-T1, 11.3.5-T1)

> Refines: `../../../05-video-assembly-subtitles.md` §4 epic 05.4 items 1–9, §5.3, F1–F3, and the plan 03 hand-over (§10)
> Contract: `../README.md` — C1–C14, C19–C22, D1, D2 (spike profile), D3, D4, D5, D12, D13, D14, F06–F12, F18, P3, P4, P7
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 05.4.1 *Confirm MPT API and define the mpt-adapter contract* — "As the Tech Lead I want the MPT task API and its failure modes proven at the pin, and the node contract written down, so that three independent consumers (backend client, mock, adapter) can be built against one frozen document." Must · L (detailed 2.0 d).

**In scope.**
- The spike on the implementation Mac at `MPT_COMMIT` with the `spike` profile (D2) and two loopback stubs: `trial/tools/image_stub.py` (03.4.1-T1, `127.0.0.1:18003`) and the new `trial/tools/tts_stub.py` (`127.0.0.1:18004`, D12). Scenarios S0–S16, no OpenAI spend.
- The executable reference client `trial/tools/mpt_client.py` (D13): D3 body builder, D4 state/stage/outcome mapping, `render-status.v1`-shaped output, download, optional `render_check.py run --normalize`, 1 s progress sampler.
- The observation note `worker-node/contract/mpt-api.md` + samples `worker-node/contract/samples/mpt/S<n>-<slug>.{json,txt}` (D12).
- The frozen contract `worker-node/contract/render-node.v1.md` (D4 + D5 + D9/D10 relations), `worker-node/contract/README.md` (index + versioning), the 24 D14 golden fixtures, and `worker-node/contract/tools/check_fixtures.py`.
- The hand-over to 09.6.2-T1, 11.1.2-T1, 11.3.5-T1, 03.4.3-T1; the update requests CR-1…CR-4 (+ CR-5, CR-7) and the 09.6 re-estimate request (P6).

**Out of scope.**
- The adapter code (11.3.5-T1), the image relay (03.4.3-T1), mock-mpt (11.1.2-T1/T2), `render_check.py` (05.5.1-T1), the `node` config profile (05.1.1-T2), the backend DTOs (05.1.2-T1). Plan 05 writes their contract, not their code (D1).
- Any MPT fork, any LLM call from the node (C19), any real OpenAI image or TTS spend (03 D14 rule; S16 is local VieNeu only), any node provisioning.

## 2. Confirmed facts

README §1 facts are snapshot evidence (pin not proven, `README.md` §1) ✅; S0 re-checks each at `MPT_COMMIT` on the implementation machine (01 DEC-2).

| # | Fact (README §1) | Rests on | Spike re-check |
|---|---|---|---|
| C1 | Route set under `/api/v1` + static mounts + `/docs` on | D4 routes | S1 inventory, S2 auth |
| C2 | `x-api-key` exactly once, constant-time; else 401 `invalid API key` | D4 auth | S2 matrix |
| C3 | `POST /videos` → 200 `{status,message,data.task_id}`; `x-task-id` → `request_id` | D4 submit | S3 |
| C4 | Task creation logs the whole `VideoParams` (script + prompts) at SUCCESS | F18, D12 redaction | S15 |
| C5 | Validation error → 400 `{"status":400,"data":[…],"message":"field required"}`; unknown fields ignored | D3, F10 | S4 |
| C6 | `GET /tasks/{id}`: `state`, `progress`, `failed_stage`, `error`, URIs, absolute node paths | D4 status | S3, S5, S12 |
| C7 | Progress 5/10/20/30/40/50/75/100; `video_script`+`video_terms` skip the LLM | D4 stage, F09 | S3 trace |
| C8 | `_mark_task_failed` keeps the first error; stages `preflight…pipeline` | D5 outcomes | S5, S10 |
| C9 | Task state memory-only; restart → 404, Redis → stuck `state 4` | F06, D4 lost rule | S12 |
| C10 | Queue invisible (`state 4`, `progress 0`); `max_queued_tasks = 0` rejects all busy submits | F07, D2 | S11 |
| C11 | `DELETE` running → 409; else rmtree + 200; no cancel exists | D4 cancel, F08 | S13 |
| C12 | `/download` `FileResponse` `video/<ext>`; `/stream` 206 + `Content-Range`; scoped to `storage/tasks` | D4 outputs, F11 | S3 |
| C13 | Task dir `audio.mp3`, `subtitle.srt`, `combined-1.mp4`, `final-1.mp4`, `script.json`, `openai-image-*.png`; never auto-deleted | D4 cleanup | S3, S12 |
| C14 | `16:9`→1920×1080, `9:16`→1080×1920; 30 fps; AAC 192 kb/s; `video_codec` fallback libx264 | D4 output | S14 |
| C19 | Non-empty `video_script`/`video_terms` ⇒ no LLM key needed | D2, D3 | S3 |
| C20 | `config.example.toml` keys (ports, `max_concurrent_tasks`, `max_queued_tasks`, `[whisper]`) | D2 | S0 |
| C21 | Lock: fastapi 0.136.3, starlette 1.2.1, uvicorn 0.32.1, faster-whisper 1.1.0, ctranslate2 4.7.1, moviepy 2.2.1 | D2 | S0 |
| C22 | `VideoParams` defaults/ranges (`video_clip_duration ≥ 1`, `subtitle_display_mode`, `font_size = 60`, …) | D3 | S1 schema, S4 |
| X5 | Truncated MP4 (moov at the end) → `ffprobe` "moov atom not found", exit 1 | D9 `container_invalid` | S14 ffprobe row |
| P3 | 03 D4/D6/D12: image fields, adapter-only `image_plan`, `images.v1` in terminal status, cleanup | D4 `images` | S9 (reference) |
| P4 | 04 D6/D8/D10: `failed_stage = audio` → `tts_failed`; `tts_proxy` = `proxy-health.v1` | D5, D4 health | S5, S16 |
| P7 | 11: mock-mpt modes `tts_failed, image_moderated, timeout, lost_task`; 400 `mock_contract_violation`; FR-11-4 | D5, CR-4 | S5, S11 |
| D2 | `spike` profile adds `[kokoro] base_url = 127.0.0.1:18004/v1`; MPT itself binds `127.0.0.1` | spike setup | S0, S3 |
| D3 | Submit body = exactly the `mpt` key set minus `n_threads` | `render-submit.v1` | S3, S4 |
| D4 | `state`/`stage`/`percent`/outcome/lost/capacity/cancel/health mapping | the contract | S3, S5, S11, S12, S13 |
| D12 | Spike method, stubs, modes, sample naming, observation note | T1 method | all |
| D14 | Fixture list and their consumers | T2 | T2 |

## 3. Domain and data

Node-side only. No database, no migration, no domain type in this folder (D1: the C# types live in 05.1.2-T1/05.5.1-T2).

**Run spec** (`trial/fixtures/spike-render-spec.json`, tracked) — the input of `mpt_client.py`; D13:

```json
{ "run_id": "spike-05-s3-landscape", "story_file": "trial/fixtures/spike-story-en.txt",
  "terms_file": "trial/fixtures/spike-terms.json", "aspect": "16:9", "language": "en",
  "voice_id": "Thiện Minh", "preset_file": "seed/subtitle-presets.v1.json", "music_track": null }
```

**D3 body built from a spec** (the `mpt` object of `render-submit.v1`): `video_subject` = the first line of `story_file` (≤ 200 chars); `video_script` = the file body, NFC; `video_terms` = the JSON array of `terms_file`; `video_aspect` = `16:9`|`9:16` from `aspect`; `video_language` = `language`; `voice_name` = `"kokoro:" + voice_id`; subtitle fields from `preset_file` when it exists, else the D6 defaults inline; `bgm_type/bgm_file/bgm_volume` from `music_track` (`"custom"`/file/volume) or `""/""/0`; `n_threads` never sent.

**State and stage mapping (D4), implemented once in `mpt_client.py` and reused by the adapter:**

| MPT | adapter `state` | adapter `stage` |
|---|---|---|
| `state 4`, `progress 0` | `queued` | `queued` |
| `state 4`, `progress 5–29` | `running` | `voice` |
| `state 4`, `progress 30–39` | `running` | `subtitles` |
| `state 4`, `progress 40–49` | `running` | `images` |
| `state 4`, `progress 50–99` | `running` | `assembling` |
| `state 1` | `succeeded` | `done` (after D9 post-processing: `checking`) |
| `state -1` | `failed` | last stage reached |
| `state 4` + 404 on `GET /tasks/{id}` after a submit | `failed` | — (outcome `render_lost`) |

Outcome (D5): `failed_stage = audio` → `tts_failed`; `materials` → `image_generation_failed` (Worker-owned, 03 D7); `video` → `render_assembly_failed`; `preflight|script|terms|pipeline` → `render_node_error`.

**Artefact layout (D12, D14).**

```
worker-node/contract/
  mpt-api.md                  observation note: one section per scenario, each claim cites its sample
  samples/mpt/S<n>-<slug>.json|.txt   S0–S16 task JSON + redacted log excerpts
  mpt-routes.pinned.json      S1 snapshot (owned by 05.1.1-T1 after T1)
  mpt-videoparams.pinned.json S1 snapshot (owned by 05.1.1-T1 after T1)
  render-node.v1.md           the frozen contract (T2)
  README.md                   index, versioning rule, changelog, fixture table
  fixtures/*.json             24 D14 golden fixtures (T2)
  tools/check_fixtures.py     stdlib checker: key sets, enums, forbidden content, route coverage
trial/tools/{tts_stub.py,mpt_client.py,image_stub.py,redact_prompts.py}
trial/fixtures/{spike-render-spec.json,spike-story-en.txt,spike-story-vi.txt,spike-terms.json,tts-stub-script.json}
$NTS_HOME/runs/spike-05/fixtures/*.mp3     speech fixtures (never committed, D12)
$NTS_HOME/runs/<run_id>/{final-1.mp4,subtitle.srt,audio.mp3,script.json,submit-body.json,manifest.json,trace.jsonl,render-check.json}
```

## 4. Use cases

| # | Use case | Scenario(s) | Precondition | Steps | Expected | Artefact |
|---|---|---|---|---|---|---|
| UC1 | Confirm the pin and the API surface | S0, S1, S2 | MPT runs (01.2.2-T5), whisper weights (01.2.3-T1) | tick C1–C22; dump `/openapi.json` and `VideoParams.model_json_schema()`; auth matrix | all C same at the pin; 401 on the three bad-key cases | `mpt-api.md` S0–S2, two pinned JSONs |
| UC2 | Prove the happy path and the stage mapping | S3 | image + TTS stubs, story/terms fixtures | submit → 1 s trace → download → Range/stream | progress 5→100, stages `voice→subtitles→images→assembling`, `final-1.mp4` + `subtitle.srt` | sample status JSON, trace, dir listing |
| UC3 | Prove the failure taxonomy | S4, S5, S6, S10 | as above | 400 validation; TTS `e500`/`empty`/`abort`; assembly failure | 400 body shape; `failed_stage = audio`; `pipeline`/`video` on assembly | samples + stub call logs |
| UC4 | Prove the subtitle traps | S7, S8, S16 | whisper weights; S16 needs 04.1.2-T1 | offline whisper, no-speech audio, real vi chain | task succeeds with all-zero cues (F02); SRT recorded | SRT samples |
| UC5 | Prove state, queue, restart, delete | S11, S12, S13 | `max_concurrent_tasks = 1` variants | two submits × two configs; stop/start mid-task; delete matrix | 429 vs queued `state 4`; 404 + orphan dir + ffmpeg leftovers; 409/200/404 | samples, `pgrep` output |
| UC6 | Prove output variants and log content | S14, S15 | two music files in `storage/bgm` | portrait, subtitles off, three `bgm_type`; `ffprobe`; read MPT log | 1080×1920, 30 fps, AAC 192 kb/s; script/prompts logged | `ffprobe` JSON, redacted log |
| UC7 | Freeze the contract | T2 | T1 landed | write `render-node.v1.md` + README + 24 fixtures + checker | `check_fixtures.py` exit 0; every route/code/field covered | contract files, hand-over |

## 5. API and events

**MPT routes consumed by the spike** (subset of C1 ✅): `POST /api/v1/videos`, `GET /api/v1/tasks/{task_id}`, `GET /api/v1/tasks` (paged), `GET /api/v1/download/{file_path:path}`, `GET /api/v1/stream/{file_path:path}`, `GET /ping` (unauthenticated), `GET /openapi.json`, `GET /docs`. The spike does **not** use `/musics`, `/video_materials` or the LLM routes.

**Contract surface frozen by T2** (D4, served by 11.3.5-T1, mocked by 11.1.2-T1, called by 09.6.2-T1): `POST /v1/renders`, `GET /v1/renders/{task_id}`, `GET /v1/renders/{task_id}/video`, `GET /v1/renders/{task_id}/subtitles`, `POST /v1/renders/{task_id}/cancel`, `DELETE /v1/renders/{task_id}`, `GET /v1/health` — plus the error envelope `{"error":{"code","message","errors"}}` and the codes `adapter_unauthorized`, `adapter_route_not_found`, `adapter_contract_violation`, `adapter_idempotency_conflict`, `adapter_capacity_full`, `adapter_task_unknown`, `adapter_output_not_ready`, `adapter_output_absent`, `adapter_task_busy`, `adapter_node_unhealthy`, `adapter_body_too_large`, `adapter_auth_throttled` (D15 S2); transport HTTPS with a pinned certificate (D15 S1).

**Events.** None. No public API route changes in this story; the Worker's read model (09.5.3) and the domain events (`RenderSucceeded`, 02 CR-23) are untouched.

**Mapping note.** `mpt_client.py` prints the same `render-status.v1` shape the adapter serves (D13), so the M0 samples are directly replayable as D14 fixtures. While `render_check.py` is absent (05.5.1-T1), `check` is `null` and `output` is filled from the download manifest (video/subtitles bytes+sha256; `narration_seconds` is `null` until that tool lands); once the checker runs (`run` always passes `--normalize`), `output.video` is the normalized `final-1.norm.mp4` with its hash (T60).

## 6. UI

None — worker-side tooling and contract files. No screen, no copy key, no i18n entry is added by this story (the review UI copy for `render_quality_failed` belongs to 10.6.2 via CR-9).

## 7. Jobs and integrations

| Path | Interval / timeout | Failure mapping |
|---|---|---|
| `mpt_client.py run` → MPT submit | POST timeout 30 s | non-200 → print the 400/429 body, exit 1 |
| `mpt_client.py poll` → `GET /tasks/{id}` | 5 s (`run`), 1 s (`--trace` sampler); `--timeout` 3600 s, timeout = resume, never re-submit (D15 P7) | 404 after a submit → `render_lost`, exit 1 |
| `mpt_client.py download` → `/api/v1/download/…` | 300 s read | missing `subtitle.srt` is not an error when subtitles are off |
| MPT → TTS stub (`[kokoro] base_url`) | stub `slow` = first byte at 130 s | `e500`/`empty`/`abort` → `failed_stage = audio` |
| MPT → image stub | stub `slow` = 320 s > MPT's 300 s read timeout | `moderated`/`e500`/all-fail → `materials` (03 P4) |
| `mpt_client.py run` → `render_check.py` | optional; skipped when the tool is absent (`check: null`; `output` filled from the download manifest — video/subtitles bytes+sha256, `narration_seconds: null` until 05.5.1-T1) | exit 1 → the written report is still embedded as `check` (D9: exit 1 = input unreadable, report written), the run exits 1, the spike continues |
| T1 → T2 | none (file hand-over) | a D3/D4/D5 change → README §10 ruling first |
| T2 → 09.6.2-T1 / 11.1.2-T1 / 11.3.5-T1 | none (hard gate) | consumers start only when `check_fixtures.py` is exit 0 |

Consumers of the tools (D1): `05.2.1-T1` (fidelity/sync evaluation reuses `mpt_client.py` + `tts_stub.py`), `05.2.3-T1` (preview frames through the same client), `05.5.2-T1` (M0 trial renders). `05.1.1-T1` consumes the S1 snapshots for the upgrade diff.

## 8. Security and privacy

| Threat | Control |
|---|---|
| MPT key leak via argv, logs, samples or the repo | key lives only in Keychain `nts.mpt.api_key`; read via the 01 `secret` helper and passed to `requests`/`curl` through the environment or `-H @<(...)`; never printed, never in a sample; `grep -F "$(secret nts.mpt.api_key)"` over `worker-node/contract/` exits 1 |
| Stub reachable from the LAN | both stubs bind `127.0.0.1` only (`18003`, `18004`), single route, 404 otherwise; MPT binds `127.0.0.1:8080` in the spike profile too (D2/F12) |
| Story, script and prompts committed in samples/fixtures | `redact_prompts.py` (03.4.1-T1, extended here with the `video_script`/`script.json` rule) replaces them with `<text sha256=…>`; fixtures carry a synthetic placeholder, never the spike text; `check_fixtures.py` rejects forbidden content keys |
| Node paths / MPT URIs leaking into the contract | D4 forbids them in `render-status.v1`; fixtures are checked for `/Users/`, `/storage/`, `audio_file`, `subtitle_path`, `materials` |
| Cost | no OpenAI spend: image stub + TTS stub only (03 D14); S16 is local VieNeu; the MPT config has no LLM key (C19) |
| Redaction tool drift | one redactor only (`trial/tools/redact_prompts.py`); no second copy is created here |

## 9. Test plan

- **Spike (manual, scripted):** S0–S16 in `05.4.1-T1`, with the stub script files controlling the failure modes; every scenario saves a sample and gets a section in `mpt-api.md`.
- **Tool self-tests (stdlib):** `tts_stub.py --self-test` (six modes, MPEG sync word, abort byte count, `slow` with a shortened delay), `mpt_client.py --self-test` (D3 body from a spec; every row of the state/stage/outcome table; no network), `redact_prompts.py --self-test` (extended rule).
- **Contract fixtures:** `check_fixtures.py` — key sets equal D4/D9/D10 exactly, enum membership, forbidden content, `--coverage` route/code/field coverage, `--self-test` (a mutated fixture must fail).
- **Consumer replay (D14):** 09.6.2-T1 deserializes every fixture with `JsonUnmappedMemberHandling.Disallow` and re-serializes; 11.1.2-T1 serves them; 11.3.5-T1 replays them as contract tests. Those tests live in the consumer tasks, not here.
- **No permanent regression suite in this folder:** the artefacts (samples, fixtures, checker) are the evidence; the tools stay for M0 (05.2.1-T1, 05.5.2-T1).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 05.4.1-T1 | Spike: MPT API, failures, restart, queue, outputs, stubs, mpt_client.py | Worker | L (2.0) | 01.2.2-T5, 01.2.3-T1, 03.4.1-T1; 04.1.2-T1 soft (S16) | 05.4.1-T2, 05.1.1-T1, 05.2.3-T1, 05.5.1-T1, 05.2.1-T1, 05.5.2-T1 |
| 05.4.1-T2 | render-node.v1 contract, fixtures, hand-over to 09/11 | TL | S (0.5) | 05.4.1-T1 | 09.6.2-T1, 11.1.2-T1, 11.3.5-T1, 05.1.1-T2, 05.1.2-T1, 05.5.1-T2 (the former 12.8.3-T3 (12 CR-19, soft) blocker was dropped by DEC 10.14) |

Total **2.5 d** (high-level 2.25 d; README §9). The +0.25 d is T2 growing XS → S: the contract file, `contract/README.md`, 24 fixtures and the hand-over for three consumers are a full deliverable, not a note (README §9 row "05.4 worker contract").

### Open items for the TL

- [RULED — README §10 T1] C23 does not exist; `/ping` is part of C1. Contract line corrected to C19–C22.
- [RULED — README §10 T33] D4 marks the adapter port **8090** ⚠️ ("final in 11.3.5") and the adapter's durable store ("a file or SQLite under its data dir ⚠️ 11.3.5"). `render-node.v1.md` ships both as *proposed*; a change is additive (base URL / storage only) and needs no re-plan, but the 11.3.5 owner should rule before the fixture freeze is treated as final.
- [RULED — README §10 T31] `render_check.py` (05.5.1-T1) is **blocked by** 05.4.1-T1 yet **used by** its `run` step (D13). The spike therefore runs with `check: null` and `output` filled from the download manifest (video/subtitles bytes+sha256; `narration_seconds: null` until 05.5.1-T1 lands) (D9). If the TL wants a real `check` block in the S3 sample, 05.5.1-T1 must move ahead of T1 — that inverts the Blocks edge in README §5/§7.
- [RULED — README §10 T32] D12's speech fixtures use macOS `say`; a **vi** voice is ⚠️. If absent, all SRT/timing evidence from S3/S7/S8 is English-only and the Vietnamese timing evidence depends on S16 (04.1.2-T1, soft dependency).
- [NOTED — README README §7 gate] F16 is **not** closed here: `initial_prompt` stays unset (D2) and S16 only records the detected-language log line; the effect on sync is measured in 05.2.1-T1. Worth stating explicitly in the 05-M0 gate so nobody expects the spike to settle it.
- [NOTED — README §4] CR-5 and CR-7 edit other plans' task headers (01.2.2-T2 Notes; 03.4.1-T1 `Depends`/`Blocks`). They are applied only after the owner answers O1–O7 (README §4/§10); until then the `spike`-profile `[kokoro]` change is agreed verbally with the 03.4.1-T1 owner and tagged in both files.
- [RULED — README §10 T37] If the owner rejects O1 (the +2.0 d re-estimate), T2 is the item to cut: XS (a note) instead of S (contract + fixtures) would leave 09.6.2-T1/11.1.2-T1/11.3.5-T1 without a frozen artefact — say so explicitly rather than dropping the hard gate.

## 11. Rollout

- **Order:** this folder starts **first in M0**, right after 01.2.2-T5 (MPT smoke) and 03.4.1-T1 (image stub + `images.md`): `05.4.1-T1` → `05.4.1-T2` → then 05.1.1-T1 and 05.5.1-T1 (both blocked by T1). S16 is deferred until 04.1.2-T1 exists and is not a gate item.
- **Flag / profile:** nothing changes on the node. On the Mac, the `spike` profile only: `render-mpt-config.py --profile spike` renders `config.spike.toml` (image stub `18003`, TTS stub `18004`, `[whisper]` unchanged), `start-mpt-api.sh --profile spike` swaps it in, and the normal profile is restored on stop (the 03.4.1-T1 guard refuses the normal profile while `openai_image_base_url` points at `18003`).
- **Gate text (05-M0, README §7):** "`worker-node/contract/mpt-api.md` + samples S0–S16 recorded at the pin; `render-node.v1.md` + the 24 D14 fixtures committed with `check_fixtures.py` exit 0 (05.4.1-T1/T2); the 09.6 re-estimate is requested from the 09 wave-2 detailing (P6)." This is the **hard gate** for 09.6.2-T1, 11.1.2-T1 and 11.3.5-T1; 05.1.2-T1, 05.5.1-T2 and 05.1.1-T2 follow the same edge.
- **Rollback:** stop the two stubs, `start-mpt-api.sh --profile normal`, `smoke.sh` green again (proves the config swap was undone); the spike artefacts are additive files and need no rollback.
- **Monitoring:** none (no service). The spike's exit criteria are file-based (`mpt-api.md`, samples, `check_fixtures.py` exit 0).
