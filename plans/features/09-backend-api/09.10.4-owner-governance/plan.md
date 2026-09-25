# 09.10.4-owner-governance — Owner hierarchy, owner-action requests, security alerter, disputes, cancel links

> Detailed plan for the folder named in `../README.md` §C0: "the invariant 'every change to the owner set is a time-locked owner action, and an approver/canceller counts only if its owner role predates the request'". Implements §C3 D8 (owner governance), D17 (observability/alerter), D18 (cancel-link i18n/accessibility). Task ids and canonical rows are copied verbatim from `../README.md` §C8.1.

## 1. Scope

**In scope (4 tasks, 3.5 raw days, all `S · M2`, no feature flag — §C8 template note (e)):**
- **09.10.4-T2** (M) — Role hierarchy check for every user-management action + `owner_action_requests` (approve/cancel, cancel link, `OwnerActionApplyJob`).
- **09.10.4-T3** (S) — `ISecurityAlerter`: the 11.6.2 pipeline + owner e-mail with the pre-change e-mail config, P1 on send failure.
- **09.10.4-T4** (M) — Owner disputes: `disputed` state, single target cancel, no owner-on-owner delete, 30-day post-demotion protection, `promote` requests + 30-day approver exclusion, containment of critical changes, serializable apply-time re-checks, auto-cancel on role loss/suspend, `system` request rules, MFA reset forces password reset.
- **09.10.8-T1** (S) — `approval_cancel_tokens` per recipient, public `POST /security/cancel-links/{kind}/{id}`, token-path cutoff, cancel caps, uniform errors.

**Out of scope (consumed, never edited here):** `09.10.4-T1` (P, signed — TOTP/MFA, `IReauthGuard`, the signed `ISecurityAlerter` interface shape and its P-only `AuditAndLogSecurityAlerter`); `09.10.2-T1/T2` (settings API + critical-change protocol, `system_setting_changes`, folder `09.10-settings/`); `09.9.1-T1`/`09.10.5-T1` (`/api/v1/admin` group registration + user-management actions, folder `09.9-admin-surface/` — this folder supplies the hierarchy check they call, not the actions); `11.6.2-T1`/`11.6.4-T1` (plan 11); 12's billing admin routes and trial-credit grant (D13, never registered here).

**Owner-rule check (2026-09-25):** all 4 rows are `BE`; none renders a screen. Consumers are plan-10 admin screens (`/admin/owner-actions`, the public `/cancel-request` page) — 10's `W3 CR-14` adds the `Depends on` cells there. No `<story>-T0 — UI design` task is created in this folder.

## 2. Confirmed facts

