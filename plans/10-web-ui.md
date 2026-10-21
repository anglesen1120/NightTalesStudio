# 10 — Web UI (Angular 22, Clean Architecture) (M1)

> Contracts: `00-overview.md` §5.1, §5.3 (feature layout), §5.4 (standards), §7 (states), §8 (routes). Backend behaviour: `09-backend-api.md`. Publishing rules: 06/07. This plan owns **every screen, the front-end architecture and UX quality**.
> Level: **high-level plan — not the final plan.** It fixes scope, contracts, key decisions, risks and rough estimates only. Every feature in it will still get (1) a review of the relevant sections and (2) its own **detailed feature plan** in `plans/features/`, reviewed again before implementation (process and template: `00-overview.md` §10.1). Details and estimates here may change in that step.
> Delivery phase (personal-only, `00-overview.md` §4.1, DEC 10.14): all P. The app is the owner's studio, so the phase-S screens are deleted, not deferred: the landing and pricing pages, `/register` (closed registration answers 403 `registration_closed`; no page is rendered), the verify-e-mail page, the CAPTCHA widget and breached-password field error, all of billing/wallet/refunds/bank-QR (plan 12), the whole admin area (settings, funding, user management, flags), the public report and settings-cancel pages, account export/self-service deletion and the separate accessibility-pass epic. TikTok (10.7.3) is phase P (§4.1: TikTok is P, not a separate release) and ships in M1.

## 1. Purpose and scope

**Goal:** a clear, bilingual (vi/en) web app that the owner uses end-to-end to run the studio, built with Angular 22 and Clean Architecture per feature.

