# 12.3.5 — Payment review, resolve approval and refund requests

> Feature plan for `plans/features/12-billing-credits/12.3.5-review-refunds/` (plan 12, all **S**, M2). Contract: `../README.md` §1 (C/P facts), §2 (F01–F22), §3 (D1–D18), §3.1 (frozen routes/DTOs), §8.1 (canonical rows), §10 (O-1…O-8, BR1–BR32), §4 (CR-1…CR-17); high level: `../../12-billing-credits.md` §5.2/§5.3/§5.6. Legend: ✅ read from source · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**Goal.** A safe, audited human path for money the automated webhook path will not settle: payments parked in `needs_review`, the four-eyes approval for large or self-interested resolves, and buyer refund requests with the FR-12-14 arithmetic.

**In scope**

- `12.3.5-T1` — `GET /admin/payments?status=needs_review` and `POST /admin/payments/{id}/resolve { action: grant_expected | grant_partial | mark_failed, note }`; a grant writes the same `purchase:{paymentId}` / `bonus:{paymentId}` ledger rows and lots the webhook writes (D9); `grant_partial` = `floor(received ÷ package price per credit)`, rounded **down**; overpayment refunded through the provider when `Capabilities.RefundApi` (key `refund:{paymentId}:overpay`, BR28), else a manual bank-refund note; every resolve writes `audit_logs`; above `Credit:ResolveApprovalThresholdCredits` (1,000 credits incl. bonus) or on the actor's own payment ⇒ `pending_approval` + `credit_grant_requests` row + 202 `{ creditGrantRequestId }` with no lot.
- `12.3.5-T2` — the approval half: `POST /admin/payments/{id}/resolve/approve|reject` by a **different** owner (`IReauthGuard.RequireRecentAsync(5 min)`), `MintingPolicy` under `pg_try_advisory_xact_lock(hashtext('nts:credit_mint:' || @actorId))` (busy ⇒ 409 `grant_in_progress`), F22 immediate + daily re-alert, 7-day overdue listing, never auto-granted.
- `12.7.5-T1` — `POST /billing/payments/{id}/refund-request { reason }` (14-day window; refundable = remaining purchased credits of that payment − bonus credits of that payment already spent), `GET /admin/refund-requests`, `POST /admin/refund-requests/{id}/approve|reject` and the manual-path `POST /admin/refund-requests/{id}/confirm { bankReference }` (CR-16); approve calls the provider refund (key `refund:{refundRequestId}`) then `VoidPaymentLotsAsync` voids **every** lot of the payment, bonus included; reject stores a reason and e-mails the buyer.

**Out of scope.** Checkout and the provider port (`12.3.2-T1`), webhook claim/verify (`12.3.3-T1`), status re-fetch/pending expiry/anomaly (`12.3.3-T2`), chargeback events and the review flag (`12.3.4-T1`), minting caps for direct grants (`12.5.1-T1`/`-T2`), packages/promos/receipt/bank-QR (`12.7.x`). This folder adds no route, table, job, option key or error code of its own — every name comes from README §3, §4 (CR-15/CR-16) or §10 (BR18/BR28).

**Consumer.** 10 S only (12 CR-9: admin payments queue + resolve dialog, approval queue, account payment detail). **No UI deliverable here** (owner rule 2026-09-25): those screens are 10.8.x behind their own `T0 — UI design` gate; this folder only fixes the wire they render (README §3.1).

