# 12.5 — Admin minting: grants, owner approvals and promo exposure (S)

> Feature plan of `plans/features/12-billing-credits/README.md` (§1 C1–C16 / P1–P5, §2 F01–F22, §3 D1–D18 + §3.1, §4 CR-2/CR-3/CR-7/CR-13/CR-15, §7 gates, §8.1 canonical rows, §10 O-2/O-4/O-7 + BR2/BR3/BR9/BR13/BR15/BR17/BR18/BR20/BR27/BR32/BR33). High-level: `../../12-billing-credits.md` §3 (FR-12-8, FR-12-10, FR-12-12), §5.4, §5.8, §12.5.1, §12.7.2.
> Phase: **S · M2**, no P slice (D1). Areas **BE + Security** (`12.5.1-T1`, `12.7.2-T2`) and **BE** (`12.5.1-T2`). No UI in this folder — the admin screens are 10.9.x (S) and the user money UI is 10.8.x (S); this folder only freezes the wire it consumes (§3.1, 12 CR-9).
> Legend: ✅ read from source/docs (file + line) · ⚠️ unverified · [DECISION] taken from the README contract · [OPEN] still open · [INFERENCE] derived in this plan.

## 1. Scope

**What this folder is.** The "one compromised admin cannot print money" contract (FR-12-8). Credits enter the system without a payment in exactly four ways — `trial`, `promo`, `purchase`-bonus and **admin grant/adjust** — and this folder owns the last one plus the *exposure* the promo path creates. It implements the minting guard (`MintingPolicy`), the admin grant/adjust command and route, the `credit_grant_requests` queue with owner approvals, the 7-day expiry of undecided requests, the daily owner digest (`CreditsMintedDigestJob`) and the owner-only promo create/patch with `value × max_redemptions` counted against the same cap.

**In scope (3 tasks — ids fixed by §8.1, no new ids, no renames).**

| Task | Deliverable |
|---|---|
| `12.5.1-T1` (M) | `MintingPolicy.EvaluateAsync` (actor ≠ target, target role, rolling 24 h cap = direct grants + approved resolves + promo exposure, `pg_try_advisory_xact_lock(hashtext('nts:credit_mint:' \|\| @actorId))`), `AdminAdjustCreditsCommand` + `POST /admin/users/{id}/credits`, the `credit_grant_requests` row per grant, `CreditLotSource.Adjust` lots (12-month expiry), `audit_logs`, metrics; the `_12_CreditGrantRequests` migration + the 12 CR-13 cap index (BR20/CR-13) |
| `12.5.1-T2` (S) | `GET /admin/credit-grant-requests` (+`/{id}/approve\|reject`), `DecideCreditGrantRequestCommand`, 7-day expiry inside the digest job, `CreditsMintedDigestJob` (daily 08:00) with the owner digest + `billing_credit_grant_pending` gauge + `grant_request_decided` e-mail; `CreditGrantRequestApplyJob` (every 15 min, BR27) |
| `12.7.2-T2` (S) | Owner-only `POST /admin/promo-codes` / `PATCH /admin/promo-codes/{id}` authorization (admins may only set `active = false`), exposure counted into the mint cap, above-cap ⇒ code saved `active = false` + `credit_grant_requests` row (`source = promo_code`, `activates_at`), second-owner approval or 24 h time-lock with an e-mailed cancel link |

**Out of scope.** Reserve/settle/release/refund and the account row lock (12.2.1-T1); lot consumption order and `VoidPaymentLotsAsync` (12.6.1-T1); balance/ledger reads (12.2.2-T1); the resolve actions themselves and `pending_approval` (12.3.5-T1/-T2, which **consumes** `MintingPolicy`); promo validation, redemption and the checkout/bonus application (12.7.2-T1); packages (12.7.1-T1); margin/reporting (12.5.2-T1, in `12.4-limits-trial/`); per-user credit cap (12.4.1-T1); funding/runway (12.8.x); legal texts (13.1.2-T1); all UI (10.8.x/10.9.x).

