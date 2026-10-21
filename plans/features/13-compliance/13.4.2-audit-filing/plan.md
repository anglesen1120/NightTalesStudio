# 13.4.2 — YouTube API Compliance Audit + quota extension filing

> Detailed plan for the P item `13.4.2` of `../../13-compliance.md` (:84-85, row `plans/13-compliance.md:151`). One task: **13.4.2-T1** (M, 1.0 d, BA · P · M1, `../README.md:82`).
> Contract: `../README.md` **D5** (:50) with **K3** (:25), **K6** (:28), **F5** (:38), sequencing and gate `13-P-ready` (:64), §8.1 (:82); high-level risks `plans/13-compliance.md:168` and `plans/06-youtube-posting.md:209`; the evidence pack is `../../06-youtube-posting/06.3.1-quota-audit/06.5.1-T1-audit-evidence-pack.md` (its §Notes:136: **"the pack must not grow into the filing document"**).
> Deliverable = documents, checklists and review steps. **No code, no route, no table, no column, no index, no migration, no CLI, no `tools/` script** is created by this task.
> Legend: ✅ (cite) · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].

## 1. Scope

**Story (13.4.2):** *"As the owner I want the audit passed so that uploads can be public and quota can grow"* — Given the 06.5.1 evidence pack, When filed via the audit form ✅ (G13), Then the answers match actual behaviour (consent, retention, deletion windows ✅) and the requested quota is justified from the owner's autopilot cadence and daily cap at the M1 filing (`plans/13-compliance.md:84-85`).

**In scope (13 README D5, :50).**

1. Re-run the 13-step behaviour walkthrough checklist as the filing's own first step and attach its verdicts (`../README.md:38` F5; step list `06.5.1-T1-audit-evidence-pack.md` §Design).
2. Draft the audit-form answers in `docs/compliance/youtube-audit-<yyyy-mm-dd>.md`: answers, quota justification from the owner's cadence and daily cap, links, pack index.
3. File through the form named in G13 (`https://support.google.com/youtube/contact/yt_api_form`, `plans/13-compliance.md:8`, 06 README:43).
4. Record the calendar entries **inline, inside this document's § 6** (M2 re-filing with beta data, `plans/13-compliance.md:85`; re-audit re-check — cadence ⚠️ [OPEN]) and the appeal path (`https://support.google.com/youtube/contact/yt_api_appeals`, `plans/06-youtube-posting.md:209`) — never a write to `docs/compliance-calendar.md`, which belonged to the dropped compliance-calendar task (phase S, DEC 10.14); § 6 is a ready-to-copy row for that task.
5. Enforce the repo-copy rule: **no secrets or personal data** in the repo copy (`../README.md:50`).
6. Record the follow-up log (Google questions, outcome) inside the same document.

**Out of scope.** The evidence pack and its read-only SQL (`06.5.1-T1`, deliverable `docs/compliance/audit-pack/<yyyy-mm-dd>/`, `06.5.1-T1-audit-evidence-pack.md:86-89`); the legal texts (13.1.1-T1 / 13.1.2-T1); the acceptance gate endpoint and dialog (13.2.1-T1); the deletion engine (13.3.1-T1); **the compliance calendar file `docs/compliance-calendar.md`** (13.5.2-T1, S, after Gate 2 — `plans/13-compliance.md:95-96`); the Google OAuth app verification (dropped with phase S, DEC 10.14) and the TikTok audit (13.4.3-T1, R2); flipping `YouTube:AuditStatus` (owner/ops, 06 README:126) and the code that consumes it (`06.2.4-T1`, 02.8.3-T3:63-68); any change to `IYouTubeClient` / `GoogleYouTubeClient` / `IOAuthClientResolver` / `FakeGoogleServer` / `platform_incidents`.

**Dependency.** Hard: `06.5.1-T1` (§8.1 `06-youtube-posting/README.md:217`, gated by **06-audit-ready**: *"evidence pack complete (06.5.1-T1) and 13 P slice done → 13.4.2-T1 files"*, `06-youtube-posting/README.md:187`). Blocks: nothing (`../README.md:82`); it is the last task of the P slice (`../README.md:64`) and one of the `13-P-ready` gate items ("filing done").
**Soft dependency:** the 13 P slice evidence (13.1.x texts, 13.2.1-T1 acceptance, 13.3.1-T1 deletion job) reaches this task only through the pack.

