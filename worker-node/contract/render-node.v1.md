# `render-node.v1` — the render-node contract (frozen)

Frozen by 05.4.1-T2 on 2026-09-27. Sources: `plans/features/05-video-assembly-subtitles/README.md`
**D4** (routes, status, health — including the 11 CR-4 additions), **D5** (outcome codes), **D9**
(`render-check.v1`), **D10** (`render-quality.v1`), **D14** (fixtures), **D15** (security/performance
hardening, T38–T52) and the 05.4.1-T1 spike evidence `worker-node/contract/mpt-api.md` with the
redacted samples `samples/mpt/` measured at `MPT_COMMIT` `3d5f4e421927d61f3eac729cf4b711ac0b69d688`
(`worker-node/pins.env`). Owner decisions O1–O7 accepted 2026-09-24.

Language-neutral on purpose (05.4.1-T2 Notes): no .NET, Python or other language type name appears
here; the same file must be implementable by the adapter, the mock and the backend client without
translation. There is no JSON-Schema companion (D14); the golden fixtures under `fixtures/` are the
executable contract artefacts.

Legend: ✅ read from a source or measured at the pin · ⚠️ unverified / decided elsewhere ·
[OPEN] still open. Lines that a spike finding changed cite their README §10 ruling id.

---

## 1. Scope, transport, authentication, error envelope

- **Owners.** Served by `mpt-adapter` (11.3.5-T1), mocked by mock-mpt (11.1.2-T1), called by
  `MptRenderClient` (09.6.2-T1). This task (05.4.1-T2) is their hard gate.
- **Transport.** `https://<node private address>:8090` — HTTPS with a node-local certificate; the
  Worker pins the certificate's SHA-256 fingerprint per node (`worker_nodes.tls_cert_sha256`) and
  refuses any other certificate (D15 S1). Port 8090 is final (11 CR-4, §10 T33). MPT itself stays on
  the node's loopback (11 FR-11-4). Bodies are JSON UTF-8; every timestamp is ISO-8601 UTC with `Z`.
- **Authentication.** Exactly one `x-api-key` header on **every** route, compared in constant time;
  absent, wrong or duplicated → 401 `adapter_unauthorized` (measured: `samples/mpt/S2-auth-matrix.json`,
  `S1-missing-key.json`, `S2-wrong-key.json`). An **empty `x-api-key` header value is 401 before any
  compare** (never matched against an empty key). Auth is checked **before** the body is read and
  **before routing**: an unknown path with a bad or absent key answers 401, never 404, so routes
  cannot be enumerated unauthenticated (§10 T68). Auth failures are counted per **TCP peer** source
  address (`X-Forwarded-For` is ignored); > 10/min → 429 `adapter_auth_throttled` over a **sliding
  60 s** window (D15 S2). A **valid key from a throttled address is still served** — the throttle
  rejects only further auth failures, and the 429 carries `Retry-After: 60` (§10 T68).
- **Keys.** The adapter key differs from `NTS_MPT_API_KEY`, `NTS_PROXY_API_KEY` and the image-relay
  key — a startup check exits (usage error) on equality (D15 S3). The adapter **exits at startup** if
  `ADAPTER_API_KEY` is empty or shorter than **32 bytes**. Rotation without downtime: the adapter
  accepts `ADAPTER_API_KEY` and, inside a rotation window, `ADAPTER_API_KEY_NEXT` (D15 S3; runbook in
  11.3.2); an **empty or unset `ADAPTER_API_KEY_NEXT` disables** the NEXT compare (it is never an
  empty-string match), and when set it must meet the same ≥ 32-byte rule and **differ from
  `ADAPTER_API_KEY`** and from the three other node keys, else startup exits (§10 T68).
- **Error envelope.** Every non-2xx response is
  `{ "error": { "code": "<code>", "message": "<text, ≤ 200 chars, never content>", "errors": [ { "field": "…", "rule": "…" } ] } }`;
  `errors` is present only on validation answers. `message` never carries script, prompt, path or
  upstream text (D15 S4). `errors[].field` echoes a caller key name **only when it matches
  `^[a-z_]{1,64}$`** — any other name becomes `<redacted>` (or the parent field, e.g. `mpt`), so a
  misnamed 2 MiB key can never be echoed back (§10 T69).
