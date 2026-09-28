# 11.1.1, 11.1.2 — Local development: native dev stack (optional Compose), storage/mail emulators and mock-mpt

> Parent: `../README.md` — D1, D2, D3, D4, D5, D13, D14, D16, D18; F16, F25, F26; CR-10, CR-16.
> Stories: `../../11-worker-deployment.md` §4 (11.1.1 Local dev stack, 11.1.2 mock-mpt), §5.1–§5.3, §6 rows 11.1.1-T1/T2, 11.1.2-T1/T2 (11 CR-1, 11 CR-16).
> Consumed by: 11.5.1-T1 (`e2e.yml`), 09.6.2-T1/T3 contract tests, 10 e2e, 03.4.3-T2, 02.5.2-T3.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope.** The developer-facing half of plan 11: `tools/dev/up.sh`/`down.sh` (native Homebrew services + `dotnet run`, 11 CR-16) with an optional `docker-compose.yml` at the repo root for e2e / image smoke, and `tools/mock-mpt`, the fake render node every render-flow test uses.

- 11.1.1 — `tools/dev/up.sh`/`down.sh` (Homebrew `postgresql@17`, `seaweedfs`, `mailpit` on `127.0.0.1` + `api`, `worker`, `mock-mpt` via `dotnet run`, `web` via `ng serve` behind `--web`) plus the same services as optional Compose services (`api`, `worker`, `web` profile `web`, `mail` (Mailpit), `storage` (SeaweedFS)), the committed `.env.example`, health checks, loopback bindings, and `tools/dev/storage-init.sh` (D14, D2, 11 CR-16).
- 11.1.2 — `tools/mock-mpt` (`NightTales.MockMpt`) on the shared server core `NightTales.RenderNode.Server` (D2, D3, D5): the exact `render-node.v1` routes, canned media, the 05 D14 fixtures, progress simulation, capacity, health, and every `MOCK_FAIL_MODE` of 05 D5 plus `MOCK_FAIL_RATE` and the trigger keywords.

**Out of scope.** Cloud resources and IaC (11.2.x, 11.4.1-T1) — dev uses emulators only. The node: host, systemd units, bundle, `nts-node`, TLS, firewall (11.3.1.x, 11.3.2.x, 11.3.4-T1). Registry/dispatch code (`worker_nodes`, `WorkerNodeSeeder`, `NodeHealthPoller`, `INodeSelector`, 11.3.3-T1/T2) — dev only declares node `mock` in Worker configuration (D13). The real adapter and its MPT backend, post-processing and health (11.3.5-T2/T3/T4), and `ImageRelay/` (03.4.3-T1). The `NightTales.RenderNode.Contract` DTOs and the server core themselves (11.3.5-T1): this folder writes hosts, wiring and tests, and must not fork or restate their logic. The `fetcher` service body (02.5.2-T3 appends it under profile `fetcher`, CR-10). CI workflows (`e2e.yml`/`deploy.yml` = 11.5.1-T1, `node.yml` = 11.5.1-T2).

**Phase / flags.** All four tasks P · M1. `web` starts only with `up.sh --web` (or profile `web` in the Compose variant) until 10.1.1 exists; `fetcher` sits behind profile `fetcher` in the Compose variant only. `mock-mpt` is a dev/staging-only host: never in the node bundle (D12), never a production render node (11 CR-16).

**Order.** 11.1.1-T1 → 11.1.1-T2, in parallel with 11.1.2-T1 → 11.1.2-T2 (README §5 M1, §8.1).

## 2. Confirmed facts

