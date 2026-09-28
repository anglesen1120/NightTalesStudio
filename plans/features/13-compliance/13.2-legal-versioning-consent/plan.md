# 13.2 — Legal versioning, consent resolution and the 18+ gate (S · M2)

> Folder `plans/features/13-compliance/13.2-legal-versioning-consent/` (Part B §B0). Contract: `../README.md` §B0, §B1 (K7–K18), §B2 (F6/F10/F11), §B3 **D6–D8**, §B4 (CR-7/10/11/12/13/16/17), §B5 (W2/W3), §B7 (gate **13-S-legal**), §B10 (O5/O6, T6/T7), `### B8.1`.
> Builds on the P slice (`../13.1-legal-acceptance/`, DoR 2026-09-25) without reopening it (T7). Not legal advice: the 18+ rule and PDPL duties stay ⚠️ until 13.6.1-T1 and the lawyer review. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**Goal.** After Gate 2 the platform must publish a new privacy/terms version **without touching consent logic**, prove which version every user accepted, block every user at the next login until they accept a material change, refuse a version whose text is not deployed, and record the 18+ rule (13 §5.1/§5.2, D8).

**In scope.** `13.2.2-T1` (M, BE/Ops) creates `legal_documents` and makes it the **runtime source of the current version** per (kind, locale), adds `nts-worker legal publish`, takes over the version CI check and adds the startup assertion (D6, F6/F10, CR-7/12/16, T6). `13.2.1-T2` (XS, BE/FE) adds the all-user blocking re-acceptance (`pendingAcceptance` on login/refresh/`GET /me/legal`; D7, F2). `13.2.3-T1` (XS, BE/FE) records the 18+ self-attestation as `user_consents.document = 'age_18'`, ships the attestation text, the enum + config key (CR-11) and the registration rule 400 `age_attestation_required` (D8, F11, CR-10, O5).

**Out of scope (owned elsewhere, never re-implemented — D18).** Lawyer review/publish of the texts (`13.1.1-T2`/`13.1.2-T2`), content policy 13.1.3-T1, cookie notice 13.1.4-T1, PDPD memo 13.6.1-T1 (`13.1-legal-publication/`); consent store/identity/`POST /auth/register`/sessions (`09.3.1-T1`, `09.3.1-T3`, `09.3.2-T1`, consumed only); the **shapes** of the P contracts (`ILegalAcceptance.EnsureCurrentAsync`, `LegalAcceptanceDto`, `GET/POST /me/legal`, `LegalAcceptanceDialog`, `tools/ci/check-legal-versions.py`, `LegalDocumentsOptions`) — this folder replaces the resolver *behind* them (T7); the web surfaces register-18+-checkbox and post-login blocking dialog (`10.3.2-T1`, CR-13) and error mapping (`10.2.2-T1`); an admin publishing UI (O6); deletion/export/report work (`13.3-data-rights/`).