**Delivery.** Phase `S · M2`, no flag on any of the three routes (BR17 — `billing_enabled` gates order creation only). Folder total **1.25 d** (S 0.5 + XS 0.25 + S 0.5); epics 12.3 (4.75 d) and 12.7 (4.0 d) unchanged.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| C1/CR | Tables fixed by 00 §6 (`payments`, `payment_events`, `refund_requests`, `credit_grant_requests`, `credit_ledger`, `credit_accounts`, `credit_lots`, `credit_packages`, `idempotency_keys`, `outbox_messages`, `audit_logs` with `category = billing`); 12 CR-1 adds `payments.expires_at`/`paid_at`, CR-2b adds `credit_lots.voided_reason` + `CHECK (remaining >= 0 AND remaining <= amount)` | README C1, CR-1, CR-2b ✅ |
| C2–C4 | Money `numeric(12,6)` USD, credits `bigint`, ids UUIDv7, `timestamptz`, `xmin` concurrency, S-only tables not created in P; the `payments.status` transitions of high level §5.2; `ICreditLedger` is 02's port and at M2 gains `GrantAsync`, `ExpireAsync`, `VoidPaymentLotsAsync`, `GetOpenReservationAsync` with DI switching `MeteringOnlyCreditLedger` → `CreditService` and no caller change | README C2, C5, CR-5, BR3 ✅; high level §5.2 ✅ |
| C5 | 09 reuse: `UnitOfWorkBehavior` is the only `SaveChangesAsync` caller; exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command; `[JobPolicy(Queue = JobQueue.Default, Retries = 0)]`; `ISystemWorkQuery.PendingAsyncT` + `ISystemContext.For(userId)`; `idempotency_keys`; `IEmailSender`, `IRealtimeNotifier`, `IReauthGuard`; rate policies `auth`/`paid`/`hub` | README C10 ✅ |
| C6 | Errors come from `ErrorCodes.cs` + `ErrorCatalogue.cs` (status from the catalogue); 12 CR-7 adds `payment_expired` 409, `payment_invalid_state` 409, `grant_in_progress` 409, `payment_already_resolved` 409 and `refund_request_not_pending` 409 (BR28 — never reuse `refund_not_eligible`); `IPaymentLookupQuery` is frozen with exactly three members — `ByIdAsync(Guid)`, `ByProviderPaymentIdAsync(string)`, `ByOrderCodeAsync(string)` — and never extended (BR28); CR-15 adds `credit_grant_requests.target_payment_id` (nullable) and `refund_requests.bank_reference`; CR-16 adds `POST /admin/refund-requests/{id}/confirm` — nothing may be invented | README C11, CR-7, CR-15, CR-16, BR28 ✅ |
| C7 | Exactly one alert pipeline: 11.6.2-T1 (08 D5); F21 owns the metric names (12 CR-8) — no second pipeline, no new metric name | README C13, F21, CR-8 ✅ |
| C8 | 13 owns the legal texts; CR-6 requires the terms to state credit expiry, the 14-day refund window and the bonus deduction. FR-12-14: refund of unused purchased credits, bonus already spent deducted, shown before sending | README C15, CR-6 ✅; high level FR-12-14 ✅ |
| C9 | Provider stays [OPEN] (12.3.1-T1); a provider without a refund API is a valid outcome (manual bank-refund fallback); no vendor SDK; deliveries are at-least-once with replay over ~28 h, so a flag flip must never drop a paid event | README P2/P3/P5, F18, O-1, BR17 ✅/⚠️ |

## 3. Domain and data

**Aggregates.** `Payment` (user-owned `IUserOwned`, entity per 09.5) with `Status`, `expires_at`, `paid_at`, `resolve_requested_by`, `credits`, `bonus_credits`, `amount`, `currency`, `package_id`, `provider_payment_id`; methods `MarkNeedsReview`, `Resolve(action, note, actorId, now, outcome) → Resolved | PendingApproval`, `ApproveResolve`, `RejectResolve`, `ReopenAfterReject`, `MarkRefunded(partial, providerRefundId)`. Guards live in the aggregate, never in the handler: resolve only from `needs_review` (else `payment_already_resolved`), approve only from `pending_approval`, `note` mandatory (D12/F22), reject ⇒ back to `needs_review` and never a grant, lots only through `ICreditLedger` (F04). `PaymentResolveAction { grant_expected, grant_partial, mark_failed }` with the shared snake_case enum converter (same convention as `CreditOperation`).

**`RefundRequest`** (`refund_requests`, user-owned): `Status ∈ pending | approved | rejected | expired | awaiting_manual_confirmation`, `credits`, `amount`, `reason`, `provider_refund_id`, `bank_reference` (CR-15, nullable), `decided_by`, `decided_at`, `decision_note`; a decided row is terminal.