| Id | Fact used here | Source |
|---|---|---|
| P1 | `render-node.v1`: 7 routes, fixed error codes, `render-status.v1`, `render-node-health.v1`, lost/capacity/cancel rules; port 8090 and the store are final (closes 05 T33) | `../README.md` P1 ✅ |
| P2 | The image relay runs inside the adapter on `127.0.0.1:8091`, registered at submit, released at cleanup | `../README.md` P2; 03 D5/D6 ✅ |
| P6/P7 | Wire DTOs move to the leaf `NightTales.RenderNode.Contract` (CR-4/CR-5); `worker_nodes` carries `api_key_ciphertext`, `api_key_hint`, `tls_cert_sha256` | `../README.md` P6/P7 ✅ |
| P11 | The fetcher container/network table is 02.5.2-T3's; 11.2.2-T2 implements and tests it | `../README.md` P11 ✅ |
| E7 | Kestrel `MaxRequestBodySize` defaults to 30,000,000 bytes; `render-node.v1` caps at 2,097,152 | D5; 05 D4 ✅ |
| E11/E12 | SeaweedFS Apache-2.0, Mailpit MIT; MinIO (source-only), Garage, Grafana, Renovate AGPL → **not used** | `../README.md` E11, E12; F16 ✅ |
| D2 | Ownership: `tools/dev/{up.sh,down.sh}` + services `api`/`worker`/`web` + `.env.example` = 11.1.1-T1; `seaweedfs`/`mailpit` services + `tools/dev/storage-init.sh` = 11.1.1-T2; `tools/mock-mpt` = 11.1.2-T1/T2 (`Images/` = 03.4.3-T2) | `../README.md` D2 ✅ |
| D3 | `NightTales.MockMpt` may reference **only** Server + Contract; enforced by `NightTales.Architecture.Tests` | `../README.md` D3 ✅ |
| D4 | `RenderNodeJson.Strict` for submit bodies and fixtures (unknown members and duplicate properties rejected); `Tolerant` on the Worker side; `OutcomeMessages` per code | `../README.md` D4 ✅ |
| D5 | Seams `IRenderBackend`, `ITaskStore` (`SqliteTaskStore` / `InMemoryTaskStore`), `INodeHealthSource`, `IImagePlanSink`; options `RenderNodeServer:{ApiKey,ApiKeyNext,MaxBodyBytes,AuthFailuresPerMinute,MaxConcurrentTasks}`; `CapacityGate`; idempotency by `render_job_id` + body sha256 | `../README.md` D5 ✅ |
| D13 | A node is registered from `RenderNodes:Nodes` + `RENDER_NODE_<NAME>_API_KEY`; `http://` accepted only when `ASPNETCORE_ENVIRONMENT = Development` (F26) | `../README.md` D13 ✅ |
| D14 | The dev service list, the primary native path (`tools/dev/up.sh`) with the optional compose variant, `127.0.0.1` ports, `.env.example`, the three dev buckets, the mock's `MOCK_*` env names and defaults, and the canned-media rules | `../README.md` D14 ✅ |
| D18 | Fixture replay in `NightTales.RenderNode.Tests`; `FakeMptServer` in `NightTales.MptAdapter.Tests`; the mock's tests in `tests/NightTales.MockMpt.Tests` (03.4.3-T2 adds there) | `../README.md` D18, D3 ✅ |
| 05 D4 | The authoritative route/status/health shapes the mock serves, incl. `/subtitles`, `/cancel`, idempotency, capacity 429, lost rule, stage boundaries | `../05-video-assembly-subtitles/README.md:177-241` ✅ |
| 05 D5 | `MOCK_FAIL_MODE` → outcome map, incl. new modes `assembly_failed`, `node_error`, `quality:<check>`; `timeout` never terminal | `../05-video-assembly-subtitles/README.md:203-219` ✅ |
| 05 D14 | The 24 golden fixtures in `worker-node/contract/fixtures/`; no JSON-Schema files | `../05-video-assembly-subtitles/README.md:345-349` ✅ |
| 05 CR-4 | The mock implements `render-node.v1` exactly; `adapter_contract_violation` replaces `mock_contract_violation`; `image_moderated` stays an alias | `../05-video-assembly-subtitles/README.md:381` ✅ |
| 09.1.1-T2 | Local PostgreSQL 17 today is a native Homebrew `postgresql@17` on `127.0.0.1:5432` (`tools/dev/db-init.sh` creates the role/database idempotently); it also owns the optional compose `postgres` service (`name: nighttales`, `postgres:17-alpine`, `pgdata` volume, `pg_isready` healthcheck) and `.env.example`. 11.1.1-T1 owns every additional service and `tools/dev/up.sh` | `../09-backend-api/09.1.1-solution-skeleton/09.1.1-T2-dev-compose-postgres.md` ✅ |
| 02.5.2-T3 | `fetcher` under `profiles: [fetcher]`, no listening socket; the Api's public listener `:8080` and internal lease listener `:8081` | `../02-story-writing/02.5.1-sources-fetcher/02.5.2-T3-…:121-128,144`; 02.5.2-T2:81 ✅ |
| 03.4.3-T2 | `MOCK_IMAGE_REPORT` values (`ok`, `moderated:<slots>`, `skipped:<share>`, `moderated_all`, `funds`, `none`), `__block_image__`, `__fail_image__`, and the mock's submit-field validation → 400 `adapter_contract_violation` | `../03-image-generation/03.4-failures/03.4.3-T2-mock-mpt-images-report.md` ✅ |

