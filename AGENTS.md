# Repository Guidelines

## Project Overview

Night Tales Studio turns story ideas into narrated videos (story → images → voice → subtitles → MP4 → YouTube). The owner's channel is built first (**personal phase P**, one owner account, autopilot). The public SaaS (**phase S**) is built only after a go decision (`plans/00-overview.md` §1.3, §4.1).

**Current state:** implementation started 2026-09-25 on the owner's Mac; which plans are in progress is recorded only in `STATUS.md`. Tracked code: `worker-node/` (bash scripts, pins, runbook `worker-node/README.md`), `trial/` (fixtures, bench tools, baselines) and `.githooks/` (gitleaks). There is no .NET or Angular code yet; `src/`, `tests/`, `web/` and `tools/` are the target layout. **Progress lives in `STATUS.md`** (done / doing / blocked / next); read it first and update it with every change. Runtime state is in `<repo>/.nts/` (gitignored, `NTS_HOME`). API keys live only in the macOS Keychain and must never be committed or pushed (plan 01 DEC-6).

## Architecture & Data Flow

Planned system (contract: `plans/00-overview.md` §5; diagrams: `DIAGRAMS.md`, `docs/diagrams/*.svg`):

```
Angular SPA ──REST + SignalR──► NightTales.Api ──► PostgreSQL 17 ◄── NightTales.Worker (Hangfire + outbox dispatcher)
                                                                         │
                     OpenAI (story, images) ◄────────────────────────────┤
                     GPU node: MPT + VieNeu (TTS) + faster-whisper ◄──────┘ over HTTP (Python there only; no business logic)
```

- Backend: C# / .NET 10 LTS, Clean Architecture + CQRS. Dependency rule `Domain ← Application ← Infrastructure, Api, Worker`, enforced by architecture tests (ArchUnitNET).
  - `NightTales.Domain`: pure C#; its only package is `ErrorOr`.
  - `NightTales.Application`: Mediator 3 (scoped, not MediatR), FluentValidation, ErrorOr, EF Core core package (no provider).
  - `NightTales.Fetcher(.Core)` never references Infrastructure or Application.
- Command pipeline order (09 README D5/D6): `Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit`. UnitOfWork opens the transaction; the idempotency record and audit rows commit inside it.
- The Api never enqueues jobs. It writes `outbox_messages`; the Worker's `OutboxDispatcher` (a `BackgroundService`, `FOR UPDATE SKIP LOCKED`) dispatches them. Job arguments are ids only; retries are 0 unless `[JobPolicy]` says otherwise.
- Optimistic concurrency uses `xmin`. ETags are `"ep-<xmin>"` / `"se-<xmin>"`.
- Web: Angular 22, Clean Architecture per feature (`feature → ui, application → domain`). Features import each other only through `index.ts`.

## Key Directories

**Authority order.** `DECISIONS.md` (why, verified facts) and `plans/00-overview.md` (contracts) win over `plans/01`–`13`, which win over `plans/features/`. `WORKFLOW.md` and `DIAGRAMS.md` only summarise: *"If this file disagrees with them, they win — fix this file"* (`WORKFLOW.md:4`, `DIAGRAMS.md:3`). Inside a plan folder, the README's D-n block is the contract: *"Feature plans and task files MUST use these names exactly. Make any change here first."*

| Path | Purpose |
|---|---|
| `plans/00-overview.md` | **The contract**: stack, solution layout, tables (§6), state machines (§7), routes (§8), estimates, phases, plan template (§10.1) |
| `plans/01`–`13-*.md` | High-level plans per area (not final). Numbering: 01 env, 02 story, 03 images, 04 voice, 05 video, 06 YouTube, 07 TikTok, 08 operations, 09 backend, 10 web, 11 worker/deploy, 12 billing, 13 compliance |
| `plans/features/<NN>-<slug>/` | Detailed plans. `README.md` holds the review, findings F-n, shared decisions D-n (the contract for that plan), CRs, index and re-estimate |
| `plans/features/<NN>-<slug>/<story-id>-<slug>/` | One feature: `plan.md` + one `<task-id>-<slug>.md` per task |
| `DECISIONS.md` | Locked product/stack decisions and verified facts. §3/§4 are locked: do not reopen them |
| `WORKFLOW.md` | Product summary, phases/gates, engineering workflow (§8), where-to-find table (§10) |
| `DIAGRAMS.md`, `docs/diagrams/` | Mermaid sources and exported SVGs (`NN-<slug>.svg`) |

## Development Commands

