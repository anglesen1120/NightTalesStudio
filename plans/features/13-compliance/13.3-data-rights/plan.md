# 13.3 — Data rights workflows (S slice): DSAR export, account deletion, public report/triage endpoints

> Feature folder of `plans/features/13-compliance/` (Part B, S slice). Contract: `../README.md` §B0 (folder brief), §B3 D6–D18, `### B8.1` (canonical rows, copied verbatim below), §B10 (owner decisions O3–O9, TL rulings T1–T10). High-level: `plans/13-compliance.md` §3 (FR-13-3/4/5), §4 (Epic 13.3), §6 (task breakdown). P-signed context: `../13.3.1-deletion-engine/` (D1–D5, YR7/YR12/YR13) — **not reopened** (§B10 T7).
> Tasks: **13.3.2-T1, 13.3.2-T2, 13.3.3-T1, 13.3.4-T1** — 4 tasks, **2.75 d raw / 3.3 buffered** (epic 13.3 = 1.0 P + 2.75 S = 3.75, unchanged per §B9).

## 1. Scope

**In scope (4 tasks).**

| Task | Delivers |
|---|---|
| 13.3.2-T1 | `AccountDeletionScopeHandler` (`account` scope of the P engine), `POST /me/delete` (password re-auth + `Idempotency-Key`), `GET /me/deletion-requests`, the handler/DTO behind 09.9.1-T1's `GET /admin/deletion-requests?status=` row, per-table batched deletes, anonymisation + pseudonymised keep-list, `DataDeletion:DeferredScopes` loses `account` |
| 13.3.2-T2 | Backup-window proof: retention config = 30 days, restore re-apply of completed deletions, confirmation e-mail + privacy-policy wording equal to the config |
| 13.3.3-T1 | DSAR export: `POST /me/export`, `GET /me/exports/{id}`, allow-listed archive `exports/{userId}/{exportId}.zip`, `ExportRequestedHandler`, `DataExportJob`, `ExportCleanupJob` (7 days), `dsar_export_failed_total` |
| 13.3.4-T1 | Public `POST /reports` (CAPTCHA + `anon` policy, always 202), `report_received.v1`, the handlers/DTOs behind 09.9.1-T1's `GET /admin/reports` + `PATCH /admin/reports/{id}` rows, the `reports` table (CR-7 columns + index), `/vi/report` + `/en/report` page content and vi+en copy |

**Out of scope / owned elsewhere (do not implement here).** The deletion engine itself (`data_deletion_requests`, `IDeletionRequests`, `IDeletionScopeHandler`, `DataDeletionJob`, `deletions export|reapply`) is P, `../13.3.1-deletion-engine/` (K7/K12). The admin **screens** (moderation, quota, reports resolve, deletion requests, 18+) are 10.9.1-T1/T2's (§B0, §B10 T10) — 13 owns the endpoints, the audit rows and the i18n keys only. CAPTCHA **verification** is 09.3.3-T2's; the `anon` rate policy is 09.3.5-T1's (§B10 T4, CR-10) — this folder adds **no** limiter. Suspensions and other user actions stay in 09.9.1 / 08.2 (D11). `POST /admin/users/{id}/delete` (09.10.5-T1) starts the **same** command and row as `POST /me/delete` (§B10 T8, CR-18) — it is not written here. Exports/deletion for a **channel** scope stay with 06 (06.4.1-T1 / 06.4.3-T1).

**Gates this folder must be able to satisfy** (§B7 `13-S-rights`): synthetic user export → allow-list archive, no token/secret/other-tenant row, links ≤ 15 min, object gone after 7 days on a fake clock; account deletion → all rows/objects gone before `due_at`, `users.status = 'deleted'`, ledger rows pseudonymised, `GET /me/deletion-requests` shows `completed`; restore drill re-applies (§B7). `13-S-trust` covers this folder's report form: 202 for a bogus target id, exactly one owner notification, admin resolve with a note.

## 2. Confirmed facts