**In scope:** workspace and guardrails; core (auth, HTTP, errors, realtime, i18n); prerendered legal pages; auth screens (login, MFA, forgot/reset); dashboard; series and autopilot; episodes (story review = checkpoint #1, render progress, video review = checkpoint #2); publish (YouTube and TikTok); channels; account security; testing.

**Out of scope:** native mobile apps; a video editor (the owner only edits text/keywords); marketing site CMS; public sign-up, pricing, billing, credits, payments and refunds; the admin surface; public-user data rights (DEC 10.14).

## 2. BA and UX review of this part

| # | Finding | Decision |
|---|---|---|
| U1 | Persona P1 is non-technical → pipeline jargon (keywords, render, MPT) confuses | UI words: "Scenes" (keywords), "Make video" (render), "Review", "Publish". Never show MPT/OpenAI names |
| U2 | Waiting 10–30 min for a render is the biggest UX risk | Progress timeline with stages + e-mail/in-app notification when ready; users can leave the page |
| U3 | Checkpoint #1 must feel like real editing, not a click-through, or the mass-produced policy risk grows | Editor encourages edits (hook tips, quality hints from 02.3.4); "Approve" needs an explicit confirm |
| U4 | Spend is abstract (unchanged by the drop: the owner still pays OpenAI per generation) | Nothing is bought or balanced (DEC 10.14), so no price, quote or balance UI exists: a paid step shows its plain action button, a 429 `spend_cap_reached` maps to one clear message, and runway alerts arrive by e-mail (09 §5.4, 11.6.2) |
| U5 | True-crime liability | Sources panel + attestation checkbox before approval (02.3.3); warning banner on true-crime series |
| U6 | Audit reviewers (P3) judge the consent screens | Publish and connect screens follow 06/07 text exactly; screenshots feed the YouTube and TikTok audit submissions (13.4.2-T1, 13.4.3-T1) |
| U7 | Vietnamese text is longer and has diacritics | Layouts tested with vi strings; fonts with full Vietnamese glyph coverage |
| T1 | Angular features evolve fast | Pin Angular 22.x (22.1.7 ✅ latest on npm 2026-09-16; 22.2.0 ✅ 2026-09-24); use only stable APIs. (10 CR-4) Zoneless is the default, Vitest is the default unit runner, Signal Forms and `httpResource` are stable in 22 (`features/10-web-ui/README.md` FE1–FE5); forms stay typed reactive forms (D13) |

## 3. Requirements

**Functional**
- **FR-10-1** Every route in §5.2 exists with loading, empty, error and success states.
- **FR-10-2** Auth: login, TOTP MFA step, silent refresh, logout, forgot/reset password. Sign-up is closed (DEC 10.14): no register or verify-e-mail page exists and `403 registration_closed` is never reached from this app.
- **FR-10-3** Series create/edit with voice previews and style previews. Voice step: tags shown as i18n keys `voices.tag.<tag>` (vi + en), no play button when `previewUrl` is null, no speed control (04 CR-11).
- **FR-10-4** Episode journey: brief → generate → edit/versions → approve (with attestation for true crime) → make video → live progress → review (play, approve/reject, download).
- **FR-10-5** Publish to YouTube with per-video consent (flag `youtube_publish`); TikTok post screen with all required items (P · M1, flag `tiktok_publish`).
- **FR-10-6** Channels connect/disconnect with revoke explanation and the Google permissions link.
- **FR-10-7** Account security: TOTP enrolment, recovery codes, re-auth dialog, accepted policy versions (10.3.3-T3, 13.2.1).
- **FR-10-8** Bilingual UI: every string in vi and en, language switch in the shell (10.2.5-T1, 10.2.6-T1). No data-export or self-service-deletion screen: those flows were phase S and are deleted with the public-user data rights (DEC 10.14; the owner runs deletion through the backend engine, 13.3.1).
- **FR-10-9** No admin surface (DEC 10.14): no `/admin` route, no user management, no feature-flag or integration-settings screens, no funding or billing console. Flags are seeded/config values and settings live in the environment (09 §5.6); the Hangfire dashboard is served by the API at `/internal/jobs`, not by this app (09.2.6).

**Non-functional**
- **NFR-10-1** Largest Contentful Paint < 2.5 s on the prerendered legal pages; app shell interactive < 3 s on 4G [target].
- **NFR-10-2** Initial JS bundle ≤ 250 kB gzip for the authenticated shell; every feature lazy-loaded (budgets in `angular.json`).
- **NFR-10-3** WCAG 2.1 AA on core flows: keyboard only, focus visible, labels, contrast ≥ 4.5:1 in dark theme. Checked inside every feature's acceptance (the `-T0` design decks carry focus order and a11y notes) and by axe in the Playwright journeys (10.10.2-T1); there is no separate accessibility-pass task — that pass and its public/billing/admin matrices were phase S and are deleted (DEC 10.14).
- **NFR-10-4** 100 % of UI strings in vi and en; missing keys fail CI.
- **NFR-10-5** No access token in `localStorage`/`sessionStorage` (memory only; refresh via HttpOnly cookie, 09.3.2).
- **NFR-10-6** Architecture boundaries enforced by lint (00 §5.4).

## 4. Epics and user stories

### Epic 10.1 — Workspace and guardrails

**10.1.1 Create workspace** — *As the Tech Lead I want a strict, standard Angular 22 workspace so that code starts clean.* Must · S
- Given `ng new` (Angular CLI 22), Then: standalone, strict TS + `strictTemplates`, SCSS, routing, SSR enabled for the prerendered legal routes only (`/privacy`, `/terms` and their `/vi|en` variants), bundle budgets per NFR-10-2, path aliases `@core/*`, `@shared/*`, `@features/*`.
- Zoneless change detection (the default since v21 ✅) + OnPush everywhere; static output (`outputMode: 'static'`), no Node server; served by the `web` container on the same origin as the API (10.1.1-T2, `features/10-web-ui/README.md` D1/D3) (10 CR-4).

**10.1.2 Lint and boundaries** — *As the Tech Lead I want architecture rules in lint so that layers stay clean.* Must · S
- Given ESLint (angular-eslint) + boundary plugin ⚠️ license check, When a `ui` component injects a store/repository, or `domain` imports `@angular/*`, or a feature deep-imports another feature, Then lint fails.
- Rules also forbid `any`, non-OnPush components, and subscriptions without `takeUntilDestroyed`.

**10.1.3 Design system** — *As a user I want a calm, dark "night" theme so that the brand fits the genre and is readable.* Must · M
- Given Angular Material with a custom M3 theme (dark default, light option), Then tokens (colour, spacing, typography with Vietnamese coverage, e.g. "Be Vietnam Pro" ⚠️ license OFL check) are defined once; shared UI: page header, empty state, error state, status chip, confirm dialog, stepper (no cost or balance badge: DEC 10.14).

**10.1.4 Generated API client** — *As a developer I want a typed client from OpenAPI so that the contract never drifts.* Must · S
- Given `openapi.json` from CI (09.9.5), When `npm run api:gen` runs (generator ⚠️ license check, e.g. `openapi-generator` or `ng-openapi-gen`), Then `shared/api/` is regenerated; only `data-access` files import it (lint rule).

**10.1.5 Test tooling** — *As the team I want fast tests so that TDD is realistic.* Must · S
- Given the CLI default unit runner (Vitest ✅) and Playwright (Apache-2.0 ✅) for e2e, When CI runs, Then unit + component tests run in `web.yml` and the Playwright suite runs in `e2e.yml` (11.5.1-T1) against the compose stack on each PR (10 CR-4).

**10.1.6 Content Security Policy and Trusted Types** — *As the security owner I want the browser to refuse injected script so that one XSS cannot steal a session.* Must · S
- Given the SPA and SSR responses, Then a strict CSP is sent: `script-src 'self'` plus build-time SHA-256 hashes of the inline scripts (static prerendered pages cannot carry per-response nonces; 10 CR-4, `features/10-web-ui/README.md` D4), `object-src 'none'`, `base-uri 'none'`, `frame-ancestors 'none'`, `connect-src` limited to the API origin and the hub, and `require-trusted-types-for 'script'` [DECISION]; a CI e2e check fails if any page logs a CSP or Trusted Types violation.
- Given ESLint, When code uses `bypassSecurityTrust*` or binds `[innerHTML]` outside the one reviewed sanitizer module, Then lint fails.
- Given a download (MP4), Then the `Content-Disposition` filename is sanitized (no quotes, path separators or control characters).

### Epic 10.2 — Core

**10.2.1 Session and silent refresh** — *As a user I want to stay signed in so that I am not interrupted.* Must · M
- Given a 401 from an API call (including `session_revoked` after a stale security stamp), When the refresh cookie is valid, Then one refresh call runs (concurrent 401s share it), the original request is retried once; if refresh fails, the session store clears and the user goes to `/login?returnUrl=…`.
- Given a page reload, When the app boots, Then `POST /auth/refresh` restores the session before guarded routes activate.

**10.2.2 Error handling** — *As a user I want clear messages so that I know what to do.* Must · S
- Given a ProblemDetails `code` (catalogue 09 §5.4), When mapped by `AppErrorMapper`, Then the user sees the i18n message for that code; `validation_failed` maps field errors onto form controls; unknown codes show a generic message with `traceId`.
- Given 429 `spend_cap_reached` or `generation.processingPaused = provider_funds_exhausted` on the episode (00 §8), Then the page says processing is paused at the owner's OpenAI spend cap and clears when the API stops reporting it — no purchase, top-up or balance link exists (DEC 10.14).

**10.2.3 Idempotency interceptor** — *As the platform I want safe retries so that double clicks never double-run a metered step.* Must · XS
- Given a metered action, When the store calls it, Then an `Idempotency-Key` (UUID) is generated once per user intent and reused on retry.

**10.2.4 Realtime service** — *As a user I want live updates so that progress moves without refresh.* Must · M
- Given `@microsoft/signalr`, When logged in, Then the service first calls `POST /hubs/ticket` and connects with that 60-second single-use ticket (never the access token in the query string), WebSockets only (`skipNegotiation`); one connection with automatic reconnect exists (each reconnect fetches a new ticket); events are exposed as signals/observables per type; stores subscribe by episode id; on reconnect, stores re-fetch the current state (events may have been missed).
- Given the server aborts the connection after an MFA reset or password reset, or a connect returns 401 `hub_ticket_invalid`, Then the service does not reconnect until the session refresh succeeds and a new ticket is issued.

**10.2.5 i18n** — *As a Vietnamese user I want the whole UI in Vietnamese so that I understand every step.* Must · S
- Given Transloco (MIT ✅), Then `vi` and `en` JSON files per feature scope; language from profile, else browser; date/number formats per locale; a CI check fails on missing keys.

**10.2.6 Shell and navigation** — *As a user I want a simple layout so that I always know where I am.* Must · S
- Given the shell, Then side nav (Dashboard, Series, Episodes, Channels, Account security), top bar with the language switch and user menu; no billing or admin entries (DEC 10.14); responsive down to 360 px.

### Epic 10.3 — Legal pages and auth

**10.3.1 Privacy and terms pages** — *As the owner I want the policy pages public and prerendered so that the YouTube audit can be filed.* Must (P) · S
- Given prerendered routes `/privacy` and `/terms` (plus `/vi/...` and `/en/...`), Then each has title/description/OG meta in both languages ([DECISION] path prefix), no auth calls during prerender, and renders the versions from 13 (13.1.1-T1, 13.1.2-T1). These are the only prerendered pages: the landing, pricing and content-policy pages were phase S and are deleted (DEC 10.14).

**10.3.3 Login, MFA, forgot/reset** — *As the owner I want the standard account flows so that access is easy and hard to steal.* Must · L
- Given `/login`, `/forgot-password`, `/reset-password#t=` (token in the URL fragment, 09 BD3) and the MFA step, Then each handles success, expired token, and rate-limit (429 `rate_limited` shows a countdown from `Retry-After`). There is no `/register` or `/verify-email` page: sign-up is closed (single owner, DEC 10.14), so no register link is rendered and 403 `registration_closed` never appears in the UI.
- Given 10 wrong MFA codes inside 15 minutes for the owner, When `/auth/login` or `/auth/login/mfa` answers 429 `rate_limited` with `Retry-After: 900` (the per-user wrong-code window, 09.3.2), Then the code field stays enabled and the step shows the countdown as an inline hint (a correct code is still accepted, because a valid TOTP never goes through the wrong-code charge); there is no CAPTCHA widget, no "device trusted" screen and no account-locked screen (all dropped with phase S, DEC 10.14).
- Given `/account/security` (10.3.3-T3), Then TOTP enrolment (QR + manual key, verify a code, show the 10 recovery codes once with download) and regenerate recovery codes; a login with MFA enrolled asks for the code or one recovery code (`POST /auth/login/mfa`), and the owner without MFA is redirected to enrolment before any other route.
- Given `reauth_required` (401) on a sensitive action — channel connect/disconnect, publication cancel, MFA enrol/reset, password change — Then a shared re-auth dialog (password + TOTP, 09.10.4) collects both and retries the action once. There is no admin redirect, no user-management screen and no "disable MFA for a user" action (dropped with phase S, DEC 10.14).

### Epic 10.4 — Dashboard and series

**10.4.1 Dashboard** — *As a creator I want an overview so that I see what needs my attention.* Must · S
- Given `GET /episodes` filtered by status, Then cards show "Needs your review" (story_review, video_review), "In progress" (story_generating, rendering) and "Ready to publish"; there is no credit balance, buy button or spend total on the dashboard (DEC 10.14).

**10.4.2 Series list and editor** — *As a creator I want to set up a series once so that every episode keeps the same style.* Must · M
- Given `/series/new`, Then a 4-step form: 1) topic + language, 2) aspect (landscape long-form / portrait Shorts), 3) voice (list from `GET /voices` filtered by language: display name, gender, tags as i18n keys `voices.tag.<tag>` in vi + en, a play button only when `previewUrl` is not null, no speed control — the speaking rate is fixed in P) (04 CR-11), 4) visual style (grid of preview images from `GET /style-templates?topic=`; labels and hints are i18n keys `series.style.<name>.label` / `.hint` from `seed/i18n-style-labels.v1.json`; for true crime only the two `true_crime` styles show, with `series.style.trueCrimeNotice`); true-crime shows a responsibility notice (03 CR-7).
- Given edit, When the language changes and the voice no longer matches, Then the voice field is cleared with an explanation.