Worker node (plan 01): `git config core.hooksPath .githooks` once per clone, then `worker-node/bin/bootstrap.sh all`, `worker-node/bin/start-vieneu.sh` (in its own terminal), `worker-node/bin/smoke.sh`. Scripts are bash 3.2-compatible (macOS `/bin/bash`), `shellcheck -x` clean. There is no build yet. For doc work:

```bash
grep -rn "Idempotency → UnitOfWork" plans          # find text that contradicts a contract
tar -czf ~/.nts-snapshots/plans-$(date +%Y%m%d-%H%M%S).tgz plans DECISIONS.md   # snapshot uncommitted work before bulk edits
```

Diagrams: 61 mermaid blocks across 30 files, all currently render (checked 2026-09-25, after the S-phase plans). A `;` inside a `sequenceDiagram` message breaks the parser even inside quotes (it is the statement separator) — use `,`. Edge labels with `:`, `(`, `)`, `<`, `>` must be quoted. After editing a block in `DIAGRAMS.md`, re-export its SVG to `docs/diagrams/NN-<slug>.svg`, because the SVGs are checked in and reviewed.

Planned backend commands (`plans/features/09-backend-api/09.1.5-ci-pipeline/09.1.5-T1-backend-ci-workflow.md`):

```bash
dotnet restore NightTales.ci.slnf --locked-mode
dotnet build   NightTales.ci.slnf -c Release --no-restore
dotnet format  NightTales.ci.slnf --verify-no-changes --no-restore
dotnet test    NightTales.ci.slnf -c Release --no-build --filter "Category!=Integration"
dotnet test    tests/NightTales.Api.IntegrationTests -c Release      # needs Docker (Testcontainers)
```

Local stack: `docker compose` with postgres, storage, mail catcher, api, worker, web and `tools/mock-mpt` in place of the GPU node (`plans/11-worker-deployment.md:28,155-157`; `WORKFLOW.md` §8.2).

CI is two jobs plus a migration check: `build-test` (restore `--locked-mode`, Release build, `dotnet format --verify-no-changes`, tests with `--filter "Category!=Integration"`, `openapi.json` artifact) and `integration` (Testcontainers PostgreSQL, needs `build-test`); 09.2.2-T2 adds a `migrations` job. No coverage tool or coverage threshold is planned anywhere — Definition of Done is behavioural (one integration test per endpoint, unit tests for domain rules).

## Code Conventions & Common Patterns

**Plan documents (what you edit today):**
- English, terse. Evidence legend: ✅ only after reading an official source; otherwise ⚠️, `[INFERENCE]` or `[OPEN]`. `[DECISION]` marks a choice. Never invent versions, licences or APIs.
- IDs: story `<plan>.<epic>.<story>` (e.g. `02.2.2`); task `<story>-T<n>` (e.g. `02.2.2-T2`). Sizes XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 days, plus a 20 % buffer. Every task is tagged P or S.
- Task files (02, 09): H1, then a one-row table `| Story | Size | Area | Depends on | Blocks | Phase / flag |`, then `## Goal`, `Inputs`, `Design`, `Steps`, `Outputs`, `Acceptance checks`, `Tests`, `Failure handling and edge cases`, `Notes`. Plan 01 uses a shorter set; see its README.
- Contract changes go to `plans/00-overview.md` first, then into the plan README's D-n. An edit to another plan is a change request: list it in the README §4 table and tag the edited text `(09 CR-12)` / `(02 CR-n)`.
- After an edit, grep for the old wording across `plans/`, because a superseded contract usually survives elsewhere. Also keep each plan's §11 estimates equal to the sum of its task rows.

**Planned code (00 §5.4):** `Nullable` enabled, warnings as errors, file-scoped namespaces, primary constructors, `sealed` by default, records for commands/queries/DTOs, `CancellationToken` on every async path. Errors are typed domain `Error` → ErrorOr → ProblemDetails `code` (catalogue in 09 §5.4); no exceptions for control flow. State changes only through aggregate methods. Mapping with Mapperly or explicit mappers. Tables are snake_case, ids are UUIDv7, money is `numeric(12,6)`, credits are `bigint`.

**Scripts (plan 01 D9):** Bash `set -euo pipefail`, shellcheck clean, idempotent; exit codes 0 ok / 1 check failed / 2 usage error. Python helpers are stdlib only.

## Important Files

- `plans/00-overview.md` §5.2 (solution layout), §6 (tables), §10.1 (plan template)
- `plans/features/09-backend-api/README.md` D1–D17: the backend skeleton contract (packages N1–N14, pipeline, outbox, ETags, tenant filter)
- `plans/features/02-story-writing/README.md` §11–§12: DoR conditions and the M0 kickoff checklist
- `plans/features/01-environment-setup/README.md` D1–D14: `$NTS_HOME`, secrets in macOS Keychain, ports, usage tap
- `.gitignore`: secrets (`config.toml`, `*.env`, `.env*`), media, models

