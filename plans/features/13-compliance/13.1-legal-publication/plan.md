# 13.1 — Legal publication (lawyer review, content policy, cookie notice, Vietnamese-law memo)

> Feature plan for the **S slice, folder 1 of 4** of plan 13. Contract: `../README.md` Part B — §B0 (folder brief), §B3 (**D6** publish path, **D13** cookie inventory, **D16** PDPL program, **D17** baseline rules), §B4 (CR-7, CR-11, CR-13), `### B8.1` (canonical rows), §B9–§B10 (`O4`, `O6`, `T6`, `T7`). High-level: `../../13-compliance.md` §4 (13.1.1, 13.1.2, 13.1.3, 13.1.4, 13.6.1), §5.1–§5.4, §6, §9–§11. Part A (P) D1–D5 / K1–K6 is signed context and is **not** reopened (`T7`).
> Tasks: 13.1.1-T2, 13.1.2-T2, 13.1.3-T1, 13.1.4-T1, 13.6.1-T1 — **2.0 d raw** (XS + XS + S + S + S), phase **S · M2**.
> **Not legal advice.** Every legal statement below keeps the README's status: ✅ = verified against the cited source, ⚠️ = the lawyer decides. Nothing in this folder may present a ⚠️ item as settled, and `D16` forbids any PDPL/legal-compliance claim in user-facing text or audit answers before 13.6.1-T1's memo lands.
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**In scope.** Turn the P drafts (`13.1.1-T1` privacy `1.0.0`, `13.1.2-T1` terms `1.0.0`) into lawyer-reviewed, published, versioned texts; add the two legal surfaces the P slice deferred (`13.1.3-T1` Content Policy, `13.1.4-T1` cookie notice); and deliver the Vietnamese personal-data-protection memo that can force a version bump (`13.6.1-T1`).

| Task | Artifact this folder writes | Runtime effect |
|---|---|---|
| 13.1.1-T2 | reviewed `web/src/content/legal/privacy.<version>.vi.md` + `.en.md`, `manifest.json` `privacy` entry, `docs/compliance/privacy-review-<yyyy-mm-dd>.md` | one `legal_documents` row (kind `privacy`) via 13.2.2-T1's publish verb; the page version and `user_consents.version` follow |
| 13.1.2-T2 | reviewed `…/terms.<version>.vi.md` + `.en.md`, manifest `tos` entry (the manifest key equals `legal_documents.kind` = `tos`, which is what D6b compares), `docs/compliance/terms-review-<yyyy-mm-dd>.md` | one `legal_documents` row (kind `tos`); same follow-through |
| 13.1.3-T1 | `…/content_policy.1.0.0.vi.md` + `.en.md` (+ `.1.1.0` if the memo forces a change), manifest `content_policy` entry, the prohibited/restricted ↔ report-reason map table | `legal_documents` row (kind `content_policy`); the reason list rendered by 13.3.4-T1's form |
| 13.1.4-T1 | `docs/compliance/cookies.md` (the single inventory), `web/src/app/core/legal/cookie-inventory.generated.ts`, `legal.cookieNotice.*` i18n copy (vi+en), the two assertion tests | none at runtime except the CI/unit assertion that fails when an undocumented cookie appears |
| 13.6.1-T1 | `docs/compliance/pdpd-memo-<yyyy-mm-dd>.md` + one backlog row per required change | none; it can trigger a version bump through D6 in another folder |