**10.4.3 Autopilot settings and run log (personal phase, 02.8)** — *As the channel owner I want to switch a series to autopilot so that ideas, stories, videos and publishing happen without me.* Must (P) · M
- Given `/series/:id` → Autopilot tab, Then the owner sets on/off, cadence (days, publish time, time zone), daily cap, ready-buffer size, privacy status on publish (public/unlisted/private), veto window, pre-audit mode and (advanced) gate thresholds; turning it on requires accepting the versioned authorization text (02.8.3); the topic is locked while autopilot is on; a true-crime series shows "Fact sheet and story will wait for your approval".
- Given the run log (`GET /series/{id}/autopilot/runs`), Then each run shows the idea picked (with rejected ideas and similarity), gate results, render and publication status, and the reason for any pause; "Pause" / "Resume" call `POST /series/{id}/autopilot/pause|resume`. The log refreshes on `EpisodeUpdated` and polls every 30 s while the tab is open (no `AutopilotRunUpdated` event in P, 02 T2).
- Given publications in the veto window, Then the tab lists them with preview, time left and Edit (`PATCH /publications/{id}`), Cancel (`DELETE`) and Approve now (`POST …/consent`).

### Epic 10.5 — Episodes: story review (checkpoint #1)

**10.5.1 Episode list** — *As a creator I want to find episodes so that I can continue work.* Must · S
- Given `/episodes`, Then a filterable (status, series) cursor-paginated list with status chips and "next action" buttons.

