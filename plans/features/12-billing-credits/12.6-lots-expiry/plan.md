# 12.6 — Wallet: credit lots, expiry and low balance (S)

> Feature plan of `plans/features/12-billing-credits/README.md` (§1 C1–C16, §2 F01–F26, §3 D1–D18 + §3.1, §4 CR-1/CR-2b/CR-5/CR-6/CR-13/CR-15/CR-16, §7 gates, §8.1 canonical rows, §10 O-3/O-7/BR1–BR32). High-level: `../../12-billing-credits.md` §3 (FR-12-3, FR-12-11), §5.1, §5.6, §5.7, §12.6.
> Phase: **S · M2**, no P slice (D1). Backend only (**Area BE**); the money UI is 10.8.x (S) — this folder only fixes the wire fields it consumes (§3.1, 12 CR-9).
> Legend: ✅ read from source/docs (file + line) · ⚠️ unverified · [DECISION] from the README contract · [OPEN] · [INFERENCE] derived in this plan.

## 1. Scope

**What credits *are*.** This folder implements the lot layer of the wallet: the `credit_lots` table, the spend order that decides *which* credits a job burns, returning credits to the **same** lots on settle/release/refund, keeping dead (expired or voided) lots unspendable, daily expiry, expiry notices, and the low-balance e-mail nudge. It is the layer that makes refunds, chargebacks and outstanding liability arithmetic possible at all (F05).

**In scope (3 tasks — ids fixed by §8.1, no new ids).**

| Task | Deliverable |
|---|---|
| `12.6.1-T1` (L) | `credit_lots` domain entity + `CreditLotSource` + `source_rank` order, `CreditService` lot half: consume under the `credit_accounts` lock, one `credit_ledger` row **per lot**, release/refund back to the same lots with dead-lot re-removal, `VoidPaymentLotsAsync` lot side, balance breakdown + `nextExpiry` data |
| `12.6.2-T1` (S) | `CreditExpiryJob` (daily 03:00) writing `expire:{lotId}` rows for `remaining` of expired live lots + the 30-day `billing.creditExpiring.*` notice (one per lot) + the **global** `credit_lots (expires_at, id) WHERE voided_at IS NULL` sweep index (12 CR-13/BR24, owned here) |
| `12.6.3-T1` (S) | `LowBalanceNotifierJob` (daily): threshold `price_versions.floors['render']['standard']` via `IPriceCatalog` (BR23, no new option), `billing.lowBalance.*` notice at most once per `Credit:LowBalanceEmailEveryDays` (7), opt-out `credit_accounts.low_balance_opt_out` (CR-15) toggled by `PATCH /credits/preferences` (CR-16, owned here) |

**Out of scope.** The lock/insert mechanics of reserve/settle/refund themselves and the stale-reserve sweep (12.2.1-T1, 12.2.3-T1); balance/ledger **routes** and their DTO projection (12.2.2-T1); payments, webhook, refunds/chargebacks (12.3.x); admin grants and the minting cap (12.5.1-T1); promo validation (12.7.2-T1); funding/runway (12.8.x); all UI (10.8.x); legal texts and the ⚠️ lawyer check on expiry/refund wording (13.1.2-T1, CR-6).

