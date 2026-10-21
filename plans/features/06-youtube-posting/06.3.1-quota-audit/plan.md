# 06.3.1 — Quota ledger, publish ETA and audit evidence pack

> Refines: `../../../06-youtube-posting.md` §4 (story **06.3.1** L91–93, story **06.5.1** L116–117); `../../../00-overview.md` §6 row `platform_quota_usage`, §8 Channels row (`GET /publishing/youtube/eta`).
> Contract: `../README.md` — **D1, D9, D10, D11, D14, D17, D18, D19, D20**; findings **F07, F22**; facts **G1, C1, C2, C5, C6, C11**; §7 gates **06-publish**, **06-audit-ready**; CR-1, CR-2, CR-6, CR-8.
> Cross-plan: `../../13-compliance/README.md` D1–D5 (legal texts, acceptance, deletion engine, filing), `../../09-backend-api/09.2.2-core-schema-migrations/plan.md` (§3 conventions), `../../09-backend-api/09.9-platform/plan.md` (`NightTalesMetrics`), `../../11-worker-deployment/11.6-operations/plan.md` (metric contract, alert `youtube_quota_used`).
> Template: `../../../00-overview.md` §10.1. Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-25. Nothing implemented yet.

## 1. Scope

**Stories.** 06.3.1 *Quota ledger* — Must · detailed **1.0 d** (T1, M). 06.5.1 *Audit evidence pack* — Must · detailed **1.0 d** (T1, M). Both **P · M1**; the YouTube feature flag `youtube_publish` gates the one new route (D1).

**In scope.**
- Table `platform_quota_usage` (00 §6), its EF configuration, the expand-only migration `_06_PlatformQuotaUsage` (D20) and the `IAppDbContext` set.
- Port `IQuotaLedger` (Application) + implementation `YouTubeQuotaLedger` (Infrastructure): atomic reserve/release of one unit on the **PT** calendar day, seeded limit from `YouTube:Quota:UnitsPerDay` (D10, F22, G1).
- `GET /publishing/youtube/eta` → `{ available, nextSlotAt }` (D10; already a 00 §8 route ✅).
- Metric `youtube_quota_used` (ratio) recorded after every reserve/release through `NightTalesMetrics.SetYoutubeQuotaUsed` (09.9.3-T1; alert wired by 11.6.2-T1 at > 80 %).
- The **audit evidence pack**: manifest, artefact index, behaviour walkthrough checklist mapped clause-by-clause to 06 README G13 (III.A/III.D/III.E) and to the policy sentences, review steps and handoff to `13.4.2-T1` (BA deliverable — files and checklists, no code).

