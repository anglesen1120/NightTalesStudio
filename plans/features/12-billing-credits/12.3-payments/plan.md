# 12.3 — Payments (provider port, checkout, webhooks, refunds/chargebacks)

> Detailed plan for epic **12.3** of `../../12-billing-credits.md` (00 §4.1 **S**). Contract: `../README.md` — facts §1 (C1–C16, P1–P5), findings §2 (F01–F26), decisions §3 (**D1–D18**), frozen routes/DTOs §3.1, canonical rows §8.1, ruling §10 (O-1…O-8, **BR1–BR32**). Authority order: `DECISIONS.md` + `plans/00-overview.md` > `plans/12-billing-credits.md` > `../README.md` > this file.
> Phase **S · M2**, flag `billing_enabled` (**order creation only** — `POST /billing/checkout`; never on the webhook/refund path, BR10/BR17). This folder owns 5 tasks / **4.0 d raw**; the epic row in §9 (4.75 d) additionally contains `12.3.5-T1` + `12.3.5-T2` (0.75 d), which live in `../12.3.5-review-refunds/`.
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

In scope (this folder):

| Task | Deliverable |
|---|---|
| 12.3.1-T1 | Provider evaluation scored from **dated primary pages**; the dated choice is recorded in `DECISIONS.md` (CR-10). No code (BA, 0.5 d) |
| 12.3.2-T1 | `IPaymentProvider` port + capability contract + contract-true fake, `POST /billing/checkout`, the `payments` row with `expires_at`, `Idempotency-Key` replay, the `IPromoValidator` **declaration** (BR32), return-URL wiring; **not** `GET /billing/packages` (12.7.1-T1 owns it — BR32) |
| 12.3.3-T1 | `POST /billing/webhooks/{provider}` — raw-body signature verification, claim-then-act, amount/currency verification, status re-fetch, grant (purchase + bonus lots), `payment_paid.v1`, realtime |
| 12.3.3-T2 | `PaymentStatusSyncJob` (10 min: pending expiry + status re-fetch) and `PaymentAnomalyJob` (15 min for 24 h after a `payment.*` setting change) |
| 12.3.4-T1 | Refund/chargeback events: void **every** lot of that `payment_id` (bonus included) via `ICreditLedger.VoidPaymentLotsAsync`, `refunded`/`partially_refunded`, platform-loss on shortfall, account review flag on chargeback, `account_under_review` on paid commands |

Out of scope (owned elsewhere, consumed here): the ledger and the account row lock (`12.2.1-T1`), lots/spend order (`12.6.1-T1`), packages catalogue (`12.7.1-T1`), promo validation (`12.7.2-T1`), payment history + receipt (`12.7.3-T1`), bank-QR orders (`12.7.4-T1`), user refund requests and admin approve/reject (`12.7.5-T1`), `needs_review` resolve and `pending_approval` (`12.3.5-T1/-T2`), funding/circuit breaker (`12.8.x`), all 10 S UI (`10.8.x`).

Fixed negatives: **no vendor SDK** in the repository (F18 — `HttpClient` + `System.Security.Cryptography` + `System.Text.Json` only), **no PDF library** (F19), **no new job queue** (BR9: everything on `JobQueue.Default`), **no second price catalog** (D4), **no session-level advisory lock** (BR2), **no credit text or quote in P UI** (10 D14 stays).

Flag scope (BR17): `billing_enabled` gates **order creation only** — `POST /billing/checkout` and the buy UI. Always on: `POST /billing/webhooks/{provider}` (**never** behind the flag — BR10/BR17), `GET /billing/packages` (12.7.1-T1, not mounted here), `GET /billing/payments*`, `POST /billing/payments/{id}/refund-request`, and the `PaymentStatusSyncJob` / `PaymentAnomalyJob` / `CreditReconciliationJob` registrations. 12.3.4-T1's refund/chargeback path is **never** flagged.

## 2. Confirmed facts

