# 13.4 — Verification, evidence and calendar (S slice)

> Contract: `../README.md` Part B — §B0 folder brief (:150), §B1 K13/K15/K16/K17/K18 (:162, :164–166), §B2 F9/F16 (:179, :186), §B3 **D12** (:234–238), **D14** (:246), **D15** (:252–254), **D17/D18** (:256, :260–262), §B4 CR-15/CR-17 (:283), §B7 gate `13-S-trust` (:325) / `13-S-R2` (:326), §B8.1 rows (:356–359), §B9 (:381–384), §B10 O7/O8/O9/T5/T9 (:400, :401, :403, :406, :416); high-level `../../13-compliance.md` §3 FR-13-5/FR-13-6/NFR-13-1, §4 epics 13.4/13.5, §6 rows 13.4.1-T1/13.4.3-T1/13.5.1-T1/13.5.2-T1, §9 exit criteria M2/M3.
> This folder is **documents and filings only**. Per D18 (:262) it creates no code, no route, no DTO, no table/column/index, no migration, no job, no CLI verb and no `tools/` script; the only *actions* it takes are reading existing surfaces, writing markdown, hashing files and (13.4.1-T1 only) flipping the existing flag `saas_registration` through 09.9.2-T2's operator verb.
> Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE]. Legal statements keep exactly the status Part B gives them — nothing here is promoted to ✅.

## 1. Scope

**In scope.** The four S tasks of plan 13 epic 13.4/13.5 that produce filings and operating records:

| Task | Deliverable | Why it exists |
|---|---|---|
| `13.4.1-T1` | Google OAuth sensitive-scope verification filing + the `saas_registration` launch gate | F9 (:179): until verification is granted every external user sees the unverified-app warning screen and an app left in Testing is capped at 100 users (K17); no plan pinned the filing or the gate |
| `13.4.3-T1` | TikTok Content Sharing audit filing (R2) | 13 §4 `13.4.3`; 13 §9 M2/M3; O8 (:401) keeps the 0.5 d inside the S slice |
| `13.5.1-T1` | Reusable evidence pack (in-repo index + binaries outside the repo) | F16 (:186) / D14 (:246): re-audits are only cheap if the artefacts are reusable and verifiable |
| `13.5.2-T1` | `docs/compliance-calendar.md` + the monthly deletion-SLA/DSAR report | F14 (:184) / D15 (:252): deadlines and the 72-hour report target must be measured, not remembered |

Folder total **2.25 d raw** (1.0 + 0.5 + 0.5 + 0.25; §B9 :381–384 all "unchanged"), inside epic 13.4 2.5 d + 13.5 0.75 d (13 §11).

**Out of scope.** (a) The P slice (Part A §11) — 13.4.2-T1's YouTube API audit is P and already DoR'd; this folder only *reuses* its calendar-row precedent (K13, :162) and its appeal URL (D12, :236). (b) Every legal text and its lawyer review (13.1.1-T2 / 13.1.2-T2, 13.1.3-T1, 13.1.4-T1), the `legal_documents` registry (13.2.2-T1), re-acceptance (13.2.1-T2), the age gate (13.2.3-T1), export/deletion/report code (13.3.x) and the PDPD memo (13.6.1-T1) — all owned elsewhere; this folder cites them and waits on them. (c) All UI: the register form, re-acceptance dialog, channel/publish screens, export/deletion status pages, `/vi|en/report` page and the 10.9.1 admin screens belong to 10's tasks (see §6). (d) Machine-checked compliance tooling: the redaction grep and the version-equality check are other tasks' artifacts (`06.5.1-T1` VP7, `13.2.2-T1` `tools/ci/check-legal-versions.py`); this folder runs them, it does not own them.

## 2. Confirmed facts

