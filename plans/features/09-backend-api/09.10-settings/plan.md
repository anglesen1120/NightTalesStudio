# 09.10 — Settings store, secret encryption, settings admin API and hot reload

Folder of wave 3 (S) of `plans/features/09-backend-api/README.md` — **Part C**, writer brief 3 (§C0): *"the secret store. Implements D6 (purpose chains, `SecretString`, env lock), D7 (critical change + 24 h time-lock + per-recipient cancel tokens), the connect-time IP re-validation, and hot reload."* Tasks: **09.10.1-T1, 09.10.1-T2, 09.10.2-T1, 09.10.2-T2, 09.10.2-T3, 09.10.3-T1** = M·S·M·M·S·S = **4.5 raw days** (§C8.1). Evidence legend: ✅ verified on disk · ⚠️ caveat · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In.** `system_settings` as the single source of truth for every non-bootstrap integration value, non-secret in `value jsonb`, secrets only as purpose-bound ciphertext (`D6`); history in `system_setting_versions`; critical changes frozen in `system_setting_changes` until a second owner approves or a 24 h time-lock expires (`D7`). The owner-only admin API (§C3.1): `GET /admin/settings`, `PUT /admin/settings/{integration}` (`If-Match`), `POST …/test`, `GET …/versions`, `POST …/rollback/{version}`, `GET/POST /admin/worker-nodes`, `PATCH /admin/worker-nodes/{id}` — **09.10.2-T1**; pending-change routes `GET /admin/settings/changes?status=pending`, `POST …/approve`, `POST …/cancel` — **09.10.2-T2**. Connect-time egress validation (`SocketsHttpHandler.ConnectCallback`, blocked ranges, `mpt`-only render-subnet CIDR) — **09.10.2-T3**. Hot reload: `DbSettingsProvider` (`IOptionsMonitor` source, ≤30 s in Api) + Worker `SettingsChanged`; degraded start on undecryptable secret — **09.10.3-T1**.

**Out.** Billing admin routes (plan 12, `D13`/F04); the `/admin` group/policies/pagination/tenant-bypass allow-list (**09.9.1-T1**); public cancel-link route + `approval_cancel_tokens` (**09.10.8-T1**); owner actions/hierarchy/`ISecurityAlerter` (**09.10.4-T2/T3/T4**); `registration` settings implementation (**09.10.6-T1**); `nts-admin` CLI (**09.10.7-T1**). Per `D20`: no impersonation, no second flag store, no endpoint returning a secret, no export route, no CAPTCHA on `/cancel-request`, no `vieneu_proxy` integration (04 CR-8). Consumed, never re-written: `09.9.4-T1` (Data Protection key ring/KEK), `09.2.2-T2` (migration), `09.2.6-T1`/`09.2.6-T3` (jobs, e-mail templates), `09.2.4-T1` (outbox), `09.10.4-T2` (owner-approval apply pattern), `09.3.5-T1` (rate policies, W3 CR-7), `09.9.1-T1` (group/policy).

## 2. Confirmed facts