**Not verified.** Mailpit/SeaweedFS image digests, Service API paths, default ports and CLI flags come from the projects' docs ⚠️, as do the Homebrew formula versions (`seaweedfs`, `mailpit`) pinned in `docs/dev-setup.md` and the `weed server`/`mailpit` loopback flags (T12); all are re-confirmed at implementation (Open items). No digest, tag or price is asserted in this folder.

## 3. Domain and data

No domain type, no migration. Three pieces of data:

1. **Local PostgreSQL 17** — the native Homebrew server on `127.0.0.1:5432` (`tools/dev/db-init.sh`, 09.1.1-T2); the `pgdata` volume exists only in the optional compose variant. Untouched here.
2. **The three dev buckets** — `nt-media-dev`, `nt-public-dev`, `nt-music-dev` (D14) with the D15 prefixes (`music/`, `music-evidence/`). Created idempotently by `tools/dev/storage-init.sh` against the native SeaweedFS service (or the compose variant); these names are the `<env> = dev` case of cloud's `nt-<bucket>-<env>` (11.4.1-T1).
3. **Mock task state** — `InMemoryTaskStore` (D5) holding `RenderTaskRecord` rows in process memory. A mock restart loses every task; a later poll gets 404 `adapter_task_unknown` → the Worker's `render_lost` path. That mirrors MPT's own in-memory state (observed by 05.4.1-T1), so a restart is a legitimate lost-task drill — but no test may treat mock state as durable. No volume, table or file is created for the mock; canned media lives in the image.

The mock stores only what the status contract allows (05 D4: never node paths, MPT URIs, script, terms, prompts).

## 4. Use cases

| Id | Actor | Trigger | Outcome |
|---|---|---|---|
| UC1 | Developer, fresh clone | `cp .env.example .env && tools/dev/up.sh` | every service healthy with no secret and no Docker; Api on `127.0.0.1:5000`, Worker health on `127.0.0.1:8082`, Postgres on 5432, mock on 8090, mail UI on 8025 |
| UC2 | CI (`e2e.yml`, 11.5.1-T1) | `docker compose up -d` then the e2e suite (**CI only** — the optional compose variant, never the daily path) | real Api/Worker against `mock-mpt` as node `mock`; no GPU, no OpenAI, no cloud |
| UC3 | Developer | submit a render with node `mock` configured | status walks `queued → running(voice/subtitles/images/assembling) → checking → succeeded` in `MOCK_RENDER_SECONDS`; `/video` serves the canned MP4, `/subtitles` the generated SRT, `X-Content-SHA256` matches `output.video.sha256` |
| UC4 | Developer / 09.6 tests | `MOCK_FAIL_MODE=<mode>` or a submission keyword | terminal state and `outcome.code` equal the 05 D5 mapping for that mode |
| UC5 | Developer / 03.4.3-T2 owner | `MOCK_IMAGE_REPORT=<value>` | the `images` block matches the 03.4.3-T2 table and validates against `images.v1.schema.json` |
| UC6 | Developer | sign-up/reset flows | mail is readable in Mailpit at `127.0.0.1:8025`; no SMTP host configured |
| UC7 | Developer | storage-backed flows (05.3.2-T2, 09.7.2, 03.1.2-T1) | the SDK reaches `http://127.0.0.1:8333` natively (`http://storage:8333` in the compose variant); the three buckets exist after `storage-init.sh` |