**Consumers (no cycle).** 12.3.5-T2 (approve/reject of a resolve above `Credit:ResolveApprovalThresholdCredits`) and 12.7.2-T2 both call `MintingPolicy.EvaluateAsync` **after** `12.5.1-T1` lands; `12.5.1-T2` consumes the `credit_grant_requests` rows `12.5.1-T1` writes; `_12_CreditGrantRequests` is **12.5.1-T1's migration alone** (BR20 — 12.3.5-T1/-T2 consume the table, never create it); `IMintingPolicy` call sites are exactly 12.5.1-T1, 12.3.5-T1/-T2 and 12.7.2-T2 — **12.7.2-T1 never calls it** (BR32); 09.10.5-T1's admin user detail already quotes `403 grant_target_forbidden (12.5.1)` (`features/09-backend-api/README.md:1049` ✅).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| C1 | Tables/columns are frozen by 00 §6: `credit_grant_requests` (`target_user_id, amount, reason, source (@admin_grant/@promo_code), promo_code_id, requested_by, status (pending/approved/rejected/expired), decided_by, decided_at, decision_note, activates_at, target_payment_id`), `credit_ledger` (`user_id, lot_id, kind (grant/bonus/purchase/reserve/settle/release/refund/adjust/expire), amount (signed bigint), price_version, reference_type, reference_id, idempotency_key (unique), created_by`), `credit_lots` (…`source`…), `promo_codes` (`code (unique citext), kind (bonus_percent/fixed_credits), value, valid_from, valid_to, max_redemptions, per_user_limit, min_package_id, active, created_by`), `credit_accounts` (`user_id` PK, `updated_at`), `audit_logs` (`category` includes `billing`, `visibility`, `actor_type`, `subject_user_id`, `data`) | `plans/00-overview.md` §6 ✅ (L295–L319, L314) |
| C2 | `credit_accounts` is a pure lock row (PK `user_id`, `updated_at`): money mutations take it `FOR UPDATE` **inside** the command transaction; balance is `SUM(credit_ledger.amount)` and is never cached (D2/D6) | 00 §6:319 ✅; README D2 |
| C3 | `credit_ledger.idempotency_key` is unique and its closed list contains `grant:adjust:{grantRequestId}`, `grant:trial:{userId}`, `grant:promo:{promoRedemptionId}`, `expire:{lotId}:{reservationId?}`, `void:{paymentId}:{lotId}`; a duplicate insert is a no-op, never a second movement | README D2 ✅ |
| C4 | Minting numbers [DECISION]: cap **2,000** credits per actor per rolling 24 h (direct grants + approved `grant_expected`/`grant_partial` resolves + promo exposure), resolve approval threshold **1,000**, single-owner promo activation via a **24 h** time-lock with an e-mailed cancel link, negative adjustments uncapped but audited and digested; options `Credit:MintCapPerActorPer24h` 2000 / `Credit:ResolveApprovalThresholdCredits` 1000 / `Credit:PromoExposureApprovalHours` 24 | README O-4, D10 ✅; plan 12 `12-billing-credits.md`:124, :126, :159 ✅ |
| C5 | Admin grant 200 path: `adjust`/`grant` row with `created_by` + `credit_lots` row `source = adjust` (expiry 12 months) + `audit_logs`; own account ⇒ 403 `self_grant_forbidden`; target `admin`/`owner` ⇒ 403 `grant_target_forbidden` (owners may grant to admins, never to themselves); above cap ⇒ 202 with the `credit_grant_requests` id (**a success — no `grant_requires_approval` code exists**, 09 README:975 ✅) | README D12 ✅; plan 12:124–126 ✅ |
| C6 | Grant-request decisions: owner, a **different person** than the requester where two owners exist, decision note, 7-day expiry, `audit_logs`, 409 `grant_request_not_pending` on a decided row; the decision applies the grant in one transaction | README D12 ✅; plan 12:127 ✅ |
| C7 | Digest (daily 08:00 server-local, retries 1): credits granted per admin (direct, resolves, promo redemptions), pending **and** expired requests, top 10 recipients of the last 24 h; the job also expires `credit_grant_requests` older than 7 days. All 12 jobs use `JobQueue.Default`, ids-only arguments, per-row commands through `ISender`, `[DisableConcurrentExecution]` for sweeps | README D7 ✅; plan 12:129, :204 ✅ |
| C8 | Promo exposure: a `fixed_credits` code's exposure = `value × max_redemptions` counts toward that owner's 24 h cap; above the cap the code is saved `active = false` with a `credit_grant_requests` row that a second owner approves (one owner only ⇒ activation after the 24 h time-lock with an e-mailed cancel link); a PATCH raising `value` or `max_redemptions` counts **only the increase**; `bonus_percent` codes need a paid purchase and are **not** counted. Admins may only set `active = false` (403 `forbidden` otherwise) | README D13/D14 ✅; plan 12:159 ✅; README §3.1 promo row ✅ |
| C9 | Locking: advisory locks are transaction-scoped only — `pg_try_advisory_xact_lock(hashtext('nts:credit_mint:' || @actorId))`, false ⇒ 409 `grant_in_progress`; global lock order `credit_accounts` → `promo_codes` → `payments` → `credit_lots`; **no** session-scoped `pg_advisory_lock` anywhere in 12 (F02/B) | README D2, BR2 ✅ |
| C10 | 09 surfaces reused: `UnitOfWorkBehavior` is the only `SaveChangesAsync` caller; exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command (arch test 09.3.6-T1); `IEmailSender.QueueAsync` + outbox `EmailRequested` with templates minted at send time (09.2.6-T3); `AdminPolicy`/`OwnerPolicy` (09.9.1-T1) and `IReauthGuard` (09.10.4-T1); `idempotency_keys` (09 D6); `xmin` ETag `"pc-<xmin>"` for promo codes; `ISystemContext.For(userId)` for a system-context money write (F10); rate policies `auth`/`paid`/`hub` | README C10/D17 ✅; 09 README:1049, :1019 ✅ |
| C11 | Money tables are `IUserOwned`/tenant-filtered (`credit_ledger`, `credit_lots`, `promo_redemptions`, `payments`); a global query **without** a scope loads 0 rows silently — so every admin money write runs under `ISystemContext.For(targetUserId)` (the F10 pattern) | README F10, D2 ✅ |
| C12 | Error codes live in `Application/Common/Errors/ErrorCodes.cs` + `Api/Http/ErrorCatalogue.cs` (status from the catalogue); 12 CR-7 adds `grant_in_progress` 409; already present: `self_grant_forbidden`/`grant_target_forbidden`/`forbidden` 403, `grant_request_not_pending` 409, `insufficient_credits` 402, `concurrency_conflict` 409 | README C11, CR-7 ✅; 09 README §5.4 ✅ |
| C13 | Observability: metrics `billing_credits_minted_total{actor}`, `billing_credit_grant_pending`; alerts ride the **one** pipeline of 11.6.2-T1 (`billing_credits_minted_total` in CR-8; the above-cap grant raises a security alert to owners by e-mail, 08 §5.2); e-mail templates `credits_minted_digest`, `grant_request_decided` | README F21, D17, CR-8 ✅ |
| C14 | Everything in this folder is S · M2 and **not** behind `billing_enabled`: the flag gates order creation only (checkout, promo/redeem, buy UI); grants, resolve and funding stay on — the closed beta runs on admin-granted credits (O-7) | README O-7, BR17 ✅; plan 12 §12.5 ✅ |
| C15 | Gate `12-minting` must be demonstrated with two actors: self-grant 403, admin target 403, the 24 h cap forces a `credit_grant_requests` row under **parallel** grants, a resolve > 1,000 credits needs a second owner, promo exposure counted | README §7 ✅ |
| C16 | Rulings applied in this folder: `_12_CreditGrantRequests` is created by **12.5.1-T1 only** (BR20; 12.3.5-T1/-T2 consume the table); one `credit_grant_requests` row per grant with idempotency `grant:adjust:{grantRequestId}`, `promo_codes.created_by` and `credit_grant_requests.target_payment_id` (BR27/CR-15); `credit_grant_requests.source` is `admin_grant`|`promo_code` only (BR33); the index `credit_ledger (created_by, created_at) WHERE created_by IS NOT NULL` is owned by 12.5.1-T1 (CR-13); the 24 h cap sums the minted positives of `credit_ledger` only, never reserved credits (BR18); `IMintingPolicy` call sites are 12.5.1-T1, 12.3.5-T1/-T2 and 12.7.2-T2 only (BR32) | README §10 BR18/BR20/BR27/BR32/BR33, §4 CR-13/CR-15 ✅ |

