# 13 — Compliance: Legal Pages, Deletion Duties, Audits (M1–M3)

> Contracts: `00-overview.md` §6 (`user_consents`, `data_deletion_requests`, `audit_logs`), §11 (privacy NFR). Platform duties: 06 (YouTube), 07 (TikTok). Facts: `../DECISIONS.md` §6, §10.6, §10.7 and the official pages cited (✅).
> This plan is **not legal advice**. A qualified lawyer should review every legal text before the tool is used by anyone other than the owner (DEC 10.14: the lawyer-review tasks themselves are dropped with phase S).
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-only, `00-overview.md` §4.1, DEC 10.14): all P. What stays is what the owner's own tool must satisfy: the privacy policy and terms drafts (13.1.1-T1 / 13.1.2-T1, YouTube Developer Policies III.A), the delivered versioned-acceptance slice (13.2.1-T1; `13.2.1-T2` and `13.2.2-T1` were never shipped and are deleted by DEC 10.14), the deletion engine (13.3.1-T1, III.D.2), the YouTube audit submission (13.4.2-T1) and the TikTok audit submission (13.4.3-T1, now P). Everything public-facing — content policy, cookie notice, DSAR export, self-service account deletion, report form, OAuth verification, evidence pack, compliance calendar and the Vietnamese-data-protection memo — is dropped with phase S.

