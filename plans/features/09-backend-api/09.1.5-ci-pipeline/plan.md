# 09.1.5 — CI pipeline (backend)

> Refines: `../../../09-backend-api.md` §4 (epic 09.1, story **09.1.5**, L75–76); `../../../11-worker-deployment.md` 11.5.1 (the full pipeline, deploys).
> Contract: `../README.md` — **D1**, **D2**, **D13**, **D14**, **O2**.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.1.5 — "As the team I want CI on every PR so that main is always releasable." Must · detailed **S 0.5**.

**In scope.** One workflow for the backend, run on every PR and on pushes to `main`:
- locked restore;
- Release build of `NightTales.ci.slnf`;
- `dotnet format --verify-no-changes`;
- unit and architecture tests;
- integration tests against a runner-provided `postgres:17` service (`NTS_TEST_PG`), no Docker-in-runner step (09 W3 CR-35);
- the `openapi.json` artifact and test-result artifacts.

**Out of scope.**
- Image builds, registry, deploys, the web and e2e jobs (11.5.1).
- The migration check job (09.2.2-T2 adds it to this workflow).
- The contract diff (09.9.5).
- Dependency scanning (09.9.4).

**O2 decided by the owner 2026-09-24: GitHub Actions** (09 CR-13). The job layout is platform-neutral: every step is a `dotnet` command and the database is a service container, so a move to another CI only changes the wrapper.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| C1 | 11.5.1 names GitHub Actions (owner decision O2, 2026-09-24); the repo has no remote yet | 11 L94 ✅, repo state ✅ |
| C2 | (09 W3 CR-35) The `integration` job needs no Docker daemon: GitHub Actions starts the `postgres:17` service container for the job and `NTS_TEST_PG` points at it (`PostgresFixture`, 09.2.1-T2) — the fixture creates its own databases on that server | 00 §5.1 (owner 2026-09-28), 09.1.1-T2, 09.2.1-T2 ✅ |
| C3 | Build-time OpenAPI generation uses `Microsoft.Extensions.ApiDescription.Server` with `OpenApiGenerateDocuments` ⚠️ (to confirm for .NET 10) | ASP.NET Core docs ⚠️ |

## 3. Domain and data

None.

## 4. Use cases

| # | Use case | Result |
|---|---|---|
| UC1 | A PR is opened or updated | the workflow runs; a red job blocks the merge (branch protection) |
| UC2 | A push to `main` | the same jobs run; artifacts are kept 14 days |
| UC3 | Two pushes to one PR in quick succession | the older run is cancelled (concurrency group) |

## 5. API and events

Artifact `openapi` = `src/NightTales.Api/openapi.json` (D14), consumed later by the 10 client generation and the 09.9.5 diff.

## 6. UI

None (backend only).

## 7. Jobs and integrations

Two jobs: `build-test` (no database, no Docker) and `integration` (a runner-provided `postgres:17` service container, `NTS_TEST_PG` set; needs `build-test`) — 09 W3 CR-35. Details in T1.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Secrets exposure in PRs from forks | no secrets are used by these jobs; `permissions: contents: read` |
| Supply-chain drift | locked restore (`--locked-mode`); actions pinned by commit SHA [DECISION] |
| Test data leaking | the CI database lives in a throwaway `postgres:17` service container, and each test class drops its own database (`DROP DATABASE … WITH (FORCE)`; 09 W3 CR-35) |

## 9. Test plan

The workflow itself is the check. It is proven once with a deliberate formatting error and a failing test on a scratch PR (both must turn the run red).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.1.5-T1 | Backend CI workflow | S (0.5) | 09.1.2-T1, 09.2.1-T2 | 09.2.2-T2 (adds the migration job), gate `09-skeleton`, 11.5.1-T1, 11.5.1-T2 (11 CR-11) |

Total **0.5 d** (unchanged).

## 11. Rollout

- Enable branch protection on `main` (required checks: `build-test`, `integration`) once the remote exists (owner action).
- Rollback: disable the workflow file; nothing else depends on it.
