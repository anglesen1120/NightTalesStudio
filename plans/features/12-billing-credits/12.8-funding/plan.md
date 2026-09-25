# 12.8 — Funding the AI providers: runway, top-ups, circuit breaker, FX (S)

> Contract: [`../README.md`](../README.md) — facts C1–C16, findings F01–F22, decisions D1–D18, routes/DTOs §3.1, canonical rows §8.1, rulings BR1–BR17, gates §7, re-estimate §9.
> Phase: **S · M2** for all six tasks (D1). This folder registers **no new job queue** (D7/BR9); every job runs on `JobQueue.Default`.
> Implements D15 (runway, top-up by set-aside gap, FX from the last top-up), D16 (circuit breaker: `OpenAiErrorClassifier`, MPT error-text mapping, two probes on resume, 12 h cancel, `processingPaused`) and D17 (alerts through 11.6.2-T1 only).
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope.** The owner-funded side of the platform: the balance snapshot + runway read model and `GET /admin/funding` (12.8.1-T1); top-up records, the weekly set-aside report and the effective FX rate source (12.8.2-T1); the runway alerts with trial pause and deferred trial grants, and the `POST /admin/funding/resume` route with its two probes (12.8.3-T1); the circuit breaker itself — `OpenAiErrorClassifier`, MPT error-text mapping, held-row tracking, the dispatch gate on `ICreditLedger.GetOpenReservationAsync`, the 12 h cancel + refund (12.8.3-T2); the 1-image probe through one healthy render node and the `node_image_project_unfunded` reason (12.8.3-T3); and `IFxRateProvider.CurrentAsync()` plus the 7-day price-review alert (12.8.4-T1).

**Out of scope — owned elsewhere, never re-implemented here.**

| Area | Owner | Boundary this folder consumes |
|---|---|---|
| Margin/revenue aggregation (per day × operation, `estimated`, `platform_loss`) | 12.5.2-T1 (D11, F15) | the funding view **calls** it; no second aggregation, no second revenue definition (BR8) |
| `price_versions`, `credit_usd`, `margin`, `fx_buffer`, floors | 12.1.2-T1 / 12.1.4-T1 (D3/D4) | read-only; `margin` feeds `setAside` and `package_below_cost` |
| Reserve/settle/release/refund, ledger rows, stale sweep | 12.2.1-T1 / 12.2.3-T1 (D2) | `GetOpenReservationAsync` is the dispatch gate; the sweep skips held rows (F08/BR5/BR14) |
| Paid-command wiring (`[PaidOperation]`), 202 + reserve while paused | 12.2.4-T1 (D2/11 CR-11) | the pause branches inside those handlers; this folder provides the flag and the reason |
| Trial grant on e-mail verification, `grant:trial:{userId}` | 12.4.2-T1 (D5) | T1 applies only **deferred** grants and owns the pause decision (see §3.4) |
| Payment provider, checkout, webhook, refunds, receipts | 12.3.x / 12.7.x (D8–D12) | untouched; funding never creates a payment |
| Jobs `CreditReconciliationJob`, `CreditExpiryJob`, `LowBalanceNotifierJob`, `CreditsMintedDigestJob` | 12.2.3-T1, 12.6.2-T1, 12.6.3-T1, 12.5.1-T2 (D7) | not touched; only the held-row skip is co-signed with 12.2.3-T1 |
| Node registry, health poller, pinned-TLS connection factory | 11.3.3-T1 | `worker_nodes.state = 'active'` + `last_heartbeat_at`, `RenderNodeConnectionFactory` |
| Alert pipeline and rules | 11.6.2-T1 (C13/D17) | one pipeline only; this folder adds no second one |
| Money screens (funding panel, paused banner, alert console) | 10.8.x (S) / 11 admin UI | §6 names the consumers; the UI design task lives there (owner rule 2026-09-25) |

**Non-goals.** No provider balance **read** API call (`OPENAI_ADMIN_KEY` stays ⚠️ [OPEN], D10 — balance is entered manually); no OpenAI auto-reload API call (a *documented* owner decision, plan §5.5); no new error code (plan 12 §5.3: `provider_funds_exhausted` is not an error response); no second metrics/alert pipeline; no new job queue; no session-level advisory lock (F02/BR2).

## 2. Confirmed facts

