# 13.3.1 — Deletion requests engine (`data_deletion_requests`, scope handlers, hourly job, due-soon metric, restore re-apply)

> Feature plan for story 13.3.1 (P · M1, 1 task, 1.0 d), per `../../00-overview.md` §10.1.
> Contract: `../README.md` §1 facts K1–K6, §3 decisions **D4** (binding) and D1/D5, §8.1 canonical row, §10 TL rulings.
> Applied rulings: **YR7** (`DeletionResult` + `ErrorOr` shape kept), **YR12** (`deletions reapply --since` replays completed **and** still-open requests), **YR13** (partial unique carries `user_id`).
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope (P · M1).**
- `data_deletion_requests` as the single ledger of deletion duties: aggregate, table, indexes, expand-only migration.
- `IDeletionRequests.CreateAsync(...)` — the only entry point every consumer calls (06 channel revoke, 02/10 episode hard delete, S-phase account deletion).
- `IDeletionScopeHandler` port + registry, with **boot-time fail-fast** when a non-deferred scope has no handler.
- **This folder ships the `episode` handler only** — `channel` is 06.4.3-T1's, `account` is *deferred* until 13.3.2 (S) (F4: the engine is idle for `account`, never silently completing it).
- `DataDeletionJob` (hourly, allow-listed system job, batch ≤ 20, atomic cross-user claim, one DI scope per row) + the due-soon metric that feeds 11.6.2-T1's alert rule.
- Restore re-apply path: `nts-worker deletions reapply --since <PITR timestamp>` plus the companion `export` the restore runbook needs (D4 ✅, 11.6.1-T1:30).
- Metrics `deletion_requests_open`, `deletion_requests_due_soon`, `deletion_request_overdue_total` (K4/K20), audit `account.data_deleted` (`support`, `user`).

**Out of scope (never re-implemented here).**
- Channel scope body (tokens/identity/payload revoke via `ChannelConnection.PurgeYouTubeData`) → 06.4.3-T1; the 06.4.1-T1 route that creates the request.
- Account scope body + `POST /me/delete` + anonymisation → belonged to the dropped data-rights task (phase S, DEC 10.14); the account scope stays unhandled.
- Object-store internals (`IBlobStore`, `S3BlobStore`, buckets) → 09.7.2-T1; asset retention sweep and `assets.deleted_at` → 09.7.3-T1.
- Extracted-text *writing* and the `research/` key layout → 02.5.2-T2; the episode/`story_sources` DB cascade shape → 02 CR-22.
- Audit table/behaviour, e-mail pipeline, job scheduler, tenant filter, outbox → 09.2.x / 09.3.6-T1; alert backends, dashboards and rule loading → 11.6.2-T1.
- **Any notification mechanism of our own**: the due-soon alert is a monitoring rule over our metric, not an e-mail, not a job-level page (K20, §4 UC3).
- Any HTTP route, and any new flag/audit/e-mail/incident mechanism.

**Consumers (blocks).** 06.4.1-T1, 06.4.3-T1 (channel), 11.6.1-T1 (soft: restore runbook re-applies through `deletions reapply`), 02 CR-22 (episode prefix), the dropped account-deletion task (phase S, DEC 10.14) would have reused the same engine.