| Id | Fact (verbatim contract) | Status / source |
|---|---|---|
| `data_deletion_requests` = `user_id, scope (account/channel/episode), target_id (partial unique (user_id, scope, target_id, reason) NULLS NOT DISTINCT while open, no FK to users), reason (user_request/in_app_revoke/google_revoke/account_delete), requested_at, due_at, completed_at`; 7-day / 30-day deadlines | ✅ | `00-overview.md:306`, K8; P engine adds `status/claimed_at/attempts/last_error_code/last_error_at/reapplied_at` (`13.3.1-T1` CR-3) |
| `reports(reporter_email, target_type, target_id, reason, status, resolved_at)` — "Takedown/abuse reports (13)"; **no** `created_at`/`ip_hash`/`handled_by`/`resolution_note`, **no** index | ✅ contract, ⚠️ insufficient for the 72 h target | `00-overview.md:324`; F8; 13 CR-7 |
| Routes already contracted: `POST /me/export`, `GET /me/exports/{id}` (*fresh short-lived media links*), `POST /me/delete`, `POST /reports` (*public, rate-limited*), `GET /admin/reports` | ✅ | `00-overview.md:360,366,367`, K9 |
| `POST /reports` has **no** in-process rate-limit policy: BD14 defines only `auth`/`paid`/`hub`; 09.3.4 explicitly flags anonymous traffic outside `/auth/*` naming `POST /reports` | ✅ | 09 README BD14:698; `09.3.4-me-limits-audit/plan.md:224`, K10 |
| `/report` is a public prerendered page with CAPTCHA + rate limit; prerender makes **no** API call; the widget is 10's `CaptchaWidget` (`features/public`, `public/index.ts`) | ✅ (route renamed by 10 CR-24 → `/vi/report` + `/en/report`, F18) | `10-web-ui.md:310`, 10 README D12, K11 |
| The S owner surfaces are named but unwritten: `POST /me/delete`, `GET /me/deletion-requests`, admin retry; 13.3.1-T1 ships **no** route in P | ✅ | K12; 13 CR-8 adds `GET /me/deletion-requests`, `GET /admin/deletion-requests?status=`, `PATCH /admin/reports/{id}` |
| Reusable S blocks: `ISystemWorkQuery.PendingAsync<T>` (pinned, one signed member, never extended) + `ISystemContext.For(userId)`; `IBlobStore` (+`ListKeysAsync`, Part A CR-6), bucket `nt-media-<env>`; operator verbs `nts-worker <noun> <verb>`; metrics `deletion_requests_open`, `deletion_request_overdue_total`; audit `account.data_deleted` | ✅ | K18; `09.2.3-T1:36`, 09 README BD10/BD13, `06.4.3-T1:13` |
| Reused job/e-mail/audit plumbing: `[JobPolicy(Queue = JobQueue.Default, Retries = n)]` (default queue `default`, 0 retries when absent), `IRecurringJobRegistration` at Worker start, ids-only job parameters, `IEmailSender` + outbox `EmailRequested` (09.2.6-T3), `[Audited]`/`IAuditWriter` (`AuditEntry(...)`, `ActorLabel = "system:operator"` for operator/system writes) | ✅ | 09 README:284, `09.2.6-T1`, `09.3.6-T1:31-34`, `09.2.6-T3:75`, §B10 T9 |
| Every command needs exactly one of `[Audited]/[NotAudited]/[AllowAnonymousRequest]` and every new command/query gets one task file triple; handlers ≤ ~80 lines; the design-review gate is 09.3.6-T1's arch test | ✅ | 09 README:1233, §A5.4 |
| `deletion_requests` engine facts reused by T1: `IDeletionRequests.CreateAsync(userId, scope, targetId, reason, ct) → ErrorOr<DeletionRequest>`; `IDeletionScopeHandler { DeletionScope Scope; Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest, ct) }`; `DataDeletionJob` hourly (id `data_deletion`, cron `0 * * * *`, batch ≤ 20, per-row `IServiceContext.For`), config `DataDeletion:DeferredScopes` (`["account"]` in P), registry validator fails Worker start for a non-deferred scope with no handler | ✅ | `13.3.1-T1:59-75, 101-103, 120`; `13.3.1-deletion-engine/plan.md:12-17` |
| Report e-mail goes to the owner (`report-to` setting); no automatic content change on a report | ✅ | D11 |
| Account-deletion ledger rule (payments + credit rows kept, `user_id` replaced by a pseudonym, amounts/dates retained for accounting) is **12's rule** and is ⚠️ pending the accountant's confirmation | ⚠️ | CR-14, D10; 13.4.1/12 §5.7 |
| 18+ / children duties and the PDPL obligation list stay ⚠️ until the lawyer memo (13.6.1-T1, Law 91/2025/QH15 + Decree 356/2025/ND-CP, D16) | ⚠️ | K16, D16 |
| `data_export_requests(id, user_id, status, requested_at, claimed_at, completed_at, expires_at, deleted_at, storage_key, error_code, attempts, last_attempt_at)`, `IUserOwned`, indexes `(user_id, requested_at DESC)` / `(status, expires_at)`; `GET /me/exports/{id}` response `{ status, requestedAt, expiresAt, downloadUrl, mediaLinks[], errorCode }` | ✅ (applied) | README §B4 CR-19 |

## 3. Domain and data

**Domain** (`src/NightTales.Domain/Compliance/`).

```csharp
// Reports (new; not IUserOwned — a report is public input, so no tenant filter exists on it)
public enum ReportTargetType { Episode, User, Publication }          // wire: episode|user|publication
public enum ReportReason { Copyright, Privacy, Defamation, Abuse, Other }
public enum ReportStatus { Open, InReview, Resolved, Rejected }

public sealed class Report : AggregateRoot
{
    public string? ReporterEmail { get; private set; }
    public ReportTargetType TargetType { get; private set; }
    public Guid TargetId { get; private set; }
    public ReportReason Reason { get; private set; }
    public string? Details { get; private set; }
    public string Locale { get; private set; }                        // vi|en, copy only
    public ReportStatus Status { get; private set; }
    public byte[]? IpHash { get; private set; }                       // sha256, never the raw address
    public Guid? HandledBy { get; private set; }
    public string? ResolutionNote { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static Report Create(ReportTargetType type, Guid targetId, ReportReason reason,
        string? details, string? reporterEmail, string locale, byte[] ipHash, DateTimeOffset now); // target existence is NEVER checked
    public ErrorOr<Success> Resolve(ReportStatus to, Guid handledBy, string? note, DateTimeOffset now); // Open|InReview -> Resolved|Rejected only
}

// Export request (new; IUserOwned -> tenant filter + system scopes, D9)
public enum ExportStatus { Queued, Running, Ready, Failed, Expired }
public sealed class ExportRequest : AggregateRoot, IUserOwned
{
    public Guid UserId { get; }
    public ExportStatus Status { get; private set; }
    public string ObjectKey { get; private set; }        // exports/{userId}/{exportId}.zip
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }// CreatedAt + ExportRetentionDays (7)
    public DateTimeOffset? ClaimedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }  // set by ExportCleanupJob / account deletion
}
```

**Tables.** `data_deletion_requests` (P, `13.3.1-T1`, unchanged), `user_consents` / `audit_logs` / `users` (09), `reports` (**created by 13.3.4-T1**, CR-16), `data_export_requests` (**created by 13.3.3-T1**, CR-19, applied). Both migrations follow 09.2.2-T1 (snake_case, unqualified names, expand-only).