**Phase.** S · M2 (00 §4.1: "where a plan names M1 for an S item, read M2"). No flag of its own; the slice sits behind `saas_registration` (O9/13.4.1-T1) until OAuth verification is granted. Gate contribution: `13-S-legal` (§B7) with `13.1-legal-publication/` and `10.3.2-T1`.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| A1 | `ILegalAcceptance.EnsureCurrentAsync(Guid userId, CancellationToken ct) → ErrorOr<Success>` (Abstractions/Compliance); failure = 409 `legal_acceptance_required` with `errors.documents` sorted alphabetically (`["privacy","tos"]`) | ✅ `../13.1-legal-acceptance/13.2.1-T1-…md:46,60-64,94`; D3 |
| A2 | `LegalAcceptanceDto { IReadOnlyList<LegalDocumentStatusDto> Documents }`; `LegalDocumentStatusDto { string Document, string CurrentVersion, string? AcceptedVersion, DateTimeOffset? AcceptedAt }`; wire `{ documents: [...] }`, order **`tos`, `privacy`** | ✅ `…/13.2.1-T1-…md:48,62`; `…/plan.md:62` |
| A3 | `AcceptLegalDocumentsCommand(string Tos, string Privacy)` + validator (`^\d+\.\d+\.\d+$`, ≤32) + `[Audited("account.legal_accepted", Category.AccountSecurity, Visibility.User)]`; records the **server's** version via `IConsentWriter`, all-or-nothing | ✅ `…/13.2.1-T1-…md:51-52,67-71,104-109` |
| A4 | `LegalDocumentsOptions` (`Infrastructure/Identity/`) binds `LegalDocuments:Current`: `Tos`/`Privacy`, `[Required, StringLength(32, MinimumLength = 1)]`, `ValidateDataAnnotations().ValidateOnStart()`; 09.3.1-T1 reads the same section | ✅ `…/13.2.1-T1-…md:53,61,129,170`; `09.3.1-T1:93,146` |
| A5 | `enum LegalDocument { Tos, Privacy }` (snake_case); `IConsentWriter.RecordAsync(Guid userId, LegalDocument document, string version, IPAddress? ip, CancellationToken ct)`; `user_consents(id, user_id, document, version, accepted_at, ip)`, `HasConversion<string>().HasMaxLength(16)` + `HasEnumCheck<LegalDocument>`, index `ix_user_consents_user_id_document`, append-only, commits in the caller's transaction | ✅ `09.3.1-T1:29,48-50,96-110,146`; K2 |
| A6 | `POST /auth/register` takes `IReadOnlyList<ConsentAcceptance> Consents` (`ConsentAcceptance(LegalDocument Document, string Version)`) — "exactly one `tos` + one `privacy` (+ `age_18` once 13's CR-11 lands)" — recording each via `IConsentWriter` | ✅ `09-backend-api/README.md:1040`; `09.3.1-T3-…md:29,59-60` |
| A7 | `POST /auth/login` → `LoginResult` (`09.3.2-T1:146`) whose `Access` is the token record `IssuedAccessToken(string Token, DateTimeOffset ExpiresAt, string Kid)` (`:29`; `:148` returns it from refresh) — so `pendingAcceptance` belongs on `LoginResult` **and** on the refresh endpoint's response body (a `RefreshResult` wrapper), never on the token record itself | ✅ `09.3.2-T1-…md:29,146,148,200-206`; ⚠️ O-1 |
| A8 | `legal_documents(kind, version, locale, effective_at, content_url)`; `user_consents.document` today `tos`/`privacy` | ✅ K8 (`00-overview.md` §6:323,:274) |
| A9 | CR-7 adds `published_by uuid` + `checksum text` (sha256 of served file) and `user_consents.document` +`age_18`; CR-11 adds `LegalDocument.Age18` + `LegalDocuments:Current:Age18` (1–32 chars), `RecordAsync` untouched; CR-10 registers `age_attestation_required` 400 and `legal_content_missing` 409; CR-12 makes the DB the API source and moves the CI script `13.2.1-T1`→`13.2.2-T1`; CR-16 gives `legal_documents` to this task in 09.2.2 conventions | ✅ `../README.md` §B4 |
| A10 | 13 owns the register 18+ checkbox (400 `age_attestation_required`) and re-acceptance "driven by `pendingAcceptance` in the login response / `GET /me/legal`"; consumers `10.3.2-T1` + mapper `10.2.2-T1`; `LegalAcceptanceDialog` P-owned, reused (WT-W5) | ✅ `10-web-ui/README.md:604`,`:517`; CR-13 |
| A11 | Legal confidence unchanged: Law 91/2025/QH15 (in force 2026-01-01 ⚠️ secondary), Decree 356/2025/ND-CP, sanctions Decree 330/2026/ND-CP, Decree 13/2023 superseded; obligations ⚠️ (lawyer) | ✅/⚠️ K16, D16 |
| A12 | Reusable: operator CLI `nts-worker <noun> <verb>` (`deletions reapply --since`, `flags set <key> on`), `RequireFeature(...)` → 404 `feature_disabled`, `IBlobStore` (+`ListKeysAsync`) | ✅ K18 |

## 3. Domain and data

**New table `legal_documents` (owned here — CR-16).** Migration `<yyyyMMddHHmm>_13_LegalDocuments` under `src/NightTales.Infrastructure/Persistence/Migrations/`, per `09.2.2-T1` (expand-only, snake_case, no seed in the migration).

