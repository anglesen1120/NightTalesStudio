# 09.3.4, 09.3.5, 09.3.6-T1 — Profile (`/me`), rate limiting and the audit log

> Parent: ../README.md (Part B)
> Refines: `../../09-backend-api.md` §4 epic 09.3 (stories 09.3.4, 09.3.5, 09.3.6 L136–149), §5.1, §5.4, §6.
> Contract: `../README.md` BD14, BD15, BD16, BD1, BD2, BD3, W8, W17, WF01; Part A D4 (pipeline slot 6), D5 (UnitOfWork), D7 (errors), D8 (persistence), D9 (`audit_logs` owner), D10 (tenant filter), D13 (harness), D14/D15 (API conventions, ETags), D16 (`Language`, `PlatformRole`).
> Consumers: 02 (CR-3 `[Audited]` commands), 06, 09, 11.3.3-T1 (T5), 13.6 (retention). Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE]. Status: wave-2 detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Stories.** 09.3.4 profile (Must · S 0.5); 09.3.5 abuse limits (Must · S 0.5); 09.3.6-T1 the `audit_logs` + `AuditBehavior` half of the activity log (Must · M 1.0 — 02 CR-3 split: **T1 = P**, `GET /me/activity`/`GET /episodes/{id}/activity` = 09.3.6-T2, S). Total **2.0 d** (B9).

**In scope.** `GET`/`PATCH /me` over the Identity user (`display_name`, `ui_locale` vi/en, read-only `email`, MFA state, role); the rate-limit policies `auth`/`paid`/`hub` with the 429 `rate_limited` + `Retry-After` response and the forwarded-header trust rule; the `audit_logs` table + migration, `[Audited]`/`[NotAudited]`, `AuditBehavior` in pipeline slot 6 inside the UnitOfWork transaction, the `IAuditData` projection, the `IAuditWriter` port for auth handlers/jobs/CLI, the architecture test and the ordered retention rule list consumed by 09.7.3-T1.

**Out of scope.** `GET /me/activity`, `GET /episodes/{id}/activity` and the 10 `/account/activity` screen (09.3.6-T2, S); sessions/MFA enrolment/re-auth (09.3.2-T1, 09.10.4-T1); password reset (09.3.3-T1); registration (09.3.1-T1/T3); admin endpoints (09.9.x); feature flags (09.9.2-T2); CAPTCHA and progressive login delay (09.3.1/09.3.3 own the counter; this folder only supplies the 429 shape); ingress limits (11) and the 429 UI (10).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| G1 | `AddRateLimiter`, `RateLimitPartition.GetFixedWindowLimiter`/`GetSlidingWindowLimiter`, `RejectionStatusCode`, `OnRejected`; `Retry-After` is set by hand because `OnRejected` carries no reset instant | W8 ✅ / ⚠️ |
| G2 | `auth` 10/min per IP fixed window on `/auth/*`; `paid` 30/min per user sliding (6 segments); `hub` 10 tickets/min per user; 429 `rate_limited` + `Retry-After`; the IP comes from the forwarded header only when the peer is in the LB CIDR | BD14 ✅ |
| G3 | `audit_logs` columns and index `(subject_user_id, created_at)`, plus the `(created_at, id)` sweep index of [RULED — README §B10 WT47] | `../../00-overview.md` §6 L301 ✅ |
| G4 | `[Audited(string action, Category category, Visibility visibility)]` with **required** visibility; `[NotAudited(string reason)]`; `AuditBehavior` in slot 6 inside the UnitOfWork transaction; `data` from the command's `IAuditData` only; `IAuditWriter.WriteAsync(AuditEntry e, ct)` for auth handlers/jobs/CLI; `actor_type = system` + `data.actor = "system:operator"` for operator verbs; arch test = every `ICommand` has exactly one attribute | BD15 ✅; 11 T5 ✅ |
| G5 | Retention = ordered rule list, first match wins: payment-linked → as long as the payment; `account_security` → 24 months; `actor_type = admin` or `visibility = internal` → 24 months; other `visibility = user` → 12 months ⚠️ legal review 13.6 | `../../09-backend-api.md` L146–147 ✅ |
| G6 | Pipeline `Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit`; the UnitOfWork opens the transaction **before** the handler and commits after `next()` | Part A D4, D5 ✅ |
| G7 | `users`: `email`, `display_name`, `ui_locale`, `role`, `status`, `two_factor_enabled`, `last_totp_step`, `security_stamp`, `last_login_at` — **no** `created_at` | `../../00-overview.md` §6 L266 ✅ |
| G8 | `retryAfterSeconds` in the error metadata ⇒ the `Retry-After` header; codes from `ErrorCatalogue`; `rate_limited` is catalogued 429 | Part A D7; `09 §5.4` L424 ✅ |
| G9 | D14: `/api/v1`, `IEndpointGroup`, camelCase JSON, snake_case enums, cursor `{ items, nextCursor }`; D15 lists ETags for episodes/series only | Part A D14, D15 ✅ |
| G10 | D10: `IUserOwned` + the `"tenant"` filter; cross-user reads only through `ISystemWorkQuery` + `ISystemContext.For`; other users' rows answer 404, never 403 | Part A D10 ✅ |
| G11 | P has no public sign-up and 09.3.6-T2 is S ⇒ `/me/activity` does not exist in P | W17 ✅ |
| G12 | `audit_logs` + `AuditBehavior` are **P** because 02.3.3 (attestation) and 02.8.2 (autopilot approvals) write rows in P | `../02-story-writing/README.md` F05/CR-3/L349 ✅ |
| G13 | `IAuditWriter` is already assumed by 02.6.3-T2 step 7 (a Worker job writes through the port) and 11.3.3-T1 (T5 tuple) | 02.6.3-T2 L101; `../11-worker-deployment/README.md` T5 ✅ |