| Id | Fact | Status / source |
|---|---|---|
| F9 | OAuth verification is a **launch gate**: unverified apps show the warning screen to every user; Testing is capped at 100 test users with 7-day refresh tokens | ✅ K17 (:166) `00-overview.md` §4.1:97–98; `support.google.com/cloud/answer/15549945` |
| K15 | `youtube.upload` is a **sensitive** scope → brand verification first, then data-access verification; the submission needs an **English demo video set to Unlisted**, homepage/privacy/ToS links on a **verified domain**, and the app in **production** | ✅ `developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification`, `support.google.com/cloud/answer/15549049`, `/15549135`, `/13464321`, `/13804565` (read 2026-09-25 per README) |
| K16 | Vietnam: **Law 91/2025/QH15** (in force 2026-01-01), **Decree 356/2025/ND-CP**, sanctions **Decree 330/2026/ND-CP**; **Decree 13/2023/ND-CP** superseded | ✅ instrument numbers/titles; ⚠️ enacted/effective dates and the **obligation list** stay ⚠️ until the lawyer confirms (13.6.1-T1); this folder must not promote them |
| K13 | `docs/compliance-calendar.md` is **13.5.2-T1's** file; 13.4.2-T1 deliberately keeps its rows inline in its own §6 as a ready-to-copy row | ✅ `13.4.2-audit-filing/plan.md:17`, `:192`, `:225` |
| K18 | Reusable blocks: `RequireFeature("…")` → 404 `feature_disabled` (09.9.2-T2); operator verbs `nts-worker <noun> <verb>` (e.g. `flags set <key> on`); metrics `deletion_requests_open`, `deletion_request_overdue_total`; audit `account.data_deleted` | ✅ 09.9.2-T2, `09 README BD10:690`, `06.4.3-T1:13`, `08.4 plan:105` |
| 13 §3 FR-13-5 | Takedown/report channel with a **72-hour response target** [DECISION] — a target, not a promise (T9, :416) | ✅ `../../13-compliance.md` §3 |
| 13 §3 FR-13-6 / NFR-13-1 | Submissions filed with evidence packs and tracked in the calendar; 100 % of deletion requests before `due_at`, alert at `due_at − 24 h` | ✅ |
| 13 §9 M2/M3 | M2: pages lawyer-reviewed, OAuth verification **filed**, re-acceptance/export/deletion working, **evidence pack complete**; M3: audit approved, memo items closed | ✅ |
| D12/D14/D15 | Filling artifacts and the flag rule (see §3) | ✅ README :234, :246, :252 |
| — | Google's verification console, the YouTube appeal page `https://support.google.com/youtube/contact/yt_api_appeals` (:236) and the TikTok audit portal are **external**; their field sets and reply times are not published in the plans | ⚠️ [OPEN] — captured verbatim at filing time, exactly as 13.4.2-T1 does for the YouTube audit form |

## 3. Domain and data

No table, column, index, migration or DTO is created or changed by this folder (D18). The "domain" here is the set of artifacts and the values read from existing systems.

**Artifacts (repo, text only).**