**Out of scope (owner in brackets).**
- The upload call site that reserves and the failure map that releases: `PublishYouTubeJob` (06.2.3-T2); the cancel path that must not double-release (06.2.6-T1).
- Per-user fairness and the queue length of one user: not built (phase S dropped, DEC 10.14).
- `GET /admin/quota` used/limit/forecast: not built (phase S dropped, DEC 10.14); the ETA endpoint and the publish-form estimate banner are P here (README §9).
- Flag rollout rules: not built (phase S dropped, DEC 10.14).
- The filing itself: `13.4.2-T1` (form answers, submission, calendar entry, appeal path, 13 README D5).
- The publish form and the quota-wait copy: 10.7.2-T1 (**10 wave 2, not detailed** → copy keys handed over here, §6).
- Token health/data refresh quota accounting: 06.4.2-T1 (see §7 — one [OPEN] for the TL).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| F1 | `platform_quota_usage(platform, quota_day (date), units_used, units_limit)` — "Global YouTube quota ledger" | `../../../00-overview.md` §6 ✅ |
| F2 | One PT day bucket per platform; a unit is reserved with an atomic `UPDATE … SET units_used = units_used + 1 WHERE units_used < units_limit`; no row updated → wait for the next PT day; `units_limit` configurable, default **100**, updated after an extension | `../README.md` D10 ✅; `../../../06-youtube-posting.md:92` ✅ |
| F3 | Reservation is taken **before** `StartUploadAsync`; release only when YouTube never received the insert, or on `quotaExceeded`; `quotaExceeded`/`uploadLimitExceeded` → `scheduled` with `next_attempt_at` = next PT midnight (+ jitter ≤ 10 min) and `youtube_quota_exhausted` on the DTO | `../README.md` D9, D10 ✅ |
| F4 | `IQuotaLedger.TryReserveAsync(platform, now)` / `ReleaseAsync(reservation)`; PT day via `TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles")`; `GET /publishing/youtube/eta` → `{ available, nextSlotAt }` (next PT midnight when full); metric `youtube_quota_used` | `../README.md` D10 ✅ |
| F5 | Upload bucket: `videos.insert` = 100 calls/day at 1 unit; other methods share 10,000 units/day; **daily quotas reset at midnight PT** | `../README.md` G1 ✅ |
| F6 | `youtube_quota_exhausted` → **429**, "Next slot via `GET /publishing/youtube/eta`" | `../../../09-backend-api.md:420` ✅ |
| F7 | `GET /publishing/youtube/eta` (next upload slot for the current user) is already a 00 §8 Channels route | `../../../00-overview.md:361` ✅ |
| F8 | `youtube_quota_used` is a **frozen** metric name (the owner writes it, 11 wires it), alert **> 80 %** (warn) on the platform board | `../../11-worker-deployment/11.6-operations/plan.md:53,71` ✅; `.../11.6-operations/11.6.2-T1-telemetry-dashboards-and-alerts.md:43` ✅; `../../../09-backend-api.md:236` ✅ |
| F9 | Metric surface: one `Meter` `NightTales`; `NightTalesMetrics.SetYoutubeQuotaUsed(double ratio)` is the observable gauge (0..1) used by 02/03/06 | `../../09-backend-api/09.9-platform/plan.md:143-153` ✅ |
| F10 | `YouTubeOptions.Quota:UnitsPerDay` = 100; `YouTubeOptions` is the one option class for the 06 settings (02 reads the same class) | `../README.md` D11, F22 ✅ |
| F11 | One expand-only migration per owning task: `_06_PlatformQuotaUsage` is **this** task's | `../README.md` D20 ✅ |
| F12 | 09.2.2 leaves `platform_quota_usage` to **06**; its conventions: `id uuid` PK unless noted, `created_at`/`updated_at timestamptz NOT NULL`, enum columns text + `CHECK`, aggregate `xmin`; later migrations are `<yyyyMMddHHmm>_<Owner>_<What>` and expand-only | `../../09-backend-api/09.2.2-core-schema-migrations/plan.md` §1, §3 ✅ |
| F13 | Pipeline: commands run inside `IUnitOfWork` (`BeginAsync`/`CommitAsync`/`RollbackAsync`, `EfUnitOfWork`); handlers never call `SaveChangesAsync`; error codes live in `Application/Common/Errors/ErrorCodes.cs`, HTTP status comes from `Api/Http/ErrorCatalogue.cs`; a 429 carrying `retryAfterSeconds` also sets `Retry-After` | `../../09-backend-api/README.md:154-196` ✅ |
| F14 | Audit: `[Audited(action, category, visibility)]` + `IAuditWriter` (09.3.6-T1); publishing categories/actions are D17's | `../../09-backend-api/README.md:156,644` ✅; `../README.md` D17 ✅ |
| F15 | The ETA path must not leak user data (global counter) and stays behind `RequireFeature("youtube_publish")` → 404 `feature_disabled` | `../README.md` D1 ✅; `../../09-backend-api/README.md` BD3 ✅ |
| F16 | Audit pack content: screencasts (policy acceptance → connect → publish form → consent → upload → disconnect, and autopilot on → series authorization text version → veto notice with edit/cancel/approve-now → scheduler consent row → upload), privacy policy + ToS URLs, data-flow diagram, retention/deletion table (7/30-day jobs live), quota justification (M1: owner cadence + daily cap), synthetic-media flag, consent model with the III.C.3 justification and the one-tap fallback; filed via the YouTube API form | `../../../06-youtube-posting.md:117` ✅ |
| F17 | Deletion/retention duties the pack must show as live: 7 days after in-app revoke, 30 days after a Google-page revoke, 30-day reconfirm/refresh, user deletion ≤ 7 days | `../README.md` G13 ✅; D15, D16 ✅ |
| F18 | Acceptance before connect: `ILegalAcceptance.EnsureCurrentAsync` (409 `legal_acceptance_required`); pages built from `manifest.json` versions and rendered by 10.3.1-T1 | `../../13-compliance/README.md` D1, D3, K1 ✅ |
| F19 | 13.4.2-T1 starts with a behaviour walkthrough checklist mapping each III.x clause and each policy sentence to a test or screenshot; answers drafted in `docs/compliance/youtube-audit-<date>.md`; **no secrets or personal data in the repo copy** | `../../13-compliance/README.md` D5 ✅ |
| F20 | Gate **06-publish** requires the `quotaExceeded` / `invalid_grant` / `invalid_client` paths to pass against `FakeGoogleServer`; gate **06-audit-ready** requires this pack complete **and** the 13 P slice done, then 13.4.2-T1 files | `../README.md` §7 ✅ |
| F21 | `FakeGoogleServer` (TestKit, Kestrel, no mocking library) covers token/revoke/channels/resumable/videos errors; integration tests run on real PostgreSQL 17 via `PostgresFixture` and drive time with `FakeClock` | `../README.md` D19 ✅; `../../09-backend-api/README.md:141,297-298` ✅ |