```sql
CREATE TABLE legal_documents (
    id           uuid        PRIMARY KEY,
    kind         text        NOT NULL,
    version      text        NOT NULL,
    locale       text        NOT NULL,
    effective_at timestamptz NOT NULL,
    content_url  text        NOT NULL,
    published_by uuid        NULL,
    checksum     text        NULL,
    CONSTRAINT ck_legal_documents_kind    CHECK (kind   IN ('tos','privacy','content_policy','age_gate')),
    CONSTRAINT ck_legal_documents_locale  CHECK (locale IN ('vi','en')),
    CONSTRAINT ck_legal_documents_version CHECK (version ~ '^[0-9]+\.[0-9]+\.[0-9]+$')
);
CREATE UNIQUE INDEX ux_legal_documents_kind_locale_version ON legal_documents (kind, locale, version);
CREATE INDEX ix_legal_documents_kind_locale_effective_at ON legal_documents (kind, locale, effective_at DESC);
```

`kind`/`version`/`locale`/`effective_at`/`content_url` are 00 §6:323 verbatim; `published_by`/`checksum` are CR-7. **No other column**: the publish instant stays in the audit row and no `is_current`/`updated_at` is added (the resolver derives the current row) [DECISION]. Index names are [INFERENCE] (00 §6 lists none): `ux_…` makes re-publish idempotent (I2), `ix_…` is the resolver path. `id` is UUIDv7 (`Guid.CreateVersion7()`, `ValueGeneratedNever()`) like `user_consents` (A5). `age_gate` is the **fourth** kind value while K8 lists three — D8 publishes the attestation "by the same path as the other texts"; the check is implemented and the missing CR line against `00-overview.md` §6:323 is raised (**O-2**).

**Resolver (D6/CR-12).**

```sql
SELECT version, content_url, effective_at, checksum
FROM legal_documents
WHERE kind = @kind AND locale = @locale AND effective_at <= now()
ORDER BY effective_at DESC, version DESC
LIMIT 1;   -- ix_legal_documents_kind_locale_effective_at
```

`effective_at DESC` first, `version DESC` as tiebreak; equal `effective_at` is ordered deterministically by `LegalVersionComparer` (numeric segments: `1.10.0 > 1.9.0`, a text sort is wrong) [INFERENCE name, DECISION]. No row → the config value is used (**seed/bootstrap only**, D6), so a first boot with an empty table validates. One indexed lookup per (document, locale); `LegalVersionResolver` is **scoped** (no `IMemoryCache`), so a publish is live on the next request without a restart [DECISION]. The DB's `now()` is the only clock in the SQL — the app clock is never injected into it [DECISION].

**Reused `user_consents` (shape unchanged, A5).** Only the enum value `age_18` is added (CR-7/CR-11); `HasMaxLength(16)` already fits and the enum check is extended in `09.3.1-T1`'s configuration. Nothing added, renamed or indexed here.

| # | Invariant | Enforced by |
|---|---|---|
| I1 | Current version = max row with `effective_at <= now()`; whenever a row exists, config equals it — a future-dated scheduled row is excluded (I5) | resolver SQL + startup check (§7) |
| I2 | Re-publishing the same (kind, locale, version) is idempotent when `checksum` matches, refused (no row) when it differs | `ux_…` + verb handler |
| I3 | A version whose served static file cannot be fetched is never written | verb pre-flight (T6, D6) |
| I4 | Rows are append-only; a correction is a **new** version with a later `effective_at`, never an `UPDATE` | no update/delete verb; migration test asserts no mutable column |
| I5 | `effective_at > now()` is legal and excluded from the resolver, `pendingAcceptance` and the gate until its instant | resolver + pending query |
| I6 | Acceptance stays per (`document`, `version`), append-only, recording the server's version | reused P command (not reopened) |

## 4. Use cases