**Pure Domain arithmetic (unit-tested, no EF).**
- `ResolveCreditArithmetic.Credits(action, receivedMinorUnits, packagePriceMinorUnits, packageCredits)` → `grant_expected`: `packageCredits`; `grant_partial`: `(receivedMinorUnits * packageCredits) / packagePriceMinorUnits` floored (D12: 929,999 ₫ on a 1,000,000 ₫ / 1,000-credit package ⇒ 929, never 930); bonus `(credits * packageBonusCredits) / packageCredits` floored [INFERENCE]. Currency `VND` → `price_vnd`, `USD` → `price_usd`, anything else ⇒ `payment_invalid_state` [INFERENCE].
- `RefundArithmetic.Refundable(remainingPurchasedCredits, spentBonusCredits)` → `max(0, a − b)` (FR-12-14; README §5.6 example 1,000 purchased − 70 bonus spent = 930); `RefundAmount(refundable, paymentAmountMinor, paymentCredits)` → `floor(refundable * amount / credits)` [INFERENCE].
- Approval predicate (D12/O-4): `action != mark_failed && (credits + bonusCredits > Credit:ResolveApprovalThresholdCredits || payment.UserId == actorId)`.

**Tables/columns used (frozen names).** `payments` (+ `(status, created_at)`, unique `(order_code)`, unique `(provider, provider_payment_id)`); `payment_events` read-only (`providerEvents[{ id, type, receivedAt, signatureValid }]`, index `(payment_id)`); `credit_grant_requests` (`source` ∈ `admin_grant` | `promo_code` (BR33), `target_user_id`, `target_payment_id` (nullable; set for the `promo_code` and resolve sources, CR-15/BR28), `amount`, `reason`, `requested_by`, `status`, `activates_at` null here, `requested_at`, decision fields, index `(status, requested_at)`); `credit_lots` (`source`, `amount`, `remaining`, `expires_at`, `payment_id`, `voided_at`, `voided_reason`); `credit_ledger` (`purchase:{paymentId}`, `bonus:{paymentId}`, `adjust` from voiding — signed per BR18: a void writes one `adjust` per lot with `amount = −remaining`; unique `idempotency_key`); `credit_accounts` (the money lock); `refund_requests` (`bank_reference` per CR-15, nullable; index `(status, created_at)` + partial unique `(payment_id) WHERE status = 'pending'`); `audit_logs`, `outbox_messages`, `idempotency_keys`.

**Locking / transactions (BR2, F02).** Order `credit_accounts` → `promo_codes` → `payments` → `credit_lots`; money mutations take `SELECT … FROM credit_accounts WHERE user_id = @u FOR UPDATE`; advisory locks are transaction-scoped only (`pg_try_advisory_xact_lock(hashtext('nts:credit_mint:' || @actorId))`, false ⇒ 409 `grant_in_progress`) — no `pg_advisory_lock` anywhere. One command = one transaction via `UnitOfWorkBehavior`; **no** DB transaction is held across the provider HTTP refund (outbox-driven, keyed, re-entrant via the request state).

**Options.** `Credit:ResolveApprovalThresholdCredits` 1000, `Credit:MintCapPerActorPer24h` 2000, `Credit:PurchasedExpiryMonths` 12, `Payment:CheckoutExpiryMinutes` 30, `Payment:ActiveProvider` + `Payment:Providers[...].Capabilities.RefundApi`.

## 4. Use cases