- **Request processing order** (D15 S2, §10 T68): **auth (before routing)** → body ≤ **2 MiB** (else
  413 `adapter_body_too_large`; the largest valid body ≈ 300 KB) → JSON parse with duplicate keys
  rejected → `{task_id}` must be a UUID text-format string before any store lookup (else 404
  `adapter_task_unknown`) → the §3 checks. Routing follows auth: a path outside §2 with a valid key →
  404 `adapter_route_not_found`, but **without** a valid key → 401.
- **Unknown path** → 404 `adapter_route_not_found` (after a valid key; see the order above).

| `code` | HTTP | When |
|---|---|---|
| `adapter_unauthorized` | 401 | absent / wrong / duplicated `x-api-key` (every route) |
| `adapter_auth_throttled` | 429 | > 10 auth failures per minute per source address (D15 S2) |
| `adapter_body_too_large` | 413 | submit body over 2 MiB (D15 S2) |
| `adapter_route_not_found` | 404 | any path outside §2 |
| `adapter_task_unknown` | 404 | unknown or non-UUID `{task_id}` |
| `adapter_contract_violation` | 400 | §3 validation failure (F10) |
| `adapter_idempotency_conflict` | 409 | same `render_job_id`, different body sha256 |
| `adapter_capacity_full` | 429 | `active ≥ max_concurrent_tasks`; `Retry-After: 30` (F07/C10) |
| `adapter_node_unhealthy` | 503 | health not `ok` — the adapter refuses new submits (11.3.3 dispatch) |
| `adapter_output_not_ready` | 409 | `video`/`subtitles` asked before terminal success |
| `adapter_output_absent` | 404 | `subtitles` asked when subtitles were off or dropped |
| `adapter_task_busy` | 409 | `DELETE` while MPT still runs (C11, F08) |
| `adapter_probe_unavailable` | 503 | `POST /v1/probe-image` not yet implemented by 11.3.5-T1 (12 CR-19) |
| `adapter_probe_throttled` | 429 | probe over the node's probe budget; `Retry-After` (§10 T67) |

## 2. Routes — the only routes

Eight routes, nothing else. 401 `adapter_unauthorized` applies to every row (§1) and is not repeated.

| Route | Request | Success | Errors |
|---|---|---|---|
| `POST /v1/renders` | body `render-submit.v1` ≤ 2 MiB | 202 `{ task_id, state: "queued" }` for a new task · 200 with the same body when the submit is a byte-identical replay (idempotent by `render_job_id` + body sha256) | 400 `adapter_contract_violation` · 409 `adapter_idempotency_conflict` · 429 `adapter_capacity_full` + `Retry-After: 30` · 503 `adapter_node_unhealthy` |
| `GET /v1/renders/{task_id}` | — | 200 `render-status.v1` (§4) | 404 `adapter_task_unknown` |
| `GET /v1/renders/{task_id}/video` | — | 200 `video/mp4` with `Content-Length`, streamed (never buffered; D15 P3), `X-Content-SHA256` taken from `check.output.sha256` = `output.video.sha256` | 404 `adapter_task_unknown` · 409 `adapter_output_not_ready` |
| `GET /v1/renders/{task_id}/subtitles` | — | 200 `application/x-subrip; charset=utf-8` | 404 `adapter_task_unknown` · 404 `adapter_output_absent` (subtitles off or dropped) · 409 `adapter_output_not_ready` |
| `POST /v1/renders/{task_id}/cancel` | — | 202 cancel accepted (idempotent; an already terminal task → 200 no-op). Releases the image plan (F08), output discarded | 404 `adapter_task_unknown` |
| `DELETE /v1/renders/{task_id}` | — | 204 — the MPT task, its task dir and the relay JSONL are removed (03 D12) | 404 `adapter_task_unknown` · 409 `adapter_task_busy` while MPT still runs |
| `GET /v1/health` | — | 200 `render-node-health.v1` with `status: "ok"` · 503 with the same body and `status: "error"` | — |
| `POST /v1/probe-image` | `{}` — no body fields; the probe exercises the node's own configured OpenAI project/key, never a caller-supplied prompt | 200 `{"funded": true}` or `{"funded": false, "reason"}` — the adapter **internally drives exactly ONE image** through its own relay-backed path (`images.v1`, 03 D5/D6, smallest size / cheapest quality), terminal **synchronously or via one status poll**; excluded from `capacity` accounting; `reason` is a closed enum (below); single-flight; result cached 60 s `funded: true` / 300 s `funded: false`; upstream timeout 10 s → `reason: timeout` | 503 `adapter_probe_unavailable` (route not yet implemented by 11.3.5-T1 — 12.8.3-T3 must treat it as fail-closed, never a fabricated pass) · 429 `adapter_probe_throttled` + `Retry-After` (§10 T67) |

