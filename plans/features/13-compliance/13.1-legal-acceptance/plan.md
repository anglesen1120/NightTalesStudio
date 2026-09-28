# 13.1 — Legal acceptance (privacy, terms, versioned acceptance)

> Feature plan for the **P slice** of plan 13. Contract: `../README.md` (K1–K6, F1/F2, D1–D3, CR-1…CR-5, §8.1). Tasks: 13.1.1-T1, 13.1.2-T1, 13.2.1-T1 — **3.0 d**.
> Not legal advice. The drafts are written by the owner/BA from the plan 13 §5.1/§5.2 checklists; the lawyer review is 13.1.1-T2 / 13.1.2-T2 (S, before any user other than the owner).
> Legend: ✅ cited · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope (P · M1).**

| Task | Deliverable |
|---|---|
| 13.1.1-T1 | Privacy policy draft, vi + en, as versioned repo files with a manifest entry and a coverage checklist. |
| 13.1.2-T1 | Terms of service draft, vi + en, including the YouTube Terms clause (III.A.1), with a manifest entry and a coverage checklist. |
| 13.2.1-T1 | `LegalDocuments:Current:*` binding, `GET /me/legal`, `POST /me/legal/accept`, `ILegalAcceptance.EnsureCurrentAsync` gate wired into the YouTube connect + autopilot paths, the owner acceptance dialog, and the CI version check. |

**Out of scope, with the owner (never re-implemented here).** Lawyer review and publication — 13.1.1-T2 / 13.1.2-T2 (S). Content policy 13.1.3, cookie notice 13.1.4 (S). All-user blocking re-acceptance at login — 13.2.1-T2 (S, XS; 13 README D3/F2). The four prerendered routes, `LegalDocumentPage`, `legal-to-json.mjs`, `PageHeader`/`EmptyState` — 10.3.1-T1 (10 README D12/[WR1]). `ConfirmDialog`, `AppErrorMapper`, the 401/409 plumbing — 10.1.3-T1 / 10.2.2-T1. `pc.v1` upload-consent text — 06 (06 README D7); this plan only cites it (13 README D2). Deletion engine — `13.3.1-deletion-engine/` (`IDeletionRequests`, `IDeletionScopeHandler`, `DataDeletionJob`, `data_deletion_requests.target_id`). YouTube audit filing — `13.4.2-audit-filing/`. Admin legal publishing and the `legal_documents` table (S, 13 README D1/F1).

