# 11.3.5 — `mpt-adapter`: render-node contract, shared server core, MPT backend, post-processing and health host

> Parent: `../README.md` — plan 11 TL review (facts K1–K9/E1–E18/P1–P13, findings F01–F28, decisions D1–D19, CR-1…CR-11, canonical dependencies §8.1, re-estimate §9, owner decisions §10, DoR §11). Contract sources: `../../05-video-assembly-subtitles/README.md` D3/D4/D5/D9/D15, `../../03-image-generation/README.md` D5/D6 + `03.4.3-T1`, `../../04-voice-proxy/README.md` D8, `../../09-backend-api/README.md` D1/D9.
> Status: detailed plan, P · M1, 4 tasks, 5.0 d (D5, F01). Names are exactly those of D1–D19; anything not in them is marked `[INFERENCE]` and listed in §10 Open items.

## 1. Scope

Deliver the render node's software: the `render-node.v1` **contract library**, one **server core** shared by the real adapter and `tools/mock-mpt`, the **MPT backend** with its durable store and poller, the **post-processing** step that produces the served normalised video, and the **health/TLS host**.

- `src/NightTales.RenderNode.Contract` — DTOs of D4, constants, `RenderNodeJson.Strict/Tolerant`, `OutcomeMessages` (11.3.5-T1).
- `src/NightTales.RenderNode.Server` — routes, `ApiKeyMiddleware`, body cap, `SubmitValidator`, UUID guard, `CapacityGate`, idempotency, the four seams, error envelope (11.3.5-T1).
- `src/NightTales.MptAdapter` — host, `MptClient`, `SqliteTaskStore`, `MptTaskPoller`, `PostProcessor`, health sources, Kestrel HTTPS on 8090 (11.3.5-T2/T3/T4).

Out of scope: node host/GPU stack/systemd units/bundle (`11.3.1-T1`/`T2`/`T3`), `worker_nodes` + selector + health poller (`11.3.3-T1`/`T2`), the image relay itself (`03.4.3-T1`, its host is wired here by T4), the mock host (`11.1.2-T1`/`T2`), node CI (`11.5.1-T2`), the whisper warm-up (`gpu-smoke.sh`, D9/D12 — this folder only reports the GPU/whisper state, see §10 Open items), node cleanup backstop (`11.3.4-T1`).

## 2. Confirmed facts