**Dependencies.** 09.2.2-T1 (`data_deletion_requests` base table + migration conventions), 09.2.6-T1 (`IJobScheduler`, `IRecurringJobRegistration`, `[JobPolicy]`, queues), 09.7.2-T1 (`IBlobStore`, `BlobLocator`, `StorageSettings`). Soft additions recorded as CRs in §10.1: 09.1.2-T1/09.2.3-T1 (allow-list + system context), 09.3.6-T1 (audit), 11.6.2-T1 (rule), 06.2.3-T3 (`platform_incidents`).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| K1 | Table `data_deletion_requests`: `user_id`, `scope` (`account`/`channel`/`episode`), `target_id` (connection/episode id), partial unique `(scope, target_id, reason)` while open, `reason` (`user_request`/`in_app_revoke`/`google_revoke`/`account_delete`), `requested_at`, `due_at`, `completed_at`; purpose "7-day / 30-day deadlines" | 00 §6 L306 ✅; 13 README K4 |
| K2 | `due_at`: 7 d for `in_app_revoke`/`user_request`/`account_delete`, 30 d for `google_revoke`; `CreateAsync` idempotent per `(scope, target_id, reason)` while open | 13 README D4 ✅ |
| K3 | `IDeletionScopeHandler { DeletionScope Scope; Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest r, ct) }`; owners register: 06.4.3-T1 `channel`, 13.3.1-T1 `episode`; `account` idle until 13.3.2 (S) | 13 README D4, F4 ✅; 06.4 plan R5:29 ✅ |
| K4 | `DataDeletionJob` hourly, allow-listed system job, batch ≤ 20, `FOR UPDATE SKIP LOCKED`, per-row `SystemContext(user)`, due-first, idempotent; success → `completed_at`, audit `account.data_deleted` (`support`, `user`), metrics `deletion_requests_open`, `deletion_request_overdue_total` | 13 README D4 ✅; 06.4 plan R6:30 ✅ |
| K5 | Restore re-apply: `nts-worker deletions reapply --since <PITR timestamp>` re-executes every request completed after that point, idempotent, used by 11.6.1-T1 before reopening | 13 README D4 ✅; 11.6.1-T1:30, :81 ✅ |
| K6 | Episode scope: objects under `research/{userId}/{episodeId}/` die with the episode; `story_sources`/`fetch_tasks`/`source_candidates`/`generation_runs`/`fact_sheets`/`story_versions` DB rows **cascade**, the objects do not | 02 README CR-22:675 ✅; `../../13-compliance.md`:67 ✅ |
| K7 | `IBlobStore` (Application port) exposes exactly `PutAsync`, `OpenReadAsync`, `DeleteAsync(bucket,key,ct)` (**missing key == success**), `ExistsAsync`, `CreateReadUrl`; failures map to `ErrorCodes.ServiceUnavailable` → 503; `AccessDenied` logs bucket + key **prefix only** | 09.7.2-T1:26-32, :61, :64 ✅ |
| K8 | `IBlobStore` has **no** list/prefix member; 09.7.2-T1:116 still names "13.3.1-T1 (prefix delete)" as a consumer; `BlobLocator.BucketFor(kind, settings)` resolves the bucket | 09.7.2-T1:26-32, :116, :161 ✅ → CR-1 (§10.1) |
| K9 | `IExtractedTextStore.WriteAsync(userId, episodeId, sourceId, sha256, text, ct) → string key`, key `research/{userId}/{episodeId}/{sourceId}/{sha256}.txt`; "a crash leaves an orphan object (cleaned by 13.3's deletion sweep)" | 02.5.2-T2:125, :219, :237 ✅ |
| K10 | Recurring jobs: `IRecurringJobRegistration { string Id; void Register(IJobScheduler scheduler); }`, `AddOrUpdateRecurring<TJob>(id, cron, call)`; job classes in Application, ids-only arguments, queues `default|render|publish|email`; Api's scheduler throws by design; `[JobPolicy(Retries = n)]` | 09.2.6-T1:23-30, :41-42, :56 ✅; 06.4 plan:140 ✅ |
| K11 | Audit: `AuditEntry(Guid? ActorUserId, ActorType ActorType, Guid? SubjectUserId, string Action, Category Category, Visibility Visibility, string? EntityType, Guid? EntityId, string DataJson, IPAddress? Ip, string? UserAgent, string? ActorLabel, DateTimeOffset OccurredAt)` + `IAuditWriter.WriteAsync(AuditEntry, ct)` ("joins the ambient transaction; never commits"); jobs and CLI verbs write directly; operator verbs set `ActorLabel = "system:operator"` | 09.3.6-T1:31-33, :34, :62, :129 ✅; 09 README BD15:700 ✅ |
| K12 | Cross-user access: `ISystemContext.For(userId)` throws on `Guid.Empty`, **on a second call**, or if an `AppDbContext`/`ReadDbContext` was already resolved in that scope; `ISystemWorkQuery.PendingAsync<T>(Expression<Func<T,bool>> where, int batch, CancellationToken ct) where T : Entity, IUserOwned` → `(Id, UserId)` pairs; `IgnoreQueryFilters(["tenant"])` is allow-listed **only** for `RetentionSweepJob`, `DataDeletionJob`, `CreditReconciliationJob`, `SystemWorkQuery`, and the IL scan attributes a hit to the outermost declaring type | 09.2.3-T1:24, :36, :68 ✅; 09.1.2-T1:43 ✅; 06.4 plan R12:36 ✅ |
| K13 | Tables are written **unqualified** in DDL (`CREATE TABLE user_consents (… REFERENCES users(id) ON DELETE RESTRICT)`); `audit_logs` and `email_messages` carry **no FK to `users`** so rows outlive a deleted account (13 §5.4); migration name shape `<yyyyMMddHHmm>_<Owner>_<What>` | 09.3.1-T1:97-99 ✅; 09.3.6-T1:55 ✅; 09.2.6-T3:75 ✅; 06.4 plan:314 ✅ |
| K14 | Production PostgreSQL is **17** (managed) ⇒ `NULLS NOT DISTINCT` on unique indexes is available; snake_case everywhere | 11.6.1-T1:18 ✅; 09.2.1-T1:66 ✅ |
| K15 | Restore drill: re-apply every `data_deletion_requests` row with `completed_at > --target-time`, ordered by `completed_at`, **idempotently**; acceptance "running the re-application twice ends in the same state with no error"; script exit codes 0 ok / 1 check failed / 2 usage-environment; the drill is operator-run — `[DECISION]` **no Hangfire job** for anything that can overwrite a database; 11.6.1-T1 plan:62 requires the list to be captured from the live DB ("a story deleted last week must not come back") | 11.6.1-T1:26-30, :53, acceptance ✅ |
| K16 | Alert rules live in a repository rule file currently asserted as **exactly 15 rules**, and none is about deletions (rule 15 is render queue) | 11.6.2-T1:32-50, :75 ✅ |
| K17 | `platform_incidents(id, platform, oauth_client_id, error_code, raised_at, cleared_at, cleared_by, note)`, partial unique `(platform, oauth_client_id) WHERE cleared_at IS NULL`, semantics = broken OAuth client; written only through 06's `IPlatformIncidentStore` | 00 §6 L293 ✅; 06.2.3-T3:9 ✅; 06.4 plan:83 ✅ |
| K18 | Audit retention: 09.7.3-T1 sweeps `assets` + `audit_logs` only; "13.3.1-T1 owns the hard delete and its own audit rows"; account deletion is **not** that job | 09.7.3-T1:86, :166 ✅ |
| K19 | Deletion scopes (13 §5.4): channel = tokens/metadata/payloads; account = everything + pseudonymised ledger; episode = story versions + assets (keeps `usage_records`); backups age out within 30 d | `../../13-compliance.md` §5.4 L26-31 ✅ |
| K20 | The due-soon alert is **an 11.6.2-T1 rule evaluated by the monitoring backend** (shape: `deletion_requests_due_soon > 0`), "**no `alerted_at` column needed**" — the engine only exports the metric | 06.4 plan R6:30 ✅ |
| K21 | A missing `IDeletionScopeHandler` for a required scope **must fail fast at Worker start** (asserted by the architecture/container test) | 06.4 plan:317 ✅ |
| K22 | Channel-scope deletion keeps the row (`revoked`) and nulls identity/tokens/payloads; the engine writes the completion audit for **every** scope | 06.4 plan:70, :208, :320 ✅ |

## 3. Domain and data

### 3.1 Domain (`src/NightTales.Domain/Compliance/Deletion/`)
- `DeletionRequest : AggregateRoot, IUserOwned` — the entity is user-owned so the tenant filter protects UC1's re-read and every future reader (13.3.2), and the job's cross-user read is the **allow-listed, reviewable bypass** (`AllowLists.IgnoreQueryFiltersTypes` already names `DataDeletionJob`, K12) rather than an invisible raw-SQL one.
  - Fields: `Id`, `UserId`, `Scope`, `TargetId (Guid?)`, `Reason`, `Status`, `RequestedAt`, `DueAt`, `ClaimedAt`, `Attempts`, `LastErrorCode`, `LastErrorAt`, `CompletedAt`, `ReappliedAt`.
  - Invariants (aggregate-checked, unit-tested):
    1. `DueAt == RequestedAt + Deadline(Reason)` — 7 d (`UserRequest`, `InAppRevoke`, `AccountDelete`) / 30 d (`GoogleRevoke`) (K2).
    2. `TargetId IS NOT NULL` ⇔ `Scope ∈ {Channel, Episode}` (K1); `Account` ⇒ `TargetId IS NULL`.
    3. `CompletedAt IS NOT NULL` ⇒ `Status == Completed`; `Status == Completed` ⇒ `CompletedAt IS NOT NULL`.
    4. Transitions: `Pending → InProgress → Completed | Failed`; `Failed → InProgress` on retry; `Completed → InProgress` **only** through the restore re-apply (`ReappliedAt` set) (K5, K15, YR12).
    5. `Attempts` is monotonic; `MaxAttempts` (5) exceeded ⇒ the row stays `Failed` and is escalated, never silently completed.