Sources: [YouTube API Services Developer Policies](https://developers.google.com/youtube/terms/developer-policies) · [YouTube API audit form](https://support.google.com/youtube/contact/yt_api_form) · [TikTok Content Sharing Guidelines](https://developers.tiktok.com/doc/content-sharing-guidelines).

## 1. Purpose and scope

**Goal:** the owner's tool can process data and publish to its own channels within the platform rules, and pass Google's and TikTok's API reviews — with deadlines enforced by code, not memory.

**In scope:** privacy policy and terms drafts; consent capture and versioned acceptance; deletion engine with SLA timers; YouTube API audit + quota extension; TikTok audit; AI-content labelling.

**Out of scope:** billing/tax/e-invoice (dropped, DEC 10.14); public-user data rights (export, self-service deletion, report form), cookie notice, content policy, Google OAuth verification for third-party users, evidence packs, compliance calendar and the Vietnamese-data-protection memo (all dropped with phase S); trademark registration (Could).

## 2. BA review of this part

| # | Finding | Action |
|---|---|---|
| C1 | YouTube policies require a privacy policy describing access/storage/sharing/deletion, the revoke link, and a contact ✅ | Checklist §5.1 |
| C2 | Deletion deadlines differ by trigger (7 days in-app revoke/user request/account delete; 30 days Google-page revoke; 30-day max for stored Authorized Data) ✅ | `data_deletion_requests.due_at` per reason; alerting |
| C3 | Uploads need the user's prior specific and express consent ✅ | Per-video consent (06.2.2) — documented in the policy |
| C4 | Users' channels risk demonetisation under the mass-produced content policy ✅ (DECISIONS §6) | ToS: no monetisation guarantee; product enforces review checkpoints |
| C5 | True crime → defamation/privacy of real people | Attestation (02.3.3) + the owner's editorial check before approval; a public content policy and report form are dropped (DEC 10.14) |
| C6 | Vietnam's Law 91/2025/QH15 (Personal Data Protection Law) + Decree 356/2025/ND-CP apply to us (13 CR-9); exact obligations not mapped ⚠️ | Owner's own diligence; do not cite an obligation until confirmed (the lawyer-memo task was dropped with phase S) |
| C7 | Google OAuth verification is not needed for the owner's own channel ⚠️ | The app runs unverified for its own account; no verification task (dropped with phase S) |
| C8 | Audits need screencasts of flows that must already exist | Build the 06 publish flow first, then record |

## 3. Requirements

- **FR-13-1** Privacy Policy and Terms of Service at `/privacy` and `/terms`, vi and en, versioned with an id and effective date (public pages required by the YouTube and TikTok developer policies).
- **FR-13-2** The owner's acceptance of the current ToS + Privacy versions is recorded (`user_consents`); a material change requires re-acceptance at the next login.
- **FR-13-4** Deletion workflows for channel disconnect and owner request, with `due_at` per C2 and completion evidence; since only the owner's own channels exist, the owner is the only person who can request deletion.
- **FR-13-6** Audit submissions filed for YouTube (C7) and TikTok, tracked by the 08 runbook.
- **FR-13-7** AI disclosure: YouTube `containsSyntheticMedia = true` ✅ (06); TikTok `is_aigc = true` ✅ (07).
- **NFR-13-1** 100 % of deletion requests completed before `due_at`; alert at `due_at − 24 h`.
- **NFR-13-2** Legal pages live before the first YouTube upload and before the TikTok audit filing.

## 4. Epics and user stories

### Epic 13.1 — Legal documents

**13.1.1 Privacy Policy** — *As the owner I want a privacy page that describes what the tool does with my data so that Google and TikTok reviews pass.* Must · M (delivered work kept; the lawyer-review and publish tasks were phase S and are dropped by DEC 10.14)
- Given the checklist §5.1, When the draft is written, Then every item is covered in both languages and published at `/privacy` with version and date.

**13.1.2 Terms of Service** — *As the owner I want clear terms so that the tool's rules are defined.* Must · M
- Given §5.2, Then the ToS covers the tool's use, AI-generated content ownership and disclosure, no monetisation guarantee, the owner's responsibility for published content and platform rules, limitation of liability, termination and governing law ⚠️ (owner's own diligence; a lawyer review is optional for personal use and dropped with phase S).

The former **13.1.3 Content Policy** and **13.1.4 Cookie notice** stories are dropped (DEC 10.14): there are no third-party users, no forms and only essential cookies.

### Epic 13.2 — Consent and versioning

**13.2.1 Policy versions and re-acceptance** — *As the owner I want proof of what I accepted so that a platform dispute is resolvable.* Must · M (delivered: 13.2.1-T1; the blocking re-acceptance dialog `13.2.1-T2` and the legal-document registry `13.2.2-T1` were never shipped and are deleted by DEC 10.14)
- Given a new material version, When the owner accepts it (explicit acceptance before the first YouTube connect or autopilot enable, `13 CR-4`), Then `user_consents` stores version, time, IP. There is no all-user blocking login dialog (`13.2.1-T2` is deleted); the 18+ registration gate was phase S and is dropped.

### Epic 13.3 — Deletion workflows

**13.3.1 Deletion requests engine** — *As the owner I want deletions tracked with deadlines so that no platform deadline is missed.* Must · M
- Given a trigger (in-app channel revoke, Google-page revoke detected, owner's deletion request), Then a `data_deletion_requests` row with `reason` and `due_at` (7 or 30 days ✅) is created; `DataDeletionJob` (hourly) executes scoped deletions; completion writes `completed_at` and an audit entry; alert at `due_at − 24 h`. 02 CR-22: an episode-scope deletion also deletes the extracted-text objects under `research/{userId}/{episodeId}/` (DB rows of `story_sources`, `fetch_tasks`, `source_candidates`, `generation_runs`, `fact_sheets`, `story_versions` cascade with the episode; storage objects do not).

The former **13.3.2 Account deletion**, **13.3.3 export** and **13.3.4 takedown/report** stories are dropped (DEC 10.14): the owner is the only account holder and the only person who can request deletion, the deletion engine above covers it, there is no cohort to export for and no third party to file a report.

### Epic 13.4 — Platform audits

**13.4.2 YouTube API Compliance Audit + quota extension** — *As the owner I want the audit passed so that uploads can be public and quota can grow.* Must · M
- Given the 06.5.1 evidence, When filed via the audit form ✅, Then answers match actual behaviour (consent, retention, deletion windows ✅); the requested quota is justified from the owner's autopilot cadence and daily cap (one channel, 3 videos/week at M1).

**13.4.3 TikTok audit** — *As the owner I want TikTok's audit passed so that posts can be public.* Must · S (phase P after DEC 10.14: TikTok is kept)
- Given 07.4.1, Then the submission states the intended use honestly (a single-owner publishing tool ⚠️, see 07 §10 K2) and shows every required UX item ✅.

### Epic 13.5 — Evidence and local law
The former **13.4.1 Google OAuth verification**, **13.5.1 Evidence pack**, **13.5.2 Compliance calendar** and **13.6.1 Vietnamese data-protection review** stories are dropped (DEC 10.14) with no replacement task: the owner's app runs unverified for its own channel, audit evidence is captured ad hoc at submission time by 13.4.2-T1 / 13.4.3-T1, no deadline calendar is maintained, and local-law obligations stay ⚠️ on the owner's own diligence.

## 5. Technical design and content checklists

### 5.1 Privacy Policy checklist (✅ items are from YouTube developer policies)

- Who we are, contact for privacy questions/complaints ✅.
- Data collected: account data, content created, channel connection data (YouTube/TikTok ids, names, tokens), usage/cost data, logs.
- Data from YouTube API Services: what is accessed, stored, shared, and how it is used ✅; statement that we use YouTube API Services and link to YouTube Terms and Google Privacy Policy ⚠️ (confirm exact required links in the policy text).
- How users revoke access: in-app disconnect and https://security.google.com/settings/security/permissions ✅.
- Retention and deletion: 7-day and 30-day windows by trigger; 30-day refresh/delete of stored Authorized Data ✅; backups are kept [DECISION] 30 days, so deleted data (including encrypted channel tokens) ages out of the backups within that window, and a restore re-applies every deletion completed after the backup date before the service reopens.
- Upload consent: every upload has its own consent record — given by the user, or for autopilot series derived from the owner's versioned series authorization after a veto window in which the owner is notified and can edit or cancel (06.2.2).
- We never ask for YouTube passwords ✅.
- Sub-processors: OpenAI (story/image generation), hosting, storage, e-mail provider.
- Access: there is exactly one account holder (the owner) and no staff or admin surface (DEC 10.14); nobody else can view story text, sources or videos. Security alerts about the account (MFA reset, recovery-code use, new-device login, re-auth failures, 08 §5.2) go only to the owner's mailbox.
- International transfers, security measures, rights on the owner's own data (access, correction, deletion), and changes to the policy. Children: the tool is used only by its owner, so there is no age gate (dropped with phase S).

### 5.2 Terms of Service checklist

Eligibility and account security, ownership (the owner owns the outputs subject to provider terms ⚠️ check OpenAI terms on output ownership), AI disclosure duty, no monetisation guarantee, the owner is the publisher of record on their channels, platform rules compliance (YouTube/TikTok), service availability, liability limits, governing law/disputes ⚠️. There are no credits, refunds or acceptable-use policy to link (dropped with phase S).

### 5.3 Content Policy checklist (dropped with phase S, DEC 10.14)

The list below is kept only as the owner's own editorial guidance; there is no published Content Policy page.
Prohibited: realistic images of identifiable real people; accusations not supported by cited sources; content sexualising anyone; harm to minors; glorifying violence or self-harm; hate/harassment; copyrighted text/music/footage; impersonation. Restricted (allowed with care): real historical cases with sources and respectful tone; religious/spiritual content without targeting groups.

### 5.4 Deletion scopes

| Scope | Deleted | Kept (pseudonymised) |
|---|---|---|
| Channel (in-app revoke / Google revoke) | tokens, channel metadata, platform payloads, external ids | publication rows without external ids |
| Account (owner request) | profile, series, episodes, story versions, assets, channels, sessions, audit rows | security audit rows with the user id hashed; `user_consents` (13 CR-9: retained as proof of what was accepted, user id hashed) |
| Episode (owner delete) | story versions, assets | usage_records (cost metering) |
| Backups (all scopes above) | ageing out: deleted rows and objects leave database and storage backups within the [DECISION] 30-day backup retention; no backup is kept longer | nothing beyond the Kept column of the live scope |

## 6. Task breakdown

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 13.1.1-T1 | Draft privacy policy (vi/en) from checklist, incl. the YouTube/TikTok data clauses | Legal/BA | M | — |
| 13.1.2-T1 | Draft ToS (vi/en), incl. the YouTube Terms clause (III.A.1) | Legal/BA | M | — |
| 13.2.1-T1 | Policy versions + acceptance endpoint/gate + owner acceptance dialog (owner accepts before first YouTube connect / autopilot; 13 CR-4) | BE/FE | M | 09.3.1-T1, 13.1.1-T1, 13.1.2-T1 |
| 13.3.1-T1 | Deletion engine + job + alerts | BE | M | 09.2 |
| 13.4.2-T1 | YouTube audit submission | BA | M | 06.5.1-T1 |
| 13.4.3-T1 | TikTok audit submission | BA | S | 07.4.1-T1 |

Lawyer review/publish of the legal texts, the content policy, the cookie notice, the 18+ gate, account-deletion flow and backup-window check, export, report form, OAuth verification, evidence pack, compliance calendar, the local-law memo, the legal-document registry (`13.2.2-T1`) and the blocking all-user re-acceptance at login (`13.2.1-T2`) are dropped with phase S (DEC 10.14) and have no rows here.

## 7. Test plan

- **Integration:** each deletion trigger creates the right `due_at`; job deletes the right scope; alert fires at T−24 h (clock fake) (13.3.1-T1).
- **Manual (owner's own account):** delete one channel in-app and once from the Google permissions page, then verify tokens and metadata are gone, the objects are removed from storage and Google's permissions page no longer lists the app (`https://security.google.com/settings/security/permissions`).
- **Review:** policy text vs actual behaviour walkthrough before each audit submission.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Policy says one thing, code does another | Behaviour walkthrough before filing; tests for deletion windows; backup retention checked against the stated 30 days in the restore drill (11.6.1) |
| Verification/audit delays | YouTube audit filed as soon as upload works and the policy pages are public (13.4.2) (M1 phase 2, 11 CR-18: the policy URLs must be public); pre-audit mode meanwhile; appeal/re-file on rejection. TikTok audit follows 07.4.1 and may be refused for a single-owner use (07 §10 K2) |
| Local-law gaps | Owner's own diligence; obligations stay ⚠️ (the lawyer memo was dropped with phase S) |
| Defamation claim from true-crime content | Sources + attestation (02.3.3) + the owner's editorial check before approval; no content policy or takedown form is shipped (DEC 10.14) |

## 9. Exit criteria

- **M1:** drafts ready; YouTube connect + private publish + consent flows on staging; deletion engine live on staging.
- **M2 (no longer applies):** the SaaS checklist (lawyer-reviewed pages, OAuth verification, re-acceptance for third-party users, export, account deletion, evidence pack) is dropped with phase S.
- **M3:** YouTube audit approved and TikTok audit filed or approved.

## 10. Open questions and contract change requests

- ⚠️ OpenAI terms on output ownership and on depicting real people.
- **Contract change reversed (00 §6 is correct as written):** no `legal_documents` table is added — the legal texts are repo files with `manifest.json` versions (`13 CR-1`) and the API serves the file matching the deployed environment (`13 CR-7`, `13 CR-12`, `13 CR-26`; `00-overview.md:308`). The `reports` table and `POST /reports` are dropped with phase S (DEC 10.14).
- **Contract changes (applied to 00):** 00 §11 Data NFR states 30-day backup retention and that deleted data ages out of backups within that window; the restore procedure re-applies deletions completed after the backup date (11.6.1).

## 11. Estimate summary

| Epic | Days |
|---|---|
| 13.1 Legal documents (privacy + ToS drafts; the lawyer reviews are dropped) | 2.0 |
| 13.2 Consent/versioning (one task: `13.2.1-T1`; the 18+ gate and the legal-document registry `13.2.2-T1` are deleted) | 1.0 |
| 13.3 Deletion engine (one task: `13.3.1-T1`; account deletion, export and report form dropped) | 1.0 |
| 13.4 Audits (YouTube submission + TikTok submission) | 1.5 |
| **Total** | **5.5** → with 20 % buffer **6.6** person-days (the sum of the six remaining task files: 13.1.1-T1 1.0, 13.1.2-T1 1.0, 13.2.1-T1 1.0, 13.3.1-T1 1.0, 13.4.2-T1 1.0, 13.4.3-T1 0.5 — matching `features/13-compliance/README.md` §9; was 13.5 / 16.2 before DEC 10.14: evidence pack, calendar, local-law memo, OAuth verification and the public data-rights flows are gone) |