- ✅ K1–K5, K9 — MPT: GPU image is unusable for the pinned ctranslate2 (F04/ D9), `ffmpeg_path`/`IMAGEIO_FFMPEG_EXE` rule (F06), whisper singleton loaded lazily and a load error is silent (K4, F05), `root_dir()`/`storage/tasks` layout (K5).
- ✅ E6 (`RemoteCertificateValidationCallback`, `GetCertHashString`), E7 (`MaxRequestBodySize` default 30,000,000 B), E8 (self-contained `linux-x64`), E11 (`Microsoft.Data.Sqlite` MIT, `SQLitePCLRaw` Apache-2.0), E18 (duplicate-property rejection option — ⚠️ still to be confirmed in T1 step 1, ruling T1 gives the `Utf8JsonReader` fallback).
- ✅ Contract facts P1 (`render-node.v1` = 7 routes + `render-status.v1` + `render-node-health.v1` + lost/capacity/cancel rules + D15 S1–S8/P1–P7), P2 (the relay runs in-process on `127.0.0.1:8091`, registered at submit, released at cleanup), P3 (`proxy-health.v1`, 04 D8), P6 (wire DTO ownership moves to the Contract project), P13 (NFR-05-1 is measured on the node).
- ✅ Decisions D3 (projects/1 reference table, `.NET 10`, three test projects), D4 (contract library contents, `Strict`/`Tolerant`), **D5** (server core: request order, seams, submit/idempotency/capacity, status, outputs, cancel, delete, options), **D6** (host: listeners, SQLite store, `MptClient`, `MptTaskPoller`, lost rule, failure mapping), **D6a** (post-processing CLI, `CheckConcurrency` 1, `CheckTimeoutSeconds` 900, timeout → `render_node_error` `check_timeout`), **D7** (health sources/caches + `gpu` block + `codec_unavailable`), D8 (node paths, `nts-render 2770`), D10 (`mpt-adapter.service`), D11 (TLS + keys + rotation without drain), D12 (bundle/deploy), D18 (fixture replay, `FakeMptServer`, security tests), D19 (S1–S8, Q1–Q6).
- ✅ Findings F01 (adapter was undersized; four tasks = 5.0 d), F02 (one contract + one server core, two hosts), F03 (a node binary must not reference Application), F19 (`MptTaskPoller` is the only MPT reader), F20 (check concurrency/timeout), F21 (`gpu` block), F24 (key rotation with `ADAPTER_API_KEY_NEXT`), F26 (`http://` refused outside Development).
- ✅ Rulings T1 (duplicate keys), T2 (a Worker 404 poll → `render_lost`), T3 (selector uses `min(capacity, max_concurrent_tasks)`), T4 (NVENC off in P).
- ✅ Upstream: 05 D3 (the exact `mpt` key set and its `n_threads` rule), 05 D4 (routes, bodies, states, stages, lost/capacity/cancel, health), 05 D5 (outcome codes + `detail`), 05 D9 (`render_check.py` CLI, `--normalize`, `render-check.v1`), 05 D15 (S1–S8/P1–P7), 03 D5 (relay: bind 8091, checks, JSONL, health block), 03 D6 (`images.v1`), 04 D8 (`proxy-health.v1`, clone embedded unchanged), 09 D1/D9.
- ✅ MPT 1.3.7 source (`/private/tmp/mptsrc/MoneyPrinterTurbo-main`): progress points `app/services/task.py:1388` (5), `:1510` (10), `:1537` (20), `:1562` (30), `:1587` (40), `:1613` (50), `app/services/video.py:914` (`_progress = 50`) / `:965`, `:1023` (`+= 50 / video_count / 2` → 75 at `video_count = 1`), `:1674-1675` (100, complete); the failure shape keeps the last progress and sets `failed_stage` (`app/services/task.py:265-287`); stages seen: `preflight` (`:1494-1495`), `audio`, `materials`, `video`, `script`, `terms`; `progress` is clamped to ≤ 100 in state (`app/services/state.py:60-74`); task DTO fields `state|progress|failed_stage|error` and the doc examples `state 1 = complete`, `state -1 = failed`, `state 4 = processing` (`app/models/schema.py:295-300, 388-403`). ⚠️ `app/models/const.py` (numeric constants) and `app/controllers/v1/video.py` were not re-read for this plan — T2 step 1 confirms both.

## 3. Domain and data

### 3.1 Store

`ITaskStore` over SQLite at `Adapter:DataDir/tasks.db` (`/var/lib/nts/adapter/tasks.db`, D6), WAL, one writer connection serialised by `SemaphoreSlim(1)`. One row = one `RenderTaskRecord` (D5).

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = FULL;
PRAGMA busy_timeout  = 5000;
PRAGMA foreign_keys  = ON;