**Ruled by the README (2026-09-25) — the four gaps this folder reported are closed.** (1) BR27 — every admin grant writes one `credit_grant_requests` row and the ledger key is `grant:adjust:{grantRequestId}`. (2) 12 CR-15 adds `promo_codes.created_by`, so the cap keys on the code's creator; the fallback attribution in §3.4 is withdrawn. (3) 12 CR-13 adds `credit_ledger (created_by, created_at) WHERE created_by IS NOT NULL`, owned by 12.5.1-T1. (4) BR27/D7 add `CreditGrantRequestApplyJob` (every 15 min, owned by 12.5.1-T2) and BR27 fixes the cancel-link kind `credit_grant_request`. Also ruled here: BR20 (`_12_CreditGrantRequests` → 12.5.1-T1 only), BR18 (the cap sums minted positives, never reserved), BR33 (`source` ∈ {`admin_grant`, `promo_code`}) and BR32 (the `IMintingPolicy` call-site list).

## 3. Domain and data

### 3.1 `MintingPolicy` (Application service, no HTTP, reads through ports)

```
public enum MintingSource { AdminGrant, Resolve, PromoExposure }        // [INFERENCE]: type fixed by D13, members pinned here; persisted mapping (BR33, 00 §6:314): AdminGrant/Resolve ⇒ `admin_grant`, PromoExposure ⇒ `promo_code` — never `payment_resolve`
public sealed record MintingEvaluation(long MintedLast24h, long Cap, long Amount, MintingDecision Decision, ErrorCode? Code);

public abstract class MintingPolicy                                          // NightTales.Application/Billing
{
    public abstract Task<ErrorOr<MintingEvaluation>> EvaluateAsync(
        Guid actorId, Guid targetUserId, long amount, MintingSource source, CancellationToken ct);
}
```