**UC1 — publish a version.** Given the lawyer-reviewed `privacy.1.1.0.vi|en.md` are deployed, When the operator runs `nts-worker legal publish --doc privacy --version 1.1.0 --locale vi --effective-at 2026-10-01T00:00:00Z --content-url /content/legal/privacy.1.1.0.vi.md`, Then the verb validates the version (`^\d+\.\d+\.\d+$`, greater than the current one by `LegalVersionComparer`), proves the file exists (HTTP `HEAD` on the configured static origin), computes its sha256, inserts one row with `published_by` = operator, writes audit `legal.document_published` (`support`, `internal`; data = kind/version/locale/effective_at/checksum, never the text) and exits 0 printing `published kind=privacy version=1.1.0 locale=vi … checksum=sha256:…`. The web deploy stays a separate step; the startup/CI check keeps page and registry equal.

**UC2 — undeployed text (409 `legal_content_missing`, T6/D6).** Given `/content/legal/privacy.1.2.0.vi.md` answers 404 or the static origin is unreachable, Then no row and no audit row are written, the command returns `Error.Conflict(ErrorCodes.LegalContentMissing, …)`, the CLI prints `code=legal_content_missing` and exits 3. "Cannot prove the file exists" is never "it exists".

**UC3 — scheduled version (I5).** Given a `tos 1.2.0` row with `effective_at = now() + 14 d`, When a user logs in during those days, Then `pendingAcceptance.documents` is empty and `GET /me/legal` still reports `currentVersion = 1.1.0`; once the instant passes the resolver picks 1.2.0 and the login response reports it as pending (`FakeClock` for the read model, explicit SQL `effective_at` for the resolver).

**UC4 — startup assertion (D6).** Given the API boots with config `Privacy = 1.1.0` and the current `privacy` row (max `version` with `effective_at <= now()`) is `1.1.0`, Then startup succeeds; Given the config still says `1.0.0`, Then startup **fails fast** with `legal_documents registry/config version mismatch: privacy 1.0.0 != 1.1.0`; Given a `privacy 1.2.0` row with `effective_at = now() + 14 d` alongside the current `1.1.0`, Then startup still **succeeds** — the future-dated scheduled row is not the current row (I5, 13 CR-33); Given the table is empty for a pair, Then only the config is checked (seed path). No file-system or network access at boot [DECISION] — manifest ↔ config ↔ row equality is the CI script's job.

**UC5 — all-user re-acceptance at login.** Given the user accepted `privacy 1.0.0` and current is `1.1.0`, When `POST /auth/login` succeeds, Then the body carries `pendingAcceptance: { documents: [{ document: "privacy", currentVersion: "1.1.0" }] }` and the token is still issued (no new error code, no forced re-auth, tokens stay valid); When 10.3.2-T1's blocking dialog accepts, Then `POST /me/legal/accept {"tos":"1.1.0","privacy":"1.1.0"}` returns 200 and the next `POST /auth/refresh` (and `GET /me/legal`) reports `documents: []`. Already-current users always get an **empty array** — never `null`, never absent — on all three responses. A stale accept still answers 409 `legal_version_outdated` with alphabetically sorted `errors.documents` and writes nothing (P regression guard, T7).

**UC6 — 18+ attestation at registration.** Given the form is submitted without the box, Then `POST /auth/register` → 400 `age_attestation_required` (CR-10), no user row, no consent rows; Given it is checked, Then the flow records `ConsentAcceptance(LegalDocument.Age18, LegalDocuments:Current:Age18)` through the existing `IConsentWriter` path (A6): one `user_consents` row (`document='age_18'`, `version='1.0.0'`, request IP) in the same transaction as the user row.

**UC7 — re-publish (I2).** Identical version + identical checksum → exit 0 `already published`, no second row, no second audit row; a changed served text → refuse (exit 2, `code=legal_version_conflict`) and write nothing, so a shipped text is never silently replaced.

## 5. API and events

**No new public or admin route (O6, D18)** — the only new surface is the operator verb.

| Surface | Auth | Change | Errors |
|---|---|---|---|
| `nts-worker legal publish --doc <kind> --version <x.y.z> --locale <vi\|en> --effective-at <ISO-8601 UTC> --content-url </path>` | operator CLI, `system:operator` (same host as `deletions reapply` / `flags set`, A12) | new verb returning `ErrorOr<LegalDocumentVersion>` | `legal_content_missing` 409, `legal_version_conflict` (exit 2), `validation_failed` for a malformed version/kind/locale |