CREATE TABLE IF NOT EXISTS tasks (
  task_id            TEXT    NOT NULL PRIMARY KEY,   -- UUIDv7 text (36 chars)
  render_job_id      TEXT    NOT NULL,
  body_sha256        TEXT    NOT NULL,               -- sha256 hex of the RAW submit body (D5)
  backend_task_id    TEXT        NULL,               -- MPT task id, internal only (05 D4)
  state              TEXT    NOT NULL,               -- queued|running|succeeded|failed|cancelled
  stage              TEXT    NOT NULL,               -- queued|voice|subtitles|images|assembling|checking|done
  percent            INTEGER NOT NULL DEFAULT 0 CHECK (percent BETWEEN 0 AND 100),
  cancel_requested   INTEGER NOT NULL DEFAULT 0 CHECK (cancel_requested IN (0,1)),
  submitted_at       TEXT    NOT NULL,               -- ISO-8601 UTC, 'Z'
  started_at         TEXT        NULL,
  backend_started_at TEXT        NULL,               -- MPT service start time at SubmitAsync (D6 lost rule)
  finished_at        TEXT        NULL,
  cleaned_at         TEXT        NULL,               -- 204 delete; row purged after 7 d (D5)
  outcome_json       TEXT        NULL,               -- RenderOutcomeV1
  output_json        TEXT        NULL,               -- RenderOutputV1
  check_json         TEXT        NULL,               -- render-check.v1 verbatim (05 D9)
  images_json        TEXT        NULL,               -- images.v1 from the relay (03 D6)
  mpt_json           TEXT        NULL,               -- last MPT snapshot for the status `mpt` block (05 D4)
  attempts           INTEGER NOT NULL DEFAULT 0
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_tasks_job_body ON tasks (render_job_id, body_sha256);
CREATE INDEX        IF NOT EXISTS ix_tasks_state    ON tasks (state);
CREATE INDEX        IF NOT EXISTS ix_tasks_cleaned  ON tasks (cleaned_at) WHERE cleaned_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS schema_meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL);
```

No column holds the script, terms, prompts or any path (05 D4 "never in a status", 11 D19 S7): the submit body is hashed and discarded, `mpt_json` stores MPT's `state`, `progress`, `failed_stage`, `warnings` only.

### 3.2 State machine

`queued → running → {succeeded | failed | cancelled}`; `cancel_requested` may be set in any non-terminal state; a cancelled task never reaches `succeeded` (D6). `percent` is monotonic; `100` only with a terminal state (D5). Stage mapping (05 D4) is the poller's job (§7.1).

### 3.3 On-disk

| Path | Owner | Contents |
|---|---|---|
| `/var/lib/nts/adapter/tasks.db` (+ `-wal`, `-shm`) | `nts-adapter` | the store |
| `/var/lib/nts/adapter/tasks/<task_id>/render-check.json` | adapter | the report as written by `--out` |
| `/var/lib/nts/mpt/storage/tasks/<mpt_task_id>/` | MPT (`nts-render 2770`) | `final-1.mp4`, `audio.mp3`, `subtitle.srt`, `script.json`, `final-1.norm.mp4` (05 D9, D8) |
| `/var/lib/nts/relay/<task_id>.jsonl`, `/var/lib/nts/relay/plans/<task_id>.json` | relay (03.4.3-T1) | call log, plan registry — deleted at cleanup (D5) |

Removal rules (05 D15 P4, D6): `cancelled` → MPT task dir and relay files removed at once; `succeeded` → removed after ingest (`DELETE`, base backstop `11.3.4-T1`); any other failure → the disk rule keeps them 24 h.

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | Worker submits a render | idempotent by `render_job_id` + raw-body sha256; plan registered; body stripped of `image_plan`; `n_threads` added; MPT called; `202 {task_id, state:"queued"}` |
| UC2 | Worker polls status | store only, never MPT (F19/Q1); monotonic `percent`; `check`/`images` only when stored |
| UC3 | Worker downloads the video | streamed `final-1.norm.mp4`, `Content-Length`, `X-Content-SHA256` from the check (P3) |
| UC4 | Worker downloads subtitles | `subtitle.srt` as `application/x-subrip; charset=utf-8`, or `adapter_output_absent` |
| UC5 | Worker cancels | idempotent; releases the image plan; MPT has no cancel route → flag + terminal-state handling; task dir removed |
| UC6 | Worker deletes a finished task | MPT task + task dir + relay JSONL removed; `409 adapter_task_busy` while running; re-delete idempotent |
| UC7 | Worker polls health | caches only (Q1); `gpu`/`codec`/`assets` drive `11.3.3`'s dispatch rule |
| UC8 | Relay calls it (in process) | `IImagePlanSink.Register/Release/BuildReport`; T4 binds the host |
| UC9 | Adapter restarts | store survives (D6); non-terminal tasks resume polling from `backend_task_id`; a vanished store → Worker's 404 → `render_lost` (T2) |

## 5. API and events

Routes and codes are 05 D4 verbatim; the adapter adds nothing.

| Route | Result |
|---|---|
| `POST /v1/renders` | 202 `AdapterAcceptedV1` new · 200 replayed (same `render_job_id` + body hash) · 409 `adapter_idempotency_conflict` · 400 `adapter_contract_violation` · 413 `adapter_body_too_large` · 429 `adapter_capacity_full` + `Retry-After: 30` · 429 `adapter_auth_throttled` + `Retry-After: 60` · 401 `adapter_unauthorized` · 503 `adapter_node_unhealthy` |
| `GET /v1/renders/{task_id}` | 200 `render-status.v1` · 404 `adapter_task_unknown` |
| `GET /v1/renders/{task_id}/video` | 200 `video/mp4`, `Content-Length`, `X-Content-SHA256` · 409 `adapter_output_not_ready` · 404 |
| `GET /v1/renders/{task_id}/subtitles` | 200 `application/x-subrip; charset=utf-8` · 404 `adapter_output_absent` · 409 · 404 |
| `POST /v1/renders/{task_id}/cancel` | 202 idempotent · 200 no-op when terminal |
| `DELETE /v1/renders/{task_id}` | 204 · 409 `adapter_task_busy` |
| `GET /v1/health` | 200/503 `render-node-health.v1` |
| anything else | 404 `adapter_route_not_found` |

Error envelope (05 D4): `{"error":{"code":"<code>","message":"<≤ 200 chars, no content>","errors":[{"field":…,"rule":…}]}}`. `OutcomeMessages.For(code)` is the fixed D15 S4 sentence; MPT's raw error text never enters it.

No events are published by the node: the Worker polls, and node→platform signalling is the Worker's health/metrics path (D13, F19).

## 6. UI

None. This story has no user-visible surface; the `render-node-health.v1` JSON is consumed by `NodeHealthPoller` (11.3.3-T1) and exported as `render_node_*` metrics (D13, F21). The only operator-visible artefacts are the fixed startup messages (exit 2, S3), journal lines (ids/hashes/codes only, S7) and the `gpu-smoke.sh` output on the node (D9).

## 7. Jobs and integrations

### 7.1 `MptTaskPoller` (T2)

`BackgroundService`, every `Adapter:PollSeconds` = 2, per non-terminal task, the only MPT reader (F19).

| MPT `state` | MPT `progress` | adapter `state` | adapter `stage` | adapter `percent` |
|---|---|---|---|---|
| — (no `backend_task_id`) | — | `queued` | `queued` | 0 |
| 4 | 5, 10, 20 | `running` | `voice` | as reported |
| 4 | 30 | `running` | `subtitles` | 30 |
| 4 | 40 | `running` | `images` | 40 |
| 4 | 50 … 99 | `running` | `assembling` | as reported |
| 1 | 100 | `checking` → `succeeded` | `checking` → `done` | 100 once succeeded |
| -1 | any | `failed` (+ mapped code) | last stage | as reported |

`percent` never decreases (D5). `stage = checking` between MPT completion and the post-processor's verdict.

### 7.2 Other integrations

| Component | Trigger | Notes |
|---|---|---|
| `PostProcessor` (T3) | MPT complete | `CheckConcurrency` 1, `CheckTimeoutSeconds` 900, list-arg process, `--normalize` (D6a) |
| Health caches (T4) | intervals 15 s (MPT/proxy/GPU/disk), 600 s (assets, single-flight), start (codec) | `GET /v1/health` reads caches only (D7/Q1); [RULED — README §10 T63] (D20 H16) every disk figure (`used_pct`, `task_dir_free_bytes`, `disk_low`, and the Worker's `render_node_disk_used_pct`) comes from **one** `statvfs` on `/var/lib/nts/mpt/storage` — never a second stat, a `du` or a per-task walk |
| Relay host (T4) | `ImageRelay:Enabled` | binds `03.4.3-T1`'s registry to `IImagePlanSink`; loopback 8091 |
| Cleanup | cancel/delete/24 h rules (T3) + hourly timer `11.3.4-T1` | P4 |
| Fixture replay | CI (`node.yml`, 11.5.1-T2) | `RenderNodeJson.Strict` + responses vs D18 fixtures |

## 8. Security and privacy

- **S1/S3 transport**: HTTPS only on 8090 with `/etc/nts/tls/adapter.crt|key`; Kestrel refuses `0.0.0.0` or a public `ListenAddress`; `http://` is a Development-only convenience (F26) and the Worker-side pin lives in `RenderNodeConnectionFactory` (11.3.3-T1).
- **S2 request handling**: auth before body → cap 2 MiB → strict parse (duplicate keys rejected) → D3 validation; UUID text check before any store lookup; per-source auth-failure throttle.
- **S3 keys**: startup equality check across `ADAPTER_API_KEY`, `ADAPTER_API_KEY_NEXT`, `NTS_MPT_API_KEY`, `NTS_PROXY_API_KEY`, `NODE_OPENAI_IMAGE_KEY` → exit 2; keys never in argv, logs, the bundle or any status body; the MPT key is used only by `MptClient`.
- **S4 error text**: fixed sentences per code; MPT exception text stays in the journal.
- **S7 content**: status/health/logs carry ids, hashes, codes, counts — never script, terms, prompts, paths or MPT URIs; `check_fixtures.py --scan` guards committed samples.
- **S8 the adapter serves only known task ids**: outputs are resolved through the store, never from a request-supplied path; download routes never redirect.
- **Q1–Q3 performance**: cache-only status/health, bounded check concurrency and timeout, 1 MiB-chunk hashing and streaming I/O with `Content-Length` (no whole-file buffers), `X-Content-SHA256` taken from the check.