Rule order (first hit wins — an evaluation is **never** a partial success):

| # | Check | Result |
|---|---|---|
| 1 | `amount == 0` | `ErrorCodes.ValidationFailed` |
| 2 | `source == AdminGrant && actorId == targetUserId` | `Rejected(self_grant_forbidden)` → 403 |
| 3 | `source == AdminGrant && target.role != user` (owners may grant to admins, never to themselves; rule 2 already caught self) | `Rejected(grant_target_forbidden)` → 403 |
| 4 | `amount < 0 && balance + amount < 0` (negative adjustments are otherwise uncapped) | `Rejected(insufficient_credits)` → 402 |
| 5 | `mintedLast24h + amount > cap` and `source != ResolveBeyondThreshold`-exempt | `NeedsRequest` → 202 + `credit_grant_requests` row |
| 6 | otherwise | `Allowed` → the grant applies in the caller's transaction |

`ResolveApprovalThresholdCredits` (1,000) is applied by `ResolvePaymentCommand` (12.3.5-T1/-T2) as its own gate before calling this policy; `12.5.1-T1` only supplies the cap arithmetic and exposes `MintedLast24hAsync`.

### 3.2 Cap arithmetic (one query, no N+1)

```sql
-- inside the grant transaction, after the advisory try-lock; @actorId = the minting actor
SELECT COALESCE(SUM(amount), 0)
FROM   credit_ledger
WHERE  created_by = @actorId
  AND  amount > 0
  AND  kind IN ('grant', 'bonus', 'purchase', 'adjust')   -- BR18: minted positives only
  AND  created_at >= now() - interval '24 hours';
```

Resolve grants count through the same `credit_ledger.created_by` (12.3.5-T2 writes `kind = 'adjust'` with its own actor), so no second source is needed. Only the minted/settled positive kinds of BR18 are summed — `reserve`/`release`/`settle`/`refund` rows are never counted, so reserved credits never enter the cap (BR18/BR27). Promo exposure is added from `promo_codes` (§3.4) — exposure is **not** money movement and therefore never appears in `credit_ledger`.

### 3.3 Data-access and index plan [DECISION]

| Query | Shape | Index |
|---|---|---|
| cap sum (§3.2) | range on `(created_by, created_at)`, partial on positive rows | `credit_ledger (created_by, created_at) WHERE created_by IS NOT NULL` (**12 CR-13; owned by 12.5.1-T1**) |
| promo exposure sum (§3.4) | `promo_codes` by creator | covered by `promo_codes.created_by` (12 CR-15) |
| pending request list + digest | `credit_grant_requests (status, requested_at)` keyset | D17 already pins `credit_grant_requests (status, requested_at)` ✅ |
| expiry sweep | same index, `status = 'pending' AND requested_at < now() - 7 d` | as above |
| digest top 10 recipients | one `GROUP BY target_user_id ORDER BY SUM(amount) DESC LIMIT 10` over the last 24 h | as above |

All list reads are keyset-paginated `(created_at, id)` with `limit ≤ 100` (09 pagination contract, 09.9.1-T1).

### 3.4 Promo exposure (12.7.2-T2)

```
exposure(actor) = Σ over active, kind = 'fixed_credits', created_by = actor
                  of (value × max_redemptions)                            // counted once per code
increase(PATCH) = max(0, value_new × max_redemptions_new − value_old × max_redemptions_old)   // only the increase
```

`bonus_percent` codes are excluded (they need a paid purchase, C8). 12 CR-15 adds `promo_codes.created_by` (BR27), so the exposure query keys on the creator exactly as designed; the fallback attribution path is withdrawn and every code — auto-approved included — is visible to the cap.

### 3.5 `credit_grant_requests` state machine

```
(AdminAdjustCreditsCommand within cap)   → approved  (decided_by = requested_by, decision_note = 'within cap')
(AdminAdjustCreditsCommand above cap)    → pending   + 202 { creditGrantRequestId } + owner alert
(promo exposure above cap)               → pending, source = 'promo_code', promo_code_id, activates_at = requested_at + PromoExposureApprovalHours
pending --owner approve--> approved      (apply in one transaction, then grant)
pending --owner reject---> rejected      (decision_note mandatory)
pending --7 days---------> expired       (CreditsMintedDigestJob pass)
pending --cancel link----> expired       (promo activation only, 09.10.8-T1 mechanism)
approved|rejected|expired --any further decision--> 409 grant_request_not_pending
```

`credit_grant_requests` is created by migration `_12_CreditGrantRequests` owned by **12.5.1-T1 only** (BR20); 12.3.5-T1/-T2 consume the table and must not claim it. `source` is `admin_grant`|`promo_code` only (BR33) — a resolve-originated request persists `admin_grant` + `target_payment_id`, a promo request `promo_code` + `promo_code_id`; never a literal `payment_resolve`. Every transition writes one `audit_logs` row (`category = 'billing'`, `actor_type` `admin`/`owner`, `subject_user_id = target_user_id`, `data = { grantRequestId, amount, source, decision }`) ✅ C1.