## Runtime/Tooling Preferences

- Planned, not in the repo yet: .NET SDK 10.0.401 pinned in `global.json` (`rollForward: latestPatch`); .NET 11 is not used. NuGet uses central package management (`Directory.Packages.props`) plus lock files (`RestorePackagesWithLockFile` in `Directory.Build.props`, committed `packages.lock.json`; CI restores with `--locked-mode`).
- Planned: Web: Angular 22 with the Angular CLI and npm (`npm run api:gen` regenerates `shared/api/` from the CI `openapi.json`); bundle budgets will live in `angular.json` (10 §10.1.1). Today's tooling is the worker-node set (`worker-node/README.md`).
- Worker (personal phase): MPT, VieNeu and faster-whisper are Python 3.11/3.12 pinned in `worker-node/pins.env` (`MPT_PYTHON`, `VIENEU_PYTHON`) — versions live only there.
- PostgreSQL 17 in dev, CI and production. Planned: CI is GitHub Actions (`.github/workflows/backend.yml`), with third-party actions pinned by SHA.
- Licences must allow commercial SaaS use. No AGPL, RPL or non-commercial licences; MediatR and AutoMapper are rejected. Hangfire LGPL-3.0 is accepted only as unmodified NuGet packages.
- Local worker: runtime state lives in `NTS_HOME="${NTS_HOME:-<repo>/.nts}"` (gitignored; default set in `worker-node/bin/lib.sh`, CR-11). Secrets live only in the macOS Keychain and are never printed. Services bind to `127.0.0.1` only (ports 8000/8001/8002/8080).
- OpenAI is the only paid provider.

## Testing & QA

Planned (09 README N10/N11, D2):
- xunit.v3, Shouldly, ArchUnitNET, Testcontainers.PostgreSql, Mvc.Testing. No mocking library: use hand-written fakes in `NightTales.TestKit`.
- `NightTales.TestKit` (`09.2.1-T2`): `PostgresFixture` (one `postgres:17-alpine` container per run, `CREATE DATABASE … TEMPLATE` per test class), `ApiFactory` (WebApplicationFactory + `TestAuthHandler`, guarded by a test that production `Program` never registers the `Test` scheme), `WorkerHost` with `InlineJobScheduler`, `FakeClock`, and builders (`UserBuilder`, `SeriesBuilder`, `EpisodeBuilder`) that write through `AppDbContext` under `ISystemContext`.
- Test projects: `NightTales.{Domain,Application,Infrastructure}.UnitTests`, `NightTales.Architecture.Tests`, `NightTales.Api.IntegrationTests` (real PostgreSQL; Worker jobs run in-process).
- Acceptance and performance suites stay outside `NightTales.ci.slnf`.
- Flaky tests are quarantined with `[Trait("Category","Quarantine")]`. Never retry until green.
- Definition of Done (`WORKFLOW.md` §8.1): CI green, unit tests for domain rules and use cases, one integration test per endpoint, strings in vi + en, logs and metrics for new jobs, OpenAPI updated.
- A feature starts only after its detailed plan passes Definition of Ready and the owner signs it off.
- **QA agent after every implementation (owner rule, 2026-09-25):** when code is implemented, a **separate QA/test agent** that did not write it must:
  - re-read the plan, the feature `plan.md` and the task files, including Acceptance checks and README amendments;
  - run every runnable acceptance check on the real system;
  - add negative, boundary and exploratory tests;
  - verify `STATUS.md` against what it observes;
  - report a pass/fail matrix and a defects list.

  Defects are fixed and re-verified before the commit or PR. Every implementation todo list includes this step explicitly, after the implementation items and before review, commit and PR. The reviewer pass (code reading) is additional; it does not replace the QA pass. For **every task**, QA runs first, then a code review, a performance review and a security review (owner rules 2026-09-25/26); all four finish, with fixes re-tested, before any `git push`.
- **Design first (owner rule, 2026-09-25):** every web/UI task or feature has an approved UI design before implementation starts — wireframes + hi-fi mockups (states loading/empty/error/success, breakpoints 360/768/1280, vi + en text, dark/light, focus order and a11y notes, built from the 10 D11 design-system tokens/components), linked from the feature `plan.md` §6 UI and approved by the owner. Each UI feature folder carries a design task `<story>-T0 — UI design` (Area `UX`) that every implementation task in the folder depends on. (`plans/00-overview.md` §10, `WORKFLOW.md` §8.1)