## 5. API and events

**mock-mpt serves `render-node.v1` and nothing else** (05 D4, 05 CR-4). `app.MapRenderNodeV1(options)` from the core (D5) maps the seven routes; the fallback is 404 `adapter_route_not_found`.

| Route | Mock behaviour |
|---|---|
| `POST /v1/renders` | auth → 2 MiB cap → `RenderNodeJson.Strict` → `SubmitValidator` → idempotency (`render_job_id` + raw-body sha256) → `CapacityGate` (`MOCK_MAX_CONCURRENT`) → `IImagePlanSink.Register` → `IRenderBackend.SubmitAsync` → 202 |
| `GET /v1/renders/{task_id}` | store only; `render-status.v1` from the progress clock |
| `GET …/video` | canned `landscape.mp4`/`portrait.mp4` for the submitted aspect, streamed with `Content-Length` + `X-Content-SHA256`; 409 before terminal success |
| `GET …/subtitles` | SRT generated from the submitted `video_script` (one cue per sentence over the canned duration); 404 `adapter_output_absent` when subtitles were off |
| `POST …/cancel` | 202 idempotent; releases the plan; the backend stops at the next tick → `cancelled` |
| `DELETE …` | 204; 409 `adapter_task_busy` while the fake backend runs |
| `GET /v1/health` | `render-node-health.v1`; `capacity.max_concurrent_tasks = MOCK_MAX_CONCURRENT`; `mpt`/`tts_proxy`/`image_relay`/`assets`/`disk`/`gpu`/`adapter` shaped per 05 D4 + D7 |

**No new route, field or code.** Failure injection (11.1.2-T2) uses only the mock's environment, keywords inside the submitted body, and the contract's existing terminal/outcome fields. `MOCK_IMAGE_REPORT` is not a route: it selects the `images` block 03.4.3-T2 owns.

**Transport.** In local development the mock is plain `http://127.0.0.1:8090` natively and `http://mock-mpt:8090` inside the compose network (D14, 11 CR-16) — the only places an `http://` node is allowed, because `RenderNodeConnectionFactory` refuses `http://` or a missing `TlsCertSha256` unless `ASPNETCORE_ENVIRONMENT = Development` (D13, F26). The dev Worker sets both, so the dev configuration cannot be copied into staging by accident. Outside Development there is no plain `http://` [RULED — README §10 T44] (D20 H2): in staging the mock runs `ASPNETCORE_ENVIRONMENT=Staging` (never Development) and serves HTTPS on 8090 with its own self-signed certificate, pinned in the staging `RenderNodes:Nodes` config like a real node.

**Events.** None: no domain event, outbox row or SignalR message from the mock, and no `openapi.json` change.

**Auth.** One `x-api-key` per request compared to `MOCK_API_KEY` by the core's constant-time compare; mismatch → 401 `adapter_unauthorized`. `MOCK_API_KEY` is a dev value in `.env`, never a real key.

## 6. UI

No UI work.

- `web` is a pass-through service so a developer can run the Angular app (plan 10) against the local Api; natively it starts with `tools/dev/up.sh --web` (`ng serve` on `127.0.0.1:4200`) and only after 10.1.1 defines the image in the compose variant (D14, 11 CR-16). 11.1.1-T1 wires the port, env reference and health check only.
- Mailpit's UI is a third-party container page, bound to `127.0.0.1` and documented in `docs/dev-setup.md`.
- The mock exposes no UI and no Swagger UI: it is machine-facing, and adding an unauthenticated page next to `render-node.v1` would be gratuitous surface.

## 7. Jobs and integrations