```sql
-- 13.3.4-T1, migration <yyyyMMddHHmm>_13_Reports.cs  (00 §6:324 + CR-7)
CREATE TABLE reports (
  id uuid PRIMARY KEY, reporter_email text NULL, target_type text NOT NULL, target_id uuid NOT NULL,
  reason text NOT NULL, details text NULL, locale text NOT NULL, status text NOT NULL DEFAULT 'open',
  ip_hash bytea NULL, handled_by uuid NULL, resolution_note text NULL,
  created_at timestamptz NOT NULL DEFAULT now(), resolved_at timestamptz NULL);
-- CHECK: target_type in (episode,user,publication); reason in (copyright,privacy,defamation,abuse,other);
--        status in (open,in_review,resolved,rejected)
CREATE INDEX ix_reports_status_created_at ON reports (status, created_at);         -- CR-7, drives the admin list + the 72 h measure

-- 13.3.3-T1, migration <yyyyMMddHHmm>_13_DataExportRequests.cs  (CR-19, applied — exact column/index list)
CREATE TABLE data_export_requests (
  id uuid PRIMARY KEY, user_id uuid NOT NULL, status text NOT NULL DEFAULT 'queued',
  requested_at timestamptz NOT NULL, claimed_at timestamptz NULL, completed_at timestamptz NULL,
  expires_at timestamptz NOT NULL, deleted_at timestamptz NULL, storage_key text NOT NULL,
  error_code text NULL, attempts int NOT NULL DEFAULT 0, last_attempt_at timestamptz NULL);
CREATE INDEX ix_data_export_requests_user ON data_export_requests (user_id, requested_at DESC);   -- CR-19
CREATE INDEX ix_data_export_requests_expiry ON data_export_requests (status, expires_at);          -- CR-19: ExportCleanupJob + lease sweep
-- ≤ 1 export per user per 24 h (D9/T3) is enforced by RequestExportCommandHandler's per-user advisory lock + read-then-insert inside one transaction, not a partial unique index (CR-19 names no such index; the lock is what makes the check race-safe — 13 CR-32).
```

**Per-table batched delete** (13.3.2-T1; D10: ≤ 5 000 rows per batch, set-based, never EF row loading). The handler walks a fixed table order and loops each table until it returns 0:

```sql
DELETE FROM episodes WHERE id IN (
  SELECT id FROM episodes WHERE user_id = @id ORDER BY id LIMIT 5000 FOR UPDATE SKIP LOCKED
);
```
Rules: one transaction per (table, batch); every statement carries `user_id = @id`; a table already empty is a no-op; a batch failure sets `status='failed'`, `last_error_code`, `attempts = attempts + 1` and the hourly `DataDeletionJob` retries (`MaxAttempts = 5`). `usage_records` **must survive** (13 §5.4, `13.3.1-T1:82`).