## 9. Test plan

| Project | Covers |
|---|---|
| `tests/NightTales.RenderNode.Tests` (T1) | fixture replay of every `worker-node/contract/fixtures/*.json` (D18), strict/tolerant JSON, auth/throttle/cap/413/400/404 matrix, idempotency, capacity race, status invariants, output headers, cancel/delete, `OutcomeMessages` completeness |
| `tests/NightTales.MptAdapter.Tests` (T2, T3, T4) | `FakeMptServer` (states, 404s, restarts, slow responses, failed stages), stage/failure mapping, store restart, poller cadence, health caches incl. `gpu`, codec check, TLS/startup guards, post-processor arguments/timeout/exit codes, `[Trait("Category","Node")]` real `render_check.py` subset |
| `tests/NightTales.MockMpt.Tests` | not this folder (11.1.2-T1/T2) — the shared core is replayed once per host (F02/D18) |
| scripts | `render_check.py --self-test`, `check_fixtures.py --scan`, `shellcheck` on new shell, `node.yml` (11.5.1-T2) |

Nothing GPU-bound runs in CI (D18); the node gate `11-node` uses `gpu-smoke.sh` + a fixture replay against the real adapter.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.3.5-T1 | Contract library and shared server core | BE | L (2.0) | 05.4.1-T2, 09.1.1-T1 | 11.3.5-T2, 11.1.2-T1, 11.3.3-T1, 11.5.1-T2, 03.4.3-T1, 09.6.2-T1 |
| 11.3.5-T2 | MPT backend, durable store and task poller | BE | M (1.0) | 11.3.5-T1 | 11.3.5-T3, 11.3.5-T4, 11.3.4-T1 |
| 11.3.5-T3 | Post-processing, output serving and disk rules | BE | M (1.0) | 11.3.5-T2, 05.5.1-T1 | 11.3.1-T2 |
| 11.3.5-T4 | Health sources, TLS host and configuration | BE | M (1.0) | 11.3.5-T2, 04.1.4-T1, 05.2.2-T1, 05.3.1-T1 | 11.3.1-T2 |