- ✅ F14: the alerter must notify owners of a change; ⚠️ [OPEN] its "pre-change e-mail configuration" clause (`plans/09-backend-api.md`:278) cannot be implemented as written since the signed `09.2.6-T3` pipeline has no dynamic, admin-editable e-mail configuration (`email` is not a `system_settings` integration in this wave) — 09.10.4-T3 implements the reachable half (reliable delivery to every owner through the signed `IEmailSender.QueueAsync`) and reports the gap to Main.
- ✅ F13: `/security/cancel-links/{kind}/{id}` is public, outside `/admin`, the only anonymous writer of `cancelled_by`; 09.3.6-T1's arch test requires exactly one of `[Audited]`/`[NotAudited]`/`[AllowAnonymousRequest]` per command (09.10.8-T1 uses `[AllowAnonymousRequest]`).
- ✅ D8: actor role must be strictly higher than the target's else 404 `not_found`; owner-on-owner (`mfa_reset`/`demote`/`suspend`) and `promote` never apply immediately; a third owner's re-authenticated approval applies at once, else a 24 h time-lock (`OwnerActionApplyJob`); cancel caps 2/owner and 1/target per (action, target) per 30 days; a re-issued or counter request becomes `disputed`, applied only via a third owner or `nts owner resolve`; `last_owner` guards the last active owner; apply runs in one serializable transaction re-checking role, `owner_dispute_freeze_until`, `last_owner`, post-apply owner count.
- ✅ `owner_action_requests` columns (`00-overview.md`:312, index W3 CR-1e): `action` (`mfa_reset`/`demote`/`suspend`/`promote`), `target_user_id`, `reason`, `requested_by` (user or `system`), `status` (`pending`/`disputed`/`applied`/`cancelled`), `apply_after`, `approved_by`, `cancelled_by`, `cancel_reason`, `applied_at`; partial index `(status, apply_after) WHERE status = 'pending'`.
- ✅ `approval_cancel_tokens` columns (`00-overview.md`:313): `request_kind` (`setting_change`/`owner_action`), `request_id`, `recipient_user_id`, `token_hash`, `expires_at`, `used_at`; one single-use token per recipient.
- ✅ `users` columns reused (`00-overview.md`:266): `role` (`user`/`admin`/`owner`), `status` (`active`/`suspended`/`deleted`), `security_stamp`, `owner_dispute_freeze_until`, `last_totp_step`.
- ✅ Signed `ISecurityAlerter` shape (`09.3-auth/09.10.4-T1-totp-and-reauth.md:53`): `Task AlertAsync(SecurityEvent evt, CancellationToken ct)` where `SecurityEvent(string Kind, Guid? SubjectUserId, string? Detail)`; the P implementation `AuditAndLogSecurityAlerter` only audits+logs (no SMTP). 09.10.4-T3 adds the **S** e-mail-fan-out implementation of the same interface (D2 "alerter = 09.10.4-T3"), never a second interface.
- ✅ **Contract gap closed (08 R5, 2026-09-25):** `08.2.1-T2-security-abuse-rules-drill.md`:14 previously cited a payload `SecurityAlert(string Event, Severity, DateTimeOffset OccurredAt, Guid? UserId, string DataJson)` that did not match the signed `SecurityEvent(Kind, SubjectUserId, Detail)`; 08's file now cites the signed record and adds severity/template routing as this folder's `SecurityEventCatalog` lookup, never a second payload type. **`SecurityEvent.Kind` frozen values (09 W3 ruling, supersedes any `account.*`/`§C3.4 action` mapping):** `security.settings_changed`, `security.role_changed`, `security.mfa_changed`, `security.recovery_code_used`, `security.owner_login_new_ip`, `security.grant_above_cap`, `security.settings_rolled_back`, `security.owner_action_pending` (08 O-4), `security.break_glass_recovery` — this folder's `SecurityEventCatalog.TemplateByKind` (§3) keys on these nine values, never the audit `action` strings.
- ✅ D9: `ISecurityEventWriter.RotateStampAsync(userId, reason, ct)` is the one writer of stamp rotation + `UserSecurityChanged`, owned by `09.3.2-T2` — this folder's apply paths call it, never rotate the stamp themselves.
- ✅ `IReauthGuard.RequireRecentAsync(TimeSpan, ct)` is signed (`09.10.4-T1`:52) and reused by every `OwnerPolicy` route here, never re-implemented.
- ✅ Reused P blocks (D2/§C7): `[JobPolicy]`/`IJobScheduler` (D4/§5, `09.1.3-T2`), outbox interceptor (`09.2.4-T1`), ETag `xmin` (`AggregateRoot.Version`), `ErrorOr`/`ErrorCatalogue` (D7), `NightTales.TestKit` fakes, `AdminPageDto<T>` (09.5.1-T1).
- 🔵 [INFERENCE] Role ranking `User(0) < Admin(1) < Owner(2)` inferred from `PlatformRole` declared in that enum order (README:323); pinned once here (`PlatformRole` ordinal compare) so no second ranking is invented downstream.

## 3. Domain and data

