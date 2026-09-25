# 12.2 — Credits ledger: reserve, settle, release, refund, balance and reconciliation (S)

> Contract: [`../README.md`](../README.md) — facts C1–C16, findings F01–F26, decisions D1–D18, rulings BR1–BR32, canonical rows §8.1.
> Phase **S · M2** for all four tasks (D1: no P build of the ledger; "M1" in `../../12-billing-credits.md` reads **M2**, 00 §4.1). This folder is the **only** place that moves credits; it replaces 02's P `MeteringOnlyCreditLedger` with `CreditService` (D1/BR3).
> Legend: ✅ source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope.** The append-only ledger and every credit movement: `reserve`, `settle` (actual, read from `usage_records`), `release` (+R, the whole hold — written only together with `settle`, same transaction; BR18: the difference is a derived DTO value, never a row amount), `refund`, lot consumption in spend order, the `credit_accounts` row lock that serialises all of it (D2), the balance + history read model and its two routes (12.2.2), `CreditReconciliationJob` (12.2.3), and the reserve/settle/refund wiring plus the `[PaidOperation]` architecture test (12.2.4).

**Out of scope — owned elsewhere, never re-implemented.** Lot creation/expiry/voiding and full spend-order semantics: 12.6.1-T1/12.6.2-T1. Formula, `price_versions`, floors: 12.1.2-T1/12.1.4-T1 (`CreditCalculator` is called; no rate arithmetic here). Per-user spend cap: 12.4.1-T1 (runs before the reserve, same command). Provider port, checkout, webhook: 12.3.2-T1/12.3.3-T1 (same movement path, keys `purchase:{paymentId}`/`bonus:{paymentId}`). Admin grants, minting cap, resolve: 12.5.1-T1/12.3.5-T1 (through `GrantAsync`). Funding pause, resume, 12 h cancel: 12.8.3-T2 (sweeps skip held rows; resume uses `GetOpenReservationAsync`). Money screens: 10.8.x (the UI design task lives there, §6).

**Non-goals.** No cached balance column (D6), no direct-SQL adjustment path (F04), no session-level advisory lock (F02), no vendor SDK (F18), no PDF library (F19).

## 2. Confirmed facts

| # | Fact | Legend |
|---|---|---|
| C1/C2/C4 | 00 §6 is the column authority (read directly for this plan, L262 + L294–320): `credit_accounts` (`user_id` PK, `updated_at`); `credit_ledger` (`user_id, lot_id, kind, amount` signed bigint, `price_version, reference_type, reference_id, idempotency_key` unique, `created_by`); `credit_lots` (`user_id, source, amount, remaining, expires_at, payment_id, voided_at`); `usage_records` (`reservation_id, operation, image_count, gpu_seconds, estimated, cost_usd`); `numeric(12,6)` USD, `bigint` credits, UUIDv7, `timestamptz`, `xmin`. `GET /credits/balance` + `GET /credits/ledger` are in 00 §8 L365 | ✅ |
| C5/C8/C9 | 02's port `ICreditLedger` (02 CR-2): `ReserveAsync(userId, CreditOperation, referenceId, UsageBound, ct)`, `SettleAsync(reservationId, ct)`, `RefundAsync(reservationId, reason, ct)`; PascalCase `CreditOperation` members ↔ snake_case wire values. `IUsageMeter.RecordAsync(UsageItem)` (02 D14) writes one `usage_records` row per call in its **own** transaction (operations incl. `story_review`, `story_revision`, `bible_proposal`, `idea`, `source_find`, `moderation`). `generation_runs.pause_reason = provider_funds_exhausted` exists (P) | ✅ |
| C10–C13 | 09: `[JobPolicy(Queue = JobQueue.Default, Retries = 0)]` when absent, `ISystemWorkQuery.PendingAsyncT` (pinned, never extended) + per-row `ISystemContext.For`, `UnitOfWorkBehavior` the only `SaveChangesAsync` caller, exactly one command attribute (09.3.6-T1), `outbox_messages`, `ErrorCodes.cs` + `ErrorCatalogue.cs`, ids-only job args, one command per row through `ISender`, delayed-job tests = recording `IJobScheduler` fake + `RunAsync` (06 YR26/YR27). One alert pipeline only: 11.6.2-T1 | ✅ |
| D2/D6 | 7-member port (BR29 split: 12.2.1-T1 declares+implements `ReserveAsync`/`SettleAsync`/`RefundAsync`/`GrantAsync`/`GetOpenReservationAsync`; 12.6.1-T1 adds `ExpireAsync`/`VoidPaymentLotsAsync`); account row lock inside the command transaction; global lock order `credit_accounts` → `promo_codes` → `payments` → `credit_lots`; advisory locks transaction-scoped **only**; the idempotency-key list (lot-scoped, BR19); `open → settled \| refunded` once; per-operation stale margins; `reservation_missing` = metric + alert, no movement; `CHECK (remaining >= 0 AND remaining <= amount)` + `CHECK (expires_at > created_at)`; `source_rank` `trial 0 · promo 1 · bonus 2 · adjust 3 · purchase 4` (Domain map, **no new column**); one ledger row **per lot** with `lot_id`; release/refund return to the same lots and re-remove what died meanwhile | [DECISION] |
| D7/D10/D17 | `CreditReconciliationJob` daily 04:00, retries 1, `JobQueue.Default`, `[DisableConcurrentExecution]`; `CreditOptions` `StaleReserveHoursStory` 24, `StaleReserveHoursRender` 72, `MaxCreditsPerUserPerDay` 5000, `TrialExpiryDays` 30, `PurchasedExpiryMonths` 12, `FxUsdVndFallback`; outbox `credit_balance_changed.v1 { userId }`; metrics `billing_reservation_missing_total`, `billing_balance_liability_credits`, `billing_provider_loss_total`; indexes as §3 | [DECISION] |
| F02/F08/F10/§3.1 | Held rows never swept; per-operation margins with terminal owners only; tenant-filtered sweep reads resolve a user id first; **no** session-scoped `pg_advisory_lock` in 12 (EF pools connections per command); `CreditBalanceDto`/`CreditLedgerEntryDto` field lists frozen | [DECISION] |