`POST /v1/probe-image` is **contract-only at this freeze** (12 CR-19; §10 T61, semantics §10 T67): it
proves the node's own image-project funding by driving **exactly one cheapest image** through the
node's relay — no GPU render, no plan slot, and the probe is **excluded from `capacity` accounting**
(12.8.3-T3 never uses `INodeSelector`, so a probe never competes with a user render for a dispatch
slot). `200 {"funded": false, "reason"}` carries a **closed** `reason` enum —
`credit_balance_exhausted` | `key_invalid` | `project_unfunded` | `rate_limited` |
`upstream_unavailable` | `timeout` — never raw provider text (which stays in the node log only; the
§1 envelope rule that a body never carries upstream text applies to this 200 body too). Concurrent
probes are **single-flight** (one upstream call); a result is cached 60 s when `funded: true` and
300 s when `funded: false` (callers always issue a fresh request — the cache is adapter-internal);
the upstream call has a 10 s timeout mapped to `reason: timeout`. The route is throttled by
`adapter_probe_throttled` (429 + `Retry-After`) once the per-node probe budget is spent. It is served
by 11.3.5-T1 like the other seven; §10 T67 also records that no probe fixture is shipped (the route
is contract-only, the request is `{}`) — mock-mpt 11.1.2-T1 must serve it directly from this table.

## 3. `render-submit.v1` — the submit body (D3 + D4)

Envelope:

```json
{ "schema": "render-submit.v1", "render_job_id": "0192…", "mpt": { "…": "the D3 table below" },
  "image_plan": { "max_images": 34, "prompt_sha256": [ "…" ] } }
```

- The adapter strips `image_plan` (03 D4 — it bounds the relay plan, it is never sent to MPT), adds
  `n_threads`, and sends `x-task-id: <task_id>` to MPT so MPT's log carries the adapter id
  (C3; §10 T57 — without it a 404 echoes a random `request_id`).
- `task_id` is a UUIDv7 minted by the adapter; MPT's own task id stays internal (logged only).
- Idempotency: `render_job_id` + the submit body sha256. A byte-identical replay → 200 (same body);
  the same `render_job_id` with a different body → 409 `adapter_idempotency_conflict`.
- **Validation order** — the first failing group answers 400 `adapter_contract_violation` with
  `errors: [ { "field", "rule" } ]` (F10); `field` echoes a caller key name only when it matches
  `^[a-z_]{1,64}$`, else `<redacted>` (or the parent field, e.g. `mpt`) — §10 T69:
  1. `mpt` key set = exactly the table below **minus `n_threads`** — an extra or a missing key fails;
  2. types and ranges;
  3. fixed values.