**Consumers (no cycle).** 12.3.3-T1, 12.3.4-T1, 12.3.5-T1, 12.5.1-T1, 12.7.2-T1, 12.7.5-T1 grant or void lots through this folder's `ICreditLedger` members; 12.6.2-T1/12.6.3-T1 read the lots it writes. **Port members (BR29):** 12.6.1-T1 adds only `ExpireAsync` and `VoidPaymentLotsAsync` to `ICreditLedger`; `ReserveAsync`/`SettleAsync`/`RefundAsync`/`GrantAsync`/`GetOpenReservationAsync` are 12.2.1-T1's (declared and implemented there) — this folder supplies the lot-side logic they call.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| F1 ✅ | `credit_lots` columns are exactly `user_id, source (trial/bonus/promo/purchase/adjust), amount, remaining, expires_at, payment_id, voided_at`; Notes: "Spend order, expiry and voiding on refund/chargeback (12.6, 12.3.4)" | `plans/00-overview.md` §6 ✅ (:296) |
| F2 ✅ | 12 CR-2b adds `voided_reason` to `credit_lots` and `CHECK (remaining >= 0 AND remaining <= amount)` | README §4 CR-2b |
| F3 ✅ | `credit_ledger` columns: `user_id, lot_id → credit_lots, kind (grant/bonus/purchase/reserve/settle/release/refund/adjust/expire), amount (signed bigint), price_version (reserve rows), reference_type, reference_id, idempotency_key (unique), created_by`; append-only; balance = `SUM(amount)`; F04 adds `CHECK (expires_at > created_at)` on lots | 00 §6 ✅ (:295); README F04 |
| F4 ✅ | `credit_accounts` is `user_id (PK), updated_at` — "Lock row for concurrency-safe reserves (12)" | 00 §6 ✅ (:319) |
| F5 ✅ | Spend order: `ORDER BY expires_at ASC, source_rank ASC, id ASC`, `source_rank` = `trial 0 · promo 1 · bonus 2 · adjust 3 · purchase 4` (Domain mapping over the existing `source` column, **no new column**); one ledger row per lot carrying `lot_id` so release/refund return to exactly those lots (F05) | README D6, F05 |
| F6 ✅ | Dead-lot rule: still live ⇒ return; expired ⇒ return and immediately write `expire:{lotId}:{reservationId}` for the same amount; voided ⇒ same with `void:{paymentId}:{lotId}`; a lot with `expires_at <= now` is never left spendable (F06) | README D6, F06, BR19 |
| F7 ✅ | Expiry rules: purchased + bonus **12 months** from payment, trial **30 days**, promo per code; `CreditExpiryJob` daily writes `expire:{lotId}` for `remaining` of lots with `expires_at <= now` and `voided_at IS NULL`; a lot expiring within 30 days gets one e-mail + in-app notice ⚠️ lawyer check (13) | README D6, O-3, §12.6.2 |
| F8 ✅ | `ICreditLedger` keeps 02 D1's three signatures and gains `GrantAsync(Guid userId, CreditLotSource source, long amount, DateTimeOffset expiresAt, CreditGrantReference reference, CancellationToken ct) → ErrorOr<LotId>`, `ExpireAsync(LotId, ct)`, `VoidPaymentLotsAsync(Guid paymentId, string reason, ct)`, `GetOpenReservationAsync(Guid referenceId, ct)`; DI switches `MeteringOnlyCreditLedger` → `CreditService` | README D2, BR3, 12 CR-5 |
| F9 ✅ | Money mutations take `SELECT … FROM credit_accounts WHERE user_id = @u FOR UPDATE` inside the command transaction; lock order `credit_accounts → promo_codes → payments → credit_lots`; **no** session-scoped `pg_advisory_lock` in 12 | README D2, BR2 |
| F10 ✅ | Idempotency keys on `credit_ledger.idempotency_key` (unique), **lot-scoped (BR19)**: `reserve:{referenceId}:{lotId}`, `settle:{reservationId}:{lotId}`, `release:{reservationId}:{lotId}`, `refund:{reservationId}:{lotId}`, `expire:{lotId}:{reservationId?}`, `void:{paymentId}:{lotId}`; grant/payment-scoped keys keep their D2 form (`grant:trial:{userId}`, `grant:adjust:{grantRequestId}`, `grant:promo:{promoRedemptionId}`, `purchase:{paymentId}`, `bonus:{paymentId}`); a duplicate insert is a no-op, never a second movement; reservation-level truth is the aggregate state (BR4) | README D2, BR19 |
| F11 ✅ | Reservation lifecycle `open → settled \| refunded` exactly once; a command that finds no open reservation ends `reservation_missing` (metric + alert, no movement) | README D2, BR4 |
| F12 ✅ | Jobs: `CreditExpiryJob` daily 03:00 server-local, 1 retry; `LowBalanceNotifierJob` daily, 1 retry; all 12 jobs on `JobQueue.Default` (no new queue), `[DisableConcurrentExecution]` for sweeps, ids-only arguments, per-row commands through `ISender` | README D7, BR9 |
| F13 ✅ | Indexes pinned by F20 that this folder creates: `credit_lots (user_id, expires_at) WHERE voided_at IS NULL`, `credit_lots (payment_id)`, `credit_ledger (user_id, created_at DESC, id DESC)`, `credit_ledger (reference_type, reference_id)`; plus the **global sweep index** `credit_lots (expires_at, id) WHERE voided_at IS NULL` (12 CR-13/BR24, owner 12.6.2-T1; the sweep is not per-user); **no metric may be added** — F21's list is the contract | README F20, F21, D17, CR-13, BR24 |
| F14 ✅ | E-mail templates `credit_low_balance`, `credit_expiring` exist in the catalog; delivery goes through `IEmailSender.QueueAsync`; ids-only realtime; `UnitOfWorkBehavior` is the only `SaveChangesAsync` caller; exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command | README D17, C10, 09.3.6-T1 |
| F15 ✅ | The low-balance opt-out is `credit_accounts.low_balance_opt_out boolean NOT NULL DEFAULT false` (12 CR-15), toggled by `PATCH /credits/preferences` (body `{ lowBalanceOptOut }`, 12 CR-16, owned by 12.6.3-T1, always on and never behind `billing_enabled`) — no `users` column, no 09 route (BR23) | README §4 CR-15/CR-16, §10 BR23 |
| F16 ✅ | `CreditBalanceDto { total, free, purchased, nextExpiry: { amount, expiresAt } \| null }` is frozen (§3.1); the balance is computed from live lots, never a cached column (M2) | README §3.1, D6 |
| F17 ✅ | Owner-rule: any web/UI work needs an approved UI design first; 10 S consumes the routes/DTOs of §3.1 and 10 D14 forbids credit text in P | README §3.1/C14, 12 CR-9 |
| F18 ✅ | `12.6.1-T1` L (2.0) · `12.6.2-T1` S (0.5) · `12.6.3-T1` S (0.5) → folder 3.0 d raw; plan 12 = 25.0 d raw / 30.0 buffered | README §8.1, §9, O-6 |

## 3. Domain and data

### 3.1 Domain types (`NightTales.Domain`)