| Id | Fact used here | Evidence |
|---|---|---|
| F-A ✅ | Tables fixed: `provider_balance_snapshots (provider, balance_usd, recorded_at, recorded_by, note)` and `provider_topups (provider, amount_usd, amount_vnd, fx_rate, paid_at, reference, recorded_by)`; `usage_records.cost_usd`/`estimated`; `credit_lots.remaining/expires_at/voided_at`; `payments.status`; `price_versions.margin/fx_buffer/credit_usd`; `generation_runs.pause_reason`; `feature_flags` | 00 §6:294/295/296/300/303/304/320 ✅ (README C1/C2/C7) |
| F-B ✅ | `IFeatureFlags.IsEnabledAsync(string key, ct)` (09.9.2-T2, 30 s cache, `FeatureFlagsChanged` evicts); `IFeatureFlagWriter.SetAsync(string key, bool enabled, CancellationToken ct)` (09.9.2-T1, writes `feature_flags` + `feature_flags_changed.v1 { Key, Enabled, OccurredAt }` + cache eviction); flag **creation** stays with the CLI `nts-worker flags set <key> on|off` | 09 README BD13/D14 ✅ (README D15 names 09.9.1 as the flag store) |
| F-C ✅ | `provider_funds_exhausted` already exists in P as `generation_runs.pause_reason`; 03.4.2-T1 raises `ProviderFundsExhausted { Source = "image" }`; 02 maps 429 `*_spend_limit_exceeded` / `credit_balance_exhausted` → `ProviderFundsExhausted` | 02 README D2 ✅, 03.4.2-T1:61 ✅, 02.2.1-T2:48/109 ✅ (README C9) |
| F-D ✅ | `processingPaused` (null or `provider_funds_exhausted`) is carried by `GET /episodes/{id}` and `EpisodeUpdated`; transitions `story_generating → draft \| story_review`, `rendering → story_approved` on failed/cancelled/paused > 12 h with credits refunded | 00 §7/§8 ✅ (README C3) |
| F-E ✅ | Reusable surfaces: `[JobPolicy(Queue = JobQueue.Default, Retries = 3)]`, `[DisableConcurrentExecution]`, ids-only job args, per-row commands through `ISender`, `ISystemWorkQuery.PendingAsyncT` + `ISystemContext.For(userId)`, `UnitOfWorkBehavior` as the only `SaveChangesAsync`, `[Audited(Category.…) ]`, `IEmailSender.QueueAsync`, `IReauthGuard.RequireRecentAsync(5 min)`, outbox `[OutboxEvent]` | 09 README C10/D11 ✅ (README D17, D12) |
| F-F ✅ | Outbox names frozen: `trial_grant_requested.v1 { userId }`, `provider_funds_exhausted.v1 { source, openedAt }`, `provider_funds_resumed.v1 { resumedAt, dispatched }` | README D17 ✅ |
| F-G ✅ | Pinned indexes: `provider_topups (paid_at DESC)`, `usage_records (user_id, operation, created_at)`, `credit_lots (user_id, expires_at) WHERE voided_at IS NULL`, `payments (status, created_at)`, `credit_ledger (reference_type, reference_id)` | README F20 ✅ |
| F-H ✅ | `FundingOptions` (section `Funding`): `WarnRunwayDays` 7, `CriticalRunwayDays` 3, `MaxPrefundBurnDays` 60, `SetAsideTopUpDays` 14, `FixedMonthlyCostUsd`, `AutoReloadMonthlyLimitUsd`; `CreditOptions.FxUsdVndFallback` | README D10 ✅ |
| F-I ✅ | `FundingRunwayJob` — hourly, 3 retries, covers "runway, alerts, trial pause/resume, deferred trial grants, 12 h held-job cancel"; funding tasks are **never** behind `billing_enabled` | README D7 ✅, §8.1 note ✅ |
| F-J ⚠️ | The provider balance **read** API is unverified; snapshot entry stays manual [DECISION]; the adapter's balance field is never read | README D10/D11 ✅, P4 ⚠️ |
| F-K ⚠️ | The MPT failure text that carries OpenAI's `credit_balance_exhausted` is **not** confirmed — the 05 spike (05.4.1-T1/T2) must pin it; until then the ≥ 3 image-stage failures in 10 min + probe path is the detector | README D15 ⚠️, 05 README §1/§6.2 |
| F-L ✅ | `worker_nodes(id, name, base_url, api_key_ciphertext, api_key_hint, capacity, state, last_heartbeat_at, gpu_model, …)`, `state ∈ active/draining/down`, node down after 2 min without a successful health pull; the node's image key/OpenAI project is node-managed and **never** stored in our DB | 11 plan L79/L184/§6 ✅, 11 README D13 |
| F-M ⚠️ | `billing_openai_runway_days` is the only runway metric in F21; there is **no** metric or rule for a margin drop, and the e-mail catalogue in D17 has no template for a 12 h cancel or a price review | README F21/D17 ✅ (gap recorded in §11) |

## 3. Domain and data

### 3.1 Tables this folder writes (only two)

`provider_balance_snapshots` (append-only, admin-entered) and `provider_topups` (append-only, admin-entered) — exactly the 00 §6:303/304 columns; no column is added, no second table, no cached balance column anywhere.

```sql
-- migration `_12_ProviderFunding` — the ONLY migration for this area, owned by 12.8.1-T1 alone (BR20: one text per area,
-- not one per task); all three indexes below land in this single text, even though CR-13 tags the last one "(12.8.2-T1)"
-- to name which task's feature needs it, not a second migration owner.
CREATE INDEX ix_provider_snapshots ON provider_balance_snapshots (provider, recorded_at DESC);   -- CR-13
CREATE INDEX ix_provider_topups_paid_at ON provider_topups (paid_at DESC);                        -- F20 ✅
CREATE UNIQUE INDEX ux_provider_topups ON provider_topups (provider, reference);                  -- CR-13 (needed by 12.8.2-T1)
```

`provider_balance_snapshots` / `provider_topups` are **not** `IUserOwned`: they are registered outside the tenant filter with an explicit comment and asserted by a test (F10 pattern) — an admin-scope table must never be readable user-side by a filter accident. Neither table is created in P (00 §6 / 09 D9: S-only tables).

### 3.2 Derived values (read model, no stored columns)

