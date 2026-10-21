# 09.1.1 — Solution skeleton

> Refines: `../../../09-backend-api.md` §4 (epic 09.1, story **09.1.1**), §5.2, §5.4
> Contract: `../README.md` — **D1** (packages/references), **D2** (projects/paths), **D13** (TestKit), **F01**, **F02**, **F10**, **N12**, **N13**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.1.1 *Create solution* — "As the Tech Lead I want the Clean Architecture solution so that every feature has one obvious place." Must · **S** (detailed 0.75 d, §10).

**In scope.** The `NightTales.sln` solution with the D2 project set, `NightTales.ci.slnf`, the D1 project references, central package management (`Directory.Packages.props`), shared build properties (`Directory.Build.props`), `global.json`, `.editorconfig`, the Api/Worker hosts with a `/health/live` probe and an empty Worker, the Application assembly marker, `.gitignore` additions, and a development PostgreSQL 17 setup: `tools/dev/db-init.sh` against a native Homebrew server, plus an optional `docker-compose.yml` `postgres` service for e2e/image smoke (09 W3 CR-35).

**Out of scope.** Any domain/application type beyond the assembly marker (D3 primitives belong to their owning features: 09.2.1-T1, 09.4.1-T1, 09.5.1-T1). The Fetcher projects (`NightTales.Fetcher.Core`/`NightTales.Fetcher`) are created by 02.5.2-T3; 09.1.1 only reserves their names in the solution filter. The mediator pipeline (09.1.3), error mapping (09.1.4), contexts/migrations (09.2.x), test harness (09.2.1-T2) and arch rules (09.1.2-T1). `api`, `worker`, `mock-mpt` and the storage emulator in Compose are 11.1.1-T1 (F10).

**Refines** §5.2 layout and §5.4 language settings; corrected by F01 (Domain may reference `ErrorOr`) and F02 (Application references `Microsoft.EntityFrameworkCore` core).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| S1 | .NET runtime 10.0.12, SDK **10.0.401**; runtime 10.0.12 is the pinned major/minor; target framework `net10.0` | 00 §5.1 ✅, README N13 |
| S2 | D2 project set: `src/` Domain, Application, Infrastructure, Api, Worker (+ Fetcher.Core/Fetcher reserved); `tests/` Domain.UnitTests, Application.UnitTests, Infrastructure.UnitTests, Architecture.Tests, Api.IntegrationTests, TestKit | README D2 ✅ |
| S3 | D1 pinned versions: Mediator 3.0.2, ErrorOr 2.1.1, FluentValidation 12.1.1, EF Core 10.0.12, Npgsql EF 10.0.3, EFCore.NamingConventions 10.0.1, Hangfire.Core/AspNetCore 1.8.25, Hangfire.PostgreSql 1.21.1, ArchUnitNET 0.13.4, xunit.v3 4.0.1, Mvc.Testing 10.0.12, Shouldly 4.3.0, Serilog.AspNetCore 10.0.0, Microsoft.AspNetCore.OpenApi 10.0.12 — no container library (09 W3 CR-35) | README D1, N1–N12 ✅ |
| S4 | Reference rules: Domain → `ErrorOr` only; Application → Domain, `Mediator.Abstractions`, `FluentValidation`, `ErrorOr`, `Microsoft.EntityFrameworkCore` (no provider); Infrastructure → Application + Npgsql EF, NamingConventions, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, Hangfire, `Microsoft.Extensions.*`; Api/Worker → Application + Infrastructure + `Mediator.SourceGenerator` | README D1, F01, F02 ✅ |
| S5 | `NightTales.TestKit` holds `PostgresFixture`, `ApiFactory`, `TestAuthHandler`, `WorkerHost`, `InlineJobScheduler`, `FakeClock`, builders; referenced by every test project. No mocking library [DECISION] | README D13 ✅ |
| S6 | `Mvc.Testing`/xunit.v3/Shouldly are test-only packages; `Mvc.Testing` is not needed by `TestKit` if the fixture types arrive in 09.2.1-T2. `TestKit` starts no container: `PostgresFixture` connects to `NTS_TEST_PG` (09 W3 CR-35) | README D13, N11 ✅ |
| S7 | `NightTales.ci.slnf` excludes `NightTales.AcceptanceTests` and `NightTales.Performance.Tests` from the default `dotnet test` run | README D2 ✅ |
| S8 | PostgreSQL 17 everywhere: native Homebrew `postgresql@17` for local dev, the `postgres:17` service container for CI/tests, production on the same major (O3 decided by the owner 2026-09-24; owner 2026-09-28 for the native dev path, 00 §5.1) | README D8, O3 ✅ |
| S9 | Docker Compose is PostgreSQL-only and **optional** (F10 / CR-6; 09 W3 CR-35): daily dev uses the native server; 11.1.1-T1 extends the same file with api, worker, mock-mpt and the storage emulator for the e2e path | README F10, CR-6 ✅ |
| S10 | `InvariantGlobalization` must be **false**: vi/en runtime i18n and Vietnamese collation needs ICU | 02 T7 note (README cross-ref) ✅ |
| S11 | CI produces `src/NightTales.Api/openapi.json` at build time (D14); the package is `Microsoft.AspNetCore.OpenApi` 10.0.12 | README D14, N12 ✅ |
| S12 | `TreatWarningsAsErrors`, `Nullable enable`, `LangVersion latest`, analyzers, file-scoped namespaces, `sealed` by default | 00 §5.4 ✅ |
| S13 | No secrets are committed; local dev uses user-secrets or env, `.env.example` documents the database variables (09 W3 CR-35) | 00 §5.1, README D8 ✅ |

