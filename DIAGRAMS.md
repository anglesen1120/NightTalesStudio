# Night Tales Studio — Diagrams

> Visual companion to `WORKFLOW.md`. Every diagram summarises `DECISIONS.md` and `plans/00`–`13`; if one disagrees with them, the plans win — fix the diagram.
> Rendered copies: `docs/diagrams/*.svg` (regenerate after editing a block here).
> Phase tags: there is **one phase, P** (the owner's own channels and autopilot); phase S was dropped and plan 12 deleted by DEC 10.14.

Contents:
1. System context
2. Deployment and network zones
3. Backend Clean Architecture (C#, .NET 10)
4. Command flow: CQRS + outbox + real time
5. Angular feature architecture
6. Autopilot end to end (P)
7. Autopilot consent and veto window (P)
8. Render orchestration
9. Episode state machine
10. Publication state machine
11. RenderJob state machine
12. Core data model (P)
13. YouTube data deletion duties (P)
14. YouTube compliance path (P)
15. Credits, payments and admin settings — removed by DEC 10.14 (kept for history)
16. Milestones and the personal path
17. Planning process
18. CI/CD

---

## 1. System context

```mermaid
flowchart LR
  OWNER(["Owner (the only account, P)"]) -->|"browser, vi + en"| NTS["Night Tales Studio"]
  NTS -->|"story text"| OAI["OpenAI API (text project)"]
  NTS -->|"render requests"| GPU["GPU render node: MPT + VieNeu + whisper"]
  GPU -->|"images"| OAI2["OpenAI API (image project)"]
  NTS -->|"official Data API: upload, status"| YT["YouTube"]
  NTS -->|"official Content Posting API: direct post, status"| TT["TikTok"]
  NTS -->|"fetch allow-listed pages"| WEB(("Public research sites"))
  NTS -->|"e-mail: veto notices, alerts"| MAIL["SMTP provider"]
```

## 2. Deployment and network zones

```mermaid
flowchart TB
  subgraph INTERNET["Internet"]
    BR["Browser"]
    YT["YouTube Data API"]
    OAI["OpenAI API"]
    SITES(("Allow-listed sites"))
  end
  subgraph APP["App network"]
    LB["Load balancer / TLS"] --> API["NightTales.Api x2 (C#)"]
    W["NightTales.Worker (C#, Hangfire)"]
    PG[("PostgreSQL: data, Hangfire, outbox")]
    S3[("Object storage: nt-media, nt-public")]
    API --- PG
    W --- PG
    W --- S3
  end
  subgraph FETCH["fetcher subnet (internet-only egress, no DB credentials)"]
    FT["fetcher container: fetch queue"]
  end
  subgraph RENDER["Render network (no inbound from internet)"]
    AD["mpt-adapter"] --> MPT["MPT"] --> VP["VieNeu proxy"] --> VN["VieNeu TTS"]
    MPT --> WH["faster-whisper"]
  end
  BR --> LB
  W -->|"Worker-initiated only, adapter port"| AD
  W --> OAI
  W --> YT
  MPT -->|"egress allow-list"| OAI
  FT --> SITES
  FT -->|"narrow internal API"| API
```

Rules: render nodes never open connections into the app network; the fetcher cannot reach the app or render networks, nor the metadata endpoint (`plans/00-overview.md` §5.2, `plans/11-worker-deployment.md` 11.2.2, 11.3.2).

## 3. Backend Clean Architecture (C#, .NET 10)

```mermaid
flowchart TB
  subgraph PRES["Presentation / composition roots"]
    API["NightTales.Api: Minimal API endpoint groups, auth, SignalR hub, ProblemDetails"]
    WK["NightTales.Worker: Hangfire server, recurring jobs, outbox dispatcher"]
  end
  INF["NightTales.Infrastructure: EF Core (write + read contexts), outbox, OpenAI, mpt-adapter client, S3, YouTube, TikTok, token protection, Hangfire adapters"]
  APPL["NightTales.Application: CQRS commands/queries, validators, pipeline behaviors, ports (IAppDbContext, IReadDb, IStoryWriter, IRenderClient, IYouTubeClient, IJobScheduler, ICreditLedger ...)"]
  DOM["NightTales.Domain: aggregates, value objects, state machines, domain events, errors (pure C#)"]
  API --> APPL
  WK --> APPL
  INF --> APPL
  APPL --> DOM
  API -. "DI wiring only" .-> INF
  WK -. "DI wiring only" .-> INF
```

The dependency rule (`Domain` ← `Application` ← outer layers) is enforced by architecture tests. Pipeline order: Logging → Authorization → Validation → Idempotency → UnitOfWork (commands only).

## 4. Command flow: CQRS + outbox + real time

```mermaid
sequenceDiagram
  autonumber
  participant UI as Angular
  participant API as Api endpoint
  participant MED as Mediator pipeline
  participant H as Command handler
  participant AGG as Aggregate
  participant DB as PostgreSQL
  participant WK as Worker
  participant HUB as SignalR (every Api)
  UI->>API: POST /episodes/{id}/story/generate (Idempotency-Key)
  API->>MED: sender.Send(command)
  MED->>MED: log, authorize, validate, idempotency
  MED->>H: handle
  H->>AGG: episode.RequestStoryGeneration()
  AGG-->>H: domain event StoryGenerationRequested
  H->>DB: save aggregate + outbox row (one transaction)
  API-->>UI: 202 Accepted
  WK->>DB: OutboxDispatcher claims outbox rows (every 5 s, SKIP LOCKED)
  WK->>WK: enqueue GenerateStoryJob
  WK->>DB: result saved + NOTIFY
  DB-->>HUB: LISTEN receives notification
  HUB-->>UI: EpisodeUpdated
```

## 5. Angular feature architecture

```mermaid
flowchart LR
  subgraph FEAT["features/<name>/ (one per feature: episodes, series, publish, channels, ...)"]
    F["feature/: routed pages"] --> U["ui/: presentational components"]
    F --> A["application/: signal stores + use-case methods"]
    A --> D["domain/: models, pure rules, repository ports"]
    DA["data-access/: HTTP repositories, DTO mappers, realtime adapter"] -. "implements ports" .-> D
  end
  DA --> GEN["shared/api: OpenAPI-generated client"]
  A --> CORE["core/: auth, http interceptors, realtime, i18n, shell"]
  F --> SH["shared/ui + shared/util"]
```

Features import each other only through `index.ts`; ESLint boundary rules enforce the layers (`plans/00-overview.md` §5.3).

## 6. Autopilot end to end (P)

```mermaid
sequenceDiagram
  autonumber
  participant SCH as AutopilotPlanJob
  participant OAI as OpenAI
  participant GS as GenerateStoryJob
  participant GATE as Gates (02.8.2)
  participant R as Render jobs
  participant GPU as mpt-adapter / GPU
  participant PUB as Publication + consent
  participant OWN as Owner
  participant YT as YouTube
  SCH->>OAI: propose ideas from series bible
  SCH->>SCH: similarity + variety guard, pick idea, create episode
  SCH->>GS: StoryGenerationRequested
  GS->>OAI: write, self-review, max 1 revision
  GS->>GATE: story gates (score, moderation, real-person, similarity, length)
  alt true_crime or a gate fails
    GATE->>OWN: wait for owner review
    OWN-->>GATE: approve
  end
  GATE->>R: RenderRequested
  R->>GPU: submit, poll every 10 s, ingest
  R->>GATE: video quality checks
  GATE->>PUB: create publication (awaiting_consent)
  PUB->>OWN: veto notice (in-app + e-mail)
  PUB->>PUB: veto window ends, consent written
  PUB->>YT: PublishYouTubeJob (private + publishAt)
  YT-->>PUB: processed, goes live at publishAt (after audit)
```

## 7. Autopilot consent and veto window (P)

```mermaid
sequenceDiagram
  autonumber
  participant OWN as Owner
  participant API as Api
  participant DB as PostgreSQL
  participant AC as AutopilotConsentJob
  participant MAIL as E-mail (outbox)
  participant PJ as PublishYouTubeJob
  OWN->>API: enable autopilot, accept authorization text (version)
  API->>DB: series_autopilot_authorizations row
  Note over DB: later, for each episode that passed its gates
  DB->>MAIL: veto notice: preview, Edit / Cancel / Approve now, signed cancel link
  MAIL-->>DB: veto_notice_sent_at
  alt owner cancels
    OWN->>API: DELETE /publications/{id} or cancel-by-link
    API->>DB: cancelled
  else owner edits or taps Approve now
    OWN->>API: PATCH (re-validate + moderation) or POST consent
    API->>DB: consent_source = user
  else window ends silently
    AC->>DB: notice sent and authorization active?
    AC->>DB: consent_at, text version, consent_source = autopilot, authorization id
  end
  PJ->>DB: re-check consent + connection
  PJ->>PJ: refuse and fail loudly if consent_at is missing
```

If the notice was not delivered, or the authorization was revoked, no consent is written: the series pauses and the publication waits (`plans/06-youtube-posting.md` 06.2.2).

## 8. Render orchestration

```mermaid
sequenceDiagram
  autonumber
  participant OB as Outbox
  participant SR as SubmitRenderJob
  participant REG as worker_nodes
  participant AD as mpt-adapter
  participant PR as PollRenderJob
  participant IN as IngestRenderOutputJob
  participant S3 as Object storage
  OB->>SR: RenderRequested
  SR->>REG: pick active node with most free slots
  SR->>AD: submit render-submit.v1 over pinned HTTPS (approved story text, settings)
  AD-->>SR: adapter_task_id
  loop every 10 s, max 60 min
    PR->>AD: status (state, stage, percent)
  end
  AD->>AD: node render check + loudness normalisation (render-check.v1)
  PR->>IN: succeeded (with check report)
  IN->>AD: download video + subtitles by task id (no URLs from responses)
  IN->>IN: streamed sha256 vs report, size/type caps, quality policy (render-quality.v1)
  IN->>S3: store video, subtitle, thumbnail
  IN->>AD: cleanup task
```

The node measures and normalises the output before `succeeded`; the Worker parses no media (05 CR-11; `features/05-video-assembly-subtitles/README.md` D9, D15).

## 9. Episode state machine

```mermaid
stateDiagram-v2
  [*] --> draft
  draft --> story_generating: generate (true_crime needs approved fact sheet)
  story_generating --> story_review: story ready
  story_generating --> draft: failed / cancelled / paused over 12 h
  story_review --> story_generating: regenerate
  story_review --> story_approved: approve (owner or autopilot gate)
  story_approved --> story_review: reopen
  story_approved --> rendering: render
  rendering --> story_approved: render failed / cancelled
  rendering --> video_review: rendered
  video_review --> rendering: re-render
  video_review --> story_review: reject video
  video_review --> ready: approve (owner or quality gate)
  ready --> scheduled: publication consented
  scheduled --> published: live
  published --> [*]
  state "any non-terminal state" as ANY
  ANY --> failed: unrecoverable
  ANY --> cancelled: user deletes
  failed --> [*]
  cancelled --> [*]
```

## 10. Publication state machine

```mermaid
stateDiagram-v2
  [*] --> awaiting_consent: post form or autopilot
  awaiting_consent --> scheduled: consent_at recorded
  awaiting_consent --> cancelled: cancelled in veto window
  scheduled --> uploading: queue time reached, quota unit reserved
  scheduled --> cancelled: cancel before upload
  uploading --> scheduled: quotaExceeded, retry next PT day
  uploading --> processing: upload complete
  uploading --> failed: invalid_grant or fatal error
  processing --> published: processed and live
  processing --> failed: rejected / failed on YouTube
  published --> [*]
  failed --> [*]
  cancelled --> [*]
```

## 11. RenderJob state machine

```mermaid
stateDiagram-v2
  [*] --> queued
  queued --> submitted: SubmitRenderJob
  submitted --> running
  running --> ingesting: adapter reports success
  ingesting --> succeeded: stored + quality report
  queued --> cancelled
  submitted --> failed
  running --> failed: error or 60 min timeout
  ingesting --> failed
  running --> cancelled
  succeeded --> [*]
  failed --> [*]
  cancelled --> [*]
```

A job retries automatically at most twice, on transient errors only. `image_moderated` and `story_moderated` are never retried automatically.

## 12. Core data model (P)

```mermaid
erDiagram
  users ||--o{ series : owns
  users ||--o{ channel_connections : connects
  users ||--o{ user_consents : accepts
  series ||--o{ episodes : contains
  series ||--|| series_bibles : remembers
  series ||--o{ episode_ideas : proposes
  series ||--o{ series_autopilot_authorizations : authorizes
  series ||--o{ autopilot_runs : logs
  episodes ||--o{ story_versions : versions
  episodes ||--o{ story_sources : researches
  episodes ||--o{ fact_sheets : grounds
  episodes ||--o{ render_jobs : renders
  episodes ||--o{ assets : stores
  episodes ||--o{ publications : publishes
  channel_connections ||--o{ publications : targets
  series_autopilot_authorizations ||--o{ publications : "consent basis"
  render_jobs ||--o{ usage_records : meters
  users ||--o{ data_deletion_requests : "deletion duties"

  series {
    uuid id PK
    text topic
    bool autopilot_enabled
    jsonb autopilot_settings
    uuid current_autopilot_authorization_id FK
  }
  episodes {
    uuid id PK
    uuid series_id FK
    text status
    uuid approved_story_version_id FK
  }
  publications {
    uuid id PK
    uuid episode_id FK
    text status
    timestamptz consent_at
    text consent_source
    uuid autopilot_authorization_id FK
    timestamptz veto_notice_sent_at
  }
  channel_connections {
    uuid id PK
    text platform
    bytea refresh_token_enc
    text status
  }
  series_autopilot_authorizations {
    uuid id PK
    int version
    text topic
    text cadence
    timestamptz accepted_at
    timestamptz revoked_at
  }
```

Every user-owned table carries `user_id` behind an EF global filter. All columns: `plans/00-overview.md` §6. The S-only tables (credits, payments, admin settings, owner actions) no longer exist (DEC 10.14).

## 13. YouTube data deletion duties (P)

```mermaid
flowchart LR
  D1["In-app disconnect"] --> RV["Revoke token with Google now"] --> R7["Deletion request, due in 7 days"]
  D2["TokenHealthJob: refresh fails invalid_grant"] --> RK["Connection revoked"] --> R30["Deletion request, due in 30 days"]
  D3["invalid_client / unauthorized_client"] --> INC["Platform incident alert, no deletion"]
  R7 --> DJ["DataDeletionJob (hourly)"]
  R30 --> DJ
  DJ --> DEL["Delete tokens, channel metadata, external ids, payloads before due_at"]
  DJ -->|"24 h before due_at"| AL["Alert"]
```

Sources: YouTube Developer Policies III.D.2 ✅; `plans/06-youtube-posting.md` 06.4; `plans/13-compliance.md` 13.3.1.

## 14. YouTube compliance path (P)

```mermaid
flowchart TD
  L["Privacy + terms drafted, published, accepted by owner"] --> OA["OAuth app In production, unverified (owner accepts warning once)"]
  OA --> UP["06 upload works: private only (unaudited project)"]
  DL["7 / 30-day deletion jobs live"] --> EP
  UP --> EP["Evidence pack on the production project, incl. autopilot + veto window"]
  EP --> AU["File YouTube API compliance audit (M1)"]
  AU -->|"approved"| PUBL["Public autopilot, Channel phase"]
  AU -->|"rejected"| APL["Appeal, fix, re-file (stays private)"]
  PUBL --> STEADY["M3 Steady autopilot operation (daily YouTube + TikTok)"]
```

## 15. Credits, payments and admin settings — REMOVED

Sections 15 (credits/payments) and 16 (critical settings change for a multi-admin SaaS) existed only for public users. They are deleted with plan 12 by `DEC 10.14` (`DECISIONS.md` §10.14): nothing is sold, so there is no ledger, no payment provider and no owner-action approval flow. What stays is the **metering** path: every OpenAI image call or GPU render writes `usage_records` (per episode, per render, per provider request) and the autopilot auto-pauses on the owner's spend cap; `ICreditLedger` keeps working in metering-only mode (`usage_records`), with no balances to reserve or refund.

## 16. Milestones and the personal path

```mermaid
flowchart LR
  M0["M0 Trial (01-05)"] -->|"Gate 1: quality + cost"| M1["M1 Personal studio (P: 249.6 d buffered)"]
  M1 --> AUD["YouTube audit filed (owner's channel)"]
  AUD -->|"approved, unknown wait"| CH["Channel phase: at least 12 weeks"]
  CH --> M3["M3 Steady autopilot operation"]
  M1 -.->|"TikTok audit approved"| TT["TikTok autopilot (P, 07)"]
```

## 17. Planning process

```mermaid
flowchart LR
  H["High-level plans 00-11, 13"] --> RV["Review touched sections (+ security review for auth/credentials/data)"]
  RV --> FP["Feature plan: plans/features/story-id-slug.md"]
  FP --> DR["Review: Definition of Ready"]
  DR --> IM["Build"]
  IM --> DD["Definition of Done; feature plan updated; contract changes to 00 first"]
```

## 18. CI/CD

```mermaid
flowchart LR
  PR["Pull request (no secrets)"] --> CI["Build + tests: .NET (PostgreSQL 17 service), Angular lint/test/build, e2e with mock-mpt"]
  CI --> MG["Merge to main"]
  MG --> IM["Images tagged with git SHA + digest"]
  IM --> MI["EF migration bundle: expand, then contract"]
  MI --> ST["Staging: automatic deploy"]
  ST --> PD["Production: manual approval, OIDC deploy"]
  MG --> NB["node.yml: node bundle + SHA256SUMS"]
  NB --> ND["nts-node deploy by the operator over VPN: drain, install, smoke, activate"]
```

Node lane: GPU nodes are updated from a CI-built, hashed bundle by the operator over the VPN; CI never reaches the render network. Image signing and SBOM (`11.5.3-T2`, 11 CR-9) were **deleted with phase S** (DEC 10.14): images are tagged and digested, not cosign-signed, and no SBOM is published.