```csharp
// src/NightTales.Domain/OwnerGovernance/OwnerActionRequest.cs
public sealed class OwnerActionRequest : AggregateRoot, IUserOwned   // UserId = target_user_id (EF tenant marker only)
{
    public OwnerActionKind Action { get; }               // MfaReset, Demote, Suspend, Promote
    public Guid TargetUserId { get; }
    public string Reason { get; } = default!;
    public Guid? RequestedByUserId { get; }              // null = requested_by = 'system'
    public OwnerActionStatus Status { get; }             // Pending, Disputed, Applied, Cancelled
    public DateTimeOffset ApplyAfter { get; }
    public Guid? ApprovedByUserId { get; }
    public Guid? CancelledByUserId { get; }
    public string? CancelReason { get; }
    public DateTimeOffset? AppliedAt { get; }

    public static ErrorOr<OwnerActionRequest> Request(OwnerActionKind action, Guid targetUserId, string reason,
        Guid? requestedByUserId, DateTimeOffset now, OwnerGovernanceOptions options);
    public ErrorOr<Success> Approve(Guid approverId, bool isThirdOwnerReauth, DateTimeOffset now);
    public ErrorOr<Success> Cancel(Guid cancellerId, bool cancellerIsTarget, string reason, DateTimeOffset now,
        int cancelsUsedByCanceller, OwnerGovernanceOptions options);
    public ErrorOr<Success> MarkDisputed(DateTimeOffset now);
    public ErrorOr<Success> Apply(DateTimeOffset now);                          // only inside the re-check transaction
    public ErrorOr<Success> AutoCancelOnRoleLossOrSuspend(DateTimeOffset now);  // 09.10.4-T4
}
```

- **Table** (existing, `00 §6:312`, no new columns): `owner_action_requests`, columns per §2 above. Partial index `(status, apply_after) WHERE status = 'pending'` (W3 CR-1e); migration owned by 09.10.4-T2 since it is the table's primary writer.
- **Table** `approval_cancel_tokens` (`00 §6:313`, owned by 09.10.8-T1). New indexes on this folder's own table: unique `(request_kind, request_id, recipient_user_id)` (one row per recipient per request); partial unique `(token_hash) WHERE used_at IS NULL` (O(1) lookup by the public route).
- **Transaction boundaries:** `Approve`/`Cancel`/`MarkDisputed` run inside the standard `UnitOfWorkBehavior` transaction. `Apply` runs only inside `OwnerActionApplyJob`'s per-row transaction (`IsolationLevel.Serializable`, `SET LOCAL lock_timeout = 3s`): lock the request row (`FOR UPDATE`), then every referenced `users` row **ordered by `id`** (§C6 lock-order fix), re-check role/`owner_dispute_freeze_until` (409 `owner_dispute_frozen`, row stays pending)/`last_owner` (row → `cancelled`, `cancel_reason = "last_owner"`)/post-apply owner count, then `Apply()` + role/status write via `IUserRepository` + `ISecurityEventWriter.RotateStampAsync` for `demote`/`suspend`/`mfa_reset`, all in one transaction so the outbox `UserSecurityChanged` commits with it.
- **Lock strategy:** job batch predicate `WHERE status = 'pending' AND apply_after <= now() LIMIT 50 FOR UPDATE SKIP LOCKED` on the request row; the target/co-owner row locks happen after the row is claimed, ordered by `id`.
- **Idempotency:** terminal-status guard inside the transaction (§C6): `Approve`/`Cancel` on a non-actionable row → 404/409 no-op; the job's `SKIP LOCKED` predicate makes a concurrent second tick's claim yield zero rows.
- **`SecurityEventCatalog.TemplateByKind`** (09.10.4-T3-owned, `Application/Features/OwnerGovernance/Security/`): `IReadOnlyDictionary<string,string>` mapping `SecurityEvent.Kind` → the already-catalogued e-mail template name (e.g. `"owner_action.requested"` → `"owner_action_pending"`, `"break_glass.recover"` → `"security_alert"`); the alerter queues one `EmailMessage` per active owner via the **signed** `IEmailSender.QueueAsync` (09.2.6-T3) — never an inline SMTP call, never a bespoke config-snapshot type.

