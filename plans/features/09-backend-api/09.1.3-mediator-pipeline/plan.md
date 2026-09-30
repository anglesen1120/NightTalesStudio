# 09.1.3 — Mediator pipeline

> Refines: `../../../09-backend-api.md` §4 (epic 09.1, story **09.1.3**, L66–69), §5.1, §7; `../../../00-overview.md` §5.2 CQRS rules.
> Contract: `../README.md` — **N1**, **N2**, **F03**, **F04**, **F05**, **F13**, **D4**, **D5**, **D6**, **D7**, **D13**.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: 09.1.3-T1 + 09.1.3-T2 **implemented 2026-09-30** (T2 in PR #54).

## 1. Scope

**Story.** 09.1.3 *Mediator pipeline* — "As a developer I want cross-cutting behaviours in one place so that handlers stay small." Must · detailed **1.5 d** (T1 S 0.5 + T2 M 1.0).

**In scope.**
- Mediator 3.0.2 configuration (Scoped lifetime).
- The single ordered behaviour list `MediatorPipeline.Behaviors`.
- `LoggingBehavior`, `AuthorizationBehavior`, `ValidationBehavior`, `UnitOfWorkBehavior`, `IdempotencyBehavior`.
- The reserved `AuditBehavior` slot.
- `ErrorOrFactory<TResponse>`.
- The markers `IIdempotentCommand`, `IRequireRole`, `[AllowAnonymousRequest]`.
- The `idempotency_keys` protocol and `IdempotencyKeyCleanupJob`.

**Out of scope.** The `AuditBehavior` implementation and `audit_logs` (09.3.6-T1). ProblemDetails mapping and endpoint filters (09.1.4-T1). JWT authentication itself (09.3.2); here `ICurrentUser` comes from 09.2.3-T1.

**Changes the high-level plan (CR-2).** The order becomes `Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit`, and UnitOfWork opens the transaction **before** the handler (F04, F05).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| P1 | `IPipelineBehavior<TMessage,TResponse>.Handle(TMessage, MessageHandlerDelegate<TMessage,TResponse> next, CancellationToken)` returns `ValueTask<TResponse>` | README N1 ✅ |
| P2 | `PipelineBehaviors` is an ordered array, configured at compile time; the default lifetime is Singleton | README N2 ✅ |
| P3 | Behaviours can be constrained to message subsets with generic constraints (`where TMessage : ICommand<TResponse>`) | Mediator README §4.5 ✅ |
| P4 | PostgreSQL: an `INSERT … ON CONFLICT DO NOTHING` that meets an uncommitted conflicting row waits for that transaction, then either skips (commit) or inserts (rollback) | PostgreSQL docs "INSERT … ON CONFLICT" ⚠️ (behaviour known, page not re-read) |
| P5 | Transaction-scoped advisory locks (`pg_advisory_xact_lock`) are released at commit/rollback | PostgreSQL docs "Advisory Lock Functions" ⚠️ |

## 3. Domain and data

`idempotency_keys` (created by `InitialCore`, 09.2.2-T1; D6, CR-4):

| Column | Type | Null | Constraint |
|---|---|---|---|
| `id` | uuid | no | PK |
| `user_id` | uuid | no | FK `users` RESTRICT |
| `key` | varchar(128) | no | printable ASCII (checked at the endpoint) |
| `request_hash` | char(64) | no | SHA-256 hex |
| `response_type` | text | yes | CLR full name of the success value; null while in flight |
| `response` | jsonb | yes | null while in flight |
| `created_at` | timestamptz | no | |
| `expires_at` | timestamptz | no | `created_at + 24 h` |

Indexes: unique `(user_id, key)`; `(expires_at)`.

Types (`src/NightTales.Application/`):
- `Abstractions/Messaging/{IIdempotentCommand,IRequireRole,AllowAnonymousRequestAttribute}.cs`
- `Abstractions/Idempotency/IIdempotencyStore.cs`
- `Behaviors/{MediatorPipeline,LoggingBehavior,AuthorizationBehavior,ValidationBehavior,UnitOfWorkBehavior,IdempotencyBehavior}.cs`
- `Common/Errors/ErrorOrFactory.cs`

Infrastructure: `Persistence/Idempotency/IdempotencyStore.cs`, `Persistence/UnitOfWork/EfUnitOfWork.cs` (the transaction port `IUnitOfWork`, D5).

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | Any request | the log line starts, then the authorization check, then validation; the first failure short-circuits with `ErrorOr` errors |
| UC2 | A command succeeds | the transaction opens → the handler runs → `SaveChangesAsync` (the outbox interceptor adds rows) → commit |
| UC3 | A command returns an error | rollback; nothing is persisted, including outbox and idempotency rows |
| UC4 | An idempotent command is replayed | the stored success value is returned, and the handler does not run |
| UC5 | Two identical requests race | one executes; the other waits on the unique index and then replays |
| UC6 | Same key, different body | 422 `idempotency_key_reused` |
| UC7 | A lost update between load and save | `DbUpdateConcurrencyException` → 409 `concurrency_conflict` |

## 5. API and events

No routes. `LoggingBehavior` emits the structured log `mediator.request` (`message`, `durationMs`, `outcome` = `ok` or the first error code, `traceId`). No domain events.

## 6. UI

None (backend only).

## 7. Jobs and integrations

- `IdempotencyKeyCleanupJob` (Application `Features/Platform/Jobs/`), recurring daily at 04:00 UTC (id `idempotency-key-cleanup`), registered through `IRecurringJobRegistration` once 09.2.6-T1 exists; it deletes rows with `expires_at < now − 1 d` in batches of 1,000.
- No external calls.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Double spend on retry or crash | idempotency row in the same transaction as the effect (D6) |
| Key replay across users | uniqueness per `(user_id, key)`; a replay requires the same authenticated user |
| Response leaking content | responses store only small acknowledgements (ids, ETags; 00 §5.2 command results) |
| Logging content | `LoggingBehavior` logs the type name and outcome only, never message properties |
| Unauthenticated access | `AuthorizationBehavior` denies by default; `[AllowAnonymousRequest]` is an explicit opt-out |

## 9. Test plan

- Unit (Application): behaviour order through a recording probe; validation short-circuit; query skips UnitOfWork; `ErrorOrFactory` conversion for `ErrorOr<Guid>` and `ErrorOr<Success>`.
- Integration (PostgreSQL, D13): commit/rollback; concurrency mapping; advisory lock held until commit; idempotency replay, race, reused key, expiry, and a crash between `SaveChanges` and commit.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.1.3-T1 | Mediator setup, `MediatorPipeline`, Logging + Validation behaviours, `ErrorOrFactory`, markers | S (0.5) | 09.1.1-T1 | 09.1.3-T2, 09.1.4-T1 |
| 09.1.3-T2 | UnitOfWork (explicit transaction) + Idempotency (in-transaction) + Authorization behaviours, cleanup job | M (1.0) | 09.1.3-T1, 09.2.1-T2, 09.2.2-T2, 09.2.3-T1, 09.2.1-T1, 09.2.6-T1 soft (cleanup job registration) (09 W2 CR-9) | 09.4.1-T1, 09.5.1-T1, every 02 command, 09.3.6-T1 (09 W2 CR-9) |

Total **1.5 d** (high-level 1.0; +0.5 for F04/D5/D6, README §9).

## 11. Rollout

- No flag. Ships before the first feature command (09.4.1-T1).
- 02 handlers rely on D5: raw locks join the UnitOfWork transaction; handlers never call `SaveChangesAsync`.
- Rollback: revert the behaviour registration; no data migration beyond `idempotency_keys` (created by `InitialCore`).