| Field | Value | Source |
|---|---|---|
| `video_subject` | story title, 1–200 chars | `StoryRenderInput.Title` (02 D21) |
| `video_script` | approved body, NFC, 1–20,000 code points (VieNeu input limit, 04 V1) | `StoryRenderInput` |
| `video_terms` | JSON array, 1–200 strings, each 1–1,000 chars | 03 D4 |
| `video_aspect` | `"16:9"` \| `"9:16"` | `series.aspect` (C14) |
| `video_fit_mode` | `"cover"` | fixed |
| `video_concat_mode` | `"sequential"` | 03 D4 |
| `video_transition_mode` | `null` | fixed |
| `video_clip_duration` | `10` | 03 D3 |
| `video_clip_speed` | `1.0` | fixed |
| `match_materials_to_script` | `true` | 03 D4 |
| `video_count` | `1` | fixed |
| `video_source` | `"openai_image"` | fixed |
| `video_materials`, `custom_audio_file` | `null` | fixed — never a server path (§10 T58: a `custom_audio_file` must resolve inside `storage/tasks/<task_id>`, unreachable over the API) |
| `video_language` | `"vi"` \| `"en"` | `series.language` (informational — MPT uses it for its LLM only) |
| `voice_name` | `"kokoro:" + voices.id` | 04 D10 |
| `voice_volume` / `voice_rate` | `1.0` / `1.0` | fixed |
| `bgm_type` / `bgm_file` / `bgm_volume` | `"custom"` / `<file>` / track `bgmVolume` — or `""` / `""` / `0` for no music | D8 |
| `video_music_prompt`, `sonilo_bgm_prompt` | `""` | fixed |
| `subtitle_enabled` | `true` (P: always on) | `render_jobs.params.subtitles.enabled` |
| `subtitle_display_mode` / `subtitle_animation` | `"sentence"` / `"none"` | F01 |
| `subtitle_position`, `custom_position`, `font_size`, `stroke_color`, `stroke_width`, `text_fore_color`, `text_background_color`, `rounded_subtitle_background` | preset values | D6 |
| `font_name` | `"BeVietnamPro-Bold.ttf"` | D7 |
| `paragraph_number` / `video_script_prompt` / `custom_system_prompt` | `1` / `""` / `""` | unused (C19) |
| `n_threads` | **not sent by the backend**; the adapter sets `ADAPTER_MPT_THREADS` (adapter config, default `max(1, floor(cpu_cores ÷ max_concurrent_tasks))`, D15 P6) | F22 |

## 4. `render-status.v1`, lifecycle rules, `render-node-health.v1`

Example (D4):

```json
{ "schema": "render-status.v1", "task_id": "0192…", "render_job_id": "0192…",
  "state": "running", "stage": "images", "percent": 40, "cancel_requested": false,
  "submitted_at": "2026-10-01T02:00:00Z", "started_at": "2026-10-01T02:00:01Z", "finished_at": null,
  "outcome": null, "output": null, "check": null, "images": null,
  "mpt": { "state": 4, "progress": 40, "failed_stage": null, "warnings": [] } }
```

Top-level fields:

| Field | Meaning |
|---|---|
| `schema` | the literal `render-status.v1` |
| `task_id`, `render_job_id` | adapter UUIDv7 · the backend's job id (idempotency key) |
| `state` | `queued` (accepted, MPT progress 0) \| `running` \| `succeeded` \| `failed` \| `cancelled` |
| `stage` | `queued` \| `voice` \| `subtitles` \| `images` \| `assembling` \| `checking` \| `done` (table below) |
| `percent` | MPT progress, non-decreasing; 100 only once MPT is complete (`stage = checking`, or a terminal state) — stages `voice`…`assembling` never reach 100 (§10 T62) |
| `cancel_requested` | true after `POST …/cancel` |
| `submitted_at`, `started_at`, `finished_at` | ISO-8601 UTC `Z`; `finished_at` null until terminal |
| `outcome` | null on `succeeded` and `cancelled`; on `failed` it is `{ "code": "<D5>", "detail": "<closed enum>", "message": "<fixed sentence, D15 S4>" }` — `detail` is the MPT `failed_stage` set ∪ `{mpt_task_missing, mpt_restarted}` ∪ the D9 check names, never free text (§10 T63/T64/T69) |
| `output` | null until `succeeded`: `{ "video": { "bytes", "sha256" }, "subtitles": { "bytes", "sha256", "cues" } \| null, "narration_seconds": <measured audio.mp3 duration, D9> }` |
| `check` | the `render-check.v1` object (D9) — present on success, and on a D9-stage failure |
| `images` | `images.v1` (03 D5/D6, by reference) — **`null` while `state = running`**, even when the relay already called; present on a terminal status only (`succeeded`/`failed`/`cancelled`, 03 D6 keeps polling cheap — §10 T70) |
| `mpt` | `{ "state", "progress", "failed_stage", "warnings" }` — the raw MPT observables, never content; `warnings` is mapped to a **closed code set or dropped by the adapter** (never raw MPT text — §10 T69); for `render_lost` it holds the last observed values with `state: null` (§10 T63) |