## 2. Confirmed facts

| Id | Fact | Cite |
|---|---|---|
| A1 | The filing = walkthrough checklist → answers in `docs/compliance/youtube-audit-<date>.md` (answers, quota justification from the owner's cadence and daily cap, links, pack index) → filing via the form → calendar entry (re-audit cadence ⚠️) → appeal path documented; no secrets or personal data in the repo copy | `../README.md:50` ✅ |
| A2 | The checklist maps **each III.x clause and each policy sentence** to a test or screenshot | `../README.md:38` ✅ |
| A3 | III.A.1 terms link + binding clause; III.A.2 privacy policy before features, "uses YouTube API Services", Google Privacy Policy link, revoke link; III.D.2 7/30 days; III.E.4 30-day refresh; user deletion ≤ 7 days; audit form URL | `../README.md:25`; 06 README:43 ✅ |
| A4 | The filing needs the 06.5.1-T1 pack; the quota asked is derived from the owner's cadence and daily cap at the M1 filing | `../README.md:28`; `plans/13-compliance.md:84-85` ✅ |
| A5 | `videos.insert`: **100 calls/day** in the Video Uploads quota bucket, 1 unit each; other methods share 10,000 units/day; daily quotas reset at midnight PT | 06 README:31 ✅ |
| A6 | Projects created after 2020-07-28: unverified projects' uploads are restricted to private **until audited** — the audit is what lifts the lock | 06 README:32; `plans/00-overview.md:95` ✅ |
| A7 | Quota extension is available **only** through the API Compliance Audit → file in M1 as soon as upload works and the pages are live (M1 phase 2, 11 CR-18: the policy URLs must be public) | `plans/06-youtube-posting.md:25` ✅ |
| A8 | Pre-audit mode: `AuditStatus ≠ approved` → upload `private`, **no** `publishAt`, run outcome `published_private_locked`; the pilot keeps the cadence while private (the audit needs real-use evidence) | 06 README:128 (D12); 02.8.3-T3:63-68, :70-72; `plans/00-overview.md:95,96` ✅ |
| A9 | `YouTubeOptions.AuditStatus ∈ pending\|approved\|rejected`, `Quota:UnitsPerDay` = 100, `ConsentTextVersion` = `pc.v1` | 06 README:126 ✅ |
| A10 | Cadence = `series.autopilot_settings.cadence` (`days_of_week`, `publish_time`, `time_zone`) with `daily_cap` 1..5 **per channel per local day**; `pre_audit_mode` ∈ `keep_cadence_private` \| `pause_after_n_private`; the series authorization names `{{daily_cap}}` and carries `{{pre_audit_note}}` while the audit is pending | 02.8.3 `plan.md:55-63, 88`; 02.8.3-T2:59, :63; 02.8.3-T2:69-71 ✅ |
| A11 | Audit status `pending → approved` changes the rendered authorization text (the pre-audit note disappears) → the covered-field diff **revokes** the authorization and the owner re-accepts | 02.8.3-T2:80, :170 ✅ |
| A12 | Pack layout `docs/compliance/audit-pack/<yyyy-mm-dd>/` with `index.md`, `01-walkthrough-checklist.md`, `02-policy-clause-map.md`, `03-retention-deletion.md`, `04-quota-justification.md`, `05-consent-model.md`, `06-artefacts.md`; clause map mandatory rows III.A.1, III.A.2, III.C.3, III.D.2, III.E.4, `proven`/`gap`; handoff to this task; **the pack must not grow into the filing document** | `06.5.1-T1-audit-evidence-pack.md:88, :95, :136` ✅ |
| A13 | Pack transport/verification inputs this task re-uses: `VP4` legal versions (`tools/ci/check-legal-versions.py`, owned by 13.2.1-T1, 13 CR-30), `VP6` quota numbers (`units_limit` = `YouTube:Quota:UnitsPerDay`), `VP7` redaction grep, `VP5` retention duties, `VP1` second-person reproduction | `06.5.1-T1-audit-evidence-pack.md` §Tests ✅ |
| A14 | Legal texts are build-time repo files with `web/src/content/legal/manifest.json` as the single version source, rendered by 10.3.1-T1; the page version must equal `user_consents` | `../README.md:23` (K1); `plans/features/10-web-ui/README.md` D12 ✅ |
| A15 | Owner-only: the owner is the only user in P; the app runs *In production*, unverified; OAuth verification is a separate gate (S) and does **not** lift the private lock | `plans/00-overview.md:96`; `plans/06-youtube-posting.md:26,28` ✅ |
| A16 | Audit timing is unknown ⚠️; until approval the pilot stays private; if rejected → appeal, fix, re-file; M2 re-files with beta data (uploads/user/day × users) | `plans/00-overview.md:480`; `plans/06-youtube-posting.md:209`; `plans/13-compliance.md:85,168` ✅ |
| A17 | Secret name for the audited client is `nts/<env>/api/YOUTUBE_CLIENT_SECRET`; P settings are environment values | 06 README:102 (D5); C12 `plans/features/06-youtube-posting/README.md` §1 ✅ |

## 3. Domain and data

### 3.1 Schema / migration

**None.** No aggregate, table, column, index, constraint or migration is added, so the expand-only migration rule is trivially satisfied (there is no schema change to expand). The task's artifacts are two Markdown documents (one in the repo, one outside it) and one external submission.

Tables **read** (never written) — all owned elsewhere and reached only through the pack's documented read-only queries:

| Table | Owner | Why the filing touches it |
|---|---|---|
| `platform_quota_usage (platform, quota_day, units_used, units_limit)` | 06.3.1-T1 (D10) | quota justification + `units_limit = YouTube:Quota:UnitsPerDay` |
| `data_deletion_requests (…, scope, reason, requested_at, due_at, completed_at)` | 13.3.1-T1 (13 README:27) | 7/30-day duty answers |
| `platform_incidents (…, cleared_at, …)` | 06.2.3-T3 / 06.4.2-T2 (06 README:130, D14) | answers about a broken client holding publications |
| `publications (…, consent_at, consent_text_version, consent_source, veto_notice_sent_at, privacy, external_post_id, contains_synthetic_media)` | 06.2.1-T1 (D6) | consent-model and synthetic-media answers |
| `user_consents` | 09.3.1-T1 (13 README:24) | III.A.2 acceptance answer |
| `series_autopilot_authorizations (text_version, cadence, revoked_at)` | 02.8.3-T2 | III.C.3 authorization-version answer |

### 3.2 Repo artifact: `docs/compliance/youtube-audit-<yyyy-mm-dd>.md`

The date in the name is the **filing date** (ISO), fixed in step 1 (`../README.md:50`). Structure (each `##` section is mandatory; a re-file appends a `## 9. Revision <n> — <date>` section instead of rewriting history):

| § | Section | Content | Source |
|---|---|---|---|
| 0 | Header | filing date, production API project (the one being audited) + owner channel (redacted label + external-artefact id), pack id/date, form URL, owner sign-off line | A12, A15, `06.5.1-T1` §Design |
| 1 | Walkthrough result | table `\| Step \| Action \| Observable \| Evidence (artefact id / timestamp) \| Clause ids \| Verdict \| Verifier \|` — the 13 steps, re-verified at filing time | A2, A12 |
| 2 | Form answers | table `\| Form question (verbatim as presented) \| Answer \| Pack artefact / clause-row refs \| Redaction \|` | D5, A2 |
| 3 | Quota justification | three figures: the observed `units_used` peak (evidence, ≤ `units_limit`), the scheduled need under the per-channel `daily_cap` rule, and the requested extension (`ask ≥ scheduled need`, `ask ≥ observed`, and `> 100` for it to be a request at all — otherwise "no extension needed at M1"); the 100-call Video Uploads bucket, the separate 10,000-unit shared bucket note, ledger evidence, M2 scaling (uploads/user/day × users) | A4, A5, A10, A16 |
| 4 | Links | privacy policy URL, ToS URL (live at the filed versions), revoke link, Google Privacy Policy link, contact | A3, A14 |
| 5 | Pack index | the pack's `index.md` artefact list (A1–A16 ids) with the pack path/date | A12 |
| 6 | Calendar | M2 re-file (beta data), re-audit re-check date, review of the filed answers after any policy change | A16 |
| 7 | Appeal path | the appeal URL, the appeal triggers (rejection, silent rejection, quota denial), what is re-submitted | A16 |
| 8 | Follow-up log | date, Google question, answer given, doc/`02-policy-clause-map.md` row added, outcome | A12 |

### 3.3 External artifacts (recorded, **not** committed)

- The submitted form copy / confirmation (Google sends a confirmation; the audit ticket id) — stored with the owner's records, path + SHA-256 recorded in the pack's `06-artefacts.md` (`06.5.1-T1-audit-evidence-pack.md:89`).
- Raw screenshots/screencast of the 13 steps (already external per A12), plus any screenshot taken at filing time (a screen captured during filing goes through the same redaction rule).
- The Google support thread (questions, appeal).

### 3.4 Invariants (review gates inside the document)

| Id | Invariant | Check |
|---|---|---|
| I1 | Every mandatory clause row (III.A.1, III.A.2, III.C.3, III.D.2, III.E.4) is `proven`; a `gap` row **blocks** filing | VF2 |
| I2 | Every § 2 answer row references ≥ 1 pack artefact id or clause-map row; no answer is unauthorised prose | manual review |
| I3 | Every legal-text version quoted equals `web/src/content/legal/manifest.json` **and** `LegalDocuments:Current:*` at filing time | VF3 |
| I4 | § 3 separates observed usage (`units_used ≤ units_limit` = `YouTube:Quota:UnitsPerDay`, from `platform_quota_usage`) from the justified future request: the scheduled need is computed under the **per-channel** `daily_cap` rule (`02.8.3-cadence-publishing/plan.md:88`), the request satisfies `ask ≥ scheduled need` and `ask ≥ observed`, and it is a request only when `ask > 100` (otherwise the document states "no extension needed at M1" and keeps the limit); expressed in `videos.insert` calls/day with the 10,000-unit shared bucket stated separately; no fabricated volume numbers | VF4 |
| I5 | No client secret, `refresh_token`, `access_token`, session URI, cancel/consent token, `code`/`state`, cookie, e-mail, IP, channel name or video id in the repo document | VF5 |
| I6 | The walkthrough names the **production** API project and the owner's channel; staging-only evidence is labelled and cannot substitute | VF6 |
| I7 | The document never quotes a Google policy sentence it did not re-read on the filing date (policy texts change) | manual review, § 2 header line |

### 3.5 Performance

No runtime path exists, so batching/streaming/N+1 do not apply. The only volume-shaped content is the quota math (§ 3/I4) and the answer set, both trivially bounded; the pack supplies any read-only query that would otherwise be repeated (`06.5.1-T1` §"Performance and reuse constraints").

## 4. Use cases

| Id | Use case | Actor | Preconditions | Success |
|---|---|---|---|---|
| UC1 | Re-run and verify the walkthrough checklist against the pack | BA (owner) | pack complete, gate **06-audit-ready** (`06-youtube-posting/README.md:187`), prod API project + owner channel available | § 1 table filled for all 13 steps, every clause id mapped, no verdict `fail` (`I1`) |
| UC2 | Draft the answers | BA | UC1 done; form opened once to read its live question list | § 2 rows: question verbatim, answer, refs, redaction |
| UC3 | Justify the quota ask | BA | cadence and `daily_cap` read from the owner's series settings at filing time; ledger rows available | § 3: observed peak, scheduled need under the per-channel cap, and `ask ≥` both, `> 100` for a real extension (else "no extension at M1"), ledger evidence cited (`I4`) |
| UC4 | File | BA | UC1–UC3 complete, VF2–VF6 green, owner sign-off | submission accepted by the form; confirmation stored; § 8 opened with the follow-up date |
| UC5 | Record calendar + appeal | BA | UC4 done | § 6 entries with dates; § 7 appeal triggers + URL |
| UC6 | Redaction review | second person (a reviewer, not the author) | document drafted | VF5 clean; each redaction stated in § 2 |
| UC7 | Handle Google follow-up | BA | a question arrives | answer row added to § 2, the pack's `02-policy-clause-map.md` row referenced; if behaviour-backed evidence is missing → the pack is extended by `06.5.1-T1`, filing pauses |

**Validation rules (BA-level, no code).** V1 pack handed over with its index + clause map (`06.5.1-T1` acceptance); V2 no `gap`; V3 `tools/ci/check-legal-versions.py` green (owned by 13.2.1-T1, 13 CR-30); V4 walkthrough ran on the production project; V5 redaction clean; V6 the owner signs the header line before submitting.
**Authorization.** The audit is per producer account: only the owner files; no role model, no tenant filter, no endpoint — nothing to authorize in code. **Idempotency.** One document per filing date; a re-file appends a revision section (never edits historical answers in place); the submitted form cannot be recalled ("unsend"), so corrections are a new submission with the revision noted (§ 8).
**Error/verdict vocabulary.** The document uses three verdicts only — `proven`, `provisional` (staging-only evidence, explicitly labelled), `gap` (blocks). Google's own outcome words are quoted verbatim in § 8 when received; they are ⚠️ [OPEN] here (no published response vocabulary).

## 5. API and events

**This task adds no route, no DTO, no status code, no outbox event, no SignalR id, no config key and no audit action.** Explicitly: `YouTubeOptions`, `IOAuthClientResolver`/`ConfigOAuthClientResolver`, `IYouTubeClient`/`GoogleYouTubeClient`, `platform_incidents`, `IQuotaLedger` and `FakeGoogleServer` are named only as subjects of the evidence the pack cites; none of them changes (06 README:102, :126, :130).

Reads the document relies on (unchanged, owned elsewhere):

| Contract | Owner | Use |
|---|---|---|
| `GET /me/legal` → `{ documents: [{ document, currentVersion, acceptedVersion, acceptedAt }] }`; 409 `legal_acceptance_required`; `POST /me/legal/accept` 409 `legal_version_outdated` | 13.2.1-T1 (`../README.md:46` D3) | III.A.2 answer: acceptance recorded before the first connect |
| `GET /publishing/youtube/eta` → `{ available, nextSlotAt }` | 06.3.1-T1 (D10) | quota-wait answer (step 13) |
| `PublicationDto { …, status, privacy, publishAt, consentAt, consentSource, etag }`, ETag `"pu-<xmin>"` | 06 README D6/D7 | consent-model answer evidence |
| `data_deletion_requests`, `platform_incidents` rows | 13.3.1-T1 / 06.2.3-T3, 06.4.2-T2 | retention/deletion + incident answers |

**Handover boundary.** The pack hands over `index.md` + `02-policy-clause-map.md`; the filing names the pack id. The pack must not be extended into the filing document, and the filing must not duplicate the pack's clause map — it references rows (`06.5.1-T1-audit-evidence-pack.md:136`).

## 6. UI

No UI is built or changed; no copy key is handed over.

| Surface | Owner | How the filing uses it |
|---|---|---|
| `/vi|en/privacy`, `/vi|en/terms` legal pages (versioned from `manifest.json`) | 10.3.1-T1 (13 README:23 K1; 10 README D12) | quoted as the live URLs in § 4 and proven live in the pack (A10) |
| veto-window list + run log | 10.4.3-T2 | walkthrough steps 10–11 screens (`06.5.1-T1` A8) |
| channels page, publish form/consent/quota wait, status screen | 10.7.1-T1 / 10.7.2-T1 / 10.7.4-T1 | walkthrough steps 3–7 and 13 — **10 wave 2, not detailed yet** [OPEN]; the walkthrough captures whatever ships and cites the task ids |
| consent/authorization text `pc.v1` and `{{pre_audit_note}}` | 06.2.2-T1 / 02.8.3-T2 (`plan.md` D7; 02.8.3-T2:69-71) | quoted verbatim in § 2, never edited here |

## 7. Jobs and integrations

**No job, no scheduler registration, no Worker change, no `RecurringJobId`.** The task is manual.

Integrations, all external or read-only:

| # | Integration | Direction | Contract |
|---|---|---|---|
| 1 | YouTube API audit form (`https://support.google.com/youtube/contact/yt_api_form`, G13) | out (submit), in (questions/decision) | form fields are dynamic and not published in the plans → the § 2 header records the field set as presented at filing time [OPEN] |
| 2 | Evidence pack on disk (`docs/compliance/audit-pack/<yyyy-mm-dd>/`) | in | produced and verified by 06.5.1-T1; the filing cites artefact ids |
| 3 | Appeal channel (`https://support.google.com/youtube/contact/yt_api_appeals`) | out | recorded in § 7 with the trigger list |
| 4 | Read-only SQL / routes of the pack (A1–A16) | in | no new query is written by this task |

**Failure map.**

| Case | Behaviour |
|---|---|
| Audit not granted before M1 exit (unknown duration ⚠️) | the filing stays open in § 8; the pilot keeps running in pre-audit mode (`AuditStatus = pending`, uploads `private`, no `publishAt`, outcome `published_private_locked`) — public growth waits; this is the documented residual risk (`plans/06-youtube-posting.md:209`, 02.8.3-T3:63-68) |
| Audit rejected | § 7 = appeal (same day), § 8 records the reason; fixes go to the owning plan task; re-file as a revision; the channel stays private meanwhile (`plans/13-compliance.md:168`) |
| Quota extension denied or deferred | keep `YouTube:Quota:UnitsPerDay = 100` and the personal cap (1/day, `plans/00-overview.md:27`); re-request at M2 with beta data (`plans/13-compliance.md:85`) |
| The form asks something the pack does not answer | add the § 2 row + the clause-map row; if behaviour-backed evidence is missing, `06.5.1-T1` extends the pack first; **filing pauses** (mirrors `06.5.1-T1` §Failure handling) |
| A policy text changed between pack collection and filing | pack invalidated for that sentence; re-collect + re-verdict before submitting (`I7`) |
| Audit approved | the owner flips `AuditStatus approved` (06 README:126); the authorization text loses `{{pre_audit_note}}` → the covered-field diff **revokes** the series authorization and the owner re-accepts (02.8.3-T2:170) — recorded in § 6 as the follow-up action, not performed here |
| No answer after the expected window | § 6 calendar follow-up date; no SLA is published ⚠️, so the document records a check date rather than a deadline [OPEN] |
| Secrets or personal data found in the repo copy | rotate the affected credential (owner, per C12: `nts/<env>/api/YOUTUBE_CLIENT_SECRET`), redo the redaction pass, re-file with the corrected copy in the submission only |

## 8. Security and privacy

- **Secrets:** never read or written. The document may name the secret **name** `nts/<env>/api/YOUTUBE_CLIENT_SECRET` (A17) but never its value; tokens live encrypted (`access_token_enc`, `refresh_token_enc`) and are quoted only as row existence (`06.5.1-T1` A4/A5).
- **Forbidden-in-repo list** (checked by VF5, two scan targets): client secret (`GOCSPX…`), OAuth access tokens (`ya29.…`), refresh tokens (`1//…`) and `refresh_token`/`access_token` values, upload session URI, `code`/`state` **values including the `?code=`/`?state=` query strings**, `pc.v1` cancel tokens, cookies, bearer headers, e-mail addresses, **IPv4 and IPv6 addresses**, channel names/titles, **YouTube video ids** (caught through their publication forms `youtu.be/<id>`, `youtube.com/watch?v=<id>`, `studio.youtube.com/video/<id>`), raw API payloads. The check has two parts (§ 9): **A** — value-shaped patterns that must print nothing over `docs/compliance/youtube-audit-*.md` **and** `docs/compliance/audit-pack/`; **B** — field names (`refresh_token`, `access_token`, `session_uri`, `Bearer`) that the pack's retention/clause tables may legitimately name, so every hit is explained in the § 2 redaction column. Known noise in A: four-part version strings (`1.76.0.4262`); clock times (`12:30:45`) do **not** match (the IPv6 alternative needs ≥ 3 colon groups). The check is a review aid, not a proof.
- **Submission copy** (outside the repo) may hold the owner's contact e-mail and the OAuth **client id** (not a secret) — the minimum Google needs; it is stored with the owner's records and its SHA-256 + location is recorded in the pack's `06-artefacts.md`.
- **Tenant 404 / CSRF / idempotency / ETag:** not applicable — no endpoint, no state change, no cookie-authenticated surface; stated here so a reviewer does not look for them. Nothing in this task is added to the `[Audited]` action list (06 README:130 D13's list stays as-is) and nothing is logged by the product.
- **Privacy of the filing itself:** the repo document is the audit trail of the filing and contains no personal data; it is therefore not in scope of 13.3.1-T1's deletion engine (which deletes Authorized Data: tokens, channel metadata, `external_post_id`, `platform_payload`, and the research/assets blobs — 13 README:27 K5). The external copy follows the owner's own retention.
- **Least privilege:** the walkthrough uses the owner's own channel only; no test user, no second account, no real third-party data.

## 9. Test plan

No code exists, so **no xunit/Shouldly test, no `PostgresFixture`, no `FakeGoogleServer`, no `FakeClock`** is written by this task (those prove the behaviour the pack documents — 06 README §3 D19). Verification = reproducible runs written into the document, all re-verified **at filing time** (the pack's own VP1–VP8 were verified at pack time and may be stale).

| Name | Kind | Asserts | Command / evidence |
|---|---|---|---|
| VF1_PackHandoffComplete | manual | pack index + clause map received with the pack path/date; gate `06-audit-ready` stated | § 0 header + § 5 index |
| VF2_ClauseMapHasNoGap | manual | a `proven` row for III.A.1, III.A.2, III.C.3, III.D.2, III.E.4 | § 1/§ 5 reference the pack's `02-policy-clause-map.md` |
| VF3_LegalVersionsAtFiling | scripted | `python3 tools/ci/check-legal-versions.py` exit 0; quoted versions = `manifest.json` = `LegalDocuments:Current:*` (13 README:23, :24; script owned by 13.2.1-T1, 13 CR-30) | § 4 line per document with the version string |
| VF4_QuotaAskJustified | scripted read-only + manual | observed `units_used` ≤ `units_limit` (= `YouTube:Quota:UnitsPerDay`); the scheduled need recomputes from the settings snapshot under the per-channel `daily_cap` rule; `ask ≥ scheduled need` and `ask ≥ observed`; a request exists only when `ask > 100`, else the document states "no extension at M1"; the 10,000-unit shared bucket stated separately | § 3 with the pack's A1/A2 rows |
| VF5_RedactionClean | scripted (grep) | **part A** (value-shaped: `GOCSPX`, `ya29.`, `1//`, e-mail, IPv4, IPv6, video-id URL forms, `?code=`/`?state=`) prints nothing over `docs/compliance/youtube-audit-*.md` **and** `docs/compliance/audit-pack/`; **part B** (field names `refresh_token`/`access_token`/`session_uri`/`Bearer`) — every hit explained in the § 2 redaction column | command block below; A = exit 1 + no output |
| VF6_WalkthroughOnProduction | manual | 13 steps recorded with timestamps and per-step verdicts, naming the production API project + owner channel; staging-only rows labelled `provisional` | § 1 table |
| VF7_CalendarRowAndAppealRecorded | manual | § 6 states the filing date, the re-audit reminder and the ⚠️ cadence assumption **inside this document** (no write to `docs/compliance-calendar.md`, which belonged to the dropped compliance-calendar task (DEC 10.14)); § 6 also states the M2 re-file entry, ready to hand to 13.5.2-T1 as a row; § 7 has the appeal URL and triggers | § 6 + § 7 |
| VF8_AnswersTraceable | manual | every § 2 row has ≥ 1 pack artefact/clause reference; no row is unauthorised prose (`I2`) | § 2 table |

Redaction is verified twice: by the author before handing over and by the **second person** (UC6) — the same separation the pack uses (`06.5.1-T1` VP1/VP7).

**VF5 command (canonical — run at filing time, then again by the second person).**

```bash
# A — value-shaped: MUST print nothing (hard gate)
grep -rnE 'GOCSPX|ya29\.[A-Za-z0-9._-]{10,}|1//[0-9A-Za-z_-]{10,}|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}|([0-9]{1,3}\.){3}[0-9]{1,3}|([0-9a-fA-F]{1,4}:){3,}[0-9a-fA-F]{1,4}|::([0-9a-fA-F]{1,4}:)+[0-9a-fA-F]{1,4}|youtu\.be/|youtube\.com/watch[?]v=|studio\.youtube\.com/video/|[?&](code|state)=|v=[A-Za-z0-9_-]{11}' \
  --include='*.md' docs/compliance/youtube-audit-*.md docs/compliance/audit-pack/

# B — field names the retention/clause tables may legitimately name: every hit explained in § 2
grep -rnE 'refresh_token|access_token|session_uri|Bearer ' \
  --include='*.md' docs/compliance/youtube-audit-*.md docs/compliance/audit-pack/
```

Part A: exit 1 with no output = clean. Any A hit is triaged by hand: a hit inside a documented redaction instruction is a false positive; a real hit **blocks the filing** — rotate the affected credential, redact, re-run VF5, re-file (plan § 7). Part B hits are listed row by row with their explanation. The IPv6 alternative requires ≥ 3 colon groups, so clock times are not hits; four-part version strings are the expected A noise (§ 8).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 13.4.2-T1 | YouTube API Compliance Audit + quota extension filing | M (1.0) | 06.5.1-T1 | — |

Equal to the canonical row `13.4.2-T1 | M (1.0) | BA | P · M1 | 06.5.1-T1 | —` (`../README.md:82`). Re-estimate: **M (1.0), unchanged** from the high-level estimate (`plans/13-compliance.md:151`; `../README.md` §9 "unchanged"); it stays inside the 13 P slice's 5.0 d (`../README.md:88,90`) and the plan-13 total as re-estimated in `../README.md` §9.

## 11. Rollout

1. **Entry gate `06-audit-ready`** (`06-youtube-posting/README.md:187`): the pack is complete (`docs/compliance/audit-pack/<date>/`), the 13 P slice is live (legal pages at the manifest versions, owner acceptance recorded before connect, deletion job live on staging with a `channel` request completed before `due_at` — `../README.md:64`).
2. Run the walkthrough (UC1) and freeze the filing date → create `docs/compliance/youtube-audit-<date>.md`.
3. Draft answers (UC2), quota justification (UC3), run VF1–VF6; second-person redaction review (UC6).
4. Owner signs § 0, submits (UC4); store the confirmation externally; open § 8 with the follow-up date.
5. Record § 6 calendar entries **inline** (never `docs/compliance-calendar.md` — the dropped compliance-calendar task's, DEC 10.14) and § 7 appeal path (UC5) → the `13-P-ready` item **"filing done"** is satisfied (`../README.md:64`).
6. **After approval (not part of this task):** the owner flips `YouTube:AuditStatus` to `approved` (06 README:126); the series authorization text loses `{{pre_audit_note}}` → the authorization is revoked and the owner re-accepts before the first public upload (02.8.3-T2:69-71, :170); the Channel phase can start (`plans/00-overview.md:67`).
7. **Rollback:** none needed (documents only). A defective filing is corrected by a revision + re-submission (§ 8); the pre-audit mode remains the safe state while the audit is open.
8. **Open items:** re-audit cadence ⚠️ (`../README.md:50`) — a re-check date is recorded, not an invented interval; the audit form's live field set [OPEN]; 10 wave-2 screens [OPEN] (`06.5.1-T1` §Inputs); M2 re-file scope with beta data [OPEN] (`plans/13-compliance.md:85`).