**Open / inference.**
- [OPEN] **02's PT-day rule ("02 T7")**: `../README.md` D10 pins the ICU zone, but no shared helper exists anywhere in `plans/` (grep 2026-09-25: only D10 mentions `America/Los_Angeles`). This task owns `PacificQuotaDay`; if the 02 writer adds an equivalent helper, one of the two must be dropped. TL ruling requested → §11.
- [OPEN] **06.4.2-T1 quota accounting**: D16 refreshes stored data with `channels.list` + `videos.list` (1 unit per batch of 50) but never says whether it reserves through `IQuotaLedger`. Those reads share the 10,000-unit/day bucket (F5), not the 100-upload bucket, so a naive reuse of the ledger would burn upload units. §7 records the two options for the TL.
- [OPEN] **10.7.1/2/4-T1 are not detailed** (10 wave 2): their exact consumers of `nextSlotAt` and the copy keys handed over in §6 are [INFERENCE] until wave 2 lands.
- [INFERENCE] `available` is an integer number of free **units** (D10 writes only `{ available, nextSlotAt }`); `nextSlotAt` is `null` while `available > 0`.

## 3. Domain and data

### 3.1 Aggregate and invariants

`platform_quota_usage` is a **platform counter**, not an aggregate and not user-owned (F1): no `user_id`, no `xmin`, no tenant filter — the value is global by definition, so 09.2.3's user filter must never touch it (it is not `IUserOwned`). Its concurrency control is the conditional `UPDATE`, not a row version (F2, F12).

| Id | Invariant |
|---|---|
| Q1 | `0 ≤ units_used ≤ units_limit` at every commit; enforced by `CHECK (units_used >= 0)`, `CHECK (units_limit > 0)` and the `WHERE units_used < units_limit` predicate of the reserve statement |
| Q2 | Exactly one row per `(platform, quota_day)`; created lazily with `units_limit` = `YouTube:Quota:UnitsPerDay` at the first reserve of that PT day (F2, F10) |
| Q3 | A reservation is exactly **one** unit and exists only inside one `PublishYouTubeJob` unit of work (reserve → upload → settle/release); the ledger keeps no per-reservation rows (D10) |
| Q4 | The day key is **always** the Pacific calendar day of the instant; never `UtcNow.Date`, never the server-local day (F4) |
| Q5 | `ReleaseAsync` never drives the counter below zero and never creates a row (a release on a missing/zero row is a no-op returning success) |

### 3.2 Table `platform_quota_usage` (new — owner 06.3.1-T1)

| Column | Type | Null | Constraint / default |
|---|---|---|---|
| `platform` | `text` | no | `CHECK (platform IN ('youtube'))` — the P set; `tiktok` joins only if 07 needs it → [OPEN] |
| `quota_day` | `date` | no | PT calendar day |
| `units_used` | `int` | no | `DEFAULT 0`, `CHECK (units_used >= 0)` |
| `units_limit` | `int` | no | `CHECK (units_limit > 0)`; written from `YouTube:Quota:UnitsPerDay` |
| `created_at` | `timestamptz` | no | set by the insert |
| `updated_at` | `timestamptz` | no | set by every reserve/release |

- **PK** `(platform, quota_day)` — a deviation from D8's "`id uuid` PK unless noted", noted here per F12; the composite key **is** the access path, so no secondary index is added (every statement filters on both columns).
- No FK, no `user_id`, no soft delete, no cleanup job: one row per day per platform (365 rows/year) [INFERENCE: no retention task needed].
- Migration: `src/NightTales.Infrastructure/Persistence/Migrations/<yyyyMMddHHmm>_06_PlatformQuotaUsage.cs` (F11, F12) — **expand-only** (create table + constraints, no drops).

### 3.3 Domain / Application / Infrastructure artefacts

