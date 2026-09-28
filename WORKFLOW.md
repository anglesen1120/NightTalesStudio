# Night Tales Studio — Project Workflow Overview

> One-page map of how the project works: phases, system, content pipeline, background jobs, compliance flow and the engineering workflow.
> This file **summarises**; it does not decide. Sources of truth: `DECISIONS.md` (product decisions), `plans/00-overview.md` (shared contracts, estimates), `plans/01`–`13` (high-level plans), `plans/features/` (detailed feature plans). If this file disagrees with them, they win — fix this file.
> Legend: ✅ verified from an official source · ⚠️ unverified · [DECISION] · [OPEN] needs the owner.
> Full diagram set (19 diagrams, with SVG exports in `docs/diagrams/`): `DIAGRAMS.md`.

---

## 1. The product in one paragraph

Night Tales Studio turns ghost, spiritual and true-crime story ideas into faceless narrated videos in Vietnamese and English and publishes them to YouTube (TikTok later). The owner first runs **their own channel fully automatically** (personal phase, P). A public SaaS for other creators (S) is built only if the channel proves the idea (Gate 2).

| Layer | Choice |
|---|---|
| Web | Angular 22, Clean Architecture per feature, vi + en |
| Backend | **C#** on ASP.NET Core / **.NET 10 LTS** (latest stable 10.0.12 ✅), Clean Architecture + CQRS, PostgreSQL |
| Background jobs | **C#** in `NightTales.Worker` (Hangfire on PostgreSQL ⚠️ license check) — no Go, no Python jobs |
| Render node (GPU) | MPT + VieNeu TTS + VieNeu proxy + faster-whisper — Python only because these tools are Python; no business logic |
| AI | OpenAI (only paid provider): story text from the backend, images from MPT on the node |

Details: `plans/00-overview.md` §5.1, `DECISIONS.md` §10.13.

## 2. Phases and gates

```mermaid
flowchart LR
  M0["M0 Trial on the Mac (01-05)"] -->|"Gate 1: quality + cost OK"| M1["M1 Personal studio (P epics)"]
  M1 --> AUD["YouTube audit filed (M1)"]
  AUD -->|"audit approved (unknown wait)"| CH["Channel phase: autopilot, at least 12 weeks"]
  CH -->|"Gate 2: SaaS go/no-go"| M2["M2 Closed beta (S epics)"]
  M2 --> M3["M3 Public launch"]
  M3 --> R2["R2 TikTok"]
```

| Step | What happens | Exit / gate |
|---|---|---|
| **M0 Trial** | Prove the pipeline locally; measure time and cost per video; 05.4 worker-contract spike | **Gate 1:** quality acceptable and cost per video known, else stop or change approach |
| **M1 Personal studio** | Backend + web end to end, one owner account with MFA, autopilot, YouTube API publishing (private until audit), privacy/terms pages, deletion jobs | Autopilot publishes on its cadence with no manual step (true-crime after approval); **YouTube audit filed** |
| **Channel phase** | Run the flagship channel on autopilot ≥ 12 weeks after the audit allows public uploads; weekly KPI sheet | **Gate 2 [OPEN]:** owner decides from channel results, cost per video, time per episode |
| **M2 Closed beta** | Sign-up, account security, admin, credits and payments, multi-user publishing extensions, compliance, lawyer review | 10 beta users; OAuth verification filed; audit extended for SaaS |
| **M3 Launch** | Public sign-up once audit + quota extension are approved | Load test, runbooks |
| **R2** | TikTok publishing (needs a TikTok audit; an own-accounts tool is not an accepted use ✅) | TikTok audit approved |

Estimates (buffered person-days, one developer): **P 236.7 (≈ 11.8 months)**, **S 105.0 (≈ 5.3 months)**, total 341.7 (10 CR-31, 10 CR-22, 13 CR-9, 09 W3 CR-6, 10 CR-39/40, 11 CR-15) — `plans/00-overview.md` §4.1, §13. External waits (audits, lawyer, payment onboarding) are not included.

## 3. System map

```mermaid
flowchart LR
  B["Angular SPA + SSR legal/landing pages"] -->|"HTTPS REST + SignalR"| API["NightTales.Api (C#)"]
  API --> PG[("PostgreSQL")]
  W["NightTales.Worker (C#, Hangfire)"] --> PG
  W -.->|"LISTEN/NOTIFY"| API
  W --> OAI["OpenAI text API"]
  W -->|"private network, Worker pulls"| AD["mpt-adapter on GPU node"]
  AD --> MPT["MPT (Python)"] --> VP["VieNeu proxy"] --> VN["VieNeu TTS"]
  MPT -->|"egress allow-list"| OAI2["OpenAI image API"]
  W --> S3[("S3-compatible storage")]
  W --> YT["YouTube Data API"]
  W -. "R2" .-> TT["TikTok Content Posting API"]
  FT["fetcher container (fetch queue)"] -->|"narrow internal API"| API
  FT --> WEB(("Allow-listed public pages"))
  PAY["Payment provider (S)"] -->|"POST /billing/webhooks/{provider}"| API
```