| Response | Change (D7) | Status |
|---|---|---|
| `POST /auth/login` (success) | body gains `pendingAcceptance` = `{ documents: [{ document, currentVersion }] }`, empty array when current | 200 (unchanged) |
| `POST /auth/refresh` (success) | same property | 200 (unchanged) |
| `GET /me/legal` | `LegalAcceptanceDto` gains `pendingAcceptance`; `documents` unchanged (A2 order) | 200 (unchanged) |
| `POST /me/legal/accept` | **unchanged** (A3) | unchanged |
| `POST /auth/register` | body unchanged; `Consents` may carry `age_18` (CR-11) | **202** (unchanged, `09.3.1-T3:112`) + new 400 `age_attestation_required` |

New DTOs: `PendingAcceptanceDto { IReadOnlyList<PendingDocumentDto> Documents }`, `PendingDocumentDto { string Document, string CurrentVersion }`. Order is **`tos`, `privacy`** (projection order, A2), deliberately *not* the gate's alphabetical `errors.documents` — a test pins both so nobody "unifies" them [DECISION, trap]. Codes come from the existing catalogue (`legal_acceptance_required` 409, `legal_version_outdated` 409 (P), `legal_content_missing` 409 (CR-10), `age_attestation_required` 400 (CR-10)); no code name is invented here. **No outbox event, no SignalR message, no e-mail** — publishing/accepting changes no publication, channel or schedule state (same rule as the P slice, `…/13.2.1-T1-…md:82,128`).

## 6. UI

**No Angular code is produced here** (owner rule 2026-09-25: a web surface needs an approved design first; those designs/widgets are the consumers'):

| Surface | Owner | This folder's contract |
|---|---|---|
| Register 18+ checkbox (blocking, bilingual, link to the attestation text) | `10.3.2-T1` (CR-13) | `POST /auth/register` accepts `age_18`; missing → 400 `age_attestation_required`; text at `/{vi\|en}/age-gate` (**O-3**) |
| Post-login blocking re-acceptance dialog | `10.3.2-T1`, reusing P's `LegalAcceptanceDialog` + store (`@features/account`) | `pendingAcceptance` on login/refresh/`GET /me/legal`; dialog links `/vi\|en/privacy`, `/vi\|en/terms` unchanged |
| Error → dialog/form mapping | `10.2.2-T1` | the two codes and their field placement |
| Operator publish feedback | CLI (no screen, O6) | one stdout line per publish/refusal |

i18n: this folder adds codes, not sentences; user-visible copy is 10's (vi + en in the same change, D17). The one content artifact owned here is `web/src/content/legal/age-gate.1.0.0.vi.md` + `.en.md` with its `manifest.json` entry in the same change (D1/D8). a11y (D17, WCAG 2.1 AA) applies to the consumer screens (labelled native checkbox, error association, the P dialog's focus trap/restore); 360/768/1280, dark/light and focus order are reviewed in `10.3.2-T1`'s design task.

## 7. Jobs and integrations

| Integration | Direction | Contract |
|---|---|---|
| `nts-worker legal publish` | this folder | operator verb; no Hangfire job — publishing is a deliberate human action (O6) |
| `LegalVersionResolver` | this folder | scoped resolver over `legal_documents` with the config seed fallback; consumed by `LegalAcceptanceReader` (gate + `GET /me/legal`), the accept path and the pending query |
| `LegalVersionStartupCheck` | this folder | `IHostedService` after migrations: per (kind, locale) with a `legal_documents` row, asserts `manifest.json`'s version == the **current** row's `version` (max `version` over `effective_at <= now()` — a future-dated scheduled row is excluded, 13 CR-33), else fail startup (D6); `LegalDocuments:Current:*` is consulted only as the seed/bootstrap fallback for a (kind, locale) with **no** row yet, never compared after the first publish — logs `legal_documents_registry_ready kind=… locale=… version=…` |
| `tools/ci/check-legal-versions.py` | moved here (CR-12) | P's check (manifest ships both locales) plus, with `--db <conn>`, manifest's version == the max `legal_documents` row with `effective_at <= now()` (D6, 13 CR-33); config is **not** part of this comparison (seed-only, never re-checked after publish); `age_gate` included once its entry exists (13.2.3-T1) |
| `13.1.1-T2` / `13.1.2-T2` (soft) | upstream | the reviewed version is what gets published; `13-S-legal` requires `1.1.0` published, not only the unreviewed `1.0.0` drafts (O1/O4) |
| `13.6.1-T1` memo (soft) / `13.5.2-T1` calendar (soft) | up/downstream | a memo item that changes a text implies a version bump through this path (D16); the calendar's "policy review every 6 months" row (D15) references it |
| `09.2.2-T1` (hard); `09.3.1-T1`, `09.3.1-T3`, `09.3.2-T1` (consumed) | upstream | migration conventions + `_13_LegalDocuments` naming; `IConsentWriter`, `LegalDocument`, the register command's `Consents`, the login/refresh handlers (A5/A6/A7) |