| Id | Fact | Evidence |
|---|---|---|
| S1 | `system_settings(integration, key, value jsonb, secret_ciphertext bytea, secret_hint, is_secret, version, updated_by, updated_at; unique(integration,key))`, `system_setting_versions(setting_id, version, value jsonb, secret_ciphertext, secret_hint, changed_by, changed_at, reason)`, `system_setting_changes(integration, requested_by, requested_at, reason, status, apply_after, approved_by, cancelled_by, applied_at, payload_ciphertext, hints jsonb)` already exist | `plans/00-overview.md:309-311` ✅ |
| S2 | §5.6 catalog: `openai`(base_url, organization_id, project_id, text_model, timeout_s + secret api_key), `mpt node image key`(read-only), `mpt`(per node: base_url, capacity, state + secret api_key), `payment`(provider, mode, merchant_id, base_url + secret_key, webhook_secret; **critical**), `email`(smtp_host/port, from_address/name + smtp_password), `youtube`(client_id, audit_status + client_secret; **critical**), `tiktok`(client_key + client_secret; **critical**), `registration`(mode, disposable_domains, trial.credits; no secret) | `plans/09-backend-api.md:470-485` ✅ |
| S3 | `captcha` becomes a catalog row (provider, site_key + secret secret_key, allow-list `challenges.cloudflare.com` only, test = siteverify with an invalid token; not critical) — **W3 CR-2**; 09.10.1-T1 seeds it, 09.3.3-T2 consumes it soft | README §C4 W3 CR-2 ✅ |
| S4 | `registration.mode ∈ {closed, invite, open}` supersedes BD2's two-value set (D15, W3 CR-12); not critical | README W3-6 ✅, `09-backend-api.md:479` ✅ |
| S5 | `SettingChangeApplyJob` — recurring 1 min, arg = change id, `status=pending AND apply_after≤now` (owner **09.10.2-T2**); `SettingSecretPurgeJob` — daily, arg = version id with non-null `secret_ciphertext` older than 7 days, keeps hint (owner **09.10.1-T2**) | `09-backend-api.md:367,369` ✅, README §C3.3 ✅ |
| S6 | Codes: `setting_invalid` 400, `setting_secret_required` 400, `setting_change_pending` 202 body/409, `setting_test_failed` 502 (W3 CR-3) | `09-backend-api.md:427,429` ✅, README §C3.6 ✅ |
| S7 | Partial index `system_setting_changes (integration, status) WHERE status='pending'` — **W3 CR-1f**, both for the one-pending rule and the apply scan | README §C3.2/§C4 ✅ |
| S8 | No new event type: `SettingsChanged` (existing) drives the Worker reload; `email_requested.v1` carries `setting_change_pending` | README §C3.3 ✅ |
| S9 | `setting_change_pending` e-mail (params `integration, changedKeys, applyAfter, approveUrl?, cancelUrl`) — **W3 CR-8**; all 4 locale/format variants or host fails at startup | README §C3.5 ✅, `09.2.6-T3:94` ✅ |
| S10 | Audit actions `settings.changed/change_pending/change_approved/change_cancelled/change_applied/rolled_back` — category `support`, visibility `internal`, never on `/me/activity` | README §C3.3/§C3.4/D5 ✅ |
| S11 | Frozen metric `settings_changes_pending` (gauge, 08.2 alerts) | README §C3.3/D17 ✅ |
| S12 | Secrets never appear in any response/log/trace/audit/export/exception; env-only bootstrap secrets (DB conn string, DP KEK, JWT key, storage creds, public URLs/redirect URIs, allow-lists, render CIDR); an env value for **any** key locks the whole integration; MPT's own node image key is node deploy config, read-only "node-managed" row | `09-backend-api.md:48,483-485` ✅, README D6/D19 ✅ |
| S13 | UI consumers signed in plan 10: `/admin/settings` + `/admin/settings/nodes` and `/admin/approvals` in the owner-only chunk, approved design already in `10.9-admin-core/10.9.1-T0-ui-design.md`; back-edges = W3 CR-14 | `10-web-ui/README.md:659-660` ✅, `10.9-admin-core/plan.md:27,54,109` ✅ |
| S14 | No new package/paid dependency in this folder (D19) | README D19 ✅ |
| S15 | Gate **09-W3-settings**: 30 s propagation without restart; `youtube` client change → 202 pending → time-locked apply → cancellable by a second owner's link; copied ciphertext fails to decrypt; after purge the live secret still works and rollback asks for re-entry; log-scan finds no seeded secret | README §C8 gates ✅ |

## 3. Domain and data

`SystemSetting` (Domain/Settings) is **not** `IUserOwned` (Part A D10, no tenant filter): `Integration, Key, IsSecret, Value(JsonDocument?), SecretCiphertext(byte[]?), SecretHint, Version, UpdatedBy, UpdatedAt, WorkerNodeId(Guid?)`. `SystemSettingVersion` is append-only. `SystemSettingChange` adds `Status, RequestedBy, RequestedAt, ApplyAfter, ApprovedBy, CancelledBy, AppliedAt, PayloadCiphertext, Hints`. `SecretString` is a masked wrapper (`ToString()=>"***"`, Serilog destructuring policy, no implicit `string` conversion) — never logged/serialized. `ISettingsEncryptor.Protect/Unprotect(integration, key, SecretString, nodeId?)` wraps `IDataProtectionProvider` with purpose chain `settings.v1/{integration}/{key}` or `settings.v1/mpt/api_key/{nodeId}` for node rows — a ciphertext copied to another `(integration,key)` or node throws `CryptographicException` by construction.