```csharp
// Domain/Ledger/LotId.cs
public readonly record struct LotId(Guid Value);                       // UUIDv7, DB default

// Domain/Ledger/CreditLotSource.cs — DB/wire values snake_case, copied from 00 §6:296
public enum CreditLotSource { Trial, Bonus, Promo, Purchase, Adjust }

// Domain/Ledger/CreditLotSourceRanks.cs — README D6/F05, no new column
public static class CreditLotSourceRanks
{
    public static int Of(CreditLotSource s) => s switch
    {
        CreditLotSource.Trial    => 0,
        CreditLotSource.Promo    => 1,
        CreditLotSource.Bonus    => 2,
        CreditLotSource.Adjust   => 3,
        CreditLotSource.Purchase => 4,
        _ => throw new UnreachableException()
    };
}

// Domain/Ledger/CreditLot.cs — IUserOwned (09.2.3 tenant filter)
public sealed class CreditLot : Entity, IUserOwned
{
    public LotId Id { get; private init; }
    public Guid UserId { get; private init; }
    public CreditLotSource Source { get; private init; }
    public long Amount { get; private init; }
    public long Remaining { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private init; }   // null = never expires (adjust only)
    public Guid? PaymentId { get; private init; }             // purchase/bonus/promo-bonus lots
    public DateTimeOffset? VoidedAt { get; private set; }
    public string? VoidedReason { get; private set; }         // 12 CR-2b
    public DateTimeOffset CreatedAt { get; private init; }

    public bool IsLive(DateTimeOffset now) => VoidedAt is null && (ExpiresAt is null || ExpiresAt > now) && Remaining > 0;
    public void Consume(long amount);      // guards: amount > 0, amount <= Remaining, IsLive(now) — else throw CreditLotErrors.*
    public void Return(long amount);       // guards: amount > 0, Remaining + amount <= Amount (F04 CHECK mirrors this)
    public void Expire();                  // Remaining = 0 (only after the expire ledger row is staged)
    public void Void(string reason, DateTimeOffset now);
}

// Domain/Ledger/Reservation.cs — value objects copied verbatim from README D2
public sealed record LotConsumption(LotId LotId, long Amount);
public sealed record ReservationSnapshot(ReservationId Id, Guid UserId, CreditOperation Operation,
        long Amount, PriceVersionVersion PriceVersion, ReservationState State,
        IReadOnlyList<LotConsumption> Lots);
public sealed record SettleResult(long Charged, long Released);
```