Observability (pinned names): audit `legal.document_published`; log events `legal.publish.refused` (warning: code + kind/version/locale, no text/path body) and `legal.version_mismatch` (critical, startup); counter `legal_documents_published_total{kind,locale}`. No new scraper is needed by 08.1 [DECISION].

## 8. Security and privacy

**Authorization.** The verb runs as `system:operator` (A12) — no API route, no role escalation, no new public surface. The registry is **not** exposed: the wire carries only `document` + `currentVersion`, never `content_url`, `checksum` or `published_by` [DECISION]. **Audit.** `legal.document_published` (`support`, `internal`) with kind/version/locale/effective_at/checksum and the operator as actor; no text, no path body, no IP. P's `account.legal_accepted` (`user` visibility, A3) stays the user-facing record. **Secrets.** None added; the checksum is computed from public content; the static origin is the existing public-origin setting; no Data Protection purpose is introduced or changed (D17). **Tenant/isolation.** `legal_documents` is global (not `IUserOwned`) like `system_settings` and is read under the pinned system scope (K18); `pendingAcceptance` reads `user_consents` for `ICurrentUser.Id` only, so a cross-user read is structurally impossible (P rule, A1). **Logging/injection.** The verbose path never logs served bytes; the sha256 is the only content-derived value stored; all verb values are validated and parameterised. **Legal confidence.** No user-facing text claims PDPL compliance; 18+ stays a self-attestation; the attestation text stays a draft (O1) until the lawyer review and 13.6.1-T1's memo land (A11/D16).

## 9. Test plan

Hand-written fakes from `NightTales.TestKit` only (no mocking library). Integration tests on real PostgreSQL 17 via `PostgresFixture`; time via `FakeClock` plus explicit SQL `effective_at`.

**`tests/NightTales.Domain.UnitTests`** — `LegalVersionComparerTests.Compare_SegmentLengths_VersionOrderIsNumeric` (`1.10.0 > 1.9.0`, `1.0.10 > 1.0.9`, malformed rejected); `LegalDocumentVersionTests.EffectiveAtInFuture_IsNotCurrent_BeforeInstant_AndIsCurrent_After`.

**`tests/NightTales.Application.UnitTests`** — `LegalVersionResolverTests.Resolve_NoRow_FallsBackToConfigSeed`, `_FutureEffectiveAt_Ignored`, `_SameEffectiveAt_DeterministicByVersion`; `PublishLegalDocumentHandlerTests.Handle_MissingServedFile_ReturnsLegalContentMissing_NoRowNoAudit`, `_LowerVersion_ReturnsValidationFailure`, `_SameVersionSameChecksum_Idempotent_NoSecondRow`; `PendingAcceptanceReaderTests.GetPending_ReturnsOnlyChangedDocuments_InTosThenPrivacyOrder`, `_Current_ReturnsEmptyArray`, `_FutureVersion_NotPending`; `AgeAttestationTests.Require_Missing_ReturnsAgeAttestationRequired`, `_Present_ReturnsSuccess`, `_WrongVersion_ReturnsAgeAttestationRequired`; `StartupCheckTests.Validate_ConfigDiffersFromMaxRow_Throws`, `_EmptyTable_UsesConfig`.