## 4. Use cases

| # | Given / When / Then | Task | Kernel |
|---|---|---|---|
| UC-1 | within cap (1,500 minted, +500) ⇒ 200; the ledger row `kind = 'adjust'`, `created_by` = actor, lot `source = 'adjust'`, `expires_at = now + 12 months`; `audit_logs` written | 12.5.1-T1 | `AdminAdjustCreditsCommand` |
| UC-2 | 1,500 minted, +501 ⇒ **202** `{ creditGrantRequestId }`, no lot, one `pending` row, owner security alert + `billing_credit_grant_pending` +1 | 12.5.1-T1 | same |
| UC-3 | actor == target ⇒ 403 `self_grant_forbidden`; no row, no audit, no lot | 12.5.1-T1 | `MintingPolicy` rule 2 |
| UC-4 | admin targets an `admin`/`owner` account ⇒ 403 `grant_target_forbidden`; an **owner** may target an admin (never themselves) | 12.5.1-T1 | rule 3 |
| UC-5 | negative `adjust` that would take the balance below zero ⇒ 409/402 `insufficient_credits`, reported in the digest, no movement | 12.5.1-T1 | `ICreditLedger` + rule 4 |
| UC-6 | two parallel grants by one actor (1,800 + 900) ⇒ one 200 and one 202; the cap sum never exceeds 2,000 | 12.5.1-T1 | advisory xact try-lock |
| UC-7 | advisory lock busy ⇒ 409 `grant_in_progress` (retryable), no row | 12.5.1-T1 | `pg_try_advisory_xact_lock` |
| UC-8 | owner approves a pending request ⇒ grant applied once in one transaction, `status = approved`, `decided_by` set, `audit_logs`, `grant_request_decided` e-mail; a retry ⇒ 409 `grant_request_not_pending` | 12.5.1-T2 | `DecideCreditGrantRequestCommand` |
| UC-9 | requester is the only owner ⇒ 403 `forbidden`; with two owners the other one must decide | 12.5.1-T2 | different-person rule |
| UC-10 | reject without a note ⇒ 400 `validation_failed`; with a note ⇒ `rejected` + e-mail | 12.5.1-T2 | same |
| UC-11 | a pending request older than 7 days ⇒ `expired` in the digest run, listed in the digest, `billing_credit_grant_pending` −1 | 12.5.1-T2 | per-row command through `ISender` |
| UC-12 | admin creates/PATCHes a promo code ⇒ 403 `forbidden`; the same admin PATCHes `active = false` ⇒ 200 | 12.7.2-T2 | `UpsertPromoCodeCommand` |
| UC-13 | owner creates a `fixed_credits` code whose exposure crosses the cap ⇒ saved `active = false`, `pending` row `source = 'promo_code'` with `activates_at`, cancel link e-mailed; a second owner approves ⇒ `active = true` | 12.7.2-T2 | `MintingPolicy(PromoExposure)` |
| UC-14 | PATCH raising `max_redemptions` 100 → 120 on a 50-credit code ⇒ only 1,000 extra exposure is counted; a decrease counts 0 | 12.7.2-T2 | `increase()` |
| UC-15 | PATCH with a stale `If-Match: "pc-<xmin>"` ⇒ 409 `concurrency_conflict` | 12.7.2-T2 | ETag |

## 5. API and events