| Artefact | Path | Kind |
|---|---|---|
| `PlatformQuotaUsage` | `src/NightTales.Domain/Publishing/PlatformQuotaUsage.cs` | entity shell (private setters, internal ctor for EF; no behaviour — the ledger drives it through SQL) |
| `PlatformQuotaUsageConfiguration` | `src/NightTales.Infrastructure/Persistence/Configurations/PlatformQuotaUsageConfiguration.cs` | `IEntityTypeConfiguration`, composite key, CHECKs |
| `IQuotaLedger`, `QuotaReservation` | `src/NightTales.Application/Abstractions/Publishing/IQuotaLedger.cs`, `.../QuotaReservation.cs` | port + record |
| `PacificQuotaDay` | `src/NightTales.Application/Publishing/Quota/PacificQuotaDay.cs` | static helper (`DateOnly Day(DateTimeOffset utc)`, `DateTimeOffset NextResetAt(DateTimeOffset utc)`) — the one implementation of Q4 |
| `YouTubeQuotaLedger` | `src/NightTales.Infrastructure/Publishing/YouTubeQuotaLedger.cs` | implementation of `IQuotaLedger` (`IAppDbContext` + `IOptionsMonitor<YouTubeOptions>` + `IClock` + `NightTalesMetrics`) |
| `GetYouTubeEtaQuery` + handler + `YouTubeEtaDto` | `src/NightTales.Application/Publishing/Quota/` | query (one `SELECT`) |
| endpoint | `src/NightTales.Api/Endpoints/Publishing/YouTubeEtaEndpoints.cs` | minimal API group |
| DI | existing composition roots of 09.1.1-T1 / 09.2.6-T1 | `IQuotaLedger` → singleton-safe scope: register **scoped** (it takes the scoped `IAppDbContext`) — mirrors the D4 handler lifetime rule for anything touching `IAppDbContext` (`09-backend-api/README.md:62,205`) |

### 3.4 Sql contract (the two statements of D10, F2)

```sql
-- step 1 (idempotent, once per new PT day): seed the row with the configured limit
INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at)
VALUES (@platform, @day, 0, @limit, now(), now())
ON CONFLICT (platform, quota_day) DO NOTHING;      -- concurrent seeding is a no-op, never an error

-- step 2 (one atomic statement; the row lock serialises concurrent attempts):
UPDATE platform_quota_usage
   SET units_used = units_used + 1, updated_at = now()
 WHERE platform = @platform AND quota_day = @day AND units_used < units_limit;
-- rows affected = 0 → youtube_quota_exhausted (§4 UC1)

-- release (Q5):
UPDATE platform_quota_usage
   SET units_used = units_used - 1, updated_at = now()
 WHERE platform = @platform AND quota_day = @day AND units_used > 0;
```

- No explicit transaction is needed: step 1 is idempotent and step 2 is a single statement whose `WHERE` is evaluated under the row lock (lost-update free). When the ledger is called from a command or job, both statements join the ambient `IUnitOfWork` transaction (F13) so a rollback also rolls the unit back.
- EF realisation: step 1 via `Database.ExecuteSqlInterpolatedAsync`; step 2 via `ExecuteUpdateAsync` on the `DbSet` (a single `UPDATE`, **not** load-modify-save → no read-modify-write race, no extra round trip); the returned row count decides success.
- No N+1: reserve = 2 statements, release = 1, ETA = 1 `SELECT`; the counter is never read for the reserve decision (the `WHERE` decides it).

### 3.5 Audit evidence pack data (no new schema)

The pack is a set of documents + a manifest that **references** artefacts; it adds no table and no column. §5 of the task file holds the manifest; the artefact→source mapping is:

| Artefact | Source of truth |
|---|---|
| Quota ledger export (used/limit/day for the audit window) | `platform_quota_usage` (this task) + `youtube_quota_used` on the 11.6.2-T1 board |
| Quota justification (owner cadence + daily cap = 100 units) | 06 README F5/F22; high-level 06.5.1 (F16) |
| Platform-incident register (raised/cleared, no user content) | `platform_incidents` rows raised by `OAuthErrorClassifier` hits and cleared by `nts-worker incident clear` (06 README D14; 06.2.3-T3, 06.4.2-T2) |
| Token-revocation evidence (7-day in-app, 30-day Google-page) | `data_deletion_requests` rows created through `IDeletionRequests.CreateAsync` + `DataDeletionJob` runs + the `channel` `IDeletionScopeHandler` completion (`completed_at ≤ due_at`) — 13.3.1-T1, 06.4.1-T1, 06.4.3-T1, 06.4.2-T1 |
| 30-day reconfirm/refresh evidence | `TokenHealthJob` run log (06.4.2-T1, 06 README D16) |
| Consent model + text versions (`pc.v1`, series authorization version, veto-window notice) | `publications.consent_*`, `series_autopilot_authorizations.text_version` (06.2.2-T3/T4; 10.4.3-T2 screens) |
| Acceptance before connect (ToS/privacy, recorded versions + IP) | `user_consents` via `IConsentWriter` (13.1.1-T1, 13.1.2-T1, 13.2.1-T1) |
| Privacy policy / ToS pages live at the filed versions | 10.3.1-T1 routes + `web/src/content/legal/manifest.json` (13 README K1) |
| Data-flow diagram, retention/deletion table, synthetic-media flag | derived from 00 §6/§7 + 06 README D6/D15/D16 |
| Behaviour walkthrough screencast list (13 steps) | 13 README D5 checklist; screens: 10.7.1-T1, 10.7.2-T1, 10.7.4-T1 |
| One-tap "approve now" fallback evidence | 06.2.2-T4 (owner decision O3 in 06 README §10) |
| Publication status/go-live evidence (`published`, `external_post_id`, `publishAt + 5 min` confirmation) | `PollYouTubeProcessingJob` poll + `PublicationGoLiveJob` re-check of `status.privacyStatus` (06.2.5-T1) + 06.2.6-T1 cancel semantics |