**10.5.2 New episode + generate** — *As a creator I want to write a short idea and get a draft so that I start fast.* Must · S
- Given `/series/:id/episodes/new`, Then a brief textarea (≤ 1,000 chars, counter), target length selector, and a "Write story" button; after click the page shows the generating state live (SignalR) and becomes the editor when done. 02 CR-7: the state comes from `generation { kind, status, stage, errorCode }` on the episode detail and `EpisodeUpdated` (stages `queued|writing|checking|reviewing|revising|scoring|saving|done`); a failure shows the message for `errorCode`. There is no credit quote, balance or price text: the step is metered in the backend only and a 429 `spend_cap_reached` shows the paused message of 10.2.2 (DEC 10.14).

**10.5.3 Story editor — view and edit** — *As a creator I want to edit my script comfortably so that it sounds like me.* Must · L
- Given `story_review`, Then the editor shows title, hook, paragraphs (each editable in place, with its "Scenes" chips = keywords that can be added, edited and removed), ending, description and tags; `hook` and `ending` are segments with their own scene chips, and each paragraph shows its `beat` as a small read-only label (02 CR-9, `story.v1`); autosave is **off** — explicit "Save version" sends `PUT /episodes/{id}/story` with `If-Match`.
- Given flagged keywords (03.2.2), Then their chips are red and "Make video" stays disabled until they are changed; a 409 `keywords_flagged` maps `errors[] { segment, index, rule }` to the chips; advisory hints `scenes_trimmed` / `scenes_sparse` show inline (copy keys in `features/03-image-generation/03.2-keyword-quality/plan.md` §6, 03 CR-7).

**10.5.9 Story editor — structure and dirty guard** — *As a creator I want to reorder and remove paragraphs safely so that I shape the story without losing work.* Must · L
- Given the paragraph list, Then each paragraph can be moved (drag handle and keyboard up/down buttons) and deleted (undo snackbar); scene chips move with their paragraph.
- Given unsaved changes, When navigating away or closing the tab, Then a confirm dialog appears (route guard + `beforeunload`).

**10.5.10 Story editor — hints, highlights and conflicts** — *As a creator I want to see what to improve and never lose a colleague's or tab's edits so that the final script is both good and mine.* Must · L
- Given quality hints (02.3.4), Then they appear inline next to the paragraph/keyword concerned (shared inline-annotation component).
- Given self-review issues (02.6.1, `audit_report`: blocking/advisory, objective/taste, evidence, repair direction), Then they use the same inline component; a version marked `revised` shows "Automatically corrected once" with a diff link; similarity warnings highlight the overlapping paragraphs (task 10.5.3-T2).
- Given 412 `story_version_conflict`, Then a dialog offers "Reload latest" or "Save mine as new version" (never silent overwrite); the user's unsaved text is kept in memory until they choose.

**10.5.4 Versions and diff** — *As a creator I want to see earlier versions so that I can go back.* Should · M
- Given `GET /episodes/{id}/story/versions`, Then a side panel lists versions; selecting two shows a word-level diff; "Restore" creates a new version from the old one.

**10.5.5 Sources and fact sheet (true crime; optional for other topics)** — *As a creator I want to add my sources and check the facts before the story is written so that my story is defensible.* Must · L
- Given `/episodes/:id/research`, Then the user pastes links one by one (max 10) or adds a personal note; each source shows live fetch status (pending / fetched with title / failed with the reason from 02.5.2); blocked domains show the message from 02.5.1 immediately.
- Given ≥ 1 fetched source, When "Build fact sheet" is clicked, Then the fact list appears; each fact is editable, deletable, and shows source chips (click opens the original page in a new tab); "Add fact" requires choosing a source or a note.
- Given a true-crime episode, Then "Write story" is disabled until the fact sheet is approved with ≥ 2 fetched sources; the attestation checkbox (02.3.3) stays on the approve-script dialog; its text and version come from `currentStory.attestation` (02 CR-7, T29).
- Given 412 `fact_sheet_conflict`, Then the same reload/save-as-new dialog as the story editor is shown.

**10.5.6 Approve / reopen** — *As a creator I want to approve my final script so that the video uses exactly it.* Must · S
- Given valid content, When "Approve script" is clicked, Then a confirm dialog states that edits after approval need reopening, and the approval is the explicit click (no cost or credit text: DEC 10.14).

**10.5.7 Series bible panel** — *As a creator I want to see and edit what my series remembers so that episodes stay consistent.* Should · M
- Given `/series/:id/bible`, Then narrator, recurring characters, places, rules and past episode summaries are editable with `If-Match`.
- Given a pending proposal after a story approval, Then a banner shows the proposed changes as a diff with "Accept" and "Dismiss".

**10.5.8 Find sources for me (flag `source_finder`)** — *As a creator I want link suggestions so that research is faster.* Should (P) · S
- Given the flag and 02.7.2, When "Find sources" is clicked on the research page, Then candidate links are listed with their site; the user ticks the ones to add; nothing is added automatically. Routes (02.7.2-T2): `POST /episodes/{id}/sources/find`, `GET …/sources/candidates`, `POST …/candidates/{cid}/add`, `POST …/candidates/{cid}/dismiss` (02 CR-12).

### Epic 10.6 — Render progress and video review (checkpoint #2)

**10.6.1 Make video + progress timeline** — *As a creator I want to see progress so that I trust the system while waiting.* Must · M
- Given `POST /episodes/{id}/render`, Then a timeline shows the guaranteed stages Queued → Rendering → Uploading to library (`RenderProgress.stage` `queued|running|ingesting`, 00 §8) with `percent` as a progress bar, elapsed time, and "We'll e-mail you when it's ready"; the user can leave and return.
- Given a `RenderProgress` event with a finer stage (`voice → subtitles → images → assembling`, plus `checking` for the node-side post-processing), reported by the adapter (`render-status.v1`, 05 D4/D5, F09) (05 CR-9), Then it is shown as a sub-step under Rendering; unknown stage values are ignored, never shown raw.
- Given `render_worker_unavailable`, Then the timeline says "Waiting for a free studio slot" (not an error).
- Given a video with skipped scenes (`renderJob.images.skipped`, 03 D7 rows 2–3), Then video review shows `render.images.skippedBanner` and highlights each skipped segment, with `render.images.moderatedScene` guidance for moderated ones (03 CR-7).
- Given `image_moderated` (no video was made: > 10 % of scenes blocked, 03 D7), Then the message (`errors.image_moderated`) names the blocked scenes with guidance, plus an "Edit scenes" button that calls `POST /episodes/{id}/story/reopen` (episode is back in `story_approved`, 00 §7) and opens the editor; the backend releases its metering reservation and the UI shows no credit or refund text (DEC 10.14).