**Gate satisfied by this folder** (`../README.md` §5–7): pages live with the manifest versions (with 10.3.1-T1), the owner's acceptance recorded before the first YouTube connect, and the CI version check green.

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| K1 | Legal texts are build-time repo files `web/src/content/legal/<doc>.<version>.<lang>.md` plus `manifest.json` `{ "<doc>": { "version", "effectiveDate" } }`, converted at build (no raw HTML, no `innerHTML`), rendered by 10.3.1-T1; the version shown on the page equals the version recorded in `user_consents` (13 owns both) | `../README.md` K1; 10 README D12; 10.3.1-T1 §Goal/§Design ✅ |
| K2 | `IConsentWriter.RecordAsync(Guid userId, LegalDocument document, string version, IPAddress? ip, CancellationToken ct)`; `enum LegalDocument { Tos, Privacy }` stored snake_case `tos`/`privacy`; config `LegalDocuments:Current:Tos` / `:Privacy` (required, 1–32 chars, validated at startup); the writer only `Add`s rows — the caller's `UnitOfWorkBehavior` commits; history kept, no unique index | 09.3.1-T1 §Design/§Outputs/§Acceptance ✅ |
| K3 | `user_consents` exists (09.3.1-T1, migration `_09_UserConsents`) | 09.3.1-T1 §Design (`CREATE TABLE user_consents …`) ✅ |
| K4 | III.A.1: link the YouTube ToS and bind users to it; III.A.2: a privacy policy surfaced before features, stating "uses YouTube API Services", linking the Google Privacy Policy and the revoke page; III.D.2: 7 days after in-app revoke, 30 days after Google-page revoke; III.E.4: reconfirm/refresh or delete stored Authorized Data every 30 days; user deletion ≤ 7 days | 06 README G13 (line 43) ✅ |
| K5 | `pc.v1` is 06's publication-consent text: `YouTube:ConsentTextVersion` = `pc.v1`, texts `Templates/Consent/pc.v1.<locale>.md`, rendered by 10.7.2, per-publication consent rows | 06 README D7/D11; `06-youtube-posting/README.md:112` ✅ |
| K6 | Audit goes through `IAuditWriter` (09.3.6-T1) or the `[Audited(action, category, visibility)]` attribute; every code has a row in `Api/Http/ErrorCatalogue.cs` and a unit test asserts it | 09.3.6-T1; 09 README D7 (line 191) ✅ |
| K7 | Privacy §5.1 checklist (verbatim items): who we are + contact ✅; data collected (account, content created, channel connection data incl. YouTube/TikTok ids/names/tokens, usage/billing, logs); data from YouTube API Services — what is accessed, stored, shared, how used ✅, the "uses YouTube API Services" statement + links to the YouTube ToS and the Google Privacy Policy ⚠️; how users revoke — in-app disconnect and `https://security.google.com/settings/security/permissions` ✅; retention and deletion — the 7-day and 30-day windows by trigger, 30-day refresh/delete of stored Authorized Data ✅, backups kept [DECISION] 30 days and never restored into the live service; upload consent — every upload has its own consent record, autopilot series derive from the owner's versioned series authorization after a veto window (06.2.2); we never ask for YouTube passwords ✅; sub-processors (OpenAI, hosting, storage, payment, e-mail); staff access + logging (09.10.5) and staff/security alerts (08 §5.2); international transfers, security measures, user rights (access, correction, deletion, export), children (not for under-18 [DECISION] ⚠️ lawyer), changes to the policy | `plans/13-compliance.md:105-116` ✅ |
| K8 | Terms §5.2 checklist (verbatim items): eligibility (18+); account security; credits (non-cash, expiry 12 months ⚠️ lawyer, refunds per 12.7.5 incl. the spent-bonus deduction); acceptable use (link Content Policy); ownership (user owns outputs subject to provider terms ⚠️ OpenAI terms); AI disclosure duty; no monetisation guarantee; user is publisher of record; platform rules compliance (YouTube/TikTok); takedown and suspension; service availability; liability limits; governing law/disputes ⚠️ — **clarification of the quoted item**: purchased and bonus lots expire **12 months from payment** (not from inactivity), trial lots 30 days, promo lots per code, and 12.6.2 requires these rules to appear in the terms | `plans/13-compliance.md:118-120` ✅; `plans/12-billing-credits.md:142-143,146` ✅ |
| K9 | The privacy page must describe the retention/deletion windows and the consent model; the terms must carry the YouTube Terms link + binding sentence | 13 README D1 ✅ |
| K10 | The FE maps ProblemDetails → `AppError { code, status, fieldErrors, retryAfterSec?, traceId?, problem }`; unknown code → `unknown_error`; `errors.<code>` keys exist in vi and en and key parity is CI-checked | 10 README D9 (line 159), D10 (line 166) ✅ |
| K11 | The shell (10.2.6-T1) loads `/me` once into `SessionStore` and is the declared consumer/blocker of 13.2.1-T1 (13 CR-5) | 10.2.6-T1 header ✅ |
| K12 | The gate callers are 06.1.2-T1 (connect; flag `youtube_publish`) and 02.8.3-T2 (enabling autopilot) | `../README.md` D3; 13 CR-2; `06-youtube-posting/README.md:208` ✅ |

**Consumed, not produced here.** `IConsentWriter`, `LegalDocument`, `LegalDocuments:Current:*` options binding and the `user_consents` table come from 09.3.1-T1 (K2/K3). `IAuditWriter` and `ErrorCodes`/`ErrorCatalogue` come from 09.3.6-T1 and 09.1.4-T1. `ConfirmDialog` and the error/i18n plumbing come from 10.1.3-T1/10.2.2-T1. `pc.v1` comes from 06.

## 3. Domain and data

**No new table, no new column, no migration in this folder.** The P slice adds only repo content files and one CI script; the expand-only rule is satisfied trivially, and `legal_documents` is **not created** (13 README D1/F1, 13 CR-1) — the table and its `kind/version/locale/effective_at/content_url` shape stay S (00 §6), because the P topology is "texts are repo files, versions are config" (K1) and a runtime `content_url` fetch is explicitly rejected by 10 README D12/F18.

**Reused table (read/write), exact shape — quoted from 09.3.1-T1 so this plan is self-contained:**

```sql
CREATE TABLE user_consents (
    id          uuid        PRIMARY KEY,
    user_id     uuid        NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    document    text        NOT NULL CHECK (document IN ('tos','privacy')),
    version     text        NOT NULL,
    accepted_at timestamptz NOT NULL,
    ip          inet        NULL
);
CREATE INDEX ix_user_consents_user_id_document ON user_consents (user_id, document);
```

**Aggregates and read models.**

- `LegalDocument` (09, `Application/Abstractions/Identity/`) — `Tos | Privacy`; the wire spelling is lowercase `tos`/`privacy` (K2). Never re-declared here.
- `LegalDocumentsOptions` (new, 13.2.1-T1) — binds `LegalDocuments:Current:Tos` / `:Privacy` with `[Required, StringLength(32, MinimumLength = 1)]`. 09.3.1-T1 reads the same section; 13.2.1-T1 owns the typed options class and the *consistency* rule (I1), not the values.
- `LegalAcceptanceDto` — `{ documents: LegalDocumentStatusDto[] }`; `LegalDocumentStatusDto { document: "tos"|"privacy", currentVersion: string, acceptedVersion: string|null, acceptedAt: DateTimeOffset|null }`. Always both documents, in the order `tos`, `privacy` (stable for the client and for tests).
- `LegalAcceptance` is a **projection** over `user_consents`, not an entity: the acceptance history is append-only rows (K2), so no state machine is needed.