| Value | Formula | Source |
|---|---|---|
| `lastBalanceUsd`, `lastBalanceAt` | last row per provider | `SELECT balance_usd, recorded_at FROM provider_balance_snapshots WHERE provider = 'openai' ORDER BY recorded_at DESC LIMIT 1` |
| `estimatedBalanceUsd` | `lastBalanceUsd − recordedCostSince + topUpsSince` (D11) | `SUM(usage_records.cost_usd) WHERE created_at > @lastBalanceAt` + the GPU share from 12.5.2-T1's query; `SUM(provider_topups.amount_usd) WHERE paid_at > @lastBalanceAt` |
| `burn7dUsd` | mean daily provider cost over the last 7 complete UTC days | 12.5.2-T1's per-day cost rows (README D11: 7-day average daily burn) |
| `runwayDays` | `estimatedBalanceUsd / burn7dUsd` — **`null` when `burn7dUsd = 0`** (never `∞`, never a divide-by-zero) | [INFERENCE] the README names the field, not the divide-by-zero rule |
| `outstandingCredits` | `SUM(credit_lots.remaining) WHERE voided_at IS NULL AND expires_at > now()` | `credit_lots` |
| `outstandingCostUsd` | `outstandingCredits × price_versions.credit_usd × (1 − margin)` of the active version — the *cost to serve* of the liability the platform already sold | [INFERENCE] D11 names the field; the formula follows `costShare = 1 − margin` (D11) |
| `revenue7dUsd`, `revenue30dUsd` | per payment `amount → USD` at **that payment's own** rate `payments.fx_usd_vnd` (CR-15), `paid`/`partially_refunded`, net of refunds (BR8) | 12.5.2-T1's query — never `credits × price`, never the current rate (BR31) |
| `fxUsdVnd`, `fxRateSource`, `fxRateDate` | `IFxRateProvider.CurrentAsync()` — `fxRateSource` ∈ `topup`\|`fallback`, `fxRateDate` = the top-up's `paid_at` or null (BR31) | D11 |
| `fixedMonthlyCostUsd`, `autoReloadMonthlyLimitUsd` | `FundingOptions` | D10 |

The funding query issues **at most 6 statements** (last snapshot, cost since, top-ups since, burn rows, outstanding credits, active `price_versions`) in one read-only transaction, all bounded by a time window; it never loops per row and never calls the margin query per day (F20 rule).

### 3.3 FX rate — one source of truth (D16, F14, BR7)

```csharp
// Application/Abstractions/… — DECLARED by 12.1.2-T1 (BR31); shown here only as the shape this folder consumes.
public sealed record FxRateSnapshot(decimal UsdVnd, FxRateSource Source, DateTimeOffset? AsOf);   // [INFERENCE] member names
public enum FxRateSource { TopUp, Fallback }        // wire values frozen by BR31: fxRateSource = "topup" | "fallback"
public interface IFxRateProvider { Task<FxRateSnapshot> CurrentAsync(CancellationToken ct); }
```

- `FxRateProvider` (the **DB-backed** implementation) is owned by 12.8.2-T1 (BR31) and reads the latest `provider_topups.fx_rate` with `ORDER BY paid_at DESC, created_at DESC LIMIT 1` (F14 verbatim); the port itself is declared by **12.1.2-T1** with an options-backed implementation, so every earlier consumer (12.5.2-T1, 12.7.1-T1) compiles before this folder lands and no new edge is created.
- Fallback `Credit:FxUsdVndFallback`, **validated at boot** by the options-backed implementation (12.1.2-T1, `ValidateOnStart`; range `> 0`, e.g. `[15_000, 40_000]`) — a bad value must fail the host, never silently price with 0; 12.8.4-T1 asserts that validation with a test instead of re-declaring it.
- A new top-up updates the rate **implicitly**: the `RecordTopUpCommand` (12.8.2-T1) evicts the cached entry in the same request; no config write, no task to run (D16). Cache TTL 60 s as a backstop.
- `fx_buffer` is **not** here — it stays a `price_versions` parameter (BR7).
- `package_below_cost` and the margin report read this same port; `fxUsdVnd` in the funding view is the same call, so the price check and the report can never disagree.
- The view also reports the rate's **date** (F14: "the funding view shows the rate's source and date"): `fxRateSource` carries `<source>:<yyyy-MM-dd>` — `provider_topups` + `paid_at`, or `fallback` (no date). ⚠️ D11's frozen field list has **no** separate date field; see §11 item (1).

### 3.4 Runway, trial pause and the pause flag

- Runway is computed **only** by `FundingRunwayJob` (F-I) and read from the funding query; two thresholds come from `FundingOptions`: `< WarnRunwayDays` (7) → warn, `< CriticalRunwayDays` (3) → critical. Sales never stop (O-5).
- Below `CriticalRunwayDays`: trial grants pause. 12.4.2-T1 defers the grant and writes `trial_grant_requested.v1 { userId }` (D5); this folder **applies** deferred grants once runway ≥ `CriticalRunwayDays`, always with the frozen idempotency key `grant:trial:{userId}` (D2 — the high-level plan writes that key without the `grant:` prefix, which BR15 supersedes).
- The pause state must survive a Worker restart and be readable by the email-verification path, so it is a **flag key**, written with `IFeatureFlagWriter.SetAsync` (F-B, `feature_flags_changed.v1` + cache eviction): `trial_grants_paused` (default off, created by `nts-worker flags set trial_grants_paused off` — creation stays with the CLI, 09 D14). ⚠️ This key is **not** in the contract's frozen flag list; see §11 item (2).
- `provider_funds_exhausted` (the exhausted-funds latch of D15) is a **separate** flag: it is opened by a trip (12.8.3-T2) and closed only by an explicit resume (12.8.3-T1) — the runway job never sets or clears it.

### 3.5 Held rows (F08, F26, BR14, BR29)

A paid command that runs while `provider_funds_exhausted` is open reserves the credits and creates its row `queued` (00 §7 render chain: `queued → submitted → running → ingesting → succeeded | failed | cancelled`) — story runs carry `generation_runs.pause_reason = provider_funds_exhausted` (00 §7 value, ✅ constrained in P), and **render rows carry no pause column at all**: BR29/F26 rule that the render hold is **derived** — `status = 'queued'` ∧ the flag `provider_funds_exhausted` is open ∧ `ICreditLedger.GetOpenReservationAsync` still returns an open reservation — so no `render_jobs` column is added or invented, and `processingPaused` stays a derived wire value (00 §8, never a column). Consequences this folder owns:

- The stale-reserve sweep (12.2.3-T1) and the 12 h cancel skip **any** reservation whose owning row matches the held predicate, at any age (BR5/BR14) — a held reservation is never expired, and a row that is `submitted`/`running` is never cancelled by the shadow of the flag.
- The render dispatch/claim query (09.6.3-T1's `SubmitRenderJob`/`RenderPollingService` claim) must **exclude** the held predicate; otherwise a paused render is still claimed and dispatched (BR29).
- The 12 h deadline is the frozen option `Credit:HoldCancelHours` (12, BR29) — never a literal in code.
- `credit_ledger` sign convention is BR18 and is restated here **verbatim** because 12.8 reads the same numbers: "reserve −R, settle −C, release **+R** (exactly, written only with settle), refund +R (no settle), grant/bonus/purchase +, expire −remaining, adjust ±; per reservation `SUM(amount)` ∈ {−R open, −C settled, 0 refunded}; DTO values are **derived per reservation group** (`quoted`, `charged`, `returned`)". This folder writes exactly one movement — `refund:{reservationId}` (**+R**, no settle) — and derives nothing else.

### 3.6 Resume re-check and the 12 h cancel

- On resume, dispatch re-checks `ICreditLedger.GetOpenReservationAsync(referenceId, ct)`; a missing/closed reservation cancels the job instead of dispatching it (no free provider spend).
- The 12 h cancel writes `refund:{reservationId}` exactly once (BR18's +R, no settle row), cancels the row, moves the episode per 00 §7 and notifies the user; the alert/notification rides the frozen `billing_held_jobs_cancelled_total` rule (CR-8).

## 4. Use cases

| # | Story | Flow | Task |
|---|---|---|---|
| UC-1 | 12.8.1 — "how long does my balance last" | owner enters a snapshot → view shows last/estimated balance, `burn7dUsd`, `runwayDays`, liability, `outstandingCostUsd`, `revenue7d/30d`, `fixedMonthlyCostUsd`, FX + its source, `autoReloadMonthlyLimitUsd` | 12.8.1-T1 |
| UC-2 | 12.8.2 — "how much of each sale goes to the API" | owner records a top-up (provider, USD, VND, rate, date, reference) → row stored, FX evicted (next call reads the new rate), `setAside = revenue × (1 − margin)`, `gap` drives the next amount | 12.8.2-T1 |
| UC-3 | 12.8.3 — runway alerts | runway < 7 d warn (11.6.2-T1); < 3 d critical + `trial_grants_paused` on; a user verifying e-mail then gets `trial_grant_requested.v1`; when runway ≥ 3 d the deferred grant is applied once (`grant:trial:{userId}`) | 12.8.3-T1 |
| UC-4 | 12.8.3 — resumed dispatch | owner tops up, calls `POST /admin/funding/resume` (re-auth ≤ 5 min): 1-token text probe **and** 1-image probe through one healthy node; both pass ⇒ flag closes, queued jobs dispatch **in order**, each re-checking its reservation; text passes but image fails ⇒ flag stays open, reason `node_image_project_unfunded`, alert | 12.8.3-T1 + 12.8.3-T3 |
| UC-5 | 12.8.3 — trip and 12 h cancel | `OpenAiErrorClassifier` sees `credit_balance_exhausted`, or `PollRenderJob` maps an MPT failure carrying that code, or ≥ 3 image-stage failures in 10 min confirmed by a 1-token paid probe ⇒ flag opens + `provider_funds_exhausted.v1 { source, openedAt }` + P1 alert; held rows mark `pause_reason`; rows still `queued` 12 h after `openedAt` are cancelled, refunded once, e-mailed | 12.8.3-T2 |
| UC-6 | 12.8.4 — FX and price review | every consumer of a VND price reads `IFxRateProvider.CurrentAsync()`; margin < 50 % on any package for 7 consecutive days ⇒ price-review alert to the owner | 12.8.4-T1 |

## 5. API and events

All routes are admin-surface (09.9.1-T1 group/policy; a non-admin gets **404**, never 403 — 09 README D5) and **never** behind `billing_enabled` (§8.1 note). Mutations are `[Audited(Category.Billing, Visibility.Internal)]` (shape per 09.9 D5) and take the actor id from `IUserContext`, never from the body.

| Route | Verb | Request | Response | Errors | Task |
|---|---|---|---|---|---|
| `/admin/funding` | GET | — | `FundingOverviewDto` (D11's field list as amended by BR31 — now 14, incl. `fxRateDate`) | `forbidden` 403 (non-admin, mapped to 404 by the admin group) | 12.8.1-T1 |
| `/admin/funding/balance-snapshots` | POST | `BalanceSnapshotInput { balanceUsd, note }` | 200 `FundingOverviewDto` (post-snapshot) | 400 `validation_failed` (`balanceUsd < 0`, `> 1_000_000`, `note` > 500) | 12.8.1-T1 |
| `/admin/funding/topups` | GET | `?from=&to=` (default: `SetAsideTopUpDays` = 14) | `SetAsideReportDto { revenue, setAside, actualCost, actualTopUps, gap }` | 400 `validation_failed` (window > 180 d) | 12.8.2-T1 |
| `/admin/funding/topups` | POST | `TopUpInput { provider, amountUsd, amountVnd, fxRate, paidAt, reference }` | 200 `SetAsideReportDto` (recomputed **after** the insert) | 400 `validation_failed` (`amountUsd <= 0`, `amountVnd <= 0`, `fxRate <= 0`, `paidAt` > now + 1 d, `provider` not `openai`) | 12.8.2-T1 |
| `/admin/funding/resume` | POST | — | 200 `{ flag, textProbe, imageProbe, reason? }` (§3.1, verbatim) | 403 `forbidden` (not owner), 401 `reauth_required` (`IReauthGuard.RequireRecentAsync(5 min)`) | 12.8.3-T1 (+ T3 for `imageProbe`) |
| `/admin/usage` | GET | `?from=&to=&operation=` | `{ items: MarginRowDto[] }` | — | **not this folder** — 12.5.2-T1 (D11) |

- `flag` values: `provider_funds_exhausted` (still open) or `open` [INFERENCE — the resumed value's spelling is not frozen; the frozen name is the flag key `provider_funds_exhausted`].
- `textProbe` / `imageProbe` ∈ `{ passed, failed, skipped }` [INFERENCE]; `skipped` is used when the text probe already failed and the image probe was not attempted.
- `reason` is present only when the flag stays open: `provider_funds_exhausted` (text probe failed — funds really are gone) or `node_image_project_unfunded` (text passed, image failed — D15's frozen reason).
- **A failing probe is not an HTTP error**: the route returns 200 with the probe result and the flag unchanged (plan 12 §5.3: `provider_funds_exhausted` is never an error response). Existing frozen codes only: `forbidden` 403, `reauth_required` 401, `validation_failed` 400. This folder adds **no** code.
- `GET /admin/funding` is the only read in this folder that the UI drives; `TopUpInput.provider` is the provider allow-list value `openai` (00 §6:303), not a payment provider (that is `PaymentOptions.ActiveProvider`, D8).

**Outbox events.**

| Event | Emitted by | Payload | Consumed by |
|---|---|---|---|
| `provider_funds_exhausted.v1 { source, openedAt }` | 12.8.3-T2 (trip) | `source ∈ openai_credit_balance_exhausted \| mpt_error_text \| image_failure_storm` [INFERENCE member spelling; `source = "image"` is the P value raised by 03.4.2-T1 ✅] | notify stage (P1 alert through 11.6.2-T1), 10.8.4 banner data, 02.8.3 pause |
| `provider_funds_resumed.v1 { resumedAt, dispatched }` | 12.8.3-T1 (successful resume) | `dispatched` = number of jobs re-dispatched in order | dispatch stage, `EpisodeUpdated` refresh |
| `trial_grant_requested.v1 { userId }` | 12.4.2-T1 (deferred grant) | `{ userId }` | **this folder** — 12.8.3-T1's handler applies it when runway ≥ 3 d |

No new event name is introduced; `feature_flags_changed.v1 { Key, Enabled, OccurredAt }` (09.9.2-T1) is what a flag write emits and is reused as-is.

**Idempotency.** No ledger movement happens in this folder, so the frozen ledger keys are not used except `grant:trial:{userId}` (the deferred grant) and `refund:{reservationId}` (the 12 h cancel, owned by 12.8.3-T2). The snapshot and top-up rows are deduped by the `ux_provider_topups (provider, reference)` index (CR-13, created in 12.8.1-T1's `_12_ProviderFunding` migration): a repeated `reference` returns the existing row's report as a **no-op** (a second identical insert is never a second top-up), matching the claim-first spirit of BR1.

## 6. UI

This folder is **BE-only** and adds no UI task: BR22/§7 `12-ui-gate` fix that 12 has **no** `-T0` task, and the consumer screens cannot start implementation before **`10.8.1-T0`** (user billing: packages, checkout, payments, refund request, balance/ledger) and **`10.9.1-T0`** (admin: payments resolve, packages, promo codes, grants, refunds, **funding**, usage) are owner-approved. This folder only freezes the routes/DTOs those gates consume.

| Surface | Consumer | What it needs from this folder |
|---|---|---|
| Paused banner + episode state | 10.8.4 (D15's frozen copy: "Processing is paused. Your credits are reserved, not charged yet") | `processingPaused = provider_funds_exhausted` on `GET /episodes/{id}` / `EpisodeUpdated` (00 §8, already designed by 09/02) |
| Funding panel (balance, burn, runway, liability, FX, auto-reload limit) | 10.9 / 11 admin UI (S) | `GET /admin/funding` (`FundingOverviewDto`) |
| Set-aside + top-up form | 10.9 / 11 admin UI (S) | `GET\|POST /admin/funding/topups`, `POST /admin/funding/balance-snapshots` |
| Resume button (owner, re-auth) | 11 admin UI (S) | `POST /admin/funding/resume` + the `{ flag, textProbe, imageProbe, reason? }` result |
| Alerts (runway warn/critical, price review, P1 trip) | 11.6.2-T1 console / e-mail | metric `billing_openai_runway_days` + the alert rules of §7 |

i18n: any label the ops screens render uses the frozen `admin.billing.*` key space (D18); this folder ships no user-facing text of its own except the e-mail templates in §7. No colour-only status, focus order and label rules follow 10.1.3 (D18).

## 7. Jobs and integrations

### 7.1 `FundingRunwayJob` (hourly, 3 retries — D7)

```csharp
[JobPolicy(Queue = JobQueue.Default, Retries = 3)]
[DisableConcurrentExecution(timeoutInSeconds: 600)]
public sealed class FundingRunwayJob(IEnumerable<IFundingRunwayStage> stages) { public Task RunAsync(CancellationToken ct); }
public interface IFundingRunwayStage { Task RunAsync(CancellationToken ct); }
```

- Ids-only arguments (none), one command per row through `ISender` (`UnitOfWorkBehavior` is the only `SaveChangesAsync`) — F-E.
- Stages are contributed by task, so the dependency direction of §8.1 is respected (12.8.3-T2 adds its stage; it does not modify T1's stage implementations):
  - `RunwayAlertStage` (12.8.3-T1): compute runway, emit the warn/critical alert through 11.6.2-T1 only, write `trial_grants_paused` when crossing the threshold, apply deferred `trial_grant_requested.v1` grants when runway ≥ 3 d (`grant:trial:{userId}`).
  - `HeldJobCancelStage` (12.8.3-T2): rows matching the derived held predicate (F26/BR29 — `status = 'queued'`, flag open, reservation still open via `GetOpenReservationAsync`; `generation_runs.pause_reason` for story runs) held longer than `Credit:HoldCancelHours` (12) → cancel + `refund:{reservationId}` once + notify (`billing_held_jobs_cancelled_total`) + `EpisodeUpdated`.
  - `PriceReviewStage` (12.8.4-T1): margin per package for the last 7 complete days < 50 % on any package → price-review alert.
- Two ports are declared by this folder's tasks and implemented by the partner task: `IProviderFundsProbe` (declared 12.8.3-T1 — `ProbeTextAsync` implemented there over `OpenAiTextClient`, `ProbeImageAsync` implemented by 12.8.3-T3 over `RenderNodeConnectionFactory`) and the `IFeatureFlags`-backed dispatch gate `ProviderFundsDispatchGate` (12.8.3-T2) sitting on the three dispatch paths, which owns the `ICreditLedger.GetOpenReservationAsync` re-check and the `refund:{reservationId}`-once cancel.
- Sweeps read ids with `ISystemWorkQuery.PendingAsyncT` (pinned, never extended) then open a per-user `ISystemContext.For(userId)` scope — a global read without a scope would silently load 0 rows (F10).
- FakeClock covers runway windows; a stage test calls the stage's `RunAsync` directly with a recording `IJobScheduler` fake (06 YR27) — Hangfire delayed jobs are not used here, so the delayed-job/FakeClock trap does not apply.

### 7.2 Integrations touched

| Integration | Direction | Contract |
|---|---|---|
| 09.9.1 flag store (`IFeatureFlags` / `IFeatureFlagWriter`) | write on trip/resume and on the trial-pause threshold; read by the paused dispatch paths and by the e-mail-verification trial path | F-B; flag creation stays with the CLI |
| 11.6.2-T1 alert pipeline | outbound only | frozen keys: warn `billing_openai_runway_days < 7`, critical `< 3`, P1 when `provider_funds_exhausted` opens, rule `billing_price_review_required` (any package below 50 % margin for 7 days), rule `billing_held_jobs_cancelled_total` (the 12 h held-job cancel and its e-mail) — all per 12 CR-8 |

**Flag rows are a stated precondition (fail-closed, not fail-silent).** 09.9.2-T2's `IFeatureFlags.IsEnabledAsync(key, ct)` returns **false** for a key with no row and 09.9.2-T1's `IFeatureFlagWriter.SetAsync` cannot create one (creation stays with `nts-worker flags set`, 09 D14). Therefore the rows `provider_funds_exhausted` (seeded false) and `trial_grants_paused` (CR-3, default off) must exist: without the first, a trip would silently no-op and the platform would keep spending; without the second, the trial pause would never engage. The runbook creates them (§11 step 1), the trip command treats a "no row" answer from the writer as a **loud failure** (error log + P1 alert, never a silent skip), and an integration test asserts both rows exist in a migrated+seeded database (09.2.5-T1 seed or the CLI step).
| 11.3.3-T1 node registry | read one **healthy** node (`state = 'active'`, `last_heartbeat_at` within 2 min) for the image probe | F-L |
| 05.4.1-T2 `render-node.v1` + 03's image relay | the image probe spends exactly one image on the node's own key through the node **image relay** (`images.v1`, `127.0.0.1:${ImageRelay:Port}` default 8091, inside the adapter — 03 D5/D6, 05 CR-5); `render-node.v1`'s `POST /v1/renders` requires the full D3 key set, so a probe route must be added by a **CR to 05.4.1-T2 (+11.3.5)** | §11 item (2), F-K ⚠️ |
| 12 h cancel e-mail | the alert/rule key is frozen (`billing_held_jobs_cancelled_total`, CR-8); the **user** e-mail template is not in D17's catalogue → proposed CR extending 09.2.6-T3's catalogue (09 W3 CR-8 precedent) — no key is invented in code | §11 item (3) |
| 02's `ProviderFundsExhausted` signal + 03.4.2-T1's `ProviderFundsExhausted { Source = "image" }` | inbound trigger | F-C |
| `IEmailSender` templates | runway critical e-mail `funding_runway_critical` (D17 ✅); the deferred trial grant's `trial_granted` e-mail is 12.4.2-T1's, not this folder's | D17 catalogue ✅ |

### 7.3 Configuration

`FundingOptions` (F-H) is the only new section; `Credit:FxUsdVndFallback` and the BR29 addition `Credit:HoldCancelHours` (12) are reused/frozen by other tasks; `Budget:*` unchanged; **no new secret** — the balance is manual, so `OPENAI_ADMIN_KEY` stays unused and ⚠️ [OPEN] (D10). Both options types are bound with `ValidateOnStart` (a running platform must never price or set-aside with a zeroed threshold). A `FundingOptions` invariant is asserted at boot: `CriticalRunwayDays < WarnRunwayDays < MaxPrefundBurnDays`; `Credit:HoldCancelHours > 0` and greater than the hourly job cadence.

## 8. Security and privacy

| Concern | Control |
|---|---|
| Authorization | every route on the 09.9.1 admin group; non-admin ⇒ 404 (not 403, 09 D5); `POST /admin/funding/resume` additionally **owner-only** + `IReauthGuard.RequireRecentAsync(5 min)` (D12/D15) — a compromised session cannot un-pause spending |
| Audit | every mutation `[Audited(Category.Billing, Visibility.Internal)]` with the actor id from `IUserContext`: who entered which balance, which top-up (amount, rate, reference), when the flag was opened/closed by whom |
| Tenancy | `provider_balance_snapshots` / `provider_topups` are admin-scope, registered **outside** `IUserOwned` with an explicit comment (F10) and covered by a test that a user-scoped query cannot see them; all reads of user data (deferred trial grants) go through `ISystemContext.For(userId)` |
| Secrets | the funding paths use **no** provider secret; if a balance API is ever enabled, `OPENAI_ADMIN_KEY` (D10) is read through the 11 D15 secret manager pattern (Keychain, `nts/<env>/api/…`) and never logged — [OPEN] |
| Logging | ids only (`userId`, `reservationId`, `jobId`, `topUpId`); never a bank reference in full, never a raw MPT/provider body, never the flag writer's payload beyond key/enabled |
| Money safety | the resume path is the only way spending restarts, and it requires two independent probes; a failed probe leaves the flag **open** (fail-closed). The 12 h cancel refunds exactly once (`refund:{reservationId}`) so a user is never charged for work that never ran |
| Input validation | `balanceUsd ∈ [0, 1_000_000]`, `amountUsd ∈ (0, 100_000]`, `amountVnd ∈ (0, 5_000_000_000]`, `fxRate ∈ (0, 100_000]`, `paidAt <= now + 1 d`, `reference` ≤ 128 chars |

## 9. Test plan

| Project | Focus | Named tests |
|---|---|---|
| `NightTales.Domain.UnitTests` | `OpenAiErrorClassifier` is pure | `Classify_CreditBalanceExhausted_ReturnsFundsExhausted`, `Classify_SpendLimitExceeded_ReturnsFundsExhausted` (02's 429 mapping, F-C), `Classify_MptErrorTextContainingCode_ReturnsFundsExhausted`, `Classify_UnrelatedText_ReturnsOther`, `Classify_NullOrEmpty_ReturnsOther` |
| `NightTales.Application.UnitTests` (hand-written fakes from `NightTales.TestKit`, no mocking library) | pure math + handler behaviour | `Runway_NoSnapshot_ReturnsNullBalanceAndNoAlert`, `Runway_ZeroBurn_ReturnsNullRunwayDaysNotInfinity`, `Runway_BelowWarn_EmitsWarnOnly_AtCritical_EmitsCriticalAndPausesTrial`, `EstimatedBalance_AddsTopUpsAndSubtractsCostSince`, `SetAside_EqualsRevenueTimesOneMinusMargin`, `SetAside_GapIsCostMinusTopUps`, `Fx_RateFromLatestTopUp_FallbackWhenEmpty`, `Fx_InsertEvictsCachedRate`, `Resume_TextProbeFailed_KeepsFlagOpenWithProviderFundsExhausted`, `Resume_TextPassedImageFailed_KeepsFlagOpenWithNodeImageProjectUnfunded`, `Resume_BothPassed_ClosesFlagAndDispatchesInOrder`, `Resume_SkipsJobWhoseReservationIsGone`, `HeldCancel_RefundsOnce_AndEmails`, `DeferredTrialGrant_AppliedOnce_WhenRunwayRecovers`, `PriceReview_MarginBelow50For7Days_Alerts`, `PriceReview_6Days_DoesNotAlert` |
| `NightTales.Api.IntegrationTests` (Testcontainers/PostgreSQL 17) | wire shapes + authz + indexes | `Funding_NonAdmin_Returns404`, `Funding_Admin_ReturnsFundingOverviewDto`, `Snapshot_InvalidAmount_Returns400ValidationFailed`, `TopUp_DuplicateReference_DoesNotInsertSecondRow`, `TopUp_UpdatesFxRateForNextCall`, `Resume_WithoutRecentReauth_Returns401ReauthRequired`, `Resume_NonOwner_Returns403Forbidden`, `Resume_ProbeFailures_Return200WithFlagStillOpen`, `FundingOverview_UsesIdxProviderTopupsPaidAt` (EXPLAIN shows the pinned index) |
| `NightTales.Architecture.Tests` | boundaries | the funding routes are in the 09.9 admin route table (09.2.3-T2 test enumerates every admin route), `provider_balance_snapshots`/`provider_topups` are not `IUserOwned`, no session `pg_advisory_lock` anywhere in 12 (BR2 grep test) |
| Worker tests (existing host test project, `NightTales.TestKit` fakes) | job/stage wiring | `FundingRunwayJob_RunsEveryStage_WhenOneFailsOthersStillRun`, `HeldJobCancelStage_UsesPendingAsyncTThenSystemContext_NoGlobalRead` (F10 regression), `RunwayAlertStage_AlertGoesThrough1162Pipeline_NoSecondSink` |

Times are absolute (`FakeClock`); no test depends on wall-clock or on the machine's locale. Every test is deterministic and isolated (fresh schema per fixture, no shared mutable state).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.8.1-T1 | M (1.0) | BE | S · M2 | 12.5.2-T1, 02.2.4-T1 | 12.8.2-T1, 12.8.3-T1, 12.8.4-T1, 10.9.2-T1 (10/08 CR-hand-off) |
| 12.8.2-T1 | S (0.5) | BE | S · M2 | 12.8.1-T1 | 12.8.4-T1, 10.9.2-T1 (10/08 CR-hand-off) |
| 12.8.3-T1 | S (0.5) | BE | S · M2 | 12.8.1-T1, 12.4.2-T1, 09.9.1-T1, 09.10.6-T1 (soft) (09 W3 CR-13) | 12.8.3-T2, 12.8.3-T3, 09.10.3-T1 (soft) (09 W3 CR-13), 08.2.2-T1 (08 W3 CR-hand-off), 10.9.2-T1 (10/08 CR-hand-off) |
| 12.8.3-T2 | M (1.0) | BE | S · M2 | 12.8.3-T1, 12.2.1-T1, 12.2.4-T1, 09.6.3-T1, 05.4.1-T2 (soft) | — |
| 12.8.3-T3 | XS (0.25) | BE | S · M2 | 12.8.3-T1 (soft), 11.3.3-T1, 05.4.1-T2 | — |
| 12.8.4-T1 | S (0.5) | BE | S · M2 | 12.1.1-T1 (soft), 12.5.2-T1, 12.8.1-T1, 12.8.2-T1 | — |

Rows equal README §8.1 verbatim (BR15). Folder total **3.75 d raw** (3.25 high-level + 0.5 for `12.8.3-T2 S → M`, CR-4): 1.0 + 0.5 + 0.5 + 1.0 + 0.25 + 0.5.

Graph edges (README §5, L270–274): `12.5.2-T1 → 12.8.1-T1`; `12.8.1-T1 → 12.8.2-T1`; `12.8.1-T1 → 12.8.3-T1`; `12.8.3-T1 → 12.8.3-T2`; `12.8.3-T1 → 12.8.3-T3`; `12.8.4-T1 ← 12.5.2-T1 + 12.8.1-T1 + 12.8.2-T1`; `12.4.2-T1 → 12.8.3-T1`; `12.2.4-T1 → 12.8.3-T2`. No cycle passes through a 12.8 task.

**Sequencing.** (1) 12.8.1-T1 after 12.5.2-T1's margin query exists — the funding view is a thin read model over it, and the set-aside share must be the *same* margin (one definition, BR8). (2) 12.8.2-T1 immediately after: without top-up rows there is no FX rate and no set-aside history — and the FX port (12.8.4-T1) is meaningless before the first row exists. (3) 12.8.3-T1 needs the runway read (12.8.1-T1), the deferred-grant producer (12.4.2-T1) and the flag store (09.9.1-T1); it owns the resume route. (4) 12.8.3-T2 is the largest and last-blocking piece: the trip needs the classifier plus the dispatch gate (`ICreditLedger.GetOpenReservationAsync`, 12.2.1-T1) and the paid-command wiring (12.2.4-T1); the MPT error-text mapping is soft on 05.4.1-T2 (⚠️ F-K). (5) 12.8.3-T3 is deliberately tiny and **soft** on 12.8.3-T1 so it can be built against T1's declared port while the node adapter is finished. (6) 12.8.4-T1 closes the loop only after 12.8.2-T1 (the rate source) and 12.8.1-T1 (the view's FX fields) exist.

**Re-estimate note (CR-4).** The +0.5 on 12.8.3-T2 covers the flag lifecycle across three dispatch paths (text job, render submit, MPT poll), held-row tracking, the reservation re-check on resume, the 12 h cancel, the MPT error-text mapping and the two-probe resume ordering (F08) — this is a behaviour surface, not a single function, and it is the reason the epic is 3.75 rather than 3.25.

## 11. Rollout

1. **Before the code runs.** Create the flag rows with the operator CLI (creation is never an endpoint, 09 D14 — see §7.2's precondition): `nts-worker flags set provider_funds_exhausted off` and `nts-worker flags set trial_grants_paused off` (the key is CR-3's). Bind `FundingOptions`; boot fails on an invalid `Credit:FxUsdVndFallback` or `CriticalRunwayDays >= WarnRunwayDays`.
2. **Staging (`12-payments-sandbox`, README §7).** The funding view shows a runway once one snapshot exists; a fake `credit_balance_exhausted` trips and clears the breaker end-to-end (flag → 202 + reserve + `queued` → resume with two probes → ordered dispatch, each re-checking its reservation); the 12 h cancel is exercised with a shifted clock (`Credit:HoldCancelHours`); the margin report shows ≥ 60 % on measured costs before public prices.
3. **Before public sales (`12-funding-ops`, README §7).** The owner records a **real** top-up; the set-aside report matches the bank; the 7-day and 3-day alerts fire (verified by shifting the burn window, never by lowering the thresholds in production); the auto-reload monthly limit is documented in the funding view/runbook; staging and production use separate OpenAI projects (plan §5.5).
4. **Runbook (owner-facing).** Trip → P1 alert → top up OpenAI → record the top-up → `POST /admin/funding/resume` (re-auth) → confirm both probes; if `reason = node_image_project_unfunded`, fund the node's image project/key (11.3.2/11.3.3) and resume again; held rows past `Credit:HoldCancelHours` were already cancelled and refunded (`billing_held_jobs_cancelled_total`) and their users notified.
5. **Rollback.** Closing the flag manually (`nts-worker flags set provider_funds_exhausted off`) restores dispatch; no job registration, migration or config change must be reverted for that (the tables are append-only, the flag is data). `trial_grants_paused` defaults to off.
6. **Carried to the TL (reported, not fixed here).**
   (1) `provider_funds_exhausted` needs a **seeded** row (09.2.5-T1 seed or the CLI in step 1); CR-3 covers only `billing_enabled`/`trial_grants_paused` — the latch row's creation should be added to the same CR so a fresh environment cannot start with a trip that silently no-ops.
   (2) 12.8.3-T3's image probe needs a route the adapter does not have: `render-node.v1`'s `POST /v1/renders` requires the full D3 key set, so spending exactly one image on the **node's** key needs a probe mode on the adapter (targeting the 03 D5/D6 image relay, `images.v1`, `127.0.0.1:${ImageRelay:Port}` 8091) — proposed CR to 05.4.1-T2 + 11.3.5; until it lands the probe reports `imageProbe = unavailable` and the latch stays open (fail-closed).
   (3) D17's e-mail catalogue has no **user** template for the 12 h held-job cancel (the alert key `billing_held_jobs_cancelled_total` is frozen by CR-8) — proposed CR extending 09.2.6-T3's catalogue in the 09 W3 CR-8 style; no key is invented in code.
   (4) 12.8.3-T2 edits three files owned outside this folder — the paid generation job (02), 09.6.3-T1's `SubmitRenderJob`/`RenderPollingService` claim and `PollRenderJob` — plus 02's Polly non-retry assertion; these need CR rows naming those task ids (CR-11 style), since §8.1's row for 12.8.3-T2 lists no 02 edge.
   (5) The high-level plan's §12.8.3 writes the trial idempotency key without the `grant:` prefix; README D2/D5 and BR15 fix `grant:trial:{userId}` — this folder uses the README form.