**Findings and rulings applied here (BR18–BR21).** §2 carries **F24** (lot-scoped ledger keys), **F25** (settle snapshots `usage_records` at `settled_at`; later rows are `credit_ledger (reference_type = 'platform_loss')`, reported as `platformLossUsd`, never re-charged — BR30) and **F26** (the render hold is **derived**: job `queued` ∧ `provider_funds_exhausted` open ∧ `GetOpenReservationAsync` returns an open reservation; `Credit:HoldCancelHours` = 12; **no** new `render_jobs` column). Sign convention and keys: **BR18/BR19**; port-member split: **BR29**; migration ownership (**BR20**): `_12_CreditAccounts` + `_12_CreditLedger` + `_12_CreditLots` (incl. `credit_accounts.review_required_at`/`review_note`, CR-14) are written by 12.2.1-T1 only, while `_12_CreditGrantRequests` belongs to **12.5.1-T1** and is never created here.

## 3. Domain and data

| Aggregate / type | Table | Notes |
|---|---|---|
| `CreditAccount` | `credit_accounts` | lock row only, created lazily on the first grant; **F10 decision this folder owns:** mapped **outside** the tenant filter with a code comment; a test asserts a foreign user gets 404 on a credit route |
| `CreditLot` | `credit_lots` | 00 §6 columns + the two `CHECK`s; `source_rank` is a Domain map over `source` |
| `CreditLedgerEntry` | `credit_ledger` | append-only; `amount` signed; `kind` ∈ `grant`/`bonus`/`purchase`/`reserve`/`settle`/`release`/`refund`/`adjust`/`expire`; unique `idempotency_key` |
| `CreditReservation` | derived from the ledger family | `open → settled \| refunded` once; the reservation id **is** the `reserve` row's `id` |
| `LotConsumption` / `ReservationSnapshot` | value objects | `LotConsumption(LotId LotId, long Amount)`; `ReservationSnapshot(ReservationId Id, Guid UserId, CreditOperation Operation, long Amount, PriceVersionVersion PriceVersion, ReservationState State, IReadOnlyList<LotConsumption> Lots)` |

**Row shapes (one `credit_ledger` row per consumed lot, BR19).** reserve: `reference_type` = the `CreditOperation` wire value, `reference_id` = owning entity id, `amount` = `−R` on that lot (BR18), `price_version` = active version, `lot_id` = the consumed lot, key `reserve:{referenceId}:{lotId}`. Family rows `settle`/`release`/`refund`: `reference_type='reservation'`, `reference_id = reservationId`, `lot_id` = consumed lot, keys `settle:{reservationId}:{lotId}`/`release:{reservationId}:{lotId}`/`refund:{reservationId}:{lotId}`. History `groupId` = `id` for reserve rows, `reference_id` otherwise. Balance is **always** derived (`SUM(credit_ledger.amount)`).