| Id | Fact used here | Evidence |
|---|---|---|
| C1 | `payments` (00 §6:300): `user_id, provider, provider_payment_id (unique), method (card/wallet/bank_qr), amount, currency, status (pending/paid/failed/needs_review/pending_approval/refunded/partially_refunded), package_id, credits, bonus_credits, price_version, promo_code_id, order_code (unique), resolve_requested_by, invoice_info jsonb, raw jsonb`; `payment_events` (:301): `provider, provider_event_id (unique), payment_id, type, signature_valid, payload jsonb, processed_at, error` + **CR-18** makes `payment_id` nullable (BR34 — an unmatched signature-valid event is still recorded, same precedent as `promo_redemptions.payment_id`); `refund_requests` (:302); `credit_lots` (:296): `user_id, source (trial/bonus/promo/purchase/adjust), amount, remaining, expires_at, payment_id, voided_at`; `credit_accounts` (:319): `user_id (PK), updated_at` + **CR-14** adds `review_required_at`/`review_note` (BR26; §3 G-1 is ruled) | `plans/00-overview.md` §6 ✅ |
| C2 | Money conventions: `numeric(12,6)` USD, credits `bigint`, `id uuid` UUIDv7, `timestamptz`, optimistic concurrency via `xmin` ETag (`"pm-<xmin>"` payments); S-only tables are **not** created in P | 00 §6 (L262) ✅; README C2, D17 |
| C3 | 12 CR-1 appends `payments.expires_at timestamptz, paid_at timestamptz`; **CR-2b** appends `credit_lots.voided_reason` + `CHECK (remaining >= 0 AND remaining <= amount)`; CR-3 adds flag `billing_enabled` (default **false**); CR-7 adds codes `payment_expired` 409, `payment_invalid_state` 409, `grant_in_progress` 409, `payment_already_resolved` 409; **CR-18** makes `payment_events.payment_id` nullable (BR34, ruled — an unmatched signature-valid event is still recorded) | README §4 ✅ |
| C4 | Frozen routes (00 §8 L365): `GET /billing/packages`, `POST /billing/checkout`, `GET /billing/payments`, `GET /billing/payments/{id}`, `GET /billing/payments/{id}/receipt`, `POST /billing/payments/{id}/refund-request`, `POST /billing/webhooks/{provider}`; admin row (L367): `GET /admin/payments?status=needs_review`, `POST /admin/payments/{id}/resolve` | README C4 ✅ |
| C5 | `ICreditLedger` (02 CR-2 shape) gains `GrantAsync`, `ExpireAsync`, `VoidPaymentLotsAsync`, `GetOpenReservationAsync`; `MeteringOnlyCreditLedger` → `CreditService` at M2 (CR-5, BR3) | README D2/BR3 ✅ |
| C6 | Ledger idempotency keys: `purchase:{paymentId}`, `bonus:{paymentId}`, `void:{paymentId}:{lotId}`; duplicate insert is a no-op | README D2 ✅ |
| C7 | Locking: `SELECT … FROM credit_accounts WHERE user_id = @u FOR UPDATE` inside the command transaction; advisory locks transaction-scoped only; lock order `credit_accounts` → `promo_codes` → `payments` → `credit_lots` (BR2) | README D2/BR2 ✅ |
| C8 | Tenant filtering: `payments`/`payment_events`/`credit_ledger`/`credit_lots`/`refund_requests`/`promo_redemptions` are user data; the webhook has no ambient user, so a global query silently loads 0 rows → system paths resolve through **`IPaymentLookupQuery`, frozen with exactly three members** (`ByIdAsync(Guid)`, `ByProviderPaymentIdAsync(string)`, `ByOrderCodeAsync(string)`, BR28, never extended, declared by 12.3.3-T1) then run under `ISystemContext.For(userId)` (F10) | README F10, C10 ✅ |
| C9 | 09 surfaces reused: `[JobPolicy(Queue = JobQueue.Default, Retries = n)]`, `[DisableConcurrentExecution]`, ids-only job args, per-row commands through `ISender`, `UnitOfWorkBehavior` is the only `SaveChangesAsync` caller, exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command (arch test 09.3.6-T1), `idempotency_keys` (09 D6), `outbox_messages` (09 CR-4), `IEmailSender.QueueAsync`, `IRealtimeNotifier` (ids only, WT54), rate policies `auth`/`paid`/`hub` | README C10 ✅ |
| C10 | `system_setting_changes` exists and critical settings include payment (09.10.2-T1 is the writer this task depends on); error codes live in `ErrorCodes.cs` + the `ErrorCatalogue` table keyed by code | README C1, C11 ✅ |
| C11 | One alert pipeline only (11.6.2-T1); metric names of F21 (`billing_webhook_received_total{provider,result}`, `billing_webhook_signature_invalid_total`, `billing_payments_needs_review_total`, …); CR-8 adds the billing rules to 11.6.2-T1's acceptance list | README C13, CR-8 ✅ |
| C12 | Provider facts (P2): Svix-style delivery carries `svix-id`/`svix-timestamp`/`svix-signature`, signature = HMAC-SHA256 over `${id}.${timestamp}.${rawBody}` (base64, `v1,`, constant-time compare, 300 s tolerance), at-least-once with ~28 h of retries plus manual/bulk replay of already-processed events, dedupe must be an **atomic claim** before any side effect. P5: payOS is reported to sign **sorted `data` fields** with a shared checksum key and to have **no refund endpoint**; P4: OpenAI prepaid balance is not a cutoff. Provider stays **[OPEN]** until 12.3.1-T1 | README §1 P2/P4/P5 ✅/⚠️ |
| C13 | DTO/RTO wire shapes are frozen in §3.1 (`CheckoutSessionDto`, `PaymentDto`, `PaymentDetailDto`, `AdminPaymentDto`, resolve body); the checkout response has `checkoutUrl`, **never** `redirectUrl`; the API return URL is `<PublicOrigin>/billing/return?paymentId={id}&orderCode={orderCode}`; `Idempotency-Key` is **required** on `POST /billing/checkout` and `POST /billing/promo/redeem` | README §3.1 ✅ |
| C14 | Config (D10), section **`Payment:`**: `Payment:ActiveProvider`, `Payment:Providers` (base URLs + capability flags), `WebhookTimestampToleranceSeconds` 300, `CheckoutExpiryMinutes` 30, `StatusSyncMinutes` 10, `AnomalyWindowHours` 24, `AnomalyBaselineDays` 7, `AnomalyFactor` 3, `ReturnUrlBase` | README D10 ✅ |
| C15 | Jobs (D7): `PaymentStatusSyncJob` every 10 min, retries 3; `PaymentAnomalyJob` every 15 min for 24 h after an applied `system_setting_changes` row with `integration = 'payment'`, retries 3 | README D7 ✅ |
| C16 | UI: 10 S owns every money screen (10.8.x) and `GET /billing/quote` is deferred to S; 12 ships no Angular code | README C14, CR-9 ✅ |