**Schema.** No new table. One partial index (W3 CR-1f, S7): `CREATE INDEX ix_system_setting_changes_pending ON system_setting_changes (integration) WHERE status='pending'` — the DB-level "one pending change per integration" guard (a racing insert hits `23505` → mapped to 409). One idempotent seed migration inserts every §5.6 row (`ON CONFLICT (integration,key) DO NOTHING`) plus `captcha` (S3); `mpt` rows are per-node and seeded by 09.10.2-T1's node CRUD, not here; the `mpt node image key` row is never a `system_settings` row at all (S12). `value jsonb` is snake_case (Part A D8); every read projection selects `CASE WHEN is_secret THEN NULL ELSE value END, secret_hint` — never `secret_ciphertext`.

**Transactions and locks.**

| Operation | Boundary | Lock strategy |
|---|---|---|
| Non-critical `PUT` | one tx: setting upsert (version+1) + version row + audit + `SettingsChanged` outbox (`UnitOfWorkBehavior`) | `FOR UPDATE` ordered by `(integration,key)`; `SET LOCAL lock_timeout=3s`, retry once |
| Critical `PUT`/rollback | one tx: `system_setting_changes` insert (`pending`) + one cancel token per owner + audit + `email_requested.v1` | the partial unique index is the guard; `23505` → 409 `setting_change_pending` |
| `…/changes/{id}/approve` | serializable tx: load `FOR UPDATE WHERE status='pending'` (terminal-status guard = idempotency), decrypt, apply, `applied` | lock order: change row → setting rows by `(integration,key)`; second approve is a 409 no-op |
| `SettingChangeApplyJob` | one tx per change id, one `ISender` command per row (exactly one `[Audited]/[NotAudited]/[AllowAnonymousRequest]`, 09.3.6-T1 arch test) | `WHERE status='pending' AND apply_after<=now() LIMIT 50 FOR UPDATE SKIP LOCKED`; env-locked mid-flight → cancelled `reason=env_locked`, never applied |
| `SettingSecretPurgeJob` | one tx per ≤5,000 batch, `system_setting_versions` **only** | predicate `secret_ciphertext IS NOT NULL AND changed_at < now()-7d`; `system_settings.secret_ciphertext` is never touched (F12) |
| rollback | restores non-secret `value` only; secret-bearing keys return a re-entry list from `secret_hint`; `base_url` change without a new secret → `setting_secret_required` | same lock order; secret columns never copied from history |

Idempotency: mutating routes rely on `If-Match` (`ETag = "settings-<xmin>"`) + the terminal-status guard; `…/test` is read-only, never writes a version.

## 4. Use cases

**UC-1** store a secret → encrypted per purpose, hint-only response, version+1; a ciphertext copied to another row fails to decrypt. **UC-2** validate per catalog → non-https/host-outside-allow-list/`mpt`-outside-CIDR/bad-timeout/unknown-model/unknown-mode → 400 `setting_invalid`, no row change. **UC-3** URL change requires the secret in the same body, else 400 `setting_secret_required` (also on rollback and an unsaved-URL `…/test`). **UC-4** test call → exactly one harmless probe (list models / adapter `GET /v1/health` / provider status / SMTP `NOOP` / format check), 5 s timeout, no redirects, `ok` or 502 `setting_test_failed` with status only, never the remote body. **UC-5** critical change is frozen → 202 `setting_change_pending`, per-owner cancel links, second pending → 409. **UC-6** approve or wait → a different owner not promoted in 30 days applies at once; otherwise 24 h time-lock via `SettingChangeApplyJob`; any owner or the recipient's public token (09.10.8) cancels. **UC-7** env lock → any env value for a key locks the whole integration read-only, a `PUT` is 400 `env_locked`, a pending change for it is cancelled not applied. **UC-8** hot reload → Api ≤30 s via `DbSettingsProvider`, Worker via `SettingsChanged`; a running job keeps its starting snapshot; an undecryptable secret at boot → degraded (paid dispatch paused) + alert, never an empty key.

## 5. API and events