Invariants enforced in the Domain **and** mirrored by DB checks (F2/F4): `0 <= Remaining <= Amount`; `ExpiresAt is null || ExpiresAt > CreatedAt`; `VoidedAt is null == VoidedReason is null`; a lot is never consumed past `Remaining`; `Sum(lots.Remaining) == SUM(credit_ledger.amount)` per user (checked by 12.2.3-T1's `CreditReconciliationJob`, alert only).

### 3.2 Schema (created by `_12_CreditLots`, **owned by 12.2.1-T1 only** — BR20; shown here for reference, this folder adds no migration for this table)

```sql
CREATE TABLE credit_lots (
    id            uuid PRIMARY KEY DEFAULT uuidv7(),
    user_id       uuid NOT NULL REFERENCES users(id),
    source        text NOT NULL CHECK (source IN ('trial','bonus','promo','purchase','adjust')),
    amount        bigint NOT NULL CHECK (amount > 0),
    remaining     bigint NOT NULL,
    expires_at    timestamptz NULL,
    payment_id    uuid NULL REFERENCES payments(id),
    voided_at     timestamptz NULL,
    voided_reason text NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_credit_lots_remaining_bounds CHECK (remaining >= 0 AND remaining <= amount),   -- 12 CR-2b
    CONSTRAINT ck_credit_lots_expiry_after_created CHECK (expires_at IS NULL OR expires_at > created_at), -- F04
    CONSTRAINT ck_credit_lots_voided_pair CHECK ((voided_at IS NULL) = (voided_reason IS NULL))
);
CREATE INDEX ix_credit_lots_user_expiry ON credit_lots (user_id, expires_at) WHERE voided_at IS NULL;  -- F20
CREATE INDEX ix_credit_lots_payment     ON credit_lots (payment_id);                                     -- F20

ALTER TABLE credit_ledger ADD COLUMN lot_id uuid NULL REFERENCES credit_lots(id);
CREATE UNIQUE INDEX ux_credit_ledger_idem ON credit_ledger (idempotency_key);  -- owner spelling, 12.2.1-T1
CREATE INDEX ix_credit_ledger_user_created  ON credit_ledger (user_id, created_at DESC, id DESC);       -- F20
CREATE INDEX ix_credit_ledger_reference     ON credit_ledger (reference_type, reference_id);             -- F20
```

Notes: `credit_lots` is `IUserOwned` ⇒ EF global query filter on `user_id` (09.2.3); the `credit_lots (user_id, expires_at)` partial index serves the per-user spend-order scan (≤ a few dozen live lots per user), while the **global** expiry sweep is served by the separate index `credit_lots (expires_at, id) WHERE voided_at IS NULL` (12 CR-13/BR24) created by 12.6.2-T1's own additive migration (`CREATE INDEX ON credit_lots (expires_at, id) WHERE voided_at IS NULL;`) — the table itself stays **12.2.1-T1's** `_12_CreditLots` (BR20; 12.6.1-T1 adds no migration for it). `remaining`/`voided_*` are the only mutable columns; there is no `balance` column on `credit_accounts` (F16).

### 3.3 Consumption query (pinned SQL, `CreditService`, Infrastructure)

Runs inside the command transaction **after** `SELECT … FROM credit_accounts WHERE user_id = @u FOR UPDATE` (F9/BR2), which also serializes all lot movements for that user:

```sql
SELECT id, source, amount, remaining, expires_at
FROM   credit_lots
WHERE  user_id = @userId
  AND  remaining > 0
  AND  voided_at IS NULL
  AND  (expires_at IS NULL OR expires_at > @now)
ORDER  BY expires_at ASC NULLS LAST,
          CASE source WHEN 'trial' THEN 0 WHEN 'promo' THEN 1 WHEN 'bonus' THEN 2
                      WHEN 'adjust' THEN 3 WHEN 'purchase' THEN 4 END ASC,
          id ASC
FOR UPDATE;
```

The service walks the rows until `remaining` is satisfied; insufficient live credits ⇒ `insufficient_credits` (402, D2) and the transaction rolls back. For each consumed lot it stages one ledger row `(kind = 'reserve', amount = -consumed, lot_id, reference_type = 'reservation', reference_id = @reservationId, price_version = @version, idempotency_key = 'reserve:{referenceId}:{lotId}')` — one row **per lot** (F05) so the release path knows exactly where to return. `LotConsumption` list is stored on the reservation and mirrored into `ReservationSnapshot.Lots`.

### 3.4 Return path and dead-lot re-removal (F06)

For each `LotConsumption` of the reservation, inside the same transaction, per lot `FOR UPDATE`:

| Lot state at return time | Action | Ledger row (kind, amount, key) |
|---|---|---|
| live (`voided_at IS NULL AND (expires_at IS NULL OR expires_at > now)`) | `Return(amount)` | `release` (settle path) / `refund` (failure path), `+amount`, `release:{reservationId}:{lotId}` / `refund:{reservationId}:{lotId}` |
| expired (`expires_at <= now`) | `Return(amount)` then `Expire()` in the same transaction | `release`/`refund` `+amount` **and** `expire` `-amount` with `expire:{lotId}:{reservationId}` |
| voided (`voided_at IS NOT NULL`) | `Return(amount)` then immediate re-removal | `release`/`refund` `+amount` **and** `adjust` `-amount` with `void:{paymentId}:{lotId}` |

Partial expiry (an hour of the lot expired while the job ran) is the common shape of the first column's neighbour and must be handled without a second lock pass: `Return` uses the `expires_at` read under `FOR UPDATE`, so two racing returns cannot both resume the same credits. `Return` never pushes `remaining` above `amount` (the `ck_credit_lots_remaining_bounds` check is the backstop); a shortfall (a void that would cross zero) is recorded once as `billing_provider_loss_total` + admin alert (12.3.4's rule, implemented there).

### 3.5 Voiding (`VoidPaymentLotsAsync`, lot side)

`VoidPaymentLotsAsync(paymentId, reason, ct)` (F8) loads **every** lot with that `payment_id` (purchase + bonus + promo bonus) under the account locks of the affected user, and for each: `voided_reason = @reason`, `voided_at = @now`, `remaining` removed with one `adjust` row `void:{paymentId}:{lotId}`. Already-voided lots are skipped (idempotent). Shared verbatim by 12.3.4-T1 (refund/chargeback) and 12.7.5-T1 (approved refund request).

### 3.6 Expiry data source and rules

| `source` | `expires_at` | Set by |
|---|---|---|
| `purchase` | `paid_at + 12 months` (`Credit:PurchasedExpiryMonths`) | 12.3.3-T1 (webhook grant) |
| `bonus` | same as its payment (`paid_at + 12 months`) | 12.3.3-T1 / 12.7.2-T1 |
| `promo` | per code (`promo_codes.valid_to`); for a checkout bonus, `paid_at + 12 months` | 12.7.2-T1 |
| `trial` | `now + Credit:TrialExpiryDays` (30) | 12.4.2-T1 |
| `adjust` | `now + 12 months` — `NULL` only for a negative adjustment, which never creates a lot | 12.5.1-T1 |

`grant` ledger rows (`kind = 'grant'` for trial/adjust, `purchase`/`bonus` for paid lots) are written by the granting task through `GrantAsync`; this folder owns the `credit_lots` row it creates and the `source_rank` semantics, never the callers' transactions.

### 3.7 Config keys (README D10, no new keys)

`Credit:PurchasedExpiryMonths` 12 · `Credit:TrialExpiryDays` 30 · `Credit:LowBalanceEmailEveryDays` 7 · `Credit:StaleReserveHoursStory` 24 / `Credit:StaleReserveHoursRender` 72 (12.2.3-T1) · `Credit:MaxCreditsPerUserPerDay` 5000 (12.4.1-T1). **BR23:** D10 declares **no** low-balance threshold option and none is added — the threshold is `price_versions.floors['render']['standard']` of the **active** version read through `IPriceCatalog` (soft read of 12.1.2-T1; missing floor ⇒ the job skips the run with a warning, never guesses).