Rules that shape the map [DECISION]:
- Render nodes are never reachable from the internet; only the Worker calls `mpt-adapter`, and nodes never open connections into the app network (11.3.2).
- The `fetcher` has internet-only egress, no database credentials and no route to app or render networks (02 §5.7, 11.2.2).
- Official APIs only. No browser automation on YouTube or TikTok (YouTube Terms item 3 forbids automated access ✅). The jev-ultrafast browser agent is used only for research on allow-listed sites that permit it (02.7).

## 4. Content pipeline (autopilot, personal phase)

```mermaid
flowchart TD
  A["AutopilotPlanJob: pick idea from series bible (similarity + variety guard)"] --> T{"Topic?"}
  T -->|"ghost / spiritual"| G["GenerateStoryJob: write + self-review + max 1 revision"]
  T -->|"true_crime"| R["Research: FindSourcesJob + FetchSourceJob, fact sheet draft"]
  R --> OW1["Owner approves fact sheet"] --> G2["GenerateStoryJob"] --> OW2["Owner approves story"]
  G --> SG{"Story gates pass? (score, moderation, real-person check, similarity, length)"}
  SG -->|"no"| RV["Owner review (story_review)"]
  SG -->|"yes"| RN["SubmitRenderJob / PollRenderJob / IngestRenderOutputJob"]
  OW2 --> RN
  RV --> RN
  RN --> VG{"Video quality checks pass? (duration, loudness, subtitle sync)"}
  VG -->|"no"| VR["Owner review (video_review)"]
  VG -->|"yes"| PUB["Publication created: awaiting_consent"]
  VR --> PUB
  PUB --> VW["Veto window (default 12 h): in-app + e-mail preview, Edit / Cancel / Approve now"]
  VW -->|"no cancel"| CS["AutopilotConsentJob writes per-video consent (consent_source = autopilot)"]
  VW -->|"edit or Approve now"| CU["User consent (consent_source = user)"]
  CS --> UP["PublishYouTubeJob: private + publishAt, containsSyntheticMedia = true"]
  CU --> UP
  UP --> PR["PollYouTubeProcessingJob, then PublicationGoLiveJob"]
```

Guardrails (02.8, 06.2.2, `DECISIONS.md` §10.12):
- **Fiction is auto-approved by gates; true-crime always waits for the owner** (fact sheet and story). A series' `topic` is locked while autopilot is on.
- **Daily cap:** 1 video per day per channel by default. A **variety guard** rotates settings, sub-genres, openings and scene styles, which limits the mass-produced content risk. The owner carries the monetization risk.
- **Auto-pause** stops new idea, story and render work, then e-mails the owner. Triggers: two gate failures in a row, spend cap reached, channel connection not active, OAuth client incident, quota or upload error, video rejected, veto e-mail not delivered.
- **Pre-audit mode:** until the YouTube audit passes, uploads stay private (default: keep the cadence; the video is marked `published_private_locked`).
- **Takedown:** a flagged or reported published video pauses the series. The owner makes it private or removes it in YouTube Studio within 24 h (08.4.4).

## 5. State machines (summary of `plans/00-overview.md` §7)

**Episode**

```mermaid
stateDiagram-v2
  [*] --> draft
  draft --> story_generating: generate (true_crime needs approved fact sheet)
  story_generating --> story_review
  story_generating --> draft: failed / cancelled / paused over 12 h
  story_review --> story_generating: regenerate
  story_review --> story_approved: approve (owner or autopilot gate)
  story_approved --> story_review: reopen
  story_approved --> rendering
  rendering --> story_approved: render failed / cancelled
  rendering --> video_review
  video_review --> rendering: re-render
  video_review --> story_review: reject video
  video_review --> ready: approve (owner or quality gate)
  ready --> scheduled: publication consented
  scheduled --> published
  published --> [*]
  state "any non-terminal state" as ANY
  ANY --> failed: unrecoverable
  ANY --> cancelled: user deletes
  failed --> [*]
  cancelled --> [*]
```

**Publication**

```mermaid
stateDiagram-v2
  [*] --> awaiting_consent
  awaiting_consent --> scheduled: consent_at recorded (user or AutopilotConsentJob)
  awaiting_consent --> cancelled: owner cancels in veto window
  scheduled --> uploading
  scheduled --> cancelled: cancel before upload
  uploading --> processing
  uploading --> scheduled: quota exceeded (retry next PT day)
  processing --> published
  processing --> failed
  uploading --> failed
  published --> [*]
```

A publication **never** leaves `awaiting_consent` without a consent record for that video. The upload job refuses anything without `consent_at` (defence in depth).