**Fake render pipeline (11.1.2-T1).** A `BackgroundService` in the mock (`FakeRenderBackend : IRenderBackend` plus its ticker) simulates MPT: on submit it records the task and schedules progress over `MOCK_RENDER_SECONDS` (default 20) using the 05 D4 boundaries (`voice` 5–29, `subtitles` 30–39, `images` 40–49, `assembling` 50–99, then `checking`, then terminal); `percent` never decreases and reaches 100 only when terminal. At the terminal tick it fills `output`, `check` (from the `render-check.v1.*` fixtures) and `images` (`IImagePlanSink.BuildReport`), then either `succeeded` or the injected failure. Capacity is the core's `CapacityGate`. Health is an `INodeHealthSource` reading in-process values — never MPT, ffmpeg or nvidia-smi (Q1).

**Canned media (F25).** The image build generates, with `ffmpeg -f lavfi`, `landscape.mp4` (10 s, h264/aac, 1920×1080) and `portrait.mp4` (10 s, h264/aac, 1080×1920) — `testsrc2` + `sine`, faststart. Nothing is committed; the mock's tests generate their own media with the same recipe. `MOCK_VIDEO_SECONDS` (default 10) is the *reported* duration; the served file stays the 10 s canned one — deliberate, documented in the mock README.

**Local integration (native and compose).** `api` ↔ PostgreSQL 17 (the native Homebrew server, 09.1.1-T2) and the `api:8081` internal lease listener when the fetcher runs (02.5.2-T2/T3, compose variant only). `api` → Mailpit (SMTP from `.env`: `127.0.0.1:1025` natively, `mail:1025` under Compose); `api`/`worker` → SeaweedFS (`127.0.0.1:8333` natively, `storage:8333` under Compose) and → `mock-mpt` (`127.0.0.1:8090` natively, `mock-mpt:8090` under Compose). `worker` registers node `mock` from `RenderNodes:Nodes` + `RENDER_NODE_MOCK_API_KEY`, only with `ASPNETCORE_ENVIRONMENT = Development` (D13). Compose adds `depends_on: condition: service_healthy` wherever a dependency has a health check; `tools/dev/up.sh` waits on the equivalent endpoints (`pg_isready`, `/cluster/status`, `127.0.0.1:8025`, `/health/live`, `/health/ready`, mock `/v1/health`) and fails naming the service that did not come up; no blanket `restart: always` (11 CR-16).

**Bucket bootstrap.** `tools/dev/storage-init.sh` (11.1.1-T2) creates the three buckets and the `music/`, `music-evidence/` prefixes idempotently with the `.env` credentials through the host `aws` CLI; safe to re-run after a dev data reset (`tools/dev/down.sh --volumes` natively, `docker compose down -v` for the compose variant), and it never deletes.

## 8. Security and privacy

- **Loopback only.** Nothing listens beyond `127.0.0.1` (11 CR-16): natively `postgresql@17`, the `up.sh`-owned SeaweedFS/Mailpit processes (explicit loopback flags, never the stock `brew services`), `dotnet run` hosts and `ng serve` all bind loopback (`lsof` audit), and in the compose variant every published port is `127.0.0.1:<host>:<container>` with the mock `expose`-only (compose-network only), so nothing in the stack is reachable from a café LAN.
- **No real secret.** `.env.example` holds dev-only values; `.env` is git-ignored (D14). Dev keys are `MOCK_API_KEY`, `POSTGRES_PASSWORD` and the emulator credentials.
- **Secrets never in argv or logs.** The mock reads `MOCK_API_KEY` from the environment; it logs ids, hashes and codes only — never the key, body, script, terms or prompts (05 D4; S7). Scripts take credentials from env files or stdin, never from a command line (01 D9).
- **Digests and formulae pinned** (`storage`, `mail` compose images by digest; native Homebrew `seaweedfs`/`mailpit`/`postgresql@17` versions pinned in `docs/dev-setup.md`, T12), so an emulator upgrade is an explicit human action (D14, 11 CR-16).
- **No AGPL component** (E12, F16): SeaweedFS instead of MinIO, Mailpit, no Grafana/Garage/Renovate.
- **`http://` fenced to Development** (F26): the dev registration is impossible elsewhere; 11.3.3-T1's unit test asserts the factory's refusal, and this folder's smoke asserts the dev side.
- **Content-free errors.** The mock returns the fixed `OutcomeMessages` catalogue (D4), never MPT's raw text; a failed body echoes field names and rules only (05 D3).
- **2 MiB body cap** (D5, E7), enforced per endpoint so the 413 test is meaningful in CI.
- **No production data, no OpenAI, no node.** A fake backend does the work; the mock needs no egress.