### 3.8 Ledger sign convention (BR18, mandatory, M1)

One convention, identical in 12.2, 12.4, 12.6 and 12.8 — this folder's rows, queries and tests restate it exactly (BR18 requires the same wording everywhere; a folder that restates it differently is wrong):

- **Sign convention (BR18) — one convention, identical in 12.2, 12.4, 12.6 and 12.8.** `credit_ledger.amount` is signed **from the user's balance view**: `grant`/`bonus`/`purchase` **positive**; `reserve` = **−R** (the reserve); `settle` = **−C** (the charge, `C = min(R, max(floor, credits(actual)))`); `release` = **+R** (the whole hold, exactly, no rounding — written only together with `settle`, in the same transaction); `refund` = **+R** with **no** `settle` row; `expire` = **−remaining**; `adjust` = signed. Per reservation: `SUM(amount)` = **−R** while open, **−C** once settled, **0** once refunded. UI/DTO values are derived per reservation group, never per row: `quoted = Σ|reserve|`, `charged = Σ|settle|`, `returned = quoted − charged` (or `= quoted` when refunded).

Releasing into a lot that died while the job ran is not an exception: the return writes `release`/`refund` **+amount** and the **same transaction** re-removes it with `expire:{lotId}:{reservationId}` (expired lot) or `void:{paymentId}:{lotId}` (voided lot) — the lot-scoped keys of **BR19**. Per-lot rows: `reserve:{referenceId}:{lotId}`, `settle|release|refund:{reservationId}:{lotId}`, `expire:{lotId}:{reservationId?}`, `void:{paymentId}:{lotId}`; grant/payment-scoped keys keep their D2 form; reservation-level truth is the aggregate state (BR4), never a second key.

## 4. Use cases

- **UC-1 Grant** — `GrantAsync(userId, source, amount, expiresAt, reference, ct)`: under the account lock, create the lot (`remaining = amount`), write the `grant`/`purchase`/`bonus` ledger row, keyed by `grant:trial:{userId}` / `grant:adjust:{grantRequestId}` / `grant:promo:{promoRedemptionId}` / `purchase:{paymentId}` / `bonus:{paymentId}`; the second call with the same key is a no-op returning the existing `LotId` (F10).
- **UC-2 Reserve** — a paid command consumes live lots in the §3.3 order; multi-lot reserves write one row per lot; `insufficient_credits` rolls the whole command back.
- **UC-3 Settle** — the charged amount is spread over the reservation's lots by the same `LotConsumption` list and the remainder is returned per §3.4 (`release` = **+R**, the whole hold, written only with `settle` — BR18); `SettleResult(Charged, Released)`.
- **UC-4 Release/refund** — full reserve returned to the same lots (`refund` = **+R** with **no** `settle` row — BR18); expired/voided lots are re-removed in the same transaction with `expire:{lotId}:{reservationId}` / `void:{paymentId}:{lotId}` (BR19).
- **UC-5 Expire** — `CreditExpiryJob` → `ExpireCreditLotCommand(lotId)` per lot → `expire:{lotId}` for `remaining`, `remaining = 0` (F7).
- **UC-6 Void** — `VoidPaymentLotsAsync` for a refund/chargeback (12.3.4-T1, 12.7.5-T1).
- **UC-7 Expiry notice** — lots with `0 < remaining` and `expires_at BETWEEN now AND now + 30 d` get exactly one `credit_expiring` e-mail; the in-app notice is `nextExpiry` on `GET /credits/balance` (F16) — no new channel.
- **UC-8 Low balance** — `balance < threshold` (BR23: `price_versions.floors['render']['standard']` of the active version via `IPriceCatalog`) and not nudged inside the current `Credit:LowBalanceEmailEveryDays` window and `credit_accounts.low_balance_opt_out = false` (12 CR-15; flipped by `PATCH /credits/preferences { lowBalanceOptOut }`, 12 CR-16, owned by 12.6.3-T1) ⇒ one `billing.lowBalance.*` notice per window (template `credit_low_balance`, D17).
- **UC-9 Balance read** — 12.2.2-T1 projects `total / free / purchased / nextExpiry` from live lots through `GetBalanceAsync`-style internal reads owned here (it must never aggregate `credit_ledger` alone: `SUM(amount)` and `SUM(remaining)` are equal but only lots know `free` vs `purchased` and the next expiry).

## 5. API and events

**One route is added, and it is 12.6.3-T1's** (00 §8 frozen; 12 CR-16 adds `PATCH /credits/preferences` and 12 CR-2b adds `credit_lots.voided_reason` + the bounds CHECK). Fields this folder fills in §3.1's frozen shapes:

| Route (owner) | Field this folder supplies |
|---|---|
| `GET /credits/balance` (12.2.2-T1) | `total` = `SUM(remaining)` of live lots; `free` = live `trial`+`bonus`+`promo`+`adjust`; `purchased` = live `purchase` (12 CR-25); `nextExpiry` = live lot with the earliest `expires_at` → `{ amount, expiresAt }`, else `null` |
| `GET /credits/ledger` (12.2.2-T1) | nothing new: per-lot rows collapse into one UI line per `groupId` (= reservation id, §3.1); `lot_id`, `idempotency_key`, `reference_*` and `created_by` stay server-side |
| `GET /billing/quote` (12.1.3-T1) | nothing (quote is 12.1) — 12.6.3's threshold reads the same active price version |
| `PATCH /credits/preferences` (12.6.3-T1, 12 CR-16) | owns the route: body `{ lowBalanceOptOut }` flips `credit_accounts.low_balance_opt_out` (12 CR-15); always on, never behind `billing_enabled` (BR23) — no other folder implements it |

**Events (outbox, `IEmailSender` per D17).** Both names are [INFERENCE] — the README names the *templates*, not the event types:

| Event | Emitted by | Payload | Consumer |
|---|---|---|---|
| `credit_expiring.v1` | `SendCreditExpiringNoticeCommand` (12.6.2-T1) | `{ userId, lotId, amount, expiresAt, uiLocale }` | outbox handler → `IEmailSender.QueueAsync(..., "credit_expiring", …)` |
| `credit_low_balance.v1` | `SendLowBalanceNoticeCommand` (12.6.3-T1) | `{ userId, total, thresholdCredits, uiLocale }` | outbox handler → `IEmailSender.QueueAsync(..., "credit_low_balance", …)` |

