# 12.4 — Limits, trial credits and margin reporting (S)

> Feature plan of `plans/features/12-billing-credits/README.md` (§0 folder brief, §1 C1–C16, §2 F01–F22, §3 D1–D18 + §3.1, §4 CR-1/CR-9/CR-12, §7 gates, §8.1 canonical rows, §10 O-1–O-8 + BR1–BR17). High-level: `../../12-billing-credits.md` §3 (FR-12-2, FR-12-3, FR-12-12), §4 epics 12.4/12.5, §5.1, §5.3, §5.7, §6.
> Phase: **S · M2**, no P slice (D1). Backend only (**Area BE**). The folder name is `12.4-limits-trial/` but it also hosts **12.5.2-T1** (epic 12.5) exactly as the README index says (README:19, README:308) — ids are frozen.
> Legend: ✅ read from source/docs (file + line) · ⚠️ unverified · [DECISION] from the README contract · [DECISION-L] decided in this plan (no README name exists) · [OPEN] · [INFERENCE].

## 1. Scope

**What this folder is.** The two abuse backstops and the anti-fraud number. (a) **12.4.1-T1** adds the *per-user credit cap* on top of the P platform-USD cap: a bug, a loop or an abuser can burn at most `Credit:MaxCreditsPerUserPerDay` credits per UTC day, and every paid command answers 429 `spend_cap_reached` past it (D5, README:141; plan §12.4.1). (b) **12.4.2-T1** turns e-mail verification into exactly one trial lot (`grant:trial:{userId}`, 30 days) with a disposable-domain list, a velocity limit, an owner-editable amount and a *deferred* path while the provider-funds pause is open (D5, D15). (c) **12.5.2-T1** builds `GET /admin/usage` — margin per day × operation computed from `payments` (revenue, net of refunds) and `usage_records` (cost), never from credits × price (F15, D11): the number the owner uses to raise prices and the input 12.8.1-T1/12.8.4-T1 consume.

**In scope (3 tasks — ids fixed by §8.1, no new ids, no renames).**

| Task | Deliverable | Size |
|---|---|---|
| 12.4.1-T1 | Per-user daily credit cap enforced inside `CreditService.ReserveAsync`, 429 `spend_cap_reached` + `retryAfterSeconds`, admin alert at 80 %, cap metric | S (0.5) |
| 12.4.2-T1 | `GrantTrialCreditsCommand` + eligibility policy (verified e-mail, disposable-domain list, amount from settings), `grant:trial:{userId}`, `trial_granted` e-mail, deferred `trial_grant_requested.v1` when paused, `ITrialGrantCandidateQuery`; Depends += `09.10.6-T1` (soft) (09 W3 CR-13) | S (0.5) |
| 12.5.2-T1 | `GetMarginReportQuery` + pinned `IMarginReportQuery` (one SQL statement) behind `GET /admin/usage?from=&to=&operation=` → `MarginRowDto[]` | M (1.0) |

**Out of scope (owned elsewhere).** Platform-USD caps `IBudgetGuard.CheckAsync` 429 `spend_cap_reached` — P, 02.2.2-T2 ✅; the reserve/settle/release/refund engine — 12.2.1-T1; the lot engine and `CreditExpiryJob` — 12.6.x; the webhook that creates purchased lots — 12.3.3-T1; the funding view that *displays* revenue7d/30d — 12.8.1-T1; the deferred-trial application in `FundingRunwayJob` — 12.8.3-T1 (this folder only publishes the event and the query it consumes); minting caps/approval — 12.5.1-T1/-T2; promo exposure — 12.7.2-T1; the price table and `CreditCalculator` — 12.1.2-T1/12.1.4-T1; every money screen — 10.8.x.

**No UI work in this folder** (Areas are BE). Per the owner rule of 2026-09-25 the user-facing screens 10.8.x (balance, quote, checkout, history, errors) and the admin usage page are S and each needs its approved `<story>-T0 — UI design` task **before** its implementation tasks; this folder only freezes the wire it consumes (§3.1, 12 CR-9) and states which screen shows what (§6).