## 9. Test plan

**`tests/NightTales.MockMpt.Tests`** (D3, D18; 03.4.3-T2 adds `MockImageReportTests.cs` here):

| Area | Coverage |
|---|---|
| Routes/auth | 401 without key on all 7 routes; 404 `adapter_route_not_found`; 409 `adapter_output_not_ready` before success; 404 `adapter_output_absent` with subtitles off |
| Fixtures (05 D14) | every `render-submit.v1.*`, `render-status.v1.*`, `render-node-health.v1.*`, `render-check.v1.*`, `render-quality.v1.*` round-trips through `RenderNodeJson.Strict`; for each scripted state the mock's body equals its fixture (ignoring the timestamps listed per fixture) |
| Progress | `Percent_IsNonDecreasing_AndReachesHundredOnlyTerminal`; stage boundaries sampled against 05 D4 |
| Idempotency/capacity | same id + body → 200 with the stored `{task_id, state}`; other body → 409; more than `MOCK_MAX_CONCURRENT` non-terminal → 429 + `Retry-After: 30` |
| Failure injection (11.1.2-T2) | one test per `MOCK_FAIL_MODE`; `MOCK_FAIL_RATE` determinism; keywords `__fail_image__`/`__block_image__`; `timeout` never terminal |
| Contract validation | malformed submit → 400 `adapter_contract_violation` with `errors[{field,rule}]` |
| Media | `/video` aspect selection, `Content-Length`, `X-Content-SHA256`; `/subtitles` cue count = sentence count |

**Dev stack checks** (the 11-local gate; native here and manual, the compose variant automated by 11.5.1-T1): `tools/dev/up.sh` exit 0 with every endpoint green (Api `/health/live` 200, Worker `/health/ready` 200, mock `/v1/health` 200) and a loopback-only `lsof` audit; `up.sh` twice → idempotent; `down.sh` → no leftover process; one render to `succeeded` through node `mock`; `storage-init.sh` twice → exit 0; `shellcheck` clean; `check_fixtures.py --self-test` still green. Compose variant (11 CR-16): `docker compose config` exit 0 and the missing-variable guard; `docker compose up -d --wait` all healthy; `GET http://mock-mpt:8090/v1/health` from inside the network; `docker compose ps` port audit all `127.0.0.1:`.

**Not tested here:** anything GPU- or MPT-bound; no real render, no OpenAI call, no node.

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 11.1.1-T1 | Native dev stack `tools/dev/{up.sh,down.sh}` (Homebrew services + `dotnet run`) + optional Compose app services (`api`, `worker`, `web` profile), `.env.example`, health checks, loopback ports | Ops | S (0.5) | 09.1.1-T2 | 11.1.1-T2, 11.1.2-T1, 11.5.1-T1; 02.5.2-T3 soft |
| 11.1.1-T2 | Native `seaweedfs`/`mailpit` services (+ optional Compose `storage`/`mail`) and `tools/dev/storage-init.sh` buckets | Ops | S (0.5) | 11.1.1-T1 | 11.5.1-T1, 09.7.2-T1 (09 W2 CR-9) |
| 11.1.2-T1 | `tools/mock-mpt` = `NightTales.MockMpt` on the server core: routes, `InMemoryTaskStore`, fake backend, canned media, D14 fixtures, native `dotnet run` on `127.0.0.1:8090` (+ optional compose service) | BE | S (0.5) | 05.4.1-T2, 11.3.5-T1, 11.1.1-T1; 03.4.1-T1 soft (mock shapes) | 11.1.2-T2, 03.4.3-T2, 09.6.2-T1 (09 W2 CR-9) |
| 11.1.2-T2 | Failure injection: every `MOCK_FAIL_MODE` of 05 D5, `MOCK_FAIL_RATE`, trigger keywords, `MOCK_IMAGE_REPORT` hook via `IImagePlanSink`, tests | BE | S (0.5) | 11.1.2-T1 | 11.5.1-T1; 09.6.3-T1 soft |