| # | Use case | Task |
|---|---|---|
| UC-1 | Admin opens the review queue (expected vs received amount/currency, order code, provider events, user, `expiresAt`) | 12.3.5-T1 |
| UC-2 | `grant_expected` ⇒ `paid` with the webhook-equivalent lots; `audit_logs` | 12.3.5-T1 |
| UC-3 | `grant_partial` (underpayment) ⇒ credits floored; surplus never granted | 12.3.5-T1 |
| UC-4 | `mark_failed` ⇒ `failed`, no lot, mandatory note, buyer e-mail; later webhook for the same `order_code` ⇒ `needs_review` (BR11) | 12.3.5-T1 |
| UC-5 | Resolve above the threshold **or** on the actor's own payment ⇒ `pending_approval` + `credit_grant_requests` + 202, no lot | 12.3.5-T1 |
| UC-6 | A different owner approves ⇒ `paid` with lots; rejects ⇒ back to `needs_review` with the reason | 12.3.5-T2 |
| UC-7 | Above the mint cap for the requesting actor, or the advisory lock busy ⇒ 409 `grant_in_progress`; a decided request ⇒ 409 `grant_request_not_pending` | 12.3.5-T2 |
| UC-8 | Undecided `pending_approval` ⇒ immediate alert, daily re-alert, 7-day overdue listing, never auto-granted (F22) | 12.3.5-T2 |
| UC-9 | Buyer requests a refund inside 14 days ⇒ row `pending` with `credits`/`amount`; else 409 `refund_not_eligible`, 403 `account_under_review` | 12.7.5-T1 |
| UC-10 | Admin approves ⇒ provider refund then every lot of the payment voided ⇒ `refunded`/`partially_refunded` + e-mail + balance realtime; a refund sets **no** review flag (only a chargeback does) | 12.7.5-T1 |

## 5. API and events

| Route | Verb | Request | Response | Codes / auth |
|---|---|---|---|---|
| `/admin/payments?status=needs_review` | GET | `?status=&cursor=&limit=` (max 100) | `{ items: AdminPaymentDto[], nextCursor }`; `AdminPaymentDto { id, createdAt, user: { id, email }, expectedAmount, receivedAmount, currency, method, orderCode, providerPaymentId, providerEvents: [{ id, type, receivedAt, signatureValid }], expiresAt, resolveRequestedBy, status }` | `Admin`; always on (BR17) |
| `/admin/payments/{id}/resolve` | POST | `{ action: grant_expected \| grant_partial \| mark_failed, note }` | 200 `AdminPaymentDto` \| 202 `{ creditGrantRequestId }` | `Admin`; 409 `payment_already_resolved` / `payment_expired` / `payment_invalid_state`; 403 `forbidden`; 502 `payment_provider_error` |
| `/admin/payments/{id}/resolve/approve` | POST | `{ note }` | 200 `AdminPaymentDto` | `Owner` + `IReauthGuard.RequireRecentAsync(5 min)`; 409 `grant_request_not_pending` / `payment_already_resolved` / `grant_in_progress` |
| `/admin/payments/{id}/resolve/reject` | POST | `{ note }` | 200 `AdminPaymentDto` (`needs_review`) | `Owner` + re-auth; 409 `grant_request_not_pending` |
| `/billing/payments/{id}/refund-request` | POST | `{ reason }` | `RefundRequestDto { id, credits, amount, status }` | user, own payment; 409 `refund_not_eligible`; 403 `account_under_review`; always on (BR17) |
| `/admin/refund-requests` | GET | `?status=&cursor=&limit=` | `{ items: (RefundRequestDto & { user, payment })[], nextCursor }` | `Admin` |
| `/admin/refund-requests/{id}/approve` | POST | `{ note }` | `RefundRequestDto & { user, payment }` | `Admin`; 409 `refund_request_not_pending` (already decided) / `refund_not_eligible` (nothing left refundable); provider refund called idempotently; `RefundApi == false` ⇒ `awaiting_manual_confirmation` (CR-15/BR28) |
| `/admin/refund-requests/{id}/confirm` | POST | `{ bankReference }` | `RefundRequestDto & { user, payment }` | `Admin`; manual path only (CR-16); 409 `refund_request_not_pending` unless `awaiting_manual_confirmation`; stores `refund_requests.bank_reference` (CR-15) then voids |
| `/admin/refund-requests/{id}/reject` | POST | `{ note }` | `RefundRequestDto & { user, payment }` | `Admin`; 409 `refund_request_not_pending` (already decided); reason stored + `refund_decided` e-mail |

**Commands/queries.** `GetAdminPaymentsQuery`, `ResolvePaymentCommand`, `ApproveResolveCommand`, `RejectResolveCommand`, `RequestRefundCommand`, `GetAdminRefundRequestsQuery`, `ApproveRefundRequestCommand`, `ConfirmRefundRequestCommand`, `RejectRefundRequestCommand` — one of `[Audited]` (money) / `[NotAudited]` each; admin routes policy `Admin`/`Owner` + rate policy `hub`, the buyer route rate policy `paid`.