**Anonymisation + keep-list** (D10, T2/T21/T22). `users.status`: `active → suspended` (on request) `→ deleted` (on completion); no new status value, no new column. Keep: `payments` + the credit ledger get a **random pseudonym UUID** in `user_id` (T21 — no mapping retained, not joinable to `audit_logs`; amounts/dates/order codes stay for accounting, 12's rule → CR-14 ⚠️ accountant on the keep-window only), `audit_logs` (subject/actor id kept, `data` redacted of e-mail/IP), `data_deletion_requests` (evidence, no FK to `users`), and `user_consents` (T22 — kept as-is: `document`/`version`/`accepted_at`/`ip`, no contact data; **not** deleted with the account). Identity needs a unique address: `email → deleted+<first16 sha256(userId)>@invalid` (+ the normalized column, ⚠️ name confirmed against 09.3.1-T1 at implementation), `display_name → deleted user`, MFA secrets/recovery codes removed.

**Backups** (13.3.2-T2). Deleted data is not removed from existing backups but ages out inside the retention window ([DECISION] 30 days, 11.6.1-T1); a restore re-applies every `data_deletion_requests` row completed after the backup instant **before** the service reopens (P engine: `deletions export --since` / `deletions reapply --since`).

## 4. Use cases

| UC | Given / When / Then |
|---|---|
| UC-1 Export requested | **Given** an authenticated user with no export in the last 24 h, **When** `POST /me/export` with `Idempotency-Key`, **Then** one `data_export_requests` row (`queued`, `expires_at = requested_at + 7 d`), one outbox `export_requested.v1`, 202 `{ exportId }` — and the API has enqueued nothing itself (the Worker handler schedules `DataExportJob`) |
| UC-2 Export built | **Given** a queued export, **When** `DataExportJob` runs, **Then** the nine members are streamed table by table (keyset, `AsNoTracking`) into a temp file, `IBlobStore.PutAsync("nt-media-<env>", "exports/{userId}/{exportId}.zip")`, status `ready`, and the ready e-mail is queued **without** a link |
| UC-3 Export denied twice | **Given** an export `ready` for that user, **When** the user calls `POST /me/export` again inside 24 h, **Then** 429 `rate_limited` with `Retry-After` = seconds until the first export is 24 h old, and **no** new row |
| UC-4 Export downloaded | **Given** a `ready` export, **When** `GET /me/exports/{id}`, **Then** `{ status: "ready", requestedAt, expiresAt, downloadUrl, mediaLinks[], errorCode: null }`, both `downloadUrl` and every `mediaLinks[].url` are pre-signed with TTL ≤ 15 min and re-minted on each call; after the cleanup job or an account deletion → `expired`, no URL, empty `mediaLinks` |
| UC-5 Account deletion requested | **Given** a signed-in user re-authenticating, **When** `POST /me/delete`, **Then** every refresh family and trusted device revoked, one `data_deletion_requests (account, target_id NULL, account_delete)` row with `due_at = requested_at + 7 d`, `users.status = 'suspended'`, 202 `{ requestId, dueAt }`; the second call with the same `Idempotency-Key` returns the same body, and a new key while the row is open returns the **same** open row (T2 + YR13 upsert) |
| UC-6 Account deletion executed | **Given** an open account-scope row that is due, **When** `DataDeletionJob` claims it, **Then** `AccountDeletionScopeHandler` deletes every child table in batches, the media/research/export prefixes, anonymises `users`, sets `completed_at`, writes audit `account.data_deleted` and updates `deletion_requests_open` / `deletion_request_overdue_total` |
| UC-7 Report filed | **Given** a visitor on `/vi/report` with a solved CAPTCHA inside the `anon` limit, **When** `POST /reports` with **any** well-formed `targetId` (including a random uuid), **Then** 202 `{ reportId }`, exactly one `reports` row, one `report_received.v1` and exactly one owner e-mail; a malformed uuid/enum is 400 `report_target_invalid` and no row |
| UC-8 Report triaged | **Given** an admin/owner, **When** `GET /admin/reports?status=open` then `PATCH /admin/reports/{id}` `{ status: "resolved", resolutionNote }`, **Then** keyset list ≤ 100/page, `resolved_at` + `handled_by` set, audit `abuse.report_resolved`, and the oldest open report age is visible for the monthly SLA report |

## 5. API and events

All routes are the versioned API group (`/api/v1/...`, 09.1.1-T1); the paths below are the contract spellings from `00-overview.md` §8.

| Method / route | Task | Request | Success | Errors |
|---|---|---|---|---|
| `POST /me/export` | 13.3.3-T1 | `Idempotency-Key` (required) | 202 `{ exportId }` | 400 `idempotency_key_required`, 400 `validation_failed`, 401, 429 `rate_limited` + `Retry-After`, 404 `not_found` (other user's export id on the status route) |
| `GET /me/exports/{id}` | 13.3.3-T1 | — | 200 `{ status, requestedAt, expiresAt, downloadUrl, mediaLinks[], errorCode }` (CR-19/T12) | 401, 404 `not_found` (never 403 — no cross-tenant oracle) |
| `POST /me/delete` | 13.3.2-T1 | body `{ currentPassword }` + `Idempotency-Key` (required) | 202 `{ requestId, dueAt }` | 401 `reauth_required` (stale step-up window, 09.10.4-T1) / 401 `invalid_credentials`, 400 `idempotency_key_required`, 429 `rate_limited` |
| `GET /me/deletion-requests` | 13.3.2-T1 | optional `?status=` | 200 `[{ scope, reason, requestedAt, dueAt, completedAt, status }]` (own rows, cursor) | 401 |
| `GET /admin/deletion-requests?status=open\|overdue` | 13.3.2-T1 | keyset (`?status=&cursor=`) | 200 `AdminPageDto<AdminDeletionRequestDto>{ items, nextCursor }` — `{ id, userId, scope, requestedAt, dueAt, status, attempts }`, read-only | 404 `not_found` (unauthenticated/wrong role — `AdminPolicy` semantics), 400 `validation_failed` |
| `POST /reports` | 13.3.4-T1 | `{ reporterEmail?, targetType, targetId, reason, details?, locale, captchaToken }` (anonymous) | **202 `{ reportId }` for every well-formed request, whatever the target resolves to** (T1) | 400 `report_target_invalid` (malformed uuid/enum), 400 `validation_failed`, 400 CAPTCHA rejection from 09.3.3-T2 (⚠️ exact code confirmed there; `captcha_required` is the 10 README spelling), 429 `rate_limited` (09.3.5-T1 `anon` policy) — **never** 404/409/403 about the target |
| `GET /admin/reports?status=open\|in_review\|resolved\|rejected` | 13.3.4-T1 | keyset (`?status=&cursor=`), ≤ 100/page, one projection query | 200 `AdminPageDto<AdminReportDto>{ items, nextCursor }` (`reporter_email` masked `a***@domain`) | 404 `not_found` (unauthenticated/wrong role), 400 `validation_failed` |
| `PATCH /admin/reports/{id}` | 13.3.4-T1 | `{ status, note }` (09.9.1-T1's row spelling; 13 D11 writes `resolutionNote` — see the gap note below) | 200 updated row | 400 `validation_failed`, 404 `not_found`, 409 `report_invalid_state` [INFERENCE — code name; the transition Open\|InReview → Resolved\|Rejected is the rule] |

**Admin-route ownership (do not restate these routes as 13-owned).** `GET /admin/reports`, `PATCH /admin/reports/{id}` and `GET /admin/deletion-requests` are already rows of the **single** `/api/v1/admin` group registered by **09.9.1-T1** under `AdminPolicy` (`09-backend-api/README.md:1053`; reports/deletion extras = W3 CR-5), and 09 D20 forbids a second `/admin` group or policy (`09-backend-api/README.md:1032`). This folder supplies the **handlers, DTOs, table, engine and audit rows** for them and nothing else: no route group, no policy, no pagination envelope of its own (`AdminPageDto<T>{ items, nextCursor }`, cursor `?status=&cursor=`, ≤ 100), wrong role/unauthenticated → 404 `not_found` (never 403), and the raw reporter address is owner-only while the admin list masks it (`a***@domain`). Divergences to ride on CR-26: 13 README D11/CR-8/CR-13 say "13 owns the endpoints" while `09-backend-api/README.md:1148` records both as "09-owned, read-only"; and the PATCH body field is `note` in 09's signed row vs `resolutionNote` in D11 — the wire name is 09's (the handler maps it onto `reports.resolution_note`).

**Errors** use `ProblemDetails` + the 09.1.4-T1 `ErrorCatalogue`; new codes are CR-10's (`age_attestation_required` 400, `legal_content_missing` 409, `report_target_invalid` 400) plus `deletion_invalid_state` if a transition is refused [INFERENCE]. No code that reveals target existence is ever emitted (T1).

**Outbox events** — `report_received.v1 { reportId }` (D11 ✅) and `export_requested.v1 { exportId, userId }` ([INFERENCE] name, follows `render_requested.v1`; registered with CR-19). `POST /me/delete` writes **no** event: the response is the row itself and the hourly `DataDeletionJob` claims it (P contract).

**Config keys** (`appsettings` + options validation): `Compliance:Exports:MaxPerDay` (1), `ExportRetentionDays` (7), `DownloadUrlTtlMinutes` (15), `MaxConcurrentExports` (2), `CleanupBatchSize` (50), `Compliance:Reports:NotificationTo` (the `report-to` setting of D11 — ⚠️ confirm whether the 09 settings store or `IConfiguration` holds it at implementation), `DataDeletion:DeferredScopes` (`["account"]` → `[]` after 13.3.2-T1).

**i18n** (D17; key style `scope.area.item`, both languages in the same change): `public.report.*` in `web/src/assets/i18n/public/{vi,en}.json` (the report page — ⚠️ folder name confirmed with 10's `features/public` at implementation), and the copy keys for the status/deletion surfaces 10 renders: `account.export.*`, `account.deletion.*`, `admin.reports.*`, `admin.deletions.*`. Error copy maps the codes above; **no** copy on the report page promises the 72-hour target as an SLA (T9).

## 6. UI

**No UI-design task exists in this folder** — §B8.1 defines exactly four rows and the deliverable set is fixed by the contract, so a `13.3.x-T0 — UI design` row is **not** added here. The owner rule (2026-09-25) is still satisfied because every UI surface of this folder belongs to 10's already-designed slices:

| Surface | Design owner (approved design first) | Implementer |
|---|---|---|
| `/account` export + deletion status (consumes this folder's endpoints) | `10.8.1-T0 UI design (billing + account)` | `10.8.2-T1` (10 CR-27: `Depends on` gains 13.3.3-T1 + 13.3.2-T1) |
| Admin reports resolve + deletion requests | `10.9.1-T0 UI design (admin)` | `10.9.1-T2` |
| Public `/vi/report` + `/en/report` page + `CaptchaWidget` host | `10.3.1-T0 UI design (public marketing + auth)` (10 CR-24) | 13.3.4-T1 authors the page content + vi+en copy; the mount is 10's (CR-27 pins the widget to 10.3.3-T2) |

**Consequence for the FE part of 13.3.4-T1** (hard gate, owner rule): the page work may not start before `10.3.1-T0`'s design is approved; §B8.1's canonical cell already names both the design gate (`10.3.1-T0`) and the widget (`10.3.3-T2`, soft). The `/vi/report` + `/en/report` route fix is 10 CR-24 (not duplicated here, F18).

This folder's UI contributions are therefore content and contracts, not components: the `reason` list is generated from the Content Policy's prohibited/restricted sections (13.1.3-T1, D11); a11y per D17 (WCAG 2.1 AA for the report form: label/`aria-describedby` on every field, a real `<form>` with a submit button, focus moves to the error summary, CAPTCHA exposed with an accessible fallback); the deletion/export status surfaces are read-only and never show a link that outlives its TTL.

## 7. Jobs and integrations

| Job / handler | Registration | Policy | Work |
|---|---|---|---|
| `ExportRequestedHandler` | outbox consumer for `export_requested.v1` | — | opens `ISystemContext.For(userId)` from the event payload (D9: "every subsequent read/write runs under `ISystemContext.For(userId)`"), re-reads the row inside that tenant scope (a cancelled/expired row is a no-op), then `IJobScheduler.Enqueue<DataExportJob>((exportId), ct)` — the Api never enqueues |
| `DataExportJob` | enqueued per export, ids-only `(Guid exportId)` | `[JobPolicy(Queue = JobQueue.Default, Retries = 2)]` | claims the row (`queued/failed → running`), opens one `ISystemContext.For(userId)` scope, streams the allow-list to a temp file, `PutAsync`, `ready`, e-mail; `failed` after `MaxAttempts` (3) + `dsar_export_failed_total` |
| `ExportCleanupJob` | `IRecurringJobRegistration`, id `export_cleanup`, cron `0 3 * * *` | `[JobPolicy(Queue = JobQueue.Default, Retries = 2)]` | ① expiry sweep: objects 7 days after creation (≤ `CleanupBatchSize` = 50 per run) via its own allow-listed cross-user scan site (the `IgnoreQueryFilters` call must live **in this class**, 09.1.2-T1:43); ② lease recovery: the candidate query is the **pinned** `ISystemWorkQuery.PendingAsync<ExportRequest>(predicate, batch, ct)` member (never widened; no `ORDER BY`, no lock hint — `06.2.2-T3:53`, 06 O-9), inside the bounded sweep loop of `06.2.3-T3:83-96` (`MaxBatches` + run-local failed-id exclusion), and each candidate row is then re-read by id inside its own `ISystemContext.For(userId)` scope before re-enqueueing |
| `DataDeletionJob` (P) | unchanged, id `data_deletion`, cron `0 * * * *` | unchanged | picks up the `account` rows now that `AccountDeletionScopeHandler` is registered and `DataDeletion:DeferredScopes` no longer contains `account` |
| `ReportReceivedHandler` | outbox consumer for `report_received.v1` | `[Audited]`-style `IAuditWriter` | one e-mail via `IEmailSender` to `report-to`, audit `abuse.report_received` (`support`, `internal`) |

**Concurrency** (D9: job side ≤ 2 concurrent exports): a Worker-local `SemaphoreSlim(2)` [INFERENCE — the only enforcement that exists without a new dependency; it is correct for the single-Worker MVP and must be revisited if the Worker is scaled out]; when full the job reschedules itself outside the retry budget (the `SubmitRenderJob` precedent).

**Storage** (T5, CR-15): no new bucket — `exports/` under `nt-media-<env>` with a 7-day lifecycle rule; the artifact is deleted by the cleanup job, by the account-scope deletion, and by nothing else. Evidence binaries are not this folder's concern (13.5.1-T1).

## 8. Security and privacy

- **Tenancy.** `ExportRequest` is `IUserOwned` → the global tenant filter applies to every API read; the only bypass sites are the two allow-listed classes (`ExportCleanupJob`, `AccountDeletionScopeHandler`'s per-table SQL) declared per 09.1.2-T1 and the pinned `ISystemWorkQuery` member used by the outbox handler. Every per-row job body opens its own `IServiceScopeFactory` scope with exactly one `ISystemContext.For(userId)` (a second `For`, or one after a `DbContext` was resolved, throws — `09.2.3-T1:24,68`).
- **`reports` has no tenant.** It is public input, so it carries no `user_id` and no filter. The admin routes (`GET /admin/reports`, `PATCH /admin/reports/{id}`, `GET /admin/deletion-requests`) are rows of the single `/api/v1/admin` group registered by **09.9.1-T1** under `AdminPolicy` (`09-backend-api/README.md:1053`; no second group or policy — 09 D20:1032); an unauthenticated caller or a wrong role gets **404 `not_found`**, never 403, and the reports list masks `reporter_email` (`a***@domain`) with the raw address and `ip_hash` owner-only. `data_deletion_requests` is read-only from that group. A report row is never returned to the reporter beyond `reportId`.
- **No existence oracle** (T1): the target is never resolved, never joined and never echoed; a bogus well-formed uuid is 202. `ip_hash` = sha256 of the **resolved** client address (after `ForwardedHeaders`, 09.3.5-T1's rule) with the platform hash salt — never the raw address, never logged; `reporter_email` is never logged and never returned by any endpoint.
- **Secrets/PII.** The export archive keeps **only** the allow-list: `profile.json`, `series-episodes.json`, `story-versions.json`, `publications.json`, `consents.json`, `audit-activity.json`, `usage-ledger.json`, `media.json`, `README.txt`. Never included (D9): tokens/refresh families, `system_settings`/secrets, `data_protection_keys`, `platform_payload` raw bodies, other tenants' rows, staff-only audit rows (`visibility = internal`). Logs carry ids, counts and codes only (09 BD16); the archive is written to a temp file and uploaded — never buffered whole in memory.
- **Idempotency & step-up.** `Idempotency-Key` on `POST /me/export` and `POST /me/delete` (09.1.3-T2 `IIdempotentCommand`); missing key → 400 `idempotency_key_required`; delete additionally requires password re-auth through the 09.10.4-T1 step-up window (401 `reauth_required` when stale) — no forced re-login, tokens stay valid (D7's principle).
- **Public path.** CAPTCHA (09.3.3-T2's verification) + the `anon` rate policy (09.3.5-T1, 60 req/min per resolved IP, CR-10); this folder registers **no** limiter of its own (T4) and cannot be used to enumerate users: the form answers identically for valid and unknown targets, and `reason`/`locale` are enum/`vi|en` only.
- **Audit.** `abuse.report_received`, `abuse.report_resolved` (D11), `account.data_deleted` (P, `support`/`internal`), plus `account.data_exported` for the export command [INFERENCE — D9 names no export audit row; every command needs one audit attribute under the 09.3.6-T1 arch test, so the task pins `[Audited]` with this action, category `support`, visibility `user`, and reports the naming gap]. Operator/system writes use `ActorLabel = "system:operator"`; `audit_logs` rows outlive a deleted account (no FK to `users`).
- **Data Protection.** Purposes unchanged (09.3.1-T1); nothing new is protected here; the archive is a transient copy of already-stored data and is deleted on schedule (7 days) and immediately by account deletion.
- **Deletion ordering.** Re-auth → revoke sessions/devices → suspend → (hourly) delete → anonymise → `deleted`. A suspended user cannot sign in, so a half-deleted account is never usable; completion is idempotent and resumable (T2).

## 9. Test plan

Projects per §A5.4/09: `NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `NightTales.Api.IntegrationTests` (real PostgreSQL 17 via `PostgresFixture`), `NightTales.Architecture.Tests`, plus 10's Vitest/Playwright for the page. Fakes are hand-written in `NightTales.TestKit` (no mocking library): reuse `InMemoryBlobStore`, `FakeClock`, `FakeJobScheduler`, `FakeEmailSender`, `FakeDeletionScopeHandler` (`tests/NightTales.TestKit/Compliance/*`) and add `ExportRequestFactory`, `ReportFactory`.

**Domain.** `ExportRequest_Lifecycle_OnlyValidTransitions`; `Report_Resolve_RejectsOpenToOpen`; `Report_Create_NeverValidatesTargetExistence`.

**Application.** `ExportCommand_SecondCallInside24h_ReturnsRateLimited`; `ExportCommand_MissingIdempotencyKey_Returns400`; `ExportJob_WritesOnlyAllowListedMembers_AndNoTokenOrSecret` (fake `IBlobStore` captures the zip entry names); `ExportJob_IsResumable_AfterTransportFailure`; `ExportCleanupJob_DeletesOnlyExpired_AndAtMostBatchSize`; `DeletionCommand_RevokesAllRefreshFamilies_AndWritesOneRow`; `AccountDeletionHandler_DeletesEveryChildTable_AndKeepsUsageRecords`; `AccountDeletionHandler_AnonymisesEmailToHashAtInvalid`; `AccountDeletionHandler_IsIdempotentAndResumable`; `AccountDeletionHandler_PseudonymisesLedger_KeepsAmountsAndDates`; `ReportCommand_UnknownTarget_Still202_AndExactlyOneEmail`; `ReportAdmin_PatchResolve_WritesNoteAndAudit`.

**Api.IntegrationTests (`PostgresFixture`).** `MeExportEndpointsTests` (202 + `Location`, missing key 400, 429 with `Retry-After` on the second call, `GET /me/exports/{id}` of another user → 404, `downloadUrl`/`mediaLinks[]` TTL ≤ 15 min and re-minted on repeat call, status `expired` after cleanup); `MeDeleteEndpointsTests` (stale step-up → 401 `reauth_required`, 202 body `{ requestId, dueAt }` twice for one key, `users.status = 'suspended'`, `GET /me/deletion-requests` shape, `GET /admin/deletion-requests?status=overdue` requires the admin policy); `ReportsEndpointsTests` (anonymous + CAPTCHA fake → 202 with a random uuid target, malformed target → 400 `report_target_invalid`, 429 through the shared `anon` limiter, exactly one `reports` row, `GET /admin/reports` keyset ≤ 100, `PATCH` sets `resolved_at`/`handled_by`, audit rows written, **no** response field echoes the target); `ReportRateLimitTests` (60 requests then 429, `anon:<iphash>` partition).

**Architecture.Tests.** Each new command has exactly one of `[Audited]/[NotAudited]/[AllowAnonymousRequest]` (09.3.6-T1 test extended to `13.3.x`); job arguments are ids-only; `AccountDeletionScopeHandler`, `DataExportJob` and `ExportCleanupJob` are the only `IgnoreQueryFilters` sites of this folder (single site per outer declaring type, 09.1.2-T1:43); a registry without an `account` handler fails Worker start (reuses the P validator test).

**Page/e2e (10's harness, for 13.3.4-T1).** Vi + en render, CAPTCHA present, `reason` list matches the content policy, a11y axe pass, and the `/vi/report` + `/en/report` routes prerender without an API call (K11).

## 10. Tasks and re-estimate

| Task | Title | Size | Area | Phase | Depends on | Blocks | File |
|---|---|---|---|---|---|---|---|
| 13.3.2-T1 | Account deletion: `account` scope handler, `POST /me/delete`, deletion-request surfaces | M (1.0) | BE | S · M2 | 13.3.1-T1, 13.1.2-T2 (soft), 06.4.1-T1, 06.2.6-T1, 12 CR-14 rule (soft) | 13.3.2-T2, 13.5.1-T1, 10.8.2-T1, 09.10.5-T1 (soft), 10.9.1-T1, 10.9.1-T2 (10/08 CR-hand-off) | `13.3.2-T1-account-deletion.md` |
| 13.3.2-T2 | Backup-window check: retention config = 30 days, restore runbook re-applies completed deletions, e-mail and policy text match | XS (0.25) | BE/Ops | S · M2 | 13.3.2-T1, 11.6.1-T1 | — | `13.3.2-T2-backup-window-check.md` |
| 13.3.3-T1 | DSAR export job: allow-listed archive, status endpoint, cleanup job | M (1.0) | BE | S · M2 | 09.2.3-T1, 09.7.2-T1 | 10.8.2-T1, 13.5.1-T1 | `13.3.3-T1-dsar-export.md` |
| 13.3.4-T1 | Report form + admin triage endpoints (`reports` table, `POST /reports`, admin list/resolve) | S (0.5) | BE/FE | S · M2 | 10.3.1-T0, 10.3.3-T2 (soft), 13.1.3-T1, 09.3.5-T1 (soft), 09.3.3-T2 (soft), 09.9.1 (soft) | 10.9.1-T1, 10.9.1-T2, 13.5.1-T1 | `13.3.4-T1-report-endpoints.md` |

Canonical rows equal `../README.md` `### B8.1` ✅. **Re-estimate:** every row is unchanged against `plans/13-compliance.md` §6 (13.3.2-T1 M, 13.3.2-T2 XS, 13.3.3-T1 M, 13.3.4-T1 S — §B9 "unchanged" for all four). Folder total **2.75 d raw / 3.3 buffered** (S slice 8.5 / 10.2 unchanged; epic 13.3 stays 3.75 = 1.0 P + 2.75 S). No new estimate is requested.

**Execution order inside the folder.** `13.3.2-T1` and `13.3.3-T1` are independent (different tables, different jobs) and can run in parallel after 13.3.1-T1 (P) and 09.2.3-T1/09.7.2-T1 respectively; `13.3.2-T2` follows 13.3.2-T1 and 11.6.1-T1; `13.3.4-T1` follows 13.1.3-T1 and is the last.

### 10.1 Change requests and open items

| Id | Target | Change |
|---|---|---|
| CR-19 (applied) | — | Already README contract (§B4): `data_export_requests` (columns + indexes above), outbox `export_requested.v1`, `GET /me/exports/{id}` response `{ downloadUrl, mediaLinks[] }` — implemented above verbatim, no residual gap |
| CR-20 | `features/09-backend-api/09.1.2-T1` allow-list | Name `ExportCleanupJob` (cross-user expiry scan) and `AccountDeletionScopeHandler` (per-table set-based `DELETE`) in `AllowLists.IgnoreQueryFiltersTypes`, in addition to the P `DataDeletionJob` entry |
| CR-21 | `features/11-worker-deployment/README.md` §8.1 + 11.6.1-T1 runbook + 11.6.2-T1 rules | Register `export_cleanup` (daily) and the new alert rules (export failed twice for one request, oldest open report > 72 h — D15); make `restore-drill.sh` call `deletions export --since` **before** the restore and `deletions reapply --since --file` after (also 13.3.1-T1 [OPEN]) |
| CR-22 | `features/09-backend-api/09.3.3-T2` | Confirm the CAPTCHA request/response field names and failure code used by `POST /reports` (the page sends `captchaToken`, 10 README SD5) |
| CR-23 | `features/06-youtube-posting` 06.2.6-T1 contract | The account-scope handler cancels publications in `awaiting_consent\|scheduled` only; an `uploaded` publication is not cancelable (409 `publication_invalid_state`, copy `publication.cancel.uploaded_use_studio`, `06.2.6-T1:30`) — the deletion path must unlink/hide, not cancel, and the deletion task records which branch it used |
| CR-24 (applied) | `plans/features/10-web-ui/README.md` §B8.1 / `10.3.1-T0-ui-design.md` | 13's §B8.1 cell already reads `10.3.1-T0, 10.3.3-T2 (soft)` (not the widget-only cell this CR originally described); the residual ask is 10's own scope extension (`10.3.1-T0` covers the report-page states) + its `Blocks` back-edge — tracked on 10's side (Lead10S), no further action here |
| N/A (resolved) | `features/09-backend-api/README.md:1150` | **Resolved, not a live CR:** the admin-route registrant question raised while drafting this plan is already settled by direct TL coordination (Lead13S ↔ Lead09S 2026-09-25 — "`GET /admin/deletion-requests` is 09.9.1-T1 read-only over their table"). 09.9.1-T1 registers `GET /admin/reports`, `PATCH /admin/reports/{id}` and `GET /admin/deletion-requests`; this folder supplies the table, handlers, DTOs and audit rows only, exactly as implemented in 13.3.2-T1/13.3.4-T1. The PATCH wire field is 09's `note` (`:1053`), mapped onto `reports.resolution_note` |
| CR-25 (applied) | — | Already README contract (§B4): `deletions retry`/`deletions cancel` are owned by **13.3.2-T1** (T11) — implemented above, no residual gap |
| [OPEN] | 12 §5.7 / 12.6.2 / 12.7.5 (CR-14) | The ledger retention rule stays ⚠️ until the accountant answers: the handler implements "pseudonymise, keep amounts and dates" and the exact keep-window is not asserted by a test |

## 11. Rollout

1. **Migrations first, expand-only** (09.2.2-T1 conventions): `reports` (13.3.4-T1) and `data_export_requests` (13.3.3-T1) land before their code; no column is dropped and `data_deletion_requests` is untouched. Deploy order: migration → API → Worker; the API's new routes return 404 `feature_disabled` only if a route-level feature flag were used — none is needed here (`saas_registration` gates registration, 13.4.1-T1, not these routes).
2. **Enable order.** 13.3.2-T1 flips `DataDeletion:DeferredScopes` to `[]` **in the same release** as `AccountDeletionScopeHandler` — otherwise the P validator either blocks the Worker or (worse) the config claims an `account` handler that does not exist. The Worker start validation is the guard: a mismatch fails boot, never a silent no-op delete.
3. **Staging rehearsal (gate `13-S-rights`, §B7).** Synthetic tenant `demo+1@invalid`: export → verify the allow-list and that no token/secret/other-tenant row is present, links ≤ 15 min, then fake-clock 7 days → object gone; then `POST /me/delete` → wait/run the hourly job → every row and object gone before `due_at`, `users.status = 'deleted'`, ledger pseudonymised, `GET /me/deletion-requests` shows `completed`; finally the 11.6.1-T1 restore drill re-applies via `deletions reapply --since`.
4. **Public report path (gate `13-S-trust`).** Ship the `/vi/report` + `/en/report` page only after `10.3.1-T0`'s design is approved and the CAPTCHA + `anon` policy are live (09.3.3-T2 / 09.3.5-T1, CR-10); before that the route is absent from the prerender list (no half-configured public form). Verify 202 for a bogus target id, exactly one owner notification, and an admin resolve with a note.
5. **Observability.** Metrics `dsar_export_failed_total`, `reports_open_total` (+ the P `deletion_requests_open` / `deletion_request_overdue_total`), log events `export.cleanup.deleted {count}`, `export.job.failed {exportId, errorCode}`, `report.received {reportId}` (ids and counts only), audit actions `abuse.report_received`, `abuse.report_resolved`, `account.data_deleted`, `account.data_exported`. Dashboards/alerts are 08.1/11.6.2's (D15, CR-21) — this folder only emits the names.
6. **Rollback.** Each task is independently revertible: disable the `export_cleanup` recurring job (export requests keep working, objects rely on the bucket lifecycle rule, CR-15); revert the account handler **together with** `DataDeletion:DeferredScopes = ["account"]` (the P status quo); the report endpoints can be hidden from the prerender list while the table and admin endpoints stay (existing rows remain triageable). No data migration is undone, no backup is rolled back.
7. **Post-launch.** 13.5.1-T1 replays this folder's two gates as evidence; 13.5.2-T1 adds the monthly deletion-SLA + DSAR + oldest-open-report rows; the PDPL memo (13.6.1-T1) can add backlog items here (e.g. keeping `user_consents` past deletion) — each would be a version-bumped follow-up task, not a change to these four.