Total 5.0 d raw (L 2.0 + M 1.0 × 3) = the adapter part of the 11.3 re-estimate (`../README.md` §9: adapter 2.0 → 5.0, F01); plan 11 total 23.0 d raw / 27.6 buffered (O3 accepted). No buffer is claimed inside a task.

**Open items** (for the TL; none blocks writing task files):

1. [INFERENCE] `RenderTaskRecord` (`../README.md:168`) has no field for the `render-status.v1` `mpt` block 05 D4 requires (`05 README:201`); T1 adds `MptLastSnapshot` and the store column `mpt_json`. Needs TL confirmation because 05 D4 is a signed contract.
2. [INFERENCE] 05 D4 requires `output.subtitles { bytes, sha256, cues }` (`05 README:205`), while the D9 report's `output` covers only the normalised video (`05 README:292`); T3 hashes `subtitle.srt` once after the check and takes `cues` from `check.subtitles.cues`.
3. [RULED — README §10 T20] F05 (`../README.md:89`) names both `gpu-smoke.sh` and "the adapter's startup warm-up call" for loading `/var/lib/nts/models/whisper-large-v3-turbo/model.bin`, while this folder's fixed scope gives the warm-up to `gpu-smoke.sh` alone (D9, D12). T4 only **reports** `mpt.whisper_device`/GPU state and runs no warm-up — confirm which side owns the load (the file itself is never read by the adapter either way).
4. [RULED — README §10 T19] `render-node-health.v1` `mpt.commit` (`05 README:216`) has no confirmed live source (MPT `/ping` was not read); T4 takes it from the bundle `MANIFEST.json`/`worker-node/pins.env` at startup and proves liveness + key with `GET /ping` + `GET /api/v1/tasks?page=1&page_size=1`. Confirm the source of `commit`.
5. [INFERENCE] The paths of `check-fonts.sh` / `check-music.sh` are not fixed (D7 and `../README.md:213` name the scripts only); T4 resolves them under `/opt/nts/current/worker-node/` and marks the `assets` source failed if absent — 11.3.1-T2 must confirm the directory.
6. ⚠️/✅ MPT state constants are documented as `-1` failed / `1` complete / `4` processing with source `models/const.py` L25–27 (05 C6, `05 README:33`), progress values 5→100 (05 C7, `05 README:34`) and the failed stages incl. `pipeline` (05 C8, `05 README:35`); T2 step 1 still re-reads `app/models/const.py` at the pin before coding the tables.