## 4. Use cases

| # | Use case | Behaviour, DTO, validation, authorization, idempotency, errors, audit |
|---|---|---|
| UC1 | **Reserve one unit** — `IQuotaLedger.TryReserveAsync(platform: "youtube", now: DateTimeOffset) → ErrorOr<QuotaReservation>` | `QuotaReservation(string Platform, DateOnly Day)` [INFERENCE: D10 writes `QuotaReservation(DateOnly Day)`; `ReleaseAsync(reservation)` must address the row, so the record carries `platform` too]. Steps: `day = PacificQuotaDay.Day(now)` → seed (§3.4 step 1, limit = `options.Quota.UnitsPerDay`) → conditional update (step 2). `rows == 1` → success + `metrics.SetYoutubeQuotaUsed(used / limit)` (ratio read back from the same statement — `RETURNING units_used, units_limit` [INFERENCE, keeps it to one round trip]); `rows == 0` → `Error.TooManyRequests(code: ErrorCodes.YouTubeQuotaExhausted, metadata: { retryAfterSeconds = seconds until PacificQuotaDay.NextResetAt(now) })`. Called from `PublishYouTubeJob` (06.2.3-T2) **before** `StartUploadAsync` (F3). No `Idempotency-Key`: one unit per call by design, and the counter is atomic. No audit row (machine-rate operation; D17 owns the publishing actions). |
| UC2 | **Release a unit** — `IQuotaLedger.ReleaseAsync(QuotaReservation) → ErrorOr<Success>` | Called only for failures where YouTube never received the insert or on `quotaExceeded`/`uploadLimitExceeded` (F3, D10). Always returns success (Q5); the caller's own state change (`scheduled` + `next_attempt_at`) commits in the same `IUnitOfWork` transaction, so a crash rolls back both the release and the state change — no double release [INFERENCE from F13 + D9's error map]. Same metric update. |
| UC3 | **Read the publish ETA** — `GetYouTubeEtaQuery() → YouTubeEtaDto(int Available, DateTimeOffset? NextSlotAt)` | One `SELECT` for today's PT row; missing row → `used = 0`, `limit = options.Quota.UnitsPerDay`, so a fresh day reports the full limit. `available = max(0, limit - used)`; `nextSlotAt = available > 0 ? null : PacificQuotaDay.NextResetAt(now)`. Bearer required; `RequireFeature("youtube_publish")` → 404 `feature_disabled` (F15); the route returns the **global** counter only — the per-user queue was dropped with phase S (DEC 10.14). No audit row. |
| UC4 | **PT rollover** | A reserve after the PT midnight boundary addresses the new `quota_day`, seeds it and succeeds even though the previous day was full (F5, Q4). Never a "reset job": the day key does the work [DECISION D10]. |
| UC5 | **Assemble the audit evidence pack** | BA sequence: run the walkthrough on prod-API staging → collect the artefact set (§3.5) → write the manifest, the clause-mapped checklist, the retention/deletion table and the quota justification → review against every III.x clause and every policy sentence (13 README D5) → hand to 13.4.2-T1 with the pack index. Signals: every artefact row has a source, a date, and a verified "live" state; no secret, token or personal data in the repo copy (F19). |

**Error codes used** (no new constants — 09 owns them, F13): `youtube_quota_exhausted` (429, F6). `feature_disabled` (404), `unauthenticated` (401) come from 09 BD3/F6. The ETA route never returns 429.

## 5. API and events

| Method | Route | Auth / gate | 200 | Errors |
|---|---|---|---|---|
| `GET` | `/publishing/youtube/eta` | bearer + `RequireFeature("youtube_publish")` (F15) | `YouTubeEtaDto { "available": int, "nextSlotAt": DateTimeOffset? }` | 401 `unauthenticated`; 404 `feature_disabled`; problem envelope per `../../09-backend-api/README.md:191` |