**`tests/NightTales.Api.IntegrationTests`** — `LegalDocumentsMigrationTests.Migration_CreatesColumnsIndexesAndChecks` (`information_schema`; duplicate (kind, locale, version) rejected; bad `kind`/`version` rejected; no mutable column, I4); `LegalPublishEndToEndTests.Publish_ThenLogin_ReportsPendingAcceptance_ThenAccept_ClearsIt` (verb in-process; `POST /auth/login` → `[{document:"privacy",currentVersion:"1.1.0"}]`; accept → 200; `POST /auth/refresh` → `[]`; raw SQL: two new `user_consents` rows + one `legal.document_published` row with `visibility = internal`); `…Publish_UndeployedContentUrl_ReturnsLegalContentMissing_NoRow`; `…Publish_ScheduledVersion_NotPendingBeforeInstant`; `RegisterAgeGateTests.Register_WithoutAgeConsent_Returns400AgeAttestationRequired_NoUserRow`, `_WithAgeConsent_WritesDocumentAge18Row`; `StartupCheckIntegrationTests.Startup_WithStaleConfig_Fails`; `LegalAcceptanceRegressionTests` (P's stale-accept 409, sorted `errors.documents`, nothing written — copied unchanged; a change here means T7 was violated).

**`tests/NightTales.Architecture.Tests`** — `EndpointCoverageTests.NoNewEndpoints_For13_2` (route set = P's + the 13.3.x CR-8 routes); `AuditedAttributeTests.PublishLegalDocumentCommand_HasDottedActionAndExplicitCategoryVisibility`; `ConsentWriterReuseTests.OnlyOneImplementationOfIConsentWriter` (D18).

**Manual/operational evidence (feeds `13-S-legal`)** — publish `privacy 1.1.0` vi+en on staging, confirm the page shows `1.1.0`, `check-legal-versions.py` green, a synthetic non-owner login shows `pendingAcceptance`, accepting clears it, an undeployed version fails closed with `legal_content_missing`; plus the startup-mismatch demo and the 400 `age_attestation_required` capture — handed to 13.5.1-T1's pack (binaries outside the repo, D14).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 13.2.2-T1 | M (1.0) | BE/Ops | S · M2 | 13.1.1-T2 (soft), 13.1.2-T2 (soft), 13.6.1-T1 (soft), 09.2.2-T1 | 13.2.1-T2, 13.2.3-T1 (soft), 13.5.2-T1 (soft) |
| 13.2.1-T2 | XS (0.25) | BE/FE | S · M2 | 13.2.1-T1, 13.2.2-T1, 10.2.2-T1 | 10.3.2-T1 |
| 13.2.3-T1 | XS (0.25) | BE/FE | S · M2 | 10.3.1-T0, 13.2.2-T1 (soft), 09.3.1-T1 | 10.3.2-T1, 13.5.1-T1 |

Folder total **1.5 d** raw. Epic roll-up (§B9): 13.2 (Consent/versioning) 1.25 → **2.5** — P half 13.2.1-T1 (1.0, Part A §9) + 13.2.2-T1 (1.0, new/F6) + 13.2.3-T1 (0.25, new/F11) + 13.2.1-T2 (0.25, unchanged); S slice **7.25 → 8.5** raw (buffered 10.2); plan 13 **12.25 → 13.5** (buffered 16.2). External lawyer time (13.1.x-T2, 13.6.1-T1) is a cost line, not a dev day.

**Open items for the TL (recorded, not resolved by editing another plan).**
- **O-1** D7 adds `pendingAcceptance` to **`LoginResult`** (`09.3.2-T1:146`) and to the `POST /auth/refresh` response body — which today is the bare token `IssuedAccessToken` (`:148`) and therefore needs a `RefreshResult` wrapper, never a field on the token record (`:29`). Both are P-signed bodies, §B4 has **no** CR for the touch (T7 sanctions only CR-12/CR-11), so the addition waits on an explicit CR to `09.3.2-T1`; **raised: 13 CR-28** (README §B4). `GET /me/legal` is fully owned here and ships regardless.
- **O-2** `legal_documents.kind` needs the fourth value `age_gate` while K8/`00 §6:323` lists three (D8); a CR line against `00-overview.md` §6:323 is required — **raised: 13 CR-26**.
- **O-3** The public route of the attestation text (`/{vi|en}/age-gate` here) is fixed by no plan; 10.3.1-T1's prerender list is **not** extended — **resolved: T16/CR-27, no attestation page is built**; the register page `10.3.2-T1` carries the checkbox.
- **O-4** Whether `age_18` should appear in `pendingAcceptance` for accounts created before 13.2.3-T1 (the owner's P account): [DECISION] not in this design — D8 makes it registration-time, `POST /me/legal/accept`'s body is P-signed and must not change (T7), and with `saas_registration` closed (O9/F9) no other account can exist. Revisit only if 13.6.1-T1's memo demands re-attestation — **ruled T19** (registration-time only).
- **O-5** The 18+ rule's two remaining P-signed touches have no CR in §B4: `09.3.1-T3:40` (`RegisterValidator`) must require the `age_18` entry and its flow (`:27-29`, recording at `:59`) must call `IAgeAttestationGuard` before the recording loop — otherwise the `13-S-trust` gate's "400 blocks registration" has no implementer; and `09.3.1-T1:100,109` (the `user_consents` CHECK `document IN ('tos','privacy')` + `HasEnumCheck<LegalDocument>`, its assertion at `:154` pinning the exact CHECK text) must gain `age_18`. CR-7/CR-10/CR-11 cover only the enum value, the error code and the config key — **raised: 13 CR-29** (`HasEnumCheck` + CHECK + `RegisterValidator` + the pre-recording guard).
- **O-6** CR-12 (README §B4) moves `tools/ci/check-legal-versions.py` ownership to 13.2.2-T1, but the signed P files still assert the old owner — `../13.1-legal-acceptance/13.1.1-T1-privacy-policy-draft-vi-en.md:176` ("enforced by 13.2.1-T1's CI script") and `../13.1-legal-acceptance/13.2.1-T1-legal-acceptance-endpoint-and-dialog.md:13-14,112-113`; CR-12 only retargets Part A **D1**'s wording, so a CR line for those files is required, otherwise two tasks each believe they own the check — **raised: 13 CR-30**.

## 11. Rollout

1. **Order.** `09.2.2-T1` conventions → migration `_13_LegalDocuments` (expand-only, empty) → CR-11 enum + `LegalDocuments:Current:Age18` → resolver replaces the config read behind the P contracts (same shapes) → `LegalVersionStartupCheck` → verb → 13.2.3-T1 (rule + content) → 13.2.1-T2 (`pendingAcceptance`) → `10.3.2-T1` consumes it.
2. **First reviewed release.** After 13.1.1-T2/13.1.2-T2 deliver the lawyer-reviewed `1.1.0` and the web build with those files is deployed, run four publish commands (`privacy`/`tos` × `vi`/`en`), then deploy the API with `LegalDocuments:Current:Privacy/Tos = 1.1.0`. The startup check is the interlock: the API refuses to start on a half-published release, so the F10 drift window (page `1.1.0` while rows record `1.0.0`) cannot open.
3. **Re-acceptance switch-on.** 13.2.1-T2 ships with the next API deploy; because every existing session is the owner's (registration closed behind `saas_registration`, O9) the first table-wide re-acceptance is the SaaS launch. Roll back by shipping the previous API build — rows stay (append-only) and the check goes green.
4. **Age gate switch-on.** 13.2.3-T1 + `10.3.2-T1`, then flip `saas_registration`; verify with a synthetic registration (no age consent → 400; with it → one `age_18` row).
5. **Rollback rules.** The registry is never written backwards: a mistaken publish is corrected by a new version with a later `effective_at` (I4), or by pointing the config back **only** together with a page rollback (the startup check compares against the deployed environment). Migration revert is the standard expand-only drop, safe because nothing references the table.
6. **Verification evidence.** The `13-S-legal` gate run (§B7) plus the startup-mismatch demo, the CI script output, the `legal.document_published` audit rows, the login/refresh `pendingAcceptance` captures and the 400 `age_attestation_required` capture — handed to 13.5.1-T1's pack (binaries outside the repo, D14).