**Sign convention (BR18).** `credit_ledger.amount` is signed from the user's balance view: `reserve −R`, `settle −C` (`C = min(R, max(floor, credits(actual)))`), `release +R` (the WHOLE hold, exactly, written only together with `settle`, same transaction), `refund +R` with no settle row, `grant/bonus/purchase +`, `expire −remaining`, `adjust ±`; per reservation `SUM(amount)` = −R open / −C settled / 0 refunded; DTO values derived per reservation group (`quoted = Σ|reserve|`, `charged = Σ|settle|`, `returned = quoted − charged`, `= quoted` when refunded).

**Locking** — inside the command transaction, before any lot or ledger write. This folder takes the first and last locks of the pinned order; `pg_advisory_xact_lock(hashtext('budget_guard'))` stays 02's and `pg_try_advisory_xact_lock(hashtext('nts:credit_mint:' || @actorId))` is 12.5.1-T1's; **no `pg_advisory_lock` anywhere in 12**.

```sql
INSERT INTO credit_accounts (user_id) VALUES (@userId) ON CONFLICT (user_id) DO NOTHING;
SELECT user_id FROM credit_accounts WHERE user_id = @userId FOR UPDATE;      -- the serialisation point
```

**Idempotency keys** (unique index; every insert `ON CONFLICT (idempotency_key) DO NOTHING`): `reserve:{referenceId}:{lotId}`, `settle:{reservationId}:{lotId}`, `release:{reservationId}:{lotId}`, `refund:{reservationId}:{lotId}`, `grant:trial:{userId}`, `grant:adjust:{grantRequestId}`, `grant:promo:{promoRedemptionId}`, `purchase:{paymentId}`, `bonus:{paymentId}`, `expire:{lotId}:{reservationId?}`, `void:{paymentId}:{lotId}`.

**Spend order and the indexes this folder ships.**

```sql
SELECT l.id, l.source, l.remaining, l.expires_at
FROM credit_lots l
WHERE l.user_id = @userId AND l.voided_at IS NULL AND l.expires_at > now() AND l.remaining > 0
ORDER BY l.expires_at ASC,
         CASE l.source WHEN 'trial' THEN 0 WHEN 'promo' THEN 1 WHEN 'bonus' THEN 2
                       WHEN 'adjust' THEN 3 WHEN 'purchase' THEN 4 END ASC,
         l.id ASC
FOR UPDATE;

CREATE INDEX ix_credit_ledger_user_created ON credit_ledger (user_id, created_at DESC, id DESC);
CREATE INDEX ix_credit_ledger_reference    ON credit_ledger (reference_type, reference_id);
CREATE UNIQUE INDEX ux_credit_ledger_idem  ON credit_ledger (idempotency_key);
CREATE INDEX ix_credit_lots_user_expiry    ON credit_lots (user_id, expires_at) WHERE voided_at IS NULL;
CREATE INDEX ix_credit_lots_payment        ON credit_lots (payment_id);
CREATE INDEX ix_usage_records_reservation  ON usage_records (reservation_id);
```

**Invariants.** (1) `SUM(credit_ledger.amount) ≥ 0` per user. (2) `SUM(credit_ledger.amount) = SUM(credit_lots.remaining)` over `voided_at IS NULL` lots, checked at 04:00 **after** `CreditExpiryJob` (12.6.2-T1, 03:00) zeroed expired lots. (3) Negative movements go only through `ICreditLedger`. (4) A reservation settles or refunds once. (5) The reserve happens inside the paid command's transaction, before any provider call.

## 4. Use cases