**System scope (F10).** `payments`, `refund_requests` and `credit_grant_requests` are user-owned: a system read without a scope returns 0 rows silently. Every handler resolves the owning `user_id` through the frozen system lookup `IPaymentLookupQuery` — exactly three members, `ByIdAsync(Guid)`, `ByProviderPaymentIdAsync(string)`, `ByOrderCodeAsync(string)`, never extended (BR28) — declared by 12.3.3-T1 and consumed here and then works inside `ISystemContext.For(userId)`; admin list queries page under the system scope with an explicit `user_id` projection, never an ambient-user cross-tenant join.

**Events (frozen).** `payment_paid.v1 { paymentId, userId, credits, bonusCredits, amount, currency, status }`, `payment_needs_review.v1 { paymentId, userId, expectedAmount, receivedAmount }`, `credit_grant_requested.v1 { requestId, actorId, targetUserId, amount }`, `refund_completed.v1 { refundRequestId, paymentId, userId, status }`, `credit_balance_changed.v1 { userId }`; realtime `CreditBalanceChanged { userId }` ids-only (09 WT54); templates `payment_needs_review`, `grant_request_decided`, `refund_decided`; ETags `"pm-<xmin>"` (payments) and `"fr-<xmin>"` (credit grant requests), `If-Match` only on admin `PATCH` (D17).

**Observability.** `billing_payments_needs_review_total`, `billing_credit_grant_pending`, `billing_credits_minted_total{actor}` (F21 list, via 11.6.2-T1); logs ids-only (`paymentId`, `refundRequestId`, `actorId`, `action`, `status`).

**i18n (D18).** vi + en keys under `billing.refund.*`, `billing.payments.*`, `billing.errors.*`, `admin.billing.*`, English fallback; new keys `billing.refund.request.form`, `billing.refund.notEligible`, `billing.refund.decided`, `admin.billing.payments.resolve.actions.*`, `admin.billing.payments.approval.pending`, `admin.billing.refundRequests.queue.*`; error copy comes from the catalogue, never from a sentence in the handler.

## 6. UI

No UI deliverable (no `T0 — UI design` task here): the screens are 10 S work behind 10.8.x's own approved design (owner rule). This folder owes them the §5 wire (README §3.1 frozen) — resolve dialog (expected vs received, provider event trail, the 200-vs-202 outcome: 200 settled, 202 awaiting a second owner), `PaymentDetailDto.refundEligible` + `refundableCredits`, owned here by `IRefundEligibility` (BR32) and rendered by 12.7.3-T1 through the soft edge `12.7.3-T1 Depends 12.7.5-T1`, for the "you can get back N credits (≈ X ₫)" line shown before sending (FR-12-14), the pricing/terms text (D18, CR-6), and the failure rendering for the codes of §5; a11y per 10.1.3, both locales.

## 7. Jobs and integrations

- **No new job, no new queue** (BR9): all three tasks ride existing registrations on `JobQueue.Default`.
- `PaymentStatusSyncJob` (12.3.3-T2, 10 min) is what fills the queue (BR11 pending expiry, late webhook ⇒ `needs_review`); this folder only consumes it.
- `CreditsMintedDigestJob` (12.5.1-T2, daily 08:00) carries the F22 duties of 12.3.5-T2: immediate alert on entry (T1), daily re-alert while `pending_approval`, `billing_credit_grant_pending`, expiry of a request pending > 7 days (status `expired`; the payment stays `pending_approval`, listed as overdue, never auto-granted) and resolves in the minted digest — a cross-folder input to 12.5.1-T2 only, §8.1 unchanged.
- `CreditReconciliationJob` (12.2.3-T1) never touches these rows (no reservation, voided lots not expired reserves) but owns the FR-12-3 invariant re-checked by this folder's tests: `balance = SUM(credit_ledger.amount) = SUM(credit_lots.remaining)`.
- Integrations: `IPaymentProvider.RefundAsync` (12.3.2-T1 adapter) from the outbox handler with key `refund:{refundRequestId}`; `ICreditLedger.GrantAsync` / `VoidPaymentLotsAsync` as the only credit movers; `IEmailSender.QueueAsync` for the three templates; `IRealtimeNotifier` ids-only; `IReauthGuard.RequireRecentAsync(5 min)` on owner approvals.