## 3. Domain and data

**Placement (Clean Architecture).** Domain `NightTales.Domain.Billing`: `Payment` (aggregate root), `PaymentEvent`, `PaymentStatus`, `ProviderMethod`, `MoneyMinorUnits` (`readonly record struct`, VND integer, USD minor units, no floats), `CheckoutRejection` reasons. Application `NightTales.Application.Billing.Payments`: port `IPaymentProvider` (D8, shapes verbatim), `IPaymentLookupQuery` (BR28, frozen with exactly three members `ByIdAsync(Guid)`, `ByProviderPaymentIdAsync(string)`, `ByOrderCodeAsync(string)`, never extended, F10 system scope, declared by 12.3.3-T1), commands `CheckoutCommand`, `ProcessPaymentWebhookCommand`, `SyncPaymentStatusCommand`, `ExpirePendingPaymentCommand`, queries `CheckoutSessionQuery` (reads the `CheckoutSessionDto` back for the SPA), `PaymentReversalPolicy` (§3 G-1). Infrastructure `NightTales.Infrastructure.Payments.<Provider>`: `HttpClient` adapter, `PaymentWebhookVerifier` per provider, EF configuration under `NightTales.Infrastructure.Persistence`.

**`Payment` invariants** (`NightTales.Domain.Billing.Payment`):

```csharp
Payment.Create(Guid userId, Guid packageId, string provider, ProviderMethod method, MoneyMinorUnits amount,
               string currency, long credits, long bonusCredits, string priceVersion, string orderCode,
               Guid? promoCodeId, InvoiceInfo? invoiceInfo, DateTimeOffset expiresAt);
ErrorOr<Success> MarkPaid(string providerPaymentId, DateTimeOffset paidAt);   // pending|needs_review → paid
ErrorOr<Success> MarkFailed(string reason);                                   // pending|needs_review → failed
ErrorOr<Success> MarkExpired();                                              // pending → failed (payment_expired)
ErrorOr<Success> MarkNeedsReview(PaymentMismatch reason);                     // pending|paid → needs_review
ErrorOr<Success> ApplyRefund(MoneyMinorUnits refundedAmount);                 // paid|partially_refunded → partially_refunded|refunded
```

Transition table (mirrors `../../12-billing-credits.md` §5.2):

| From | Event | To | Guard |
|---|---|---|---|
| — | `POST /billing/checkout` | `pending` | flag `billing_enabled`, package active, credits > 0, `expires_at = now + CheckoutExpiryMinutes` |
| `pending` | verified `paid` webhook / re-fetch | `paid` | claim won **and** amount+currency equal **and** status API agrees (or no status API) |
| `pending` | mismatch / unknown status / re-fetch disagreement / provider error > 30 min | `needs_review` | never grants credits |
| `pending` | no webhook, older than `expires_at` | `failed` (`payment_expired`) | no `provider_payment_id` needed |
| `needs_review` | verified `paid` (late webhook) | `paid` | amount+currency match; re-checked amount rule (F12) |
| `paid` | refund event / approved refund (12.7.5) | `refunded` \| `partially_refunded` | every lot of the payment voided |
| `paid`, `partially_refunded` | chargeback | `refunded` \| `partially_refunded` | as above **and** account review flag set |

**G-1 [RULED] — account-review storage (BR26/CR-14, F23).** `credit_accounts` gains `review_required_at timestamptz NULL` + `review_note text NULL` (**CR-14**). **12.3.4-T1 writes them on a chargeback** (with the chargeback reason) and they are cleared by the owner-only `POST /admin/users/{id}/review/clear { note }`, **owned by 12.3.4-T1** (re-auth, mandatory `note`, `[Audited]` → `billing.review_cleared`). An unreviewed account keeps returning 403 `account_under_review` on paid commands and is listed in the minted digest. Enforcement stays behind the single seam `AccountReviewPolicy.IsUnderReviewAsync(Guid userId, CancellationToken ct)` (+ `SetAsync`/`ClearAsync`), whose EF-backed implementation reads those two columns; every paid command calls the same predicate, and no second name and no second table is introduced. Config section for this folder is **`Payment:`** (D10).