## 6. Background jobs (all C#, `NightTales.Worker`)

| Area | Jobs | Trigger |
|---|---|---|
| Messaging | `OutboxDispatcher` (Worker `BackgroundService`, not a Hangfire job), `SendEmailJob` | every 5 s / outbox events |
| Autopilot | `AutopilotPlanJob`, `AutopilotConsentJob` | series cadence / every 1 min |
| Story | `GenerateStoryJob` | outbox `StoryGenerationRequested` |
| Research (fetch queue, `fetcher` container) | `FindSourcesJob`, `FetchSourceJob` | user or autopilot request |
| Render | `SubmitRenderJob`, `PollRenderJob`, `IngestRenderOutputJob` | outbox `RenderRequested` / every 10 s while active |
| YouTube | `PublishYouTubeJob`, `PollYouTubeProcessingJob`, `PublicationGoLiveJob`, `TokenHealthJob` | consent / after upload / publishAt / daily |
| Data duties | `DataDeletionJob` (7- and 30-day YouTube duties ✅), `RetentionSweepJob` | hourly / daily |
| Settings (S) | `SettingChangeApplyJob`, `OwnerActionApplyJob`, `SettingSecretPurgeJob` | every 1 min / daily |
| Billing (S) | `CreditReconciliationJob`, `PaymentStatusSyncJob`, `CreditExpiryJob`, `LowBalanceNotifierJob`, `FundingRunwayJob`, `PaymentAnomalyJob`, `CreditsMintedDigestJob` | see 12 §5.1 |
| TikTok (R2) | `PublishTikTokJob`, `PollTikTokStatusJob` | 07 |

Full table with retry and idempotency rules: `plans/09-backend-api.md` §5.3. In the personal phase, credit calls go through an `ICreditLedger` port with a metering-only implementation; the 12 ledger replaces it at M2.

## 7. YouTube compliance flow

```mermaid
flowchart LR
  L["Privacy policy + terms live and accepted (13.1.1-T1, 13.1.2-T1, 13.2.1-T1, 10.3.1-T1)"] --> C["Connect channel (OAuth app In production, unverified)"]
  D["Deletion jobs live: 7 / 30 days (13.3.1-T1, 06.4.3-T1)"] --> E
  C --> U["Upload works (private)"] --> E["Evidence pack on the production project (06.5.1)"]
  E --> F["File YouTube API audit (13.4.2)"]
  F -->|"approved"| P["Public autopilot"]
  F -->|"rejected"| AP["Appeal, fix, re-file (channel stays private meanwhile)"]
```

Key facts (`DECISIONS.md` §5.5, §10.12):
- Uploads from unaudited API projects created after 28 July 2020 are **private only** ✅. Only the **API compliance audit** lifts this.
- **OAuth verification is a separate gate.** It controls the unverified-app screen and the 100-user cap; it is needed only for the SaaS. The owner's app runs "In production" without verification, because Testing status expires tokens after 7 days ✅.
- The client must have its own terms and privacy policy (Developer Policies III.A ✅). Users must keep "final control over the data that will be published" (III.C.3 ✅); the veto window supports this.
- [OPEN] If the audit rejects series-level authorization, the owner decides before filing: one-tap "Approve now" per video, or stay private-only.

## 8. Engineering workflow

### 8.1 From plan to code

```mermaid
flowchart LR
  H["1. High-level plans 00-13"] --> RV["2. Review the sections a feature touches"]
  RV --> FP["3. Detailed feature plan in plans/features/"]
  FP --> DR["4. Feature plan review (Definition of Ready)"]
  DR --> IM["5. Implementation"]
  IM --> DD["Definition of Done + feature plan updated"]
```

- **No feature is built from a high-level plan alone.** Template and process: `plans/00-overview.md` §10.1 and `plans/features/README.md`.
- Security review is required for auth, admin, billing and data features.
- **Contract changes go to `plans/00-overview.md` first**: domain names, states, routes, tables. Product decisions go to `DECISIONS.md`.
- **Definition of Ready:** user value, Given/When/Then acceptance, dependencies known, estimate ≤ XL, reviewed feature plan, and for web/UI work an approved UI design linked.
- **Design first (owner rule, 2026-09-25):** every web/UI task or feature has an approved UI design before implementation starts — wireframes + hi-fi mockups (states loading/empty/error/success, breakpoints 360/768/1280, vi + en text, dark/light, focus order and a11y notes, built from the 10 D11 design-system tokens/components), linked from the feature `plan.md` §6 UI and approved by the owner. Each UI feature folder carries a design task `<story>-T0 — UI design` (Area `UX`) that every implementation task in the folder depends on.
- **Per-task QA and review before push (owner rule, 2026-09-25):** every task, one at a time, ends with (1) a separate QA agent that re-reads the plan, feature `plan.md` and task file and runs the acceptance checks plus negative/edge cases, then (2) a code review, (3) a performance review and (4) a security review by separate reviewer agents; QA runs first, never in parallel with the reviews (the three reviews may run in parallel; their fixes are re-tested). Findings are fixed and re-tested and both passes are recorded in `STATUS.md` **before** the commit is pushed. A task is not `done` until all four are closed.
- **Definition of Done:** code reviewed, **a separate QA agent (not the author) re-read the plan, feature and task files and ran every acceptance check plus negative and edge cases against the implementation, reporting pass/fail with command output** (owner rule 2026-09-25, `AGENTS.md` Testing & QA), CI green, unit tests for domain rules and use cases, integration test per endpoint (real PostgreSQL 17, `PostgresFixture`), strings in vi + en, logs and metrics for new jobs, OpenAPI/runbook updated.