- `DeletionScope { Account, Channel, Episode }`, `DeletionReason { UserRequest, InAppRevoke, GoogleRevoke, AccountDelete }`, `DeletionStatus { Pending, InProgress, Completed, Failed }`.
- `DeletionResult(int ObjectsDeleted, int RowsAffected)` — **YR7**: this shape and `ErrorOr` failures are kept; 06.4.3-T1 adapts to it.
- `DeletionRequest.Create(userId, scope, targetId, reason, now) → DeletionRequest`; domain errors in `DeletionErrors`. No new wire code (§5).
- **Files (this folder).** `Domain/Compliance/Deletion/{DeletionRequest,DeletionScope,DeletionReason,DeletionStatus,IDeletionScopeHandler,DeletionResult}.cs`; `Application/Compliance/Deletion/{IDeletionRequests,DeletionRequests,IDeletionScopeHandlerRegistry,DeletionScopeHandlerRegistry}.cs` + `Jobs/{DataDeletionJob,DataDeletionRegistration}.cs`; `Infrastructure/Compliance/Deletion/{DataDeletionRequestRepository,DeletionScopeHandlerRegistry,DeletionScopeHandlerRegistryValidator,Handlers/EpisodeDeletionScopeHandler,Configuration/DeletionRequestConfiguration}.cs`; `Infrastructure/Persistence/Migrations/<yyyyMMddHHmm>_13_DataDeletionRequests.cs`; `Worker/Cli/DeletionsCommand.cs`.

### 3.2 Table and DDL
`data_deletion_requests` (default schema; **unqualified names**, K13), expand-only migration `<yyyyMMddHHmm>_13_DataDeletionRequests`:

```sql
CREATE TABLE data_deletion_requests (
  id              uuid        PRIMARY KEY,
  user_id         uuid        NOT NULL,                        -- NO FK: the row outlives the account (K13, §8 Evidence)
  scope           text        NOT NULL CHECK (scope IN ('account','channel','episode')),
  target_id       uuid        NULL,                            -- connection id / episode id; NULL for account scope
  reason          text        NOT NULL CHECK (reason IN ('user_request','in_app_revoke','google_revoke','account_delete')),
  status          text        NOT NULL DEFAULT 'pending'
                              CHECK (status IN ('pending','in_progress','completed','failed')),
  requested_at    timestamptz NOT NULL,
  due_at          timestamptz NOT NULL,
  claimed_at      timestamptz NULL,                            -- lease start; stale leases are re-claimable
  attempts        int         NOT NULL DEFAULT 0,
  last_error_code text        NULL,                            -- ErrorCodes value only, never a message
  last_error_at   timestamptz NULL,
  completed_at    timestamptz NULL,
  reapplied_at    timestamptz NULL                             -- set by `deletions reapply`
);

-- YR13: the partial unique carries user_id
CREATE UNIQUE INDEX ux_deletion_requests_open
  ON data_deletion_requests (user_id, scope, target_id, reason) NULLS NOT DISTINCT
  WHERE completed_at IS NULL;

CREATE INDEX ix_deletion_requests_due
  ON data_deletion_requests (due_at)
  WHERE completed_at IS NULL;                                  -- claim scan + open/overdue/due-soon counts (index-only)
CREATE INDEX ix_deletion_requests_user
  ON data_deletion_requests (user_id, requested_at DESC, id DESC);  -- tenant-scoped history (UC1 re-read, 13.3.2, ops)
CREATE INDEX ix_deletion_requests_reapply
  ON data_deletion_requests (completed_at, id) WHERE completed_at IS NOT NULL;  -- `deletions export --since` (completed half)
```

**Why `user_id` and `NULLS NOT DISTINCT` — [DECISION], YR13 (clarifies 13 CR-1 / 00 §6).**
1. `user_id` first: without it, two different users' open `(account, NULL, account_delete)` rows would share one key slot, so `ON CONFLICT DO NOTHING` would **silently swallow the second user's account deletion** and UC1's post-conflict re-read could return another user's row. With `user_id` the key is per-user and the re-read can also filter by `user_id` (belt and braces with the tenant filter).
2. `NULLS NOT DISTINCT`: `account`/`channel`-with-no-target rows carry `target_id IS NULL`, and PostgreSQL's default `NULLS DISTINCT` would let two identical open rows insert, silently breaking D4's idempotency. `NULLS NOT DISTINCT` (PostgreSQL 15+; prod is 17, K14) keeps the `ON CONFLICT` upsert usable, which a `COALESCE()` expression index cannot do.

**Claim — one atomic conditional `UPDATE` (no long-held lock).** Issued from inside `DataDeletionJob` (the allow-listed declaring type, K12) via EF `ExecuteUpdateAsync`:

```csharp
var won = await db.DeletionRequests.IgnoreQueryFilters(["tenant"])
    .Where(r => ids.Contains(r.Id) && r.CompletedAt == null &&
        (r.Status == DeletionStatus.Pending
         || (r.ClaimedAt < leaseCutoff)                       // stale lease: crash mid-execution
         || (r.Status == DeletionStatus.Failed && r.Attempts < maxAttempts)))
    .ExecuteUpdateAsync(s => s
        .SetProperty(r => r.Status, DeletionStatus.InProgress)
        .SetProperty(r => r.ClaimedAt, now)
        .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
```

D4's `FOR UPDATE SKIP LOCKED` is realised as **this atomic conditional claim instead of a lock held across the handler call**: handlers take seconds (S3 round-trips), so holding row locks that long pins a transaction and risks idle-in-transaction (BD16). Exactly-once is preserved — two workers that scanned the same candidate both issue the `UPDATE`, and the `status='in_progress' AND claimed_at=now` marker afterwards identifies which rows *this* worker won (EF's `ExecuteUpdateAsync` has no `RETURNING`, so the ≤ 20 claimed rows are read back once with the same filter and the marker). `ClaimedAt < now − DataDeletion:LeaseMinutes` makes a crash reclaimable; `Failed` rows are only re-claimable while `attempts < MaxAttempts`.