```
docs/compliance/google-oauth-verification-<yyyy-mm-dd>.md   # 13.4.1-T1   (path fixed by D12 :236)
docs/compliance/evidence-<yyyy-mm-dd>/index.md              # 13.5.1-T1   (path fixed by D14 :246)
docs/compliance-calendar.md                                 # 13.5.2-T1   (path fixed by K13/D15)
```
`13.4.3-T1` produces the TikTok filing; the README names no path for it → `docs/compliance/tiktok-audit-<yyyy-mm-dd>.md` is proposed following D12/D16's convention [INFERENCE], ruled **T17** (README §B10): this stays 13.4.3-T1's single filing copy and 07.4.1-T1 links to it (see §10 and the folder report). Artifacts owned by other tasks and only *referenced*: `docs/compliance/cookies.md` (13.1.4-T1, D13), `docs/compliance/pdpd-memo-<yyyy-mm-dd>.md` (13.6.1-T1, D16), `docs/compliance/audit-pack/<yyyy-mm-dd>/` (06.5.1-T1 — the YouTube **audit** pack, a different deliverable from D14's evidence pack).

**External (recorded, never committed).** Evidence binaries for D14 → `~/.nts/compliance/evidence-<yyyy-mm-dd>/` on the FileVault disk, never the repo and never `/Volumes/Project` (O7 :400, D14 :246, T5 :406, CR-15 :283). Submitted forms, Google/TikTok ticket references and support threads → recorded in the index by location + sha256, not copied into the repo.

**Values read (read-only, no config key added).** `YouTube:AuditStatus` and the audited client id from `06.1.1-T1`; the legal version currently served — `manifest.json` (page source) must equal `MAX(version)` of `legal_documents` per (kind, locale) for the deployed environment (D6 :205, CR-12 :270); the flag `saas_registration`'s state through 09.9.2-T2's store (never read from a config file); the P metrics `deletion_requests_open` / `deletion_request_overdue_total` and the S ones `dsar_export_failed_total` / `reports_open_total` + oldest-open-report age (D15 :254). The two S metrics are emitted by `13.3.3-T1` (`dsar_export_failed_total`) and `13.3.4-T1` (`reports_open_total`) — ruled **T13** (README §B10); 13.5.2-T1 still records `n/a — emitting task <id>` for any metric it reads before that folder ships (never a fabricated 0).

**Identifier list used verbatim from the contract (no synonyms).** Flag `saas_registration`; error codes `feature_disabled` (404), `legal_content_missing` (409), `age_attestation_required` (400), `report_target_invalid` (400); operator verbs `nts-worker flags set <key> on|off`, `nts-worker legal publish --doc … --version … --locale … --effective-at … --content-url …`; audits `legal.document_published`, `abuse.report_received`, `abuse.report_resolved`, `account.data_deleted`; surfaces `GET /me/legal`, `POST /me/legal/accept`, `POST /me/export`, `GET /me/exports/{id}`, `POST /me/delete`, `GET /me/deletion-requests`, `POST /reports`, `PATCH /admin/reports/{id}`, `GET /admin/reports`, `GET /admin/deletion-requests`.

## 4. Use cases

- **UC-1 (13.4.1-T1) — file verification, keep the door closed.** Given the lawyer-reviewed pages are live and multi-user connect runs, When the owner files sensitive-scope verification, Then the submission proves every D12 pre-condition, the artifact records it, the calendar gets its row, and registration still answers `feature_disabled` until the grant.
- **UC-2 (13.4.1-T1) — open registration after the grant.** Given verification granted, When the owner runs the flag verb, Then new external users can register and the unverified-app screen no longer appears; the flip is recorded in the calendar row and the 08.3 runbook.
- **UC-3 (13.5.1-T1) — build the pack once, reuse it.** Given staging with a synthetic tenant, When the seven flows are captured and hashed, Then 13.4.1-T1, 13.4.2-T1's re-file and 13.4.3-T1 can each cite artefact ids instead of retaking screens.
- **UC-4 (13.4.3-T1, R2) — file the TikTok audit.** Given 07.4.1's required-UX list and the pack, When each item maps to a shipped screen with an artefact, Then the filing is submitted with no unsupported claim.
- **UC-5 (13.5.2-T1) — never miss a window.** Given the calendar exists, When a month closes, Then the log gets the metric row and any `due_at` miss is written down, not hidden; the three alert rules are handed to 11.6.2-T1.
- **UC-6 (13.5.2-T1) — react to a lapse.** Given a row is overdue (e.g. the 6-month policy review), Then the owner sees it with its evidence link and the remediation is recorded in the log.

## 5. API and events

**None new.** This folder adds no route, DTO, error code, outbox event or handler (D18 :262; §B8 has no BE task here). What it exercises and documents:

| Interaction | Contract | Used by |
|---|---|---|
| `nts-worker flags set saas_registration on` | 09.9.2-T2's flag operator verb; unknown/flag path → 404 `feature_disabled` while off | 13.4.1-T1 (§B7 `13-S-trust` :325, O9 :403) |
| Registration (10.2.2-T1's form → `POST /auth/register`) | unchanged; the gate is the feature check, not a new code | 13.4.1-T1 |
| `GET /me/legal` / `POST /me/legal/accept` | `pendingAcceptance` (D7 :211) — the re-acceptance evidence a demo video must show | 13.4.1-T1, 13.5.1-T1 |
| `POST /reports`, `GET /admin/reports`, `PATCH /admin/reports/{id}` | always 202 for a well-formed report (T1 :398), `resolutionNote` optional | 13.5.1-T1, 13.5.2-T1 (age of the oldest open report) |
| `POST /me/export`, `GET /me/exports/{id}`, `POST /me/delete`, `GET /me/deletion-requests` | D9/D10 — recorded flows, and the source of the SLA numbers | 13.5.1-T1, 13.5.2-T1 |
| Audit rows `legal.document_published`, `abuse.report_received`, `abuse.report_resolved`, `account.data_deleted` | written by other tasks; cited as evidence references | 13.4.1-T1, 13.5.1-T1 |

Version-drift guard relevant to the filing (F10 :180): a filing that claims "privacy policy v1.1.0 is live" is only true when the page shows it **and** `MAX(version)` for that (kind, locale) matches — i.e. the D6/CR-12 equality check is green (startup fail-fast + `tools/ci/check-legal-versions.py`, owned by 13.2.2-T1). A publish of a version whose static file is not deployed fails closed with 409 `legal_content_missing`.

## 6. UI

**No UI work is owned by this folder** — no screen, component, route, copy key, breakpoint or focus order is designed or built here (owner rule 2026-09-25: every *web/UI* story needs an approved `-T0 — UI design` task; this folder has none, and D18 forbids new surfaces). The four tasks are document/filing tasks; the screens that the filings and the demo video *record* belong to their own folders and their own T0 designs:

| Recorded surface (evidence only) | Owner | What this folder does |
|---|---|---|
| Register form + 18+ checkbox, error copy for `age_attestation_required` | 10.2.2-T1 / 10.3.2-T1 (13.2.3-T1 + 13.2.1-T2 consume) | screenshots the 18+ attestation and the consent gate (13.5.1-T1) |
| Re-acceptance blocking dialog after a material change | 10.3.2-T1 ← 13.2.1-T2, 13.2.2-T1 | demo video shows the pending-acceptance grant (13.4.1-T1, K15) |
| Channel connect / publish form / per-video consent / veto window | 10.7.1-T1, 10.7.2-T1, 10.7.4-T1 **[OPEN]** — 10 wave 2 is not detailed yet (`13.4.2-audit-filing/13.4.2-T1…md:23` "capture whatever ships and cite the task ids") | captures them as artefacts; if a screen is not shipped the artefact is `provisional` |
| Export + deletion status pages, `GET /me/deletion-requests` view | 10.8.2-T1 ← 13.3.3-T1 / 13.3.2-T1 | capture for D14 + the deletion-SLA evidence |
| `/vi/report` + `/en/report` form (CAPTCHA widget from 10's `features/public`) and the admin report screen | 13.3.4-T1 owns the page; 10.9.1-T1/T2 own the screens (T10 :414, 10 CR-24) | captures the report flow; the 72-hour figure is measured on `created_at` |
| Cookie notice | 10.2.6-T1 ← 13.1.4-T1 (D13) | captures the notice rendered from `docs/compliance/cookies.md` |

i18n (D17 :256): every *user-facing* surface of these flows ships vi + en in its own task; this folder's own artifacts are English markdown (repo convention) with vi/en names quoted only where the evidence refers to a page. a11y: no owned surface — the a11y review gate applies to the screens above in 10.

## 7. Jobs and integrations

**No new job, no new schedule, no new integration endpoint** (D18). What exists and is consumed:

- **Existing jobs (read-only expectations).** `ExportCleanupJob` (daily, ≤ 50 per run, 7-day retention — D9 :226) and the hourly `DataDeletionJob` (D10 :228) produce the states 13.5.1-T1 photographs and 13.5.2-T1 measures. No job is created or modified here.
- **Monitoring (08.1).** The monthly report reads `deletion_requests_open`, `deletion_request_overdue_total` (P, K18), `dsar_export_failed_total`, `reports_open_total` and the oldest open report age (D15 :254). The S metrics' emitters are `13.3.3-T1` / `13.3.4-T1` — ruled T13, per §3.
- **Alert rules to hand over (not added here).** 11.6.2-T1's list gains: deletion overdue (`due_at − now < 24 h`, already P), export failed twice for the same request, oldest open report > 72 h (D15 :254).
- **External integrations (outside the repo).** Google Cloud console / OAuth verification console (13.4.1-T1), YouTube appeal page `https://support.google.com/youtube/contact/yt_api_appeals` (:236), the TikTok developer portal (13.4.3-T1), and the time-keeping that 13.5.2-T1's calendar encodes. Reply times are ⚠️ unknown (K15 note, 13 §2 C7) — the calendar stores "expected reply" as a range/unknown, never a fabricated date.
- **Operator CLI (reuse only).** `nts-worker flags set saas_registration on` (09.9.2-T2) and, for version evidence, `nts-worker legal publish …` (13.2.2-T1, D6). No verb is added.
- **08.3 runbook.** 13.4.1-T1 adds the flag paragraph (D12 :238: "The flag is named in the calendar row and in the 08.3 runbook"); the runbook file itself is 08's.

## 8. Security and privacy

- **Evidence content (D14 :246).** Synthetic tenants only (`demo+<n>@invalid`), no real e-mail, IP, token or API key, no real user's story text; the redaction grep is 06.5.1-T1's VP7 pattern (`VP7_RedactionClean`, `06.5.1-T1-audit-evidence-pack.md:117`). Failure to redact = recapture, never publish.
- **Binaries stay out of the repo.** `~/.nts/compliance/evidence-<yyyy-mm-dd>/` (FileVault; O7/T5/CR-15). Repo copies are markdown + sha256 + paths only (`.gitignore`/review check: `git status --porcelain docs/compliance` shows no media). ⚠️ `/Volumes/Project` is an unencrypted APFS volume with Owners disabled — nothing sensitive may sit there (the 01/11 rule; memory of that constraint is why the rule exists).
- **Demo video.** Google's demo video is **Unlisted** and in **English**, showing the consent grant (K15); its id + visibility are recorded in the artifact and the pack index. Unlisted is a sharing setting, not a secret — the video must still not show real accounts or tokens.
- **No secrets in the filings.** The audited client secret is referenced by *name only* (`nts/<env>/api/YOUTUBE_CLIENT_SECRET`, `13.4.2-T1…md:13`'s convention); Data Protection purposes and the `ITokenProtector` path are untouched (no key, no purpose added).
- **Tenant filter / authorization.** No query is written here; the evidence flows read only the owner's own tenant plus 10.9.1 admin screens (admin policy owned by 09.10.x). The public report path is anonymous by design (T1 :398, `anon` policy + CAPTCHA, T4 :404) — the pack records it, the plan adds no limiter.
- **Audit.** The only writes this folder triggers in the product are the flag change (audited by 09.9.2-T2's path) and, indirectly, acceptances/report resolutions photographed from other tasks' audit rows (`legal.document_published`, `abuse.report_received`, `abuse.report_resolved`, `account.data_deleted`). No new audit id.
- **Retention of the pack.** Kept until the next successful re-audit, at least **24 months** [DECISION] (D14 :248).
- **Legal caution.** K16's obligation list and dates stay ⚠️; no filing, calendar row or product copy may assert PDPL compliance before `docs/compliance/pdpd-memo-<yyyy-mm-dd>.md` lands (D16 :258). This folder states instruments by number only where it quotes the memo.

## 9. Test plan

This folder has no code path, so its "tests" are document/documentation checks that a reviewer or the owner runs; each is a named command or a named manual step, and none of them requires new tooling (D18).

| # | Check | How | Pass condition |
|---|---|---|---|
| TS-1 | Header/shape integrity of this folder | `python3 tools/verify_plan.py plans/features/13-compliance` and the folder's header-vs-§B8.1 diff (`13 README :342-359`) | 1 plan.md with sections 1–11; 4 task files with H1 + header + 9 headings; every header row equals its `### B8.1` row |
| TS-2 | Redaction of every text artifact | VP7 grep pattern from `06.5.1-T1:117` over `docs/compliance/**` | no token / e-mail (except `*@invalid`) / IP / real video-id pattern; no binary (`*.png\|*.mp4`) tracked |
| TS-3 | Artifact ↔ sha256 ↔ index consistency (13.5.1-T1) | `shasum -a 256 ~/.nts/compliance/evidence-<date>/*` against the `index.md` column | every index row has a file, a hash that matches, a date, an app version and a redaction-check mark |
| TS-4 | Launch-gate behaviour (13.4.1-T1) | `Api.IntegrationTests` (Testcontainers) exercising 09.9.2-T2's `RequireFeature`: flag **off** → `POST /auth/register` returns **404** `feature_disabled`; flag **on** → succeeds | the flag, not a code path, is the switch; **owned/implemented by 09.9.2-T2**, this folder only names the gate and records the result (no test file is added here) |
| TS-5 | Version-drift guard before filing (13.4.1-T1) | `tools/ci/check-legal-versions.py` (13.2.2-T1) + the startup equality check on the deployed environment | green, and the filing's quoted version equals the page and the max `legal_documents` row |
| TS-6 | Filing completeness (13.4.1-T1, 13.4.3-T1) | manual matrix read: every scope row cites a code path; every required-UX row cites a shipped task id + artefact id; verdict ∈ {proven, provisional, gap}; **any `gap` blocks the filing** | no empty cell, no unsupported claim |
| TS-7 | Calendar completeness and honesty (13.5.2-T1) | grep each D15 item kind in `docs/compliance-calendar.md`; diff the 13.4.2-T1 row against `13.4.2-audit-filing/plan.md:192/:225`; read `⚠️` rows | all seven item kinds present with owner + cadence + next due; the copied row is identical; unknown deadlines read "pending", never a fabricated date |
| TS-8 | Deletion-SLA numbers are real (13.5.2-T1) | read the month's metrics from the 08.1 stack next to the log row | row equals the dashboard (or says `n/a — emitting task <id>`); a month with `deletion_request_overdue_total > 0` records the miss |
| TS-9 | Evidence reproducibility | second person retakes one artefact using the index's reproduction column | same observable state, no real user data in the recapture |

No permanent automated test is added by this folder (nothing executable is owned here); the checks above that are automated belong to 09.9.2-T2, 13.2.2-T1 and 06.5.1-T1 and are cited as such.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 13.4.1-T1 | M (1.0) | BA | S · M2 · flag `saas_registration` | 13.1.1-T2, 13.1.2-T2, 06.1.1-T1, 09.9.2-T2 (soft) | 13.5.2-T1 |
| 13.4.3-T1 | S (0.5) | BA | R2 | 07.4.1-T1, 13.5.1-T1 (soft) | 07.4.1-T2 |
| 13.5.1-T1 | S (0.5) | BA | S · M2 | 13.1.1-T2, 13.1.2-T2, 13.1.4-T1, 13.2.3-T1, 13.3.2-T1, 13.3.3-T1, 13.3.4-T1, 10.7.2-T1 (soft) | 13.4.3-T1 (soft) |
| 13.5.2-T1 | XS (0.25) | BA/Ops | S · M2 | 13.2.2-T1 (soft), 13.4.1-T1 | — |

Rows are byte-equal to `../README.md` §B8.1 (:356–359). Milestones per `00-overview.md` §4.1 ("where a plan names M1 for an S item, read M2"): all S · M2 except `13.4.3-T1` (R2, :361).

Re-estimate (§B9 :381–384 — no change from the high-level plan):

| Task | High-level (§6) | Detailed | Why |
|---|---|---|---|
| 13.4.1-T1 | 1.0 | 1.0 | unchanged |
| 13.4.3-T1 | 0.5 | 0.5 | unchanged (R2; inside the 8.5, O8) |
| 13.5.1-T1 | 0.5 | 0.5 | unchanged |
| 13.5.2-T1 | 0.25 | 0.25 | unchanged |
| **Folder** | **2.25** | **2.25** | 0 change; external lawyer/console time is a cost line, not dev days (D16/B9 :388) |

Sequencing (§B5 :305–313): **W3** = 13.4.1-T1 + 13.5.1-T1 (after the lawyer review is published and the pages are live; the pack also feeds 13.4.1-T1's demo video); **W4** = 13.5.2-T1, then 13.4.3-T1 last (R2, after 07's slice and the pack). Hand-offs: `13.5.2-T1` may start against 13.2.2-T1's contract (soft) and hard-waits on 13.4.1-T1's filing row; `13.4.3-T1` soft-waits on 13.5.1-T1.

[OPEN] items carried into implementation (none blocks writing these tasks): the TikTok filing path/owner reconciliation with `07.4.1-T1` — **ruled T17** (`docs/compliance/tiktok-audit-<yyyy-mm-dd>.md` is 13.4.3-T1's); the emitters of `dsar_export_failed_total` (13.3.3-T1) / `reports_open_total` (13.3.4-T1) — **ruled T13** (§3/§7); 10 wave-2 screens not detailed yet, so pack artefacts for channel/publish/veto start `provisional` (§6, `[OPEN]`); the exact Google console field set and reply times (⚠️, captured at filing time).

## 11. Rollout

Order and gates (each gate is §B7's, quoted by id):

1. **Pre-work (W1–W2, other folders).** 13.1.1-T2/13.1.2-T2 publish the lawyer-reviewed texts; 13.2.2-T1 creates `legal_documents` + the publish verb + the startup/CI equality check; 13.2.3-T1 and 13.2.1-T2 give the 18+ attestation and the re-acceptance dialog; 13.3.3-T1/13.3.2-T1/13.3.4-T1 give export, deletion and reports. 13.4.1-T1's hard dependency is the pair 13.1.x-T2; the rest of this folder starts as soon as they land.
2. **W3 — file and capture.** 13.4.1-T1: prove D12's pre-conditions (verified domain + live homepage/privacy/ToS, production app, narrowest scope set from 06.1.1-T1, Unlisted English demo video showing the consent grant), write `docs/compliance/google-oauth-verification-<yyyy-mm-dd>.md`, file, add the calendar row (expected reply as a range/unknown), keep `saas_registration` **off** (O9). In parallel 13.5.1-T1 builds the pack (7 flows) used by the demo video and both audits.
3. **Gate `13-S-legal`** must be green before filing (page shows the published version, `GET /me/legal` returns `pendingAcceptance` for a non-owner, an undeployed publish fails closed with `legal_content_missing`).
4. **W4 — calendar and R2.** 13.5.2-T1 writes `docs/compliance-calendar.md` (all D15 rows incl. the ready-to-copy 13.4.2-T1 row) and starts the monthly log; the three alert rules go to 11.6.2-T1. Then 13.4.3-T1 files the TikTok audit with 07.4.1's UX list + the pack.
5. **Grant and flip (UC-2).** On grant: record the outcome + evidence, `nts-worker flags set saas_registration on`, update the calendar and the 08.3 runbook paragraph, and re-run the D6 equality check. Registration stays closed until then — the 100-user Testing cap and the warning screen are the reason (K17/F9).
6. **Denied / needs-info path.** Record the reason, re-file via `https://support.google.com/youtube/contact/yt_api_appeals` where applicable, keep the flag off, keep the calendar row's status honest. A TikTok "needs more info" gets the same treatment in the calendar.
7. **Steady state.** Monthly: append the SLA/DSAR report row (13.5.2-T1) from the 08.1 stack; weekly/monthly: 11.6.1-T1's restore drill row; every 6 months: the policy review row (which, if a text changes, forces a version bump through 13.2.2-T1 and a re-acceptance through 13.2.1-T2); on the lawyer memo (13.6.1-T1) landing, add one backlog row per required change. Gate `13-S-trust` (:325) and `13-S-R2` (:326) close this folder; the DoR review for Part B (§B11) follows once all four folders are written.