| # | Given / When / Then | Errors |
|---|---|---|
| UC-1 Reserve | **Given** a paid command, **When** `ReserveAsync` runs in its transaction, **Then** it locks the account row, computes `reserve = max(floor[operation][tier], Credits(CostBoundUsd(bound)))` via `CreditCalculator`, checks `balance ≥ reserve`, inserts the `reserve` row and returns its `ReservationId` | `insufficient_credits` 402; `spend_cap_reached` 429 (12.4.1-T1, before the reserve) |
| UC-2 Settle | **Given** an open reservation, **When** `SettleAsync(reservationId, ct)` runs, **Then** it reads `usage_records` **by `reservation_id`**, computes `charged = min(reserve, max(floor, Credits(Cost(actual))))`, writes one `settle` + one `release` row per lot, returns `SettleResult(Charged, Released)` | `reservation_missing` (metric + alert, no movement) |
| UC-3 Release | **Given** `charged < reserve`, **Then** the difference returns to the **same** `lot_id`s; a lot expired or voided meanwhile is re-removed (`expire:{lotId}:{reservationId}` / `void:{paymentId}:{lotId}`) | — |
| UC-4 Refund | **Given** a failed/cancelled job, **When** `RefundAsync(reservationId, reason, ct)` runs, **Then** the full reserve returns (`kind='refund'`), the reservation closes `refunded`, and a later settle/refund is `reservation_missing` | `reservation_missing` |
| UC-5 Over-run / late usage | **Given** `Credits(Cost(actual)) > reserve`, **Then** the charge is capped at the reserve and the gap goes to `billing_provider_loss_total` + alert; settle snapshots the reservation’s `usage_records` at `settled_at` and a later row is attributed to `credit_ledger (reference_type = 'platform_loss')` (reported as `platformLossUsd`, F25/BR30), never re-charged | — |
| UC-6 Balance | **Given** `GET /credits/balance`, **Then** total, free vs purchased and next expiry come from live lots; `GetBalanceQuery { UserId }` also serves 09.10.5-T1's admin bundle — no second read port | 401 |
| UC-7 History | **Given** `GET /credits/ledger?cursor=&limit=`, **Then** one item per `groupId` with `quoted`/`charged`/`released` and the `usage` block; keyset cursor `(created_at DESC, id DESC)`, `limit` default 50 / max 100 | 400 invalid cursor |
| UC-8 Reconciliation | **Given** 04:00 server-local, **When** `CreditReconciliationJob` runs, **Then** stale **terminal** reservations are refunded per operation margin, held/paused and non-terminal rows are skipped, and negative balances, lot/ledger drift and failed `payment_events` alert | — |
| UC-9 Held rows | **Given** `pause_reason = provider_funds_exhausted` or a non-terminal render job, **Then** the sweep leaves the reservation alone, and resume dispatch re-checks with `GetOpenReservationAsync`, cancelling instead of dispatching; the render hold is **derived** (`queued` ∧ flag open ∧ open reservation, `Credit:HoldCancelHours` = 12 — BR29/F26), **no** `render_jobs` column | — |
| UC-10 Paid wiring | **Given** `GenerateStoryCommand`, `GenerateFactSheetCommand`, the `bible_proposal`/`idea`/`source_find` handlers and the render command behind `POST /episodes/{id}/render` (09.6), **Then** each reserves in its own transaction and its job settles or refunds; a paused platform returns **202** with the reserve held and `processingPaused = provider_funds_exhausted` | `insufficient_credits` 402 |

## 5. API and events

| Route | Verb | Response (frozen, §3.1) | Notes |
|---|---|---|---|
| `/credits/balance` | GET | `CreditBalanceDto { total, free, purchased, nextExpiry: { amount, expiresAt } \| null }` | header widget; **always on** (not behind `billing_enabled`); no query parameter |
| `/credits/ledger` | GET | `{ items: CreditLedgerEntryDto[], nextCursor }`; `CreditLedgerEntryDto { id, createdAt, kind, amount, operation, episodeId, title, groupId, quoted, charged, released, usage: { inputTokens, cachedInputTokens, outputTokens, imageCount, gpuSeconds, estimated } }` | `?cursor=&limit=` (default 50, max 100); one line per `groupId` |

Both routes are authenticated, rate policy `auth`, tenancy from the ambient `IUserOwned` filter — **no `userId` parameter is ever accepted from the client**.

Events: `credit_balance_changed.v1 { userId }` (`[OutboxEvent]`, emitted by `CreditService` inside the movement transaction) and realtime `CreditBalanceChanged { userId }` via `IRealtimeNotifier`, ids only (09 WT54). ProblemDetails from the C11 catalogue: `insufficient_credits` 402, `spend_cap_reached` 429, `forbidden` 403, `concurrency_conflict` 409; `reservation_missing` is never an HTTP status (metric + alert).

## 6. UI