**10.6.2 Video review** — *As a creator I want to watch before publishing so that nothing bad goes out.* Must · M
- Given `video_review`, Then an HTML5 player plays from the signed URL; the store refreshes the URL before expiry (≤ 15 min) and on player error 403.
- Given the `render-quality.v1` advisory hints `subtitle_sync_suspect`, `subtitle_cues_long` and `music_none` (05 D10), Then they are shown as advisory notes (i18n keys `render.quality.subtitleSyncSuspect`, `render.quality.subtitleCuesLong`; a failed quality gate uses `errors.render_quality_failed`) (05 CR-9).
- Buttons: "Approve video", "Reject — fix script" (reason required), "Re-make video" (confirm dialog), "Download MP4".

### Epic 10.7 — Publishing and channels

**10.7.1 Channels page** — *As a creator I want to connect my YouTube channel so that I can publish from here.* Must · S
- Given `/channels`, Then connected accounts show name, platform, status, last verified; "Connect YouTube" redirects to Google (`POST /channels/youtube/connect` → URL); callback page shows success/failure.
- "Disconnect" explains: access at Night Tales is removed now, revocation at Google is only requested, pending publications are cancelled and related data is scheduled for deletion 7 days after the disconnect; also links https://security.google.com/settings/security/permissions (DECISIONS §10.6 ✅) *(10.7.1-T0 S75)*.

**10.7.2 YouTube publish form** — *As a creator I want to publish or schedule with explicit consent so that uploads only happen when I say so.* Must · M
- Given a `ready` episode and flag `youtube_publish`, Then the form has channel, title (prefilled), description, tags, privacy, optional schedule date/time (converted to UTC; scheduled posts are uploaded as private with `publishAt` ✅ — only valid with privacy private per YouTube docs ✅), and read-only "Altered or synthetic content: Yes" (`containsSyntheticMedia = true`).
- Given submit, Then a consent step shows: "I authorise Night Tales Studio to upload this specific video to <channel> [now/at <time>]." The publication is created only after the explicit click (`POST /publications/{id}/consent`).
- Given global quota exhaustion (`youtube_quota_exhausted`), Then the UI shows the next available slot from `GET /publishing/youtube/eta`.

**10.7.3 TikTok post screen (P · M1)** — *As the owner I want TikTok's required post screen so that my post is compliant.* Must (P) · L
- On open: call `GET /channels/{id}/tiktok/creator-info`; show the creator **nickname**; if the creator cannot post now, block with the API message; block videos longer than `max_video_post_duration_sec` ✅.
- Title field; privacy dropdown built only from `privacy_level_options` with **no default** ✅; interaction toggles Comment/Duet/Stitch **unchecked by default** and disabled when the creator has them off ✅.
- Commercial content disclosure toggle off by default; when on, "Your brand"/"Branded content" options with the required label texts ✅.
- Before the Post button: "By posting, you agree to TikTok's Music Usage Confirmation" ✅; AI-generated label on by default (`is_aigc` ✅, 07).

**10.7.4 Publication status** — *As a creator I want to see publishing status so that I know when it is live.* Must · S
- Given `PublicationUpdated` events, Then the episode page shows each publication's state (scheduled/uploading/processing/published/failed) with a link to the live post.
- Given an autopilot publication inside its veto window (P), Then the veto e-mail links open `/publications/cancel` (public, token in the fragment, no session) or `/publications/approve` (owner, guarded) and both pages render from that token alone (10.7.4-T2).

### Epic 10.10 — Quality

**10.10.2 E2E journeys** — *As the owner I want the main journeys automated so that changes are safe.* Must · L
- Playwright specs against API + mock-mpt: login → MFA → series → episode → generate → edit → approve → render → review → download; legal pages prerender in vi and en; ETag conflict; moderation failure; `spend_cap_reached`; YouTube publish with consent (mocked Google); TikTok post screen (P). No register → verify journey exists (phase S, DEC 10.14) and no billing or admin journey is planned.
- Given every spec, Then axe runs on the page it drives (WCAG 2.1 AA ruleset ⚠️ axe-core license MPL-2.0 [INFERENCE], confirm) and a violation fails the spec; this is the plan's only automated accessibility gate, because the separate accessibility-pass epic and its public/billing/admin matrices were phase S and are deleted (DEC 10.14).

## 5. Technical design

### 5.1 Example feature slice (episodes)

```ts
// features/episodes/domain/episode.model.ts  (no Angular imports)
export type EpisodeStatus = 'draft' | 'story_generating' | 'story_review' | 'story_approved'
  | 'rendering' | 'video_review' | 'ready' | 'scheduled' | 'published' | 'failed' | 'cancelled';
export interface Episode { id: string; status: EpisodeStatus; etag: string; nextActions: EpisodeAction[]; /* … */ }
export const canApproveStory = (e: Episode, s: StoryDraft): boolean =>
  e.status === 'story_review' && s.paragraphs.length > 0 && !s.hasFlaggedKeywords;

// features/episodes/domain/episodes.repository.ts
export abstract class EpisodesRepository {
  abstract get(id: string): Promise<Episode>;
  abstract generateStory(id: string, idempotencyKey: string): Promise<void>;
  abstract saveStory(id: string, draft: StoryDraft, etag: string): Promise<{ etag: string }>;
}

// features/episodes/application/episode-detail.store.ts
@Injectable()
export class EpisodeDetailStore {
  private readonly repo = inject(EpisodesRepository);
  readonly episode = signal<Episode | null>(null);
  readonly status = computed(() => this.episode()?.status);
  async approveStory() { /* call repo, update signals, map AppError */ }
}

// features/episodes/ui/story-editor.component.ts — inputs/outputs only, OnPush, no injection of stores
// features/episodes/feature/story-review.page.ts — provides EpisodeDetailStore, wires ui ↔ store
// features/episodes/episodes.routes.ts — providers: [{ provide: EpisodesRepository, useClass: EpisodesHttpRepository }]
```