**Invariants.**

| Id | Invariant | Enforced by |
|---|---|---|
| I1 | `manifest.json[doc].version` == the version in the file name `legal/<doc>.<version>.<lang>.md` == `LegalDocuments:Current:<Doc>` (case-insensitive doc key `tos`/`privacy`) | `tools/ci/check-legal-versions.py` (13.2.2-T1, 13 CR-30) + the build-time generator of 10.3.1-T1 |
| I2 | A version is semver-like `1.0.0` (13 README D1); the DB column is `text` capped at 32 (K2) | the CI script's regex + `StringLength(32)` |
| I3 | `POST /me/legal/accept` accepts only versions equal to the current ones; a mismatch accepts **nothing** (all-or-nothing, one transaction) | `AcceptLegalDocumentsHandler` |
| I4 | Rows are append-only and one row per acceptance event; re-accepting the same version appends a second row (history, K2) — no unique index, no upsert | design (do not add an index) |
| I5 | Every document exists in **both** locales with an identical set of heading anchors | the parity check in 13.1.1-T1/13.1.2-T1 + `legal-to-json.mjs` (vi/en version equality, 10.3.1-T1) |
| I6 | Consent rows commit in the caller's transaction (`IConsentWriter` has no `SaveChangesAsync`) | reused writer (K2) |
| I7 | The client IP is stored only in `user_consents.ip`; it never appears in logs, audit `data`, or ProblemDetails | 09 README (line 703) + §8 below |

**Indexes and access paths.** No new index. Both endpoints and the gate run **one** query per request:

```sql
-- latest acceptance per document for one user (no N+1, no per-document round trip)
SELECT DISTINCT ON (document) document, version, accepted_at
FROM user_consents
WHERE user_id = $1
ORDER BY document, accepted_at DESC;
```

`ix_user_consents_user_id_document` covers the filter; the sort is over ≤ a handful of rows per document (a personal install has one). The gate runs the projection and compares two strings — O(1) after the query.

## 4. Use cases

**UC1 — gate: `ILegalAcceptance.EnsureCurrentAsync(Guid userId, CancellationToken ct) → ErrorOr<Success>`** (port in `src/NightTales.Application/Abstractions/Compliance/`, implementation `LegalAcceptanceService` in `src/NightTales.Application/Features/Account/Legal/`).

- Reads the current versions from `LegalDocumentsOptions` and the user's latest accepted version per document (query above) and returns `Success` when both equal the current versions.
- Otherwise `Error.Conflict(code: ErrorCodes.LegalAcceptanceRequired, description: "Legal acceptance required.")` with metadata `documents` = the missing documents in wire spelling, **sorted** (`["privacy","tos"]` when both are missing, `["privacy"]` when only privacy is). The ProblemDetails writer puts the list in the envelope's `errors` map, i.e. `errors: { "documents": ["privacy","tos"] }` — exactly the shape 13 README D3 fixes (sorted; 06 YR15).
- Never writes, never throws for a missing row; a stale version (accepted != current) counts as missing.
- Callers: `06.1.2-T1` (before starting the OAuth flow) and `02.8.3-T2` (before enabling autopilot; 13 CR-2). Both surface the error unchanged as **409 `legal_acceptance_required`**.

**UC2 — query: `GetLegalAcceptanceQuery()` (implicit current user) → `ErrorOr<LegalAcceptanceDto>`.**

- `ICurrentUser.Id` only; no user id in the route (same rule as 09.3.4-T1). Authorisation: an authenticated session; otherwise `401` via the existing pipeline.
- Returns both documents with `currentVersion` (from options) and the latest accepted `acceptedVersion`/`acceptedAt` (null when never accepted).
- Used by the dialog after a 409 and by the account screen; pure read, no cache, no ETag (the value changes only on acceptance, which is a local action).

**UC3 — command: `AcceptLegalDocumentsCommand(string Tos, string Privacy)` → `ErrorOr<LegalAcceptanceDto>`.**