### 8.2 Branch → production

```mermaid
flowchart LR
  PRB["Pull request"] --> CI["CI: backend build/test, web lint/test/build, e2e with mock-mpt"]
  CI --> MN["Merge to main"] --> IMG["Images tagged with git SHA + digest (signing and SBOM in S)"]
  IMG --> MIG["EF migration bundle (expand, then contract)"] --> STG["Deploy to staging (automatic)"]
  STG --> PRD["Deploy to production (manual approval)"]
  MN --> NB["node.yml: node bundle + SHA256SUMS"] --> ND["nts-node deploy (operator, VPN): drain, install, smoke, activate"]
```

Source: `plans/11-worker-deployment.md` 11.5.
- CI is GitHub Actions (owner decision 2026-09-24, `plans/features/09-backend-api/README.md` O2).
- Deploys authenticate with OIDC. Third-party actions are pinned by SHA.
- Runtime secrets come from the secret manager, never from CI.
- Local development is native on macOS, no Docker (owner 2026-09-28, `plans/00-overview.md` §5.1): Homebrew PostgreSQL 17, SeaweedFS, Mailpit, `dotnet run` with **mock-mpt**, so no GPU is needed; Docker only builds and smoke-tests the deploy images.
- GPU nodes are updated from a CI-built, hashed bundle by the operator (`nts-node deploy`); CI has no access to the render network (11 CR-9).

### 8.3 Conventions

- **IDs:** story `<plan>.<epic>.<story>` (for example `06.2.2`); task `<story>-T<n>`.
- **Sizes:** XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 person-days, plus a 20 % buffer. Each plan's §11 epic rows equal the sum of its §6 tasks.
- **Phase tag:** every task is P or S (the S list is in `plans/00-overview.md` §4.1).
- **Evidence:** ✅ only with an opened official source; otherwise ⚠️, [INFERENCE] or [OPEN]. Never invent facts.
- **Licences:** commercial SaaS use required (no AGPL, RPL or non-commercial). OpenAI is the only paid provider.
- **Languages:** plan files are English; the UI is vi + en.

## 9. Open decisions (owner)

| # | Decision | Needed by |
|---|---|---|
| 1 | Audit fallback: one-tap approval per video vs private-only | Before filing 13.4.2 |
| 2 | Gate 2 criteria (subscribers, watch hours, monetization, cost per video) | Before the Channel phase |
| 3 | Hosting provider/region for API and GPU node | M0 → M1 (11.2.1) |
| 4 | ✅ Decided 2026-09-24: Hangfire LGPL-3.0 accepted, unmodified NuGet packages (09 README O1) | — |
| 5 | Payment provider; lawyer (credit expiry, refunds); accountant (VAT e-invoices) | Gate 2 |
| 6 | SaaS review rule for other users (checkpoints vs autopilot) | Gate 2 |

## 10. Where to find things

| Need | File |
|---|---|
| Why a decision was made, verified facts | `DECISIONS.md` |
| Contracts: tables, states, routes, estimates, phases | `plans/00-overview.md` |
| Environment and M0 trial | `plans/01`–`05` |
| Story writing, research, autopilot | `plans/02-story-writing.md` (Epics 02.5, 02.7, 02.8) |
| YouTube publishing and consent | `plans/06-youtube-posting.md` |
| TikTok (R2) | `plans/07-tiktok-posting.md` |
| Channel operation, runbooks | `plans/08-90-day-operations.md` |
| Backend skeleton, jobs, security, admin | `plans/09-backend-api.md` |
| Screens | `plans/10-web-ui.md` |
| GPU node, environments, CI/CD | `plans/11-worker-deployment.md` |
| Credits and payments (S) | `plans/12-billing-credits.md` |
| Legal pages, deletion, audits | `plans/13-compliance.md` |
| Detailed feature plans | `plans/features/` |
| Diagrams (architecture, flows, states, data model) | `DIAGRAMS.md`, `docs/diagrams/*.svg` |