| Route | Policy | Contract | Codes | Task |
|---|---|---|---|---|
| `GET /admin/settings`, `PUT /admin/settings/{integration}` (`If-Match`), `POST …/test`, `GET …/versions`, `POST …/rollback/{version}`, `GET/POST /admin/worker-nodes`, `PATCH /admin/worker-nodes/{id}` | `OwnerPolicy`+`IReauthGuard` | D6; node CRUD incl. adapter key (secret, write-only); `admin` role → 404 | 200/201/202/204, 400 `setting_invalid`/`setting_secret_required`, 401 `reauth_required`, 409 `setting_change_pending`, 412 `setting_conflict` (09 W3 CR-31), 502 `setting_test_failed` | **09.10.2-T1** (key purposes **09.10.1-T2**) |
| `GET /admin/settings/changes?status=pending`, `POST …/{id}/approve`, `POST …/{id}/cancel` | `OwnerPolicy`+`IReauthGuard` | D7 | 200/202/204, 401 `reauth_required`, 404, 409 | **09.10.2-T2** |

DTOs: `SettingsCatalogDto(Items: SettingIntegrationDto[])`; `SettingIntegrationDto(Integration, Critical, LockedByEnvironment, BaseUrlRule, Keys: SettingKeyDto[], PendingChange?)`; `SettingKeyDto(Key, IsSecret, Value?, SecretHint?, Version, UpdatedBy?, UpdatedAt?, NodeManaged?)`; `SettingChangeDto(Id, Integration, ChangedKeys, RequestedBy, RequestedAt, ApplyAfter, Status)`; `WorkerNodeDto(Id, Name, BaseUrl, Capacity, State)`. Commands: `UpdateSettingsCommand(Integration, Values, Secrets, Reason?, IfMatch)`, `TestSettingCommand(Integration, BaseUrl?, Secret?)`, `RollbackSettingCommand(Integration, Version, Secret?, Reason)`, `ApproveSettingChangeCommand(Id)`, `CancelSettingChangeCommand(Id, Reason)`.

Attributes (exactly one each, 09.3.6-T1 arch test): `[Audited("settings.changed", Category.Support, Visibility.Internal)]` on update/rollback/nodes, `change_pending`/`change_approved`/`change_cancelled`/`change_applied` on their handlers; reads `[NotAudited]`; no `[AllowAnonymousRequest]` in this folder. Events: only the existing `SettingsChanged` (same-transaction publish on an applied change) and `email_requested.v1` for `setting_change_pending` — no new event type (D20).

## 6. UI

Backend-only folder (Area BE); the owner "approved design before implementation" rule is satisfied upstream — **no `-T0` task here** (§C8.1 fixes exactly these 6). Consumers (W3 CR-14 back-edges):

| Screen | Plan-10 owner | Data |
|---|---|---|
| `/admin/settings` (cards, test, versions, rollback, locked-by-environment, pending banner) | `10.9-admin-ops/`; shell/routing `10.9-admin-core/10.9.1-T1`; design `10.9.1-T0-ui-design.md` | `GET/PUT /admin/settings*`, `…/test`, `…/versions`, `…/rollback/{version}` |
| `/admin/settings/nodes` | `10.9-admin-ops/` | `GET/POST /admin/worker-nodes`, `PATCH …/{id}` |
| `/admin/approvals` (pending changes, approve/cancel) | `10.9-admin-core/10.9.4-T2` | `GET/POST /admin/settings/changes*` |
| `/cancel-request#kind=setting_change&…` | 10 renders; route owner **09.10.8-T1** | not this folder |

Client contract this API must keep true: 202 bodies are pending states, not errors; 401 `reauth_required` opens step-up and retries once; `lockedByEnvironment` renders read-only with a break-glass note; `error.setting_invalid`/`setting_secret_required`/`setting_test_failed` exist vi+en (D18); the version list never shows a secret, only the hint.

## 7. Jobs and integrations