## 3. Domain and data

**Enums** (`Domain/Audit/` [INFERENCE: folder]): `Category { AccountSecurity, Episodes, Publishing, Billing, Support }`, `Visibility { User, Internal }`; `ActorType` already exists in Application (D10: user/admin/system) and `Language { Vi, En }` in Domain (D16) is reused for `users.ui_locale` and e-mail locales — no second enum.

**Table `audit_logs`** — migration `<yyyyMMddHHmm>_09.3.6T1_AuditLogs`, expand-only, owner 09.3.6-T1 (D9):

```sql
CREATE TABLE audit_logs (
  id uuid PRIMARY KEY, actor_user_id uuid NULL, subject_user_id uuid NULL,      -- both nullable: system rows and failed logins on an unknown address have no user (09 §4 L143; 11.3.3 plan L137)
  actor_type text NOT NULL, action text NOT NULL, category text NOT NULL, visibility text NOT NULL,
  entity_type text NULL, entity_id uuid NULL,
  data jsonb NOT NULL DEFAULT '{}'::jsonb, ip inet NULL, user_agent text NULL,   -- ip: [INFERENCE] native type for 00 §6 'ip'
  created_at timestamptz NOT NULL,
  CONSTRAINT ck_audit_logs_user_visible_has_subject CHECK (visibility <> 'user' OR subject_user_id IS NOT NULL)
);
CREATE INDEX ix_audit_logs_subject_created ON audit_logs (subject_user_id, created_at DESC);   -- BD15 / 00 §6
CREATE INDEX ix_audit_logs_entity ON audit_logs (entity_type, entity_id) WHERE entity_id IS NOT NULL;  -- [INFERENCE] 09.3.6-T2
CREATE INDEX ix_audit_logs_created_id ON audit_logs (created_at, id);   -- [RULED — README §B10 WT47] retention sweep deletes by keyset
```