Total **2.0 d** (README §9: 11.1 Local dev 2.5 → 2.0, "mock-mpt reuses the server core (−0.5)"). The header cells above equal README §8.1.

**Open items** (marked [INFERENCE]/[OPEN] in the task files):

1. [RULED — README §10 T10] Native: Api binds `127.0.0.1:5000` (public) + `127.0.0.1:8081` (internal), Worker health `127.0.0.1:8082`, mock `127.0.0.1:8090`, `web` `127.0.0.1:4200` [INFERENCE]; the compose variant publishes only `127.0.0.1:5000:8080` (containers listen 8080 public + 8081 internal, 02.5.2-T2) with 8081 on the compose network. 11.5.1-T1 uses these URLs.
2. [RULED — README §10 T12] Mailpit's 1025/8025 and SeaweedFS's 8333/9333 are documented defaults ⚠️, not repo facts. A different choice changes 11.1.1-T2 and `.env.example` together.
3. [RULED — README §10 T32] `MOCK_RENDER_SECONDS=20` (D14) is longer than most tests want; 11.1.2-T2 proposes a ≥ 1 s floor and `1` in test fixtures [INFERENCE]. Only a floor ruling is needed.
4. [INFERENCE] The fake backend's stage boundaries are derived from the 05 D4 percentage table, not from MPT: the mock is a *contract* fake. If 05.4.1-T1 observes different boundaries, the mock follows the fixture, not the table.
5. [OPEN] `MOCK_FAIL_MODE=quality:<check>` needs the failing field from the `render-check.v1.*` fixtures (05.5.1-T2 owns the ids). 11.1.2-T2 reads them and fails at start on an unknown id.

## 11. Rollout

- **Flag / profile.** No feature flag: dev-only artefacts. Natively everything but `web` starts with a plain `tools/dev/up.sh` (`--web` adds `ng serve`); in the compose variant `web` sits behind profile `web` and `fetcher` behind profile `fetcher` (02.5.2-T3) (11 CR-16).
- **Gate text (11-local, README §7)** — this folder's exit criterion: "`tools/dev/up.sh` on a fresh clone reaches healthy for every service (the Compose variant runs the same check in `e2e.yml`, CI only); mock-mpt serves every D14 fixture and every D5 mode." 11.5.1-T1 then consumes the compose file for `e2e.yml`.
- **Inherited by 11.5.1-T1:** service names, health-check conditions, the mock's env defaults; `e2e.yml` — **CI only**, Docker is available on the runner — runs `docker compose up -d --wait` and the suite against node `mock`; daily development never needs Docker (11 CR-16). A rename here is a CR to 11.5.1-T1, not a local edit there.
- **Inherited by 11.5.1-T2 / `deploy.yml`:** `mock-mpt` is built and pushed as an image tagged `sha-<git sha>`; staging renders against it when no GPU node is provisioned (D1, D15) — there it runs `ASPNETCORE_ENVIRONMENT=Staging`, never Development, and serves HTTPS on 8090 with its own self-signed certificate pinned in the staging `RenderNodes:Nodes` [RULED — README §10 T44] (D20 H2). It is never part of `nts-node-<version>.tar.zst` (D12).
- **Monitoring.** None for the dev stack; a mock in staging appears as `render_node_up{node="mock"}` and is expected `down` outside demo windows.
- **Rollback.** `tools/dev/down.sh` (native; `--volumes` drops the dev data) or `docker compose down` / `down -v` for the compose variant (dev data is disposable). Rolling back the mock = previous image tag (compose/staging) or the previous commit (native run); the store is in memory, so no data migration is involved.