State: plain signal-based stores per feature [DECISION]; NgRx SignalStore (⚠️ license check) is acceptable later if stores grow, but not mixed within one feature.

### 5.2 Screen inventory

| Route | Feature/page | API calls | States |
|---|---|---|---|
| `/privacy`, `/terms` (prerendered, also `/vi/…`, `/en/…`) | public | none | static (both languages) |
| `/login`, `/login/mfa`, `/forgot-password`, `/reset-password#t=` | auth | `/auth/*` (reset token in the URL fragment) | form, submitting, error, success, rate-limited |
| `/dashboard` | dashboard | `GET /episodes` | loading, empty (first-run guide), data |
| `/series`, `/series/new`, `/series/:id` | series | `/series`, `/voices`, `/style-templates` | loading, empty, form, error |
| `/episodes` | episodes | `GET /episodes` | loading, empty, list |
| `/series/:id/episodes/new` | episodes | `POST /series/{id}/episodes`, `POST …/story/generate` | form, generating, spend-capped |
| `/episodes/:id` | episodes (router by status) | `GET /episodes/{id}` + SignalR | per status: generating, story review, rendering, video review, ready, published, failed, paused |
| `/episodes/:id/publish/youtube` | publish | `/channels`, `POST /episodes/{id}/publications`, `POST /publications/{id}/consent` | form, consent, submitted, quota wait |
| `/episodes/:id/publish/tiktok` (P) | publish | `GET /channels/{id}/tiktok/creator-info`, publications | creator blocked, form, consent, submitted |
| `/channels`, `/channels/:platform/callback` | channels | `/channels*` | list, connecting, callback result |
| `/publications/cancel` (token in the fragment, no session) | publish (10.7.4-T2) | `POST /publications/cancel-by-link` | confirm, done, invalid/expired link, already published |
| `/publications/approve` (owner, guarded) | publish (10.7.4-T2) | `GET /episodes/{e}/publications`, `POST .../consent` | preview, consent text, approved, refusal |
| `/account/security` | account | `/me/mfa/*`, `/auth/reauth` | MFA enrol/verify, recovery codes shown once, re-auth dialog |

### 5.3 Episode journey (UX flow)

```mermaid
flowchart TD
  A[New episode: brief] -->|Write story| B[Generating]
  B --> C[Story review: edit, versions, sources]
  B -->|"failed / cancelled / paused: metering released, back to previous step"| A
  C -->|Approve + attest| D[Script approved]
  D -->|Make video| E[Rendering timeline]
  E -->|failed: metering released, reopen to edit| C
  E --> F[Video review]
  F -->|Reject: fix script| C
  F -->|Re-make| E
  F -->|Approve| G[Ready]
  G --> H[Download MP4]
  G --> I[Publish YouTube: form + consent]
  G --> J[Publish TikTok (P · M1): required screen]
  I --> K[Scheduled / Published]
  J --> K
```

## 6. Task breakdown