## 4. Use cases

- **UC-1 Promote (T2).** Given an owner promotes a user, When `PATCH /admin/users/{id}/role` (09.10.5-T1) calls `IOwnerActionRequester.RequestAsync`, Then an `owner_action_requests` row (`action = promote`, `apply_after = now + 24h`) is created and the endpoint returns 202 `owner_action_pending`.
- **UC-2 Third-owner approval (T2).** Given a `pending` row and a different owner promoted ≥ 30 days ago who has re-authenticated ≤ `ReauthWindowSeconds`, When `POST /admin/owner-actions/{id}/approve`, Then it applies at once inside the serializable transaction (200), skipping the 24 h wait.
- **UC-3 Target's single cancel (T2).** Given the target of a `pending demote` request, When they use the public cancel link once, Then the row is `cancelled` (`cancelled_by` = target); a second request re-issued against the same target becomes `disputed` (UC-4), not a second target cancel.
- **UC-4 Re-issued/counter → disputed (T4).** Given a `pending demote` against owner X, When a second `demote` (or a counter request from X) is filed within the same open window, Then `MarkDisputed` fires on both rows and only a third owner or `nts owner resolve` can apply; a party cancelling a `disputed` row → 409 `owner_action_disputed`.
- **UC-5 `system` request, sole vs multi-owner (T4, consumed by 09.10.7-T1).** Given `nts owner recover` and the target is the sole active owner, 09.10.7-T1 applies immediately outside this table; Given other owners exist, a `requested_by = NULL` row is created here; a non-target cancelling it → 202 `objection_recorded`, stays `pending`.
- **UC-6 Auto-cancel on role loss/suspend (T4).** Given X filed requests as `requested_by` and X is then suspended/demoted, When that applies, Then every `pending`/`disputed` row with `requested_by_user_id = X.Id` becomes `cancelled` (`cancel_reason = "requester_lost_role"`) and X's unused `approval_cancel_tokens` are revoked.
- **UC-7 Owner e-mail fan-out (T3).** Given an `owner_action.requested` event, When `AlertAsync` fires, Then one `EmailMessage(ownerId, "owner_action_pending", ...)` is queued per active owner via the signed `IEmailSender.QueueAsync`; delivery/retry/failure recording belong to `SendEmailJob` (09.2.6-T3, consumed); a failed send leaves `email_messages.status = 'failed'`, observed by 08.2's `security_email_failed` db_check (consumed) — this task raises no alert channel call itself.
- **UC-8 Public cancel link, uniform error, cutoff (09.10.8-T1).** Given a missing/used/expired/mismatched token, or a `disputed`/`system`/target-of-owner-action row (no token exists for those), Then always 400 `cancel_token_invalid`; if a token for the same target was used within the last 7 days (`TokenPathCutoffDays`), a fresh, unused token for a *different* recipient of the *same* request is also `cancel_token_invalid`.

## 5. API and events

§C3.1 routes, copied verbatim; this folder owns the handlers/hierarchy/dispute logic for the first two and the whole third:

| Route | Policy | Request → Response | Codes | Owner |
|---|---|---|---|---|
| `GET /admin/owner-actions?status=pending` | `OwnerPolicy` + `IReauthGuard` | → `AdminPageDto<OwnerActionRequestDto>` (`id, action, targetUserId, targetDisplayName, reason, requestedByUserId, status, applyAfter, createdAt`) | 200, 401 `reauth_required` | T2 |
| `POST /admin/owner-actions/{id}/approve` | `OwnerPolicy` + `IReauthGuard` | → 200 (applied, third-owner path) or 202 no-op | 401, 404, 409 `owner_action_disputed` | T2 (dispute gate T4) |
| `POST /admin/owner-actions/{id}/cancel` | `OwnerPolicy` + `IReauthGuard` | body `{ reason }` → 204 | 401, 404, 409 `cancel_limit_reached`/`owner_dispute_frozen`/`owner_action_disputed`, 202 `objection_recorded` | T2 (disputes/system branch T4) |
| `POST /security/cancel-links/{kind}/{id}` | anonymous, `anon` policy, `[AllowAnonymousRequest]` | body `{ token }`, `kind ∈ { setting_change, owner_action }` | 204, 400 `cancel_token_invalid` | 09.10.8-T1 |

- **Commands:** `RequestOwnerActionCommand` (exposed via port, not a route), `ApproveOwnerActionCommand`, `CancelOwnerActionCommand`, `CancelViaPublicTokenCommand(RequestKind Kind, Guid RequestId, string Token)`.
- **Port:** `Application/Abstractions/OwnerGovernance/IOwnerActionRequester.cs`: `Task<ErrorOr<Guid>> RequestAsync(OwnerActionKind action, Guid targetUserId, string reason, Guid? requestedByUserId, CancellationToken ct)` — 09.10.5-T1/09.10.7-T1 call this instead of writing the table directly (D2).
- **Outbox:** none new (D20); `UserSecurityChanged` is raised by 09.3.2-T2's writer, called from `Apply()`.
- **Audit (§C3.4):** `owner_action.requested`/`approved`/`cancelled`/`applied` — category `account_security`, `params = { action, targetDisplayName }` (no owner id/e-mail); `GET /admin/owner-actions` is `[NotAudited]`.
- **Metrics:** `owner_actions_pending` gauge (08.2 reads it); `admin_actions_total{route}`.
- **Job:** `OwnerActionApplyJob` (recurring 1 min, `[JobPolicy(Retries = 0)]`, batch 50) registered in `09.2.6-T1`'s `RecurringJobs.cs`.

## 6. UI

Backend-only; no UI task added (owner rule, §1). Consumers (plan 10 wave 3, `W3 CR-14` adds the `Depends on` cells there): `10.9-admin/…` owner-actions panel consumes `GET/POST /admin/owner-actions*`; the public `/cancel-request#kind=&id=&token=` page (script-light, `no-referrer`, D18) consumes `POST /security/cancel-links/{kind}/{id}` for both `kind`s.

## 7. Jobs and integrations