| Route | Verb | Body / query | Success | Failure | Policy |
|---|---|---|---|---|---|
| `/admin/users/{id}/credits` | POST | `{ amount, reason }` + `Idempotency-Key` (09 D6) | 200 (within cap) / 202 `{ creditGrantRequestId }` | 403 `self_grant_forbidden`, 403 `grant_target_forbidden`, 402 `insufficient_credits`, 409 `grant_in_progress`, 400 `validation_failed` | `AdminPolicy` (owners included) |
| `/admin/credit-grant-requests` | GET | `?status=&cursor=&limit=` | 200 `{ items: CreditGrantRequestDto[], nextCursor }` | — | `OwnerPolicy` |
| `/admin/credit-grant-requests` | POST | `{ targetUserId, amount, reason }` (an admin's above-cap grant may also be filed directly) | 201 `CreditGrantRequestDto` | 403, 400 | `OwnerPolicy` |
| `/admin/credit-grant-requests/{id}/approve` | POST | `{ note }` | 200 `CreditGrantRequestDto` | 409 `grant_request_not_pending`, 409 `grant_in_progress`, 403 `forbidden` (self-approval) | `OwnerPolicy` + `IReauthGuard.RequireRecentAsync(5 min)` [INFERENCE, 09.10.4-T1] |
| `/admin/credit-grant-requests/{id}/reject` | POST | `{ note }` (mandatory) | 200 `CreditGrantRequestDto` | 409 `grant_request_not_pending`, 403 | `OwnerPolicy` + `IReauthGuard` |
| `/admin/promo-codes` | POST | `PromoCodeInput` | 201 `PromoCodeInput` fields + `id`/`active`/`xmin`, or **200** `{ creditGrantRequestId, active: false, … }` when the exposure crosses the cap (BR27) | 403 `forbidden`, 409 `grant_in_progress`, 400 `validation_failed` | `OwnerPolicy` |
| `/admin/promo-codes/{id}` | PATCH | `PromoCodeInput` + `If-Match: "pc-<xmin>"` | 200 same resource shape, or **200** `{ creditGrantRequestId, active: false, … }` when the increase crosses the cap (BR27) | 403 `forbidden` (admin raising) / 409 `concurrency_conflict` / 409 `grant_in_progress` / 404 `package_not_found` | `OwnerPolicy`, `active = false` allowed for `AdminPolicy` |

DTO (frozen, §3.1): `CreditGrantRequestDto { id, targetUser, amount, reason, source, promoCodeId?, requestedBy, status, activatesAt | null, requestedAt }`; `PromoCodeInput { code, kind, value, validFrom, validTo, maxRedemptions, perUserLimit, minPackageId?, active }`. `source` wire values are 00 §6's `admin_grant` / `promo_code`.

**Events, e-mail, metrics.** No new outbox event is introduced: e-mail rides 09.2.6-T3's `EmailRequested` through `IEmailSender.QueueAsync` with the templates `credits_minted_digest` and `grant_request_decided` (D17 catalog ✅), the above-cap/owner alert rides the 11.6.2-T1 pipeline (+ owner e-mail, 08 §5.2), and metrics are `billing_credits_minted_total{actor}` (incremented on every applied grant/adjust and every promo activation) and `billing_credit_grant_pending` (gauge, set by each digest run and each request transition). Realtime is **not** used by this folder (`CreditBalanceChanged` is the ledger's, ids only, C10).

**Cancel link (promo activation only).** BR27 fixes the mechanism and the kind: 09.10.8-T1's `approval_cancel_tokens` per recipient, public `POST /security/cancel-links/{kind}/{id}` with `[AllowAnonymousRequest]`, rate policy `anon`, uniform 400 `cancel_token_invalid`, `expires_at = activates_at`, `kind = "credit_grant_request"`; the token is minted at send time by the per-template `IEmailTokenMinter`, never stored in the payload or `email_messages.params` (09.2.6-T3 ✅).

## 6. UI

No UI ships from this folder (all three tasks are backend). What the 10 S surfaces consume:

| Consumer | What it needs from here |
|---|---|
| 10.9.x admin billing screens (S) | the routes/DTOs of §5 verbatim; the above-cap **202** must be rendered as "sent for approval", not an error; the admin promo editor hides the owner-only fields for `AdminPolicy` |
| 10.8.x user money UI (S) | nothing directly; the credits that appear in `GET /credits/balance` may have come from an `adjust` lot |
| i18n (D18) | keys `admin.billing.grant.*`, `admin.billing.grantRequests.*`, `admin.billing.promo.*`, `billing.errors.*` (vi + en, English fallback); the digest e-mail template text is vi + en |
| a11y | the admin screens inherit 10.1.3's contract; status is never colour-only (a `pending` request is labelled) |

Owner rule (2026-09-25): the 10.9.x admin screens require their own approved `10.9.x-T0 — UI design` task **before** implementation; this folder only freezes the wire and the copy keys. [DECISION]

## 7. Jobs and integrations

| Job | Cadence | Retries | Attributes | Notes |
|---|---|---|---|---|
| `CreditsMintedDigestJob` | daily 08:00 server-local | 1 | `[JobPolicy(Queue = JobQueue.Default, Retries = 1)]`, `[DisableConcurrentExecution]`, ids-only args (none) | owner digest (grants per actor, resolves, promo redemptions, top 10 recipients of the last 24 h, pending + overdue requests) **and** the 7-day expiry of `pending` requests, each dispatched as one command per row through `ISender` (BR9, 06 YR26) |
| `CreditGrantRequestApplyJob` | every 15 min | 1 | `[JobPolicy(Queue = JobQueue.Default, Retries = 1)]`, `[DisableConcurrentExecution]`, ids-only args | **BR27/D7 — owned by 12.5.1-T2**: applies `credit_grant_requests` rows with `status = 'pending'` and `activates_at <= now` (single-owner promo time-lock), one command per row through `ISender`, `grant:adjust:{grantRequestId}` idempotency; a request cancelled through the cancel link (09.10.8-T1, kind `credit_grant_request`) is skipped |