## 11. Rollout

- **Phase**: all four tasks are P · M1 (D1). No M0 work (M0 renders from the Mac via `mpt_client.py`, 05 D13).
- **Order**: T1 (after `05.4.1-T2` and `09.1.1-T1`) → T2 → {T3, T4} → `11.3.1-T2` → `11.3.1-T3`. `11.1.2-T1`, `11.3.3-T1` and `03.4.3-T1` start from T1; `11.5.1-T2` from T1.
- **Gates**: `11-local` (mock on the shared core), `11-node` (fixtures replay green against the real adapter, health 200 with `gpu`/`tts_proxy`/`image_relay`/`assets` ok, one trial story passing 05 D10), `11-M1`.
- **Flags/config**: `ImageRelay:Enabled` (default true, 03), `Adapter__*` and `RenderNodeServer__*` (D6); `Adapter__CheckConcurrency` 1 and `Adapter__CheckTimeoutSeconds` 900 re-set from `05.5.2-T1` wall times and `11.6.3-T1`; `Adapter__VideoCodec` = `NTS_MPT_VIDEO_CODEC` (`libx264` in P, T4).
- **Deploy path**: the adapter is published `-r linux-x64 --self-contained` (E8) into the node bundle and installed by `nts-node deploy` (D12); adapter restarts never lose state (SQLite) and a lost store degrades to `render_lost` on the Worker side (T2).
- **Rollback**: per task — T1/T2: keep the previous release dir, `current` swap back, restart `mpt-adapter.service`; T3: `Adapter__CheckTimeoutSeconds`/concurrency are config-only, and reverting the release restores the previous check arguments; T4: health sources are additive (05 CR-4); disabling `ImageRelay:Enabled` requires the emergency MPT image profile (03.4.3-T1) and is the relay's rollback, not this story's.