No FK from `audit_logs` to `users` — rows outlive the account (13 §5.4 hashes the stored id). Nullable `subject_user_id`, three cases [DECISION]: a user acting on his own account writes `actor_user_id = subject_user_id`, `visibility = user`; an admin acting on another account writes both ids, `visibility` from the attribute; a system row (11.3.3's `render_node.state_changed`, webhook/refund writers) and a **failed login or CAPTCHA challenge on an unknown e-mail** write `subject_user_id = null` with `visibility = internal` and **no plain address in `data`** — the attempted address is stored hashed, the way `email_messages.to_hash` hashes a recipient. A failed login on a **known** account writes that user as `subject_user_id` with `visibility = user`, so it appears in `GET /me/activity` (09 §4 L145). The `CHECK` makes "user-visible row without a subject" unrepresentable; the `(subject_user_id, created_at DESC)` index still serves the feed because a user filter never matches a null key; and the retention rules need no null branch — a null-subject row is always `visibility = internal`, so rule 3 (24 months) matches it before rule 4.

`HasEnumCheck` for `actor_type`/`category`/`visibility`; `data` `jsonb`; `Guid` PK `ValueGeneratedNever`; snake_case (D8). `AuditLog` is **not** `IUserOwned`: it carries `subject_user_id` **and** `actor_user_id`, which one tenant filter cannot express, and the retention sweep reads across users — rows are read with an explicit predicate, and the only P HTTP reader (09.3.6-T2) filters `subject_user_id = ICurrentUser.Id AND visibility = User` [DECISION, D10's explicit-predicate path for non-aggregate rows].

**Write path.**

```csharp
// Application/Abstractions/Audit/
[AttributeUsage(AttributeTargets.Class, Inherited = false)] public sealed class AuditedAttribute(string action, Category category, Visibility visibility) : Attribute;
[AttributeUsage(AttributeTargets.Class, Inherited = false)] public sealed class NotAuditedAttribute(string reason) : Attribute;

public interface IAuditData { AuditData ToAuditData(); }        // the only source of `data`
public sealed record AuditData(Guid? SubjectUserId, string? EntityType, Guid? EntityId, IReadOnlyDictionary<string, string> Summary);

public sealed record AuditEntry(Guid? ActorUserId, ActorType ActorType, Guid? SubjectUserId, string Action, Category Category,
        Visibility Visibility, string? EntityType, Guid? EntityId, string DataJson, IPAddress? Ip, string? UserAgent,
        string? ActorLabel, DateTimeOffset OccurredAt);

public interface IAuditWriter { Task WriteAsync(AuditEntry entry, CancellationToken ct); }   // joins the ambient transaction; never commits
public interface IAuditContext { IPAddress? Ip { get; } string? UserAgent { get; } }
```

`EfAuditWriter` only adds the row to `AppDbContext` — the caller's UnitOfWork commits it (D5). A caller with no Mediator pipeline (a CLI verb) opens `IUnitOfWork.BeginAsync`/`CommitAsync` itself [DECISION].

**`AuditBehavior` (slot 6, D4).**

```csharp
public sealed class AuditBehavior<TRequest, TResponse>(IAuditWriter writer, ICurrentUser user, IAuditContext ctx,
        AuditMetadataCache meta, IClock clock) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull where TResponse : IErrorOr
{
    public async ValueTask<TResponse> Handle(TRequest request, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken ct)
    {
        var response = await next(request, ct);
        if (!response.IsError && request is IAuditData data && meta.TryGet<TRequest>(out var attribute))
            await writer.WriteAsync(ToEntry(attribute, data, user, ctx, clock.UtcNow), ct);
        return response;
    }
}
```

Consequences of the slot order (G6): the write is inside the open transaction, so the row commits with the change or not at all; an error writes nothing; an idempotent replay short-circuits in slot 5 — **outside** Audit — so a replay never duplicates a row. `ActorType`: `User` when `ICurrentUser.Id == SubjectUserId`, `Admin` when the role is admin/owner and the subject differs, else `System` [DECISION, so G5's rule 3 keys on real staff actions]. `Ip`/`UserAgent` stay null for ordinary commands; auth handlers fill them. `[INFERENCE]` `AuditMetadataCache`, `IAuditContext` (Api = `HttpContext` IP + `User-Agent` ≤ 300 chars; Worker = nulls).

**Retention (consumed by 09.7.3-T1; numbers are G5).**

```csharp
public sealed record AuditRetentionRule(string Id, string Description, TimeSpan? Retain, Func<AuditRetentionRow, bool> Matches);
public sealed record AuditRetentionRow(Category Category, ActorType ActorType, Visibility Visibility, bool HasPaymentLink, DateTimeOffset CreatedAt);

public static class AuditRetentionRules
{
    public static readonly IReadOnlyList<AuditRetentionRule> Ordered =
    [
        new("payment_linked",    "kept as long as the payment record (12, accountant ⚠️)", null,                   r => r.HasPaymentLink),
        new("account_security",  "24 months [DECISION, 13.6]", TimeSpan.FromDays(730), r => r.Category == Category.AccountSecurity),
        new("staff_or_internal", "24 months [DECISION]",       TimeSpan.FromDays(730), r => r.ActorType == ActorType.Admin || r.Visibility == Visibility.Internal),
        new("user_visible",      "12 months [DECISION]",       TimeSpan.FromDays(365), r => true),
    ];
}
```

First match wins; `Retain = null` = never delete. 09.7.3-T1 deletes through this list and must not restate the numbers. `HasPaymentLink` is always `false` in P (payments are S) — rule 1 is inert until 12.

## 4. Use cases

| # | Actor | Use case | Behaviour |
|---|---|---|---|
| UC1 | User | `GET /me` | 200 `displayName`, `email`, `uiLocale`, `role`, `mfaEnabled`, `lastLoginAt`; never `securityStamp`, `passwordHash`, `lastTotpStep` |
| UC2 | User | `PATCH /me { "uiLocale": "vi" }` | 200 with the new profile; later requests read the DB (claims carry no locale) |
| UC3 | User | `PATCH /me { "displayName": "  " }` | 400 `validation_failed`, field `displayName`; no audit row |
| UC4 | User | `PATCH /me { "email": "…" }` | 400 `validation_failed`, field `email` — address change is 09.3.1-T3/admin |
| UC5 | Attacker | 11 `POST /auth/login` in a minute from one IP | the 11th is 429 `rate_limited` + `Retry-After`, unauthenticated |
| UC6 | Attacker | Spoofs `X-Forwarded-For` from a non-LB peer | ignored: the partition key stays the peer IP |
| UC7 | User | An `[Audited]` command whose handler fails | 0 rows (rollback); on success exactly 1 row with the attribute's visibility |
| UC8 | Developer | Adds a command with neither attribute | the architecture test fails the build with the command's name |
| UC9 | Operator | `nts-worker flags set youtube_publish off` | one row, `actor_type = system`, `data.actor = "system:operator"`, category `support`, visibility `internal` (BD13, 11 T5) |
| UC10 | User | Opens `/account` in the UI | profile + language from `GET /me`; subsequent e-mails use `ui_locale` (09.2.6-T3) |

## 5. API and events

| Method | Route | Auth | Policy | Success | Errors |
|---|---|---|---|---|---|
| `GET` | `/api/v1/me` | required | — | 200 `MeDto` | 401 |
| `PATCH` | `/api/v1/me` | required | — | 200 `MeDto` | 400 `validation_failed`, 401 |

```csharp
public sealed record MeDto(Guid Id, string DisplayName, string Email, Language UiLocale, PlatformRole Role, bool MfaEnabled, DateTimeOffset? LastLoginAt);
public sealed record GetMeQuery : IQuery<ErrorOr<MeDto>>;

[Audited("account.profile_updated", Category.AccountSecurity, Visibility.User)]
public sealed record UpdateMeCommand(string? DisplayName, Language? UiLocale) : ICommand<ErrorOr<MeDto>>, IAuditData;
```

- Validator: at least one field; `DisplayName` 1–80 after trim, no leading/trailing whitespace, no control characters. `PATCH` is idempotent (no key) and `/me` is outside D15's ETag list (no `If-Match`, no `ETag`) [DECISION].
- [RULED — README §B10 WT18, WT44] `UpdateMeRequest` is closed: `email`/`role`/`mfaEnabled` or a typo is a 400, not a silent no-op — via a **per-endpoint strict binding filter on `PATCH /me`** (09.3.4-T1), not an API-wide `JsonUnmappedMemberHandling.Disallow`, so no other endpoint's JSON contract changes.
- `data` = change summary only: `{ "fields": ["uiLocale"], "uiLocale": { "from": "en", "to": "vi" } }`; a display-name change records `{ "fields": ["displayName"] }` — never the text [DECISION, BD16].
- Reads use `ICurrentUser.Id` (`sub`), never a route id → no IDOR.

Rate limiting (middleware + one line per group):

```csharp
public static class RateLimitPolicies { public const string Auth = "auth"; public const string Paid = "paid"; public const string Hub = "hub"; }

options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
options.OnRejected = RateLimitRejection.WriteAsync;                                   // ProblemDetails + Retry-After
options.AddPolicy(RateLimitPolicies.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
    RateLimitPartitionKeys.Ip(ctx, ip), _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
options.AddPolicy(RateLimitPolicies.Paid, ctx => RateLimitPartition.GetSlidingWindowLimiter(
    RateLimitPartitionKeys.User(ctx), _ => new SlidingWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0, AutoReplenishment = true }));
options.AddPolicy(RateLimitPolicies.Hub, ctx => RateLimitPartition.GetFixedWindowLimiter(
    RateLimitPartitionKeys.User(ctx), _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
```

- Order: `UseForwardedHeaders` → authentication → `UseRateLimiter` → endpoints (user-keyed partitions need `sub`). Health/OpenAPI routes carry no policy.
- `OnRejected` writes the D7 `ProblemDetails` (`code = "rate_limited"`, `type = https://nighttales.app/errors/rate_limited`, 429, `retryAfterSeconds`) and D7/L424 then require the `Retry-After` header, set from `RateLimiting:RetryAfterSeconds` (default 60). `OnRejected` exposes no reset instant (G1 ⚠️), so the value is the policy window — never a negative.
- Mounting: `auth` on 09.3.2-T1's `/api/v1/auth` group; `paid` on the cost-incurring POSTs of 02 (02.2.2-T2 applies its own line — the `soft` edge), 09.6.1-T1 and 06; `hub` on 09.8.1-T2's `POST /hubs/ticket`. `/me` carries no policy (BD14 lists three) [DECISION].
- A missing `sub` on a user-keyed policy falls back to `anon:<hash of the peer IP>`, so an anonymous call can never share one global bucket [DECISION].

Events: none — a profile change raises no outbox and no SignalR event [DECISION]; the audit row is the record. Audit itself has no route; **`action` is the stable wire `messageKey`** for 09.3.6-T2 and the vi/en UI, frozen once released [DECISION].

## 6. UI

Reference `../../10-web-ui.md`: the `/account` screen (profile + language, L206, route table L309 → `/me*`), the auth screens' "429 `rate_limited` shows a countdown from `Retry-After`" (L107–108), and the S-only `/account/activity` timeline (L213). This folder supplies data and error shapes only. Two UI consequences: `PATCH /me` returns the full `MeDto` (no second call) and a 429 body carries `retryAfterSeconds` (the countdown survives a reload).

## 7. Jobs and integrations

- No new job. `RetentionSweepJob` (09.7.3-T1) consumes `AuditRetentionRules.Ordered` (G5), one delete per matched rule, walking keyset-ordered `(created_at, id)` batches ([RULED — README §B10 WT47]).
- `IAuditWriter` callers: auth handlers (09.3.2-T1/T3, 09.3.3-T1, 09.10.4-T1 — login success/failure, CAPTCHA challenge, logout, password change/reset, MFA on/off, refresh-token reuse, session revoked by security stamp), Worker outbox handlers/jobs (story generated/failed, render done/failed, publication published/failed, credits expired, refunds), CLI verbs (`nts-worker flags set`, `nts owner …`), 11.3.3-T1 (`render_node.state_changed`, T5 tuple) and 09.7.2-T1 (`episode.video_url_issued` — a **query**, so it opens `IUnitOfWork` explicitly).
- Metric `audit_rows_total{category}`; a failed insert rolls the command back and surfaces 503 `service_unavailable` (D5) — it is never swallowed [DECISION]. No audit content in logs (BD16).
- Config: `RateLimiting:Enabled` (true), `:RetryAfterSeconds` (60), `:Auth|Paid|Hub:{PermitLimit, WindowSeconds, SegmentsPerWindow}` per G2, `Http:ForwardedHeaders:KnownNetworks|KnownProxies` (empty ⇒ ignored; loopback in Development, the LB CIDR in staging/production per 11). `RateLimiting:Enabled = false` makes every policy a pass-through without a deploy (incident rollback).

## 8. Security and privacy

| Threat | Control |
|---|---|
| Audit rows used as a content store | `data` comes only from `IAuditData`; no story text, tokens, secrets or addresses; 09.3.6-T2 allow-lists keys per `action`; a reviewer reads every new command's `IAuditData` |
| PII in a row (IP, user agent) | written only for security events (auth/session writers); ordinary commands leave both null; `user_agent` ≤ 300 chars |
| Cross-tenant read | the only P reader filters `subject_user_id = ICurrentUser.Id AND visibility = User`; the retention sweep runs under D10's allow-list |
| Rows outliving a deleted account (13 §5.4) | no FK to `users`; deletion hashes the stored id instead of cascading |
| Rate-limit bypass by header spoofing | forwarded headers only from `KnownNetworks`, `ForwardLimit = 1`, `XForwardedHost` never trusted; empty config ⇒ ignored (fail closed) |
| Limiter as a memory DoS | one partition per key, `AutoReplenishment`, bounded by the limiter's built-in idle-partition cleanup ([RULED — README §B10 WT48]): no `MaxPartitions` option is claimed; 09.3.5-T1 step 1 confirms the cleanup and a size-limited store wraps the limiter if it is absent |
| `/me` over-posting (role/e-mail escalation) | closed DTO + unknown-member rejection; the handler mutates `display_name`/`ui_locale` of `ICurrentUser.Id` only |
| Token/IP in logs | rejection logs carry the policy name and a **hashed** partition key only; Serilog redaction (09.9.3-T2) covers the rest (BD16) |

## 9. Test plan

Harness: `ApiFactory`, `TestAuthHandler` (`X-Test-User`/`X-Test-Role`), real PostgreSQL 17 (`PostgresFixture`, `NTS_TEST_PG`; 09 W3 CR-35), `FakeClock`, hand-written fakes — no mocking library (D13). Rate-limit tests inject low limits through config overrides so nothing sleeps. The per-test method tables are in each task file; the suites are:

- `NightTales.Api.IntegrationTests` — `MeEndpointsTests` (own profile only, 401 unauthenticated, no security fields, locale persists, 400s for long/empty/`email`, one audit row), `RateLimitTests` + `ForwardedHeadersTests` + `RateLimitingOptionsTests` (11th request 429 with `rate_limited` + `Retry-After`, rejection never reaches the handler, per-user not per-IP partition, sliding-window smoothing, hub cap, `anon:<iphash>` fallback, disabled = pass-through, spoofed header ignored, `XForwardedHost` never trusted, bad CIDR fails startup), `AuditWriteTests` (row commits with the change, handler error rolls both back, operator tuple, row outlives user delete).
- `NightTales.Application.UnitTests` — `AuditBehaviorTests` (one row on success, none on error, one row for a keyed replay, actor-type derivation, `data` only from `IAuditData`), `AuditRetentionRulesTests` (first match wins, 730/365 windows).
- `NightTales.Architecture.Tests` — `AuditAttributeTests` (`EveryCommand_HasExactlyOneAuditAttribute`, non-empty `NotAudited` reason, action pattern + uniqueness, negative probe fixture).
- `NightTales.Infrastructure.UnitTests` — `AuditLogsMigrationTests` (columns, indexes, enum checks, no FK to `users`).

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.3.4-T1 | `GET`/`PATCH /me` (display name, UI locale, e-mail read-only, MFA state) | BE | S (0.5) | 09.3.2-T1, 09.9.2-T2 (soft, `features`; 10 CR-10) | 10.2.6-T1 (10 CR-8) |
| 09.3.5-T1 | Rate-limit policies `auth`, `paid`, `hub`, `anon`; 429 `rate_limited` with `Retry-After`; forwarded-header rule | BE | S (0.5) | 09.3.2-T1 | 02.2.2-T2 soft, 09.3.3-T2, 09.10.8-T1 (09 W3 CR-17), 13.3.4-T1 |
| 09.3.6-T1 | `audit_logs` migration, `[Audited]`/`[NotAudited]`, `AuditBehavior`, `IAuditWriter`, architecture test, retention rule list | BE | M (1.0) | 09.1.3-T2, 09.2.4-T1 | 09.2.6-T2, 09.7.3-T1, 11.3.3-T1, 09.3.6-T2, 09.9.1-T1 (09 W3 CR-17) |

Total **2.0 d** (high-level S+S+S = 1.5; +0.5 because audit grew to M — architecture test, `IAuditWriter`, system-event writers; B9).

### Open items

- [INFERENCE] names this folder introduces: the `Domain/Audit/` folder, `ip inet` + `ix_audit_logs_entity`, `ck_audit_logs_user_visible_has_subject`, `IAuditContext`, `AuditEntry.ActorLabel`, `AuditMetadataCache`, `RateLimiting:*` config keys, `audit_rows_total`, and `MeDto.MfaEnabled` as the wire name of "MFA state" (read from `users.two_factor_enabled`, owned by 09.10.4-T1).
- [DECISION] `subject_user_id` is **nullable**: 09 §4 L143 requires `account_security` rows for failed logins, and `../11-worker-deployment/11.3.3-registry-dispatch/plan.md` L137 already writes `actor_user_id`/`subject_user_id` **null** for system rows, so `NOT NULL` would contradict both. `ck_audit_logs_user_visible_has_subject` keeps a user-visible row subject-bound, and every null-subject row is `internal`, so retention rule 3 (24 months) matches it before rule 4.
- [OPEN] CR requested of 09.7.3-T1: its audit delete hardcodes the windows and has **no rule-1 predicate** (`09.7.3-T1-retention-sweep-job.md` L54–62, its `plan.md` L173–183) — a `category='billing'`, `visibility='user'` row older than 12 months would be deleted, contradicting 09 §4 L147 once plan 12 lands. It must consume `AuditRetentionRules.Ordered` and carry the payment-linked rule.
- [OPEN] Reserved `[Audited]` names live in 09.4.1 plan L66 and 09.5.1 plan L118, but B8.1 gives 09.3.6-T1 no `Depends on`/`Blocks` edge to those tasks: the attributes are applied by their owners (09.4.1-T1, 09.5.1-T1, and 02's commands already carry theirs) in their own change sets, and this task ships **no allow-list** — a command without an attribute must fail the build. If the TL wants that sequencing enforced, the edges have to be added to B8.1.
- [RESOLVED 2026-09-24] [RULED — README §B10 WT18, WT44] Closed-DTO enforcement is a **per-endpoint strict binding filter on `PATCH /me`** (09.3.4-T1) — not a global `JsonUnmappedMemberHandling.Disallow`, so 02/06 and every other endpoint keep their current contract.
- [OPEN] BD14 covers `auth`/`paid`/`hub` only: anonymous traffic outside `/auth/*` (health probes excluded, `POST /reports` in 13.3.4-T1) has **no** in-process policy. Recommend one `anon` policy (300/min per resolved IP) or an explicit statement that ingress (11) covers it.
- [OPEN] Slot 6 must be the **innermost** behaviour in 09.1.3-T2's `MediatorPipeline.Behaviors` for the same-transaction guarantee; the ordering test belongs to 09.1.3-T2, and this task's `AuditedCommand_RowCommitsWithChange` fails if the slot moves outwards.
- [OPEN] `xmin` on `audit_logs` is configured for D8 consistency although the table is insert-only; drop it if EF demands version handling on a table with no update path.
- ⚠️ G5's numbers (24/24/12 months) are pending the legal review in 13.6; they live in one place (`AuditRetentionRules`).

## 11. Rollout

- Order: 09.3.5-T1 and 09.3.4-T1 after 09.3.2-T1 (both need `sub`/`sid`); 09.3.6-T1 after 09.1.3-T2 (pipeline host) and 09.2.4-T1 (outbox). 09.3.6-T1 blocks 09.2.6-T2 (the dashboard writes rows), 09.7.3-T1 (retention) and 11.3.3-T1 (T5), and 02/06 consumers start tagging commands as soon as it lands (G12, G13).
- Gate: `09-W2-auth` (B7) exercises the 429 countdown; the audit half is verified by 02's attestation rows arriving in the same transaction.
- Rollback: the audit migration is expand-only (dropping the table loses history but breaks no writer), `/me` is one endpoint group, and the limiter has a config kill switch (`RateLimiting:Enabled = false`). No data back-out; reverting the profile locale only changes which template file 09.2.6-T3 picks.