Integrations: 09.2.6-T3 e-mail pipeline + template catalog; 11.6.2-T1 alerts (no second pipeline); 09.9.1-T1 admin policy group; 09.10.4-T1 `IReauthGuard`; 09.2.6-T1 Hangfire scheduler (`IJobScheduler.Enqueue/Schedule(j, ct)` lambdas); `ISystemWorkQuery.PendingAsyncT` is **not** usable for `credit_grant_requests` (its pinned shape is `(id, user_id)` for user-owned work) — the digest reads the admin-scoped table directly, while the money write itself runs under `ISystemContext.For(targetUserId)` (C11).

## 8. Security and privacy

**Threat: a compromised or dishonest admin mints credits.** Layered, every layer independently tested (gate `12-minting`): (1) no self-grant and no grant to an `admin`/`owner` target; (2) rolling 24 h cap 2,000 per actor summed **inside** the grant transaction, under a per-actor `pg_try_advisory_xact_lock` — a busy lock returns 409 `grant_in_progress` instead of racing; (3) above the cap nothing moves without a **different** owner; (4) promo exposure counts `value × max_redemptions` so a code cannot be a side door; (5) promo create/patch is owner-only, admins may only deactivate; (6) every movement and decision writes `audit_logs` (`category = 'billing'`, actor + subject + before/after data); (7) the daily digest and `billing_credits_minted_total{actor}` make minting visible to the owner, and a silent digest failure is itself a security failure (F21); (8) negative adjustments are allowed but audited and digested — a "reversal" sweep would be a bigger hole than the imbalance it fixes (D12).

**Policy call sites (BR32).** `IMintingPolicy` is called by exactly 12.5.1-T1, 12.3.5-T1/-T2 and 12.7.2-T2; **12.7.2-T1 never calls it** (it owns validation, the catalogue write and redemption), no other folder may add a call site, and the 24 h cap sums only the minted positives of `credit_ledger` (BR18) — never reserved credits.

**Secrets and PII.** No provider secret touches these routes; the cancel token is issued at send time by `IEmailTokenMinter` (never in an outbox payload or `email_messages.params`, 09.2.6-T3) and stored hashed by 09.3.3-T1's `IUserActionTokenService`/09.10.8-T1's `approval_cancel_tokens`. Logs carry ids and outcome codes only — **never** a target e-mail address, a cancel token or a promo code list; `admin.billing.*` copy is not logged.

**Transport/abuse.** Admin routes are authenticated (`AdminPolicy`/`OwnerPolicy`) with the `auth` rate policy and mTLS-not-needed; `POST /admin/users/{id}/credits` additionally requires `Idempotency-Key` (09 D6) because a retried POST must not mint twice. Owner decisions require a recent re-auth [INFERENCE], as 09 does for every owner money action.

**Tenant scope.** The write path runs under `ISystemContext.For(targetUserId)` (F10) so `credit_ledger`/`credit_lots`/`credit_accounts` writes are not silently dropped by the tenant filter; every read the admin UI does goes through the same scoped query with an explicit `UserId` (README D2 read rule).

## 9. Test plan

**`NightTales.Domain.UnitTests`** — `MintingPolicyRulesTests`: rule order (self-grant before target-role), 403 codes are `self_grant_forbidden`/`grant_target_forbidden`, exempt `Resolve` source, negative-adjust crossing zero. **`NightTales.Application.UnitTests`** (hand-written fakes from `NightTales.TestKit`, no mocking library) — `AdminAdjustCreditsCommandHandlerTests`: 1,500 + 500 ⇒ 200 with one `adjust` ledger row (`grant:adjust:{id}`, `created_by`, lot `adjust`/12 months) and one `approved` request row; 1,500 + 501 ⇒ 202 + `pending` row + no ledger row + alert recorded; replay of the same `Idempotency-Key` ⇒ one movement; advisory-lock-busy fake ⇒ 409 `grant_in_progress`; `MintedLast24h` window boundary (23 h 59 m counted, 24 h 01 m not). `DecideCreditGrantRequestCommandHandlerTests`: approve applies once and set `decided_by`/`decided_at`; second approve ⇒ 409 `grant_request_not_pending`; reject without a note ⇒ `validation_failed`; self-approval when the requester is an owner ⇒ 403 `forbidden`. `UpsertPromoCodeCommandHandlerTests`: admin ⇒ 403 unless `active = false`; exposure increase arithmetic (100 → 120 on 50 credits ⇒ +1,000); above cap ⇒ `active = false` + `pending` row with `activates_at`; stale ETag ⇒ `concurrency_conflict`. `CreditsMintedDigestJobTests` use 06 YR27's split (a recording `IJobScheduler` fake + a direct `RunAsync`), never Hangfire's clock.

