# 06.S — SaaS slice: multi-client OAuth settings, fairness queue, admin quota view, flag rollout

> Level: detailed feature plan (00-overview §10.1 sections 1–11). Contract: `../README.md` **Part B** (§B1 facts S1–S20, §B2 F23–F32, §B3 D21–D29, §B4 CR-13…CR-19, §B8.1). Part A (P) is signed and is **not** reopened here; this plan adds only the four S tasks.
> Phase: **S · M2** (Gate 2 SaaS) — 1.25 d. Every task is behind `RequireFeature("youtube_publish")` through the existing P guard (Part A D1); recurring jobs keep registering on `enabled` only (ST8).

## 1. Scope

**In —** the four S items of `../../06-youtube-posting.md` §6 (`00-overview.md:101`):

| Task | What it delivers |
|---|---|
| 06.1.2-T3 | `SettingsOAuthClientResolver` over `system_settings` (keys `oauth.active_client_id`, `oauth.clients`) with the P `ConfigOAuthClientResolver` as the fallback while no row exists; `IYouTubeClientRemovalGuard` (409 `oauth_client_in_use`); read-only `GET /api/v1/admin/youtube/clients` |
| 06.3.2-T1 | Tables `platform_user_quota_usage` / `platform_user_quota_limits`, port `IUserQuotaLedger` (try-reserve/release, one statement each), the CR-13 reserve/release insertion in `PublishYouTubeJob`, admin override routes, `youtube_user_quota_deferred_total` |
| 06.3.3-T1 | `GET /api/v1/admin/quota` → `AdminQuotaDto` (day row, queue length, per-user top-20), three statements, `Features/Admin` |
| 06.5.2-T1 | `RolloutRuleEvaluator` over `feature_flags.rules` for `youtube_publish`, the additive `IFeatureFlags.IsEnabledForAsync`, the rules write path (CR-14/CR-15), the per-run guard in 02's autopilot job (CR-16) |

**Out.**
- All P work (Part A) and every P id: nothing in `06.1-connect`…`06.4-revocation` is re-estimated or rewritten (CR-13/CR-13b are the only P-side edits, and they are three inserted lines in a task Part B does not own).
- UI: the three admin screens (quota, client counts, quota-limit form), their vi+en strings and a11y are the **10 S admin slice** — handed over as CR-19 (§6, §7).
- 09 internals: `system_settings` storage, Data Protection purposes and versions (09.10.1-T1), the admin policy/rate policies (09.9.1-T1), flag read service + CLI + eviction (09.9.2-T2). This slice consumes them via CR-14/CR-15/CR-17.
- 12 credits, 13 compliance, TikTok (07), the P ETA endpoint (`/publishing/youtube/eta`).