**`progress → stage` boundaries — measured in the S3 happy-path trace
(`samples/mpt/S3-progress-trace.json`, observed `0 → 20 → 30 → 40 → 50 → 75 → 100`; the 5/10 writes
complete inside the first second when `video_script` + `video_terms` are supplied, C19/F09):**

| `stage` | MPT `progress` |
|---|---|
| `queued` | 0 |
| `voice` | 5–29 (script/terms writes 5, 10; TTS 20) |
| `subtitles` | 30–39 |
| `images` | 40–49 |
| `assembling` | 50–99 (incl. the computed 75, C7/T6) |
| `checking` | MPT terminal at 100; the adapter's D9 post-processing is running |
| `done` | terminal — with `state = succeeded` **or** `state = cancelled` (§10 T64/T70) |

`failed_stage` → outcome-code mapping (§10 T56 — map on `failed_stage` **alone**, never on `error`
text; an uncaught exception surfaces as `pipeline`, proven by S10 `samples/mpt/S10-assembly-missing-font.json`):
`audio` → `tts_failed` (04 D6; S5/S6) · `materials` → `image_generation_failed` or `image_moderated`
(03 D7; S9) · `video` → `render_assembly_failed` · `preflight`/`script`/`terms`/`pipeline` →
`render_node_error` (`detail` = the stage).

**Lifecycle rules**

- **Lost task** (F06/C9, §10 T63/T70): MPT answers 404 for a task the adapter submitted →
  `state = failed`, `outcome.code = render_lost`, `detail = "mpt_task_missing"`; **or** MPT's process
  start time is newer than `mpt_started_at` → the same code with `detail = "mpt_restarted"`. Both are
  adapter reasons, not MPT `failed_stage`s. `mpt` carries the last observed values with `mpt.state = null`
  (measured S12 `samples/mpt/S12-restart.json`; the orphan task dir stays on disk — MPT never GCs).
- **Capacity** (F07/C10): the adapter counts its own non-terminal tasks; `active ≥ max_concurrent_tasks`
  → 429 `adapter_capacity_full` + `Retry-After: 30`. An MPT 429 (should not happen) is passed on as
  the same code (measured S11 `samples/mpt/S11-429.json`); a queued submit returns 200 and sits at
  `state 4, progress 0` — indistinguishable from running (C10, `samples/mpt/S11-queue.json`).
- **Cancel** (F08/C11, O6, §10 T64): `POST …/cancel` marks `cancel_requested`, releases the image plan
  and keeps polling; on MPT's terminal state the task becomes `cancelled` (`stage = done`,
  `percent = 100`, `outcome = null` — `render_cancelled` is the Worker's job outcome, never an
  adapter outcome code), its outputs are never served and the task dir is removed at once. MPT
  itself has **no cancel route** and refuses DELETE while running (409; measured S13
  `samples/mpt/S13-delete-matrix.json`) — the adapter owns the discard.
- **Cleanup** (03 D12, D15 P4): task dir removed at once for `cancelled`; after ingest (`DELETE`) for
  `succeeded`; 24 h after any other failure.
- **Durable store** (final, 11 CR-4/§10 T33): the adapter keeps
  `{task_id, render_job_id, body_sha256, mpt_task_id, submitted_at, mpt_started_at, state, cancel_requested, outputs}`
  in SQLite at `/var/lib/nts/adapter/tasks.db` and survives its own restart.
- **Never in a status** (F11): node paths, MPT URIs, the script, the terms, the prompts.
- **Post-processing**: after MPT reaches its terminal state the adapter runs `render_check.py …
  --normalize` on the node (D9/F20), serves `final-1.norm.mp4` as the `video` output, and
  `output.video.sha256` is the normalized file's hash (§10 T60; D15 P2 — measure the file that is
  served).