Backend only — **no UI task and no `<story>-T0 — UI design` task here** (the owner rule attaches the design task to the UI folder). Consumers: 10.8.x header widget + account page (`GET /credits/balance`); 10.8.x spend history (`GET /credits/ledger`, grouped by `groupId`); 10.8.4 paused banner (`processingPaused = provider_funds_exhausted`, copy "Processing is paused. Your credits are reserved, not charged yet" — emitted by 12.8.3-T2). i18n keys this data feeds: `billing.history.*`, `billing.errors.*` (vi + en, English fallback). a11y is 10.1.3's contract, checked in 10.8.x.

## 7. Jobs and integrations

**`CreditReconciliationJob`** — daily 04:00 server-local, `[JobPolicy(Queue = JobQueue.Default, Retries = 1)]`, `[DisableConcurrentExecution]`, ids-only args, registered **unconditionally** (`billing_enabled` gates only the money-mutating user routes — O-7/CR-3; beta credits are admin-granted while the ledger is live).

1. **Stale reserves.** `kind='reserve'` rows with no `settle`/`refund` row for the same `reference_id`, older than the per-operation margin (`Credit:StaleReserveHoursStory` 24 h for `story_generate`/`fact_sheet`, `Credit:StaleReserveHoursRender` 72 h for `render`), **and** whose owning row is terminal (`generation_runs.status ∈ failed/cancelled/succeeded`; render job `succeeded/failed/cancelled`) **or** gone. Held rows (`pause_reason = provider_funds_exhausted`) and non-terminal owners are skipped and logged. Each candidate = one `RefundStaleReservationCommand` in its own `ISystemContext.For(userId)` scope; **every refund raises an alert**; batch `Credit:ReconciliationBatchSize` default 500, looped.
2. **Negative balances.** `SELECT user_id, SUM(amount) FROM credit_ledger GROUP BY user_id HAVING SUM(amount) < 0` ⇒ alert per user, no auto-fix.
3. **Lot/ledger drift.** `SUM(credit_ledger.amount)` vs `SUM(credit_lots.remaining)` over `voided_at IS NULL` lots ⇒ alert naming the user, no auto-fix; meaningful only because `CreditExpiryJob` (12.6.2-T1, 03:00) ran an hour earlier.
4. **Provider-event failures.** Count/alert `payment_events WHERE processed_at IS NULL AND error IS NOT NULL` (12.3.3-T1 owns the write).

Discovery reads are **system** reads through `ISystemWorkQuery.PendingAsyncT` (pinned, single member, never extended — 09.2.3-T1) returning `(id, user_id)`; margin/terminal/held predicates are applied by the caller. [INFERENCE] a tenant-filtered read with no ambient user loads 0 rows silently; if the pinned member cannot express the predicate it is a CR to 09.2.3-T1 — no member is added here.

Metrics: `billing_reservation_missing_total`, `billing_balance_liability_credits`, `billing_provider_loss_total{reason}`; per-run counters logged (`refunded`, `skippedHeld`, `skippedRunning`, `negativeUsers`, `driftedUsers`, `failedEvents`). Alerts only through 11.6.2-T1. No email template from this folder.

## 8. Security and privacy