| Kind | Name | Contract |
|---|---|---|
| Job | `SettingChangeApplyJob` | 1 min, ids-only, `LIMIT 50 FOR UPDATE SKIP LOCKED`, one `ISender` command/id, `[JobPolicy(Retries=0)]`; owner **09.10.2-T2** |
| Job | `SettingSecretPurgeJob` | daily, `system_setting_versions` only, keeps `secret_hint`, batches ≤5,000, `[JobPolicy(Retries=0)]`; owner **09.10.1-T2** |
| Outbox | `SettingsChanged` | existing; published on apply; Worker reload consumer (09.10.3-T1) |
| Outbox | `email_requested.v1` | `setting_change_pending` per recipient (W3 CR-8 params) |
| Options | `SettingsOptions` | `ChangeTimeLock`(24h), `CacheSeconds`(30), `PurgeDays`(7), `TestTimeoutSeconds`(5), `MaxKeysPerRequest`; hosts/allow-lists/CIDR stay env-only |
| HTTP clients | reuse 09.3.5-T1's named clients (W3 CR-7) | settings test call shares 09.10.2-T3's `ConnectCallback` factory, `AllowAutoRedirect=false`, `UseProxy=false` |

Hot reload (09.10.3-T1): `DbSettingsProvider : IOptionsMonitor<T>` per integration, 30 s cache invalidated by `SettingsChanged` and the local write path; typed clients (`OpenAI text`, `mpt-adapter`, payment, SMTP) read current options per request; an in-flight job keeps its snapshot.

## 8. Security and privacy

Every route is `OwnerPolicy`+`IReauthGuard` (5 min window); any other role/anonymous → 404 `not_found`, never 403 (D5). Secrets stored only as `secret_ciphertext` (Data Protection, KEK-protected key ring, 09.9.4-T1), purpose-bound per `(integration,key)`/node; `secret_hint`=last 4; `SecretString` masked in every sink; `[Audited]` writes keys/hints only, never values; the log-scan integration test seeds a unique secret and asserts zero occurrences in logs/traces/audit/exports. `system_settings*`/`worker_nodes` are **not** `IUserOwned` and reached only via `/admin` — `IgnoreQueryFilters` is not needed and must not appear here; every route is added to 09.2.3-T2's cross-tenant table (its test enumerates the admin route set). Egress: `SocketsHttpHandler.ConnectCallback` re-validates the resolved IP at connect time for every call (loopback, link-local incl. `169.254.169.254`, `0.0.0.0/8`, IPv4-mapped/compatible IPv6 normalized first, always blocked; `mpt` additionally CIDR-restricted); the same validated IP is dialled (no re-resolve). Audit: one `audit_logs` row per mutation (`support`/`internal`); critical actions also raise the D9 security event via 09.10.4-T3 (soft). No personal data beyond `updated_by`/`requested_by`; nothing here reaches `/me/activity` (all rows `internal`).

## 9. Test plan

`NightTales.Domain.UnitTests`: `SystemSetting`/`SystemSettingChange` invariants (version increment, secret/value mutual exclusion, `ApplyAfter=+24h`, `WorkerNodeId` only for `(mpt,api_key)`); `SecretString` never reveals its value via `ToString`/serialization; egress IP-normalization table (IPv4-mapped/compatible IPv6).

`NightTales.Application.UnitTests` (hand-written `NightTales.TestKit` fakes, no mocking library): catalog validator per §5.6 row; `UpdateSettingsCommandHandler` (env-locked, `setting_secret_required`, stale `If-Match`, non-critical happy path, critical delegate path); `ApproveSettingChangeCommandHandler` (eligible/ineligible approver, second-approve no-op); `SettingChangeApplyJob` (before/after time-lock, env-lock-mid-flight cancel); `SettingSecretPurgeJobTests` (only touches versions, never live row); `EgressGuardTests` (every blocked range + `mpt` CIDR branch); `SettingsSnapshotCacheTests` (TTL + explicit invalidate).

`NightTales.Api.IntegrationTests` (real PostgreSQL 17 on `NTS_TEST_PG` via `PostgresFixture`; 09 W3 CR-35, Part A D13 harness): per-route 404-for-admin with a DB-state assertion (no row mutated); purpose-binding cross-row decrypt failure; `PurgeJobLiveSecretSurvivesTests` (live secret decrypts and `…/test` succeeds after a purge run); `CriticalChangeFlowTests` end-to-end (submit→202→no-op-before-lock→advance clock→apply-exactly-once→cancel-never-reapplies) with a DB-state assertion after each step; `ConnectTimeValidationTests` (loopback/metadata/rebinding refused, dialled IP == validated IP); `HotReloadEndToEndTests` (Api ≤30 s, Worker via real outbox); `DegradedStartTests` (wrong KEK → `/health/ready` still healthy, dispatch paused, one alert, no row mutated); `LogScanTests` (seeded secrets absent from every sink).