| Task ID | Task | Area | Size | Depends on |
|---|---|---|---|---|
| 10.1.1-T1 | `ng new`, static prerender for the legal routes, aliases, budgets | FE | S | — |
| 10.1.1-T2 | `web` container (nginx), same-origin routing contract, dev proxy (10 CR-4) | FE | S | 10.1.1 |
| 10.1.2-T1 | ESLint + boundary rules + CI lint | FE | S | 10.1.1 |
| 10.1.3-T1 | Material M3 theme, fonts, shared UI components | FE | M | 10.1.1 |
| 10.1.4-T1 | OpenAPI generator script + lint rule for imports | FE | S | 09.9.5 |
| 10.1.5-T1 | Unit runner + Playwright setup + CI | FE | S | 10.1.1 |
| 10.1.6-T1 | Build-time hash CSP + Trusted Types, ESLint sanitizer ban, CSP-violation e2e check, safe download filenames (10 CR-4: S → M) | FE | M | 10.1.1, 10.1.2 |
| 10.2.1-T1 | Session store, auth interceptor, refresh queue, guards | FE | M | 09.3.2 |
| 10.2.2-T1 | AppErrorMapper + form error binding | FE | S | 09.1.4 |
| 10.2.3-T1 | Idempotency-Key interceptor/helper | FE | XS | 10.2.1 |
| 10.2.4-T0 | UI design (app shell, nav, language switch, user menu, realtime status) (10 CR-31) | UX | S | 10.1.3 |
| 10.2.4-T1 | SignalR service (hub ticket, WebSockets only) + reconnect re-fetch + server-abort handling | FE | M | 09.8.1 |
| 10.2.5-T1 | Transloco setup, scopes, missing-key CI check | FE | S | 10.1.1 |
| 10.2.6-T1 | Shell layout, nav (no billing or admin entries), language switch, user menu | FE | S | 10.2.4 |
| 10.3.1-T1 | Privacy and terms pages (prerender + meta, vi/en; P — required before the YouTube audit) | FE | S | 13.1.1-T1, 13.1.2-T1 |
| 10.3.3-T0 | UI design (login, MFA, forgot/reset, re-auth, legal pages) (10 CR-31) | UX | M | 10.1.3 |
| 10.3.3-T1 | Login (password step)/forgot/reset pages | FE | M | 10.2.1 |
| 10.3.3-T3 | MFA login step, owner enrolment, recovery codes, re-auth dialog (P; moved from 10.9.5-T1, 10 CR-4; the rest of 10.9.5 — MFA disable for other users, admin redirect — is deleted by DEC 10.14) | FE | M | 10.3.3-T1, 09.3.2, 09.10.4 |
| 10.4.1-T0 | UI design (dashboard, series editor, autopilot tab, run log, veto list) (10 CR-31) | UX | M | 10.1.3 |
| 10.4.1-T1 | Dashboard | FE | S | 10.5.1 |
| 10.4.2-T1 | Series list + 4-step editor + style grid | FE | M | 09.4 |
| 10.4.2-T2 | Voice picker with preview audio (04 CR-11; 10 CR-4) | FE | S | 09.4.2 |
| 10.4.3-T1 | Autopilot tab (settings, authorization, re-auth, pause/resume) | FE | M | 02.8 |
| 10.4.3-T2 | Autopilot run log, calibration panel, veto-window list (10 CR-4) | FE | M | 10.4.3-T1, 02.8, 06 |
| 10.5.1-T0 | UI design (episode list, new + generate, story editor, conflict, dirty guard) (10 CR-31) | UX | M | 10.1.3 |
| 10.5.1-T1 | Episode list | FE | S | 09.5.1 |
| 10.5.2-T1 | New episode + generate state | FE | S | 09.5.2 |
| 10.5.3-T1 | Story editor view/edit: paragraphs, scene chips, flagged chips, Save version with ETag | FE | L | 10.5.2 |
| 10.5.9-T1 | Paragraph reorder (drag + keyboard) / delete with undo, dirty guard + `beforeunload` | FE | L | 10.5.3-T1 |
| 10.5.10-T1 | Inline annotation component + quality hints, 412 conflict dialog keeping unsaved text | FE | L | 10.5.3-T1 |
| 10.5.3-T2 | Self-review issues, "corrected once" badge, similarity highlights in the editor | FE | XS | 10.5.10-T1, 02.6.1 |
| 10.5.4-T0 | UI design (versions/diff, approve & reopen, research page, find sources, bible) (10 CR-31) | UX | M | 10.1.3 |
| 10.5.4-T1 | Versions panel + diff + restore | FE | M | 10.5.3 |
| 10.5.5-T1 | Research page: sources list, fetch status, fact-sheet editor, approve | FE | L | 02.5, 10.5.3 |
| 10.5.7-T1 | Series bible panel + proposal diff | FE | M | 02.6.3 |
| 10.5.8-T1 | Find-sources suggestions | FE | S | 02.7.2 |
| 10.5.6-T1 | Approve/reopen dialogs (no cost text, DEC 10.14) | FE | S | 10.5.3 |
| 10.6.1-T0 | UI design (render timeline, failure states, video review) (10 CR-31) | UX | S | 10.1.3 |
| 10.6.1-T1 | Render timeline (guaranteed stages + optional sub-steps) + failure states | FE | M | 09.6 |
| 10.6.1-T2 | "Make video" action (keyword gate, idempotency) + cancel render (10 CR-14) | FE | S | 10.6.1-T1, 09.6.4 |
| 10.6.2-T1 | Video review page + URL refresh | FE | M | 09.7 |
| 10.7.1-T0 | UI design (channels: connect, callback result, disconnect) (10 CR-31) | UX | S | 10.1.3 |
| 10.7.1-T1 | Channels page + callback (re-auth + legal gate; 10 CR-14: S → M) | FE | M | 06 |
| 10.7.2-T0 | UI design (publish form, consent, quota wait, publication status, veto-email pages) (10 CR-31) | UX | M | 10.1.3 |
| 10.7.3-T0 | UI design (TikTok) (10 CR-24) | UX | S | 10.1.3 |
| 10.7.2-T1 | YouTube publish form + consent + quota wait | FE | M | 06 |
| 10.7.3-T1 | TikTok connect + creator info (P) (10 CR-24: split) | FE | M | 10.7.3-T0, 07 |
| 10.7.3-T2 | TikTok post composer + status (P) (10 CR-24: split) | FE | M | 10.7.3-T1, 07 |
| 10.7.4-T1 | Publication status widgets | FE | S | 06 |
| 10.7.4-T2 | Veto e-mail pages: cancel-by-link (public) and approve-now (10 CR-14) | FE | S | 10.7.4-T1, 06.2.2 |
| 10.10.2-T1 | Playwright owner journeys + axe accessibility assertions (no register, billing or admin journeys; those were phase S, DEC 10.14) | FE | L | all P screens |

## 7. Test plan

- **Domain/util unit tests:** status → next actions; `canApproveStory`; mappers DTO ↔ model; keyword flag rules mirror backend codes.
- **Store tests:** with fake repositories — success, AppError mapping, ETag conflict path, optimistic updates rolled back on error, realtime event merge.
- **Component tests:** story editor (edit, chips, reorder by keyboard, delete + undo, dirty guard, 412 dialog keeps unsaved text), render timeline (guaranteed stages only; finer stage shown as sub-step; unknown stage ignored), login and MFA step (rate-limit countdown on `rate_limited`; no CAPTCHA path exists, DEC 10.14), account security (enrolment, recovery codes shown once, re-auth dialog), publish consent (button disabled until explicit consent), TikTok screen (no default privacy, disabled interactions, duration block).
- **Security checks:** e2e fails on any CSP/Trusted Types violation; the SignalR connect URL never contains the access token.
- **E2E:** 10.10.2 list; run in CI against docker compose (API + mock-mpt + mail catcher).
- **Manual:** vi/en visual review of every screen; 360 px mobile check; screen reader smoke (VoiceOver).

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Story editor complexity explodes | Structured paragraphs (not rich text); editor split into three stories (10.5.3 view/edit, 10.5.9 structure + dirty guard, 10.5.10 hints + conflicts) plus 10.5.3-T2 |
| Trusted Types breaks a third-party library (Material, SignalR) | Check in 10.1.6 before the first feature; a narrowly named policy in the sanitizer module is the only allowed exception |
| Missed realtime events | Re-fetch on reconnect and on page focus |
| Audit rejection due to UI wording | Wording owned by 06/07/13; screenshots reviewed before filing |
| Angular upgrade churn | Pin 22.x; upgrade on a schedule with `ng update` in a dedicated PR |