**Schema impact: none.** No table, column, index or migration is added by this folder beyond the indexes 12.2.1-T1/12.6.1-T1 create (D17/F20). Everything here is enforcement, one read-only report, and config.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| C1 | All three tasks are **S · M2**, no P task, no P build of the ledger; `ICreditLedger` + `CreditOperation` + `UsageBound`/`CostBound` + `IBudgetGuard` + `spend_cap_reached` + 02's `MeteringOnlyCreditLedger` are **reused**, not rebuilt | README:103 (D1) |
| C2 | Per-user cap: M2, config `Credit:MaxCreditsPerUserPerDay` **default 5,000**, **same error code** `spend_cap_reached` as the platform cap, `retryAfterSeconds` = next UTC midnight, both caps alert admins at **80 %** | README:141 (D5) ✅ |
| C3 | Platform half (daily/monthly USD over `usage_records` + open worst-case bounds, `pg_advisory_xact_lock(hashtext('budget_guard'))` in the command transaction, 429 `spend_cap_reached`) is P, built by 02.2.2-T2 | 02 README:119–124, 02 README:448 (CR-2), README:341 |
| C4 | Trial: one-time `GrantAsync(source = Trial, expiresAt = now + 30 d)` with **`grant:trial:{userId}`**, after **e-mail verification**; amount = owner-editable settings key `registration.trial.credits` (09.10.6-T1, validated 0…10000), `Credit:TrialCredits` is only the seed/fallback; blocked by a **disposable-domain list + IP/device rate limit**; when the funding pause is open the grant is **deferred through outbox** | README:141 (D5), plan §12.4.2 ✅ |
| C5 | The deferred grant is applied when runway ≥ 3 d; users verifying meanwhile get the deferred `trial_grant_requested.v1` applied later; trial grants are **paused** below 3 d runway | README:198 (D15) ✅ |
| C6 | Trial lot expiry 30 d / purchased+bonus 12 months / promo per code; trial = the value of **one short episode** | README O-3 (README:383), D6 (README:144) ✅ |
| C7 | `CreditOptions.MaxCreditsPerUserPerDay` 5000 · `TrialCredits` (seed only) · `TrialExpiryDays` 30 · `StaleReserveHoursStory/Render` · `MintCapPerActorPer24h` · `ResolveApprovalThresholdCredits` — one options class, one section `Credit` | README:183 (D10) ✅ |
| C8 | Margin: `GET /admin/usage?from=&to=&operation=` → `{ items: MarginRowDto[] }`, `MarginRowDto { day, operation, jobs, creditsCharged, revenueUsd, costUsd, estimatedCostUsd, platformLossUsd, marginPct }` | README §3.1 (README:232) ✅ |
| C9 | Revenue = `SUM(payments.amount → USD at that payment's rate)` for `paid`/`partially_refunded`, **net of refunds**, in the window; bonus/promo credits are never revenue; cost = `SUM(usage_records.cost_usd)` + GPU share, with an `estimated` sub-total and a `platform_loss` sub-total (settle cap hits, negative-balance writes, late usage rows); `margin % = (revenue − cost) / revenue`; **one grouped query per (day, operation), no N+1** | README:90 (F15), README:199 (D11) ✅ |
| C10 | Indexes pinned for these query classes: `credit_ledger (user_id, created_at DESC, id DESC)`, `credit_ledger (reference_type, reference_id)`, `payments (status, created_at)`, `usage_records (user_id, operation, created_at)`, `refund_requests (status, created_at)`, `credit_grant_requests (status, requested_at)…` | README:95 (F20), README:202 (D17, 12.2-ledger/plan.md:42) ✅ |
| C11 | Metrics/alerts ride the existing 11.6.2-T1 pipeline only (no second pipeline); reuse `UnitOfWorkBehavior`, `ISystemWorkQuery.PendingAsyncT`, `[OutboxEvent]`+`outbox_messages`, `IEmailSender` template catalog (`trial_granted`), `IRealtimeNotifier` (ids only), `audit_logs` | README:202 (D17), README:96 (F21) ✅ |
| C12 | `credit_ledger` = `user_id, lot_id, kind (grant/bonus/purchase/reserve/settle/release/refund/adjust/expire), amount (signed bigint), price_version, reference_type, reference_id, idempotency_key (unique), created_by`; balance is **always** `SUM(amount)` | 00 §6:295, 12.2-ledger/plan.md:61 ✅ |
| C13 | Row shapes: reserve row `reference_type = CreditOperation` wire value, `reference_id = referenceId`, `amount = −reserve`, `lot_id = NULL`; the **reservation id *is* the `id` of the reserve row**; settle rows `reference_type = 'reservation'`, `reference_id = reservationId` | 12.2-ledger/plan.md:62,66–70 ✅ (⚠️ 12.2-ledger/plan.md:67–68 writes the magnitude (`+charged`, `+released`) without a final sign convention for the settle/release pair — see §3.5) |
| C14 | `usage_records` = `user_id, episode_id, render_job_id, reservation_id, provider, operation, model, input_tokens, cached_input_tokens, output_tokens, image_count, gpu_seconds, estimated (bool), cost_usd, price_version, provider_request_id`; the cost writer is 02 D14 only | 00 §6:294 ✅ |
| C15 | `payments` = `…, amount, currency, status (pending/paid/failed/needs_review/pending_approval/refunded/partially_refunded), package_id, credits, bonus_credits, price_version, promo_code_id, order_code (unique), …` + **CR-1** adds `expires_at`, `paid_at` + **CR-15** adds `fx_usd_vnd numeric(12,6)` (the rate actually paid at checkout, written once per payment — never re-read at query time) | 00 §6:300, README:234 (12 CR-1), README §4 CR-15 (BR8/BR30/BR31) ✅ |
| C16 | `credit_accounts` = `user_id (PK), updated_at` — nothing but the lock row; created lazily on the first grant; the tenant filter is **excluded** for this table | 00 §6:319, 12.2-ledger/plan.md:61 ✅ |
| C17 | `credit_grant_requests (target_user_id, amount, reason, source (admin_grant/promo_code), promo_code_id, requested_by, status, decided_by, decided_at, decision_note, activates_at)` | 00 §6:314 ✅ |
| C18 | 1 credit = US$0.01 of **price**; `k = (1+fx_buffer) / ((1−margin) × (1−fee_rate))`; `credits(C) = ceil(C × k ÷ credit_usd)`; rounding once per job; charge = `min(reserve, max(floor, credits(C)))` | README O-2 (README:383), D3 (README:132), plan §5.7 ✅ |
| C19 | `billing_enabled` (default false) gates **order creation only** (checkout, promo/redeem, buy UI). Never gated: webhook, payment reads/receipt, refund-request, the funding jobs — and, by the same rule, `GET /admin/usage` and the trial/cap logic are never behind the flag | README:410 (BR17), README:397 (O-7) ✅ |
| C20 | `users.email_verified_at` (09's registration flow, 09.3.1-T1 / 09.3.1-T3) is **required** by 12.4.2-T1, and 12 CR-12(b) states no further CR is needed for it; 09's audit catalog already contains `account.email_verified` | README:251 (12 CR-12b), 09 README:1107, 09 README:617 ✅ |
| C21 | 12.4.1-T1's `Blocks` is `—`; 12.4.2-T1 blocks **12.8.3-T1**; 12.5.2-T1 blocks **12.8.1-T1** and **12.8.4-T1** | README:341–343, README:345 ✅ |
| C22 | Gate `12-measure` (signed spreadsheet before any public price), `12-ledger` (staging: 20 parallel reserves never overdraw), `12-payments-sandbox` (**margin ≥ 60 % on measured costs before public prices**), `12-minting`, `12-funding-ops` | README:289–296 ✅ |
| C23 | Balance/history/quote are 12.2.2-T1 / 12.1.3-T1; the trial's user-visible surface is the balance widget (`GET /credits/balance`) plus the `trial_granted` e-mail; no new route is created by this folder | README §3.1 (README:207–212), D17 ✅ |

## 3. Domain and data

### 3.1 Per-user cap (12.4.1-T1)

Enforcement point: **inside** `CreditService.ReserveAsync`, after the `SELECT … FROM credit_accounts WHERE user_id = @u FOR UPDATE` serialisation point and before any lot/ledger write (D2/BR2 lock order). It is *not* a new `ICreditLedger` member: BR3 freezes the port at 02's three signatures + `GrantAsync`/`ExpireAsync`/`VoidPaymentLotsAsync`/`GetOpenReservationAsync` and says "no caller signature changes" (README:388) — the cap is enforcement inside the implementation, so every existing caller (02.2.x/03/05 paid commands) is protected with zero edits.

```sql
-- inside the reserve transaction, after the row lock, before the lot split
-- BR18: derived per reservation group, never a row count — a settled group counts charged = Σ|settle|,
-- an open group counts quoted = Σ|reserve|, a group refunded without a settle counts 0.
SELECT COALESCE(SUM(COALESCE(s.charged, abs(r.amount))), 0) AS used_today_credits
FROM   credit_ledger r
LEFT   JOIN (SELECT l.reference_id, SUM(abs(l.amount)) AS charged
             FROM   credit_ledger l
             WHERE  l.kind = 'settle' AND l.reference_type = 'reservation'
             GROUP  BY l.reference_id) s ON s.reference_id = r.id
WHERE  r.user_id = @userId
  AND  r.kind    = 'reserve'
  AND  r.created_at >= @dayStartUtc          -- date_trunc('day', now() AT TIME ZONE 'utc') AT TIME ZONE 'utc'
  AND  r.created_at <  @dayEndUtc            -- @dayStartUtc + 1 day
  AND  NOT EXISTS (SELECT 1 FROM credit_ledger f WHERE f.reference_id = r.id AND f.kind = 'refund');
```

* Window: the **UTC** day (D5's `retryAfterSeconds` is "the next UTC midnight", so the window must be UTC, not server-local — the `Trial`/expiry/notice jobs use server-local time, this one must not).
* **BR18 sign convention applied, derived per reservation group, never a row count** [supersedes the folder's earlier "gross reserved, independent of the sign convention" draft — C13 ⚠️ is resolved]: a group opened inside the UTC day counts `quoted = Σ|reserve|` while open, `charged = Σ|settle|` once settled, and `0` once refunded without a settle (BR18/D2's `reserve −R, settle −C, release +R, refund +R (no settle)`); the cap exists so that "a bug or abuser cannot burn the OpenAI budget" (plan §12.4.1), and a settled charge stays counted for the day even if a later refund returns the credits (refunds do not retroactively free budget already spent). The operational escape hatch is the configurable cap plus the 80 % alert; both are documented in §11.
* Held (paused) reservations count: they are real reserves (D15/F08 — credits are reserved, not charged), so a held job consumes the day's budget exactly once, and the sweep never expires it (BR5/BR14).
* Decision: `usedToday + requested > MaxCreditsPerUserPerDay` ⇒ refuse. Boundary: equal to the cap is allowed (`<=`).
* `MaxCreditsPerUserPerDay = 0` ⇒ **cap closed** (every paid command past zero credits is refused) — fail-closed [DECISION-L]; negative or absent ⇒ startup validation failure (`IOptions` `ValidateOnStart`), fail fast.
* Rejection shape: `ErrorOr` failure `spend_cap_reached` mapped to **429** ProblemDetails with extensions `retryAfterSeconds` (`ceil` of seconds to the next UTC midnight, never < 1) and `operation` (the `CreditOperation` wire value), plus a `Retry-After` response header [DECISION-L — `retryAfterSeconds` is pinned by D5, the header and `operation` are additive].
* 80 % alert: when a *successful* reserve crosses `0.8 × cap` for the first time in that UTC day, one alert is raised through 11.6.2-T1 with the stable dedupe key `credit_spend_cap_warn:user:{userId}:{utcDate}`; the block raises `credit_spend_cap_blocked:user:{userId}:{utcDate}` (also once per day, else a loop creates an alert storm). The alert pipeline owns dedupe (C11) — no new table, no cached flag.
* Metric: `billing_credit_cap_reached_total` (this cap's refusal) and `billing_spend_cap_hits_total{scope}` with `scope = user|platform` (BR30/CR-8 — both names frozen and owned in the metric registry by 12.5.2-T1; the platform half emits the same counters with `scope="platform"` through 02's guard so both caps are visible on one series).

### 3.2 Trial grant (12.4.2-T1)

`GrantTrialCreditsCommand(Guid UserId, TrialGrantTrigger Trigger)` — the **only** place a trial lot is created; `Trigger ∈ { EmailVerified, RecoverySweep, Deferred }` for observability only (never for eligibility).

Eligibility (`TrialEligibility.EvaluateAsync`, Application, pure + one read):

1. `users.email_verified_at IS NOT NULL` (C20) — else no-op `not_verified` (never an error: the caller is 09's verification flow and a race with a re-send must not 500).
2. e-mail domain ∉ the embedded disposable-domain list — else no-op `blocked_domain` + audit row + metric.
3. amount = settings key `registration.trial.credits` (09.10.6-T1, validated 0…10000), fallback `CreditOptions.TrialCredits`; `0` ⇒ trial **disabled** (no lot, no deferral, no e-mail) — the owner kill switch (§11).
4. no existing `grant:trial:{userId}` row (D2 idempotency, unique index) — else no-op `already_granted`; the unique index is the real guard under concurrency (`ON CONFLICT (idempotency_key) DO NOTHING`).
5. trial pause closed (`trial_grants_paused` false, CR-3 — read here through 09.9.1's cached flag service, **written by 12.8.3-T1**) — else **defer** (§5).

On grant: `ICreditLedger.GrantAsync(userId, CreditLotSource.Trial, amount, expiresAt: now + CreditOptions.TrialExpiryDays, reference: grant:trial:{userId})` inside the command transaction (the ledger creates the `credit_lots` row, source `trial`, **no** `payment_id`, and the `grant` ledger row) ⇒ one lot, one ledger row, balance moves; e-mail template `trial_granted` in `ui_locale` (D18) and the balance change rides the ledger's own `credit_balance_changed.v1 { userId }` outbox (12.2-ledger/plan.md:42) — 12.4.2-T1 publishes **no** second balance event [DECISION-L].

Disposable-domain list [DECISION-L]: one embedded resource list (`TrialBlockedEmailDomains`, one domain per line, lower-case ASCII/punycode, sub-domain match by suffix on a label boundary) so that checking it is allocation-free and offline. A settings key was rejected because it would need a 09.10 CR and a redeploy is cheap for a list that changes rarely; both alternatives are noted in the task file. Matching: exact + sub-domain, case-insensitive, IDN normalised to punycode before comparison; never log the local part of the address (privacy, §8).

Velocity guard: the registration/e-mail-verification endpoint carries the API's existing rate-limit policy (09) and 12.4.2-T1 registers one additional named policy `RateLimitPolicies.Trial` (partition = client-IP hash + the device cookie) intended for that endpoint. This is a **velocity** limit, not a durable device ledger: nothing in the schema can store a device fingerprint and this plan adds no table, so a determined attacker with fresh IPs and no cookies can still take a second trial [DECISION-L, consistent with plan §12.4.2's "[DECISION] simple list + rate limit"]. The 09-side attribute on the verification endpoint is **09's to own** (BR30) — this task registers the policy and files the request; if 09 does not apply the attribute, the durable per-user guard (§3.2.4) still holds and only the velocity guard is lost.

Recovery: `ITrialGrantCandidateQuery.PendingAsync(CancellationToken) → IReadOnlyList<Guid>` — **pinned, single member, never extended** (09.2.3-T1's house pattern, D17): verified users with no `grant:trial:{userId}` row and no open `trial_grant_requested.v1`. It is consumed by **12.8.3-T1**'s `FundingRunwayJob` (which is why 12.4.2-T1 *blocks* 12.8.3-T1, C21). This folder owns the query and the command; the job cadence, `[JobPolicy]` and the retry policy stay with 12.8.3-T1 (BR9 — no new queue, `JobQueue.Default` only).

### 3.3 Margin report (12.5.2-T1)

One SQL statement, one round trip, no N+1 (F15). Read through a pinned single-member port `IMarginReportQuery.MarginAsync(DateOnly from, DateOnly to, string? operation, CancellationToken) → IReadOnlyList<MarginRowDto>` implemented in Infrastructure with `Database.SqlQueryRaw<MarginRowDto>` (raw SQL on purpose: EF `Include`-style composition cannot express the `FILTER (WHERE …)` sub-totals in three queries without an N+1 — F15). *Abridged below: the executable statement (final `SELECT`, allocation window, platform-loss join) is written out in `12.5.2-T1-margin-report.md` §Design.*

```sql
WITH cost AS (                                                 -- per day × operation
  SELECT (u.created_at AT TIME ZONE 'utc')::date AS day,
         u.operation,
         COUNT(DISTINCT u.reservation_id)        AS jobs,
         SUM(u.cost_usd)                         AS cost_usd,
         SUM(u.cost_usd) FILTER (WHERE u.estimated) AS estimated_cost_usd
  FROM   usage_records u
  WHERE  u.created_at >= @from AND u.created_at < @toExclusive
    AND  (@operation IS NULL OR u.operation = @operation)
  GROUP  BY 1, 2
), charged AS (                                                -- settled charges, by the same day
  SELECT (l.created_at AT TIME ZONE 'utc')::date AS day,
         u.operation,
         SUM(abs(l.amount))                      AS credits_charged,
         SUM(u.cost_usd)                         AS settled_cost_usd
  FROM   credit_ledger l
  JOIN   usage_records u ON u.reservation_id = l.reference_id
  WHERE  l.kind = 'settle' AND l.reference_type = 'reservation'
    AND  l.created_at >= @from AND l.created_at < @toExclusive
    AND  (@operation IS NULL OR u.operation = @operation)
  GROUP  BY 1, 2
), platform_loss AS (                                          -- BR30: derived from the day's platform_loss ledger rows, no new table
  SELECT (l.created_at AT TIME ZONE 'utc')::date AS day,
         rv.reference_type                       AS operation, -- reserve row carries the CreditOperation wire value (C13)
         SUM(abs(l.amount) * pv.credit_usd
             / ((1 + pv.fx_buffer) / ((1 - pv.margin) * (1 - pv.fee_rate)))) AS loss_usd
  FROM   credit_ledger l
  LEFT   JOIN credit_ledger rv ON rv.id = l.reference_id
  LEFT   JOIN price_versions pv ON pv.version = l.price_version
  WHERE  l.reference_type = 'platform_loss'
    AND  l.created_at >= @from AND l.created_at < @toExclusive
    AND  (@operation IS NULL OR rv.reference_type = @operation)
  GROUP  BY 1, 2
), revenue AS (                                                -- payments, net of refunds, on the payment's own day
  SELECT (COALESCE(p.paid_at, p.created_at) AT TIME ZONE 'utc')::date AS day,
         SUM(CASE p.status
               WHEN 'paid'               THEN @toUsd(p.amount, p.currency)
               WHEN 'partially_refunded' THEN @toUsd(p.amount, p.currency) - COALESCE(r.refunded_usd, 0)
               ELSE 0 END)              AS revenue_usd
  FROM   payments p
  LEFT   JOIN (SELECT rr.payment_id, SUM(@toUsd(rr.amount, p2.currency)) AS refunded_usd
               FROM refund_requests rr JOIN payments p2 ON p2.id = rr.payment_id
               WHERE rr.status = 'approved' GROUP BY rr.payment_id) r ON r.payment_id = p.id
  WHERE  p.status IN ('paid','partially_refunded','refunded')
    AND  COALESCE(p.paid_at, p.created_at) >= @from AND COALESCE(p.paid_at, p.created_at) < @toExclusive
  GROUP  BY 1
)
SELECT COALESCE(c.day, ch.day, pl.day)                       AS day,
       COALESCE(c.operation, ch.operation, pl.operation)     AS operation,
       COALESCE(c.jobs, 0)                                   AS jobs,
       COALESCE(ch.credits_charged, 0)                       AS credits_charged,
       COALESCE(c.cost_usd, 0)                               AS cost_usd,
       COALESCE(c.estimated_cost_usd, 0)                     AS estimated_cost_usd,
       COALESCE(pl.loss_usd, 0)                              AS platform_loss_usd,   -- BR30: summed straight from the ledger
       <allocated revenue, each payment at its own fx_usd_vnd> AS revenue_usd,
       <margin, decimal?, null when revenue = 0>              AS margin_pct,
       NULL                                                  AS package_code,        -- BR30: always null on this route
       <fxRateUsed from IFxRateProvider.CurrentAsync(), read once> AS fx_rate_used,
       <fxRateDate: that rate's top-up date, or null on fallback>  AS fx_rate_date
FROM …
```

* `@toExclusive` = `to + 1 day` at 00:00 UTC; `from`/`to` are `DateOnly` **UTC** calendar days (the report's day is the UTC day, matching every other money timestamp in 12).
* `jobs` = `COUNT(DISTINCT usage_records.reservation_id)` per (day, operation) — the operation dimension is `usage_records.operation`'s pinned value set (`story`, `story_review`, `story_revision`, `fact_sheet`, `image`, `tts`, `render`, plus `bible_proposal`, `idea`, `source_find`, `moderation` from 02 CR-4, 00 §6:294).
* `creditsCharged` = magnitude of the **settle** rows for the reservations whose usage rows carry that operation. `abs()` is deliberate: 12.2-ledger/plan.md:67–68 states the magnitude (C13 ⚠️). An integration test pins the equivalence `report.creditsCharged == SettleResult.Charged` for one job, so a sign or shape change in the ledger falsifies the test instead of silently negating the margin column.
* `revenueUsd` **allocation**: payments are per *package*, not per operation, so a payment cannot be split by operation. Day revenue is allocated across the day's operation rows **by cost share**, with the rounding remainder added to the day's highest-cost operation row; the property `SUM(revenueUsd per day) == day revenue` is tested. `revenue` for a fully `refunded` payment is 0; a `partially_refunded` payment contributes `amount − approved refund_requests.amount`; refunds restate the **payment's own day** (documented, §Notes) [DECISION-L].
* FX (BR8/BR30, CR-15 — corrected from an earlier query-time draft): `@toUsd(amount, currency, fx_usd_vnd) = amount` for `USD`, `amount ÷ fx_usd_vnd` for `VND` (other currencies are impossible — `payments.currency` is set from the package), where `fx_usd_vnd` is **that payment's own stored column** (CR-15, C15), written once at checkout from `IFxRateProvider.CurrentAsync()` and never re-read. "Revenue at each payment's own rate" (F15) is therefore exact and stable under later FX moves — a top-up next week cannot restate last month's revenue. `IFxRateProvider.CurrentAsync()` (D16/BR31 — declared by 12.1.2-T1, DB-backed by 12.8.2-T1) is used only where no payment exists yet (e.g. `package_below_cost`, the funding view's live estimate); the margin report never calls it.
* `estimatedCostUsd` is a **sub-total of** `costUsd` (D11), never added again.
* `platformLossUsd` (BR30) is **derived straight from `credit_ledger` rows with `reference_type = 'platform_loss'`** — settle-cap hits, negative-balance writes, F25's late usage rows and void shortfalls, all written by 12.2.1-T1/12.6.1-T1 — converted to USD with the same price-equivalent arithmetic as `creditsCharged` (`× credit_usd ÷ k` from the row's `price_version`, C18). **No new table, no new column**, no second cost derivation; a reservation still open inside its BR5 stale margin has no `platform_loss` row yet (12.2.3-T1 only writes one once the margin passes unsettled), so it is not counted as loss until then.
* `marginPct = ROUND((revenueUsd − costUsd) / revenueUsd × 100, 2)`, and **`null` when `revenueUsd = 0`** (BR30: a cost-only day/operation row is not "−∞ %"; the DTO field is `decimal?`). `packageCode` is always `null` on this day × operation route (BR30 — 12.8.4-T1's package view sets it); `fxRateUsed`/`fxRateDate` are read once per request from `IFxRateProvider.CurrentAsync()` (BR7/D16/BR31), `fxRateDate` being that rate's top-up date or `null` on the option fallback.
* Ordering `day DESC, operation ASC`; the validator clamps the window to ≤ 90 days and requires `to >= from` (FluentValidation ⇒ the standard 400, no new error code); `operation` must be a member of the pinned set else 400 (silent empty results would hide typos). Max rows = 90 × 11 = 990 ⇒ no paging in the DTO (which has no cursor, C8).
* Performance: `credit_ledger (reference_type, reference_id)` and `payments (status, created_at)` serve the joins/range (C10). The cost scan filters `created_at` **without** `user_id`, so the pinned `usage_records (user_id, operation, created_at)` (leading `user_id`) cannot serve it — at M2 beta volume (≲ 10⁴ usage rows/day) the bounded scan is acceptable, and a candidate CR to F20 (`usage_records (created_at, operation) INCLUDE (cost_usd, estimated)`) is reported in §Notes (08.1.1-T1 has the same need, 12 CR-12(a)).
* Published interface for the consumers this task blocks: **12.8.1-T1** (`revenue7dUsd`, `revenue30dUsd`, `costUsd` for burn) calls `IMarginReportQuery.MarginAsync` and sums `revenueUsd`/`costUsd` per day — it must **not** write a second revenue query (F15); **12.8.4-T1** (the < 50 % package-margin alert) shares the same revenue definition: the SQL fragments (`toUsd`, the refund netting, the settle join) live in one internal Infrastructure helper class `MarginSql` so 12.8.4-T1's package grouping reuses the *definition* without a second port [DECISION-L — `MarginRowDto` has no `packageCode`, so the package dimension cannot live on this route; §Notes].

### 3.4 Tables touched (all read-only except the trial lot)

| Table | Access here | Notes |
|---|---|---|
| `credit_ledger` | read (`reserve` sum for the cap; `settle` for the report), write only via `ICreditLedger.GrantAsync` | C12; the cap read is inside 12.2.1-T1's transaction |
| `credit_accounts` | the existing `FOR UPDATE` lock (12.2.1-T1) | C16; 12.4.1-T1 adds no new lock and no new lock ordering |
| `credit_lots` | written by 12.2.1-T1/12.6.1-T1 for the trial lot | source `trial`, `expires_at = now + 30 d`, `payment_id NULL` |
| `usage_records` | read | C14; 02 D14 stays the only writer |
| `payments` | read | C15; `COALESCE(paid_at, created_at)` day attribution |
| `refund_requests` | read (net revenue) | grouped by `payment_id`, `status = 'approved'` |
| `users` | read `email_verified_at` only | C20 |
| `audit_logs` | written via `[Audited]` for trial grant/deny | C11/§8 |

### 3.5 Dependencies and interfaces (frozen for the consumers)

* From **12.2.1-T1** (hard): `CreditService.ReserveAsync` (cap inserted here), `GrantAsync`, the row shapes and idempotency keys of C12/C13, `SettleResult(long Charged, long Released)`, the `credit_accounts` lock, `credit_balance_changed.v1 { userId }`.
* From **02.2.2-T2** (hard, P): `IBudgetGuard.CheckAsync(CostBound, ct)` and its `spend_cap_reached` mapping — 12.4.1-T1 *adds a second gate*, it does not touch the first; both must answer the same code (C2/C3) and the platform guard keeps running first at dispatch.
* From **12.1.4-T1** (hard): `CreditCalculator` — 12.5.2-T1 reuses its `k`/`credits(C)` definition to convert `platform_loss` ledger rows to USD (no second implementation, D3).
* From **12.3.3-T1** (hard): the `paid`/`partially_refunded` statuses and lots that make revenue real; 12.5.2-T1 reads them, never writes.
* From **02.2.4-T1** (hard): `price_versions` with `credit_usd`, `margin`, `fx_buffer`, `fee_rate` for the `platform_loss` conversion.
* From **09.3.1-T1** (hard): `users.email_verified_at` and the verification flow that dispatches `GrantTrialCreditsCommand`.
* To **12.8.3-T1**: `trial_grant_requested.v1 { userId }` (outbox, ids-only) and `ITrialGrantCandidateQuery.PendingAsync`.
* To **12.8.1-T1 / 12.8.4-T1**: `IMarginReportQuery.MarginAsync` + `MarginSql`.
* Reused, never re-invented: `IOptions<CreditOptions>`, `IEmailSender` (`trial_granted`), `IRealtimeNotifier` (ids only), `IAlertService`-equivalent of 11.6.2-T1, `[Audited]`/`audit_logs`, `ISender`.

## 4. Use cases

**UC-1 — cap blocks a paid command.** Given a user whose current UTC-day reservation groups total `4,900` credits (BR18: settled groups counted at `charged = Σ|settle|`, open groups at `quoted = Σ|reserve|`) and `Credit:MaxCreditsPerUserPerDay = 5,000`; When `ReserveAsync` is asked for `200`; Then it returns `spend_cap_reached` → **429** with `retryAfterSeconds` to the next UTC midnight + `Retry-After`, no ledger row and no lot change, and the block alert key is raised once for that user and UTC day. (Boundary: `100` is allowed, i.e. `used + requested = cap` succeeds.)

**UC-2 — 80 % warning.** Given `usedToday = 3,999` (BR18-derived) and the cap `5,000`; When a `2`-credit reserve succeeds; Then `billing_spend_cap_hits_total{scope="user"}` and `billing_credit_cap_reached_total` are incremented and the alert `credit_spend_cap_warn:user:{userId}:{utcDate}` is raised **once** even if the user crosses 80 % again the same day.

**UC-3 — trial on verification.** Given a registered user whose `email_verified_at` was just set and whose domain is not blocked; When 09's verification flow dispatches `GrantTrialCreditsCommand`; Then exactly one `credit_lots` row (`source = trial`, `expires_at = now + 30 d`, `payment_id NULL`), one `credit_ledger` row (`kind = grant`, `idempotency_key = grant:trial:{userId}`), `credit_balance_changed.v1`, the `trial_granted` e-mail in `ui_locale` and one `audit_logs` row exist; When the same command is dispatched again (webhook-less retry, sweep, double click); Then nothing moves and the result is a no-op.

**UC-4 — deferred trial while paused.** Given the `trial_grants_paused` flag (CR-3, written by 12.8.3-T1) is open; When a verified user's trial is triggered; Then **no** lot is created, one `trial_grant_requested.v1 { userId }` outbox row is written, and the admin alert is not raised; When the flag closes and 12.8.3-T1's `FundingRunwayJob` applies the event; Then the grant runs exactly once (UC-3) and the deferred marker closes.

**UC-5 — disposable domain.** Given `mailinator.com` in the embedded list; When `trial@mailinator.com` verifies; Then no lot, `billing_trial_grant_total{result="blocked_domain"}` increments, an `audit_logs` row records the **domain** (never the local part) and the user still gets the product (they just have to buy credits; the response is not an error).

**UC-6 — margin report.** Given a day with a `paid` VND payment, one `partially_refunded` payment with an approved refund request, a settled `render` reservation with 2 usage rows (one `estimated`) and one failed `story_generate` whose usage rows exist but whose reservation never settled; When an admin calls `GET /admin/usage?from=&to=`; Then one `MarginRowDto` per (day, operation) comes back in a single query: `revenueUsd` nets the refund, `estimatedCostUsd` is a sub-total of `costUsd`, `platformLossUsd` sums the day's `credit_ledger (reference_type = 'platform_loss')` rows — the unsettled story cost and the settle-cap hit (BR30) —, `creditsCharged` equals `SettleResult.Charged`, `SUM(revenueUsd) == day revenue`, and `marginPct = null` for the cost-only operation row.

**UC-7 — authorization.** Given a plain authenticated user; When they call `GET /admin/usage`; Then 403 and no rows are read; Given an `admin`; Then 200. Given a range of 91 days or `to < from`; Then 400 with the standard validation ProblemDetails.

**UC-8 — cap accuracy under concurrency.** Given 20 parallel reserves of 300 credits from the same user with a 5,000 cap and 10,000 credits of balance; When they run concurrently; Then at most 17 succeed and the sum of the successful reserves never exceeds 5,000 (the `credit_accounts` row lock serialises the read-and-write) and every refusal is `spend_cap_reached`.

## 5. API and events

**Routes.** No new route is created by this folder. The only route it owns is the frozen admin one:

| Route | Verb | DTO | Notes |
|---|---|---|---|
| `/admin/usage` | GET | `{ items: MarginRowDto[] }`; `MarginRowDto { day, operation, jobs, creditsCharged, revenueUsd, costUsd, estimatedCostUsd, platformLossUsd, marginPct }` | `?from=&to=&operation=`; `from`/`to` are UTC `DateOnly` (default: the last 30 days, `to` = today); range ≤ 90 days; `Admin` policy; **never** behind `billing_enabled` (C19); no cursor (C8) |

Everything the *user* sees is read from 12.2.2-T1's `GET /credits/balance` / `/credits/ledger` and 12.1.3-T1's `GET /billing/quote` — this folder changes none of their fields; the trial appears as an ordinary `trial`-source credit in the existing balance/history payloads (C23).

**ProblemDetails.** `spend_cap_reached` **429** — `code = "spend_cap_reached"`, extensions `retryAfterSeconds` (int, ≥ 1, to the next UTC midnight — D5) and `operation`; `Retry-After` header in seconds. Both caps produce the identical body shape so 10.8.x has one copy (C2/C3). No new error code is introduced: `validation_failed`-shaped 400 for the report's range/operation validation comes from the standard FluentValidation path (§5.3 has no range code, C8/plan §5.3).

**Events published.** `trial_grant_requested.v1 { userId }` — outbox, `[OutboxEvent]` + `outbox_messages`, **ids only**, published when the trial is deferred (UC-4), consumed by 12.8.3-T1. No other event is published: the trial's balance change rides 12.2.1-T1's `credit_balance_changed.v1 { userId }` (C11/C13). The cap publishes nothing (it is a rejection, and the alert rides 11.6.2-T1).

**Consumed.** None. (`trial_grant_requested.v1` is *consumed* by 12.8.3-T1, not here.)

## 6. UI

No UI task lives in this folder (owner rule 2026-09-25: every UI feature folder has its own `<story>-T0 — UI design` task and its implementation tasks depend on it; the areas here are BE). The UI that consumes this folder:

| Consumer (S, not yet detailed) | Consumes | Notes |
|---|---|---|
| 10.8.x user money screens | `GET /credits/balance`, `/credits/ledger`, `/billing/quote` (unchanged shapes) | the trial lot shows up as ordinary history; the pricing/credit copy already states expiry (D18) |
| 10.8.x error surfaces | the 429 body of UC-1 | copy key `billing.errors.spend_cap_reached` with the retry **time** interpolated from `retryAfterSeconds`; the API supplies the value, 10 supplies the vi+en sentence (D18) |
| 10.8.x (or the admin S screens) admin usage page | `GET /admin/usage` | must render `marginPct = null` (cost-only rows) as "—", `estimatedCostUsd`/`platformLossUsd` as secondary lines, `day` in the user's locale; a11y per 10.1.3 (no colour-only margin status) |

The frozen wire for all of them is README §3.1 (12 CR-9); this folder adds no field to a 10-facing DTO and asks 10 for no change.

## 7. Jobs and integrations

**No job is registered by these three tasks** (BR9: all 12 jobs run on `JobQueue.Default`, no new queue, `[DisableConcurrentExecution]` for sweeps). Specifically:

| Task | Job involvement |
|---|---|
| 12.4.1-T1 | none — the cap runs inside the command transaction |
| 12.4.2-T1 | none owned; it *feeds* 12.8.3-T1's `FundingRunwayJob` (`[JobPolicy(Queue = JobQueue.Default, Retries = 3)]`, hourly — D7) with `trial_grant_requested.v1` and `ITrialGrantCandidateQuery.PendingAsync`; the per-user grant loops dispatch one `GrantTrialCreditsCommand` per user through `ISender` in its own `ISystemContext.For(userId)` scope (one `SaveChangesAsync` per command, `UnitOfWorkBehavior`, D17) |
| 12.5.2-T1 | none — a read-only admin query; 12.8.4-T1's alerting job consumes the definition, not the route |

Integrations: `IOptions<CreditOptions>` / `IOptions<FundingOptions>` (D10, `ValidateOnStart`), 09's feature-flag store (`trial_grants_paused`, CR-3, read here/written by 12.8.3-T1, via 09.9.1) and settings key `registration.trial.credits` (09.10.6-T1), `IEmailSender` (`trial_granted`), 11.6.2-T1 alerts (F21 keys), `IFxRateProvider` (D16), `IAuditWriter`/`[Audited]`, `[OutboxEvent]` + the Worker's outbox dispatcher, `IRealtimeNotifier` (ids only), 02's `IBudgetGuard` (untouched), OpenAI budget semantics unchanged.

## 8. Security and privacy

* **Tenant filter.** The cap query takes `userId` from the authenticated principal (`ICurrentUser`), never from the request; it runs inside 12.2.1-T1's transaction which already holds the `credit_accounts` row lock — no second lock, no new lock order (BR2). `credit_accounts` keeps the tenant filter **excluded** with the documented comment (C16).
* **The margin report is the only user-filter-free read in this folder.** It is reachable only through `GetMarginReportQuery`, guarded by the `Admin` authorization policy at the endpoint (defence in depth: the policy is on the endpoint *and* asserted by an architecture test), and the query class is registered on the allow-list of system reads that may run without a tenant filter (F10's rule: a global read must be explicit, never accidental). It returns **aggregates only** — no user id, no e-mail, no payment row — so an admin cannot use it to read another user's money.
* **Authorization.** `/admin/usage` requires the admin policy; the report is read-only; no owner requirement (D11 places it on the admin surface). A non-admin gets 403 **before** any SQL runs.
* **Audit.** `GrantTrialCreditsCommand` is `[Audited]` (`credit.trial_granted`; `credit.trial_denied` with a reason for the blocked-domain/no-amount paths) — exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command (09.3.6-T1's scan). `GetMarginReportQuery` is `[NotAudited]` (a read). The cap refusal writes an audit row only when the configured cap is lower than the credits requested? No — a refusal is not a mutation: it is logged (see below) and alerted, no `audit_logs` row [DECISION-L]. The admin's own report views are covered by 09's request logging.
* **Secrets.** None. This folder reads no credential; the trial grant and the report touch no provider secret; **no Data Protection purpose is added** (nothing is encrypted or stored as a protected payload here — the outbox payload is an id).
* **Privacy.** The trial denial logs and audits the **domain** only, never the local part or the full address; the embedded list is a code artefact (no PII). The report is aggregate-only. No IP is persisted (the rate limiter's partition is ephemeral by design — §3.2).
* **Abuse controls.** Per-user daily credit cap (fail-closed), disposable-domain list, velocity policy, one-trial-per-user idempotency, admin alert at 80 %/100 %, and the minted digest (12.5.1-T2) covering the *granted* side.
* **i18n vi + en.** `trial_granted` e-mail (D18's template catalog) in `ui_locale`; the 429's user-facing sentence lives in 10.8.x (`billing.errors.spend_cap_reached`, both locales); no server-rendered money text is added here.

## 9. Test plan

**NightTales.Domain.UnitTests** — `TrialEligibilityTests`: verified gate; domain matching (exact, sub-domain on a label boundary, `MAILINATOR.COM`, `mailinator.com.evil.com` **not** matched, punycode/IDN normalised); amount 0 ⇒ disabled; amount clamped by 09.10.6-T1's 0…10000 range. `CreditSpendCapTests`: boundary (`used + requested == cap` allowed, `+1` refused), `retryAfterSeconds` = seconds to the next UTC midnight for 23:59:30 and 00:00:00 UTC (never < 1), 80 % threshold crossing including the already-above case.

**NightTales.Application.UnitTests** (hand-written fakes from `NightTales.TestKit`: `FakeCreditLedger`, `FakeEmailSender`, `RecordingJobScheduler`, `FakeClock`, `FakeFeatureFlags`, `FakeSettingsReader`; **no mocking library**): `GrantTrialCreditsCommandHandlerTests` — verified ⇒ one `GrantAsync(source = Trial, expiresAt = clock + 30 d, reference = grant:trial:{userId})`; unverified ⇒ zero ledger calls and no e-mail; second invocation ⇒ the idempotency key already exists ⇒ no second call; flag open ⇒ `trial_grant_requested.v1` published, no `GrantAsync`; amount from the settings key, fallback to `CreditOptions.TrialCredits`, and the owner's 0 ⇒ nothing; blocked domain ⇒ no `GrantAsync`, audit + metric. `GetMarginReportQueryValidatorTests` — `to < from`, 91-day range, unknown `operation`, default window.

**NightTales.Api.IntegrationTests** (real PostgreSQL 17, `PostgresFixture`, real migrations, seeded tenants):
1. reserve at/over the cap → 429 `spend_cap_reached` with `retryAfterSeconds` = seconds to the next UTC midnight (± 2 s), `Retry-After` set, **no** ledger row and unchanged lots;
2. 20 parallel reserves (UC-8) → successes ≤ floor(cap / amount) and the reserved sum ≤ cap;
3. cap counts a held (paused-flag-open) reservation once (F08);
4. cap disabled by a large configured value and closed by `0` (fail-closed) — both from a test config override;
5. trial e2e: register → verify → exactly one `credit_lots` row + ledger row + `trial_granted` e-mail row + `credit_balance_changed.v1` + one `audit_logs` row; replay → no second movement;
6. paused trial → `trial_grant_requested.v1` in `outbox_messages` and no lot; then flag cleared + the event applied once ⇒ the UC-3 state (the application itself is 12.8.3-T1's test, this one asserts only that the event exists and is unique);
7. disposable domain → no lot, audit row with the **domain** only, metric;
8. margin report over a seeded day → revenue nets an approved refund; `creditsCharged == SettleResult.Charged`; `estimatedCostUsd ≤ costUsd`; `platformLossUsd` = unsettled cost + settle-cap hit; `SUM(revenueUsd) == day revenue`; `?operation=` filters; `marginPct` null on a cost-only row; two CTE halves agree on the day/operation key set;
9. authorization: plain user ⇒ 403 with no SQL (assert via a recorded query log/`EXPLAIN`-free spy on the pinned query), admin ⇒ 200; invalid range/operation ⇒ 400;
10. query count: the whole report is **one** SQL statement — asserted by a command-interceptor counter (no N+1).

**NightTales.Architecture.Tests** — `GrantTrialCreditsCommand` carries exactly one of the three attributes; `GetMarginReportQuery` is `[NotAudited]`; the margin query class is on the user-filter-free allow-list and is called only by `GetMarginReportQuery`; no session-scoped `pg_advisory_lock` appears in 12 (BR2); `IMarginReportQuery`/`ITrialGrantCandidateQuery` each have **one** member (pinned-never-extended rule); no new `JobQueue` value and no new job registration from this folder (BR9).

**Coverage floor:** cap boundary + UTC window + concurrency, trial idempotency + each no-op reason, deferred event uniqueness, revenue netting, the four platform-loss components measured above, and the allocation invariant. No test asserts incidental defaults (no "row count grew"), no test pins source text, and no test re-asserts wiring.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase / flag | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.4.1-T1 | S (0.5) | BE | S · M2 | 12.2.1-T1, 02.2.2-T2 | — |
| 12.4.2-T1 | S (0.5) | BE | S · M2 | 12.2.1-T1, 09.3.1-T1, 09.10.6-T1 (soft) (09 W3 CR-13) | 12.8.3-T1 |
| 12.5.2-T1 | M (1.0) | BE | S · M2 | 12.2.1-T1, 12.1.4-T1, 12.3.3-T1, 02.2.4-T1 | 12.8.1-T1, 12.8.4-T1, 08.1.1-T1, 10.9.2-T1 (10/08 CR-hand-off) |

Folder total **2.0 d raw** (0.5 + 0.5 + 1.0), matching README §8.1 and §9's "12.4 Limits + abuse 1.0 unchanged / 12.5 Admin + reporting 2.5 unchanged" split (this folder holds one task of each). No re-estimate is requested by this folder — the README §9 numbers already include these sizes; plan 12 total 25.0 d raw / 30.0 buffered (O-6, 12 CR-4). No task exceeds M; no `billing_enabled` flag appears in any header (C19). Task files: `12.4.1-T1-per-user-spend-cap.md`, `12.4.2-T1-trial-grant.md`, `12.5.2-T1-margin-report.md`.

## 11. Rollout

**Order.** `12.2.1-T1` (ledger) → `12.4.1-T1` and `12.4.2-T1` in the same release as the ledger: the cap must exist *before* the first credit can be spent in production (otherwise the only backstop is 02's platform USD cap), and the trial is the first real credit anyone gets, so both belong to the `12-ledger` staging gate. `12.5.2-T1` ships before `12.8.1-T1`, which displays its revenue/cost numbers.

**Config to set (D10, `Credit` section).** `MaxCreditsPerUserPerDay = 5000` (beta default; raise deliberately, never silently), `TrialCredits` = the Short-tier value from 12.1.1-T1's signed spreadsheet (O-3: one short episode), `TrialExpiryDays = 30`, `FxUsdVndFallback` set (also the report's FX fallback, D16). The effective trial amount is the owner-editable key `registration.trial.credits`; `0` disables the trial. All are `ValidateOnStart`.

**Gates this folder must pass.** `12-ledger` (staging): cap refuses correctly under concurrency, the trial grants exactly once, its lot expires at 30 days, the deferred path works with the pause flag; `12-measure`: the trial seed amount comes from the signed spreadsheet before any public price; `12-payments-sandbox`: **margin ≥ 60 % on measured costs** as reported by `GET /admin/usage` before public prices; `12-funding-ops`: the deferred trial below 3 days of runway behaves (12.8.3-T1's test).

**Migration.** **None** — no schema change in this folder (the tables and indexes are created by 12.2.1-T1/12.6.1-T1 per D17/F20). Rollback is a config change, not a deploy: raise `MaxCreditsPerUserPerDay`, set `registration.trial.credits = 0`, and the report is read-only.

**Observability.** New metrics `billing_spend_cap_hits_total{scope}` (`user`+`platform`) and `billing_credit_cap_reached_total` (BR30/CR-8, registry-owned by 12.5.2-T1); `billing_trial_grant_total{result}` (granted/deferred/already/blocked_domain/blocked_disabled); reused `billing_credits_minted_total{actor="trial"}` (F21) and `billing_balance_liability_credits`; alerts through 11.6.2-T1 with the stable dedupe keys `credit_spend_cap_warn:user:{userId}:{utcDate}` and `credit_spend_cap_blocked:user:{userId}:{utcDate}`; structured logs `billing.credit_cap` (userId, operation, used, limit, decision — no PII) and `billing.margin_report` (from, to, operation, rows, durationMs, fxSource); the `12-measure`/`12-payments-sandbox` reviewer reads `GET /admin/usage` in staging before any public price.

**BR30/BR31 landed (no longer gaps).** `MarginRowDto` now carries `packageCode | null`, `fxRateUsed`, `fxRateDate` and a nullable `marginPct` (BR30); `platformLossUsd` is derived from `credit_ledger (reference_type = 'platform_loss')` rows — no schema change, no double-counting of the liability metrics; this folder owns the pinned index `usage_records (created_at, operation)` (12 CR-13, shared with 08.1.1-T1) instead of the `(user_id, operation, created_at)` index that could not serve the range scan; the cap metrics are the frozen names `billing_spend_cap_hits_total{scope}`/`billing_credit_cap_reached_total` (BR30/CR-8, declared in 12.5.2-T1's registry); the per-user cap is now derived per reservation group per BR18 (§3.1), resolving the earlier "sign-agnostic" caveat. **Notes / gaps still reported to the README (not edited by this folder):** (1) the velocity guard needs a rate-limit policy attribute on 09's e-mail-verification endpoint — that attribute is **09's to own** (BR30), this folder only registers the policy and files the request; if 09 does not apply it, the durable per-user guard still holds; (2) the revenue-only-day marker `operation = 'all'` is a wire *value* addition 10's admin page must render specially.