`NightTales.Architecture.Tests`: every command exactly one of `[Audited]/[NotAudited]/[AllowAnonymousRequest]`; no `IgnoreQueryFilters` outside `Application/Features/Admin/**`; every settings route present in 09.2.3-T2's cross-tenant table; `SettingSecretPurgeJob` only accesses `system_setting_versions`.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.10.1-T1 | M (1.0) | BE | S · M2 | 09.9.4-T1, 09.2.2-T2 | 09.10.1-T2, 09.10.2-T1, 09.10.3-T1, 09.3.3-T2 soft, 06.1.2-T3 (09 W3 CR-hand-off) |
| 09.10.1-T2 | S (0.5) | BE | S · M2 | 09.10.1-T1, 09.2.6-T1 | 09.10.2-T1 soft (node keys) |
| 09.10.2-T1 | M (1.0) | BE | S · M2 | 09.10.1-T1, 09.10.1-T2 | 09.10.2-T2, 09.10.2-T3, 09.10.6-T1, 09.10.3-T1 soft, 12.3.3-T2 (12 CR-11, 09 W3 CR-13), 10.9.3-T1 (09 W3 CR-hand-off) |
| 09.10.2-T2 | M (1.0) | BE | S · M2 | 09.10.2-T1, 09.10.4-T2 | 09.10.8-T1 |
| 09.10.2-T3 | S (0.5) | BE | S · M2 | 09.10.2-T1 | — |
| 09.10.3-T1 | S (0.5) | BE | S · M2 | 09.10.1-T1, 09.2.4-T1; 09.10.2-T1 soft | 02.8.3-T3 soft (audit status via settings) |

Files: `09.10.1-T1-settings-store.md` · `09.10.1-T2-purpose-chain-and-purge.md` · `09.10.2-T1-settings-admin-api.md` · `09.10.2-T2-critical-changes.md` · `09.10.2-T3-connect-time-validation.md` · `09.10.3-T1-hot-reload.md` = **4.5 d raw**.

Re-estimate: **no change** — §C9 confirms M+S+M+M+S+S = 4.5 d, inside the 09 S row (18.5 raw / 22.2 buffered, W3-O1); high-level §6 rows match one-for-one (`plans/09-backend-api.md:548-554`). Cross-folder prerequisites this folder needs: `09.9.4-T1` (key ring), `09.2.6-T1` (job list), `09.2.4-T1` (outbox), `09.10.4-T2` (owner-approval path for the "different owner approves" branch — hard per §C8.1).

## 11. Rollout

1. **M2 order.** 09.10.1-T1 → 09.10.1-T2 → 09.10.2-T1 → (09.10.2-T2, 09.10.2-T3 either order) → 09.10.3-T1. 09.10.6-T1/09.10.8-T1 (other folders) follow 09.10.2-T1/T2 respectively.
2. **Migration + seed.** Idempotent §5.6 + `captcha` seed (W3 CR-2); the partial pending-change index (W3 CR-1f) ships in the same batch.
3. **Environment first.** `DP_KEK` must exist before settings is exercised (11.5.2, 09.9.4-T1); allow-lists/render CIDR/redirect URIs stay env-only — an env value is the documented break-glass path.
4. **Not flag-gated** — all six tasks are `S · M2`, no feature flag (§C8(e)); rollout is the deploy plus the existing secrets-rotation runbook (node image key rotates on the node, never in-app, 11 CR-3).
5. **Post-deploy walk (gate 09-W3-settings).** Non-critical change visible in Api+Worker ≤30 s without restart; `youtube` critical change → 202 pending → time-locked apply → cancellable by a second owner's link; copied ciphertext fails to decrypt; after `SettingSecretPurgeJob` the live secret still works and rollback asks for re-entry; log scan finds nothing; both jobs visible in Hangfire with ids-only arguments; `settings_changes_pending` gauge scraped.
6. **Rollback.** Revert by deploy (values live in the DB, not the image); an operator re-enters a secret only through the UI; never restore a secret from `system_setting_versions` (purged after 7 days) or from a DB backup alone (ciphertext only; KEK is separate, 11.5.2).