## 9. Exit criteria

- **M1:** every P screen (legal pages, owner login/MFA, dashboard, series + autopilot, story review, render and video review, channels, YouTube publish) works against the real API and mock-mpt; the primary journey is green in e2e; vi/en complete; axe clean on the pages the journeys drive. There is no second milestone for billing or admin: those screens are deleted with phase S (DEC 10.14).
- **TikTok (P · M1):** the post screen passes the internal checklist of every required item; per the 07 audit caveat the post stays private-only until the audit passes.

## 10. Open questions and contract change requests

- [DECISION recorded in 10.3.1] Public URLs use `/vi/…` and `/en/…` path prefixes.
- ✅ Closed (10 CR-4): zoneless default, Signal Forms and `httpResource` stable, Vitest default runner (`features/10-web-ui/README.md` FE2–FE5).
- **Contract changes (applied to 00):** `GET /episodes/{id}` includes `nextActions` and the latest render `stage`/`percent`; `RenderProgress` payload defined in 00 §8; `GET /publishing/youtube/eta` for the quota wait state.
- **Contract changes (applied to 00/09):** `POST /hubs/ticket`; codes `rate_limited`, `session_revoked`, `hub_ticket_invalid` and `spend_cap_reached` in 09 §5.4.
- **Withdrawn with phase S (DEC 10.14):** the admin approval routes (settings changes, owner actions, credit-grant requests, payment resolve, signed single-use cancel token) and the codes `captcha_required`, `breached_password`, `setting_change_pending`, `owner_action_pending`, `grant_requires_approval` no longer have a web consumer; no `/admin` chunk and no account-cancel or report page exist in this app. The only public token pages left are the autopilot-veto e-mail pages `/publications/cancel` and `/publications/approve` (10.7.4-T2, plan 06 keeps the veto), which are unrelated to the withdrawn governance approvals. Closed registration is a backend answer only (403 `registration_closed`, 09.3.1), so no screen is planned for it.
- **TikTok is P, not a release (DEC 10.14 + `00-overview.md` §4.1):** this plan tags 10.7.3 as P · M1. The peer files that still say "P · R2" — `features/10-web-ui/10.7.3-tiktok-post/plan.md:1,236-238` and the "Part C" tables of `features/10-web-ui/README.md` — need the same re-tag; that is a hand-off, not an edit made here.
- **Hand-over from plan 03 (2026-09-24, read before detailing this plan).** Plan 03 is owner-signed and its task files already name the task ids below. Keep those ids, or update the `Depends on` / `Blocks` cells in plan 03 when they change. Contract: `features/03-image-generation/README.md` D1–D14.
  - [ ] 10.4.2-T1 style picker (CR-7):
    - Copy the labels from `seed/i18n-style-labels.v1.json` (03.1.1-T1) into the vi/en bundles as `series.style.<name>.label` / `.hint`.
    - Call `GET /style-templates?topic=` so a `true_crime` series only sees `true_crime` styles.
    - Empty state: when there is no preview image, show the label only (03.1.2-T1).
  - [ ] 10.5 video review / render states (CR-7):
    - Map the 409 `keywords_flagged` `errors[] { segment, index, rule }` to red chips and disable "Make video".
    - Show the advisory hints `scenes_trimmed` / `scenes_sparse`.
    - Show the skipped-scenes banner from `latestRenderJob.images.skipped`, with "Edit scenes".
    - Add copy for `image_moderated` (names the scene, no retry) and for `image_generation_failed`, including its detail `provider_funds_exhausted`.
    - Dev states come from mock-mpt `MOCK_IMAGE_REPORT` (03.4.3-T2).

## 11. Estimate summary

| Epic | Days |
|---|---|
| 10.1 Workspace + guardrails | 4.5 |
| 10.2 Core (10 CR-31: +0.5 `10.2.4-T0`) | 4.25 |
| 10.3 Legal + auth (10 CR-31: +1.0 `10.3.3-T0`; the marketing/register/CAPTCHA tasks are deleted by DEC 10.14) | 3.5 |
| 10.4 Dashboard + series + autopilot (10 CR-31: +1.0 `10.4.1-T0`) | 5.0 |
| 10.5 Story review (10 CR-31: +1.0 `10.5.1-T0` +1.0 `10.5.4-T0`) | 14.25 |
| 10.6 Render + video review (10 CR-31: +0.5 `10.6.1-T0`) | 3.0 |
| 10.7 Publishing + channels (10 CR-31: +0.5 `10.7.1-T0` +1.0 `10.7.2-T0`; 10 CR-24: +0.5 `10.7.3-T0`) | 7.0 (incl. 2.5 TikTok) |
| 10.10 Quality (one journey task with axe assertions; the separate accessibility-pass and SaaS-journey tasks are deleted by DEC 10.14) | 2.0 |
| **Total** | **43.5** → with 20 % buffer **52.2** person-days (of which TikTok 2.5 raw (P, M1); recomputed after DEC 10.14 from the remaining rows only — P 43.5 raw. Before the drop the plan read 61.0 / 73.2 with P 41.0 + S 20.0; removed: epic 10.8 billing/account 5.5, epic 10.9 admin 7.75, 10.3.1-T0 + 10.3.1-T2 + 10.3.2-T1 + 10.3.3-T2 2.25, and 10.10.1-T1 + 10.10.1-T2 + 10.10.2-T2 2.0, i.e. 17.5 raw; 61.0 − 17.5 = 43.5. Earlier notes that stand: 10 CR-4 (was 45.25 / 54.3), 10 CR-14 (+1.5, Part B §B9) and the 10 CR-22/10 CR-31 design-first retrofit, of which 9 of the 12 `-T0` tasks remain (the 10.3.1, 10.8.1 and 10.9.1 decks are deleted)) |
