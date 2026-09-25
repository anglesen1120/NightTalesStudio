# 06.4 — Revocation, token health and channel-scope deletion (YouTube publishing, personal phase P)

> Contract: [`../README.md`](../README.md) D14, D15, D16, D17, D18, D19, D20; C11, C12; §5.4 data-duty timers; §7 gates `06-connect` / `06-publish`. High-level: [`../../06-youtube-posting.md`](../../06-youtube-posting.md) 06.4.1–06.4.3, §5.4, §8. Companion: [`../../13-compliance/README.md`](../../13-compliance/README.md) D4 (deletion engine), K4, K5.
> Tasks in this folder: **06.4.1-T1 (S)**, **06.4.2-T1 (M)**, **06.4.2-T2 (XS)**, **06.4.3-T1 (S)** — all P · M1. Siblings, now finalized on disk: `06.1-connect` (`06.1.2-T1`, `06.1.2-T2`), `06.2.3-upload` (`06.2.3-T1`, `06.2.3-T2`, `06.2.3-T3`). This revision reconciles every port/signature against those shipped files, superseding earlier [OPEN]/[INFERENCE] guesses.
> Legend: ✅ read from source/docs (file + line) · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In.** The three duties that close a channel's lifecycle: (a) in-app disconnect — Google revoke **first**, tokens wiped, pending publications cancelled with quota released only where a unit was actually reserved, a 7-day `data_deletion_requests` row, outbox + audit (06.4.1-T1); (b) `TokenHealthJob`, the daily allow-listed system job that detects a Google-page revoke (`invalid_grant` → `revoked` + 30-day deletion request) and re-verifies or drops stored Authorized Data older than 25 days so the 30-day duty is met (06.4.2-T1); (c) the token-health platform-incident path per `oauth_client_id` — a broken *our* client never revokes a user, never creates a deletion request; it only calls the shared incident store, which does the rest (06.4.2-T2); (d) the `IDeletionScopeHandler` for `scope = channel`, consuming `ChannelConnection.PurgeYouTubeData` (06.4.3-T1).

**Out (other folders, same plan).** Connect/callback; the `ChannelConnection` aggregate in full, including `Revoke`, `MarkRefreshed`, `RefreshFromApi` and `PurgeYouTubeData` (all four declared and domain-tested by **06.1.2-T2**, consumed here); `ITokenService` (declared+implemented by 06.1.2-T2); `IYouTubeClient` including `RevokeAsync` (declared by 06.1.2-T1, implemented in its `GoogleYouTubeClient.cs`); the pinned-client resolver (06.1.2-T1); the publication aggregate, consent, cancel-by-link (06.2.1-T1, 06.2.2-T1, 06.2.6-T1); `IQuotaLedger` (06.3.1-T1); `IPlatformIncidentStore` + `platform_incidents` + `_06_PlatformIncidents` + the upload-side lazy hold inside `PublishYouTubeHandler` (**06.2.3-T2**, CR-9 moved the table here from T3); `OAuthErrorClassifier`, `ClearPlatformIncidentCommand`, the `nts-worker incident clear` CLI verb, the `platform_incident_open`/`platform_incident_cleared_total` metrics and the CR-12 alert rule (**06.2.3-T3**); the processing poller and go-live job (06.2.5-T1); the admin quota view and flag rollout rules (S).

**Out (other plans).** `IDeletionRequests`, `IDeletionScopeHandler`'s registry, `DataDeletionJob` (hourly, batch ≤ 20), the completion audit `account.data_deleted`, the metrics `deletion_requests_open`/`deletion_request_overdue_total`, the due-date alert and the restore re-apply — all owned by **13.3.1-T1** (13 README D4:48, F3:36, K5:27); the `account` scope (13.3.2, S).

**Non-goals.** No deletion of videos from the user's channel, ever (high-level 06 §4 06.2.6: "After upload the UI links to YouTube Studio"); no disconnect of a channel on the user's behalf after the audit; no per-publication realtime frame on the disconnect cascade (§5.3); no second alert channel, no second deletion engine, no second incident store, no second refresh path, no second classifier.

**Phase and flag.** P · M1. Every route is behind `RequireFeature("youtube_publish")` → 404 `feature_disabled` when off (README D1; 09.9.2-T2:9, :73-83). Recurring jobs register only while the flag is on (same pattern as 02.8.3-T1). The **deletion engine is 13's and keeps running with the flag off** — a request already created must still be executed (§11).

**Reuse rule.** One e-mail path (`IEmailSender.QueueAsync`, 09.2.6-T3), one flag path (`RequireFeature`), one audit mechanism (`[Audited]`/`IAuditData`, 09.3.6-T1), one crypto path (`ITokenProtector`, 09.9.4-T1), one refresh path (`ITokenService`, 06.1.2-T2), one classifier (`OAuthErrorClassifier`, 06.2.3-T3), one incident store (`IPlatformIncidentStore`, 06.2.3-T2), one deletion engine (13.3.1-T1).

## 2. Confirmed facts