## 8. Security and privacy

- **Authorization.** `Admin` policy on queue/resolve/refund-queue; the approval half is `Owner` **and** a 5-minute re-auth. A resolve by an admin on their own payment deliberately reaches `pending_approval` (the attempt is audited) but that actor can never approve it: `resolve_requested_by == actorId` ⇒ 403 `forbidden` (F07, D12/O-4).
- **Self-dealing.** Own-payment resolves always go to approval (UC-5); above-cap resolves need the second owner (UC-7); `PaymentAnomalyJob` reports admin/owner purchases (BR13) and the digest lists resolves (F21).
- **Tenant isolation.** Buyer routes are tenant-filtered (another user's payment ⇒ 404, no existence disclosure); admin/system work runs in `ISystemContext.For(userId)` after the pinned system lookup (F10), asserted by a cross-tenant integration test.
- **Audit.** Every resolve, approval, rejection, refund request, approve and reject writes `audit_logs` (`category = billing`, action, entity id, redacted payload: ids, amount, currency, note) via `[Audited]`.
- **Secrets/PII.** No provider secret is read here; credentials stay behind Data Protection and are never logged. Logs/events carry ids only; `payment_events.payload` (raw provider body) is never echoed into an error, a metric label or an admin DTO; `user.email` is admin-visible only; the manual bank reference is stored, not logged.
- **Abuse.** Buyer route uses rate policy `paid`, admin routes `hub`; the partial unique `(payment_id) WHERE status = 'pending'` plus the idempotent pending return stops refund spam; state guards (`payment_already_resolved`, `grant_request_not_pending`) leave no brute-force path.
- **Money safety.** `grant_partial` floors and can never exceed the package value; a refund can never exceed `payments.amount`; voiding runs under the `credit_accounts` row lock and the `CHECK (remaining >= 0 AND remaining <= amount)` backstops a shortfall (F04).

## 9. Test plan

- **`NightTales.Domain.UnitTests`** — `ResolveCreditArithmeticTests` (929,999 ⇒ 929; exact price; bonus scaling; unknown currency), `RefundArithmeticTests` (README §5.6 ⇒ 930; spent > remaining ⇒ 0; 14-day boundary; amount floors and never exceeds `payments.amount`), `PaymentResolveStateTests` (state guards, mandatory note, threshold 1,000 vs 1,001).
- **`NightTales.Application.UnitTests`** (hand-written fakes from `NightTales.TestKit`, no mocking library) — `ResolvePaymentCommandHandlerTests`, `ApproveResolveCommandHandlerTests`, `RequestRefundCommandHandlerTests`, `ApproveRefundRequestCommandHandlerTests`: replay no-ops, F07 403, `mark_failed` writes nothing, the 202 path creates one request row and no lot, `NeedsRequest`/`Rejected` mapping, manual vs provider branch, provider error leaves the lots live.
- **`NightTales.Api.IntegrationTests`** (real PostgreSQL 17, `PostgresFixture`, real migrations) — `AdminPaymentReviewEndpointTests`, `ResolvePaymentEndpointTests`, `ResolveApprovalEndpointTests`, `ResolveApprovalConcurrencyTests`, `RefundRequestEndpointTests`, `RefundConcurrencyTests`: queue shape/paging, 401/403, 200 vs 202, 409 matrix, audit row per action, cross-tenant resolve, one grant set under concurrent approvals, approval racing a late `paid` webhook, refund racing a chargeback (FR-12-3 holds), `ETag` values.
- **`NightTales.Architecture.Tests`** — one audit attribute per command; no `credit_lots`/`credit_ledger` write outside `ICreditLedger`; no job registration in this folder; `VoidPaymentLotsAsync` is the only voiding path.
- **Gate rehearsal** — 12-minting (two owners: self-approval 403, cap forces a request, resolve > 1,000 needs the second owner) and 12-payments-sandbox (sandbox refund request → provider refund → lots voided; the manual branch rehearsed with `Capabilities.RefundApi = false`).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.3.5-T1 | S (0.5) | BE | S · M2 | 12.3.3-T1, 12.2.1-T1, 12.6.1-T1, 12.5.1-T1 | 12.3.5-T2, 10.9.2-T1 (10/08 CR-hand-off) |
| 12.3.5-T2 | XS (0.25) | BE | S · M2 | 12.3.5-T1, 12.5.1-T1 | 10.9.4-T2 (10/08 CR-hand-off) |
| 12.7.5-T1 | S (0.5) | BE | S · M2 | 12.3.4-T1, 12.6.1-T1, 12.3.3-T1 | 12.7.3-T1 (soft), 10.9.2-T1 (10/08 CR-hand-off) |

Rows = README §8.1 verbatim (the same three rows appear in each task file's header table after the template's column reorder). Folder total **1.25 d**; 12.3.5-T1/-T2 sit inside epic 12.3 (4.75 d detailed) and 12.7.5-T1 inside epic 12.7 (4.0 d) — neither epic total changes here (CR-4's deltas are in 12.3.3-T2 and 12.8.3-T2).

**Sequencing.** 12.3.5-T1 first (queue + resolve + the `pending_approval` decision and request row), then 12.3.5-T2 (approval half). 12.7.5-T1 is independent of both (depends on 12.3.4-T1 for the voiding rule, 12.6.1-T1 for lot arithmetic) and may run in parallel with T2 — but **12.7.3-T1 Depends `12.7.5-T1` (soft, BR32)**, the payment-detail renderer needing `IRefundEligibility`; all three must land before the 12-payments-sandbox rehearsal.

## 11. Rollout

- **Flag.** None of these routes is behind `billing_enabled` (BR17/O-7): the webhook, payment reads, refund requests and review/approval paths stay live so a replay or a pre-flip payment is still settled. No new job queue (BR9).
- **Environment.** Staging first, after 12.2.1-T1 + 12.6.1-T1 (`ICreditLedger` + lots) and 12.3.3-T1 (which produces the queue) are merged. Production enablement rides the 12-payments-sandbox gate; a real refund needs the provider's production refund capability or the manual bank fallback (D12 ⚠️ [OPEN] until the provider is chosen).
- **Ops runbook.** Daily: work `/admin/payments?status=needs_review` (`grant_partial` underpayments, `mark_failed` unmatched transfers, escalate > 1,000 credits). `pending_approval` older than 7 days: decide or bank-refund manually (F22). Refund requests: check `refundableCredits`, approve, then `POST /admin/refund-requests/{id}/confirm { bankReference }` (CR-16) when the provider has no refund API. A 409 `payment_already_resolved` is a support case — read `payment_events` and the digest first.
- **DoR conditions (for the TL).** Confirm (a) the two [INFERENCE] formulas of §3 (bonus scaling on `grant_partial`, `RefundAmount` rounding), (d) that 12.5.1-T2 accepts the `CreditsMintedDigestJob` additions of §7 — no canonical dependency change. Ruled by the README: (b) `IPaymentLookupQuery` is frozen with exactly three members — `ByIdAsync(Guid)` (the admin path used here), `ByProviderPaymentIdAsync(string)`, `ByOrderCodeAsync(string)` — never extended (BR28); (c) the decided/not-pending case is 409 `refund_request_not_pending` (CR-7/BR28), the overpayment key is `refund:{paymentId}:overpay` (BR28), and the manual confirmation is `POST /admin/refund-requests/{id}/confirm { bankReference }` (CR-16) storing `refund_requests.bank_reference` (CR-15) — ⚠️[OPEN] only until a provider without a refund API is chosen.
- **Post-launch (2 weeks).** No `pending_approval` row older than 7 days; every resolve has a note and an audit row; FR-12-3 holds after every approved refund; no `billing_payments_needs_review_total` spike without a matching provider incident.