## 3. Domain and data

**No domain or database types.** The solution skeleton ships project files, config and two host entry points only:

| Object | Path | Content |
|---|---|---|
| Solution | `NightTales.sln` | all D2 projects |
| Filter | `NightTales.ci.slnf` | excludes `NightTales.AcceptanceTests`, `NightTales.Performance.Tests` (S7) |
| SDK pin | `global.json` | `10.0.401`, `rollForward: latestPatch` |
| Build props | `Directory.Build.props` | `net10.0`, `Nullable`, `TreatWarningsAsErrors`, `LangVersion latest`, `ImplicitUsings`, `InvariantGlobalization=false` (S10), analyzer level |
| Package props | `Directory.Packages.props` | D1 versions, `ManagePackageVersionsCentrally` |
| Marker | `src/NightTales.Application/ApplicationAssemblyMarker.cs` | `public sealed class ApplicationAssemblyMarker;` — the `Mediator` assemblies argument (D4) |
| Api host | `src/NightTales.Api/Program.cs` | `WebApplication`, `MapGet("/health/live", …)`, `public partial class Program` for `WebApplicationFactory<Program>` |
| Worker host | `src/NightTales.Worker/Program.cs` | empty `Host.CreateApplicationBuilder` host, exits cleanly |
| Editorconfig | `.editorconfig` | file-scoped namespaces, `sealed`-by-default rule severity, `dotnet_diagnostic` levels |
| Dev DB | `tools/dev/db-init.sh`, `.env.example` | idempotent role/database creation on the native PostgreSQL 17 at `127.0.0.1:5432` (09 W3 CR-35) |
| Compose (optional) | `docker-compose.yml` | `postgres` service only; used by e2e/image smoke, never required for daily dev (S9) |

**Data:** none. No `AppDbContext`, no migration, no table (09.2.x).

## 4. Use cases

| # | Use case | Entry point | Behaviour |
|---|---|---|---|
| UC1 | A developer clones and builds | `dotnet build NightTales.ci.slnf` | restores from the pinned versions, builds warning-free; every project lands in its D2 place |
| UC2 | A developer runs the tests | `dotnet test NightTales.ci.slnf` | exits 0; no feature tests exist yet, and acceptance/performance suites are excluded by the filter |
| UC3 | A developer starts a local database | `brew services start postgresql@17` + `tools/dev/db-init.sh` | a native PostgreSQL 17 listening on `127.0.0.1:5432`, with the `.env` role and database created idempotently (09 W3 CR-35); the optional `docker compose up postgres` path still works for e2e/image smoke |
| UC4 | A developer points the Api at the local DB | user-secrets or env | the development connection string resolves; no secret is committed (S13) |
| UC5 | A CI/feature job extends the environment | 11.1.1-T1 | appends `api`, `worker`, `mock-mpt` and the storage emulator to the same Compose file (F10) |

**Authorization/idempotency.** None. `/health/live` is anonymous and side-effect free.

## 5. API and events

| Surface | Shape | Owner |
|---|---|---|
| `GET /health/live` | 200, empty body; liveness only (no DB dependency) | this story |
| `/api/v1` groups | not mapped yet | 09.1.3 onwards |
| `openapi.json` | generated at build to `src/NightTales.Api/openapi.json`; the skeleton enables generation, the document body is filled as endpoints land | this story enables; 09.9.5 owns the diff |

**Events.** None. No outbox, no SignalR, no job registration.

## 6. UI

None (backend only). No Angular artifact is added, changed or referenced by this story.

## 7. Jobs and integrations