| Id | Fact | Evidence |
|---|---|---|
| R1 | Revoke = `POST https://oauth2.googleapis.com/revoke` with the token, form-encoded | ✅ 06 README G9 (`developers.google.com/identity/protocols/oauth2/web-server`) |
| R2 | In-app revoke → revoke now + delete within **7 calendar days**; Google-page revoke → **30 days**; stored Authorized Data ≤ **30 days** then refresh or delete | ✅ 06 README G13; high-level 06 §5.4 |
| R3 | `data_deletion_requests(user_id, scope account/channel/episode, reason user_request/in_app_revoke/google_revoke/account_delete, requested_at, due_at, completed_at)` | ✅ 13 README K4:26; 00 §6 |
| R4 | `IDeletionRequests.CreateAsync(userId, DeletionScope scope, Guid? targetId, DeletionReason reason, ct)` computes `due_at` (7 d for `in_app_revoke` / `user_request` / `account_delete`; **30 d** for `google_revoke`) and is idempotent per `(scope, target_id, reason)` while open (partial unique index) | ✅ 13 README D4:48 |
| R5 | `IDeletionScopeHandler { DeletionScope Scope; Task<ErrorOr<DeletionResult>> ExecuteAsync(DeletionRequest r, ct) }`, registered by the owning plan; the `channel` handler is **06.4.3-T1** | ✅ 13 README D4:48, F4:37 |
| R6 | `DataDeletionJob` hourly (allow-listed system job, batch ≤ 20, `FOR UPDATE SKIP LOCKED`, per-row `SystemContext(user)`) executes due-first, idempotently; success → `completed_at`, audit **`account.data_deleted`** (`support`, `user`), metrics **`deletion_requests_open`**, **`deletion_request_overdue_total`**; alert when any open request has `due_at − now < 24 h` (an 11.6.2-T1 rule, evaluated by the monitoring backend — no `alerted_at` column needed, the same shape as 06.2.3-T3's `platform_incident_open > 0 for 5m` rule); restore re-apply `nts-worker deletions reapply --since <PITR timestamp>` | ✅ 13 README D4:48 (full text) |
| R7 | `platform_incidents(id, platform, oauth_client_id, error_code, raised_at, cleared_at, cleared_by, note)`, partial unique `ux_platform_incidents_open (platform, oauth_client_id) WHERE cleared_at IS NULL`; table + entity + migration `_06_PlatformIncidents` + `IPlatformIncidentStore` + the hold transition `Publication.HoldForIncident()` are **06.2.3-T2's** (README D20 originally named T3; moved by **06.2.3 CR-9** because T2's own job must read the table and T3 depends on T2); the classifier, the CLI clear verb, the metrics and the alert rule are **06.2.3-T3's** | ✅ `../06.2.3-upload/plan.md:45, 95` (CR-9); `../06.2.3-upload/06.2.3-T3-oauth-error-classifier-incidents.md:15, 86-88` |
| R8 | `ChannelConnection` (`src/NightTales.Domain/Publishing/ChannelConnection.cs`) is declared **in full by 06.1.2-T2**, including all four members this folder consumes: `Revoke(now)`, `MarkRefreshed(accessTokenEnc, refreshTokenEnc, tokenExpiresAt, now)`, `RefreshFromApi(displayName, now)`, `PurgeYouTubeData(now)` (requires `Status == Revoked`, throws otherwise; nulls both tokens + `ExternalAccountId` + `DisplayName` — **no placeholder value**, both identity columns are `text NULL`); `Status ∈ Active/Revoked/Expired/Error`; no FK to `users` | ✅ `../06.1-connect/06.1.2-T2-channel-connection.md:24, 42-64`; `agent://Y06Connect` 2026-09-25 |
| R9 | Outbox payloads: `channel_connection_changed.v1 { connectionId, userId, status }`; `platform_incident_raised.v1 { incidentId, platform, oauthClientId, affectedConnections }` **and** the audit `publishing.incident_raised` are **both written inside `IPlatformIncidentStore.RaiseAsync` itself, only when it created the row** (**README §10 YR25**) — neither the upload path nor this folder's 06.4.2-T2 writes either one; `platform_incident_cleared.v1` (written by 06.2.3-T3's `ClearPlatformIncidentCommand`) | ✅ 06 README D13, D17; README §10 YR25 |
| R10 | Consumers: 02.8.3-T4 `ChannelConnectionChangedHandler` (pause reason `channel_not_active`, code 6) and `PlatformIncidentRaised`/`PlatformIncidentCleared` handlers (`oauth_client_incident`, code 7, matched to the authorization's `channel_connection_id`); `platform_incident_cleared.v1` needs **no** consumer in this folder — 06.4.2-T1 re-derives "client is healthy again" live from `HasOpenIncidentAsync` on its next daily run, so no handler and no persisted skip-state exist to clear | ✅ `../02-story-writing/02.8.3-cadence-publishing/02.8.3-T4-…:28-29`; `../06.2.3-upload/plan.md:214` (this folder reads that line's "06.4.2-T1 resumes the client" as the implicit, stateless resume above, not a registered handler) |
| R11 | 11.6.2-T1 owns the collector, dashboards, alert rules and delivery; its base 15 rules' only YouTube rule is #10 (quota > 80 %); `platform_incident_open{platform,oauth_client_id}` is a **16th rule requested by 06.2.3 CR-12**, not yet loaded — until then the incident is still correctly stored/cleared, only the page is missing | ✅ `../06.2.3-upload/06.2.3-T3-…:86-87`; `../../11-worker-deployment/11.6-operations/11.6.2-T1-…:15, 30` |
| R12 | Cross-user sweeps read `(id, user_id)` pairs through `ISystemWorkQuery` and process each under `ISystemContext.For`; `IgnoreQueryFilters(["tenant"])` is allow-listed only for `RetentionSweepJob`, `DataDeletionJob`, `CreditReconciliationJob`, `SystemWorkQuery`. `ISystemWorkQuery` already exists and is consumed the same way by `PublishYouTubeHandler` (06.2.3-T2) — this folder extends it, it does not invent a parallel query class | ✅ `../../09-backend-api/README.md:250-256` (D10); `../06.2.3-upload/06.2.3-T2-publish-youtube-job.md:52` |
| R13 | Google HTTP goes through `GoogleYouTubeClient` (`src/NightTales.Infrastructure/Publishing/GoogleYouTubeClient.cs`, **created and owned by 06.1.2-T1**, no `Google/` subfolder) with base URLs from `YouTubeOptions` so `FakeGoogleServer` replaces them; `IYouTubeClient` is **declared once, in full, by 06.1.2-T1** — 06.2.3-T1 adds the upload members to the same interface, 06.4.1-T1 adds no member and no file, it only implements the body of the already-declared `RevokeAsync` | ✅ `../06.1-connect/06.1.2-T1-connect-callback.md:44-57, 280` |
| R14 | `videos.list` = 1 unit of the **10,000/day general bucket**; `videos.insert` has its own 100/day bucket; `status.uploadStatus ∈ deleted, failed, processed, rejected, uploaded`; a `videos.list` for an unknown id returns `items: []` (not an error) | ✅ 06 README G1, G6 |
| R15 | Staging runs a Google **Testing** project ⇒ refresh tokens expire in 7 days, so a staging connection will be detected as `invalid_grant` weekly and raise a 30-day deletion request — expected, not a bug | ✅ 06 README G10; `../06.1-connect/plan.md` §7 |
| R16 | Publication cascade targets only `awaiting_consent`/`scheduled`; quota release uses `IQuotaLedger.ReleaseAsync(reservation)` and must be gated on `platform_payload.quotaReservedDay` being present — a `scheduled` row a job never picked up holds **no** reserved unit, and releasing unconditionally would corrupt `units_used` | ✅ 06 README D15, D10; `../06.2.3-upload/plan.md:71` (`quotaReservedDay`, "06.2.6-T1's cancel path releases only when the key is present") |
| R17 | `IPlatformIncidentStore` (`Application/Abstractions/Publishing/`, declared+implemented by **06.2.3-T2**): `Task<ErrorOr<PlatformIncidentRaiseResult>> RaiseAsync(string platform, string oauthClientId, string errorCode, DateTimeOffset now, ct)` → `PlatformIncidentRaiseResult(Guid IncidentId, bool IsNew, int AffectedConnections)` (idempotent, writes the outbox row itself when `IsNew`, R9); `Task<ErrorOr<Guid?>> HasOpenIncidentAsync(string platform, string oauthClientId, ct)` — **null = no open incident; non-null is that incident's id**, which is what `Publication.HoldForIncident(incidentId, nextAttemptAt)` needs (verified in the shipped `../06.2.3-upload/06.2.3-T2-publish-youtube-job.md` §Incidents and spot-checked by README §11's DoR note; YR27's own published text covers upload-job guards, not this signature); `Task<ErrorOr<PlatformIncidentClearResult>> ClearAsync(Guid incidentId, string @operator, string note, DateTimeOffset now, ct)` — consumed only by 06.2.3-T3's `ClearPlatformIncidentCommand` (**YR16**'s `--operator`), never by this folder. `RaiseAsync` opens no transaction of its own: it enlists the incident row, the outbox row and the audit row in the **caller's ambient `IUnitOfWork`**, so a raise from this folder's 06.4.2-T2 commits with that row's transaction and stands alone (06.4.2-T2 never calls `HoldForIncident`). · `OAuthErrorClassifier` (**06.2.3-T3**, `Application/Features/Publishing/Incidents/`): `static OAuthErrorClassification Classify(YouTubeFailure failure)`, `YouTubeFailure(string? GoogleReason, int? HttpStatus, bool IsTransport)`, `OAuthErrorClassification(OAuthErrorOutcome Outcome, string ErrorCode, string? GoogleReason)`, `enum OAuthErrorOutcome { Transient, TokenRevoked, PlatformIncident, Quota, Validation }`. Names agreed directly with `agent://Y06Revocation` per 06.2.3-T3's own Notes | ✅ `../06.2.3-upload/06.2.3-T3-oauth-error-classifier-incidents.md:15, 24-33, 144` |
| R18 | The D14 hold is **lazy, per row, by design (README §10 YR24)**: any job that owns a publication and has the row locked (upload, poll, go-live) finds the open incident via `HasOpenIncidentAsync`, then calls `Publication.HoldForIncident(incidentId, now + TimeSpan.FromSeconds(5 × Poll:IntervalSeconds))` — setting `platform_payload.heldByIncidentId` and a **deferred `next_attempt_at` (never `null`)** on **only that one row**. Every other publication of the same client is held independently by its own job run; there is **no raise-time batch sweep anywhere**. The clear command (06.2.3-T3) is the only sweep and reschedules `scheduled` **and `processing`** rows carrying that id via `Publication.ReleaseHold(now)` (≤ 200/batch, tracked rows so the outbox interceptor emits `publication_updated.v1`). Absent a clear, the deferred `next_attempt_at` makes each row re-check on its own — that is the "lazy" part. 06.4.2-T2 therefore performs no hold sweep and never calls `HoldForIncident`; its raise stands alone. Earlier revisions recorded this as an open contradiction between 06.2.3-upload's own files; **YR24 settles it** | ✅ README §10 YR24, YR16; `../06.2.3-upload/06.2.3-T2-publish-youtube-job.md:97` |
| R19 | `ITokenService.GetAccessTokenAsync(Guid connectionId, ct) → ErrorOr<ChannelAccessToken(AccessToken, ExpiresAt)>` (declared+implemented by **06.1.2-T2**, `src/NightTales.Infrastructure/Publishing/TokenService.cs`, Scoped, registered in both Api and Worker roots): `SELECT … FOR UPDATE` + re-read after the lock; `token_expires_at ≥ now + 5 min` → unprotect and return without any Google call; otherwise `IYouTubeClient.RefreshAsync` → on success calls `connection.MarkRefreshed(...)` **itself** (the caller never calls `MarkRefreshed` directly) and returns the new token; `invalid_grant`/client-broken failures propagate as a typed failure so the caller applies its own mapping; a `revoked` connection fails without calling Google | ✅ `../06.1-connect/06.1.2-T2-channel-connection.md:28-37, 121-123, 165-167` |

## 3. Domain and data

### 3.1 Aggregates

**This folder declares no domain member.** `ChannelConnection` (`src/NightTales.Domain/Publishing/ChannelConnection.cs`) is declared and domain-tested in full by 06.1.2-T2 (R8), including the four members this folder's tasks call:

```csharp
public void Revoke(DateTimeOffset now);                                          // consumed by 06.4.1-T1
public void MarkRefreshed(string accessTokenEnc, string refreshTokenEnc,
                          DateTimeOffset tokenExpiresAt, DateTimeOffset now);     // called internally by ITokenService; not called directly by 06.4.2-T1
public void RefreshFromApi(string displayName, DateTimeOffset now);              // consumed by 06.4.2-T1 (25-day data refresh)
public void PurgeYouTubeData(DateTimeOffset now);                                // consumed by 06.4.3-T1
```

`Revoke`: `Status = Revoked`, `RevokedAt = now`, both token columns `NULL`, `TokenExpiresAt = now`; throws when already `Revoked` (the caller decides idempotency, per 06.4.1-T1 UC1 step 3). `MarkRefreshed` writes the rotated pair, `Status = Active`, `LastVerifiedAt = now`, clears `RevokedAt`; it throws on a `Revoked` row and is invoked **only** from inside `ITokenService.GetAccessTokenAsync` (R19) — no task in this folder calls it directly. `RefreshFromApi` writes `DisplayName` and `LastVerifiedAt = now` only, from the 25-day Authorized-Data check (06.4.2-T1's own logic, unrelated to token refresh). `PurgeYouTubeData` requires `Status == Revoked` (throws otherwise) and nulls both tokens plus `ExternalAccountId`/`DisplayName` — both columns are `text NULL`, so there is no placeholder string anywhere in this folder's design.

State machine (arrows this folder's jobs/commands trigger, all via the members above — never a direct column write):

| From | To | Trigger | Owner | Side effects |
|---|---|---|---|---|
| `active`/`expired`/`error` | `revoked` | `DELETE /channels/{id}` | 06.4.1-T1 | tokens nulled, publications cancelled (quota released only where reserved), 7-day deletion request, outbox, audit |
| `active`/`expired`/`error` | `revoked` | refresh fails `invalid_grant`/`TokenRevoked` in `TokenHealthJob` | 06.4.2-T1 | tokens nulled, 30-day deletion request (`google_revoke`), outbox |
| `expired`/`error` | `active` | refresh succeeds inside `ITokenService` (called by `TokenHealthJob`) | `ITokenService` (06.1.2-T2), triggered by 06.4.2-T1 | rotated tokens, `last_verified_at = now` (06.4.2-T1 does not call `MarkRefreshed` itself) |
| any | unchanged | classifier outcome `PlatformIncident` | 06.4.2-T2 | **no** status change, **no** deletion request; `IPlatformIncidentStore.RaiseAsync` called, refresh skipped for the rest of the client group |
| `revoked` | `active` | user reconnects the channel | 06.1.2-T2 (`Reconnect`) | new tokens, same row, `revoked_at = null` |
| `revoked` | (identity nulled) | `DataDeletionJob` executes a `channel` request | 06.4.3-T1 (`PurgeYouTubeData`) | tokens + identity nulled; row stays `revoked` |

Publication cascade (`Publication.Cancel(...)`, owned by 06.2.6-T1, called here in bulk): `awaiting_consent` | `scheduled` → `cancelled`. `uploading` | `processing` | `published` | `failed` are **never** touched by a disconnect (no video deletion on the user's channel, 06.2.6).

### 3.2 Tables

**This folder adds no table and no column: no migration** (README D20's original mapping of `_06_PlatformIncidents` to 06.2.3-T3 was superseded by **06.2.3 CR-9**, which moves the table + entity + migration to **06.2.3-T2**; `_06_ChannelConnections` is 06.1.2-T2's, `_06_Publications` is 06.2.1-T1's, `_06_PlatformQuotaUsage` is 06.3.1-T1's, the deletion tables are 13.3.1-T1's). Everything below is **read/written through existing tables only**.

`channel_connections` (06.1.2-T2, R8) — the columns this folder reads or writes:

```sql
-- read: id, user_id, platform, oauth_client_id, status, last_verified_at
-- write (via aggregate methods only, never raw SQL): access_token_enc, refresh_token_enc,
--        token_expires_at, status, revoked_at, display_name, external_account_id
--        (both identity columns are `text NULL` — PurgeYouTubeData writes NULL, not a placeholder)
```

`data_deletion_requests` (13.3.1-T1 owns the migration + `target_id` + the partial unique `(scope, target_id, reason) WHERE completed_at IS NULL`, 13 README D4 + CR-1) — this folder inserts only through `IDeletionRequests.CreateAsync` (never raw SQL) and reads nothing.

`platform_incidents` (**06.2.3-T2**, R7/R17: `id, platform, oauth_client_id, error_code, raised_at, cleared_at, cleared_by, note`, partial unique `ux_platform_incidents_open (platform, oauth_client_id) WHERE cleared_at IS NULL`) — 06.4.2-T2 writes only through `IPlatformIncidentStore.RaiseAsync`/`HasOpenIncidentAsync`, never raw SQL and never its own entity.

`publications` (06.2.1-T1) — the disconnect cascade sets `status = 'cancelled'` for rows of the connection with `status IN ('awaiting_consent','scheduled')`; 06.4.3-T1 scrubs `external_post_id` and `platform_payload` for all rows of the connection.

**Indexes / query shapes.**

| Query | Predicate | Index used |
|---|---|---|
| cascade cancel | `publications WHERE channel_connection_id = @c AND status IN ('awaiting_consent','scheduled') FOR UPDATE` | `publications (channel_connection_id)` (README D18) |
| deletion scrub | `publications WHERE channel_connection_id = @c` (batched ≤ 500, no `OFFSET`) | `publications (channel_connection_id)` |
| token-health sweep | `channel_connections WHERE status IN ('active','expired') ORDER BY last_verified_at NULLS FIRST LIMIT @n` (extends `ISystemWorkQuery`, R12) | sequential scan in P — ≤ `TokenHealth:MaxConnectionsPerRun` (500) rows and one owner in P; a `(status, last_verified_at)` index is deferred to S ([OPEN] §10) |
| publication cancel in the engine | 13.3.1-T1's own indexes | — |

**Expand-only.** No DDL from this folder ⇒ nothing to apply before its deploy; the tables it consumes are already applied by their owners' expand-only migrations (§11).

### 3.3 Invariants (domain-enforced, tested)

1. A `revoked` connection has `access_token_enc IS NULL AND refresh_token_enc IS NULL AND revoked_at IS NOT NULL`. No refresh, upload or revoke path ever reads a revoked row's tokens.
2. Revocation and the deletion request commit in the **same transaction**: the observable states are "untouched" or "revoked + request"; never "revoked without a request".
3. `data_deletion_requests` is idempotent per `(scope, target_id, reason)` while open (13 D4) ⇒ a repeated `DELETE /channels/{id}` or two token-health runs never open a second request for the same connection.
4. The disconnect cascade releases a quota unit **only** for a publication whose `platform_payload.quotaReservedDay` is present; `units_used` never goes below 0 (06.3.1-T1 guards the decrement, and this folder never releases speculatively).
5. An open incident for a client ⇒ (a) no connection of that client changes status, (b) no deletion request is created for it, (c) `TokenHealthJob` performs **no refresh call** for that client for the whole run (checked once per client, before the first row — not per row).
6. `invalid_grant`/`TokenRevoked` is a **user-side** fact: it revokes and starts the 30-day timer. `PlatformIncident` is **our** fault and must never revoke (README D14/D16, high-level §8 risk row).
7. The 25-day data refresh only ever **narrows** stored YouTube data (title refreshed; a vanished video's id nulled) — it never deletes a publication row (13 §5.4 "keeps publication rows").
8. Every log line, audit row and alert carries ids, statuses, counts and error codes only — never a token, a channel title or a video title (README D17).
9. `PurgeYouTubeData` is always called on a `Revoked` row: either the row was already `revoked` (06.4.1-T1/06.4.2-T1's normal disconnect paths), or — on a restore re-apply against a row a PITR restore returned to `active` — the `channel` scope handler revokes it first, then purges, in the same call (**YR12**). The handler, not the method's precondition, owns that sequencing decision.
10. A held publication carries `platform_payload.heldByIncidentId` and a **deferred `next_attempt_at`**, never `null` (**YR24**): the hold is applied lazily, per row, only by the job that owns that row and has it locked (`HoldForIncident(incidentId, now + 5 × Poll:IntervalSeconds)`). This folder performs **no** hold sweep and never calls `HoldForIncident`; only 06.2.3-T3's clear command sweeps, rescheduling `scheduled` **and `processing`** rows via `ReleaseHold(now)`.
11. A token-health raise writes exactly **one** `platform_incidents` row, one `platform_incident_raised.v1` outbox row and one `publishing.incident_raised` audit row — all three written by `RaiseAsync` itself on `IsNew` (**YR25**), in the caller's ambient transaction; a repeat raise within the same open incident writes none of them.

## 4. Use cases

### UC1 `DisconnectChannelCommand` — `src/NightTales.Application/Features/Publishing/Channels/DisconnectChannel/{DisconnectChannelCommand,DisconnectChannelHandler}.cs`

`DELETE /channels/{id}` — bearer, flag, rate policy `default`; no request body; no `If-Match` (README D15: the operation is idempotent by design, so a conditional header would add a failure mode without adding safety).

| Step | Behaviour | Failure |
|---|---|---|
| 1 | `RequireFeature("youtube_publish")` | 404 `feature_disabled` |
| 2 | Load the connection through the tenant filter (`ChannelConnection : IUserOwned`) with a `SELECT … FOR UPDATE` row lock (**YR11**: "disconnect also locks the row") | 404 `not_found` (never 403) |
| 3 | If already `revoked`: ensure a request exists for `(Channel, connectionId, InAppRevoke)` (idempotent `CreateAsync`, R4) and return 204 — no Google call, no second outbox row, no audit row | 404 `not_found` if the row is gone |
| 4 | `TryUnprotect(connectionId.ToString(), refresh_token_enc)` (R8's purpose convention); fall back to the access token when the refresh token is null (a partially revoked row) | an unprotectable token is unrecoverable, so the flow continues at step 6 with `googleRevoke = "skipped"` [INFERENCE] |
| 5 | `IYouTubeClient.RevokeAsync(token, ct)` — **no client credentials in this call** (R13: the signature is `RevokeAsync(string token, ct)`, declared once by 06.1.2-T1); token in the **form body**, never a query string. Retry 3× with backoff 250 ms / 1 s / 4 s + 0–20 % jitter on transport/5xx; `RevokeOutcome.AlreadyRevoked` (Google's 4xx `invalid_token`) counts as success | after the third failure: log + metric + `googleRevoke = "failed"`, **continue** (D15: the tokens are wiped anyway and the grant dies with the deletion) |
| 6 | `connection.Revoke(now)` (both ciphertexts nulled, `status = revoked`, `revoked_at = now`) | — |
| 7 | Cascade: select the connection's `awaiting_consent`/`scheduled` publications `FOR UPDATE`; for each, `Publication.Cancel(now)`; release a quota unit through `IQuotaLedger.ReleaseAsync` **only when `platform_payload.quotaReservedDay` is present** on that row (invariant 4/R16 — a `scheduled` row `PublishYouTubeJob` never picked up holds no reservation) | — |
| 8 | `IDeletionRequests.CreateAsync(userId, DeletionScope.Channel, targetId: connectionId, DeletionReason.InAppRevoke, ct)` → `due_at = now + 7 d` (R4) | throws on a DB failure → the whole transaction rolls back |
| 9 | Outbox `channel_connection_changed.v1 { connectionId, userId, status: "revoked" }` (R9) | — |
| 10 | Audit `publishing.channel_disconnected`, `data = { platform, oauthClientId, externalAccountId, publicationsCancelled, quotaUnitsReleased, googleRevoke }` (no titles, no tokens) | — |
| 11 | `204 No Content` | 401 `unauthorized`, 404 `feature_disabled`/`not_found`, 429 `rate_limited`, 503 |

Idempotency: steps 3–10 are re-entrant; the deletion request and the status transition are the two guarded writes (invariants 2–3). One transaction (`IUnitOfWork`), one audit row, one outbox row per call that changes something. Concurrency: the connection row is read `FOR UPDATE` (step 2), so a concurrent `POST /channels/youtube/connect` (`Reconnect`) serialises against the disconnect.

### UC2 `TokenHealthJob` — `src/NightTales.Worker/Jobs/Publishing/TokenHealthJob.cs` + runner

Recurring daily, stable id `token-health`, queue `publish`, allow-listed system job (the 13.3.1-T1 `DataDeletionJob` pattern, R6). `[JobPolicy(Retries = 2)]` on transport-level failure of the whole run; per-row failures never fail the run.

1. **Sweep read** — an extension to `ISystemWorkQuery` (R12; the exact member is added here, e.g. `GetYouTubeConnectionsForHealthAsync(limit, ct)`) returns `(id, userId, oauthClientId)` for `status IN ('active','expired')` ordered by `last_verified_at NULLS FIRST`, `LIMIT TokenHealth:MaxConnectionsPerRun` (500, D16). Ids only; **no** `IgnoreQueryFilters` in this folder (`ISystemWorkQuery` is already allow-listed, R12) — `expired` rows are included deliberately: an upload-side 401 sets `expired` (06.2.3-T2), and excluding them would leave the 30-day duty unmet for a connection nobody has retried.
2. **Group by `oauthClientId`.** Before the first row of a group: `IPlatformIncidentStore.HasOpenIncidentAsync("youtube", clientId, ct)` → a non-null incident id skips the whole group for the rest of the run (invariant 5, and 06.4.2-T2's Design).
3. **Per row, own DI scope under `ISystemContext.For(userId)`, own transaction:**
   - `ITokenService.GetAccessTokenAsync(connectionId, ct)` (R19) — this call transparently refreshes when the local `token_expires_at` has passed (which it normally has, given a daily cadence against ~1-hour access tokens) and, on success, rotates the tokens and sets `Status = Active` **inside `ITokenService` itself**; this job does **not** call `MarkRefreshed`;
   - success ⇒ the row becomes due for the Authorized-Data refresh (step 4);
   - failure ⇒ classify. The exact mechanism by which `GetAccessTokenAsync`'s `ErrorOr` failure exposes a `YouTubeFailure` (Google reason + HTTP status) for `OAuthErrorClassifier.Classify` to consume is **[OPEN]** — confirm with 06.1.2-T2's owner whether the error carries structured metadata or a pre-classified code; whichever it is, this task classifies and never re-implements the reason table (R17: one classifier, no second switch);
   - `Classify(...).Outcome == TokenRevoked` ⇒ `connection.Revoke(now)` + `IDeletionRequests.CreateAsync(userId, DeletionScope.Channel, targetId: connectionId, DeletionReason.GoogleRevoke, ct)` (`due_at = now + 30 d`) + outbox `channel_connection_changed.v1 { status: "revoked" }` + audit `publishing.channel_revoked_google` (**confirmed in D17 by README §10 YR21**, R9) + metric;
   - `Classify(...).Outcome == PlatformIncident` ⇒ **06.4.2-T2's branch** (call `RaiseAsync`, skip the client for the rest of the run — no revoke, no request). No hold sweep either (**YR24**): the hold is lazy and per row, applied by whichever job owns the publication and holds its lock (upload, poll, go-live);
   - any other outcome / transport failure ⇒ leave the row untouched, count a metric, retry tomorrow.
4. **Authorized-Data refresh (30-day duty, 25-day trigger, D16).** For every row whose `last_verified_at < now − 25 d`:
   - `IYouTubeClient.GetMyChannelAsync(accessToken, ct)` (1 unit, general bucket, R14) → `connection.RefreshFromApi(channel.Title, now)`; a returned channel id that disagrees with the stored `ExternalAccountId` ⇒ `connection.MarkError(now)`, skip the video refresh [INFERENCE];
   - `IYouTubeClient.GetVideosAsync(accessToken, ids, ct)` for the connection's `publications.external_post_id`s in batches of ≤ 50 (1 unit per batch, R14) → write the returned `uploadStatus` into `platform_payload`; a video absent from `items` **or** with `uploadStatus = deleted` ⇒ `external_post_id = NULL`, row kept (invariant 7);
   - a 429/`quotaExceeded` or a 5xx on either call ⇒ skip that connection's refresh, **null nothing**, count a metric, retry tomorrow; never touches `IQuotaLedger` (different Google bucket, D10/R16).
5. **Run summary** — one log line and one metric series per run: rows considered / refreshed / revoked / skipped-by-incident / failed, counts and ids only (README D17). Run budget: `TokenHealth:MaxConnectionsPerRun` (500), per-call HTTP timeout `YouTube:Http:TimeoutSeconds` = 15 [INFERENCE key, same as `06.1-connect`].

`MarkError(now)` is used for an unclassified, non-retryable condition (e.g. the channel-id mismatch above) so the row stops being attempted every day [INFERENCE].

`platform_incident_cleared.v1` needs no handler in this folder (R10): exclusion of a broken client is derived **live**, every run, from `HasOpenIncidentAsync`, so a cleared incident is naturally picked up on the very next daily run with no persisted skip-state to invalidate.

### UC3 Incident path per `oauth_client_id` — 06.4.2-T2

Edits UC2's step 3 failure branch. Everything it calls is another folder's already-shipped contract (R17, R18), consumed verbatim, never re-declared: `IPlatformIncidentStore.RaiseAsync`/`HasOpenIncidentAsync` (06.2.3-T2), `OAuthErrorClassifier.Classify` + `YouTubeFailure` (06.2.3-T3).

1. **Pre-step, once per client group, before the first refresh:** `HasOpenIncidentAsync("youtube", clientId, ct)` returns `ErrorOr<Guid?>` (R17) — a **non-null id** skips the whole group and counts `youtube_token_health_skipped_incident_total{oauth_client_id}` (invariant 5); `null` means no open incident. An `ErrorOr` failure is treated as "no incident" but logged and counted — a store outage must not silently stop the 30-day duty [INFERENCE].
2. **On `Classify(...).Outcome == PlatformIncident`:** call `RaiseAsync("youtube", clientId, classification.ErrorCode, now, ct)`. That is the **entire** action: the store itself decides idempotency (`IsNew`), writes the `platform_incidents` row, and — **when `IsNew`, in the caller's ambient `IUnitOfWork`** (R17) — writes both the outbox `platform_incident_raised.v1` **and** the audit `publishing.incident_raised` (**YR25**). 06.4.2-T2 emits **no outbox event, no audit row and no metric of its own** for the raise; the connection's status, tokens and `revoked_at` are left **untouched** and no deletion request is created (invariant 6). The raise **stands alone**: this folder never calls `HoldForIncident`.
3. **No hold sweep here (README §10 YR24).** The D14 hold is lazy, per row, and belongs to whichever job owns the publication being processed (upload/poll/go-live): that job calls `Publication.HoldForIncident(incidentId, now + 5 × Poll:IntervalSeconds)`, setting `platform_payload.heldByIncidentId` and a **deferred `next_attempt_at` (never `null`)** on that one row only. There is **no raise-time batch sweep anywhere**; 06.4.2-T2 does not batch-update `publications`.
4. **The remainder of the run skips that client** (the group is abandoned as in step 1 above, no repeated `RaiseAsync` call for the same client this run).
5. **Clearing** is entirely 06.2.3-T3's (`ClearPlatformIncidentCommand`, the `nts-worker incident clear <id> --operator <name> --note <text>` CLI verb — **YR16**; it is the only sweep and reschedules `scheduled` **and `processing`** rows carrying `heldByIncidentId` via `Publication.ReleaseHold(now)`, ≤ 200/batch tracked rows so the outbox interceptor emits `publication_updated.v1` — YR27(4)). This folder never calls `ClearAsync`. A cleared incident is simply an absent open row on the next run (the partial unique index only covers open rows), so the group resumes automatically; absent a clear, each held row re-checks on its own thanks to the deferred `next_attempt_at`.

### UC4 `ChannelDeletionScopeHandler` — 06.4.3-T1

`src/NightTales.Application/Features/Publishing/Deletion/ChannelDeletionScopeHandler.cs` implementing `IDeletionScopeHandler` (R5), registered in the Worker composition root. Executed by 13.3.1-T1's `DataDeletionJob` under `ISystemContext.For(r.UserId)` with `r.TargetId` = the connection id. Return type is **YR7**'s: `ErrorOr<DeletionResult(int ObjectsDeleted, int RowsAffected)>` — failures are plain `Error`s, never a `Completed`/`Failed` factory.

| Step | Behaviour |
|---|---|
| 1 | Load the connection by `r.TargetId` under the tenant filter (matches because the job set `SystemContext`) |
| 2 | **Not found under the tenant filter?** **YR14**: distinguish "gone" from "not visible" with an allow-listed existence check (an additive `ISystemWorkQuery` member, no tenant filter, returns `bool` only) — truly gone (hard-deleted) → `DeletionResult(0, 0)`, a normal completion; exists but belongs to another user (not visible, a corrupted/foreign request) → an `Error`, never silently "Completed" |
| 3 | **Row visible but not `Revoked`?** **YR12** (restore re-apply against a row a PITR restore returned to `active`): `connection.Revoke(now)` first, then continue — never throw on `PurgeYouTubeData`'s precondition |
| 4 | `connection.PurgeYouTubeData(now)` — **consumed, not declared here** (06.1.2-T2, R8): nulls both token ciphertexts and `ExternalAccountId`/`DisplayName` (both `text NULL`, no placeholder) |
| 5 | Scrub every publication of the connection: `external_post_id = NULL`, `platform_payload = NULL` (batched, `publications (channel_connection_id)`). `title`, `description`, `tags`, `consent_*` and the status stay — they are **our** consent/audit record, not YouTube-derived data (13 §5.4, D15) |
| 6 | Return `DeletionResult(ObjectsDeleted: 0, RowsAffected: 1 + scrubbedPublications)` [INFERENCE mapping — this scope deletes no blob objects, unlike the `episode` handler] — the engine sets `completed_at`, writes the completion audit `account.data_deleted` and owns the due-date alert (R6) |
| 7 | Log one line with ids and counts only |

Re-entrancy: every step is an idempotent `UPDATE`, so a re-run after a failed job (11.6.1-T1 restore re-apply, R6) is safe, including the YR12 revoke-then-purge sequence on a restored row. **This task builds no alert mechanism**, but it does prove the input to the engine's mechanism is correct: a channel-scope request the engine has not completed, with `due_at` inside the 24-h window, must still read as open at the point the alert rule would evaluate it — the 24-h pre-due rule itself, its metrics (`deletion_requests_open`, `deletion_request_overdue_total`) and its dedupe are entirely 13.3.1-T1's/11.6.2-T1's (R6).

## 5. API and events

### 5.1 Routes

| Method | Path | Auth | Flag | Rate | Success | Notes |
|---|---|---|---|---|---|---|
| DELETE | `/channels/{id}` | bearer | `youtube_publish` | `default` | 204 No Content | idempotent, no `If-Match`, no body; `{id}` is the connection id (D15) |

`{id}` that is not a UUID, is another user's row, or is unknown ⇒ 404 `not_found`. No new route is added by 06.4.2-T1/T2 or 06.4.3-T1. The operator incident-clear verb is 06.2.3-T3's CLI, not a route.

### 5.2 Status codes

`204`; `401 unauthorized|reauth_required` (the latter only if a future re-auth guard is added — D15 does not require one for disconnect [INFERENCE]); `404 feature_disabled|not_found`; `429 rate_limited`; `500 internal_error`; `503 service_unavailable`. `ProblemDetails` envelope and the `errors` map are 09.1.4-T1's, unchanged (no new error code: `feature_disabled`, `not_found`, `unauthorized`, `rate_limited` already exist).

### 5.3 Events

| Kind | Type | Payload | Raised by | Consumer |
|---|---|---|---|---|
| Outbox | `channel_connection_changed.v1` | `{ connectionId, userId, status }` | 06.4.1-T1 (`revoked`), 06.4.2-T1 (`revoked`) | 02 orchestrator (`channel_not_active` pause, R10), 10 UI |
| Outbox | `platform_incident_raised.v1` | `{ incidentId, platform, oauthClientId, affectedConnections }` | **inside `IPlatformIncidentStore.RaiseAsync`** (06.2.3-T2), triggered by both `PublishYouTubeHandler` and this folder's 06.4.2-T2 calling `RaiseAsync` — the store itself writes it (**YR25**) | 02 orchestrator (`oauth_client_incident`, R10), 06.2.3-T3's alert path |
| Outbox | `platform_incident_cleared.v1` | `{ incidentId, platform, oauthClientId }` | 06.2.3-T3's `ClearPlatformIncidentCommand` | 02 orchestrator (resume), this job's next scheduled run (implicit, no handler — R10), 10 UI |
| Audit | `publishing.channel_disconnected` | `{ platform, oauthClientId, externalAccountId, publicationsCancelled, quotaUnitsReleased, googleRevoke }` | 06.4.1-T1 | 06.5.1-T1, 13.4.2-T1 |
| Audit | `publishing.channel_revoked_google` (**README §10 YR21** confirms this is in D17's list) | `{ connectionId, oauthClientId }` | 06.4.2-T1 | 06.5.1-T1 |
| Audit | `publishing.incident_raised` (already in D17) | `{ incidentId, oauthClientId, errorCode, affectedConnections }` | **inside `RaiseAsync`** (06.2.3-T2), only when `IsNew` (**YR25**) — neither 06.4.2-T2 nor `PublishYouTubeHandler` writes it themselves | 06.5.1-T1 |
| Audit | `account.data_deleted` | engine's own shape | 13.3.1-T1's `DataDeletionJob`, for every scope including `channel` | 06.5.1-T1 |
| Metrics (this folder's own, distinct from 06.2.3-T3's `platform_incident_*` and 13.3.1-T1's `deletion_request*`) | `youtube_token_health_runs_total`, `youtube_token_health_rows_total{outcome}`, `youtube_token_health_skipped_incident_total{oauth_client_id}`, `youtube_channel_revoke_failed_total` [INFERENCE names] | counters | 06.4.1-T1 / 06.4.2-T1 | 11.6.2-T1 rules (R11) — no rule requested for these in P; diagnostic only |

**No SignalR frame** is raised by the disconnect cascade: `RealtimeData` is reserved for `RenderProgress` (WT54) and D13 scopes `publication_updated.v1` to Worker handlers, so the cancelled publications are **not** pushed per publication. The user acts from `/channels` or an episode page and the client refetches; the 02 orchestrator learns about the connection change from `channel_connection_changed.v1`. Recorded as a deliberate scope decision, revisit only with a TL ruling.

## 6. UI

No UI is written in this folder; every surface is 10's. Hand-overs:

| Surface | Owner (10) | What this folder hands over |
|---|---|---|
| Channels page `/channels` (list, connect, **disconnect**) | 10 wave 2 — id not pinned [OPEN], same as `../06.1-connect/plan.md` §6 | `DELETE /channels/{id}` → 204; the `revoked`/`expired` states from `ChannelConnectionDto.status`; the disconnect confirmation flow |
| Channel status copy | 10 wave 2 | `revoked` → "Disconnected"; `expired` → "Connection expired — reconnect" |
| Publication surfaces (veto list 10.4.3-T2, editor 10.5.3-T1) | 10.4.3-T2, 10.5.3-T1 | a publication cancelled by the cascade shows `cancelled`; no realtime push (§5.3) ⇒ refetch on page entry |
| Incident banner | 06.2.3-T3's hand-over (reused, never a second copy) | `publishing_temporarily_unavailable` (D14) — shown while `platform_incidents` is open; **no** reconnect prompt |

Copy keys this folder needs (proposed, [INFERENCE] — 10 owns the final names): `channels.disconnect`, `channels.disconnect.confirm.title`, `channels.disconnect.confirm.body`, `channels.disconnect.confirm.submit`, `channels.disconnect.confirm.cancel`, `channels.disconnected` (toast), `channels.status.revoked`, `channels.status.expired`, `channels.reconnect`, `channels.disconnect.error.<code>` (mapped by 10.2.2-T1's `AppErrorMapper`), `publications.status.cancelled`. The disconnect confirmation body must state that pending YouTube posts are cancelled and that the channel's data is deleted on Google's side (III.A.2 revoke link copy is 13's, not here).

## 7. Jobs and integrations

| Job | Owner | Schedule | Queue | Batching | Bounds |
|---|---|---|---|---|---|
| `TokenHealthJob` | 06.4.2-T1 (+T2's incident-raise branch) | daily recurring, stable id `token-health`, cron from `TokenHealth:RunAtUtc` [INFERENCE key] | `publish` | `ISystemWorkQuery` extension sweep ≤ `MaxConnectionsPerRun` (500); `videos.list` ≤ 50 ids per call | per-call HTTP timeout 15 s [INFERENCE]; `[JobPolicy(Retries = 2)]` for a transport-level run failure; per row one transaction |
| `DataDeletionJob` | **13.3.1-T1** (consumed) | hourly | 13's | batch ≤ 20, `FOR UPDATE SKIP LOCKED` | R6 — 06.4.3-T1 only registers a handler |

**External calls.**

| Call | Where | Cost / quota | Timeout | Retry | Failure map |
|---|---|---|---|---|---|
| `IYouTubeClient.RevokeAsync(token, ct)` | 06.4.1-T1, Api thread (awaited) | free | 15 s [INFERENCE] | 3× (250 ms / 1 s / 4 s + jitter) on transport/5xx | exhausted ⇒ wipe anyway + metric + `googleRevoke = "failed"`; `AlreadyRevoked` ⇒ success |
| `ITokenService.GetAccessTokenAsync` | 06.4.2-T1, Worker | free | 15 s [INFERENCE] | 1 in-row retry (inside `ITokenService`) | `TokenRevoked` ⇒ revoke+30 d (06.4.2-T1); `PlatformIncident` ⇒ 06.4.2-T2's raise; transport ⇒ metric, row unchanged |
| `channels.list?mine=true` | 06.4.2-T1 | 1 unit, general 10,000/day bucket (R14) | 10 s [INFERENCE] | 2 on 5xx | skip the connection's data refresh; **null nothing**; retry tomorrow |
| `videos.list` (≤ 50 ids) | 06.4.2-T1 | 1 unit per batch, general bucket (R14) | 10 s [INFERENCE] | 2 on 5xx | 429/`quotaExceeded` ⇒ skip refresh, metric, retry tomorrow; never touches `IQuotaLedger` |
| `IDeletionRequests.CreateAsync` | 06.4.1-T1 / 06.4.2-T1 | — | — | in-transaction | failure rolls the row's transaction back (invariant 2) |
| `IPlatformIncidentStore.RaiseAsync`/`HasOpenIncidentAsync` | 06.4.2-T2 | — | — | per-call | store failure on `HasOpen` ⇒ fail-open (refresh continues, invariant preserved by not silently excluding a client forever) |

**Performance.** No N+1: the sweep is one ids-only query; the cascade is one `FOR UPDATE` select + a conditional release per cancelled row (bounded by an episode's publications); the deletion scrub is two batched updates; the data refresh is one `channels.list` + ⌈ids/50⌉ `videos.list` per due connection and runs only every ~25 days per connection. Rows are processed sequentially inside the job (a personal-phase table with ≤ 500 rows/day; no `Parallel.ForEach`) [INFERENCE].

**Interaction with the upload path.** A disconnect mid-upload: `PublishYouTubeJob` re-checks the connection at start and each Google call fails after the revoke ⇒ the publication ends `failed` with `channel_reauth_required` (06.2.3-T2's map, unchanged here); the cascade only cancels rows that had **not** started uploading.

## 8. Security and privacy

| Control | Implementation |
|---|---|
| Revoke token handling | the token goes in the `application/x-www-form-urlencoded` **body**, never a query string (R1/R13's declared signature already enforces this at the port boundary) |
| Tokens at rest | already `ITokenProtector` ciphertext; this folder only reads them to call Google and **nulls** them on revoke and on deletion (invariants 1, 9) |
| Tenant isolation | `ChannelConnection : IUserOwned` + the 09.2.1-T1 global filter ⇒ another user's channel is 404, never 403; the job reaches rows only through `ISystemWorkQuery` + per-row `ISystemContext.For` (R12); no `IgnoreQueryFilters` anywhere in this folder |
| CSRF | the route is bearer-authenticated (no cookie), so no `Origin` filter and no CSRF token is required; the callback's state machine (06.1.2-T1) is untouched |
| Idempotency / replay | disconnect is idempotent (invariant 3); revoke retries are safe (`AlreadyRevoked` = already revoked); the incident raise is idempotent through 06.2.3-T2's partial unique index, not this folder's own logic |
| Audit | `publishing.channel_disconnected`, `publishing.channel_revoked_google` (this folder's own writes) + `publishing.incident_raised` (written inside `RaiseAsync` itself, YR25 — not by this folder) + the engine's `account.data_deleted`; ids/codes/counts only; `[Audited]`/`IAuditData` on the disconnect command |
| Logs | ids, statuses, counts and Google error **codes**; never a token, a channel title, a video title, a raw Google response body, or an `Authorization` header; 09.9.3-T2 redaction is the second line |
| Alerts | this folder emits no alert of its own; the platform-incident alert (06.2.3-T3) and the deletion-overdue alert (13.3.1-T1/11.6.2-T1) carry ids and counts only, by design in their owning tasks |
| Retention duties | 7 d (in-app revoke, UC1 step 8), 30 d (Google-page revoke, UC2), 30 d Authorized-Data age with a 25-day trigger (UC2 step 4) — R2/D16; the engine's 24-h pre-due alert is the backstop |
| Incident safety | a broken OAuth client can never look like a mass user revoke: no status change, no deletion request, refresh stopped for the client (invariant 6) — the failure mode the high-level §8 risk table calls out |
| Least privilege | the revoke call needs no client credentials at all (R13); the refresh call uses the client **pinned** on the connection via `IOAuthClientResolver` inside `ITokenService`; no secret ever leaves the secret manager (C12) |
| No destructive overreach | never delete a video on YouTube, never delete a publication row, never touch an `uploading`/`processed` publication in the cascade (§1, §3.1) |

## 9. Test plan

Projects (09 README D2, canonical names): `tests/NightTales.Domain.UnitTests`, `tests/NightTales.Application.UnitTests`, `tests/NightTales.Infrastructure.UnitTests`, `tests/NightTales.Api.IntegrationTests`, `tests/NightTales.Architecture.Tests`, `tests/NightTales.TestKit`. xunit.v3 + Shouldly; integration on **real PostgreSQL via Testcontainers**; `FakeClock`; `FakeGoogleServer` (Kestrel, 06 README D19) — **no mocking library**. This folder consumes the fake's `/revoke`, `/token` (`invalid_grant`, `invalid_client`), `channels.list` and `videos.list` routes (all already extended by 06.1.2-T1/06.2.3-T1) and adds the `videos.list` "video gone / `uploadStatus = deleted`" and "429 quotaExceeded" responses (TestKit, owner 06.4.2-T1). `ChannelConnection.Revoke`/`MarkRefreshed`/`RefreshFromApi`/`PurgeYouTubeData` domain tests live in 06.1.2-T2's `ChannelConnectionTests.cs` — this folder's tests exercise them only through its own commands/jobs, never re-testing the aggregate directly.

| Test | Project | Assertion |
|---|---|---|
| `Disconnect_RevokesAtGoogleThenWipes_TokensNullInDb` | Api.IntegrationTests | 204, `FakeGoogleServer` saw exactly one `/revoke` with the token in the **body**, DB row `revoked` with null ciphertexts |
| `Disconnect_SecondCall_Returns204_NoSecondRequest` | Api.IntegrationTests | one `data_deletion_requests` row, one audit row, one outbox row total |
| `Disconnect_GoogleRevokeFailsAfterRetries_StillWipesAndAlerts` | Api.IntegrationTests | `FakeGoogleServer` returns 500 three times; DB still `revoked`; deletion request exists; metric `youtube_channel_revoke_failed_total` = 1; audit `googleRevoke = "failed"` |
| `Disconnect_GoogleSaysAlreadyRevoked_IsSuccess` | Api.IntegrationTests | no alert, no retry beyond the first, DB `revoked` |
| `Disconnect_CancelsPendingPublicationsAndReleasesQuota_OnlyWhenReserved` | Api.IntegrationTests | a `scheduled` row with `quotaReservedDay` set releases one unit; a `scheduled` row without it releases nothing; `processing`/`published` untouched |
| `Disconnect_CreatesChannelScopeRequest_DueIn7Days` | Api.IntegrationTests | `scope = channel`, `target_id = connectionId`, `reason = in_app_revoke`, `due_at ≈ now + 7 d` (`FakeClock`) |
| `Disconnect_OtherUsersChannel_Returns404` | Api.IntegrationTests | victim row untouched, no deletion request |
| `Disconnect_FlagOff_Returns404FeatureDisabled` | Api.IntegrationTests | no Google call, no state change |
| `TokenHealth_InvalidGrant_RevokesAndOpens30DayRequest` | Api.IntegrationTests | connection `revoked`, tokens null, request `google_revoke` due +30 d, outbox `channel_connection_changed.v1`, no data refresh in the same run |
| `TokenHealth_InvalidClient_NoRevokeNoRequest_ClientSkipped` | Api.IntegrationTests | two connections of the same client: the second is never refreshed (fake call count = 1), both stay `active`, zero deletion requests, one `platform_incidents` row |
| `TokenHealth_OpenIncident_SkipsClientBeforeAnyCall` (06.4.2-T2) | Api.IntegrationTests | with a pre-inserted open `platform_incidents` row: zero `/token` calls, zero `RaiseAsync` calls |
| `TokenHealth_InvalidClient_CallsRaiseAsyncOnlyOnce` (06.4.2-T2) | Api.IntegrationTests | one `RaiseAsync` call per client per run; the store's own idempotency (not this folder's) produces one row/event |
| `TokenHealth_DoesNotWriteToPublications` (06.4.2-T2) | Api.IntegrationTests | a `scheduled` publication of the broken client is bit-for-bit unchanged after the run — the hold, if any, is applied lazily by whichever job later touches that publication (**YR24**), never a sweep built here |
| `TokenHealth_InvalidClient_WritesOneAuditRowOnlyWhenNew` (06.4.2-T2) | Api.IntegrationTests | a token-health raise results in exactly one `publishing.incident_raised` row, written by `RaiseAsync` itself (**YR25**) — not by this task's code; a repeat raise within the same open incident adds none |
| `TokenHealth_ExpiredRow_RefreshSucceeds_ReturnsToActive` | Api.IntegrationTests | status `active` after the run (via `ITokenService`'s own `MarkRefreshed` call), rotated ciphertext unprotects |
| `TokenHealth_DataOlderThan25Days_RefreshesTitleAndNullsGoneVideo` | Api.IntegrationTests | `FakeClock` +26 d; `display_name` updated; an id absent from `videos.list` → `external_post_id` NULL with the row **still present** |
| `TokenHealth_DataRefreshQuotaExceeded_NullsNothingAndRetriesTomorrow` | Api.IntegrationTests | fake returns 429; title and ids unchanged; `platform_quota_usage` unchanged |
| `TokenHealth_MaxConnectionsPerRun_BoundsTheSweep` | Api.IntegrationTests | 3 candidates, config 2 ⇒ 2 processed, 1 deferred |
| `TokenHealth_ClearedIncident_NextRunRefreshesNormally` | Api.IntegrationTests | no handler needed: `HasOpenIncidentAsync` returns `null` (no open incident) the run after a clear, and that run's refresh succeeds |
| `DeletionChannelScope_PurgesTokensAndIdentity_KeepsPublicationRows` | Api.IntegrationTests | tokens NULL, `external_account_id`/`display_name` NULL, publications' `external_post_id`/`platform_payload` NULL, all rows present, `title`/`consent_at` intact; returned `DeletionResult(ObjectsDeleted, RowsAffected)` matches the mapping (**YR7**) |
| `DeletionChannelScope_RestoredActiveRow_RevokesThenPurges` | Api.IntegrationTests | **YR12**: a connection seeded `active` (restore re-apply scenario) is revoked then purged in one call |
| `DeletionChannelScope_ForeignTarget_ExistsButNotVisible_ReturnsError` | Api.IntegrationTests | **YR14**: existence check true, tenant read null → `Error`, distinct from the "gone" (hard-deleted) case which completes with `DeletionResult(0, 0)` |
| `DeletionDueAlert_ChannelScopeRequestWithin24h_StaysOpenUntilCompleted` | Api.IntegrationTests | a channel request at `due_at − 23h` still reads open at the point the engine's alert rule would evaluate it; once this handler completes it, it no longer counts as open |
| `Disconnect_AuditAndLogs_ContainNoTokenOrTitle` | Application.UnitTests | audit projection + captured log lines contain ids/codes only; a seeded token/title string is absent |
| `TokenHealth_JobTakesNoDomainType` | Architecture.Tests | `TokenHealthJob.RunAsync` takes only `CancellationToken`; no entity/DTO parameter |
| `DeletionScopeHandler_IsRegisteredForChannel` | Architecture.Tests | the handler resolves from the Worker container and its `Scope` is `Channel` (D4 fail-fast) |

**Manual (staging, gate `06-connect`/`06-publish`, README §7).** Connect a test channel → publish → `DELETE /channels/{id}` → the app no longer appears on the Google permissions page (evidence for the audit pack); the DB shows null ciphertexts, `revoked`, cancelled publications and a 7-day request; force `DataDeletionJob` → identity/`external_post_id`/`platform_payload` scrubbed, completed before `due_at`. On staging the Testing project's 7-day refresh tokens make `TokenHealthJob` revoke weekly — expected (R15), and the evidence pack records it.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.4.1-T1 | Disconnect: Google revoke, wipe tokens, cancel publications, deletion request | S (0.5) | 06.1.2-T2, 06.2.6-T1, 06.3.1-T1, 13.3.1-T1 | 06.4.2-T1, 06.4.3-T1, 10.7.1-T1 (10 CR-15), 13.3.2-T1 (13 CR-17) |
| 06.4.2-T1 | `TokenHealthJob`: refresh check, Google-page revoke, 30-day data refresh | M (1.0) | 06.4.1-T1 | 06.4.2-T2, 02.8.3-T4 |
| 06.4.2-T2 | Token-health incident path per `oauth_client_id` | XS (0.25) | 06.4.2-T1, 06.2.3-T3 | 06.5.1-T1 |
| 06.4.3-T1 | YouTube channel-scope deletion handler + due-date alert | S (0.5) | 06.4.1-T1, 13.3.1-T1 | 06.5.1-T1 |

Folder total **2.25 d**, equal to README §9's "06.4 revocation + duties 2.25 (unchanged)" — no re-estimate change is requested by this folder. The `Size`/`Depends on`/`Blocks` cells above are README §8.1's rows and are not rewritable here; each task file repeats them verbatim in its header. Ordering inside the folder is strict: 06.4.1-T1 → 06.4.2-T1 → 06.4.2-T2, and 06.4.3-T1 after 06.4.1-T1.

**Open items (genuinely remaining after reconciling against the shipped 06.1-connect and 06.2.3-upload files).**

1. [OPEN] **`YouTubeFailure` extraction from `ITokenService.GetAccessTokenAsync`'s error.** F12/R19 confirm the port and its internal `MarkRefreshed` call; they do not spell out the exact `Error` shape 06.4.2-T1 reads to call `OAuthErrorClassifier.Classify`. Confirm with 06.1.2-T2's owner whether the failure carries `(GoogleReason, HttpStatus, IsTransport)` as `Error.Metadata`, or whether `ITokenService` already pre-classifies and 06.4.2-T1 should branch on a returned code instead of calling `Classify` itself.
2. [OPEN] **Index** `channel_connections (status, last_verified_at NULLS FIRST)` is deferred to S (D18 lists only `(user_id)`; P has one owner and a bounded sweep).
3. [INFERENCE] names to confirm at first implementation step: `TokenHealth:RunAtUtc`, `TokenHealth:MaxConnectionsPerRun` = 500, `YouTube:Http:TimeoutSeconds` = 15, the metric names in §5.3, `DeletionResult`'s `ObjectsDeleted`/`RowsAffected` semantics for a non-blob scope (YR7, [INFERENCE] mapping in 06.4.3-T1), the `ISystemWorkQuery` existence-check member name (YR14).
4. [RESOLVED] All four `ChannelConnection` members, `ITokenService`, `IYouTubeClient.RevokeAsync`, the `platform_incidents` store/table ownership, and the deletion engine's audit/metric names are confirmed against the shipped `06.1-connect` and `06.2.3-upload` files. Audit action `publishing.channel_revoked_google` is confirmed by **YR21**. `DeletionResult`'s shape is fixed by **YR7**; the connection-row lock discipline on disconnect is fixed by **YR11**; restore re-apply's revoke-then-purge sequence is fixed by **YR12**; the gone-vs-foreign distinction is fixed by **YR14**. **The two CRs this folder previously filed against `06.2.3-upload` (the hold-sweep mechanism, and the `publishing.incident_raised` writer) are both closed: YR24 fixes the hold as lazy-per-row with no raise-time sweep, and YR25 fixes `RaiseAsync` as the sole writer of both the outbox event and the audit row** — 06.4.2-T2 was revised to remove its own audit write accordingly.

## 11. Rollout

1. **Before deploy.** Nothing to migrate for this folder (§3.2). The tables it needs are already applied by expand-only migrations owned elsewhere: `_06_ChannelConnections` (06.1.2-T2), `_06_PlatformIncidents` (**06.2.3-T2**, CR-9), the `data_deletion_requests` migration + `target_id` (13.3.1-T1). `YouTube:ActiveClientId` and the client secret (C12) must be set or startup validation fails. A missing `IDeletionScopeHandler` for `Channel` must fail fast at Worker start (asserted by the `Architecture.Tests`/container test, §9).
2. **Deploy order.** Api (the DELETE route) and Worker (the daily job + the handler registration) are the same release train (11.5.1-T1); the route is inert until the flag is on, and the Worker validates the handler registration at start.
3. **Flag.** `youtube_publish` ships **off**: `DELETE /channels/{id}` answers 404 `feature_disabled`, and `TokenHealthJob` is not registered (README D1). The **deletion engine keeps running regardless** — a request created while the flag was on must still be executed after it is turned off (13's job, invariant 2).
4. **Staging smoke (gates `06-connect`, `06-publish`).** Connect → publish (private) → disconnect: 204, the app gone from the Google permissions page (evidence for 06.5.1-T1), tokens NULL, `revoked`, publications `cancelled`, quota released only where reserved, one 7-day `channel` request; then force `DataDeletionJob` → identity/`external_post_id`/`platform_payload` scrubbed, rows kept, `completed_at` before `due_at`; then run `TokenHealthJob` twice on the staging Testing project's 7-day-expired connection → first run `revoked` + 30-day request, second run no duplicate request (R4/R15).
5. **Incident drill (evidence for 06.5.1-T1).** Point one staging connection's `oauth_client_id` at a deleted client, run `TokenHealthJob` → one `platform_incidents` row via `RaiseAsync`, no connection change, no request; clear the incident with 06.2.3-T3's `nts-worker incident clear <id> --operator <name> --note <text>` verb (**YR16**) → the next run refreshes normally.
6. **Prod.** Same code, prod client secret, flag on for the owner. A prod disconnect behaves identically; the 30-day duty is fulfilled by the engine, watched by its own 24-h pre-due alert.
7. **Rollback.** Turn `youtube_publish` off: the route 404s and the job unregisters within 30 s (A6 flag cache). No data unwind — already-revoked rows stay revoked (correct) and already-open deletion requests stay open (required). There is no second mechanism to disable and no schema change to reverse.