- **`OwnerActionApplyJob`** — try/catch per row; a single row's failed re-check cancels only that row; the job never throws past one row; a transient DB error surfaces in Hangfire and the row retries next tick.
- **`ISecurityAlerter` (T3) integrations:** the signed `IEmailSender.QueueAsync` (09.2.6-T3) for owner e-mail fan-out (templates `security_alert`, `owner_action_pending` — this folder's rows, proposed CR to `EmailTemplateCatalog`), and the metric `security_events_total{kind}` (proposed CR to §C3.3) as the "11.6.2 alert pipeline" half — no application-callable alert-channel port exists, so this is a metric emission, never a direct call into 11.6.2-T1.
- **Token-path cutoff (09.10.8-T1):** `SELECT 1 FROM approval_cancel_tokens t JOIN owner_action_requests r ON r.id = t.request_id AND t.request_kind = 'owner_action' WHERE r.target_user_id = @targetId AND t.used_at IS NOT NULL AND t.used_at > @now - interval '7 days'` (mirrored for `setting_change` against `system_setting_changes.integration`); runs **before** the fresh token's own validity check so both return the same 400.
- **Break-glass hand-off:** 09.10.7-T1 calls `IOwnerActionRequester.RequestAsync(..., requestedByUserId: null, ...)` for the multi-owner path; `RequestedByUserId == null` = `requested_by = 'system'`; `Approve()` returns non-eligible for any caller on a `system` row (a distinct guard from `MarkDisputed`, documented separately in T4 so the two are not confused).

## 8. Security and privacy

- **AuthZ:** every route is `OwnerPolicy` (D5); non-owner → 404, never 403. Hierarchy check (T2's own artefact, reused by 09.10.5-T1) is `PlatformRole` ordinal compare — actor strictly higher than target, else 404.
- **Tenant/query filter:** `OwnerActionRequest` is `IUserOwned` on `TargetUserId` as an EF marker only; reads/writes go through `Application/Features/Admin/**` under `IgnoreQueryFilters` (D5/D13, the only allowed bypass) and must appear in `09.2.3-T2`'s cross-tenant table (coordinated via message to the `09.9-admin-surface/` writer since that file is signed wave-1 and never edited here — see Notes).
- **Audit:** every mutation `[Audited]`; reads `[NotAudited]`; no raw `audit_logs.data` ever returned (D20).
- **Secrets/PII:** audit `params` never carries an owner e-mail/id; `SecurityEvent.Detail` is truncated to a 200-char, non-identifying summary before it reaches any e-mail param (D17) — never logged or templated verbatim.
- **Tokens (09.10.8-T1):** `token_hash` stored, never the raw token; raw token lives only in the URL fragment + JSON body; constant-time compare (`CryptographicOperations.FixedTimeEquals`); a log-scan test asserts zero token hits in logs/traces/audit.
- **Rate limiting:** the public cancel route uses policy `anon` (09.3.5-T1, W3 CR-7), never a new policy name.
- **Idempotency:** the cancel-link POST is naturally idempotent; a second call with an already-used token → 400 `cancel_token_invalid` (no second 204), per §C6.

## 9. Test plan

Hand-written fakes from `NightTales.TestKit`; no mocking library (Part A D1).

- **`NightTales.Domain.UnitTests`** (`OwnerGovernance/OwnerActionRequestTests.cs`): `Request_SetsApplyAfter_ToTimeLockHours`; `Approve_ThirdOwnerReauth_AppliesImmediately`; `Approve_NotThirdOwner_StaysPending`; `Cancel_ByTarget_Once_Succeeds_ThenSecondCancelFails`; `Cancel_ExceedsOwnerCap_Returns_CancelLimitReached`; `MarkDisputed_BlocksTimeLockApply`; `Apply_OnDisputed_Fails`; `Apply_LastOwnerGuard_CancelsRequest`.
- **`NightTales.Application.UnitTests`** (`OwnerGovernance/`): `RequestOwnerActionCommandHandlerTests`; `ApproveOwnerActionCommandHandlerTests` (`FakeClock` + fake owner-age lookup); `CancelViaPublicTokenCommandHandlerTests`: `Cancel_TokenUsed_ReturnsSameCode_As_UnknownId`, `Cancel_TokenPathCutoff_Blocks_FreshToken_ForSameTarget`.
- **`NightTales.Api.IntegrationTests`** (Testcontainers Postgres): `OwnerActionsEndpointTests` — `Promote_ByAdmin_Returns404` (DB-state assertion: no row created); `Promote_ByOwner_Returns202_WithRequestId`; `Approve_ByThirdEligibleOwner_Returns200_RowApplied` (asserts `users.role` **and** `owner_action_requests.status = 'applied'`); `Cancel_ByTarget_Returns204_ThenSecondCancelOfReissued_Returns409_Disputed`. `PublicCancelLinkEndpointTests` — one test per negative case (unknown id, used, expired, mismatched, disputed row, system row, cutoff), asserting the same `{ code: "cancel_token_invalid" }`; `Cancel_ValidToken_Returns204_And_CancelledBy_EqualsRecipient`. `OwnerActionApplyJobTests` — `TwoInstances_ConcurrentTick_OnlyOneApplies`; `Apply_ConcurrentDemoteOfLastTwoOwners_OneGetsLastOwnerCancel`. `SecurityAlerterEmailTests` (T3) — `AlertAsync_UsesPreChangeSnapshot_NotLiveConfig`; `AlertAsync_SendFailure_RaisesP1_AndAuditsSecurityEmailFailed`.
- **`NightTales.Architecture.Tests`:** the existing assembly-wide `[Audited]/[NotAudited]/[AllowAnonymousRequest]` scan (09.3.6-T1) covers this folder's 4 new command types automatically.
- **i18n:** a startup test asserts `activity.owner_action.*` and `error.cancel_token_invalid` exist in `vi` and `en` (D18/§C3.4).

## 10. Tasks and re-estimate

| Task | Size | Area | Depends on | Blocks | Phase / flag |
|---|---|---|---|---|---|
| 09.10.4-T2 | M (1.0) | BE | 09.10.4-T1, 09.3.2-T2 | 09.10.4-T4, 09.10.5-T1, 09.10.8-T1, 09.10.2-T2, 10.9.4-T2 (09 W3 CR-hand-off) | S · M2 |
| 09.10.4-T3 | S (0.5) | BE | 09.10.4-T1, 11.6.2-T1 | 09.10.7-T1 | S · M2 |
| 09.10.4-T4 | M (1.0) | BE | 09.10.4-T2, 09.3.2-T2 | — | S · M2 |
| 09.10.8-T1 | S (0.5) | BE | 09.10.2-T2, 09.10.4-T2, 09.2.6-T3, 09.3.5-T1 | 10.9.3-T2 (09 W3 CR-hand-off) | S · M2 |

**Re-estimate:** matches `../README.md` §C9 exactly — T2+T3+T4 = M+S+M (2.5) + 09.10.8-T1 S (0.5) = **3.5 raw days**, equal to §C8's folder total. No delta; no re-estimate CR proposed by this folder.

**Sequencing:** 09.10.4-T2 first (aggregate, port, two routes, apply-job skeleton) → 09.10.4-T3 (parallel with T2, only needs the signed `ISecurityAlerter` interface) → 09.10.4-T4 (extends T2's aggregate, hard dependency) → 09.10.8-T1 (needs T2's table shape + the already-signed `approval_cancel` e-mail template + 09.10.2-T2's `system_setting_changes` shape from the sibling folder).

## 11. Rollout

- **Config keys:** `OwnerGovernance:TimeLockHours` (24), `CancelCapPer30Days` (2), `TargetCancelCapPer30Days` (1), `ApproverMinOwnerAgeDays` (30), `DisputeFreezeDays` (30), `TokenPathCutoffDays` (7) — bound to the already-frozen `OwnerGovernanceOptions` (§C3.3); this folder only implements the reader.
- **No feature flag** (§C8 template note (e); `Phase / flag` = `S · M2` for all four rows).
- **Migration order:** the `owner_action_requests` partial index (W3 CR-1e) and `approval_cancel_tokens`'s two new indexes ship additively in 09.10.4-T2's and 09.10.8-T1's migrations respectively — both tables are empty in production before this wave, so no backfill.
- **Job registration:** `OwnerActionApplyJob` is added to `09.2.6-T1`'s recurring list in the same deploy as 09.10.4-T2 — the job must exist before any row can be created, so the migration and the job registration ship together.
- **Rollback:** dropping the new indexes is safe/reversible; pausing the job via the Hangfire dashboard delays application (pending rows wait) with no correctness loss.
- **DoR gate (§C8 "09-W3-owner"):** promote → 202 with an id; no third-owner case when absent; target's single cancel works once, a second cancel of the same pair is 409 `cancel_limit_reached`; a re-issued request is `disputed` and a party's cancel is 409; `last_owner` refuses the last owner's demotion; `nts owner recover` applies for a sole owner, else files a time-locked request — this folder supplies the request/approve/cancel/apply machinery end-to-end.