**Out of scope (do not build here).** No migration, no new table/column (`legal_documents`, `reports` and the `user_consents.document` enum belong to 13.2.2-T1 / 13.3.4-T1 / CR-7); no HTTP route, DTO or ProblemDetails code (the three new codes are CR-10's, owned by `09.3.5-T1`); no job or `[JobPolicy]`; no feature flag of its own; no `legal_documents` implementation, no publish verb implementation, no startup/CI equality checker (all `13.2.2-T1`); no screen (see §6); no TikTok submission (`13.4.3-T1`); no evidence-pack binaries (`13.5.1-T1`, path rule `D14`). External lawyer/consultant time is a **cost** line, not a dev-day line (`B9`, `O4`).

## 2. Confirmed facts

| Id | Fact (README verbatim where quoted) | Source |
|---|---|---|
| K1 | Legal texts are build-time repo files `web/src/content/legal/<doc>.<version>.<lang>.md` + `manifest.json` `{ "<doc>": { "version", "effectiveDate" } }`, converted at build (no raw HTML), rendered by 10.3.1-T1; the page version must equal the version recorded in `user_consents` | 10 README D12; 10.3.1-T1:57, 70 ✅ |
| K1b | The public pages are prerendered and make **no** API call (`/report` is the cited case) | `10-web-ui.md:310`; 10 README D12:170 ✅ |
| K8 | `legal_documents(kind tos/privacy/content_policy, version, locale, effective_at, content_url)` is **S only**; `reports(reporter_email, target_type, target_id, reason, status, resolved_at)`; `user_consents.document` = `tos`/`privacy` | `00-overview.md` §6:323, :324, :274 ✅ |
| CR-7 | `legal_documents` gains `published_by uuid`, `checksum text` (sha256 of the served file); `user_consents.document` → `tos`/`privacy`/`age_18`; `reports` gains `created_at`, `ip_hash`, `handled_by`, `resolution_note` + index `(status, created_at)` | `../README.md` §B4 ✅ |
| D6 | Publish = operator verb `nts-worker legal publish --doc privacy\|tos\|content_policy --version 1.1.0 --locale vi --effective-at <ISO> --content-url /content/legal/privacy.1.1.0.vi.md`; it validates that the served file exists → otherwise **409 `legal_content_missing`**, no row written; on success audit `legal.document_published` (`support`, `internal`); `effective_at > now()` allowed; `content_url` is always a path under the deployed static site, never an external URL | `../README.md` §B3 ✅ |
| D6b | Equality invariant: `manifest.json` (page artifact) equals the max `legal_documents` row per (kind, locale) for the deployed environment — checked at API startup (fail fast) and in CI by `tools/ci/check-legal-versions.py` (owner: 13.2.2-T1). `LegalDocuments:Current:*` is only the seed/bootstrap value | `../README.md` §B3 ✅ |
| D13 | `docs/compliance/cookies.md` is the single inventory (name, host, purpose, category `essential`\|`analytics`\|`marketing`, lifetime, set by whom) and the notice renders from it; the initial set is taken from 09's cookie table (BD3) + the SPA language cookie at writing time — **no name is invented**; while the inventory contains only `essential` entries the platform shows a notice (no wall, dismissible, keyboard reachable, links to `/vi/privacy`), and the first non-essential entry requires a consent banner obtained **before** that cookie is set | `../README.md` §B3 ✅ |
| D11 | The report form's `reason` list is generated from the Content Policy's prohibited/restricted sections; wire values `'copyright' \| 'privacy' \| 'defamation' \| 'abuse' \| 'other'` | `../README.md` §B3 ✅ |
| D16 | Instruments: **Law 91/2025/QH15** (in force 2026-01-01), **Decree 356/2025/ND-CP**, sanctions **Decree 330/2026/ND-CP**; **Decree 13/2023/ND-CP superseded**. Deliverable `docs/compliance/pdpd-memo-<yyyy-mm-dd>.md`; "the **obligation list stays ⚠️** until the lawyer confirms it"; no claim of PDPL compliance in any user-facing text or audit answer before the memo lands | `../README.md` §B3, §B1 K16 ✅ |
| K16 | ⚠️ enacted 2025-06-26 / in force 2026-01-01 (gazette pages expose metadata, not the date text); 13.6.1-T1 **must not promote those dates to ✅** on this evidence | `../README.md` §B1 ✅ / ⚠️ |
| K14 | The P slice explicitly defers: `13.1.1-T2` / `13.1.2-T2` (lawyer review + publish), `13.1.3` (content policy), `13.1.4` (cookie notice) | `13.1-legal-acceptance/plan.md:17` ✅ |
| K2 | `IConsentWriter.RecordAsync(Guid userId, LegalDocument document, string version, IPAddress? ip, ct)`, `LegalDocument { Tos, Privacy }`, config `LegalDocuments:Current:Tos` / `:Privacy` (1–32 chars); rows commit in the caller's transaction | 09.3.1-T1:29, 47-50, 92, 146, 186 ✅ |
| O1/O4 | Personal-version drafts published without lawyer review while the owner is the only user; the lawyer review is two tasks (13.1.1-T2 privacy, 13.1.2-T2 terms), each XS (0.25) — one priced 0.5 d engagement split in two — plus external lawyer cost | `../README.md` §10 `O1`/`O4` ✅ |
| T6/T7 | `content_url` always validates and fails closed; the P contracts are not reopened — 13.2.2-T1 changes D1's version source via CR-12 and 13.2.3-T1 extends the signed 09.3.1-T1 enum via CR-11 | `../README.md` §10 ✅ |

## 3. Domain and data

**3.1 Content artifact model (page side).** One file per (doc, version, lang) under `web/src/content/legal/`; `manifest.json` holds exactly one entry per doc, `{ "version", "effectiveDate" }` (K1). Locale parity is mandatory: a version that exists in only one locale is not publishable (`D17`: every S surface ships vi + en in the same change), and the publish verb refuses the missing locale with 409 `legal_content_missing` (D6). No raw HTML in the files. Markdown only, so 10.3.1-T1's build-time converter is the single renderer.

**3.2 `legal_documents` row produced at publish (API side).** Columns used: `kind` ∈ `tos`/`privacy`/`content_policy`, `version` (semver-like, e.g. `1.0.0`/`1.0.1`/`1.1.0`), `locale` ∈ `vi`/`en`, `effective_at timestamptz`, `content_url text` (a deployed static path), plus CR-7's `published_by uuid` and `checksum text` (sha256 of the **served** file). Written **only** by 13.2.2-T1's operator verb; this folder supplies the version choice, the file and the checksum and performs the call. Invariant (D6b): for the deployed environment, `manifest.json` version == max `legal_documents.version` per (kind, locale) with `effective_at <= now()`; a mismatch fails the API startup and `tools/ci/check-legal-versions.py`. `published_by` is the operator's user id from the CLI session — the folder records which operator ran it in the review log so the audit row can be traced back to the reviewed revision.

**3.3 Version-mint rule [INFERENCE] (D1 "versions semver-like" + D6 + D7).** The reviewer's outcome selects exactly one of three outcomes; the choice is recorded in the review log with a one-line justification each.

| Outcome | Version after the review | Re-acceptance (D7) |
|---|---|---|
| no text change (review notes only, or comments already satisfied by the draft) | unchanged (`1.0.0`) | none; no new `legal_documents` row, only the review log |
| editorial / non-material change (typos, structure, clarity, added contact channel — no obligation, right, retention window, price/credit rule or data category changes) | **patch** (`1.0.1`) | none — accepted (`tos`/`privacy`, `1.0.0`) versions are per (document, version) but the materiality test is the gate for requiring the all-user dialog; `13.2.1-T2` shows `pendingAcceptance` only for a version it is told is material |
| material change (any of the five categories above) | **minor** (`1.1.0`) | mandatory: `13.2.1-T2`'s login-level `pendingAcceptance` + the command-level 409 `legal_acceptance_required` from `ILegalAcceptance.EnsureCurrentAsync` |

Materiality is decided by the review log, not by the author: the log carries a five-row checklist (obligation / right / retention window / price-credit rule / data category) with `changed` or `unchanged` per row, and **any** `changed` forces the minor bump. A published version is never deleted and never overwritten (see §11 rollback).

**3.4 Cookie inventory (D13).** `docs/compliance/cookies.md` is the only place a cookie name is declared; the table columns are exactly name, host, purpose, category (`essential`|`analytics`|`marketing`), lifetime, set by whom. The initial rows come from 09's BD3 cookie table plus the SPA language cookie **as they exist at writing time** — no name is invented in this folder, and a name that is not in the running system is a defect of the inventory, not a licence to add code. `cookie-inventory.generated.ts` is derived from that file by the same task (`tools/ci/generate-cookie-inventory.py`, deterministic ordering by (category, name)) so the SPA cannot drift from the document; the file is committed and the generator is run in CI with `--check` (a stale constant fails).

**3.5 No DDL, no tenant surface.** This folder writes content files, two `docs/compliance/` documents, one generated TS constant, i18n keys and tests. It creates no migration, no table, no column and no index; the `legal_documents` table and its `(kind, version, locale)` uniqueness are 13.2.2-T1's (CR-16). Nothing here reads tenant data, so no tenant filter, `ip_hash` or audit row applies to a user.

**3.6 Data classes and retention.** The legal texts and the cookie inventory are public documents (no personal data, no secret). The review logs (`docs/compliance/privacy-review-<date>.md`, `-terms-`) hold the reviewer's professional identity, the clause matrix, the materiality checklist and the dispositions — no client PII, no user e-mail, no token, no tenant row; they are still text artifacts of the evidence family and therefore pass the redaction grep of D14 / 06.5.1-T1's VP7 pattern before 13.5.1-T1 references them. The memo (`pdpd-memo-<date>.md`) is ⚠️ throughout and is never quoted into user-facing copy or an audit answer (D16). Content-policy examples use synthetic illustrations only [DECISION] — no real person, case or channel name.

## 4. Use cases

**UC-1 — Lawyer review of the privacy policy, then publish (13.1.1-T2).**
*Given* 13.1.1-T1's draft `privacy.1.0.0.vi.md` / `.en.md`, its manifest entry and `docs/compliance/privacy-checklist-1.0.0.md`, *when* the lawyer returns the clause-by-clause review, *then* the reviewer's dispositions are recorded per checklist item (accepted / reworded / rejected with reason) in `docs/compliance/privacy-review-<yyyy-mm-dd>.md` with the materiality checklist, the text is corrected in **both** locales at the chosen version, the manifest and file names agree, and the version is published through 13.2.2-T1:
`nts-worker legal publish --doc privacy --version <v> --locale vi --effective-at <ISO> --content-url /content/legal/privacy.<v>.vi.md` (then `--locale en`).
*Error branches*: (a) the served file is not deployed → the verb exits non-zero with **409 `legal_content_missing`** and writes **no** row — the folder fixes the deploy, never the check; (b) the lawyer's change is material → minor bump + `13.2.1-T2` re-acceptance, and the owner re-accepts on next login (the owner's `1.0.0` acceptance does not cover `1.1.0`); (c) a checklist item cannot be satisfied without a product change → it becomes a backlog row in the owning plan (and, if a text promise must wait for the code, the text must not promise it — same rule as the P slice's "no registration acceptance in P").

**UC-2 — Lawyer review of the ToS, then publish (13.1.2-T2).**
*Given* `terms.1.0.0.vi.md` / `.en.md` + `docs/compliance/terms-checklist-1.0.0.md`, *when* the review runs, *then* it additionally verifies the clauses that depend on other plans: credits (non-cash, expiry per 12.6.2 [DECISION] 12 months ⚠️), refunds (12.7.5 incl. the spent-bonus deduction), the AI-disclosure duty (06/07 flags), "no monetisation guarantee", user is the publisher of record, and governing law/disputes ⚠️ (`12 §6 credit/refund wording (soft)` in the canonical row). *Then* the same publish sequence as UC-1 with `--doc tos`. *Error branch*: the reviewer cannot settle governing law or OpenAI output-ownership ⚠️ → the text carries a neutral formulation and the review log keeps the item ⚠️ **open**; it is not marked resolved by silence, and no backlog row may claim PDPL/other-law compliance (D16).

**UC-3 — Content Policy and the report-reason map (13.1.3-T1).**
*Given* 13 §5.3's prohibited list and restricted list, *when* the policy is drafted, *then* `content_policy.1.0.0.vi.md` + `.en.md` ship with the manifest `content_policy` entry and a mapping table whose left column is exactly the five wire values `copyright`|`privacy`|`defamation`|`abuse`|`other` (D11) and whose right column names the prohibited/restricted section that justifies each, *and* every prohibited/restricted section maps to at least one of the five (no orphan section, no sixth value). *Error branch*: a section with no matching wire value → either reword it into an existing category or raise a CR; the enum is not extended here (CR-11 owns the other enum; `reports.reason` is fixed by 00 §6).

**UC-4 — Cookie inventory, notice copy and the assertion (13.1.4-T1).**
*Given* 09's BD3 cookie table and the SPA language cookie, *when* `docs/compliance/cookies.md` is written, *then* `cookie-inventory.generated.ts` is generated from it, the vi+en `legal.cookieNotice.*` copy lists the same rows (name, purpose, lifetime, category) and links to `/vi/privacy` / `/en/privacy`, the notice is a dismissible notice (not a wall) while every row is `essential`, and two tests assert the closure: the API enumerates every `Set-Cookie` name it emits and the SPA enumerates every cookie its helpers write — both must be a subset of the inventory. *Error branches*: (a) an emitted cookie is missing from the inventory → **CI red**, and the fix is to document it and re-run (never to relax the test or skip the cookie); (b) a first non-essential entry appears → the row and its consent banner land in the same change and the notice stays a notice only until then (D13); (c) the inventory and the generated constant disagree → the `--check` generator run fails.

**UC-5 — Vietnamese data-protection memo and backlog (13.6.1-T1).**
*Given* the consultation, *when* the memo is written, *then* `docs/compliance/pdpd-memo-<yyyy-mm-dd>.md` names the instruments exactly as D16 does (Law 91/2025/QH15; Decree 356/2025/ND-CP; sanctions Decree 330/2026/ND-CP; Decree 13/2023/ND-CP superseded), lists the duties with the product/process change each implies (consent wording, notices, records of processing, impact-assessment dossier, cross-border transfer to OpenAI, breach notification, children, DPO/contact), states plainly what is **not** met yet, and gives one backlog row per required change naming the owning plan/folder; every obligation and every instrument date stays ⚠️ on the README's evidence (K16). *Error branch*: the lawyer's scope or fee is not settled → the memo is delivered with the covered scope stated and the uncovered questions listed as ⚠️ [OPEN]; a partial memo never reads as a clearance, and words like "compliant" do not appear.

## 5. API and events

This folder introduces **no** route, DTO, error code, outbox event or job. It consumes, and must not redefine:

| Surface | Shape (verbatim from the contract) | Owner |
|---|---|---|
| publish verb | `nts-worker legal publish --doc privacy\|tos\|content_policy --version <semver> --locale vi\|en --effective-at <ISO> --content-url <deployed static path>` | 13.2.2-T1 (D6) |
| publish failure | 409 `legal_content_missing` (no row written) | 13.2.2-T1 / CR-10 |
| publish audit | `legal.document_published` (`support`, `internal`) | 13.2.2-T1 |
| version config | `LegalDocuments:Current:Privacy` / `:Tos` (1–32 chars) — seed/bootstrap only after 13.2.2-T1 (D6b) | 09.3.1-T1 (K2) |
| acceptance enum | `LegalDocument { Tos, Privacy, Age18 }` | CR-11 |
| report reason | `'copyright' \| 'privacy' \| 'defamation' \| 'abuse' \| 'other'` | D11 / 00 §6 |
| equality check | `tools/ci/check-legal-versions.py` (startup twin inside the API) | 13.2.2-T1 (D6b) |

**i18n keys contributed** [INFERENCE — names follow 10's Transloco convention and are the folder's design decision]: `legal.cookieNotice.title`, `.body`, `.cookie.name`, `.cookie.purpose`, `.cookie.lifetime`, `.cookie.category.essential|analytics|marketing`, `.dismiss`, `.privacyLink`; `legal.contentPolicy.reason.copyright|privacy|defamation|abuse|other`. Both locales ship in the same change (D17). The Content Policy and the legal texts themselves are **not** i18n keys — they are the Markdown files of K1.

**Events.** None emitted. `report_received.v1` (D11) is 13.3.4-T1's and is only *shaped* by the reason list this folder defines.

## 6. UI

**Owner rule (2026-09-25) — no task in this folder builds a screen, so no `<story>-T0 — UI design` row exists here, and none is invented (the canonical rows in §10 are closed).** The UI surfaces this folder feeds are owned elsewhere and their design gate is that owner's:

| Surface | Owner of the screen | What 13.1 gives it |
|---|---|---|
| the dismissible cookie notice in the shell | **10.2.6-T1** (Blocks edge; CR-13) — UI design belongs to 10's design gate | the inventory + the vi/en copy + the generated constant |
| the report form (`/vi/report`, `/en/report`) | **13.3.4-T1** (page + `CaptchaWidget` from 10's `features/public`; CR-13, `F18`/10 CR-24) | the reason list and the Content Policy text |
| the legal pages `/vi\|en/privacy` and `/vi\|en/terms` | **10.3.1-T1** (renderer; K1 — 10 README D12 pins exactly these two prerendered documents) | the reviewed Markdown + manifest entries |
| the Content Policy's public page | **not in any plan's route set** — resolved (T16/CR-27 adds it to `10.3.1-T1`'s prerender list), **O-8**: 10 D12 / 10.3.1-T1 pin only privacy + terms, while plan 13 FR-13-1 requires the Content Policy to be public; a CR to 10 must add the route before 13.1.3-T1 can claim a page | 13.1.3-T1 ships the file, the manifest entry and the `legal_documents` row; the reason list on 13.3.4-T1's form does not need the page |

**a11y and copy duties this folder still owns (D17).** The notice copy is plain-language, ≤ 2 sentences per row, describes each cookie's purpose in both locales, and is passed to 10.2.6-T1 together with the a11y requirements (keyboard reachable, dismissible by button **and** `Escape`, WCAG 2.1 AA contrast) so the gate is met on the mounted component; the reason-list labels are short nouns that match the policy sections word for word. **O-2** (resolved: no page, CR-13/T16): if the owner later wants a dedicated cookie-notice **page** (instead of the shell notice), a `<story>-T0 — UI design` task must be added and approved before any component work; this plan deliberately ships content + copy + assertions only.

## 7. Jobs and integrations

**Jobs: none.** No new Hangfire job, no `[JobPolicy]`, no allow-listed system job, no scheduler entry. The publish step is a **manual operator action** (D6/`O6`: an operator verb; there is no admin UI and no public route — `O6` accepted).

**Integrations.**

| Edge | Direction | Note |
|---|---|---|
| static site deploy (web) | in | the reviewed files must be deployed **before** the publish verb can succeed (409 `legal_content_missing` otherwise, T6) |
| 13.2.2-T1 registry + verb | out | the only write this folder triggers; the row is created there |
| 10.3.1-T1 renderer | out | renders the pages from the manifest; the page version must equal `user_consents.version` (K1) |
| 13.2.1-T2 re-acceptance | out | consumes a minor bump minted here |
| 13.4.1-T1 OAuth verification | out (soft) | the verification filing needs the published Privacy Policy and ToS URLs and a verified domain (D12/K15) |
| 13.5.1-T1 evidence pack | out | consumes the review logs + the inventory + the notice copy (post-redaction, D14) |
| 13.5.2-T1 calendar | out | rows this folder creates content for: **policy review every 6 months**, the PDPL backlog follow-up (M3), the re-audit cadence ⚠️ |
| 11 / 12 / 06 / 07 | read-only | no e-mail, no blob, no flag, no bucket, no operator CLI beyond `legal publish` (`D18` reuse rule) |

No secret, bucket or configuration key is added by this folder; the only config key that matters (`LegalDocuments:Current:*`) is 09.3.1-T1's and is written by 13.2.2-T1's flow.

## 8. Security and privacy

- **No secret and no personal data in the artifacts.** The content files, the inventory, the review logs and the memo carry no token, key, IP, e-mail of a user, tenant id or other tenant's row. The review logs carry the reviewer's professional identity only. Every text artifact passes 06.5.1-T1's VP7 redaction grep before 13.5.1-T1 cites it (D14).
- **Publishing is privileged and audited.** The verb is CLI-only (operator/system scope, no route, no user token); each call writes audit `legal.document_published` with `visibility = internal`, and the review log records the operator, the version, the checksum and the UTC timestamp so a published revision is traceable to the reviewed text (D6). No new Data Protection purpose is introduced; no key material is touched.
- **Fail closed.** An undeployed file, a missing locale, a manifest/row mismatch (D6b, `T6`) must refuse the publish or fail the startup — never serve a page whose version differs from the recorded consent version (K1).
- **Weakening a published text silently is impossible by design** — versions are append-only, so the worst case (a bad publish) is superseded by a new version and, if material, forces re-acceptance (D7).
- **Legal-status honesty (the folder's main "security" property for the business).** Object.

| Rule | Consequence in this folder |
|---|---|
| The review is external and may not exist when the text goes live for the owner | the P drafts stay live for the owner only (O1); no user-facing text claims "lawyer reviewed" |
| ⚠️ items (governing law, OpenAI output ownership, credit expiry, exact instrument dates) | carried as ⚠️ in the review logs and the memo; never promoted to ✅ on the README's evidence (K16) |
| `D16`: no compliance claim before the memo | no page, e-mail, notice or audit answer may state PDPL/Vietnamese-law compliance; the memo's backlog is the only place such a claim can be earned |
| 18+ / children duty | stated in the Privacy Policy and the ToS (D8); the memo checks it against the obligations ⚠️ |
| Cross-border transfer to OpenAI | named in the Privacy Policy's sub-processor list and examined in the memo (D16) |
| ADministrative requests / DPO contact | a dedicated contact section in both texts; the memo lists the statutory contact duty ⚠️ |

- **Tenant filter / 404-for-other-users / idempotency:** not applicable (no user-scoped read or write, no route). Stated so a reviewer does not look for them.

## 9. Test plan

**Given/When/Then (the folder's own gates).**

1. *Given* the legal content tree, *when* the coverage test runs, *then* every §5.1/§5.2/§5.3 clause anchor is present in **both** locales, each file's version equals its `manifest.json` entry, every manifest doc has a `vi` **and** an `en` file, and no file contains raw HTML.
2. *Given* a stale file version (fixture), *when* the same test runs, *then* it fails naming the file and the expected version.
3. *Given* the five reason values, *when* the mapping test runs, *then* the Content Policy has a section for each and no section is orphaned.
4. *Given* a running API, *when* the cookie inventory test signs in, refreshes, logs out and fetches the CSRF/state surface, *then* every emitted `Set-Cookie` name is in `docs/compliance/cookies.md`; *given* a fixture cookie added in code only, *then* the test is red (this is the failing-before/passing-after case of UC-4).
5. *Given* the SPA cookie helpers, *when* the unit test exercises every helper, *then* the written names are a subset of the generated constant; *given* a helper writing an unknown name, *then* it is red.
6. *Given* the memo/review documents, *when* the docs check runs, *then* the mandated sections exist, the five instrument names appear verbatim, no ⚠️ obligation is marked met, and the backlog table has one row per required change with an owner.
7. *Given* a reviewer-generated instruction, *when* a page claims compliance without a backlog row, *then* the redaction/claims grep (`forbidden-claims` list: "compliant", "fully compliant", "lawyer reviewed" before the review lands) fails CI.

| Test | Project / path | Proves |
|---|---|---|
| `LegalContentCoverageTests` | `NightTales.Architecture.Tests/Legal/LegalContentCoverageTests.cs` (repo-path fixture, no fakes needed) | checks 1–2; the durable regression that a clause or a locale was dropped |
| `ReportReasonMappingTests` | `NightTales.Architecture.Tests/Legal/ReportReasonMappingTests.cs` | check 3; the D11 list cannot drift from the policy |
| `CookieInventoryTests` | `NightTales.Api.IntegrationTests/Cookies/CookieInventoryTests.cs` (`PostgresFixture`, real pipeline) | check 4; the API side of D13 |
| `cookie-helpers.spec.ts` + `cookie-notice-copy.spec.ts` | `web/` Vitest + TestBed (10's convention) | check 5; the SPA side of D13 and the vi/en copy coverage |
| `test_check_legal_content.py` | `tests/ci/test_check_legal_content.py` (pytest, mirroring `tests/ci/test_check_legal_versions.py`) | the CI script's behaviour incl. `--selftest`: red on a missing locale, red on a manifest mismatch, green on the consistent tree |
| `test_check_cookie_inventory.py` | `tests/ci/test_check_cookie_inventory.py` | the inventory parser + the `--check` generator mode (stale constant → red) |
| `test_check_compliance_docs.py` | `tests/ci/test_check_compliance_docs.py` | check 6–7 (memo/review structure, forbidden claims) |

Manifest/repo-path readers are plain file reads; **no mocking library** is used, and any fake needed (e.g. a fake `IClock` for `effective_at` fixtures) is hand-written in `NightTales.TestKit`.

**Manual / external (recorded, not automated).** (1) the lawyer's clause matrix for both documents (external calendar time, `O4`); (2) the owner's walkthrough of the cookie notice on staging in both locales (dismissible, keyboard, no wall); (3) the memo read-through by the owner and creation of its backlog rows in the owning plans; (4) for the 13.5.1-T1 pack: screenshots of the published version pages and of the notice, with the app version and date.

**What is deliberately not tested.** The *content* of the lawyer's opinion (unreviewable by code) and the exact rendered typography of the pages (10.3.1-T1's visual test covers the renderer).

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 13.1.1-T2 | XS (0.25) | Legal | S · M2 | 13.1.1-T1, 09.3.1-T1 (soft) | 13.4.1-T1, 13.5.1-T1, 13.2.2-T1 |
| 13.1.2-T2 | XS (0.25) | Legal | S · M2 | 13.1.2-T1, 12 §6 credit/refund wording (soft) | 13.3.2-T1 (soft), 13.4.1-T1, 13.5.1-T1, 13.2.2-T1 |
| 13.1.3-T1 | S (0.5) | BA | S · M2 | 13.1.1-T1 (soft), 02/03 content rules (soft) | 13.3.4-T1, 10.3.1-T2 (13 CR-27) |
| 13.1.4-T1 | S (0.5) | FE | S · M2 | 10.3.1-T0, 10.2.6-T1, 10.3.1-T1 (soft) | 13.5.1-T1 |
| 13.6.1-T1 | S (0.5) | Legal | S · M2 | 13.1.1-T1, 13.1.2-T1 (soft) | 13.2.2-T1 (soft) |

Rows equal `../README.md` `### B8.1` (13 CR-17 owns the back-edges on the other plans' headers). Task files: `13.1.1-T2-lawyer-review-privacy-policy.md`, `13.1.2-T2-lawyer-review-terms-of-service.md`, `13.1.3-T1-content-policy-vi-en.md`, `13.1.4-T1-cookie-notice-and-inventory.md`, `13.6.1-T1-vietnamese-data-protection-memo.md`.

**Re-estimate.** 0.25 + 0.25 + 0.5 + 0.5 + 0.5 = **2.0 d** for this folder (unchanged against §B9: 13.1.1-T2 0.25, 13.1.2-T2 0.25, 13.1.3-T1 0.5, 13.1.4-T1 0.5, 13.6.1-T1 0.5). Share of the S slice's 8.5 d → 13.2 (2.5) + 13.3 (3.75) + 13.4/13.5 (3.25, R2 inside) + 13.1/13.6 (2.0). 13.1.1-T2 and 13.1.2-T2 share **one** priced 0.5 d lawyer engagement (`O4`, §B9) — they are two task files, not two engagements, and the external fee is a cost line, not a dev-day line.

**Open items for the TL.**

| # | Item | Why it matters |
|---|---|---|
| O-1 (resolved 2026-09-25: the two `docs/compliance/<doc>-review-<yyyy-mm-dd>.md` names are confirmed) | Confirm the review-log artifacts (`docs/compliance/privacy-review-<yyyy-mm-dd>.md`, `-terms-`) as the folder's names — the README names the checklist (`-1.0.0.md`) and the memo pattern but not the review log [INFERENCE] | the TL owns repo artifact names; the evidence pack links them |
| O-2 (resolved: no page — the notice mounts in `10.2.6-T1`; CR-13/T16) | A dedicated cookie-notice **page** (instead of 10.2.6-T1's shell notice) would need a `<story>-T0 — UI design` task (owner rule) | keeps this folder free of unapproved UI work |
| O-3 (resolved: T15 — the verb computes it from the fetched bytes; a re-run detects a hand-edit) | `checksum text` (CR-7) is "sha256 of the served file": confirm it is computed by 13.2.2-T1's verb (this folder supplies nothing) and that a served-file change without a version bump is therefore detectable | a silent edit of a deployed file is the one way to break K1 without a version change |
| O-4 (resolved: T18/O10 — `--material` defaults to false, no re-acceptance) | Whether a patch bump (`1.0.1`) may be published without an owner acceptance round (this plan says yes when the materiality checklist is all `unchanged`) [INFERENCE] | decides whether `13.2.1-T2` shows `pendingAcceptance` for the owner after an editorial fix |
| O-5 (resolved: T20 + CR-21 — 13.6.1-T1 owns the script, 11 wires it into the backend workflow) | The "no compliance claim" grep's forbidden-word list is this folder's (13.6.1-T1) and not a contract; confirm the TL wants it enforced in `backend.yml` next to the other `tools/ci/*` checks (11 owns the workflow file) | avoids a check nobody runs |
| O-6 (resolved: T15 — config is seed-only; the startup check compares `manifest.json` to the max row) | **Contract gap found while writing this folder:** `D6b` says `LegalDocuments:Current:*` is a seed/bootstrap value that the startup check asserts **equals** the max `legal_documents` row, but no task owns updating that value after a publish — 13.1.1-T2 / 13.1.2-T2 publish and report the version, 13.2.2-T1 owns the verb and the check; the README names no editor | without an owner the first publish either fails the startup check or leaves a stale seed value; the TL should name the owner (13.2.2-T1 or the publish verb itself) |
| O-7 | **First cell = story id (house convention, not part of the contract).** The task files carry `\| Story \| Size \| Area \| Depends on \| Blocks \| Phase / flag \|` with the **story id** first (`13.1.1`), as do the sibling `13.1-legal-acceptance/13.1.1-T1-privacy-policy-draft-vi-en.md:5`, plan 01's task files and 06/09/10, while `### B8.1` writes the task id (`13.1.1-T2`) first. The contract values — Size, Area, Phase, Depends on, Blocks — are B8.1-verbatim (incl. `(soft)` markers) and are what the DoR checker compares. Lead12's full-task-id ruling (2026-09-25) was retracted by its author before this folder closed, so the five cells stay in the house form (no divergence from 06/09/10/13) | no churn; a checker must compare the five fields by name, not by cell index |
| O-8 (resolved: T16 + CR-27 — `10.3.1-T1`'s prerender list gains `/vi\|en/content-policy`; the writer's `10.3.1-T2` guess is wrong) | **Contract gap found while writing this folder:** plan 13 FR-13-1 requires the Content Policy to be public, but 10 README D12 (`features/10-web-ui/README.md:170`) and `10.3.1-T1` pin only the `/vi\|en/privacy` and `/vi\|en/terms` prerendered routes, and `manifest.json` (K1) has no third document in 10's build; 13.1.3-T1 can publish the file, the manifest entry and the `legal_documents` row, but **no route exists for the page** | raise a CR to 10 (add a third prerendered document/route, e.g. `/vi\|en/content_policy`) or accept that the Content Policy is only published as a file + API row for now; the report form's reason list (D11) works either way |

## 11. Rollout

- **Order.** 13.1.1-T2 and 13.1.2-T2 are independent of each other (different documents) and run against the lawyer's single engagement (`O4`); they start as soon as the P drafts are signed (both `Depends on` edges are satisfied). 13.1.3-T1, 13.1.4-T1 and 13.6.1-T1 are independent of the reviews and of each other's files and can run in parallel with them (they touch different paths; the only shared file is `manifest.json`, whose ownership is split per key: `content_policy` is 13.1.3-T1's, `privacy`/`tos` stay with the reviews). 13.1.3-T1 must land before 13.3.4-T1 renders the report form, and 13.1.4-T1 before 10.2.6-T1 mounts the notice. The publish calls of UC-1/UC-2 require 13.2.2-T1's verb to exist — that is a **hard ordering inside the S slice**: 13.2.2-T1 may be implemented first and the reviews may publish through it when it is green (the canonical rows keep the edge soft in the other direction).
- **Gate `13-S-legal` (D6/B7).** Publish privacy `1.1.0` (vi + en) → the page shows `1.1.0`, `GET /me/legal` returns `pendingAcceptance` for a non-owner user, accepting clears it, the CI/startup equality check stays green, and a publish of an undeployed version fails closed with `legal_content_missing`. This folder's part of that walkthrough: the reviewed files are the ones published, in both locales, with the manifest entry.
- **Gate `13-S-trust` (this folder's half).** The cookie notice matches the inventory (the test is red when a new cookie is added; the notice is a notice, dismissible, keyboard reachable, links to `/vi/privacy`); the reason list on the report form equals the five D11 values and each maps to a Content Policy section.
- **Rollback.** Content is repo files: reverting the commit reverts the page on the next deploy, and the API side is append-only — a published `legal_documents` row is never deleted (there is no delete verb; D6), so a wrong publish is corrected by publishing a superseding version (patch if the materiality checklist is all `unchanged`, minor otherwise, which re-opens re-acceptance through D7). The generated cookie constant is regenerated, never hand-edited. No migration to undo (this folder has none).
- **Post-deploy follow-ups (recorded in the calendar, 13.5.2-T1).** policy review every 6 months; the memo's backlog rows tracked to M3 (13 §9 exit criteria: "lawyer memo items closed"); ⚠️ re-audit cadence; the PDPL check repeated whenever a text, a sub-processor or a data category changes (which is also the trigger for a version bump per §3.3).
- **Standing caveat.** While the owner is the only user the personal-version drafts stay live (`O1`); registration stays behind `saas_registration` until 13.4.1-T1's verification is granted (`O9`/D12), so no external user can reach an unreviewed or unreconciled text. After the memo (D16) lands, any user-facing text or audit answer that claims compliance must cite a memo item, not this plan.