**`render-node-health.v1`** (D4 + D15 P1/P5):

```json
{ "schema": "render-node-health.v1", "status": "ok", "reasons": [],
  "capacity": { "max_concurrent_tasks": 2, "active_tasks": 1, "accepting": true },
  "mpt": { "ok": true, "commit": "<MPT_COMMIT>", "config_profile": "node",
           "started_at": "2026-10-01T00:00:00Z", "whisper_device": "cuda",
           "video_codec": "h264_nvenc", "threads_per_task": 4 },
  "tts_proxy": { "schema": "proxy-health.v1", "…": "04 D8, unchanged" },
  "image_relay": { "ok": true, "tokens_available": 5, "active_plans": 1 },
  "assets": { "ok": true, "font": "BeVietnamPro-Bold.ttf", "music_revision": 1,
              "music_missing": [], "checked_at": "2026-10-01T00:00:10Z" },
  "disk": { "ok": true, "task_dir_free_bytes": 120000000000, "used_pct": 41 },
  "gpu": { "ok": true, "util_pct": 12, "mem_used_mb": 4096, "mem_total_mb": 16384,
           "temp_c": 58, "sampled_at": "2026-10-01T00:00:10Z" },
  "adapter": { "version": "0.1.0", "started_at": "2026-10-01T00:00:00Z" } }
```

| Field | Meaning |
|---|---|
| `status` | `ok` → HTTP 200 \| `error` → HTTP 503 (04 D8 convention: consumers key off the HTTP code and `reasons`) |
| `reasons` | ⊆ `mpt_unreachable` \| `mpt_unauthorized` \| `tts_proxy_not_ok` \| `image_relay_not_ok` \| `font_missing` \| `music_missing` \| `disk_low` (used ≥ 85 %) \| `config_mismatch` \| `codec_unavailable` (D15 P5) \| `gpu_unavailable` (11 CR-4) |
| `capacity` | `max_concurrent_tasks`, `active_tasks`, `accepting` — `accepting` is false whenever `status` is not `ok` (dispatch rule, §10 T62) |
| `mpt` | `ok`, `commit` (`MPT_COMMIT`), `config_profile`, `started_at` — plus `whisper_device`, `video_codec`, `threads_per_task` (D15 P5) |
| `tts_proxy` | `proxy-health.v1` verbatim (04 D8) |
| `image_relay` | `ok`, `tokens_available`, `active_plans` |
| `assets` | `ok`, `font`, `music_revision`, `music_missing`, `checked_at` — computed by one cached background job (adapter start + every `ADAPTER_ASSETS_REFRESH_S` 600 s, single-flight); never re-hashed per health request (D15 P1) |
| `disk` | `ok`, `task_dir_free_bytes`, `used_pct` (`disk_low` ≥ 85 %) |
| `adapter` | `version`, `started_at` |
| `gpu` | `ok`, `util_pct`, `mem_used_mb`, `mem_total_mb`, `temp_c`, `sampled_at` — additive to v1 (11 CR-4, 11 D7); part of the frozen key set |

MPT liveness = `GET /ping` plus an authenticated `GET /api/v1/tasks?page=1&page_size=1` (proves the
key). A 503 health blocks new submits (dispatch rule 11.3.3; 04 D8 for `tts_proxy`).

## 5. Outcome codes (D5), mock-mode mapping, by-reference blocks

All codes are **job outcomes** (`render_jobs.error_code`), never platform HTTP codes (D5/CR-3).
Retries follow 00 §7 (≤ 2 automatic, transient only).

