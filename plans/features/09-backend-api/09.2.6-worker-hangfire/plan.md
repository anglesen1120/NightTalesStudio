# 09.2.6 — Worker host, Hangfire, `IJobScheduler`, recurring jobs, dashboard

> Refines: `../../../09-backend-api.md` §4 (epic 09.2, story **09.2.6**, L104–109), §2 T8, §5.3, §8 (Hangfire licence risk).
> Contract: `../README.md` — **N5**, **N6**, **N14**, **F09**, **D1**, **D10**, **D12**, **D13**, **O1**; CR-11.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: 09.2.6-T1 **implemented 2026-09-30** (PR #46); T2 waits for 09.10.4-T1 + 09.3.6-T1; T3 waits for 09.2.4-T1.

## 1. Scope

**Story.** 09.2.6 — "As the platform I want one Worker composition root with a job scheduler so that the outbox, render and billing jobs have somewhere to run." Must · detailed **1.5 d** (T1 M 1.0 + T2 S 0.5).

**In scope.**
- T1:
  - the Worker host (Serilog, Mediator Scoped, persistence, the outbox dispatcher hosting point, health);
  - the Hangfire server on PostgreSQL storage (schema `hangfire`) with the four queues and configured worker counts;
  - the `IJobScheduler` adapter, `[JobPolicy]` translation and recurring-job registration with stale-id removal;
  - the Hangfire.PostgreSql × Npgsql 10 compatibility spike.
- T2: the dashboard at `/internal/jobs` (after 09.10.4-T1 and 09.3.6-T1).

**Out of scope.**
- Individual jobs (02, 09.6, 12).
- The ids-only architecture rule (09.1.2-T1).
- A `fetch` queue: there is none (02 CR-1).

**Split** (F09, CR-11): the Worker no longer waits for MFA work.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| H1 | Hangfire.Core/AspNetCore 1.8.25, LGPL-3.0 or commercial; `Enqueue<T>(queue, expr)` overloads exist | README N5 ✅ |
| H2 | Hangfire.PostgreSql 1.21.1, LGPLv3, `netstandard2.0`, `Npgsql ≥ 6.0.11`; runtime compatibility with Npgsql 10 ⚠️ | README N6 |
| H3 | Hangfire passes a `CancellationToken` parameter by substituting `CancellationToken.None` in the expression with the server's shutdown token | Hangfire docs "Using cancellation tokens" ⚠️ |
| H4 | The Api never enqueues directly: side effects go through the outbox, and outbox handlers run in the Worker (00 §5.2) | README D11 ✅ |

## 3. Domain and data

No domain tables. Hangfire creates and upgrades its own schema `hangfire` (`PrepareSchemaIfNecessary = true` in Development and tests; in production a separate deploy/migration role installs the schema before rollout and the Worker runs with `PrepareSchemaIfNecessary = false` and no DDL rights (O2, owner 2026-09-30) [DECISION]; the earlier plan had the Worker install it at start ⚠️ 2 Workers: the 1.21.1 installer runs in one Serializable transaction and tolerates a concurrent install rather than locking — read from source, not load-tested; T1 Notes).

Types:

| Type | Path |
|---|---|
| `IJobScheduler`, `IBackgroundJob`, `JobPolicyAttribute`, `JobQueue`, `IRecurringJobRegistration` | `Application/Abstractions/Jobs/` |
| `HangfireJobScheduler` | `Infrastructure/Jobs/` |
| `JobPolicyFilterProvider` | `Infrastructure/Jobs/` |
| `RecurringJobRegistrar : IHostedService` | `Infrastructure/Jobs/` (Worker only) |
| `JobsServiceCollectionExtensions` (`AddJobs(IConfiguration)`) | `Infrastructure/Jobs/` |
| `InternalJobsDashboard` (T2) | `Api/Internal/Jobs/` |

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | An outbox handler enqueues `GenerateStoryJob(runId)` | the job runs on its `[JobPolicy]` queue in the Worker, in a new DI scope |
| UC2 | A job throws with `Retries = 0` | it ends `Failed`, with no retry |
| UC3 | The Worker starts | recurring jobs are upserted by id; ids no longer registered are removed |
| UC4 | An owner opens `/internal/jobs` (T2) | after password + TOTP cookie: write actions; admin: read-only; others: 404 |

## 5. API and events

T2 only: `/internal/jobs` (not in the public OpenAPI). No events.

## 6. UI

The Hangfire dashboard is third-party UI, hosted as is (T2). No Angular work.

## 7. Jobs and integrations

- Queues: `default`, `render`, `publish`, `email`. Worker counts come from `Jobs:Workers:<queue>` (defaults 4 / 2 / 1 / 1 [DECISION]).
- A job's scope: Hangfire's ASP.NET Core activator creates one DI scope per job. The job itself calls `ISystemContext.For(userId)` when it touches user data, with the user id taken from its run or aggregate (02 pattern).
- Health: the Worker exposes `/health/live` and `/health/ready` on `Worker:HealthPort` (8082) [DECISION]; `ready` checks the DB and the Hangfire storage.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Content or secrets in job arguments (stored in plain text in `hangfire.job`) | ids-only rule (09.1.2-T1) |
| Dashboard exposure | T2: separate cookie after password + TOTP, role checks, antiforgery, 404 for everyone else, audit rows, IP restriction |
| A job running as the wrong tenant | jobs start with no user; `For(userId)` is explicit (D10) |
| Licence | O1: LGPL-3.0, unmodified package references only |

## 9. Test plan

T1 integration tests with a real Hangfire server on the test database (short `SchedulePollingInterval`, `WorkerCount = 1`): enqueue → run; `Retries = 0` failure; queue routing; recurring registration idempotent plus stale removal; the Npgsql 10 spike. T2: auth matrix.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.2.6-T1 | Worker host, Hangfire server/storage, `IJobScheduler`, `[JobPolicy]`, recurring registration | M (1.0) | 09.2.1-T1 | 09.2.4-T1, 09.1.3-T2 (cleanup job registration), every 02 job, 09.10.1-T2 (09 W3 CR-17) |
| 09.2.6-T2 | `/internal/jobs` dashboard auth | S (0.5) | 09.2.6-T1, 09.10.4-T1, 09.3.6-T1 | — |
| 09.2.6-T3 | E-mail pipeline: `IEmailSender`, outbox `EmailRequested`, `SendEmailJob` (queue `email`, 5 retries), templates, `email_messages`, `IEmailDeliveryStatus` | M (1.0) | 09.2.6-T1, 09.2.4-T1 | 09.3.3-T1, 09.3.1-T3, 02.8.2-T1, 02.8.3-T4, 06.1.2-T2, 06.2.2-T2 (06 CR-8), 09.3.3-T3, 09.3.7-T1, 09.10.8-T1 (09 W3 CR-17), 12.6.3-T1, 12.5.1-T2, 07.1.2-T2, 07.2.2-T2 |

Total **1.5 d** (high-level M 1.0; +0.5 because the dashboard is split out, README §9).

09.2.6-T3 is a wave-2 addition to this signed folder `(09 W2 CR-7)` (its 1.0 d is carried by the wave-2 re-estimate, `../README.md` B9/BD9, not by the 1.5 d total above).

### Open items for the TL

- O1 (owner): Hangfire licence acceptance. If rejected, `IJobScheduler` is re-implemented on another C# library (≈ 2 d, 09 §8); this plan's port and tests stay.
- H2 resolved (2026-09-30): the spike PASSED on Hangfire.PostgreSql 1.21.1 + Npgsql 10 (T1 Notes); no pin change, no separate `NpgsqlDataSource`.
- O2 (owner, before the first production deploy — 11.1.1-T1): the earlier design had the Worker install the `hangfire` schema at start, which needs DDL rights for its long-lived runtime role. Alternative: a separate deploy/migration role installs the schema and production runs with `PrepareSchemaIfNecessary = false`. Local dev is unaffected (security review 09.2.6-T1, SEC-03). **Decided (owner 2026-09-30): the separate deploy/migration role.** Production runs with `PrepareSchemaIfNecessary = false` and the Worker role has no DDL rights; 11.1.1-T1 wires the schema install into the deploy step.

## 11. Rollout

- T1 before any job-owning feature. T2 when 09.10.4-T1 and 09.3.6-T1 exist; until then there is no dashboard (operators use the logs and metrics).
- Rollback: stopping the Worker pauses all jobs; Hangfire state persists in `hangfire`.