Both are ids-only + amounts (no PII beyond the user's own address, D18). `CreditBalanceChanged { userId }` stays 12.3.3-T1's (ids only).

## 6. UI

**No UI implementation in this folder.** The money surfaces are 10 S: `10.8.x` owns the balance widget (`header-end`, 10 D14), the credits page (history lines per `groupId`, `nextExpiry`/expiry banner) and the low-balance banner with "Buy credits". 12.6.3-T1 supplies the server side the banner derives from (`total` vs the Standard-render quote); per the owner rule (2026-09-25) the banner/notice visual design ships as **10.8.x's own `<story>-T0 — UI design` task** (loading/empty/error/success, 360/768/1280, vi+en, dark/light, focus order, D11 components) — 12.6's canonical rows contain no UI task, so this folder only records the dependency (see report note R-3).

Copy contract for 10 S: expiry rules are stated on the pricing page and in the terms (13.1.2-T1 via CR-6); the expiry banner uses `nextExpiry.amount`/`nextExpiry.expiresAt`; the low-balance banner must not promise "unlimited" anything (B6); a11y per 10.1.3 (no colour-only status).

## 7. Jobs and integrations

| Job | Cadence / policy | What it does |
|---|---|---|
| `CreditExpiryJob` | daily 03:00 server-local · `[JobPolicy(Queue = JobQueue.Default, Retries = 1)]` · `[DisableConcurrentExecution]` · ids-only args | Pass 1: select due lots (§7.1) → `ExpireCreditLotCommand(lotId)` one per lot via `ISender`. Pass 2: select lots expiring within 30 d → `SendCreditExpiringNoticeCommand(lotId)` one per lot |
| `LowBalanceNotifierJob` | daily · `[JobPolicy(Queue = JobQueue.Default, Retries = 1)]` · `[DisableConcurrentExecution]` | Select users whose live `SUM(remaining)` < threshold and whose last nudge is outside the window → `SendLowBalanceNoticeCommand(userId)` one per user |

**7.1 Selection queries (pinned, single-member, never extended — 09.2.3-T1 pattern).** `IExpiringLotQuery.DueAsync(DateTimeOffset now, int take, LotId? afterId, ct) → IReadOnlyList<(LotId Id, Guid UserId, long Remaining)>` (`remaining > 0 AND voided_at IS NULL AND expires_at <= now`, ordered `expires_at, id`, keyset-paged with `take = 500`) and `IExpiringLotQuery.DueSoonAsync(now, now.AddDays(30), …)`. `ILowBalanceQuery.UsersBelowAsync(long thresholdCredits, DateTimeOffset windowStart, int take, LotId? afterId, ct) → IReadOnlyList<(Guid UserId, long Total)>`, a set-based `SUM(remaining) … GROUP BY user_id HAVING` over live lots, `LEFT JOIN`-free (the rate limit is enforced per command by the claim in §8). Both are `ISystemWorkQuery`-style global readers; **every write goes through a command scoped with `ISystemContext.For(userId)`** (C10) so the tenant filter of `credit_lots` is satisfied, never bypassed.

**7.2 Registration.** Both recurring jobs register unconditionally (not behind `billing_enabled`): the flag gates only the money-mutating **user routes** (O-7/CR-3), while beta credits are admin-granted and must still expire. [INFERENCE] stated explicitly because 06 D1's "register while the flag is on" pattern would silently disable expiry in the closed beta.

**7.3 Reuse, never re-invent.** `UnitOfWorkBehavior` is the only `SaveChangesAsync` (one per command, F14); per-row commands through `ISender` so one bad row never rolls back the batch (06 YR26/BR9); `IClock` (never `DateTimeOffset.UtcNow` in handlers); `IEmailSender.QueueAsync`; `[OutboxEvent]`; `IFeatureFlagReader`; `IPriceCatalog` (soft, 12.1.2-T1); no new HTTP client, no new queue, no new metric (F13).

## 8. Security and privacy

- **Tenant isolation.** `credit_lots` is `IUserOwned` with the global filter; jobs resolve ids globally then execute per-user commands under `ISystemContext.For(userId)` (F9/C10/F10 in README). A user-facing query without a scope silently returns 0 rows — the selection queries above are the only unscoped readers and they never write.
- **Ledger monopoly.** Credits move only through `ICreditLedger`/`CreditService`; no handler, job or admin path writes `credit_lots`/`credit_ledger` directly. Architecture test `credit_movements_only_through_ledger` (12.2.1-T1 owns it; this folder's `ExpireCreditLotCommand` and the lot store must pass it).
- **Command attributes.** `ExpireCreditLotCommand`, `SendCreditExpiringNoticeCommand`, `SendLowBalanceNoticeCommand`: `[NotAudited]` (system-driven; the ledger rows are the audit) — exactly one attribute each (09.3.6-T1). Admin-driven lot changes (12.5.1-T1 grants, 12.3.4-T1 voids) write `audit_logs.category = 'billing'` in **their** tasks.
- **Abuse.** Expiry and nudge are rate-limited by the ledger/claim keys, so a replayed job cannot double-expire or double-mail; the expiry job's `[DisableConcurrentExecution]` plus the row lock means two workers cannot consume the same lot.
- **Privacy.** E-mails carry amounts, dates and the user's own locale only — no payment ids, no e-mail addresses in logs (`user_id` only); no secrets are added by this folder (no Data Protection purpose needed).
- **Money invariants.** `remaining ∈ [0, amount]` (DB + Domain), `expire`/`void` rows can never make `SUM(amount)` negative (DB check on lots; 12.2.3-T1 alerts on any negative balance), and a refund into an expired/voided lot is netted in the same transaction (§3.4).
- **Opt-out (BR23, 12 CR-15/CR-16):** the low-balance opt-out is `credit_accounts.low_balance_opt_out boolean NOT NULL DEFAULT false` toggled by `PATCH /credits/preferences { lowBalanceOptOut }` (owned by 12.6.3-T1; always on, never behind `billing_enabled`) — **no** `users` column, **no** 09 route. The column lands in 12.2.1-T1's `_12_CreditAccounts` migration (BR20); 12.6.3-T1 owns the route and the command. The nudge is `Should`, so it never blocks 12.6.1-T1/12.6.2-T1.

## 9. Test plan

**`NightTales.Domain.UnitTests`** — `CreditLotConsumeTests` (Given a lot `amount 100, remaining 40`, When `Consume(41)`, Then `CreditLotErrors.OverRemaining`, `remaining` unchanged); `CreditLotReturnTests` (return up to `amount`, never above; expired lot return → `Expire()` path is a service rule, domain guards `Remaining <= Amount`); `CreditLotSourceRankTests` (the five ranks pinned: trial 0, promo 1, bonus 2, adjust 3, purchase 4 — asserts against a hard-coded expectation, not against the same mapping).

**`NightTales.Application.UnitTests`** (hand-written fakes from `NightTales.TestKit`, no mocking library) —
`ExpireCreditLotCommandHandlerTests`: Given a live lot with `expires_at <= now` and `remaining 30`, When handled, Then one `expire` row `-30` with key `expire:{lotId}`, `remaining = 0`; Given the same command replayed, Then no second row (claim conflict) and no error; Given a lot already voided, Then no row and no error; Given a lot with `expires_at` in the future, Then `credit_lot_not_due` and no movement.
`SendCreditExpiringNoticeCommandHandlerTests`: first call emits `credit_expiring.v1` once; second call inside the same lot lifetime emits nothing; a lot with `remaining = 0` emits nothing.
`SendLowBalanceNoticeCommandHandlerTests`: balance below threshold ⇒ `credit_low_balance.v1` once; same user 3 days later ⇒ nothing; 7 days later ⇒ sent again (fake `IClock`); a user with `credit_accounts.low_balance_opt_out = true` ⇒ nothing; missing `floors['render']['standard']` in the active price version ⇒ nothing and no exception (BR23).
`CreditReservationLotTests` (the lot half of the ledger): Given lots trial 50 (20 d), bonus 100 (11 months), purchased 1000 (11 months), When reserving 120, Then consumed `(trial 50, bonus 70)` in that order and two `reserve` rows; When later refunded, Then 50 and 70 return to exactly those lots; Given the trial lot expired before the refund, Then the return is followed by an `expire:{lotId}:{reservationId}` row for the same amount and `SUM(amount)` is unchanged.

**`NightTales.Api.IntegrationTests`** (real PostgreSQL 17, `PostgresFixture`, `NightTales.TestKit` seeding) —
`credit_lots_spend_order_is_deterministic`: seed 4 lots with equal expiry in all source combinations, 20 parallel reserves of 1 credit → the observed consumption order is always `trial, promo, bonus, adjust, purchase` and never overdraws (NFR-12-1).
`credit_expiry_never_goes_negative`: expire a lot with an open reserve, settle the job, assert `SUM(amount) == SUM(remaining)` and both ≥ 0, and that a replayed `CreditExpiryJob` run changes nothing.
`credit_release_into_voided_lot_nets_to_zero`: reserve spanning two lots, void the payment mid-flight (12.3.4-T1 path), refund, assert balances return to the pre-reserve values and the voiding shortfall is reported once.
`low_balance_notice_rate_limit`: three `LowBalanceNotifierJob` runs within one window produce exactly one `outbox_messages` row of type `credit_low_balance.v1` (delayed-job tests split into a recording `IJobScheduler` fake + a direct `RunAsync` call — 06 YR27).

**`NightTales.Architecture.Tests`** — the §8 ledger-monopoly rule and the "exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]`" rule for the three new commands (09.3.6-T1's scan picks them up automatically).

**Coverage floor:** `source_rank` order, dead-lot re-removal (three states), expire idempotency, notice-once, rate-limit window, and `SUM(remaining) == SUM(amount)` after every scenario. No test asserts incidental defaults (no "row count grew" checks) and no test pins source text.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase / flag | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.6.1-T1 | L (2.0) | BE | S · M2 | 12.2.1-T1 | 12.3.3-T1, 12.3.4-T1, 12.3.5-T1, 12.5.1-T1, 12.6.2-T1, 12.6.3-T1, 12.7.2-T1, 12.7.5-T1, 10.8.3-T1 (10/08 CR-hand-off) |
| 12.6.2-T1 | S (0.5) | BE | S · M2 | 12.6.1-T1, 09.2.6-T1, 13.1.2-T1 (12 CR-6) | — |
| 12.6.3-T1 | S (0.5) | BE | S · M2 | 12.6.1-T1, 09.2.6-T3 | 10.8.3-T1 (10/08 CR-hand-off) |

Folder total **3.0 d raw** (2.0 + 0.5 + 0.5), matching README §8.1/§9; plan 12 total 25.0 d raw / 30.0 buffered (O-6). No task exceeds L; no re-estimate is requested by this folder (the README §9 numbers already include it). Task files: `12.6.1-T1-credit-lots-and-spend-order.md`, `12.6.2-T1-credit-expiry-job.md`, `12.6.3-T1-low-balance-nudge.md`.

## 11. Rollout

1. **Order.** `12.2.1-T1` (port + `CreditService` + lock) → `12.6.1-T1` (this folder's table, order, return path) → `12.6.2-T1` and `12.6.3-T1`. Every grant path (12.3.3-T1, 12.4.2-T1, 12.5.1-T1, 12.7.2-T1) starts only after `12.6.1-T1`, so the first credit ever granted already has a lot, an expiry and a spend position (§5 sequencing).
2. **Migration.** `credit_lots` (§3.2 DDL) is created by **12.2.1-T1's** `_12_CreditLots` (BR20) — this folder adds no migration for the table itself; 12.6.2-T1 owns the one migration this folder does add, the global expiry-sweep index `credit_lots (expires_at, id) WHERE voided_at IS NULL` (BR24, CR-13). No backfill: in P the `MeteringOnlyCreditLedger` wrote no ledger rows and no lots (C5/D1), so `credit_lots` starts empty; the migration is additive (`credit_ledger.lot_id` nullable) and safe to deploy while 12.2.x is already live.
3. **Flags.** `billing_enabled` stays **false** (O-7): the closed beta runs admin-granted credits; the two jobs are registered anyway (§7.2) so beta credits still expire and nudge. Nothing in this folder is reachable through a user money route.
4. **Gate 12-ledger (README §7)** must stay green for 7 days with these jobs running: no negative balance, `SUM(remaining) == SUM(amount)`, no unexplained `expire` rows, no duplicate notices.
5. **Verification SQL after the first week** (read-only, run by the operator):
   `SELECT count(*) FROM credit_lots WHERE remaining < 0 OR remaining > amount;` → 0 ·
   `SELECT count(*) FROM (SELECT user_id, SUM(remaining) r FROM credit_lots WHERE voided_at IS NULL GROUP BY user_id) l JOIN (SELECT user_id, SUM(amount) a FROM credit_ledger GROUP BY user_id) g USING (user_id) WHERE l.r <> g.a;` → 0 ·
   `SELECT count(*) FROM credit_lots WHERE expires_at <= now() AND remaining > 0 AND voided_at IS NULL;` → 0 after a job run ·
   `SELECT idempotency_key, count(*) FROM credit_ledger GROUP BY 1 HAVING count(*) > 1;` → 0.
6. **Rollback.** Disable the two Hangfire recurring jobs (they are the only new writers); the table and columns stay (additive) and no data has to be undone — a lot with `expires_at` passed but not yet expired is correct, just unexpired until the job returns.
7. **Open items carried to DoR.** The UI-design task owner for the banner (report R-3) and CR-6's lawyer check on expiry wording ⚠️ (13). **Resolved by the TL rulings:** R-1 (lot-scoped ledger keys) → **BR19**; R-2 (no low-balance threshold key) → **BR23** (threshold = `price_versions.floors['render']['standard']` via `IPriceCatalog`, no new option); R-4 (notice claim key) → **BR23** fixes the notice keys (`billing.lowBalance.*`, `billing.creditExpiring.*`, `billing.lowBalance.optOut`), the once-per-lot claim mechanism stays as designed in 12.6.2-T1; R-5 (global sweep index) → **BR24**; O-12 (opt-out storage/route) → **CR-15/CR-16 + BR23**.