- The route is already listed in 00 §8 (F7) — no 00 CR needed for it. Body/response types are new DTOs (no CR).
- No `If-Match` (no ETag: F12 excludes platform counters; the publication ETag `"pu-<xmin>"` belongs to 06.2.1-T1 per README D7 + 06 CR-2). No `Idempotency-Key` (read-only, UC1/UC3).
- **No outbox event and no SignalR message** from this folder: the ledger is a counter, not a state change anyone subscribes to; `publication_updated.v1`, `channel_connection_changed.v1`, `platform_incident_*` stay with 06.2.3-T2/06.2.5-T1 (README D13). Recorded explicitly so a reviewer does not look for a missing publisher.
- Metric (not HTTP): `youtube_quota_used` = `units_used / units_limit` (0..1), observable gauge, recorded after every reserve/release (F8, F9). 11.6.2-T1 wires the > 80 % warn alert; this folder only feeds the gauge.
- 06.2.6-T1 (cancel) consumes the port's error/semantics, not a route: a `scheduled` publication holds no reservation (F3), so cancel has nothing to release — the dependency exists for `next_attempt_at`/ETA semantics [INFERENCE; flagged to the 06.2.1 folder owner].

## 6. UI

10 owns every screen; this folder hands over contracts and copy keys (no screen here).

| Consumer | What it renders | Status |
|---|---|---|
| `10.7.2-T1` YouTube publish form + consent + **quota wait** | reads `GET /publishing/youtube/eta`; when `available = 0` shows the wait and the reset time from `nextSlotAt`; when a publication is `scheduled` with `errorCode = youtube_quota_exhausted` shows `nextAttemptAt` | 10 wave 2, **not detailed** → [OPEN] |
| `10.4.3-T2` autopilot run log / veto list (wave 1, ✅ exists) | publication row shows `errorCode` + `nextAttemptAt` (both already on `PublicationDto`, 06 README D6); the run log shows the same for a quota-held publication | detailed; no new field needed |
| `10.7.4-T1` publication status widgets | `scheduled → published` timeline; quota-held publications show the next PT slot | 10 wave 2 → [OPEN] |
| `10.7.1-T1` channels page | shows connect state only; not a quota consumer | 10 wave 2 → [OPEN] |
| `10.3.1-T1` legal pages (wave 1, ✅ exists) | not a runtime consumer: an **evidence** source for the pack (`manifest.json` versions, 13 README K1) | detailed |
| `10.4.3-T2` / 06.5.1-T1 pack | the veto-notice screenshot source (edit/cancel/approve-now) | detailed |

