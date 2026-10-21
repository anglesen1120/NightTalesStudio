# 09.2.4 — Outbox

> Refines: `../../../09-backend-api.md` §4 (epic 09.2, story **09.2.4**, L97–99), §2 T2, §5.3 (the `OutboxDispatcher` row, renamed by CR-3), §8 (outbox lag); `../../../00-overview.md` §5.2 CQRS rule (side effects via the outbox).
> Contract: `../README.md` — **F06**, **F07**, **D3**, **D5**, **D10**, **D11**, **D12**, **D13**; CR-3, CR-4.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: task **implemented 2026-09-30** (09.2.4-T1, branch `feature/09.2.4-T1-outbox`). Security-relevant (tenant context in the Worker): second reader required.

## 1. Scope

**Story.** 09.2.4 — "As the platform I want domain events stored with state changes so that jobs are never lost or duplicated." Must · detailed **L 2.0** (high-level M 1.0; +1.0 for F06/F07).

**In scope.**
- `OutboxSaveChangesInterceptor`.
- The event type registry built from `[OutboxEvent]` names.
- Serialization.
- The Worker `OutboxDispatcher` (claim, lease, per-message scope under `ISystemContext`, backoff, dead letter, graceful stop).
- `IOutboxHandler<TEvent>` registration.
- Trace continuation.
- The metrics `outbox_lag_seconds` and `outbox_dead_letters_total`.
- The processed-row cleanup.

**Out of scope.** Individual handlers (their features). SignalR notification from events (09.8.1). E-mail (`SendEmailJob`, 09.3).

**Changes the high-level plan.** The dispatcher is a `BackgroundService`, not a Hangfire recurring job (F06, CR-3), and `outbox_messages` gains four columns (F07, CR-4).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| O1 | Hangfire recurring jobs are cron-based and polled (default 15 s) — too coarse for a 5 s dispatcher | README N14 ⚠️ |
| O2 | `SELECT … FOR UPDATE SKIP LOCKED` lets several dispatchers claim disjoint rows | PostgreSQL docs "The Locking Clause" ⚠️ (well known, not re-read) |
| O3 | `SaveChangesInterceptor.SavingChangesAsync` runs inside `SaveChanges`, before the database commands, so added entities are saved in the same batch | EF Core docs "Interceptors" ⚠️ |
| O4 | 02 handlers rely on at-least-once delivery with their own idempotency keys (for example the bible key `bible:{episodeId}:{storyVersionId}`) | 02.6.3-T2 ✅ |

## 3. Domain and data

Table `outbox_messages` (created by `InitialCore`, 09.2.2 plan §3; D11).

Types:

| Type | Path |
|---|---|
| `OutboxEventAttribute(string name)`, `PlatformEventAttribute` | `Domain/Common/` (pure attributes) |
| `IOutboxHandler<TEvent>`, `OutboxContext` | `Application/Abstractions/Outbox/` |
| `OutboxHandlerRegistration` (`AddOutboxHandler<TEvent, THandler>()`) | `Application/Abstractions/Outbox/OutboxServiceCollectionExtensions.cs` |
| `OutboxSaveChangesInterceptor` | `Infrastructure/Persistence/Outbox/` |
| `OutboxEventRegistry` (name ↔ type, built once at start from the Domain assembly) | `Infrastructure/Persistence/Outbox/` |
| `OutboxDispatcher : BackgroundService`, `OutboxDispatcherOptions` | `Infrastructure/Persistence/Outbox/` (hosted only by the Worker) |
| `OutboxCleanupJob` (daily, deletes `processed_at < now − 7 d`) | `Application/Features/Platform/Jobs/` |

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | A command raises `EpisodeCancelled` | the row is written in the command transaction (UnitOfWork D5) |
| UC2 | The command rolls back | no row |
| UC3 | The dispatcher runs | claims ≤ 20 due rows, handles each in its own scope under `For(user_id)`, marks it processed |
| UC4 | A handler throws | `attempts++`, backoff `min(2^attempts s, 5 min)`; after 10 attempts `failed_at`, metric, log `Error` |
| UC5 | The Worker crashes mid-message | the lease (`next_attempt_at` bumped by 5 min at claim) expires; the row is claimed again and handlers run again (at-least-once) |
| UC6 | An unknown `type` or a missing `user_id` on a non-platform event | dead letter at once, and no handler runs |

## 5. API and events

No routes. Event wire format (`payload`): System.Text.Json, camelCase, enums as snake_case strings, event record properties only. Event names follow `^[a-z_]+\.v[0-9]+$`. A breaking payload change gets a new version name, and both are handled during one release.

## 6. UI

None (backend only).

## 7. Jobs and integrations

| Item | Detail |
|---|---|
| `OutboxDispatcher` | Worker `BackgroundService`; `Outbox:PollIntervalSeconds` = 5, `Outbox:BatchSize` = 20, `Outbox:LeaseMinutes` = 5, `Outbox:MaxAttempts` = 10 |
| Wake-up | [DECISION] polling only in P; `LISTEN outbox` is a later optimisation if `outbox_lag_seconds` p95 > 10 s |
| `OutboxCleanupJob` | recurring daily 04:30 UTC (`outbox-cleanup`), batches of 1,000 |
| Metrics | `outbox_lag_seconds` (gauge: now − oldest due `occurred_at`), `outbox_dead_letters_total` (counter), `outbox_handled_total{type,outcome}` |
| Alert | lag > 30 s (09 §8); owned by 09.9.3 / 11.6 |

## 8. Security and privacy

| Threat | Control |
|---|---|
| A handler for A reads B's data | per-message scope + `ISystemContext.For(user_id)` before any context is resolved (09.2.3-T1 guard) |
| A payload without `user_id` runs unfiltered | refused → dead letter (UC6) |
| Content in payloads | events carry ids, enums and small values only (D3); content is loaded by id inside the handler |
| Poison message blocking the queue | per-row backoff and dead letter; other rows keep flowing (ordering is best-effort, not per-aggregate [DECISION]) |

## 9. Test plan

Integration tests through `WorkerHost.RunOutboxOnceAsync` with `FakeClock`, no sleeps: atomicity, rollback, at-least-once after a crash, concurrent dispatchers, backoff and dead letter, missing user, tenant isolation, trace continuation.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.2.4-T1 | Outbox interceptor, registry, `OutboxDispatcher`, handlers, metrics, cleanup | L (2.0) | 09.2.2-T1, 09.2.3-T1, 09.2.6-T1 | 09.5.1-T1, every 02 outbox handler, 09.8.1-T1, 09.3.6-T1, 09.2.6-T3 (09 W2 CR-9) |

Total **2.0 d** (+1.0, README §9).

## 11. Rollout

- Worker only. The Api writes rows but never dispatches.
- Ordering note: events of one aggregate may be handled out of order when one of them fails and backs off. Handlers must not assume order; they re-read the current state by id.
- Rollback: stopping the Worker stops dispatch; rows accumulate and drain when it restarts.