**Dependencies are contracts:** `06.3.1-T1` (`IQuotaLedger`, `PacificQuotaDay`, platform-table conventions — `06.3.1-T1:21,38-53,63-86`), `06.2.3-T2` (`PublishYouTubeJob` quota block, YR19 release semantics — `06.2.3-T2:56,62`), `06.1.2-T2` (`channel_connections.oauth_client_id`, `IOAuthClientResolver`), Part A D5/D10/D11/D13/D20.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| F1 | S scope of 06 = exactly these four tasks; M2 closed beta; sizes XS/XS/S/XS; S total 1.25 d = `00 §4.1` | `00-overview.md:101, 70`; `06-youtube-posting.md:175-177,190-191,197`; `../README.md:242` ✅ |
| F2 | Fairness story: "at most N uploads per user per PT day ([DECISION] 3 in beta), FIFO by `consent_at`; admins can override"; F20 left the data model open | `06-youtube-posting.md:95-96`; `../README.md:85` ✅ |
| F3 | Admin view story: `GET /admin/quota` shows used/limit/queue length today and forecast | `06-youtube-posting.md:98-99` ✅ |
| F4 | Rollout story: `youtube_publish` rules `jsonb`, chain admins → beta users → everyone | `06-youtube-posting.md:119-120` ✅ |
| F5 | `IQuotaLedger.TryReserveAsync(string platform, DateTimeOffset now, ct) → ErrorOr<QuotaReservation>`; `QuotaReservation` is a readonly record struct; `ReleaseAsync` always succeeds; `PacificQuotaDay.Day/NextResetAt` is the single PT-day implementation (DST-safe) | `06.3.1-T1:38-53,63-86` ✅ |
| F6 | Platform-counter conventions: no `id`, no `xmin`/ETag, no `HasQueryFilter`, no `IUserOwned`, every statement filters the full PK, migration name `<yyyyMMddHHmm>_<Owner>_<What>`, `HasEnumCheck`/partial-index helpers | `06.3.1-T1:14,21,86`; `09.2.2-T1` ✅ |
| F7 | Upload job quota block: step 6 reserve → `reservedThisRun`; `youtube_quota_exhausted` → `RescheduleUpload(QuotaExhausted, NextPtMidnight(clock) + jitter(≤10 min))`; release in `finally` only under YR19 (`reservedThisRun`, `sessionCreateSent`, `quotaExceeded`/`uploadLimitExceeded`); resume skips the reserve; session-404 restart reserves a fresh unit; `PublicationErrorCodes` declared once in Domain | `06.2.3-T2:56,58,62`; `06.2.3-upload/plan.md:135,260,277` ✅ |
| F8 | `publications` has an index on `(status, next_attempt_at)` (the claim query's index) — reused by the queue count and the deferral skip | `06.2.5-T1:197` ✅ |
| F9 | `00 §6`: `platform_quota_usage(platform, quota_day, units_used, units_limit)`; `system_settings(integration, key, value jsonb, secret_ciphertext, secret_hint, is_secret, version, updated_by, updated_at)` unique `(integration,key)`; `system_setting_changes`; `feature_flags(key, enabled, rules jsonb)`; `audit_logs(actor_user_id, actor_type, subject_user_id, action, category, visibility, entity_type, entity_id, data jsonb, ip, user_agent, created_at)`; user-FK precedents on non-`IUserOwned` tables (`audit_logs.actor_user_id`, `credit_grant_requests.target_user_id`) | `00-overview.md:292,309,311,308,307,314` ✅ |
| F10 | `00 §8`: `GET /admin/quota`, `PATCH /admin/feature-flags/{key}`, `GET /admin/settings`, `PUT /admin/settings/{integration}` already listed | `00-overview.md:367-368` ✅ |
| F11 | Roles `user`/`admin`/`owner`; MFA mandatory for admin/owner; ASVS L2; integration secrets encrypted + write-only; Data Protection purpose chain `settings.v1/{integration}/{key}` | `00-overview.md:43,417`; `plans/09-backend-api.md:247` ✅ |
| F12 | `IgnoreQueryFilters` allowed **only** in `Features/Admin` (09.9.1) + the named system jobs; arch tests require `IUserOwned` on episode/series FK entities and maintain the allow-list | `plans/09-backend-api.md:94,505` ✅ |
| F13 | Flags in P: `IFeatureFlags.IsEnabledAsync(key, ct)`, `CachedFeatureFlags(IFeatureFlagReader, IMemoryCache, IClock)`, `RequireFeature("youtube_publish")` → 404 `feature_disabled` through the 09.1.4-T1 catalogue, CLI `nts-worker flags set <key> on|off`, audit `feature_flag_changed` (`support`/`internal`), event `feature_flags_changed.v1 { Key, Enabled, OccurredAt }`, `feature_flags` not `IUserOwned` | `09.9-platform/plan.md:11,42,60,104-111,174,188` ✅ |
| F14 | `rules jsonb` unused in P: the reader answers from `enabled` only; percentage/targeted rollout belongs to 06.5.2 (S); [RULED WT13] | `09.9-platform/plan.md:80,262`; `09.9.2-T2:44-45` ✅ |
| F15 | 09 consumers are S: 09.9.1-T1 (admin endpoints), 09.9.2-T1 (flags write), 09.10 except 09.10.4-T1 (so 09.10.1-T1 = `system_settings` + versions) | `plans/09-backend-api.md:541-548`; `features/09-backend-api/README.md:607` ✅ |
| F16 | Connection/client pinning: `channel_connections.oauth_client_id`; `IOAuthClientResolver.ResolveAsync(string oauthClientId, ct) → ErrorOr<OAuthClient(ClientId, ClientSecret)>` + `ActiveClientId`; P impl `ConfigOAuthClientResolver` (`YouTube:ActiveClientId`, `YouTube:Clients:<clientId>:SecretName` → `nts/<env>/api/YOUTUBE_CLIENT_SECRET`) | `../README.md:93,102` ✅ |
| F17 | A removed/broken client already has a defined failure path: `invalid_client`/`unauthorized_client` → `platform_incidents` hold, cleared only by the operator | `../README.md:83,87`; YR4 ✅ |
| F18 | Part A D1/D11: every 06 route is behind `RequireFeature("youtube_publish")`; `YouTubeOptions` is the single option class (`PublishLeadMinutes` 120, `MinVetoWindowHours` 2, `AuditStatus`, `Quota.UnitsPerDay` 100) | `../README.md:91,111`; `06.3.1-T1:94` ✅ |

## 3. Domain and data

**New tables (this slice's migration `<yyyyMMddHHmm>_06_UserQuotaUsage`, additive/expand-only, `06.3.1-T1:14` conventions):**

```sql
CREATE TABLE platform_user_quota_usage (
    platform      text        NOT NULL CHECK (platform IN ('youtube')),
    user_id       uuid        NOT NULL REFERENCES users(id),
    quota_day     date        NOT NULL,
    uploads_used  int         NOT NULL DEFAULT 0 CHECK (uploads_used >= 0),
    uploads_limit int         NOT NULL CHECK (uploads_limit > 0),
    units_used    int         NOT NULL DEFAULT 0 CHECK (units_used >= 0),
    updated_at    timestamptz NOT NULL,
    CONSTRAINT pk_platform_user_quota_usage PRIMARY KEY (platform, user_id, quota_day)
);

CREATE TABLE platform_user_quota_limits (
    user_id         uuid        NOT NULL REFERENCES users(id),
    uploads_per_day int         NOT NULL CHECK (uploads_per_day BETWEEN 1 AND 100),
    reason          text        NOT NULL CHECK (char_length(reason) BETWEEN 3 AND 500),
    updated_by      uuid        NOT NULL,
    updated_at      timestamptz NOT NULL,
    CONSTRAINT pk_platform_user_quota_limits PRIMARY KEY (user_id)
);
```

Rules that hold for both: **no** `id` column, **no** `xmin`/ETag, **no** `HasQueryFilter`, **not** `IUserOwned` (platform tables — `06.3.1-T1:21`), no secondary index (every statement filters the full PK), `snake_case` mapping via the entity configuration. `users(id)` FKs follow the `audit_logs.actor_user_id`/`credit_grant_requests.target_user_id` precedent (F9); the 09.2.3-T2 allow-list line for the two tables is CR-17.

**Entities/configurations/DbSets:** `src/NightTales.Domain/Publishing/PlatformUserQuotaUsage.cs`, `PlatformUserQuotaLimit.cs`; `src/NightTales.Infrastructure/Persistence/Configurations/PlatformUserQuotaUsageConfiguration.cs`, `PlatformUserQuotaLimitConfiguration.cs`; `IAppDbContext`/`AppDbContext` gain `DbSet<PlatformUserQuotaUsage> PlatformUserQuotaUsage` and `DbSet<PlatformUserQuotaLimit> PlatformUserQuotaLimits`.

**Ports (Application):**

```csharp
// src/NightTales.Application/Abstractions/Publishing/IUserQuotaLedger.cs
public interface IUserQuotaLedger
{
    Task<ErrorOr<UserQuotaReservation>> TryReserveAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default);
    Task<ErrorOr<Success>> ReleaseAsync(UserQuotaReservation reservation, CancellationToken ct = default);
}
// src/NightTales.Application/Abstractions/Publishing/UserQuotaReservation.cs
public readonly record struct UserQuotaReservation(string Platform, Guid UserId, DateOnly Day);

// src/NightTales.Application/Abstractions/Publishing/IYouTubeClientRemovalGuard.cs
public interface IYouTubeClientRemovalGuard
{
    Task<ErrorOr<Success>> EnsureRemovableAsync(IReadOnlyCollection<string> proposedClientIds, CancellationToken ct = default);
}
```

**Reserve statement (one round-trip, atomic):**

```sql
INSERT INTO platform_user_quota_usage (platform, user_id, quota_day, uploads_used, uploads_limit, units_used, updated_at)
VALUES ('youtube', @u, @d, 1, COALESCE((SELECT uploads_per_day FROM platform_user_quota_limits WHERE user_id = @u), @default), 1, now())
ON CONFLICT (platform, user_id, quota_day) DO UPDATE
   SET uploads_used  = platform_user_quota_usage.uploads_used + 1,
       units_used    = platform_user_quota_usage.units_used + 1,
       uploads_limit = COALESCE((SELECT uploads_per_day FROM platform_user_quota_limits WHERE user_id = @u), @default),
       updated_at    = now()
 WHERE platform_user_quota_usage.uploads_used
       < COALESCE((SELECT uploads_per_day FROM platform_user_quota_limits WHERE user_id = @u), @default)
RETURNING uploads_used, uploads_limit;
```

0 rows → `Error.Conflict("user_upload_limit_reached")` (429); 1 row → `new UserQuotaReservation("youtube", userId, PacificQuotaDay.Day(now))`. `units_used` is seeded to `1` on insert and incremented by 1 on every `DO UPDATE` branch alongside `uploads_used`, so the two counters never drift (**06 CR-20**). The live `COALESCE` makes an override effective on the next statement; `uploads_limit` keeps the value last used (the admin view shows it as `uploadsLimit`). `ReleaseAsync` is one statement — `UPDATE platform_user_quota_usage SET units_used = units_used + 1 …` is **wrong**; the release is `SET units_used = GREATEST(units_used - 1, 0), uploads_used = CASE WHEN units_used > 0 THEN GREATEST(uploads_used - 1, 0) ELSE uploads_used END, updated_at = now() WHERE platform = @p AND user_id = @u AND quota_day = @d` and always returns `Result.Success` (the `IQuotaLedger.ReleaseAsync` contract, F5) — idempotent for a missing row.

**Configuration:** `YouTubeOptions.Quota` (the class of F18) gains `UploadsPerUserPerDay = 3`, bound to `YouTube:Quota:UploadsPerUserPerDay`, validated 1–100 at startup next to the existing option validators. `NightTalesMetrics` gains `youtube_user_quota_deferred_total` (counter) and `feature_flag_evaluations_total{key,result}` [INFERENCE names] on the 09.9.3 holder.

**Invariants.** (1) One row per `(platform, user, PT day)`; `uploads_used ≤` the effective limit always holds because the only writer is the conditional statement above. (2) The per-user slot and the global unit in `platform_quota_usage` move together 1:1 for the same publication (ST1/ST2) — a test asserts equality after a mixed sequence. (3) `platform_user_quota_limits` has at most one row per user; the option is the fallback. (4) No secret, token or content field is ever stored in the two tables or in `system_settings.value jsonb` for `youtube` (F27).

## 4. Use cases

| UC | Use case | Contract |
|---|---|---|
| UC-1 | **Resolve the OAuth client of a connection** — `SettingsOAuthClientResolver.ActiveClientId` / `ResolveAsync(oauthClientId, ct)`. Settings map present (30 s `IMemoryCache`) → the row wins; missing entry or an unresolvable `clientSecretKey` → `Error.NotFound("oauth_client_unknown")`; **no `youtube` row at all** → the P `ConfigOAuthClientResolver` answer (O7, fallback). Secrets are read by name from the secret manager (F16); the settings row never carries secret material. | D27 |
| UC-2 | **Refuse an unsafe client removal** — `IYouTubeClientRemovalGuard.EnsureRemovableAsync(proposedClientIds, ct)`: for every currently-mapped id **absent** from the proposal, count `channel_connections` pinned to it; count > 0 → `Error.Conflict("oauth_client_in_use")` with metadata `{ clientId, connections }`; a proposal without the active id → the same error for that id. Called by the settings write path inside its transaction (CR-17), so a concurrent connect cannot slip between check and write. | D27, F26 |
| UC-3 | **Admin client counts** — `GetYouTubeClientSettingsQuery() : IQuery<YouTubeClientSettingsDto>`: one grouped query over `channel_connections` (all statuses, `Features/Admin`, F12) + the settings map; `Connections` counts every pinned row, `ActiveConnections` only `status = 'active'`, `InUse = Connections > 0`, `LastConnectedAt = MAX(connected_at)`. No token, channel name or e-mail leaves the handler. | D27, F24 |
| UC-4 | **Reserve a per-user upload slot** — `IUserQuotaLedger.TryReserveAsync(userId, now, ct)` (statement above). Success → proceed to the global reserve; `user_upload_limit_reached` → the caller reschedules the row to `scheduled`/`next_attempt_at = PacificQuotaDay.NextResetAt(now) + jitter(≤10 min)` and **does not** call the global ledger (ST1). | D22, D24, F23 |
| UC-5 | **Release a per-user slot** — `IUserQuotaLedger.ReleaseAsync(reservation, ct)` on exactly YR19's branch (`reservedUserThisRun`, `sessionCreateSent == false`, or `quotaExceeded`/`uploadLimitExceeded`, F7); always success, floor 0. | D24, F25, ST2 |
| UC-6 | **Override a user's daily cap** — `SetUserQuotaLimitCommand(userId, uploadsPerDay, reason)` / `ClearUserQuotaLimitCommand(userId)` upsert/delete `platform_user_quota_limits` (`updated_by` = actor), audit `publishing.user_quota_limit_changed` / `…_cleared`, return `204`. Validation: `1..100`, reason `3..500` chars, user exists and is not deleted (else 404 `not_found`). | D26 |
| UC-7 | **Admin quota view** — `GetAdminQuotaQuery() : IQuery<AdminQuotaDto>`: (1) `platform_quota_usage` day row (`platform = 'youtube'`, `quota_day = PacificQuotaDay.Day(now)`; missing → used 0 / limit `UnitsPerDay`); (2) `COUNT(*)` over `publications WHERE status = 'scheduled'` (index F8); (3) `platform_user_quota_usage` ⋈ `users` `ORDER BY uploads_used DESC, user_id LIMIT 20` with `uploads_limit`, an `EXISTS` on the override table and `MIN(next_attempt_at)` per user (window function in the same statement). `Available = max(0, Limit − Used)`, `NextResetAt = PacificQuotaDay.NextResetAt(now)`. Three statements, asserted. | D25, F30 |
| UC-8 | **Evaluate a rollout for a subject** — `RolloutRuleEvaluator.IsEnabled(flagKey, enabled, rulesJson, FeatureFlagSubject)` (pure): `enabled == false` → false; `rules` null/unparsable/unknown `rollout` → `enabled` (ST4); `admins` → `subject.IsAdmin`; `list` → `subject.UserId ∈ userIds`; `percent` → `subject.UserId is not null && Bucket(key, userId) < percentage`; `all` → true. `Bucket` = SHA-256(`"{key}:{userId}"`) first 4 bytes big-endian `% 100` (ST5). Cached with the P row (30 s TTL + `feature_flags_changed.v1` eviction, F13) — no second cache. | D28, F28, F29 |
| UC-9 | **Write rollout rules** — `PATCH /api/v1/admin/feature-flags/{key}` body `{ enabled, rules }` validated against the pinned schema (unknown `rollout` → 400 `validation_failed`; `percent` ⇒ `0..100`; `list` ⇒ 1–500 ids; `off` ⇒ ignores `userIds`/`percentage`), audited (`feature_flag_changed`, `support`/`internal`), eviction through the existing event (unchanged payload, F13). CLI `nts-worker flags set <key> on --rules '<json>'` / `--rules clear`. | D28, CR-15 |

## 5. API and events

| Verb + path | Auth | Request | Response | Codes |
|---|---|---|---|---|
| `GET /api/v1/admin/quota` | admin/owner policy + MFA (F11) | — | `AdminQuotaDto` (§6) | 200; 403 `forbidden` (non-admin); 404 (soft-deleted caller) |
| `PUT /api/v1/admin/users/{userId}/quota-limit` | admin/owner + MFA; rate policy `paid` | `{ "uploadsPerDay": 3, "reason": "beta support" }` | 204 | 204; 400 `validation_failed`; 404 `not_found`; 403 `forbidden` |
| `DELETE /api/v1/admin/users/{userId}/quota-limit` | same | — | 204 | 204; 404 `not_found`; 403 `forbidden` |
| `GET /api/v1/admin/youtube/clients` | admin/owner + MFA | — | `YouTubeClientSettingsDto` (§6) | 200; 403 `forbidden` |
| `PATCH /api/v1/admin/feature-flags/{key}` | admin/owner + MFA (09.9.1-T1, CR-15) | `{ "enabled": true, "rules": { … } \| null }` | 200 `{ key, enabled, rules }` | 200; 400 `validation_failed`; 404 `not_found` (unknown key); 403 `forbidden` |
| `nts-worker flags set <key> on\|off [--rules '<json>'\|--rules clear]` | worker secret path (F13) | — | exit 0/1/2 | 1 = invalid rules schema or key format |

Every route above is additionally behind `RequireFeature("youtube_publish")` (Part A D1) and answers 404 `feature_disabled` when the flag is off — including for an admin (ST4: no existence leak, BD13). `AdminQuotaDto` and the client DTOs are exactly §B3 D25/D27 (field names, camelCase on the wire per 09 D14).

**Events.** No new outbox event type, no new realtime id (ST8). The slice reuses `feature_flags_changed.v1` (F13, payload unchanged — the eviction handler reloads the row, so a rules change evicts identically) and writes only audit rows: `publishing.user_quota_limit_changed`, `publishing.user_quota_limit_cleared`, `feature_flag_changed` (09.9.2-T2's existing action). Errors through the 09.1.4-T1 catalogue: new codes `user_upload_limit_reached` (429), `oauth_client_in_use` (409), `oauth_client_unknown` (404) — CR-17.

## 6. UI

**None in this slice.** The API returns codes and ISO-8601 timestamps only (no localized copy, no HTML), so all vi+en strings, the a11y checks and any polling belong to the 10 S admin slice (F31). Handover (CR-19, §B5):

- Quota screen `admin/quota`: `{ quotaDay, used, limit, available, nextResetAt, queueLength, users[] { userId, displayName, uploadsToday, uploadsLimit, overridden, earliestScheduledAt } }`; the "forecast" copy is derived from `available`/`nextResetAt` client-side; vi keys `admin.quota.title|used|limit|queue|reset|override.active`.
- Clients screen `admin/youtube/clients`: `{ activeClientId, clients[] { clientId, displayName, active, connections, activeConnections, lastConnectedAt, inUse } }`; removal lives in the 09.10 settings UI (that route's 409 `oauth_client_in_use` is rendered as a blocking dialog listing the count); vi keys `admin.clients.title|connections|active|inUse`.
- Quota-limit control on the user row: `PUT`/`DELETE /admin/users/{userId}/quota-limit` with the reason field (3–500 chars) and a confirm step; vi keys `admin.users.quotaLimit.set|clear|reason`.

## 7. Jobs and integrations

- **`PublishYouTubeJob` (06.2.3-T2) — CR-13/CR-13b, the only P-side change.** In step 6, before `quota.TryReserveAsync("youtube", clock.UtcNow, ct)`, call `userQuota.TryReserveAsync(publication.UserId, clock.UtcNow, ct)`; `user_upload_limit_reached` → `RescheduleUpload(PublicationErrorCodes.UserUploadLimitReached, NextPtMidnight(clock) + jitter(≤10 min))` and **no** global reserve in that run (ST1). Step 8's session-404 restart reserves both. The `finally` release mirrors YR19 with `reservedUserThisRun` (ST2). `PublicationErrorCodes` gains `UserUploadLimitReached = "user_upload_limit_reached"`. No other job changes: the claim query, `PublicationQueueTime`, `PublicationScheduledForUploadHandler`, `AutopilotConsentJob` and the incident sweeps are untouched (ST8) — a deferred row is simply skipped by the existing `next_attempt_at` predicate (F8).
- **Job registration stays `enabled`-based** (02.8.3-T1:98): narrowing a rollout must not require a Worker restart. The user-scoped gate is **inside** the run (CR-16: one `IsEnabledForAsync("youtube_publish", new FeatureFlagSubject(series.UserId, false), ct)` per series in the autopilot job, skipping series outside the rollout) and on the HTTP create path. In-flight publications are never affected (O6/ST6).
- **09.10 settings integration (CR-17):** `PUT /admin/settings/{integration}` calls `IYouTubeClientRemovalGuard.EnsureRemovableAsync(proposedClientIds, ct)` inside its transaction before persisting `oauth.clients`; the read side is the resolver of UC-1. 09.10 owns versions/rollback and the Data Protection purpose chain (F11) — this slice adds no new purpose.
- **09.9.2/09.9.1 flag integration (CR-14/CR-15):** `IsEnabledForAsync` + `FeatureFlagSubject` are additive; `RequireFeature` uses the scoped overload with the subject from `ClaimsPrincipal` (no DB read — F29); the CLI and the `PATCH` body gain `rules`.
- **Admin queries:** all three live in `Features/Admin/YouTube` (F12) and use `AsNoTracking`; no background job, no cache, no polling (F30/F31).

## 8. Security and privacy

| ASVS L2 area | Measure |
|---|---|
| V4 Access control | Every endpoint in §5 carries the admin/owner policy + MFA (F11) and 404s for a non-admin (F24/ST3); `RequireFeature` unchanged. The quota-limit writes are audited with the actor (`audit_logs.actor_user_id`, `updated_by`). |
| Tenant isolation | Per-user counters are read only from `Features/Admin` (the sole allow-listed `IgnoreQueryFilters` site, F12); no handler in this slice reads another user's `publications`, episodes or channels outside that folder; the DTOs expose counts + `display_name` only (no titles, ids, addresses, tokens — F30). |
| V6 Secrets | `system_settings.value jsonb` for `youtube` holds no secret; `clientSecretKey` is a name resolved through the P secret path (F16/F27). A test scans the written `value jsonb` and the log sink for secret-shaped values. The resolver returns `oauth_client_unknown` instead of leaking which client ids exist. |
| V7 Error/logging | New codes only (catalogue CR-17); no exception carries a secret; `oauth_client_in_use` echoes the client id and a count (already visible to admins). Log redaction is 09.9.3-T2's. |
| V11/V13 Business logic | The cap is enforced by a single conditional SQL statement (no read-then-write race, F23); the removal guard runs inside the settings transaction (F26); the rollout evaluator is pure and fails safe to the P answer (ST4). |
| Abuse/DoS | The admin routes are low-traffic and read on demand; the override writes use the existing `paid` rate policy; the fairness deferral is bounded (`next_attempt_at` + jitter) so no retry storm; no new endpoint is anonymous. |
| Privacy | No new personal data: `display_name` was already admin-visible (00 §8 `GET /admin/users`); counters are operational, retention note ST7 (90 days, handed to 11). |
| i18n/a11y | Server-side: nothing to localize (codes + ISO-8601). UI: 10 S slice (CR-19). |

## 9. Test plan

**Unit (TestKit, `FakeClock`).** (a) `RolloutRuleEvaluator`: each `rollout` value; `enabled = false` wins; null/unknown/`{}`/`"bogus"` → the `enabled` answer; a null subject is false for `admins`/`list`/`percent` and true for `all`; `Bucket` is stable across calls and across process restarts, and its distribution over 10 000 ids is within ±2 pp of `percentage`; `percentage 0`/`100` edges. (b) `PacificQuotaDay` is not re-tested (P). (c) `SettingsOAuthClientResolver`: fallback path (no row), partial map, unknown id, unresolvable secret name. (d) `IYouTubeClientRemovalGuard` decision table.

**Integration (Testcontainers Postgres, the P harness `09.2.1-T2`).** (a) Two parallel `TryReserveAsync` with limit 2 → exactly 2 successes + 1 `user_upload_limit_reached` and `uploads_used = 2`. (b) Override takes effect on the next statement; deleting it restores the option. (c) Reserve/release parity: after the resume, 404-restart and failed-session-create sequences, `platform_user_quota_usage.units_used` equals the units that reached the fake Google server and `platform_quota_usage.units_used` matches it. (d) `GetAdminQuotaQuery`: three SQL commands (command counter), top-20 order, `Overridden`/`EarliestScheduledAt` correctness, no N+1. (e) Migration applies to an empty DB and to a DB with existing connections; the FK to `users` behaves.

**API tests (TestKit web host).** 403 for a `user` token on all four admin routes; 404 `feature_disabled` when the flag is off; 400 on `uploadsPerDay = 0`/`101` and a 2-char reason; 204 + audit row on `PUT`; 200 DTO shape snapshot (field names camelCase); `PATCH /admin/feature-flags/youtube_publish` with `rules` round-trips and a user outside the rollout gets 404 on `GET /channels`.

**Not tested (deliberately).** Framework plumbing (EF mapping of matching columns, DI registration), the P `IQuotaLedger` behaviour, real Google calls (staging gate §B7), Hangfire scheduling (P covers it).

**Staging gate:** the four checks of `../README.md` §B7 (`06-saas`), including the secret-shape scan and the two-Worker fairness run.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 06.1.2-T3 | XS (0.25) | BE + Security | S · M2 | 06.1.2-T2, 09.10.1-T1, 09.2.3-T2 (soft) | 06.5.2-T1, 10.9.3-T1 |
| 06.3.2-T1 | S (0.5) | BE | S · M2 | 06.3.1-T1, 06.2.3-T2, 09.9.1-T1 (soft) | 06.3.3-T1 |
| 06.3.3-T1 | XS (0.25) | BE | S · M2 | 06.3.2-T1, 06.3.1-T1, 09.9.1-T1 (soft) | 10 S admin UI (id pending; 06 CR-19), 10.9.1-T2 (10/08 CR-hand-off) |
| 06.5.2-T1 | XS (0.25) | BE | S · M2 · flag `youtube_publish` | 09.9.2-T2, 09.9.1-T1 (soft), 06.1.2-T3, 09.9.2-T1 (09 W3 CR-15), 02.8.3-T1 | 10.7.3-T1 (10 W3 CR-hand-off) |

Raw **1.25 d** = `../README.md` §B8.1 = §B9 = `00-overview.md` §4.1 S column for 06 (1.25) — no CR. The tightness of 06.3.2-T1 (0.5 d for two tables + port + override routes + the CR-13 call site) is logged in `../README.md` §B10 ST7; the fallback CR would be `06.3.2-T1 S → M (1.0)` plus `00 §4.1` S 1.25 → 1.75 and the matching lines in `06-youtube-posting.md` §11 and `00 §9`.

## 11. Rollout

1. **Prerequisites:** the P slice live (connections, publications, `IQuotaLedger`, ETA); `09.10.1-T1` and `09.9.1-T1` deployed; CR-13/CR-13b applied with 06.2.3-T2's implementation.
2. **Deploy order:** migration `_06_UserQuotaUsage` (expand-only, safe before the code) → Api (the four admin routes + the resolver/guard registration) → Worker (CR-13 call site, CR-16 guard) → switch `youtube` `system_settings` rows (`oauth.clients`, `oauth.active_client_id`) → last, the rollout `rules`.
3. **Flag state:** `youtube_publish` stays `off` in every environment except staging until the gate below is green; with the flag off all routes of this slice answer 404 `feature_disabled` (ST4), so deploying the code is inert.
4. **Gate `06-saas`** (`../README.md` §B7): clients (second client id, removal refused while pinned, no secret in `value jsonb`), fairness (cap 2, two Worker instances, deferral to the next PT midnight, override lifts it, counters match the uploads that reached Google), admin view (numbers match SQL, non-admin 404, no content fields), rollout (`admins` → user 404 / admin 200, stable `percent` across cache windows, `bogus` → 400, in-flight rows unaffected).
5. **Rollout chain (S5):** `off` → `admins` → `list` (the 10 beta user ids) → `percent 50` → `all`. Each step is a `PATCH` (or the CLI) plus a look at `feature_flag_evaluations_total` [INFERENCE] and the `06.3.3-T1` admin view; a step is reverted by a single `PATCH`. Confirm before widening: no `user_upload_limit_reached` spike, no `oauth_client_in_use` surprise, queue length stable.
6. **Monitoring:** `youtube_quota_used` (P) unchanged; add `youtube_user_quota_deferred_total` and `feature_flag_evaluations_total{key,result}` [INFERENCE]; the admin quota screen is the human view (no alert of its own — the P `> 80 %` quota alert still applies and the per-user cap is not an alert condition in M2).
7. **Rollback:** rollout → `off` or back one step (no deploy); fairness → `PUT` `uploadsPerDay: 100` per affected user or `YouTube:Quota:UploadsPerUserPerDay = 100` (option change + restart) — the tables stay, no data unwind (forward-only migration); settings → repoint `oauth.active_client_id` at the previous client id (connections pinned to the removed one would fail with `invalid_client` → incident, which is why the guard exists, F26). No route of this slice can corrupt P data: it only inserts two counter rows and audit rows.

**Open items raised by this plan.** (1) The 09.10.1-T1 call site for the removal guard (CR-17) must be applied before the settings swap step; (2) CR-14/CR-15 must be in 09.9.2-T2 before the first `PATCH` with `rules`; (3) the metric names marked [INFERENCE] are confirmed with 09.9.3-T1's holder during 06.3.2-T1 (they are additive).