- **Tenancy.** `credit_ledger`/`credit_lots`/`credit_accounts` are user data; every user-facing query is `IUserOwned` and the routes expose only the caller's rows. Sweeps and admin paths resolve a user id first (`ISystemWorkQuery.PendingAsyncT` + `ISystemContext.For`; `IPaymentLookupQuery` for payments) — never a raw global read. `CreditAccount` is excluded from the filter with a comment; 12.2.1-T1's test asserts a foreign user gets 404.
- **Authorization.** Both routes: authenticated, rate policy `auth`; admin reuse of `GetBalanceQuery` goes through the admin policy (09.10.5-T1). No admin route is added here.
- **Commands and secrets.** Each new command carries exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` (09.3.6-T1); settle/refund/reconciliation commands are `[NotAudited]` — their audit trail is the append-only ledger; admin grant audit belongs to 12.5.1-T1 (`audit_logs.category = 'billing'`). Nothing here reads `PAYMENT_*`/`OPENAI_*`; protected payloads would use an existing Data Protection purpose (`tokens.v1/{connectionId}` pattern); ledger rows carry `user_id` only and logs/alerts are ids-only.
- **Money safety.** No direct-SQL adjustments; negative movements only through `ICreditLedger` under the account lock; `reservation_missing`, provider-loss growth and negative balances are security-relevant (double-spend / minting-bug signature, F21).

## 9. Test plan

| Project | Tests | Proves |
|---|---|---|
| `NightTales.Domain.UnitTests` + `NightTales.Application.UnitTests` | `CreditLotSpendOrderTests`, `ReservationLifecycleTests`, `CreditBalanceProjectionTests`, `StaleReserveMarginTests`, `CreditServiceTests`, `ReconciliationPlannerTests`, `CreditLedgerProjectionTests` | spend order + skip rules; `open → settled \| refunded` once; free/purchased/`nextExpiry`; 24 h vs 72 h margins, terminal owners only; lock before the first write; 402 before any provider call; cap-at-reserve; late-usage rule; `reservation_missing` metric; cursor codec + family grouping — hand-written fakes from `NightTales.TestKit`, **no mocking library** |
| `NightTales.Api.IntegrationTests` (Testcontainers PostgreSQL) | `CreditLedgerConcurrencyTests`, `CreditLedgerIdempotencyTests`, `CreditReconciliationJobTests`, `CreditBalanceQueryTests`, `CreditLedgerQueryTests`, `PaidCommandReservationTests`, `PaidJobSettlementTests`, `PaidCommandPausedTests` | 20 parallel reserves never overdraw; concurrent settles move once; sweep refunds terminal stale only, skips held/running, alerts without movement; balance = live-lot sum; cursor stable, limit ≤ 100; foreign user 404; reserve-before-dispatch; settle/refund + 00 §7 moves; paused 202 keeps the reserve |
| `NightTales.Architecture.Tests` | `PaidOperationArchTests`, `LedgerWriteArchTests`, `CommandAttributeArchTests` | `[PaidOperation]` handler must call `ICreditLedger.ReserveAsync` (12 CR-11); only `CreditService` writes `credit_ledger`; one command attribute per command |

Deterministic: clock/`TimeProvider` fakes from `NightTales.TestKit`; no `Thread.Sleep`; concurrency tests use real PostgreSQL transactions; job tests split into a recording `IJobScheduler` fake + direct `RunAsync` (06 YR27).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.2.1-T1 | L (2.0) | BE | S · M2 | 12.1.4-T1, 09.2.1-T1 | 12.2.2-T1, 12.2.3-T1, 12.2.4-T1, 12.3.3-T1, 12.3.4-T1, 12.3.5-T1, 12.4.1-T1, 12.4.2-T1, 12.5.1-T1, 12.5.2-T1, 12.6.1-T1, 12.8.3-T2 |
| 12.2.2-T1 | S (0.5) | BE | S · M2 | 12.2.1-T1 | 09.10.5-T1 (soft, `GetBalanceQuery`) (09 W3 CR-13), 10.8.3-T1, 10.8.1-T0 (10/08 CR-hand-off) |
| 12.2.3-T1 | S (0.5) | BE | S · M2 | 12.2.1-T1, 09.2.6-T1 | — |
| 12.2.4-T1 | S (0.5) | BE | S · M2 | 12.2.1-T1, 12.1.4-T1, 02.2.2-T3, 09.6.1-T1 | 12.8.3-T2 |

Folder total **3.5 d raw** = epic "12.2 Ledger = 3.5" in `../../12-billing-credits.md` §11 (12 CR-4 leaves this epic unchanged). One task file per row, next to this plan.

## 11. Rollout

**Order.** 12.2.1-T1 → 12.2.2-T1 and 12.2.3-T1 in parallel → 12.2.4-T1; then dependents: 12.6.1-T1 (lots), 12.3.3-T1 (webhook grants), 12.4.1-T1 (cap), 12.8.3-T2 (dispatch gate).

**Gate `12-ledger` (staging).** reserve/settle/release/refund live; 20 parallel reserves never overdraw; lots + spend order + refund-to-same-lot proven; every paid job reserves and settles or refunds; beta credits admin-granted; reconciliation and expiry jobs green for **7 days**.

**Flag state.** `billing_enabled` stays **false** (O-7/CR-3): the ledger and both credit routes are live, credits come from admin grants, no checkout route is reachable; nothing in this folder is gated by the flag.

**Rollback.** No undo for a ledger row: a wrong movement is corrected by an opposite `adjust` row through `ICreditLedger` under the account lock (audited), never by direct SQL. Disabling the reconciliation job is not part of any rollback.

**Operational readiness.** F21's alert rules (`billing_reservation_missing_total > 0`, provider-loss growth, negative balances, the 04:00 job's own failure paging the owner) must be live in 11.6.2-T1 before the gate (12 CR-8), and §3's indexes ship in the same migration as the ledger tables.