- Validation (`AcceptLegalDocumentsValidator`, FluentValidation): both fields required, non-empty, ≤ 32 chars, each matching `^\d+\.\d+\.\d+$`. A failure is the existing 400 `validation_failed` with `errors` grouped by camelCase field (`tos`/`privacy`).
- Handler: loads the current versions; if either submitted version differs → `Error.Conflict(ErrorCodes.LegalVersionOutdated, "You are accepting an outdated document version. Reload the documents and try again.")` with metadata `documents` = the mismatching ones in wire spelling, **sorted** (`errors.documents = ["privacy","tos"]` when both are stale, `["tos"]` when only `tos` is); **nothing is written**. This is the only writer path; there is no per-document accept endpoint (a partial acceptance would be unprovable). One rule, one list: the metadata always names exactly the offending documents, sorted — so the fixtures and the FE never have to guess.
- On success: for each document, `await consentWriter.RecordAsync(user.Id, document, currentVersion, ip, ct)` where `ip` is `IPAddress.TryParse(info.Ip, out var parsed) ? parsed : null` — the **version recorded is always the server's current version**, never the client string (acceptance is of what was shown, and the server decides what is current); the client's submitted values are only a confirmation that it rendered the same version. Then returns the fresh projection.
- Idempotency: repeating the same accept appends rows (I4) and returns the same body with a newer `acceptedAt` — safe, no idempotency key required ([DECISION]: unlike credit-moving commands, this is append-only and non-pecuniary, so 09's `RequireIdempotencyKey()` is deliberately **not** applied; recorded as an [OPEN] line in §10 for the TL to confirm).
- Audit: the command carries `[Audited("account.legal_accepted", Category.AccountSecurity, Visibility.User)]` so `AuditBehavior` writes exactly one `account.legal_accepted` row inside the same transaction (K6); the action is the dotted, unique form the architecture test requires and `Category`/`Visibility` are explicit attribute arguments (no defaults) — see `09.3.4-T1:28` for the same shape and `09.3.6-T1` (`AuditAttributeTests`, `^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$`). `IAuditData` exposes only the two document versions — no IP, no e-mail, no user agent.
- IP: the IP travels as a `string?` — `public sealed record RequestInfo(string? Ip, string? UserAgent)` (`Application/Abstractions/Security/IRefreshTokenService.cs`, 09.3.2-T1:51), the same record 09.3.1-T1 passes to `IRegistrationFlow.RegisterAsync`. `ICurrentUser` exposes no IP. Conversion: `IPAddress.TryParse(info.Ip, out var parsed) ? parsed : null`, so an absent or unparsable value stores `NULL` (`user_consents.ip` is nullable) and acceptance still succeeds. Never logged.

**Error codes (13 CR-3).** `legal_acceptance_required` → 409 and `legal_version_outdated` → 409, added to `Application/Common/Errors/ErrorCodes.cs` and `Api/Http/ErrorCatalogue.cs` by 13.2.1-T1; `ErrorCatalogueTests.EveryCodeHasAStatus` (09.1.4-T1) must stay green. Both codes get `errors.<code>` keys in vi and en through 10.2.2-T1's key set.

## 5. API and events

All under the existing `/me` group (`Api/Endpoints/AuthEndpoints.cs`, the group 09.3.4-T1 creates for `GET/PATCH /me`; 13.2.1-T1 adds these two routes to that group rather than opening a second group) [INFERENCE on the file name]. Base path `/api/v1`.

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| GET | `/me/legal` | session cookie (CSRF not required for a safe method) | 200 `LegalAcceptanceDto` | 401 `unauthenticated` (existing), 500 |
| POST | `/me/legal/accept` | session cookie + `Origin` check (cookie-authenticated POST) | 200 `LegalAcceptanceDto` (fresh projection) | 400 `validation_failed`, 401 `unauthenticated`, 409 `legal_version_outdated`, 500 |

The gate is not an endpoint: it is raised inside the connect and autopilot commands, so it appears as `409 legal_acceptance_required` on `POST /channels/youtube/connect` (06.1.2-T1) and on the autopilot enable command (02.8.3-T2) — the response body is the standard closed ProblemDetails envelope with `errors: { documents: ["privacy","tos"] }`.

Envelope (09 README D7, line 191 — closed set): `type` (`https://nighttales.app/errors/<code>` ⚠️ host placeholder), `title`, `status`, `code`, `traceId`, `errors?` (camelCase field → `string[]`), plus allow-listed metadata keys. There is **no** action list in ProblemDetails; the dialog must not parse actions from the error body — it re-reads `GET /me/legal` (see §6 and the repo-wide trap recorded for 10.x plans).

**Outbox / realtime events: none.** Legal acceptance changes no publication, channel or schedule state and no screen subscribes to it. No `[OutboxEvent]`, no SignalR message, no e-mail (`IConsentWriter` has no e-mail side effect and the acceptance is the owner's own action). Recorded explicitly so nobody adds a broadcast "consent changed" event.

## 6. UI

**Pages (already owned elsewhere).** `LegalDocumentPage` renders the four routes `/vi/privacy`, `/en/privacy`, `/vi/terms`, `/en/terms` from the build-time generated module and shows `manifest.json`'s `version` + `effectiveDate` (10.3.1-T1, 10 README D12). This folder's obligation is content only: the files must exist, must be in the Markdown subset `legal-to-json.mjs` accepts (headings `h2`/`h3`, paragraphs, `-`/`1.` lists, `>` quotes, `**emphasis**`, `[text](https://…|mailto:|/)`), and must carry both locales at the same version (I1/I5). Anything outside that subset fails the web build (10.3.1-T1 edge cases) — the drafts are written to the subset, which is why the checklists carry a "no tables/images/footnotes/nested lists" note.

**Dialog produced here (P · M1).** `web/src/app/features/account/` (10 README D2 layout):

| File | Content |
|---|---|
| `domain/legal-acceptance.model.ts` | `LegalDocumentKind = 'tos' \| 'privacy'`; `LegalDocumentStatus`; `LegalAcceptance` — the wire model of `LegalAcceptanceDto` |
| `data-access/legal-acceptance.repository.ts` | `LegalAcceptanceRepository { get(): Promise<LegalAcceptance>; accept(tos: string, privacy: string): Promise<LegalAcceptance> }` — `HttpClient` + `firstValueFrom`, returns `Promise<T>` (10 README line 136), surfaces `HttpErrorResponse` to the caller so 10.2.2-T1's `AppErrorMapper` handles it |
| `application/legal-acceptance.store.ts` | signals `acceptance`, `pending`, `error`; `refresh()` (on open), `accept(tos, privacy)`; **no optimistic state** — the dialog closes only after the API returns 200 |
| `ui/legal-acceptance-dialog/legal-acceptance-dialog.{ts,html,scss}` | standalone, OnPush, `MatDialog` opened with `disableClose: true`, `ConfirmDialog` chrome reused from 10.1.3-T1 (title, body slot, single primary action) |

Dialog behaviour (13 README D3): lists both documents with their current versions, links to `/{vi|en}/privacy` and `/{vi|en}/terms` opening in a **new tab** with `rel="noopener noreferrer"`, a checkbox "I have read and accept both documents" that gates the primary button, a secondary link "sign out" (the only way out without accepting when opened from the gate — the owner must not be able to connect YouTube or enable autopilot without accepting), and an inline error zone rendering `errors.<code>` for 409/500. The language of the links follows the active UI locale (Transloco), and both locales are tested.

Copy keys handed over (D10, both languages, `web/src/assets/i18n/account/<lang>.json`, prefix `account.legal.*`):
`account.legal.title`, `account.legal.intro`, `account.legal.privacyLabel`, `account.legal.termsLabel`, `account.legal.currentVersion`, `account.legal.acceptedOn`, `account.legal.checkbox`, `account.legal.accept`, `account.legal.openPrivacy`, `account.legal.openTerms`, `account.legal.signOut`, `account.legal.outdated`, `account.legal.required`, `account.legal.error.generic`.
Error keys handed to 10.2.2-T1's set: `errors.legal_acceptance_required`, `errors.legal_version_outdated` (vi + en; 10.2.5-T1's parity check covers them).

**Cross-task hand-off (no edit outside this folder).** `10.2.2-T1` owns the 409 mapping: when `AppError.code` is `legal_acceptance_required` (or `legal_version_outdated` after a submit), the shell opens this dialog and, on success, re-runs the blocked action (the connect button or the autopilot enable call) instead of parsing the problem body for instructions. This folder supplies the component and the model; the wiring is 10.2.2-T1's line item — recorded as a hand-off in §10 and in the task's Notes, not as an edit by 13.2.1-T1.

## 7. Jobs and integrations

**Jobs: none.** This folder adds no `IBackgroundJob` — the deletion engine (`DataDeletionJob`, hourly, batch ≤ 20, `FOR UPDATE SKIP LOCKED`) is `13.3.1-deletion-engine/`, and the 30-day Authorized-Data refresh is 06's `TokenHealthJob` (06 README F16/D16). No Hangfire registration, no queue, no retry policy is introduced here.

**CI integration — owned by 13.2.2-T1, not this folder (13 CR-30).** `tools/ci/check-legal-versions.py` (13 README D1) checks
`web/src/content/legal/manifest.json` against `LegalDocuments:Current:Tos`/`:Privacy` (I1) and wires into the `backend.yml` pipeline;
this folder's only obligation is that 13.2.1-T1's routes read the same config section the script checks, so the two stay equal.

**External calls.** None. No Google call, no HTTP client, no e-mail: the gate is a local read plus a string comparison, and `pc.v1` (the upload consent) is 06's, untouched here.

**Failure map.**

| Failure | Behaviour |
|---|---|
| `manifest.json` present but a `.md` file missing / wrong version in the name | CI red (`check-legal-versions.py`), and independently the web build fails in `legal-to-json.mjs` (10.3.1-T1) — never a blank page |
| `LegalDocuments:Current:*` != manifest | CI red (I1); at runtime the API still serves, but the page version would differ from the recorded one — the CI step is the only guard, so it must be wired (13 CR-1/F1) |
| `LegalDocuments:Current:*` absent or > 32 chars | startup failure via `ValidateDataAnnotations().ValidateOnStart()` (K2, 09.3.1-T1) — fail fast, never a default `1.0.0` |
| Database unavailable during `GET /me/legal` or the gate | 500 with `traceId`; the connect/autopilot call fails closed (the action does **not** proceed) — accept this over "allow when unsure" |
| `IConsentWriter` throws / FK violation (user deleted mid-flight) | the whole command rolls back (no partial acceptance, I6); the FK violation maps to 400 `validation_failed`, never a raw 500 |
| Client submits a stale version (page cached from before a bump) | 409 `legal_version_outdated`, nothing written; the dialog refreshes and shows the new version |
| Client IP absent (proxy/loopback) | `ip` stays `NULL` — the column is nullable and acceptance is still recorded |

## 8. Security and privacy

- **Reuse, never duplicate.** Consent writing goes through 09's `IConsentWriter` (K2); audit through 09's `IAuditWriter`/`[Audited]` (K6); error codes through 09's catalogue (K6); session/auth through 09's pipeline. This folder introduces **no** second e-mail, audit, flag or token mechanism, no new secret, and no new configuration secret.
- **Tokens/secrets.** None handled here: no OAuth token, no client secret, no signing key. The only configuration values are public versions (K2), safe to log and to echo in ProblemDetails.
- **Tenant/isolation.** Both routes act on `ICurrentUser.Id` only; there is no `userId` route or body parameter, so a cross-user read is structurally impossible (the 09 rule for other users' resources — 404 — is not even reachable here). Unauthenticated → the existing 401.
- **CSRF and state.** `GET /me/legal` is safe and read-only. `POST /me/legal/accept` is cookie-authenticated, so it requires the existing `Origin` check and the session's CSRF protection (09 BD3/pipeline); it carries no OAuth `state` (that belongs to 06's connect flow, not here).
- **Idempotency and integrity.** Append-only rows, all-or-nothing acceptance (I3/I4); the server records its own current version, so a forged client version can never be written into history. No ETag is involved: legal acceptance has no versioned mutable resource, so the `If-Match` machinery does not apply ([DECISION] — documented so a reviewer does not ask for one).
- **Logging.** The IP address is stored in `user_consents.ip` and never logged; request logging redacts the body (it contains no secret anyway); audit `data` carries only the two versions (no IP, no e-mail, no user agent); ProblemDetails contains code, title, traceId and the document list — nothing sensitive.
- **Privacy of the drafts themselves.** The personal-version drafts (O1) contain no secrets, no personal data of third parties and no real user data; the sub-processor list and the staff-access paragraph are the only third-party mentions. `docs/compliance/*.md` in the repo contains the coverage checklist only — never a filled-in audit form with personal data (13.4.2-T1 owns the filing rules, D5).
- **Deletion interaction.** Acceptance rows are part of the account data handled by 13.3.1-T1's scopes; this folder keeps no copy of them and adds no exception to the deletion rules. The privacy text must state the retention windows correctly because a wrong sentence here is a policy-vs-behaviour finding at the M1 filing (13 README D5).
- **Front-end.** Both legal links are `target="_blank" rel="noopener noreferrer"` to same-origin pages; no `innerHTML`; the dialog renders only the versions it received from the API and the i18n copy.

## 9. Test plan

Conventions: **xunit.v3 + Shouldly**, no mocking library — hand-written fakes/TestKit; integration tests run against real PostgreSQL 17 via **`PostgresFixture`**; time through **`FakeClock`** (`accepted_at` assertions); DB/HTTP tests and Worker jobs run in-process (`NightTales.Api.IntegrationTests`, 09 README D2); architecture rules (`ErrorCatalogueTests.EveryCodeHasAStatus`, `NightTales.Architecture.Tests`) stay green.

**`tests/NightTales.Application.UnitTests`** (fake `IConsentWriter`, `IAppDbContext` in-memory substitutes from TestKit, `FakeClock`):
- `LegalAcceptanceServiceTests.EnsureCurrent_NoConsents_Returns409WithBothDocuments` — code `legal_acceptance_required`, metadata `documents` alphabetical `["privacy","tos"]`.
- `LegalAcceptanceServiceTests.EnsureCurrent_AcceptedCurrentVersions_Succeeds`.
- `LegalAcceptanceServiceTests.EnsureCurrent_StaleAcceptedVersion_Returns409` (accepted `0.9.0`, current `1.0.0`).
- `LegalAcceptanceServiceTests.EnsureCurrent_OnlyOneDocumentMissing_ListsOnlyThatDocument`.
- `AcceptLegalDocumentsValidatorTests.VersionNotSemver_ReturnsValidationFailed`, `…MissingDocument_ReturnsValidationFailed`.
- `LegalDocumentsOptionsTests.MissingConfigKey_FailsValidation`, `…VersionOver32Chars_FailsValidation`.

**`tests/NightTales.Api.IntegrationTests`** (real PostgreSQL 17, `PostgresFixture`, `ApiFactory`, `FakeClock`):
- `LegalEndpointsTests.GetMeLegal_NeverAccepted_ReturnsBothDocumentsWithNullAccepted` — 200, `documents[0].document == "tos"`, version equals config.
- `LegalEndpointsTests.GetMeLegal_Unauthenticated_Returns401`.
- `LegalEndpointsTests.Accept_WritesTwoRowsAndOneAuditRow_AndReturnsFreshProjection` — raw SQL count `user_consents` = 2 with the config versions and `ip` set; `audit_logs` has exactly one `account.legal_accepted` row with `visibility = user` and no IP in `data`. The attribute form itself (`account.legal_accepted`, `Category.AccountSecurity`, `Visibility.User`, dotted and unique) is asserted by 09.3.6-T1's `AuditAttributeTests`; a variant with an unparsable `RequestInfo.Ip` still commits both rows with `ip = NULL`.
- `LegalEndpointsTests.Accept_Twice_AppendsTwoMoreRows` (history, I4) — four rows, no unique violation.
- `LegalEndpointsTests.Accept_OutdatedVersion_Returns409LegalVersionOutdated_AndWritesNothing` (`errors.documents` names exactly the offending documents, sorted — `["privacy","tos"]` when both are stale, `["privacy"]` when only privacy is).
- `LegalEndpointsTests.Accept_ResponseBody_HasNoIpAndNoEmail` (grep the serialized body).
- `AcceptanceRollbackTests.Accept_FailingAuditWriter_LeavesNoConsentRow` (a TestKit audit writer that throws → 500 and zero rows; proves I6).
- `CheckLegalVersionsScriptTests` is **not** here — the script is tested by pytest in `tests/ci/` (below) so the .NET suite stays free of shell-outs.
- The gate as seen by a caller: `ConnectGateTests.WithoutAcceptance_Returns409LegalAcceptanceRequired` lives in **06.1.2-T1's** suite (it owns the connect endpoint and mounts the gate); 13.2.1-T1 supplies the service and asserts the same contract at the unit level. Cross-check recorded in the task's Tests section.

**`tests/ci/test_check_legal_versions.py`** (pytest, mirroring 09.9.5-T1's `tests/ci/test_check_version_rule.py`): manifest/version mismatch → exit 1 naming the file and line; missing `en` file → exit 1; locale heading drift → exit 1; appsettings mismatch → exit 1; consistent tree → exit 0; `--selftest` green.

**Front end** (`web/`, Vitest + TestBed; Playwright e2e is 11.5.1-T1's `e2e.yml` and covers the pages via 10.3.1-T1):
- `legal-acceptance-dialog.spec.ts` — renders both documents with the versions from the API; the primary button is disabled until the checkbox is ticked; clicking it calls `accept(tos, privacy)` with the rendered versions and closes only on 200; a 409 `legal_version_outdated` keeps the dialog open and shows `errors.legal_version_outdated`; Escape/outside click do not close it (`disableClose`); both document links are `target="_blank" rel="noopener noreferrer"`; the string is rendered in both locales (Transloco test config).
- `legal-acceptance.store.spec.ts` — `refresh()` after a 409 refetches and never parses actions out of the error body; `accept()` surfaces `AppError` from the mapper on 500 without losing the entered state.
- Manual walkthrough (recorded for 13.4.2-T1's evidence pack): owner signs in, connect is blocked with the dialog, accepts, connect proceeds; the acceptance is visible in `user_consents` with the version shown on the page.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 13.1.1-T1 | Privacy policy draft vi/en (checklist §5.1 + YouTube III.A.2) as versioned repo files | M (1.0) | — | 13.2.1-T1, 06.5.1-T1, 10.3.1-T1, 06.1.1-T1 (soft), 13.1.1-T2, 13.6.1-T1, 10.3.1-T0, 07.4.1-T1 (13 CR-17), 13.1.3-T1, 07.1.1-T1 |
| 13.1.2-T1 | Terms of service draft vi/en incl. the YouTube Terms clause (III.A.1) | M (1.0) | — | 13.2.1-T1, 06.5.1-T1, 10.3.1-T1, 06.1.1-T1 (soft), 13.1.2-T2, 13.6.1-T1, 10.3.1-T0, 07.4.1-T1, 12.6.2-T1 (13 CR-17), 07.1.1-T1 |
| 13.2.1-T1 | Legal versions config, acceptance endpoint + gate, owner acceptance dialog | M (1.0) | 13.1.1-T1, 13.1.2-T1, 09.3.1-T1, 10.2.6-T1 | 06.1.2-T1, 06.5.1-T1, 10.7.1-T1 (10 CR-15), 13.2.1-T2, 10.8.2-T1, 07.1.2-T1 (13 CR-17) |

Rows equal `../README.md` §8.1 (13 CR-5 owns the back-edges). Re-estimate: 13.1.1-T1 1.0 + 13.1.2-T1 1.0 + 13.2.1-T1 1.0 = **3.0 d** for this folder (13 README §9: the whole P slice is 5.0 d including 13.3.1-T1 and 13.4.2-T1; 13.2.1-T1 grew from 0.5 to 1.0 for the BE gate + endpoints + dialog + the version CI check).

Hand-offs recorded for the readers (not edits by this folder): the 409 → dialog wiring in 10.2.2-T1 (§6); the `errors.<code>` keys with 10.2.2-T1's set; the deleted `registration consent` clause is 09.3.1-T3's (S) — P has no registration, so the privacy text must not promise a registration-time acceptance (it describes acceptance before first connect).

**Open items for the TL.**

| # | Item | Why it matters |
|---|---|---|
| O-1 | Confirm that `POST /me/legal/accept` needs no `RequireIdempotencyKey()` (append-only, non-pecuniary) | §4 UC3; a reviewer may read 09's idempotency rule as universal |
| O-2 | Confirm that the missing-document list travels in `errors.documents` as a **sorted** list (`["privacy","tos"]`; the map the FE reads as `fieldErrors`) and not as a top-level metadata key | the front end and the acceptance test must agree with 09.1.4-T1's writer |
| O-3 | Confirm the CI step placement (`backend.yml`, next to the other `tools/ci/*` checks) and that 11 owns the workflow edit | 13.2.1-T1 produces the script; the workflow file is 11's |
| O-4 | `401 unauthenticated` appears in the plan's error table but is not in 09's §5.4 catalogue (known 10.x trap); until it is, the client relies on the session interceptor | do not invent the constant in this folder |
| O-5 | Extend **13 CR-1** to two more files that still list `legal_documents` as a P table: `features/09-backend-api/README.md:240` (row "13 (P items): legal_documents, data_deletion_requests") and `features/09-backend-api/09.2.2-core-schema-migrations/plan.md:26`, while `plans/00-overview.md:323` already says "S only — not created in P" | an implementer reading 09 would write a P migration for a table D1 says is not created; this folder creates no table and must not edit 09 |

## 11. Rollout

- **Order.** 13.1.1-T1 and 13.1.2-T1 start immediately in parallel (no dependencies; they touch different files except `manifest.json`, whose ownership is split: 13.1.1-T1 creates the file with the `privacy` key, 13.1.2-T1 adds the `tos` key). 13.2.1-T1 follows and is the only task with code. The `tos` key must land before the CI check can be green — the check requires both documents (I1).
- **Milestone.** M1, personal phase, no feature flag ([DECISION]: unlike `youtube_publish` in 06, this has no flag — an owner-only acceptance gate needs no rollout switch; the *pages* are behind the prerender list that 10.3.1-T1 already owns).
- **Staging walkthrough before the gate is called met.** (1) `check-legal-versions.py` green on the branch; (2) the four pages render with the manifest versions; (3) the owner accepts through the dialog and `user_consents` shows two rows at version `1.0.0`; (4) a fresh sign-in with an empty `user_consents` (and, separately, a hand-edited stale version) yields 409 `legal_acceptance_required` with `errors.documents = ["privacy","tos"]` (both missing) or `["privacy"]` (only that document stale), sorted, and the connect action stays blocked; (5) the audit appends exactly one `account.legal_accepted`. The evidence (screenshots/screencast with dates and app version) is the input 13.4.2-T1's filing pack expects (13 §5 evidence story).
- **Gate `13-P-ready`** (13 README §5–7): pages live with the manifest versions (with 10.3.1-T1), owner acceptance recorded before the first YouTube connect, deletion job live on staging with a channel request completed before `due_at` (13.3.1-T1), filing done (13.4.2-T1).
- **Rollback.** Content is repo files: reverting the commit reverts the policy text, and the pages follow on the next deploy. The API half is code + config: a revert restores the previous behaviour; the two new error codes are additive and safe to leave in the catalogue. `LegalDocuments:Current:*` must be reverted **with** the content, otherwise I1 breaks and the CI check fails — which is the intended loud failure, not a silent drift. No migration to undo (this folder has none).
- **After P (S, not planned here):** 13.1.x-T2 lawyer review before any other user; 13.2.1-T2 all-user blocking re-acceptance with a change summary; `legal_documents` admin publishing (13 CR-1 closes when the table lands). The personal-version drafts are expected to change at that point — the version bump path (`1.0.0` → `1.1.0`, both locales, manifest, config, CI) is the same one exercised here.