| Item | Detail |
|---|---|
| Jobs | **None.** The Worker host starts empty; `IJobScheduler`/Hangfire arrive in 09.2.6-T1. |
| Integrations | **Native PostgreSQL 17** (`postgresql@17` via Homebrew) for local dev, the `postgres:17` service for CI/tests, and the optional Docker Compose `postgres` service for e2e/image smoke (S8, S9; 09 W3 CR-35). |
| Config keys | `ConnectionStrings:Postgres` (dev: user-secrets/env), `Compose` env `POSTGRES_DB`/`POSTGRES_USER`/`POSTGRES_PASSWORD` via `.env`; `ASPNETCORE_ENVIRONMENT=Development`. |
| CI | 09.1.5-T1 consumes the solution filter and the OpenAPI artifact (S11). |

## 8. Security and privacy

| Threat / rule | Control |
|---|---|
| Secret committed | `.env` is git-ignored; only `.env.example` is committed; the connection string comes from user-secrets/env (S13). The Compose password is a local throwaway value, never a production one. |
| Database exposed beyond localhost | loopback-only in both paths: the native server sets `listen_addresses = '127.0.0.1'` and the compose mapping is `127.0.0.1:5432:5432`, never `0.0.0.0` (UC3). |
| Supply chain | central package management pins exact versions (S3); no floating ranges. |
| Globalization | `InvariantGlobalization=false` so vi/en behaviour is ICU-backed and does not silently degrade (S10). |
| Version drift | one PostgreSQL major (17) in dev, CI, tests and production (S8, O3); the dev server is Homebrew, CI/tests use `postgres:17` (09 W3 CR-35). |

## 9. Test plan

| Level | Cases |
|---|---|
| Build | `dotnet build NightTales.ci.slnf -c Release` warning-free (no warnings-as-errors escape); `dotnet build` without the filter |
| Test discovery | `dotnet test NightTales.ci.slnf` exits 0 and discovers no failing tests; the filter excludes acceptance/performance |
| Host | `HealthLive_ReturnsOk` — `GET /health/live` → 200 (added by 09.2.1-T2 once `ApiFactory` exists) |
| Dev DB | `tools/dev/db-init.sh` run twice → exit 0 and a no-op the second time; `psql "host=127.0.0.1 …"` answers PostgreSQL 17; `lsof` shows a loopback-only listener (09 W3 CR-35) |
| Compose (optional) | `docker compose config` validates; `docker compose up -d postgres` becomes healthy per its password-login healthcheck (09 W3 CR-36) within the healthcheck window |
| Config | the dev connection string resolves from user-secrets/env and never appears in the repo; `git grep` finds no password literal |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.1.1-T1 | Solution, projects, references, `Directory.Build.props`/`Directory.Packages.props` | **S (0.5)** | — | 09.1.1-T2, 09.1.2-T1, 09.1.3-T1, 09.2.1-T1, 05.1.2-T1, 11.3.5-T1 (Contract project, 11 CR-3) |
| 09.1.1-T2 | Native development database (PostgreSQL 17 via Homebrew; optional compose `postgres` service) | **XS (0.25)** | 09.1.1-T1 | 11.1.1-T1 |

Total **0.75 d** — matches README §9 (T1 0.5, T2 XS 0.25: the compose service is optional and the daily path is Homebrew + `db-init.sh`, F10 / 09 W3 CR-35).

### Open items for the TL

- **[DECISION] O3 PostgreSQL major (owner, 2026-09-24).** PostgreSQL 17 in dev (Homebrew `postgresql@17`), CI and tests (`postgres:17`) and production; the hosting choice (11.2.1) must offer 17 (09 W3 CR-35).
- **[DECISION] Locked restore.** `packages.lock.json` + `RestoreLockedMode` are introduced here and used by 09.1.5-T1; if the team prefers not to commit lock files, CI drops the locked mode flag only.
- **[DECISION] Fetcher projects deferred.** `NightTales.ci.slnf` names them only once 02.5.2-T3 creates them; until then the filter lists existing projects.
- **[OPEN] Analyzer level.** `AnalysisLevel`/`EnforceCodeStyleInBuild` values are chosen at T1; the only hard requirement is that the `sealed`-by-default rule is an error (00 §5.4).

## 11. Rollout

- **Flag:** none — infrastructure only.
- **Order:** 09.1.1-T1 → 09.1.1-T2 → (09.1.2-T1, 09.1.3-T1, 09.2.1-T1 in parallel) → 09.1.5-T1.
- **Migration order:** none.
- **Gate 09-skeleton:** 09.1.1–09.1.5 merged; CI green on an empty feature set; the architecture tests fail on a deliberate violation (demonstrated once, then reverted).
- **Rollback:** the solution and Compose files are additive; removing the story removes only scaffolding that nothing in production depends on yet.