**Copy keys handed over** (recorded here so the wave-2 writer does not invent them; messages live in 10's message catalogue, keys are frozen here): `publishing.quota.wait` ("Nhà xuất bản đang chờ hạn mức YouTube hôm nay."), `publishing.quota.resetAt` ("Hạn mức làm mới lúc {{time}} (giờ Thái Bình Dương)."), `publishing.quota.exhausted` ("Đã dùng hết hạn mức tải lên hôm nay."), `publishing.eta.estimated` ("Dự kiến tải lên lúc {{time}}.") [INFERENCE: key names follow 10's `<area>.<feature>.<item>` convention; the vi/en copy is 10's to write].

## 7. Jobs and integrations

| Integration | Owner | Contract |
|---|---|---|
| `PublishYouTubeJob` (queue `publish`, `[JobPolicy(Retries = 5)]`) | 06.2.3-T2 | reserves before `StartUploadAsync`; on `youtube_quota_exhausted` → publication `scheduled`, `next_attempt_at` = next PT midnight + jitter ≤ 10 min; on `quotaExceeded`/`uploadLimitExceeded` → `ReleaseAsync` + `scheduled` + next PT midnight; failure before the session URI was created → `ReleaseAsync`; the session exists → no release (the unit is spent, F3) |
| `AutopilotConsentJob` | 06.2.2-T3 | does not touch the ledger (no Upload API call) |
| `TokenHealthJob` | 06.4.2-T1 | **outside** the upload bucket: `channels.list` + `videos.list` (1 unit per batch of 50) share the 10,000-unit/day bucket, not the 100-upload bucket (F5). [OPEN] whether it reserves: option A — a second ledger bucket (`platform = 'youtube_api'`, limit from a new option) is **out of P** (scope creep); option B — exempt, only counted by the metric. TL decides; this folder ships the upload bucket only |
| `DataDeletionJob` | 13.3.1-T1 | no ledger interaction; produces the deletion evidence the pack cites (F17, F19) |
| `GET /admin/quota` | dropped (DEC 10.14) | not built; the alert runbook points at the ledger row |
| `FakeGoogleServer` | TestKit (D19, F21) | used for the reserve-then-fail sequences (`quotaExceeded`) that exercise the release path |
| 11.6.2-T1 | 11 | consumes `youtube_quota_used`; warn at > 80 %; no rename (F8) |

**Retries / timeouts.** No Google call and no background job in this folder, so no external timeout or retry policy is owned here; the only DB bound is the transaction/command timeout of the ambient unit of work. Reserve/release are idempotent under retry: a retried reserve that already succeeded within a rolled-back transaction reserves again (correct), and a retried release of a committed release is prevented by committing the release only with the state change that causes it (UC2).

**Failure map.**

| Failure | Behaviour |
|---|---|
| `platform_quota_usage` row missing at reserve | seeded by step 1 in the same call (Q2) — never an error |
| Two workers reserve the last unit | one `UPDATE` returns 1 row, the other 0 → the loser gets `youtube_quota_exhausted` (never an over-reservation) |
| Release on a zero/missing row | no-op, success (Q5); no CHECK violation |
| `units_limit` lowered below `units_used` (an option change mid-day) | the existing row keeps its written limit for the day; the new limit applies to the next day's row (Q2) — the option is read at seed time only [INFERENCE from F2/F10] |
| Clock skew between Api and Worker | both compute the day from the same rule (Q4) with the ICU zone, so at most one unit lands in the wrong bucket within the skew window; the day boundary is a hard reset, so the risk is one mis-bucketed unit, not a lost quota [INFERENCE] |
| A metric recorder throws | must not fail the reserve: the gauge write is outside the transaction's decision and is wrapped (log-only) [INFERENCE; 09.9.3-T1 records metrics as best-effort] |

## 8. Security and privacy

| Threat / duty | Control |
|---|---|
| Cross-tenant read of the counter | the counter is **global by design** (F1) and carries no user data; the route still requires bearer + feature flag, and a disabled feature answers 404 `feature_disabled` without revealing existence (F15) |
| Quota tampering through the API | no write route exists; only the Worker reserves/releases through the port; the ETA route is read-only |
| Over-reservation / lost update | single conditional `UPDATE` under the row lock (§3.4); `CHECK (units_used >= 0)` + `CHECK (units_limit > 0)` as backstops (Q1); concurrency test in §9 |
| Retry double-release | release commits atomically with the resulting publication state change (UC2) |
| Sensitive data in logs | the ledger logs `platform`, `quota_day`, `units_used`, `units_limit`, `traceId` only — never a title, token, channel name or video id (README D17) |
| Evidence-pack leakage | the repo copy holds the manifest, clause mapping, retention table and links only; screencasts/screenshots (owner channel, video ids) live outside the repo with a recorded location + SHA-256; no token, e-mail, IP or raw API payload in the repo copy (F19) |
| Consent/retention truthfulness | each III.D/III.E clause maps to a **live** job and a query proving it ran (13 README D5); a clause without evidence blocks the handoff |
| Secrets | none in this folder; no new secret, no `YouTubeOptions` secret field is read here (only `Quota:UnitsPerDay`, a non-secret) |

## 9. Test plan

Tools: xunit.v3 + Shouldly, `NightTales.TestKit` (`FakeClock`, `PostgresFixture`, `ApiFactory`, `TestAuthHandler`), real PostgreSQL 17 via `PostgresFixture`, no mocking library (F21). Canonical projects (`../../09-backend-api/README.md` Part A D2): `NightTales.Domain.UnitTests`, `NightTales.Application.UnitTests`, `NightTales.Infrastructure.UnitTests`, `NightTales.Architecture.Tests`, `NightTales.Api.IntegrationTests`, `NightTales.TestKit`.

| Name | Project / kind | Asserts |
|---|---|---|
| `QuotaLedger_Reserve_SeedsRowWithConfiguredLimit` | Api.IntegrationTests (real PG) | first reserve of a day creates the row with `units_used = 1`, `units_limit = YouTube:Quota:UnitsPerDay` |
| `QuotaLedger_Reserve_IncrementsUntilLimit` | Api.IntegrationTests | 100 reserves → 100 units, 101st → `youtube_quota_exhausted` |
| `QuotaLedger_Reserve_AtLimit_ReturnsRetryAfterToNextPacificMidnight` | Api.IntegrationTests | error metadata `retryAfterSeconds` = seconds to `PacificQuotaDay.NextResetAt`; the HTTP path sets `Retry-After` |
| `QuotaLedger_Reserve_ConcurrentLastUnit_OnlyOneSucceeds` | Api.IntegrationTests | limit 1, two parallel scopes (`Task.WhenAll`) → exactly one success, `units_used = 1` |
| `QuotaLedger_Reserve_AfterPacificMidnight_UsesNewDay` | Api.IntegrationTests + `FakeClock` | advance across 07:00/08:00 UTC (PST/PDT) → new row, full limit again, previous day intact |
| `QuotaLedger_Release_Decrements` | Api.IntegrationTests | reserve → release → `units_used` back, no CHECK violation |
| `QuotaLedger_Release_OnZeroRow_IsNoOpSuccess` | Api.IntegrationTests | no row / zero row → success and still no row below zero |
| `QuotaLedger_RejectsNegativeAndZeroLimit` | Api.IntegrationTests | raw insert violating the two CHECKs → 23514 |
| `PacificQuotaDay_Day_AcrossDstTransitions` | Application.UnitTests (theory) | 2026-03-08 and 2026-11-01 instants around the PT boundary map to the correct `date` and `NextResetAt` |
| `PacificQuotaDay_NextResetAt_IsAlwaysStrictlyFuture` | Application.UnitTests | for instants 1 s before/after the boundary |
| `YouTubeEta_ReturnsAvailableUnits` | Api.IntegrationTests (HTTP) | 200 body `{ available, nextSlotAt: null }` matching `limit - used` |
| `YouTubeEta_WhenDayFull_ReturnsNextSlotAt` | Api.IntegrationTests | `available = 0`, `nextSlotAt` = next PT midnight |
| `YouTubeEta_WithNoRowForToday_ReturnsFullLimit` | Api.IntegrationTests | fresh day → `available = limit` |
| `YouTubeEta_WithoutFeature_Returns404FeatureDisabled` | Api.IntegrationTests | flag off → 404 `feature_disabled`; unauthenticated → 401 |
| `NightTalesMetrics_YoutubeQuotaUsed_ReflectsRatio` | Infrastructure.UnitTests (`MeterListener` on `NightTalesMetrics.MeterName`) | after a reserve the observable gauge reports `used / limit` |
| `QuotaLedger_LivesInApplicationWithoutGoogleTypes` | Architecture.Tests | `IQuotaLedger` is in Application; no `Google.Apis.*` type in its signature |

**FakeGoogleServer / manual sequences** (needed for the release path, F20 gate 06-publish): a publication forced to `quotaExceeded` releases exactly one unit and returns to `scheduled` with `next_attempt_at` = next PT midnight; a publication failing before the session URI is created releases; a publication failing after the first chunk does **not** release. These run in `NightTales.Api.IntegrationTests` (jobs run in-process, `WorkerHost`).

**BA verification (06.5.1-T1)** — checklists, not tests: every artefact row resolves, every III.x clause has a test or screenshot, the retention table matches the live jobs, the quota justification matches the filed cadence, and the pack index is reproducible by a second person from the repo copy alone.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 06.3.1-T1 | Quota ledger (PT day, atomic reserve/release) + `GET /publishing/youtube/eta` | M (1.0) | 09.2.2-T1 | 06.2.6-T1, 06.2.3-T2, 06.4.1-T1, 10.7.2-T1 (10 CR-15) |
| 06.5.1-T1 | Audit evidence pack (filing is 13.4.2-T1) | M (1.0) | 06.2.5-T1, 06.2.2-T4, 06.4.2-T2, 06.4.3-T1, 13.1.1-T1, 13.1.2-T1, 13.2.1-T1, 10.3.1-T1, 10.4.3-T2, 10.7.1-T1, 10.7.2-T1, 10.7.4-T1 | 13.4.2-T1 |

Total **2.0 d** — matches `../README.md` §9 (06.3 quota 1.0 unchanged, 06.5 audit 1.0 unchanged). The ETA endpoint is folded into T1 (phase S, and with it the admin view, is dropped by DEC 10.14).

## 11. Rollout

- **Flag.** The ETA route and the reserve path are behind `youtube_publish` (D1); recurring jobs that would reserve register only while the flag is on (06.2.3-T2 owns that registration).
- **Sequence (M1).** `_06_PlatformQuotaUsage` is applied by the migration bundle (11.5.1) before the Worker starts; 06.3.1-T1 must land before 06.2.3-T2 (upload) and 06.2.6-T1 (cancel). Then 06.5.1-T1 assembles the pack once 06.2.5-T1, 06.2.2-T4, 06.4.2-T2 and 06.4.3-T1 are green and the 13 P slice (13.1.x, 13.2.1, 13.3.1) is live → `13.4.2-T1` files (gate **06-audit-ready**, F20).
- **Monitoring.** `youtube_quota_used` on the platform board with the > 80 % warn (11.6.2-T1); the operator action is to read the day row (no admin view: phase S dropped, DEC 10.14).
- **Smoke on staging** (gate 06-publish evidence): set `YouTube:Quota:UnitsPerDay = 1`, upload once → success; a second publication → `scheduled`, `next_attempt_at` = next PT midnight, form shows the wait from `nextSlotAt`; next day the counter resets with no operator action; `platform_quota_usage` shows one row per day.
- **Rollback.** Forward-only migration; if the counter desyncs, an operator `UPDATE` on the day row is the documented fix ([INFERENCE] runbook step for 11.6.1 — no admin endpoint; phase S dropped by DEC 10.14). Lowering `units_limit` takes effect on the next day's row (§7).
- **DoR items raised by this plan** (§2 [OPEN]): (1) the 02 PT-day helper duplication; (2) `TokenHealthJob` quota accounting; (3) 10 wave-2 ETA/copy keys. All three are contract-level and must be ruled before 06.3.1-T1 starts.