### 3.3 Reuse, never fork
- Audit → `IAuditWriter`/`AuditEntry` (K11). No `deletion_audit` table.
- Cross-user reads → `IgnoreQueryFilters(["tenant"])` **inside `DataDeletionJob`** (allow-listed, K12) for the scan/claim, and `ISystemContext.For(userId)` in a fresh DI scope for the per-row work. No raw-SQL bypass, no `ISystemWorkQuery` widening (its single member returns pairs and has no locking hint, so D4's claim cannot use it — recorded as a CR-5 note).
- Object deletion → `IBlobStore.DeleteAsync` + `BlobLocator.BucketFor` (K7). No bucket is created (11.4.1-T1 owns buckets, 09.7.2-T1:161).
- Job scheduling → `IRecurringJobRegistration` + `IJobScheduler.AddOrUpdateRecurring` (K10). No `Timer`, no `HostedService` loop, no second Hangfire server.
- Alerts → the exported metrics + 11.6.2-T1's rule file (K20). No e-mail, no page from the job, no second notification mechanism.

## 4. Use cases

*All commands/queries live in Application and are invoked in-process; the only operator surface is the CLI verbs in §5.3.*

| UC | Trigger | Port / call | Transaction | Idempotency | Audit |
|---|---|---|---|---|---|
| UC1 request | 06.4.3-T1 channel revoke, 02/10 episode hard delete, 13.3.2 (S) | `IDeletionRequests.CreateAsync` | caller's UoW (joins it, never opens one) | upsert on `ux_deletion_requests_open`; returns the **existing** open row | none (the request row is the record; the caller's command audits its own action) |
| UC2 hourly execute | `DataDeletionJob` | allow-listed scan + atomic claim + `IDeletionScopeHandler.ExecuteAsync` | one DI scope per row, each with its own UoW | claim is atomic (lost rows are skipped); handler must be re-runnable | `account.data_deleted` (`support`, `user`) on success |
| UC3 due-soon signal | same job, metric phase | metric export (`deletion_requests_due_soon`) | none (read-only counts) | none needed — the rule is stateless | none (ops signal) |
| UC4 failure/backoff | handler error / store error | job loop | own UoW per row | `attempts` monotonic; `Failed` stays claimable until `MaxAttempts` | none (metric + incident), audit only on success |
| UC5 restore re-apply | operator CLI (11.6.1-T1 runbook) | `deletions export` / `deletions reapply --since` | one UoW per row | re-running is a no-op (handler idempotency + `reapplied_at`) | `account.data_deleted` with `ActorLabel = "system:operator"` (`support`, `internal`) |
| UC6 inspect | operator | `SELECT` on `ix_deletion_requests_user` / `_due` | read-only | n/a | none |

### UC1 — `IDeletionRequests.CreateAsync`
`Task<ErrorOr<DeletionRequest>> CreateAsync(Guid userId, DeletionScope scope, Guid? targetId, DeletionReason reason, CancellationToken ct)`.
- Validation (Application validator + domain invariants): `userId != Guid.Empty`; `scope`/`reason` parse; `targetId` required for `Channel`/`Episode` and forbidden for `Account`; `reason == AccountDelete ⇒ scope == Account`; `reason is InAppRevoke or GoogleRevoke ⇒ scope == Channel`.
- Authorization: **no** HTTP surface ⇒ the caller's authorization stands (06.4.1-T1/06.4.3-T1 for channel, 10.5.1-T1/09.5.1 for episode, S-phase re-auth for account). `CreateAsync` additionally asserts the target belongs to `userId` (episode: `Episodes` lookup; channel: `Channels`) and returns `Error.NotFound(ErrorCodes.NotFound)` otherwise (09.1.4-T1 catalogue semantics).
- Idempotency: `INSERT … ON CONFLICT (user_id, scope, target_id, reason) WHERE completed_at IS NULL DO NOTHING` then re-read **by `(user_id, scope, target_id, reason)` inside the caller's tenant scope** (YR13); on conflict return the existing open row with `due_at` unchanged — a second revoke must not extend the clock.
- Error codes: `ErrorCodes.NotFound`, `ErrorCodes.ValidationFailed` — both already in the 09 §5.4 catalogue; **no new code** (nothing reaches HTTP in P).
- Commit: joins the caller's transaction, so a revoke and its deletion duty commit together — this is what makes 06.4.1-T1/02 crash-safe.

### UC2 — hourly execute (`DataDeletionJob.RunAsync(ct)`)
1. Preamble: `deletion_requests_open` gauge (rows with `completed_at IS NULL`) and `deletion_request_overdue_total` counter (`due_at < now`) — plain counts over `ix_deletion_requests_due`.
2. Candidate scan, **in `DataDeletionJob`** (allow-listed declaring type, K12):
   `db.DeletionRequests.IgnoreQueryFilters(["tenant"]).Where(r => r.CompletedAt == null && r.DueAt <= now).OrderBy(r => r.DueAt).Select(r => r.Id).Take(DataDeletion:BatchSize (20))`.
3. Atomic claim of those ids (§3.2), then read back the ≤ 20 rows this worker won by the `status='in_progress' AND claimed_at=now` marker.
4. **Per claimed row: a fresh DI scope** — `using var rowScope = _scopeFactory.CreateScope();` then `rowScope.ServiceProvider.GetRequiredService<ISystemContext>().For(row.UserId)` (exactly one `For` call per scope: a second call throws, and so does a `For` after any `AppDbContext` was resolved in that scope — K12), resolve a fresh `IAppDbContext` in the same scope, then resolve the handler from `IDeletionScopeHandlerRegistry` by `row.Scope` and call `ExecuteAsync`.
5. `Success` → complete + `IAuditWriter.WriteAsync(new AuditEntry(ActorUserId: null, ActorType: ActorType.System, SubjectUserId: row.UserId, Action: "account.data_deleted", Category: Category.Support, Visibility: Visibility.User, EntityType: "deletion_request", EntityId: row.Id, DataJson: JsonSerializer.Serialize(new { scope = row.Scope, targetId = row.TargetId, reason = row.Reason }), Ip: null, UserAgent: null, ActorLabel: null, OccurredAt: now), ct)` in that row's unit of work, then commit.
6. `Error` → `Fail(now, error.Code)`; if `attempts >= MaxAttempts` keep `Failed` and escalate (§7.3); otherwise the next hourly run retries through the stale-lease branch of the claim.
7. **No handler registered** for the row's scope (e.g. `account` in P) → **do not complete**: leave the row `Pending`, log `data_deletion_scope_unregistered scope=account`, count `deletion_requests_unhandled_total{scope}`. D4's "idle for account" must never read as "deleted". (The boot validator already fails for a non-deferred scope — §7.1.)
8. No outbox event, no realtime message (deletion is internal; precedent 09.7.3-T1:86). Summary log: `data_deletion_run claimed=… completed=… failed=… unhandled=… due_soon=… duration_ms=…` (ids/counts only, §8).