| `code` | Raised by | When | Auto-retry | Fixed `outcome.message` (D15 S4) |
|---|---|---|---|---|
| `tts_failed` | adapter | MPT `failed_stage = audio` (04 D6) | yes | "Text-to-speech synthesis failed on the node." |
| `image_moderated` | Worker (03 D7) | MPT `failed_stage = materials` with moderation, or a succeeded task whose `images.v1` breaks 03 D7 | per 03 D7 | "One or more scene prompts were rejected by the image provider's moderation." |
| `image_generation_failed` | Worker (03 D7) | MPT `failed_stage = materials` without a moderation answer | per 03 D7 | "Image generation failed for all required scenes." |
| `render_assembly_failed` | adapter | `failed_stage = video` | yes | "The node failed to assemble the video from the generated material." |
| `render_node_error` | adapter | `failed_stage ∈ {preflight, script, terms, pipeline}` (`detail` = the stage) — §10 T56 | yes (on another node when one is free) + ops alert | "The render node failed before media generation (internal stage)." |
| `render_lost` | adapter | the §4 lost-task rule (F06/C9; S12) | yes | "The render task is no longer known to the node (MPT restart or lost task state)." |
| `render_timeout` | Worker | no terminal state after `Render:MaxMinutes` (60, 09.6.3) | yes | "The render did not reach a terminal state within the configured time limit." |
| `render_output_invalid` | Worker | download size/type cap breached, or sha256 ≠ `output.video.sha256` | yes | "The node output failed integrity or type checks after download." |
| `render_quality_failed` | Worker (05.5.1-T2) | `RenderQualityEvaluator` → `pass = false` (D10) | no — a deterministic output fails again | "The video failed the render-quality policy." |
| `render_cancelled` | Worker | user cancel (09.6.4) — the job ends `cancelled`, not `failed` | — | "The render was cancelled by the user." |

**mock-mpt `MOCK_FAIL_MODE` mapping** (D5 + CR-4): `tts_failed` → `tts_failed` · `image_moderated` →
`image_moderated` (via `MOCK_IMAGE_REPORT=moderated_all`, 03) · `lost_task` → `render_lost` ·
`timeout` → the task never reaches a terminal state (the Worker raises `render_timeout`) · new modes:
`assembly_failed` → `render_assembly_failed`, `node_error` → `render_node_error`,
`quality:<check>` → a `check` block that fails `<check>`.

**By reference — owned outside this contract:**

- `images` = `images.v1` (03 D5/D6): the image-relay report; the relay runs on `127.0.0.1:8091`.
- `tts_proxy` = `proxy-health.v1` (04 D8): embedded verbatim in health.
- `check` = `render-check.v1` (D9): measured by `worker-node/checks/render_check.py … --normalize`
  on the node; the tool measures, the **backend** decides (F20). Top-level keys:
  `schema`, `tool_version`, `ffmpeg_version`, `input` (`file`, `bytes`, `sha256`), `output`
  (`file`, `bytes`, `sha256`, `normalized`), `container` (`format`, `duration_s`, `faststart`),
  `video` (`codec`, `pix_fmt`, `width`, `height`, `fps`), `audio` (`codec`, `sample_rate`,
  `channels`), `narration` (`duration_s`), `loudness` (`before`, `after` — each
  `integrated_lufs`, `true_peak_dbtp`, `lra` — `normalization_type`), `silence` (`longest_s`,
  `total_s`), `subtitles` (`present`, `cues`, `zero_duration` (§10 T55 — the whisper fail-open signal),
  `overlapping`, `non_monotonic`, `out_of_range`, `coverage_share`, `max_cue_chars`, `long_cues`,
  `cps_p95`, `sync_suspect`, `text_matches_script`, `text_mismatch_cues`), `warnings`, `errors`. Null-serialization (§10 T65):
  a non-representable measure is `null`, never a non-finite number — `input_i = -inf` becomes
  `errors: ["audio_silent"]` with the loudness measure `null`; an unreadable container becomes
  `errors: ["container_invalid"]` with the unmeasurable blocks (`container`, `video`, `audio`,
  `narration`, `loudness`, `silence`, `subtitles`, `output`) all `null`.
- `render-quality.v1` (D10) is the **Worker's** report (`render_jobs.quality_report`, 05.5.1-T2):
  the adapter measures `check`, the backend applies the pass/fail policy — it is never a node field
  (F20). Its `images` member is `null` in the D14 fixtures — it is owned by 03.4.2-T1, never
  written by this contract (§10 T65).