**Transactions, locks, idempotency.**
- Checkout: one transaction = insert `payments` (status `pending`) → optional promo reservation through `IPromoValidator` (declared here, implemented by 12.7.2-T1, BR6) → `idempotency_keys` row (`request_hash` over body + user) → provider call is **outside** the DB transaction is **not** allowed: the provider call happens after commit, and a provider failure marks the row `failed` `payment_provider_error` (502) in a second transaction (the row never leaks a live checkout URL).
- Webhook: one transaction = claim `payment_events` → status re-fetch (out of band, before the transaction is opened; result carried into it) → `payments` transition (`FOR UPDATE` on the row via the aggregate) → ledger grants `purchase:{paymentId}` / `bonus:{paymentId}` → `payment_paid.v1` outbox → realtime. Order of locks obeys BR2 (`credit_accounts` is taken by the ledger inside this same transaction, before `payments`… — the ledger's grant is therefore issued **before** the payment row is locked: `ProcessPaymentWebhookCommand` resolves the payment, then calls `ICreditLedger.GrantAsync` (which locks `credit_accounts`), then updates `payments` — the pinned lock order holds because `credit_accounts` < `payments`).
- Idempotency keys used here (verbatim): `idempotency_keys` row for `POST /billing/checkout`; `payment_events (provider, provider_event_id)` unique for delivery dedupe; `purchase:{paymentId}`, `bonus:{paymentId}`, `void:{paymentId}:{lotId}` on `credit_ledger`.
- Indexes (F20, D17, BR20) — each created by the migration named, never by another task: `_12_Payments` (12.3.2-T1) carries `payments (status, created_at)`, `payments (provider, provider_payment_id)` unique, `payments (order_code)` unique; `_12_PaymentEvents` (12.3.3-T1) carries `payment_events (provider, provider_event_id)` unique, `payment_events (payment_id)`; `credit_lots (payment_id)` is `_12_CreditLots` (12.2.1-T1). Money columns are `numeric(12,6)` for **USD-denominated** values only; `payments.amount` is **`bigint`** (12 CR-21 — VND has no subunits, and `numeric(12,6)` overflows at 999,999.999999, well under a 1,000,000 ₫ purchase) and `ProviderPayment.AmountMinorUnits` is normalised per currency in the adapter (F12).

Key SQL:

```sql
-- claim (BR1/BR34, ruled): @paymentId resolved by IPaymentLookupQuery (BR28) before this INSERT — a read, not the check-then-act guard BR1 forbids; NULL when unmatched (CR-18 makes payment_events.payment_id nullable in 00 §6:301). No claim ⇒ 200 no-op, payments untouched
INSERT INTO payment_events (id, provider, provider_event_id, payment_id, type, signature_valid, payload, created_at)
VALUES (@id, @provider, @eventId, @paymentId, @type, true, @payload::jsonb, now())
ON CONFLICT (provider, provider_event_id) DO NOTHING
RETURNING id;

-- checkout replay: same Idempotency-Key + same request_hash ⇒ same paymentId
SELECT id, status, expires_at FROM payments WHERE user_id = @u AND idempotency_key = @key;

-- pending expiry (12.3.3-T2)
UPDATE payments SET status = 'failed', updated_at = now()
WHERE status = 'pending' AND provider_payment_id IS NULL AND expires_at < now()
RETURNING id, user_id, order_code;
```

## 4. Use cases

- **UC-1 Buy a package (card).** Given `billing_enabled` and an active package, When the user posts `POST /billing/checkout` with `Idempotency-Key`, Then a `pending` payment exists with `amount`, `credits`, `bonus_credits`, `price_version`, `order_code`, `expires_at = now + 30 min`, and the response is `CheckoutSessionDto` with `checkoutUrl` and `bankQr = null`.
- **UC-2 Replay checkout.** Given the same `Idempotency-Key` and body, Then the stored response is replayed (same `paymentId`); a different body with the same key ⇒ 409 `payment_invalid_state`; a second key ⇒ a second `payments` row.
- **UC-3 Buy with bank QR.** Given `method = bank_qr` and a provider whose `Capabilities.BankQr` is true, Then `bankQr` carries `qrPayload, bankName, accountNumber, accountName, amount, orderCode` and the return URL is still `<PublicOrigin>/billing/return?paymentId={id}&orderCode={orderCode}` (12.7.4-T1 owns the QR order route). **Given** `method = bank_qr` and `Capabilities.BankQr` false, **then** 400 `payment_method_unavailable` — **fail-closed**, never a silent card fallback (BR32/CR-7).
- **UC-4 Paid webhook.** Given a delivery with a valid signature whose provider status re-fetch says `Paid` and whose amount/currency equal the row, Then `payment_events` has the raw body, `payments.status = paid` with `paid_at`, one `purchase` lot (+ one `bonus` lot when `bonus_credits > 0` or a `bonus_percent` promo applied), `payment_paid.v1` is queued, `CreditBalanceChanged { userId }` is emitted, HTTP 200 `{ received: true }`.
- **UC-5 Duplicate/concurrent delivery.** Given two identical deliveries processed in parallel, Then exactly one grants (claim + ledger keys); the loser returns 200 and touches nothing; a manual/bulk replay 2 h later is the same no-op.
- **UC-6 Forged webhook.** Given a bad signature, Then 401 `webhook_signature_invalid`, a `payment_events` row with `signature_valid = false`, an alert and **no** 5xx (so the provider stops retrying).
- **UC-7 Amount mismatch.** Given `ProviderPayment.AmountMinorUnits ≠ payments.amount` or a currency difference, Then no lots exist, `payments.status = needs_review`, `billing_payments_needs_review_total` increments and owners are alerted; amounts > 10× the package price go straight to `needs_review` + owner alert (F12).
- **UC-8 Status re-fetch.** Given a valid `paid` webhook and a configured status API, Then the re-fetch happens server-side against the configured base URL (never a URL from the body) before any lot; disagreement ⇒ `needs_review`; API error ⇒ retry with backoff, after 30 min ⇒ `needs_review`.
- **UC-9 Never paid.** Given a `pending` row past `expires_at`, Then `PaymentStatusSyncJob` sets `failed` `payment_expired`, e-mails the user, and a late webhook for that `order_code` becomes `needs_review`.
- **UC-10 Refund.** Given a provider `refunded` event (or 12.7.5's approved refund), Then every lot of that `payment_id` is voided (`voided_at`, negative `adjust` row with `void:{paymentId}:{lotId}`), the payment is `refunded`/`partially_refunded`, and the account is **not** flagged.
- **UC-11 Chargeback.** As UC-10 **and** the account review flag is set, `audit_logs.category = 'billing'` is written, and paid commands return 403 `account_under_review` until the **owner** clears it with `POST /admin/users/{id}/review/clear { note }` (12.3.4-T1, re-auth, `[Audited]` `billing.review_cleared` — BR26).
- **UC-12 Flag off.** Given `billing_enabled = false`, Then `POST /billing/checkout` is refused while the webhook, payments reads and refund handling still work (BR17), and a webhook for an unknown `order_code` is logged + alerted + 200 (never a new `payments` row).
- **UC-13 Anomaly.** Given an applied `system_setting_changes` row with `integration = 'payment'`, Then for 24 h `PaymentAnomalyJob` compares paid count/amount per hour with the same hours of the previous 7 days and alerts when either exceeds `AnomalyFactor` (3×) or one user has > 3 purchases in 1 h or an admin/owner bought a package.

## 5. API and events

| Route | Verb | Auth / policy | Flag | Success | Errors |
|---|---|---|---|---|---|
| `/billing/packages` | GET | user, rate `auth` | — (always on) | `{ items: CreditPackageDto[] }` | — (owned by **12.7.1-T1**; **not mounted by this folder** — BR32) |
| `/billing/checkout` | POST | user, rate `paid`, `Idempotency-Key` required | `billing_enabled` | 200 `CheckoutSessionDto` | 400 `payment_method_unavailable`, 400 `promo_*`, 404 `package_not_found`, 409 `payment_invalid_state`, 409 `payment_expired`, 502 `payment_provider_error` |
| `/billing/webhooks/{provider}` | POST | `[AllowAnonymousRequest]` **exactly once**, rate `auth` | **never** (BR10/BR17) | 200 `{ received: true }` | 401 `webhook_signature_invalid`, 404 unknown `provider`, 5xx only on transport/DB failure |
| `/billing/payments` | GET | user | — | `{ items: PaymentDto[], nextCursor }` | — |
| `/billing/payments/{id}` | GET | user (tenant filtered, ETag `"pm-<xmin>"`) | — | `PaymentDetailDto` | 404 |
| `/billing/payments/{id}/receipt` | GET | user | — | `text/html; charset=utf-8`, `Content-Disposition: inline` (12.7.3-T1) | 404 |
| `/billing/payments/{id}/refund-request` | POST | user (+ admin approval later) | — | `RefundRequestDto` (12.7.5-T1) | 409 `refund_not_eligible`, 403 `account_under_review` |

ProblemDetails codes used by this folder: `payment_provider_error` 502, `payment_invalid_state` 409, `payment_expired` 409, `payment_method_unavailable` 400 (bank QR unsupported by the active provider, **fail-closed** — BR32), `webhook_signature_invalid` 401, `package_not_found` 404, `promo_invalid`/`promo_expired` 400, `promo_exhausted`/`promo_already_used` 409, `account_under_review` 403, `insufficient_credits` 402 (raised by the ledger inside the grant). Codes are added to `Application/Common/Errors/ErrorCodes.cs` and to `Api/Http/ErrorCatalogue.cs` (status comes from the catalogue, not `ErrorType`) — CR-7 carries the new ones (including `payment_method_unavailable` 400).

Events: `payment_paid.v1` (outbox, payload `{ paymentId, userId, packageId, credits, bonusCredits, amountMinorUnits, currency, provider, paidAt }`; consumed by 12.7.2-T1's promo handler per BR6), realtime `CreditBalanceChanged { userId }` (ids only). Nothing else is published by 12.3; `needs_review` and anomalies are alerts (11.6.2-T1) plus `billing_payments_needs_review_total`.

## 6. UI

This folder ships **no web UI**: no Angular component, template or route. 10 S owns the storefront — buy page, package cards, checkout hand-off, `/billing/return`, payment history, receipt print styles, and the `billing.*` copy (README C14, CR-9, D18). Because there is no UI deliverable here, **no `<story>-T0 — UI design` task exists in this folder**; 10.8.x owns its own design gate, and `/billing/return` is 10's page (the API contract for it is frozen in §3.1: query parameters `paymentId`, `orderCode`; the page reads `GET /billing/payments/{id}` and never trusts a status in the URL).

What this folder must guarantee for that UI: (a) `CheckoutSessionDto` is exactly `{ paymentId, provider, method, amount, currency, checkoutUrl | null, bankQr | null, expiresAt }` — no `redirectUrl`; (b) `bankQr.qrPayload` is render-ready text (10 renders it verbatim with a self-hosted MIT QR library behind `@defer`); (c) `PaymentDto.status` uses the 7 values of 00 §6:300 verbatim so 10 can map copy without a second vocabulary; (d) every money error carries a code the client can map to `billing.errors.*`; (e) the API is locale-agnostic here (only the receipt of 12.7.3-T1 formats VND/USD and dates in `ui_locale`). Per the owner rule of 2026-09-25, backend tasks note their consumer: 12.3.2-T1 → 10.8.1/10.8.3, 12.3.3-T1/-T2 → 10.8.3/10.8.4 (`needs_review` copy), 12.3.4-T1 → 10.8.5.

## 7. Jobs and integrations

| Job | Cadence | `[JobPolicy]` | Work |
|---|---|---|---|
| `PaymentStatusSyncJob` | `Payment:StatusSyncMinutes` (10) | `Queue = JobQueue.Default, Retries = 3` | (1) expire `pending` rows without a `provider_payment_id` and older than `expires_at` → `failed` `payment_expired` + e-mail `payment_expired`; (2) rows with a `provider_payment_id`: re-fetch through `IPaymentProvider.GetPaymentAsync` and apply the UC-4/UC-7 rules. Skips silently when the provider's `Capabilities.StatusApi` is false [OPEN, 12.3.1-T1] |
| `PaymentAnomalyJob` | every 15 min while within `AnomalyWindowHours` (24 h) of the last applied `system_setting_changes` row with `integration = 'payment'` | `Queue = JobQueue.Default, Retries = 3` | hourly paid count/amount vs the same hours of the previous `AnomalyBaselineDays` (7); alert on `> AnomalyFactor` (3×), `> 3` purchases by one user in 1 h, admin/owner self-purchase (BR13) |

Job mechanics (C9/BR9): ids-only arguments; `ISystemWorkQuery.PendingAsyncT` + per-row `ISystemContext.For(userId)`; **one command per row** through `ISender` so a failing row never rolls back the batch; both jobs query `payments (status, created_at)`; no session-level lock; registrations run only while the feature flag / provider is configured, and per BR17 these two registrations are **not** behind `billing_enabled` (they settle pre-flip and replayed payments).

Integrations: provider HTTP API (typed `HttpClient`, `Payment:Providers:<name>:BaseUrl`, Polly with **no** retry on business 4xx and no retry of a probe), secret material from configuration (user-secrets/env) or a Data Protection-protected store with purpose `NightTales.Payments.<Provider>.Credentials` — never in `appsettings.json` and never in logs; `IEmailSender.QueueAsync` with templates `payment_needs_review`, `payment_expired`; `IRealtimeNotifier` (ids only); alerts only through 11.6.2-T1 (C11). Config keys: C14 verbatim + `Payment:Providers:<name>:Capabilities:{Card,Wallet,BankQr,StatusApi,RefundApi}`.

Observability: logs `payment.checkout.created {paymentId, orderCode, userId, packageId}` (ids only), `payment.webhook.received {provider, eventId, paymentId, result}`, `payment.webhook.signature_invalid {provider, eventId}`, `payment.mismatch {paymentId, expectedMinor, receivedMinor, currency}`, `payment.grant {paymentId, credits, bonusCredits}`, `payment.refund.applied {paymentId, voidedLots, creditsRemoved, platformLossCredits}`, `payment.expired {paymentId, orderCode}`, `payment.anomaly {window, factor}`, `payment.reservation_missing {referenceId}` (F03 path); metrics `billing_webhook_received_total{provider,result}`, `billing_webhook_signature_invalid_total`, `billing_payments_needs_review_total`, `billing_provider_loss_total`, `billing_credits_minted_total{actor}` (F21 names verbatim). **Never** log the raw body, buyer e-mail, bank account name or signature value (F11).

## 8. Security and privacy

| Threat | Control |
|---|---|
| Forged webhook | Provider-specific signature verified over the **raw bytes exactly as received** (never a re-serialised body), constant-time comparison, timestamp/replay window `WebhookTimestampToleranceSeconds` (300), provider allow-list (404 otherwise), 64 KiB cap read before binding, `[AllowAnonymousRequest]` exactly once (BR10) |
| Replay / duplicate delivery | Atomic claim on `payment_events (provider, provider_event_id)` **before** any side effect (BR1) + ledger keys `purchase:`/`bonus:`/`void:`; no check-then-act |
| Amount/currency tampering | `ProviderPayment.AmountMinorUnits` == `payments.amount` **and** currency equal, checked before any lot; explicit per-currency minor-unit normalisation; > 10× package price ⇒ `needs_review` + owner alert (F12) |
| SSRF via webhook body | The status re-fetch uses only the configured base URL; no URL, host or path from the body is ever followed |
| Tenant leakage | `payments`/`payment_events`/`refund_requests` are `IUserOwned`; system paths go through `IPaymentLookupQuery` (BR28, three members, never extended) + `ISystemContext.For(userId)` (F10); integration test proves user B cannot read user A's payment by id (404, not 403 leak) |
| PII in logs | Raw body, e-mail, bank account name and signature are never logged; `payment_events.payload` keeps raw JSON under the normal retention/soft-delete rules |
| Secret exposure | Provider secrets outside the repo, Data Protection purpose `NightTales.Payments.<Provider>.Credentials`, redacted in logs and ProblemDetails |
| Self-dealing by an admin | Buy path unchanged for admins, but `PaymentAnomalyJob` reports admin/owner self-purchase and the minted digest lists it (BR13) |
| Abuse of the checkout/promo endpoints | Rate policy `paid`; `Idempotency-Key` required; promo limits under row locks (12.7.2-T1) |
| Money moving without a verified payment | No path creates a lot except the ledger call inside a won webhook claim (or an admin resolve of 12.3.5, which has its own approval gate); `[PaidOperation]` arch test (CR-11) in `NightTales.Architecture.Tests` |

Authorization: user routes require an authenticated `user`; this folder adds exactly **one** admin route — the owner-only `POST /admin/users/{id}/review/clear { note }` (12.3.4-T1, re-auth, `[Audited]` `billing.review_cleared`, BR26); the `needs_review` resolve routes stay 12.3.5's. `billing_enabled` is a flag read through the 09.9.1 flag store with cache invalidation, checked in the `CheckoutCommand` handler (not in middleware) so the webhook path can never be blocked by it. Audit `AuditLogs.category = 'billing'` for every state-changing admin action (this folder writes audit rows for the chargeback review flag and for `billing.review_cleared` — CR-14, BR26).

## 9. Test plan

Hand-written fakes from `NightTales.TestKit` only; **no mocking library**. Projects: `NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `NightTales.Api.IntegrationTests` (Testcontainers PostgreSQL 17 + `WebApplicationFactory`), `NightTales.Architecture.Tests`. Job tests are split into a recording `IJobScheduler` fake (scheduling) plus a direct `RunAsync` (behaviour) — Hangfire delayed jobs ignore `FakeClock` (06 YR27).

| Test | Project | Asserts |
|---|---|---|
| `Payment_Transitions_Guard_Invalid` | Domain.UnitTests | every illegal transition (`paid → pending`, `failed → paid`, double `ApplyRefund` on a fully refunded row) returns an error and leaves state untouched |
| `MoneyMinorUnits_Normalises_By_Currency` | Domain.UnitTests | VND → integer, USD → cents; `long` overflow and negative amounts rejected |
| `PaymentWebhookVerifier_VerifiesRawBody` | Application.UnitTests (TestKit vectors) | tampering with one whitespace byte fails; the valid vector passes; comparison is constant-time (fixed-time oracle test) |
| `CheckoutCommand_Replays_SameKey` | Application.UnitTests | same key+hash → same `paymentId`, one `payments` row; different hash → 409 `payment_invalid_state` |
| `CheckoutCommand_ProviderFailure_MarksFailed` | Application.UnitTests | provider error → `failed` + 502 `payment_provider_error`, no live checkout URL leaks |
| `ProcessPaymentWebhookCommand_GrantsOnce` | Api.IntegrationTests (Testcontainers) | two **parallel** HTTP posts of the same event ⇒ one `paid`, one `purchase` lot, one `bonus` lot, `billing_credits_minted_total` +1 |
| `Webhook_Mismatch_NeedsReview` | Api.IntegrationTests | wrong amount/currency ⇒ `needs_review`, zero lots, alert metric ≥ 1 |
| `Webhook_BadSignature_401_NoSideEffect` | Api.IntegrationTests | 401, `signature_valid = false` row, no `payments` change, no 5xx |
| `Webhook_DoesNotLeakAcrossTenants` | Api.IntegrationTests | a webhook for user A's payment never touches B's ledger; `GET /billing/payments/{A}` by B is 404 |
| `PaymentStatusSyncJob_Expires_And_Refetches` | Application.UnitTests + IntegrationTests | pending-without-id past `expires_at` → `failed`; with id + `Paid` → grants; `Unknown` → `needs_review` |
| `PaymentAnomalyJob_Alerts_OnFactor` | Application.UnitTests | 4× baseline or 4 purchases by one user in 1 h ⇒ one alert, `AnomalyBaselineDays` respected |
| `RefundChargeback_VoidsEveryLot` | Application.UnitTests + IntegrationTests | purchase+bonus+promo lots all `voided_at`; `void:{paymentId}:{lotId}` rows once; shortfall → `billing_provider_loss_total`; chargeback sets `credit_accounts.review_required_at`/`review_note`; paid command then 403 `account_under_review` until `POST /admin/users/{id}/review/clear` (BR26) |
| `FlagOff_CheckoutBlocked_WebhookSettles` | Api.IntegrationTests | `billing_enabled = false`: checkout refused, webhook for a pre-flip payment still grants |
| `Billing_ArchTests` | Architecture.Tests | the webhook endpoint has exactly one `[AllowAnonymousRequest]`; no 12 handler references a vendor SDK assembly; `[PaidOperation]` handlers call `ICreditLedger.ReserveAsync` |

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.3.1-T1 | S (0.5) | BA | S · M2 | — | 12.3.2-T1, 12.7.4-T1 |
| 12.3.2-T1 | M (1.0) | BE + Security | S · M2 · flag `billing_enabled` | 12.3.1-T1, 12.1.2-T1, 12.7.1-T1 | 12.3.3-T1, 12.7.2-T1 (soft), 12.7.3-T1, 12.7.4-T1 (soft), 10.8.1-T1, 10.8.1-T0 (10/08 CR-hand-off) |
| 12.3.3-T1 | M (1.0) | BE + Security | S · M2 | 12.3.2-T1, 12.2.1-T1, 12.6.1-T1 | 12.3.3-T2, 12.3.4-T1, 12.3.5-T1, 12.5.2-T1, 12.7.3-T1, 12.7.4-T1, 12.7.5-T1 |
| 12.3.3-T2 | M (1.0) | BE | S · M2 | 12.3.3-T1, 09.10.2-T1, 11.6.2-T1 (soft) | 12.5.1-T2 (soft), 08.2.2-T1 (08 W3 CR-hand-off) |
| 12.3.4-T1 | S (0.5) | BE | S · M2 | 12.3.3-T1, 12.2.1-T1, 12.6.1-T1 | 12.7.5-T1 |

Sum = **4.0 d raw** for this folder; epic 12.3 = **4.75 d** in README §9 (the extra 0.75 d is `12.3.5-T1` 0.5 + `12.3.5-T2` 0.25, owned by `../12.3.5-review-refunds/`). The S → M move of 12.3.3-T2 is already inside the rows above (CR-4). Task files: `12.3.1-T1-provider-evaluation.md`, `12.3.2-T1-checkout-and-provider-port.md`, `12.3.3-T1-webhook-processing.md`, `12.3.3-T2-status-sync-and-anomaly.md`, `12.3.4-T1-refunds-and-chargebacks.md`.

Critical path: 12.3.1-T1 (external, start in M0/M1) → 12.3.2-T1 → 12.3.3-T1 → {12.3.3-T2, 12.3.4-T1} → 12.7.5-T1.

## 11. Rollout

1. **Pieces already live in P:** `price_versions` (02.2.4-T1), `idempotency_keys` (09 D6), `IBudgetGuard` (02), `ICreditLedger` with `MeteringOnlyCreditLedger`. This folder lands only after `12.2.1-T1` switched the registration to `CreditService` and `12.6.1-T1` created lots (BR3) — a webhook that grants before lots exist must not be deployed.
2. **Provider onboarding (external lead time ⚠️):** 12.3.1-T1 starts in M0/M1; the application is filed the day a provider is chosen; the sandbox gate must not wait for onboarding (BR16). Until the choice is dated in `DECISIONS.md` (CR-10), 12.3.2-T1 is built against the contract-true fake (`FakePaymentProvider` in `NightTales.TestKit`) and `Payment:ActiveProvider` points at it in dev/staging.
3. **Migrations (BR20):** `_12_Payments` is **12.3.2-T1**'s migration (`payments.expires_at`/`paid_at` CR-1 columns + its three F20 indexes) and `_12_PaymentEvents` is **12.3.3-T1**'s (its two F20 indexes); both additive only. `credit_lots.voided_reason` + its CHECK (CR-2b) is **12.6.1-T1**'s own migration, not this folder's. No backfill (M2 tables are empty in P; existing M1-era test rows are truncated). Roll back = drop the columns/indexes; nothing else reads them.
4. **Deploy order:** webhook + reads + jobs → verify with a sandbox payment and a replayed duplicate → flip `billing_enabled` to true for the buy path only (BR17). A flip off never drops a provider delivery (retries ≈ 28 h, P2).
5. **Gate `12-payments-sandbox`** (README §7) is the exit: sandbox checkout end-to-end (card, and bank QR if supported), replay/bad-signature/mismatch paths, receipt in both locales, refund → lots voided, a fake `credit_balance_exhausted` trips and clears the circuit breaker, margin ≥ 60 % on measured costs, and a `needs_review` backlog runbook (who looks at `GET /admin/payments?status=needs_review`, within what SLA, and how a manual bank refund is recorded).
6. **Monitoring after the flip:** `billing_webhook_signature_invalid_total`, `billing_payments_needs_review_total`, `billing_credits_minted_total` and the P1 alert on an open `provider_funds_exhausted` (CR-8 additions to 11.6.2-T1); a dashboard row for pending-payment count and the oldest `pending` age.
7. **Open items carried to DoR:** the provider choice and its webhook algorithm (12.3.1-T1) — D9's signature shape stays [OPEN] until then; refund-API availability (P5: a provider without one is valid, the manual bank fallback applies); the chargeback review-flag storage is **ruled** (CR-14: `credit_accounts.review_required_at`/`review_note`, cleared by the owner-only `POST /admin/users/{id}/review/clear` — BR26), and `IPromoValidator`'s signature is **frozen** by BR32: `ValidateAsync(string code, Guid userId, Guid packageId, long amountMinorUnits, ct) → ErrorOr<PromoValidation(Guid PromoCodeId, PromoKind Kind, long BonusCredits, long BonusPercent)>`, declared in 12.3.2-T1, implemented by 12.7.2-T1.