### UC3 — due-soon signal (K20, no e-mail)
- Read-only count: open rows with `due_at <= now + DataDeletion:AlertLeadHours (24 h)` → gauge `deletion_requests_due_soon`, plus **one** `Warning` log line per run naming the soonest row (`deletion_request_due_soon scope=… due_in_hours=…`, ids only) as the interim signal.
- The alert itself is 11.6.2-T1's rule over that metric (shape `deletion_requests_due_soon > 0`, following 06.2.3-T3's `platform_incident_open > 0 for 5m` precedent); the rule text/CR is **06 CR-10**, which the TL applies, so this folder adds no new rule file and no CR of its own beyond the §10.1 coordination row.
- **No `alerted_at` column and no dedupe state**: the rule is evaluated by the monitoring backend over a gauge, so a repeated hourly signal cannot double-page (R6: "no `alerted_at` column needed"). **No e-mail, no SMTP, no template** — one notification path only.

### UC4 — failure, backoff, escalation
`DataDeletion:MaxAttempts` (5) attempts, retried on the hourly tick (no separate backoff timer — one row per hour is bounded and the deadline is 7/30 days). On exhaustion: metric `deletion_request_failed_total{scope,error_code}`, one `Error` log, and a `platform_incidents` row through 06's `IPlatformIncidentStore` when the failure is platform-wide (`service_unavailable` from the blob store failing ≥ 3 distinct requests in one run) — never for a single per-request data problem. **`platform_incidents` caveat [OPEN]:** its columns (`platform`, `oauth_client_id`) and partial unique `(platform, oauth_client_id) WHERE cleared_at IS NULL` are OAuth-client shaped (K17); a non-OAuth incident needs a sentinel `platform` plus the same `NULLS NOT DISTINCT` treatment, or dedupe silently fails. Pending 06's ruling (CR-4); until then UC4 escalates by metric + log only.

## 5. API and events

### 5.1 HTTP
**No route in P.** Every P caller is in-process (06.4.3-T1 revoke handler, 02/10 episode hard delete); the S-phase owner surfaces (`POST /me/delete`, admin retry) belonged to the dropped data-rights tasks with their own re-auth and routes (DEC 10.14). Consequence: no new error codes, no OpenAPI change, no oasdiff surface, no CSRF/idempotency-key surface to define.

### 5.2 Outbox / SignalR
None (§4 UC2 step 8). The audit row plus the request row are the durable record; 06's/10's own events carry any user-visible effect.