**`NightTales.Api.IntegrationTests`** (real PostgreSQL 17, `PostgresFixture`) — 10 parallel `POST /admin/users/{id}/credits` by one actor never exceed 2,000 (assert the final cap sum and the row mix); two concurrent deliveries of the same `Idempotency-Key` mint once; parallel approve of the same request ⇒ exactly one grant; the cap sum sees a concurrent resolve grant (`credit_ledger.created_by`); `credit_accounts FOR UPDATE` ordering (`credit_accounts` → `promo_codes` → `payments` → `credit_lots`) with no deadlock under 20 parallel mixed grants; a `pending` request 8 days old becomes `expired` and is never granted; keyset pagination of the request list.

**`NightTales.Architecture.Tests`** — every new command has exactly one of `[Audited]`/`[NotAudited]` (09.3.6-T1); the money handlers are not `[AllowAnonymousRequest]`; the `[PaidOperation]` rule (12 CR-11) is unaffected; no `pg_advisory_lock` (session form) appears in 12's code (source scan of the folder's SQL strings).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 12.5.1-T1 | M (1.0) | BE + Security | S · M2 | 12.3.5-T2, 12.5.1-T2, 12.7.2-T2, 12.3.5-T1 (12 CR-31) | 12.3.5-T2, 12.5.1-T2, 12.7.2-T2 |
| 12.5.1-T2 | S (0.5) | BE | S · M2 | 12.5.1-T1, 09.2.6-T3, 11.6.2-T1 (soft), 12.3.3-T2 (soft) | 09.10.5-T1 (soft) (09 W3 CR-13), 10.9.4-T2 (10/08 CR-hand-off) |
| 12.7.2-T2 | S (0.5) | BE + Security | S · M2 | 12.7.2-T1, 12.5.1-T1, 09.3.3-T1 | 10.9.2-T1 (10/08 CR-hand-off) |

Rows equal README §8.1 verbatim. Folder total **2.0 d raw** (1.0 + 0.5 + 0.5); the README's epic split counts 1.5 of these days in **12.5 Admin + reporting** (2.5 d with `12.5.2-T1`) and 0.5 in **12.7 Packages, promos, receipts, refunds** (4.0 d), unchanged by this folder — no re-estimate (README §9). Task files: `12.5.1-T1-admin-credit-grants.md`, `12.5.1-T2-grant-requests-and-digest.md`, `12.7.2-T2-owner-only-promo-control.md`.

## 11. Rollout

**Order.** `12.5.1-T1` first (it is the reason the folder exists and blocks 12.3.5-T2 and 12.7.2-T2), then `12.5.1-T2` (requests + digest, needs 09.2.6-T3 for e-mail), then `12.7.2-T2` (needs 12.7.2-T1's `UpsertPromoCodeCommand` and 12.5.1-T1's policy). All three land before the **12-minting** gate rehearsal and before any beta user receives a granted credit.

**Gate `12-minting` (README §7).** Two actors: self-grant ⇒ 403; admin targeting an admin ⇒ 403; a parallel-grant run forces a `credit_grant_requests` row instead of exceeding 2,000; a resolve of 1,001 credits waits for a second owner; promo exposure is visible in the cap. Sign-off needs the audit rows and one digest e-mail inspected by hand (the owner's anti-fraud control is only real if it arrives).

**Flags and settings.** None of these routes is behind `billing_enabled` (O-7/BR17): the closed beta is funded by admin grants, so grants must work with the flag **off**; only checkout, promo/redeem and the buy UI are gated. Config keys `Credit:MintCapPerActorPer24h`, `Credit:ResolveApprovalThresholdCredits`, `Credit:PromoExposureApprovalHours` are validated at boot and are **not** owner-editable settings in M2 (D10).

**Monitoring after rollout.** `billing_credits_minted_total{actor}` (a spike by one actor is the insider signal), `billing_credit_grant_pending` (a growing gauge = owners are not deciding), the digest e-mail (a missing 08:00 mail = the anti-fraud control is broken), and 409 `grant_in_progress` rate (a lock storm means the try-lock is too coarse). Alerts ride 11.6.2-T1 only.

**DoR notes.** (1) The four contract gaps in §2 are **ruled by the README (BR18/BR20/BR27/BR32/BR33, CR-13/CR-15)**: `_12_CreditGrantRequests` is 12.5.1-T1's alone, one grant-request row per grant with `source` limited to `admin_grant`|`promo_code`, `promo_codes.created_by` + `credit_grant_requests.target_payment_id`, the CR-13 cap index, and the 15-minute `CreditGrantRequestApplyJob` with cancel kind `credit_grant_request`. (2) `CreditGrantRequestDto.targetUser`/`requestedBy` shapes must be pinned with 09.10.5-T1's admin user projection so the admin UI reads one shape. (3) The security alert e-mail copy must be in the template catalog of 09.2.6-T3 before the gate rehearsal. (4) Owner approval requires the two-owner configuration to be exercisable on staging (gate criterion).