### 5.3 Operator CLI (Worker, D4/K5/K15/YR12)
```
nts-worker deletions export  --since <iso8601> --out <path>     # read-only, run against the LIVE db BEFORE a restore
nts-worker deletions reapply --since <iso8601> [--file <path>]  # run against the RESTORED instance
```
- `export` selects **completed and still-open** requests (YR12): `WHERE completed_at > @since OR completed_at IS NULL ORDER BY completed_at NULLS FIRST, id` — the still-open half matters because a PITR restore to `@since` cannot see rows *created* after that instant, which are exactly the duties whose data comes back (11.6.1-T1 plan:62: "a story deleted last week must not come back"). `@since` is the restore's target time; rows already completed before it were never lost. Output: NDJSON (ids, `scope`, `target_id`, `reason`, `requested_at`, `due_at`, `completed_at`; no PII beyond ids), with the row count printed.
- `reapply` **replays completed and still-open requests** (YR12): per row, ordered by `completed_at` then `requested_at`, inside a fresh DI scope with `ISystemContext.For(user_id)`: upsert the row (id preserved) with `status='in_progress'`, `claimed_at = now`, `reapplied_at = now`, original `requested_at`/`due_at`; then resolve the handler and `ExecuteAsync` — completed rows re-execute immediately, open rows whose `due_at <= now` execute now, and open rows not yet due are re-inserted as `Pending` for the hourly job. Success re-writes `completed_at`; failure records `last_error_code`.
- **Fails loudly — never exits 0 having done nothing** (YR12/K15): a non-empty file with every row skipped, a row whose scope has no registered handler, an unparseable file, or any handler failure ⇒ non-zero exit (1) with a per-row summary; `--self-check` style no-op runs are not accepted as success. Exit codes match 11.6.1-T1's script contract (0 ok / 1 check failed / 2 usage-environment).
- Re-running is a no-op: handlers are idempotent (`IBlobStore.DeleteAsync` missing key == success, K7) and a row whose storage state already matches returns success (K15's "running the re-application twice ends in the same state with no error").
- Secrets never in argv (01 D9); the audit row uses `ActorLabel = "system:operator"` (K11).
- **No Hangfire job re-applies a restore** — 11.6.1-T1:53 `[DECISION]`: nothing that can overwrite a database belongs in the Worker scheduler.

## 6. UI

**No UI in this folder** (Area BE). Consumers own the surfaces and the copy:
| Consumer | Surface | Copy keys (owned by the consumer) |
|---|---|---|
| 06.4.3-T1 / 06.4.1-T1 | channel disconnect + the resulting duty | `channels.revoke_*`, `deletion_scheduled` |
| 10.5.1-T1 (+09.5.1) | episode hard delete confirmation | episode delete copy; states only that the deletion is in progress |
| 06.4.2-T2 | channel state display | `publications.*` (06 owns) |
No new i18n key is introduced here — there is no user-visible surface and no notification copy in P (UC3 is a metric). A UI that showed "deletion scheduled/complete" would need `GET /me/deletion-requests` — **not in P** (the phase-S account-deletion task is dropped, DEC 10.14) and deliberately absent so no client depends on a route that does not exist.

## 7. Jobs and integrations

### 7.1 `DataDeletionJob`
- `Application/Features/Compliance/Deletion/Jobs/DataDeletionJob.cs` — `public sealed class DataDeletionJob : IBackgroundJob { public Task RunAsync(CancellationToken ct); }`; ids-only arguments (K10); queue `default`; `[JobPolicy(Retries = 3)]` for the whole-run transport failures (per-row retry is ours, §4 UC4). Constructor takes `IServiceScopeFactory`, `IDeletionScopeHandlerRegistry`, `IClock`, `IAuditWriter` and the metric factory; every `IAppDbContext` it touches comes from a DI scope (the job's own for the scan/claim, a fresh one per row), never injected directly.
- **Allow-listed by design:** the scan/claim call sites live lexically in `DataDeletionJob` because `AllowLists.IgnoreQueryFiltersTypes` already lists it and the IL scan attributes a hit to the outermost declaring type (K12). No repository may call `IgnoreQueryFilters`, and the file stays small so the bypass is reviewable.
- Registration: `Application/Features/Compliance/Deletion/Jobs/DataDeletionRegistration.cs` — `IRecurringJobRegistration` with `Id => "data_deletion"`, `Register(s) => s.AddOrUpdateRecurring<DataDeletionJob>("data_deletion", "0 * * * *", (j, ct) => j.RunAsync(ct))` (K10 pattern, 09.7.3-T1:58-59), wired in the Worker composition root next to 09.2.6-T1's registrations.
- **Boot fail-fast (K21, 06.4 plan:317):** `DeletionScopeHandlerRegistryValidator` (startup validation, part of the Worker's options/hosted validation) compares the registered handler scopes against `Enum.GetValues<DeletionScope>()` minus `DataDeletion:DeferredScopes`, and throws at start when a non-deferred scope has no handler. In P `DataDeletion:DeferredScopes = ["account"]`; when 13.3.2 registers the account handler the entry is removed and the validator then enforces it. A `Channel` handler regression therefore cannot reach production silently.
- Config: `DataDeletion:Enabled` (true), `DataDeletion:BatchSize` (20), `DataDeletion:LeaseMinutes` (15), `DataDeletion:MaxAttempts` (5), `DataDeletion:AlertLeadHours` (24), `DataDeletion:DeferredScopes` (`["account"]`).
- Runtime: single job; no advisory lock needed (the claim is atomic, §3.2); a second Worker instance is safe by construction; the batch cap and one scope per row bound the run; every store/HTTP call uses the 09 timeout conventions and a per-row `try/catch` so one bad row cannot abort the batch.
- Performance: no N+1 — one scan, one claim `UPDATE`, one read-back, then per row one handler query plus bounded object deletes (≤ 100 keys per S3 batch, sequential); every query is index-backed (`ix_deletion_requests_due`), no `OFFSET`.

### 7.2 Integrations
| Integration | Port | Notes |
|---|---|---|
| Object storage | `IBlobStore.DeleteAsync(bucket, key, ct)` (K7); prefix listing via CR-1 | bucket from `BlobLocator.BucketFor(kind, StorageSettings)`; missing key = success |
| Asset rows | read `assets` for the episode, then remove the episode aggregate so the rows cascade (K6); never writes `assets.deleted_at` (09.7.3-T1's column) | 09.7.3-T1:82-83, :166 |
| Extracted text | keys from `story_sources.extracted_text_key` (K9) + prefix sweep for orphans (CR-1) | 02.5.2-T2:125, :219 |
| Audit | `IAuditWriter.WriteAsync` (K11) | `account.data_deleted`, `support`, `user` for every scope (K22); operator verb `internal` |
| Incident | 06's `IPlatformIncidentStore` (K17) | platform-wide failures only; [OPEN] per UC4 |
| Metrics/logs | `deletion_requests_open`, `deletion_requests_due_soon`, `deletion_request_overdue_total`, `deletion_request_failed_total{scope,error_code}`, `deletion_requests_unhandled_total{scope}`, `deletion_run_seconds` | feed 11.6.2-T1's rule (CR-2/K20) |

### 7.3 Failure map
| Failure | Where | Behaviour |
|---|---|---|
| Blob store 503/timeout | handler `IBlobStore` call | row `Failed` + `last_error_code = service_unavailable`; retried next tick; ≥ 3 rows/run → incident (UC4) |
| Object already gone | `DeleteAsync` `NoSuchKey` | success (idempotent, K7) |
| Episode not found / wrong tenant | handler precondition | `Error.NotFound` → row `Failed`, log `data_deletion_target_missing`, **no** object calls |
| Handler missing for a required scope | boot | **startup fails** (validator, K21) — never reaches the job |
| Handler missing for a deferred scope (`account` in P) | job step 4 | row stays `Pending` + `unhandled` metric; never `completed` |
| Crash between object delete and `completed_at` | job step 5 | stale lease → next run re-claims and re-executes the idempotent handler |
| Concurrency: two workers claim the same rows | job step 3 | the atomic `UPDATE` matches once; the loser's read-back returns nothing and it skips |
| Metric export unavailable | UC3 | one `Warning`, run continues (the job's real work is unaffected) |
| Task/run-level transport failure | job run | `[JobPolicy(Retries = 3)]`; each row commits alone, so no partial state |
| `deletions reapply` handler failure or all-skipped file | CLI | non-zero exit (1), per-row summary; re-running is safe (K15) |

## 8. Security and privacy

- **Tenant isolation.** `DeletionRequest` is `IUserOwned`, so the tenant filter protects UC1's re-read and 13.3.2's later readers. The only cross-user reads are the job's scan/claim, and they use the **allow-listed** `IgnoreQueryFilters(["tenant"])` call site declared in `DataDeletionJob` itself (K12) — a reviewable bypass, not an invisible raw-SQL one. Per-row work runs in a fresh DI scope with exactly one `ISystemContext.For(userId)`, so a scope-handler bug can touch at most that request's owner's data. A cross-tenant `targetId` yields `NotFound` (404 semantics, never 403) — no existence leak.
- **No new secrets.** The engine touches no token, key or client secret; channel tokens are 06's (13 §5.4). Data Protection, `DP_KEK` and storage credentials are untouched (11.6.1-T1:26).
- **Nothing sensitive in logs.** Row ids, `scope`, `target_id`, `reason`, counts, `error_code` and blob bucket + **key prefix** only (09.7.2-T1:64). Never an e-mail address, never a token, never a full object key, never extracted text (`last_error_code` stores a code, not a message).
- **Idempotency everywhere.** The `(user_id, scope, target_id, reason)` NULLS-NOT-DISTINCT upsert for UC1, the atomic claim for UC2, handler-level object-delete idempotency for UC4/UC5. The legal promise is a *deadline*, so a duplicate execution is only ever wasted work — never a wrong outcome, a swallowed request, or an extended clock.
- **Evidence survives the account.** `data_deletion_requests` carries **no FK to `users`** (K13, same choice as `audit_logs`/`email_messages`), so the rows that prove the 7/30-day duty was met are not cascaded away by 13.3.2's account deletion — and the open account-scope row cannot be destroyed mid-run. The audit row is written in the same transaction as `completed_at`, so a completed row without an audit row is impossible (K11, BD15).
- **Operator path.** `deletions reapply` is a CLI verb reachable only with host/container access, needs no token, writes `ActorLabel = "system:operator"` (K11), is audited, and reads only `data_deletion_requests` (never caller-supplied keys); its restore guard refuses a non-scratch target.
- **Least privilege.** The engine calls only `DeleteAsync`; no bucket creation, no policy change, no listing outside the two prefixes it owns (11.4.1-T1/09.7.2-T1 own buckets).
- **Account erasure must cover `source_creations`.** 02.5.1-T1 adds the `source_creations` ledger (one row per URL source ever created, kept after the source is removed so the 24 h cap cannot be reset by delete-and-re-add). It holds `user_id` and a timestamp, no URL or text, and its `users` FK is `ON DELETE RESTRICT`: the account-scope erasure (13.3.2) must delete the user's ledger rows explicitly, before the `users` row, or the account delete fails; a row surviving the account would be personal data outside the retention promise (SEC-5 of the 02.5.1-T1 review).
- **Backup honesty.** Deleted data ages out of backups within 30 days (13 §5.4, 00 L412); the CLI's export file holds ids and dates only, lives on the operator's scratch host, and is deleted by the runbook after the drill.

## 9. Test plan

Projects (09 README Part A D2): `tests/NightTales.Domain.UnitTests`, `tests/NightTales.Application.UnitTests`, `tests/NightTales.Api.IntegrationTests` (all DB/job/CLI tests, real PostgreSQL 17 via `PostgresFixture`), `tests/NightTales.Architecture.Tests`, `tests/NightTales.TestKit` (`FakeClock`, `InMemoryBlobStore` (09.7.2-T1:112), `TestUserFactory`). xunit.v3 + Shouldly; **no mocking library**; real PostgreSQL for every claim/SQL/CLI assertion.

| Layer | Test | Proves |
|---|---|---|
| Domain unit | `DeletionRequest_Create_SetsDeadlinePerReason` (7/30 d table) | K2 |
| Domain unit | `DeletionRequest_TargetIdRequiredForChannelAndEpisode_FreeForAccount`; `…_CompletionAndReapplyTransitionsAreExclusive` | §3.1 invariants |
| Domain unit | `DeletionResult_KeepsObjectsDeletedAndRowsAffectedShape` | YR7 |
| Application unit | `CreateAsync_SecondOpenRequest_ReturnsExistingRowWithoutExtendingDueAt` | UC1 idempotency |
| Application unit | `CreateAsync_CrossTenantTarget_ReturnsNotFound` | §8 (404, no leak) |
| Integration | `CreateAsync_TwoUsersWithSameAccountScope_ProduceTwoRows`; `…_SameUserParallelInserts_ProduceOneRow` | YR13 index (the platform-wide-collision defect this plan exists to prevent) |
| Integration | `CreateAsync_PostConflictReread_ReturnsOwnRowOnly` | YR13 + tenant filter |
| Integration | `Claim_IsExclusiveAcrossTwoWorkers` (two concurrent runs over the same due rows) | §3.2 atomic claim, K4 |
| Integration | `Claim_ReclaimsStaleInProgressAfterLease`; `…_DoesNotReclaimBeforeLease` | crash recovery |
| Integration | `Job_ProcessesDueRowsDueFirst_UpToBatchCap` (25 due rows, batch 20 → 20 completed, 5 next run) | K4 batch ≤ 20 |
| Integration | `Job_UsesOneDiScopePerRow_AndSystemContextOncePerScope` (3 rows → 3 scopes, one `For` each; no `InvalidOperationException`) | K12/UC2 step 4 |
| Integration | `Job_Success_WritesCompletedAtAndOneAuditRow` (`account.data_deleted`, `support`, `user`) | K4/K11 |
| Integration | `Job_HandlerThrows_RowBecomesFailed_AndRetriesOnNextRun`; `…_StopsAtMaxAttempts_AndEscalates` | UC4 |
| Integration | `Job_DeferredScope_StaysPendingAcrossTicks`; `Job_RegisteredScopeWithoutHandler_FailsAtStartup` | F4, K21 |
| Integration | `Job_EpisodeScope_DeletesObjects_RemovesEpisodeRow_KeepsUsageRecords_ThenCompletes` (real episode with assets + `story_sources.extracted_text_key`; objects gone, episode/`story_versions`/`fact_sheets` rows gone, `usage_records` kept) | K6, K9, K19, 13 §5.4 |
| Integration | `Job_EpisodeScope_RunTwice_IsNoOp` (second run: 0 objects deleted, success) | idempotency |
| Integration | `Job_MetricPhase_ExportsOpenOverdueAndDueSoonCounts` (23 h-due row included, 25 h-due row excluded) | UC3, K20 |
| Integration | `Job_NoHandlerForDeferredScope_LogsOncePerRun_AndNeverCompletes` | UC2 step 7 |
| Integration | `Cli_ExportIncludesCompletedAndStillOpenRows` | YR12 |
| Integration | `Cli_Reapply_CompletesCompletedRows_AndReinsersOpenOnes_AndIsIdempotentTwice` | YR12, K15 |
| Integration | `Cli_Reapply_EmptyResultOrUnregisteredScope_ExitsNonZero` | YR12 loud failure |
| Integration | `DataDeletionRegistration_RegistersOnceWithHourlyCron_AndHonoursEnabledFalse` | K10 |
| Architecture | `DataDeletionJob_IsTheOnlyIgnoreQueryFiltersSiteInThisFeature` (allow-list entry unchanged); `DeletionEngine_WritesAuditThroughIAuditWriter_AndSendsNoMail` (no `SmtpClient`, no raw `audit_logs` insert) | §3.3, K12, BD15 |
| Performance (manual, `[INFERENCE]` thresholds) | 10k open rows: `EXPLAIN (ANALYZE, BUFFERS)` on the scan and the claim uses `ix_deletion_requests_due`; a run with an empty handler finishes < 5 s | §7.1 no N+1 |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 13.3.1-T1 | Deletion requests engine | M (1.0) | 09.2.2-T1, 09.2.6-T1, 09.7.2-T1 | 06.4.1-T1, 06.4.3-T1, 11.6.1-T1 (soft); the former 10.8.2-T1 blocker was dropped by DEC 10.14 |

Canonical row equals `../README.md` §8.1 ✅. Re-estimate: high-level M (1.0) → detailed **M (1.0)**, unchanged (`../README.md` §9: "13.1.1-T1, 13.1.2-T1, 13.3.1-T1, 13.4.2-T1 … unchanged"). Folder total 1.0 d.

### 10.1 Change requests and open items
| Id | Target | Change |
|---|---|---|
| CR-1 | 09.7.2-T1 (`IBlobStore`) | Add a prefix-delete/list member — `Task<ErrorOr<PrefixDeleteResult>> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct)` (paged, bounded, missing prefix = success) **or** `Task<ErrorOr<IReadOnlyList<string>>> ListAsync(string bucket, string prefix, int limit, string? after, CancellationToken ct)`. Required by K6/K8/K9: the `research/{userId}/{episodeId}/` sweep cannot be done by `DeleteAsync` alone (orphan objects have no DB row). Without it the handler deletes only the keys it can enumerate from DB rows and reports the rest via `research_prefix_sweep_pending`. |
| CR-2 | 11.6.2-T1 (rule file) | Load the deletion due-soon rule (`deletion_requests_due_soon > 0`); its rule set is asserted as **exactly 15**, so the rule text (owned by **06 CR-10**) and that assertion must land together. |
| CR-3 | `plans/00-overview.md` §6 | `data_deletion_requests`: additive columns `status`, `claimed_at`, `attempts`, `last_error_code`, `last_error_at`, `reapplied_at`; **no FK to `users`**; partial unique **`(user_id, scope, target_id, reason) NULLS NOT DISTINCT WHERE completed_at IS NULL`** (YR13 clarification of 13 CR-1) and the `scope`/`reason`/`status` CHECK vocabularies. |
| CR-4 | 06.2.3-T3 / 00 §6 (`platform_incidents`) | Rule how a non-OAuth platform incident is raised (sentinel `platform`, NULL `oauth_client_id`, dedupe). Until ruled, UC4 escalates by metric + log only ([OPEN]). |
| CR-5 | 09.2.3-T1 / 09 README D10 | Note that `DataDeletionJob` uses its allow-listed `IgnoreQueryFilters` call site for the batch claim because `ISystemWorkQuery`'s single pinned member returns `(Id, UserId)` pairs with no locking hint and cannot express D4's claim. No port widening is requested. |
| CR-6 | `plans/00-overview.md` §4.1 / `13-compliance.md` §6 | Note that 13.3.1-T1 also owns the operator CLI `deletions export|reapply` (11.6.1-T1:30 consumes the pair) — D4 names only `reapply`. |
| [OPEN] | 11.6.1-T1 | `restore-drill.sh` step (4) must call `deletions export --since <target-time> --out …` **before** the restore and `deletions reapply --since <target-time> --file …` after, and treat a non-zero exit as a failed check; the file currently says only "via 13's path". Hand-off, not a code change here. |
| [OPEN] | 02.5.2-T2 | Its "[OPEN] 9" asks 13.3.1-T1 to confirm the `research/` prefix sweep — **confirmed**, conditional on CR-1; the orphan-object path (02.5.2-T2:219) is covered only by the prefix listing. |
| [OPEN] | 08 §5.2 | Threshold/severity for the due-soon rule (11.6.2-T1:32 says thresholds are 08's) — no behaviour depends on it. |
| [OPEN] | 06.4.3-T1 | **Remove `channel` from `DataDeletionOptions.DefaultDeferredScopes`** when registering the `channel` `IDeletionScopeHandler`. 13.3.1-T1 ships the default `["account", "channel"]` (deviation from §7.1's `["account"]`) because no `channel` handler exists yet and the Worker must boot; until the entry is removed, K21's fail-fast does not cover `channel`. |

## 11. Rollout

1. **Order.** Migration (`<yyyyMMddHHmm>_13_DataDeletionRequests`) is additive → deploy with the Api/Worker image; the Worker then boot-validates the handler registry (K21) and registers the job (K10). Enable `DataDeletion:Enabled` only after the validator passes in staging.
2. **Staging proof (gate 13-P-ready, `../README.md` §5).** On staging: create a channel revoke request (or call `CreateAsync` through 06.4.3-T1), force `due_at` inside the window (`UPDATE … SET due_at = now() - interval '1 minute'` on staging only, recorded in the gate log), watch the hourly run complete it → `completed_at NOT NULL`, one `audit_logs` row, `deletion_requests_open` back to 0; confirm 06.4's smoke expectations (`identity`/`external_post_id`/`platform_payload` scrubbed, rows kept — K22); then a request due in 23 h makes `deletion_requests_due_soon` = 1 and the monitoring rule (06 CR-10) fires once, with no e-mail sent. The README's acceptance is exactly "deletion job live on staging with a channel request completed before `due_at`".
3. **Restore drill.** Run 11.6.1-T1's drill once with the export/reapply pair wired ([OPEN] row above) and record the re-applied count, the still-open rows re-inserted, and the exit codes; the drill's own acceptance already requires the twice-run idempotency (K15).
4. **Rollback.** Set `DataDeletion:Enabled=false` (the registration is removed on restart; rows stay untouched) or roll the image back — the migration is additive so a rollback never drops evidence; pending rows wait, and the due-soon metric keeps firing so the duty is not silently lost.
5. **Docs/handover.** `docs/runbooks/` (11.6.1-T1's file) gains the export/reapply pair; the ops dashboard gains `deletion_requests_open`/`_due_soon` (11.6.2-T1 + CR-2); `plans/00-overview.md` §6 gets CR-3. No template or e-mail handover exists — the engine sends no mail (UC3).
