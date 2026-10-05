# 10.4 — Dashboard, series, voice picker and autopilot tab (detailed plan)

> Refines: `../../../10-web-ui.md` §4 epic 10.4 — stories 10.4.1 (L110–112), 10.4.2 (L114–118), 10.4.3 (L120–122); FR-10-3; `../README.md` §3 **D1–D18** (contract), §4 CR-7/CR-11, §5 sequencing, §8 task mapping, C12–C14, O1.
> Upstream contracts: `../../09-backend-api/09.4.1-series-crud/plan.md`, `…/09.4.2-5.3-queries/09.4.2-T1-voices-style-templates.md`, `../../02-story-writing/02.8.3-cadence-publishing/02.8.3-T2-autopilot-endpoints-authorization.md`, `…/02.8.3-T4-pause-triggers-spend-caps-alerts.md`, `../../02-story-writing/02.8.2-auto-approval-gates/02.8.2-T3-thresholds-dry-run-calibration.md`, `../../02-story-writing/02.8.1-idea-generator/plan.md` §6, `../../03-image-generation/03.1-style-system/plan.md` §5–§6, `../../04-voice-proxy/README.md` CR-11, `../../../00-overview.md` §6/§8.
> Legend: ✅ read from a file/URL with `path:line` · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet. Frontend only — no backend file is edited by this folder.

## 1. Scope

**In scope (5 tasks, 4.0 d raw / 4.8 d buffered).**

1. `10.4.1-T1` — Dashboard overview: ruled "needs your review" / "in progress" / "ready to publish" rows over five scalar-status `GET /episodes` calls; no credit UI in P (D14).
2. `10.4.2-T2` — Voice picker with optional preview audio (`GET /voices?language=`), tags as `voices.tag.<tag>` i18n keys, no speed control (04 CR-11).
3. `10.4.2-T1` — Series list (cursor, archive) + 4-step create/edit editor + visual-style grid (`GET /style-templates?topic=`, 03 CR-7), true-crime notice and topic-lock behaviour.
4. `10.4.3-T1` — Autopilot tab: settings form, on/off with versioned authorization dialog, re-auth, pause/resume, topic lock, next-slots preview, pause banner.
5. `10.4.3-T2` — Run log (idea picked + rejected ideas, gate verdicts, state/outcome/pause reason), calibration panel (`calibrationReport`, `gatesDryRun*`), veto-window list (behind flag `youtube_publish`).

**Out of scope (owned elsewhere).** Episode list/detail/editor/story review (10.5.x); auth, TOTP, re-auth dialog internals (10.3.3-T3 owns `ReauthDialog`); app shell, nav, language switcher (10.2.6-T1); realtime transport and resync (10.2.4-T1); error mapper and `ConflictDialog` (10.2.2-T1); design system and shared components (10.1.3-T1); API client generation (10.1.4-T1); credits/quote/buy and `CreditBalanceChanged` (12, S — D14 forbids any amount in P); calendar/scheduling generation (02.8.3 backend); gate thresholds *config* (`AutopilotGateOptions`, 02.8.2-T3) — the UI only selects the `gate_thresholds_ref` profile (`default`/`strict`) and *reads* the calibration report; TTS proxy and preview-file production (04, backend); YouTube OAuth/consent mechanics (06.2.2-T3).

**Explicitly not built in P:** `AutopilotRunUpdated` SignalR event (`02.8.1/plan.md` §5 [OPEN]) — the run log polls instead (D17); series-level `GET /series/{id}/publications` (see §5 [OPEN]) — the veto list hydrates publications per episode; a live video preview player in the veto row (wave 2 owns playback).

## 2. Confirmed facts

Legend: ✅ = read with citation; ⚠️ = unverified; [DECISION]/[OPEN]/[INFERENCE] as defined in `../README.md` §3.

**Contract (frontend workspace).**

| # | Fact | Source |
|---|---|---|
| F1 | Angular 22.2.x, standalone, `strict` + `strictTemplates` + `noUncheckedIndexedAccess`, SCSS, zoneless, `@angular/build:application`, `outputMode: 'static'`, Node 26.10.0 (10 CR-43), path aliases `@core/*` `@shared/*` `@features/*`. | ✅ `../README.md:102` |
| F2 | Feature tree `features/<name>/{domain,data-access,application,ui,feature}` + `<name>.routes.ts` + `index.ts`; wave-1 features `public, auth, dashboard, series, episodes`; lint bans `domain` → Angular/rxjs, `ui` → store/repository/HttpClient, non-`data-access` → `@shared/api`, cross-feature import other than through `index.ts`, `any`, non-`OnPush`, `subscribe()` in components, `[innerHTML]`, `localStorage`. | ✅ `../README.md:109` |
| F3 | Only `features/*/data-access/**` and `core/**/data-access` import `@shared/api`; repositories return `Promise<T>`; `withIfMatch(etag)`/`readEtag(response)` in `core/http/`; `IdempotencyKeys` (10.2.3-T1); `shared/util/cursor-page.ts` + `core/state/cursor-list.ts` for every list (both owned by **10.2.6-T1**, WR4). | ✅ `../README.md:133` |
| F4 | `AppErrorMapper` → `AppError { code, status, fieldErrors, retryAfterSec?, traceId?, problem }`; every code used by wave-1 screens has `errors.<code>` in vi + en; `detail` never shown raw; 412 → `ConflictDialog`; 429 → `RetryCountdown`; 428 is a client bug. | ✅ `../README.md:159` |
| F5 | Transloco, `vi` (default) + `en`, one scope per feature (`dashboard`, `series`, …) + `core` + `errors`, files `web/src/assets/i18n/<scope>/<lang>.json`, key style `scope.area.item`; cross-plan keys are used **verbatim**. | ✅ `../README.md:166` |
| F6 | `RealtimeService` (D8) typed per-event signals, `RealtimeService.subscribeEpisode(episodeId, handler)` (WR7), `resync` on (re)connect and on visible; `RenderProgress` updates its signal directly. | ✅ `../README.md:153` |
| F7 | `core/session/activity.ts` `isActive()` (owner/admin idle window 25 min) gates refresh and polling; `visible-poll.ts` (`intervalMs`, visible + active, single in-flight) is used by the autopilot run log at 30 s and by nothing else in wave 1. | ✅ `../README.md:148`, `:190` |
| F8 | Wave 1 has **no** credit amounts, quotes, balance or buy buttons; `ConfirmDialog` has a `cost-line` projection slot that S fills; `GET /me` gains `features: string[]` (`source_finder`, `youtube_publish`) read via `core/session.hasFeature(key)`; a flag switched off answers 404 `feature_disabled` → the block hides and refetches `/me`. | ✅ `../README.md:180` |
| F9 | `Stepper`, `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `StatusChip`, `ConfirmDialog`, `ConflictDialog`, `RetryCountdown`, `CharCounter`, `CopyableCode`, `ReauthDialog` exist as shared components (D11; `CharCounter`/`CopyableCode` are 10.1.3-T1's, `ConflictDialog`/`RetryCountdown`/`bindFieldErrors` 10.2.2-T1's — WR4) (inputs/outputs only, OnPush, component test); Material M3 dark default + light option. | ✅ `../README.md:168` |
| F10 | Vitest via `@angular/build:unit-test` + jsdom; hand-written fakes implementing the domain ports (`features/*/testing/fake-*.repository.ts`), no mocking library; Playwright e2e in `web/e2e/` with `@axe-core/playwright` per page and the CSP fixture; `web.yml` runs lint → i18n → api:gen drift → unit → build → gzip budget → gen-csp → e2e. | ✅ `../README.md:182` |
| F11 | D16 a11y baseline applies to every task (labels, focus, `aria-live`, `aria-describedby`, contrast, no colour-only, ≥ 24 px targets, 360 px, `prefers-reduced-motion`). | ✅ `../README.md:188` |
| F12 | Every component `OnPush`; `@for` with `track`; `@defer (on viewport|on interaction)` for heavy parts (diff view, QR code, **audio preview**); every feature lazy; gzip budget 250 kB initial. | ✅ `../README.md:172` |
| F14 | Rulings that bind this folder (README §10): `RealtimeService` consumers use `subscribeEpisode(episodeId, handler)` (WR7); poll/tick specs advance Playwright `page.clock` and the idle gate is unit-tested through a fake `ActivityTracker` port, never an in-app hook (WR14); the veto countdown tick is **1 minute** and visibility-gated (WR16); no test asserts template source (WR16). | ✅ `../README.md:350`, `:357`, `:359` |
| F13 | `SegmentChips`/`segment-address.ts` (`title|hook|p:<i>|ending|meta`) is the single segment-addressing implementation; the autopilot run-log deep link uses `/episodes/:id?segment=p:3`; the helper is owned by **10.5.3-T1** (WR5). | ✅ `../README.md:192` |

**Backend contracts this folder consumes.**

| # | Fact | Source |
|---|---|---|
| B1 | Series routes: `POST /api/v1/series` → 201 + `Location` + `ETag`; `GET /api/v1/series?cursor=&limit=&includeArchived=` → 200 `{ items, nextCursor }`; `GET /api/v1/series/{id}` → 200 + `ETag`; `PATCH /api/v1/series/{id}` → 200 `{ etag }` + `ETag`, `RequireIfMatch()` (428), JSON merge semantics; `DELETE /api/v1/series/{id}` → 204 (archive). | ✅ `09.4.1/plan.md:72-76` |
| B2 | `SeriesDto(Id, Title, Topic, Language, Aspect, VoiceId, StyleTemplateId, StoryPreset, AutopilotEnabled, ArchivedAt, CreatedAt, UpdatedAt, Etag)`; list item omits `StoryPreset`. ETag `"se-<xmin>"`; `CreateSeriesCommand(Title 1–120 trimmed, Topic, Language, Aspect, VoiceId, StyleTemplateId, StoryPreset?)`. | ✅ `09.4.1/plan.md:33-36`, `:78` |
| B3 | Series error codes: `validation_failed` (fields `title`, `voiceId`, `styleTemplateId`, `language`, `limit`), `voice_language_mismatch`, `series_conflict` (412), `series_archived` (409), `series_topic_locked` (409), `concurrency_conflict` (409), 428 missing `If-Match`. `language` is immutable after creation. | ✅ `09.4.1/plan.md:47`, `:50-51`, `:88-89`, `:101`, `09.4.1-T1:77-89` |
| B4 | `GET /api/v1/voices?language=` → `200 { items: [...] }`, item `{ id, displayName, language, gender, tags, previewUrl }`, `previewUrl` `null` when the storage key is absent/unsafe; `Cache-Control: public, max-age=<Catalog:PublicCacheSeconds>`; `ETag` + `If-None-Match` → 304; `language=xx` → 400 `validation_failed` `errors.language`; 401 without a bearer token. | ✅ `09.4.2-T1:34-35`, `:93`, `:96`, `:117-123` |
| B5 | Voice tag vocabulary (closed, 11 values): `storyteller, natural, news, deep, warm, calm, bright, whisper, northern, central, southern`; i18n keys `voices.tag.<tag>` in vi + en (04 CR-11). The speaking rate is **not** adjustable in P — no rate control in the UI. | ✅ `04-voice-proxy/README.md:213`, `:222`, `:284` |
| B6 | `GET /api/v1/style-templates?topic=` → `200 { items: [...] }`, item `{ id, name, topicHint, previewUrl }`; only `active` rows; `topic=true_crime` returns only `true_crime` hints, fiction topics exclude them; unknown topic → 400 `validation_failed` `errors.topic`; same cache/ETag/304 rules; 401 without a token. | ✅ `09.4.2-T1:36`, `:94`, `:120-124` |
| B7 | Style labels/hints are i18n keys `series.style.<name>.label` / `.hint` (vi + en) supplied by 03.1.1-T1's seed; template names include `dark-folklore-ink`, `foggy-cinematic`, `vintage-photo-grain`, `watercolor-night`, … (7 rows, exactly 2 `true_crime`). True-crime notice: "Chỉ các phong cách minh hoạ không có khuôn mặt người thật" / "Only illustration styles without real faces", key `series.style.trueCrimeNotice`. Style picker a11y: every preview `<img>` has `alt` = the label; the grid is a radio group. | ✅ `03.1-style-system/plan.md:88`, `:93-95`; `03.1.1-T1:31-36`, `:45-48`, `:67` |
| B8 | Style/topic policy: a `true_crime` series needs a `true_crime` template; a fiction series may not use one — enforced server-side (400 field `styleTemplateId`) and used as the query filter. | ✅ `03.1-style-system/plan.md:81-82`; `09.4.1-T1:47` |
| B9 | Autopilot routes (all `youtube_publish`-flagged, 404 `feature_disabled` when off): `GET /series/{id}/autopilot` → 200 `AutopilotDto` (includes the **rendered** authorization text + `textHash`); `PUT /series/{id}/autopilot` (If-Match required 428; enabling needs a current authorization + re-auth) → 200 `AutopilotEtag`; `POST /series/{id}/autopilot/authorization` → 201 `{ version, acceptedAt }`; `DELETE …/authorization` → 204; `POST …/pause` (body `{ note? }`) → 204; `POST …/resume` → 204; `GET …/runs` → 200 `{ items, nextCursor, calibrationReport? }`, cursor `(slot_at DESC, id DESC)`, `limit ≤ 100`. | ✅ `02.8.3-T2:24-30` |
| B10 | `AutopilotDto(Enabled, Settings, PausedAt, PauseReason, Authorization?, AuthorizationText, RequiredAuthorization, Channel, GatesDryRun, GatesDryRunRemaining, NextSlots[])`; `AutopilotSettingsDto(Cadence, DailyCap, BufferDays, Privacy, VetoWindowHours, PreAuditMode, PreAuditPauseAfter, GateThresholdsRef, Tone, BannedThemes[], RenderLeadMinutes?, StoryLeadMinutes?)`; `CadenceDto(DaysOfWeek[], PublishTime, TimeZone)`; `AuthorizationDto(Version, TextVersion, TextHash, AcceptedAt)`; `AuthorizationTextDto(TextVersion, Text, TextHash, Locale)`; `NextSlotDto(SlotAt, StartAt, State)`; `AutopilotRunDto(Id, SlotAt, StartAt, State, Outcome?, PauseReason?, Step?, Attempt, RenderAttempts, WaitingFor?, IdeaId?, EpisodeId?, PublicationId?, Gates?, AuthorizationId?, FinishedAt?, LastTransitionAt)`; `GateSummaryDto`. | ✅ `02.8.3-T2:37-52` |
| B11 | Enable rule order: re-auth within 5 min (else 401 `reauth_required`) → settings valid (400 `autopilot_settings_invalid`, `errors` keyed by JSON path) → current authorization matching the rendered hash (else 409 `autopilot_authorization_required`) → `active` channel connection (else 409 `channel_reauth_required`) → topic unchanged. Disable revokes (`revoked_reason = disabled`) and needs no re-auth. Covered fields (a change revokes): `topic`, `default_channel_id`, `cadence.*`, `daily_cap`, `privacy`, `veto_window_hours`, `pre_audit_mode`; not covered: `tone`, `banned_themes`, `gate_thresholds_ref`, `render_lead_minutes`, `story_lead_minutes`, `title`. | ✅ `02.8.3-T2:79-88` |
| B12 | Accept: `textVersion` + `textHash` must match exactly; mismatch → 409 `autopilot_authorization_required` with `errors["textHash"] = "stale_text"`. `PATCH /series/{id}` with a topic change while enabled → 409 `autopilot_topic_locked`; archive while enabled disables + revokes. | ✅ `02.8.3-T2:79`, `:89-90` |
| B13 | `AutopilotEtag` is returned as the `ETag` header **and** in the body so the UI needs no second GET (⚠️ [INFERENCE] in the backend task; the UI treats the body value as authoritative and falls back to the header). | ✅ `02.8.3-T2:182` |
| B14 | Copy keys supplied by 02.8.3 §6 (verbatim for this UI): `autopilot.settings.cadence_days`, `.publish_time`, `.time_zone`, `.daily_cap`, `.buffer_days`, `.privacy`, `.veto_window`, `.pre_audit_mode`; `autopilot.authorization.required`, `.accepted_version`, `.stale_text`; `autopilot.topic_locked`; `autopilot.pause.banner`, `autopilot.resume.blocked`; `autopilot.next_slots.title`. Cadence weekday chips are a `fieldset`/`role="group"`; the time-zone select is searchable and labelled; the authorization dialog traps focus, shows the full text in a scrollable region with a heading, and only the accept button is enabled after the text has been scrolled to the end (10.4.3-T1 owns the markup). | ✅ `02.8.3/plan.md §6` |
| B15 | Pause reasons (`AutopilotPauseReason`, 15 values): `gate_failed_streak, spend_cap_reached, run_cost_cap_reached, provider_funds_exhausted, channel_not_active, oauth_client_incident, youtube_quota_exhausted, upload_failed, video_rejected, veto_notice_undelivered, repeated_run_failures, slot_missed, pre_audit_private_limit, content_reported, owner_paused`; copy keys `autopilot.pause.*`. Resume re-checks every trigger and refuses with 409 `autopilot_pause_reason_active` + `errors.reasons = [...]` while a reason still holds ([OPEN] new code, plan §10 item 4). Pause is idempotent; the manual pause sends no e-mail. | ✅ `02.8.3-T4:39`, `:100`, `:200` |
| B16 | Run-log states (02.8.2 plan §6): `planned · producing · waiting_owner (amber) · gate_failed (red) · published (green) · published_private_locked (grey + lock) · cancelled_by_owner · paused · failed`; `autopilot_runs.outcome` per 00 §6. Gate verdict detail from `gate_results` (`gates.v1`): per check `id`, `value`, `threshold`, `pass`, with a `dry_run` badge when `mode = dry_run`, failing checks first. Copy keys `autopilot.run.state.*`, `autopilot.gate.failed.<checkId>`. | ✅ `02.8.2/plan.md:237-248`; `00-overview.md:277` |
| B17 | Idea rows: picked idea `{ title, logline, hook, structure{…}, score, similarity, status }`; `otherIdeas[]` `{ title, score, similarity, rejectedReason }`; copy keys `autopilot.ideas.picked`, `.rejected ({n})`, `.reason.noveltyLexical|noveltyJudge|bannedTheme|moderated|templateRepeat|tripleRepeat|notBest|invalid`, `.none`, `.requiresResearch`, `.similarity`; the run row is a `<table>` with a caption, reasons are text never colour-only, `similarity` is a percentage with the raw value in `title`. | ✅ `02.8.1/plan.md:250-274` |
| B18 | Calibration: `GET …/autopilot/runs` adds an optional `calibrationReport: GateCalibrationReportDto` when the series has ≥ 1 `mode = dry_run` verdict; the report has `checkStats`, `reviewScoreHistogram`, `similarityHistogram`, `falseBlockRate`, `potentialFalseApproveRate`, `noOwnerVerdictCount`, `recommendationLines`, `evaluated`/`target`/`ready`; `gatesDryRun` + `gatesDryRunRemaining` drive a banner; the field never carries story text/titles/descriptions/source URLs; if the ground-truth query fails the field is omitted (the list still returns) → the UI shows "calibration data unavailable". | ✅ `02.8.2-T3:94`, `:113-116`, `:164-168`, `:203` |
| B19 | Publication routes (00 §8): `GET /episodes/{id}/publications`, `POST /episodes/{id}/publications`, `POST /publications/{id}/consent`, `PATCH /publications/{id}` (If-Match; title/description/tags/privacy/publish_at, only in `awaiting_consent` or `scheduled` before upload; re-runs validation and moderation), `DELETE /publications/{id}`, `POST /publications/cancel-by-link`. `publications` columns include `status`, `privacy`, `publish_at`, `consent_at`, `consent_text_version`, `consent_source`, `autopilot_authorization_id`, `veto_notice_sent_at`. Publication status graph: `awaiting_consent → scheduled → uploading → processing → published | failed | cancelled`. | ✅ `00-overview.md:290`, `:342`, `:362` |
| B20 | Episode statuses used by the dashboard: `story_review`, `video_review`, `story_generating`, `rendering`, `ready` (+ `scheduled`, `published` after publication). Episode list is `GET /episodes` with a status filter (10.5.1-T1). | ✅ `10-web-ui.md:111`; `00-overview.md:335-337` |
| B21 | `GET /me` gains `features: string[]` = enabled keys of the allow-list (`source_finder`, `youtube_publish`); `core/session.hasFeature(key)`. | ✅ `../README.md:180`; CR-10 `../README.md:216` |

**Gaps.** No series-level publications query exists (B19 is episode-scoped) → §5 [OPEN-1]. `AutopilotRunUpdated` does not exist in P → polling (§7). `AutopilotEtag` body shape is [INFERENCE] upstream → the UI handles both header and body. The 3 remaining style-template names are not quoted in the files read here → they are **not** hard-coded in the UI (labels come from `series.style.<name>.label`, the grid iterates the API response) ✅ `03.1-style-system/plan.md:88`.

## 3. Domain and data

No database. `web/src/app/features/<name>/domain/` holds pure TypeScript (no `@angular/*`, no `rxjs` — D2 lint).

### 3.1 Domain models

`features/series/domain/series.model.ts`
```ts
export type Topic = 'ghost' | 'spiritual' | 'true_crime';
export type Language = 'vi' | 'en';
export type Aspect = 'landscape' | 'portrait';

export interface Series {
  id: string; title: string; topic: Topic; language: Language; aspect: Aspect;
  voiceId: string; styleTemplateId: string; storyPreset: string | null;
  autopilotEnabled: boolean; archivedAt: string | null;
  createdAt: string; updatedAt: string; etag: string;      // "se-<xmin>", wrapped by the repo
}
export interface SeriesListItem extends Omit<Series, 'storyPreset' | 'etag'> {}

export interface Voice {                                              // 09.4.2-T1 voice item
  id: string; displayName: string; language: Language; gender: string;
  tags: readonly VoiceTag[]; previewUrl: string | null;               // null → no play control (B4/B5)
}
export type VoiceTag = 'storyteller' | 'natural' | 'news' | 'deep' | 'warm' | 'calm'
  | 'bright' | 'whisper' | 'northern' | 'central' | 'southern';        // closed, 11 values (B5)

export interface StyleTemplate {                                      // 09.4.2-T1 style item
  id: string; name: string; topicHint: Topic | null; previewUrl: string | null;
}
```
`features/series/domain/autopilot.model.ts`
```ts
export type PublishPrivacy = 'public' | 'unlisted' | 'private';
export type PreAuditMode = 'normal' | 'pause_after_n_private';
export type GateThresholdsRef = 'default' | 'strict';
export type AutopilotRunState = 'planned' | 'producing' | 'waiting_owner' | 'gate_failed'
  | 'published' | 'published_private_locked' | 'cancelled_by_owner' | 'paused' | 'failed';
export type AutopilotRunOutcome = 'published' | 'published_private_locked' | 'waiting_owner'
  | 'gate_failed' | 'cancelled_by_owner' | 'awaiting_authorization' | 'paused' | 'failed';
export type AutopilotPauseReason = 'gate_failed_streak' | 'spend_cap_reached'
  | 'run_cost_cap_reached' | 'provider_funds_exhausted' | 'channel_not_active'
  | 'oauth_client_incident' | 'youtube_quota_exhausted' | 'upload_failed' | 'video_rejected'
  | 'veto_notice_undelivered' | 'repeated_run_failures' | 'slot_missed'
  | 'pre_audit_private_limit' | 'content_reported' | 'owner_paused';

export interface Cadence { daysOfWeek: readonly string[]; publishTime: string; timeZone: string }
export interface AutopilotSettings {
  cadence: Cadence; dailyCap: number; bufferDays: number; privacy: PublishPrivacy;
  vetoWindowHours: number; preAuditMode: PreAuditMode; preAuditPauseAfter: number;
  gateThresholdsRef: GateThresholdsRef; tone: string; bannedThemes: readonly string[];
  renderLeadMinutes: number | null; storyLeadMinutes: number | null;
}
export interface AutopilotAuthorization { version: number; textVersion: string; textHash: string; acceptedAt: string }
export interface AuthorizationText { textVersion: string; text: string; textHash: string; locale: Language }
export interface NextSlot { slotAt: string; startAt: string; state: string }
export interface Autopilot { enabled: boolean; settings: AutopilotSettings; pausedAt: string | null;
  pauseReason: AutopilotPauseReason | null; authorization: AutopilotAuthorization | null;
  authorizationText: AuthorizationText; requiredAuthorization: boolean;
  channel: { id: string; platform: string; displayName: string; status: string } | null;
  gatesDryRun: boolean; gatesDryRunRemaining: number; nextSlots: readonly NextSlot[]; etag: string }
export interface GateCheck { id: string; value: number; threshold: number; pass: boolean }
export interface GateSummary { story: { mode: 'dry_run' | 'live'; checks: readonly GateCheck[]; failures: readonly string[] };
  video: { mode: 'dry_run' | 'live'; checks: readonly GateCheck[]; failures: readonly string[] } }
export interface AutopilotRun { id: string; slotAt: string; startAt: string; state: AutopilotRunState;
  outcome: AutopilotRunOutcome | null; pauseReason: AutopilotPauseReason | null; step: string | null;
  attempt: number; renderAttempts: number; waitingFor: string | null; ideaId: string | null;
  episodeId: string | null; publicationId: string | null; gates: GateSummary | null;
  authorizationId: string | null; finishedAt: string | null; lastTransitionAt: string }
export interface IdeaSummary { id: string; title: string; logline: string; hook: string; score: number;
  similarity: number; status: 'proposed' | 'picked' | 'rejected' | 'used'; rejectedReason: string | null;
  requiresResearch: boolean }
export interface CalibrationReport { checkStats: readonly { id: string; pass: number; fail: number }[];
  reviewScoreHistogram: readonly { bucket: number; count: number }[];
  similarityHistogram: readonly { bucket: number; count: number }[];
  falseBlockRate: number; potentialFalseApproveRate: number; noOwnerVerdictCount: number;
  recommendationLines: readonly string[]; evaluated: number; target: number; ready: boolean }
```

`features/series/domain/publication.model.ts` — the veto row is a *view model* built from `publicationStatus.vetoRow`:
```ts
export type PublicationStatus = 'awaiting_consent' | 'scheduled' | 'uploading' | 'processing'
  | 'published' | 'failed' | 'cancelled';
export interface Publication { id: string; episodeId: string; platform: string; status: PublicationStatus;
  title: string; privacy: PublishPrivacy; publishAt: string; consentAt: string | null;
  consentSource: 'user' | 'autopilot' | null; etag: string;
  preAudit: boolean; requestedPrivacy: 'public' | 'unlisted' | 'private' | null }   // 06 CR-11: the edit dialog disables privacy when preAudit (409 publication_privacy_locked is the server guard)
export interface VetoRow { publication: Publication; episodeTitle: string; vetoEndsAt: string; // publishAt
  remainingMs: number; runId: string | null }
```

### 3.2 Ports (abstract classes, implemented in `data-access`)

```ts
// features/series/domain/series.repository.ts
export abstract class SeriesRepository {
  abstract list(q: { cursor?: string; limit?: number; includeArchived?: boolean }): Promise<CursorPage<SeriesListItem>>;
  abstract get(id: string): Promise<Series>;
  abstract create(input: CreateSeriesInput): Promise<{ id: string; etag: string }>;
  abstract patch(id: string, ifMatch: string, patch: SeriesPatchInput): Promise<{ etag: string }>;
  abstract archive(id: string): Promise<void>;
  abstract getAutopilot(id: string): Promise<Autopilot>;
  abstract putAutopilot(id: string, ifMatch: string, settings: AutopilotSettings, enabled: boolean): Promise<{ etag: string }>;
  abstract acceptAuthorization(id: string, body: { textVersion: string; textHash: string }): Promise<{ version: number; acceptedAt: string }>;
  abstract revokeAuthorization(id: string): Promise<void>;
  abstract pause(id: string, note?: string): Promise<void>;
  abstract resume(id: string): Promise<void>;
  abstract listRuns(id: string, q: { cursor?: string; limit?: number }): Promise<CursorPage<AutopilotRun> & { calibrationReport?: CalibrationReport }>;
}
// features/series/domain/catalog.repository.ts
export abstract class CatalogRepository {
  abstract voices(language: Language): Promise<readonly Voice[]>;            // ETag-aware (304 → cached list)
  abstract styleTemplates(topic?: Topic): Promise<readonly StyleTemplate[]>;
}
// features/series/domain/publication.repository.ts
export abstract class PublicationRepository {
  abstract listForEpisode(episodeId: string): Promise<readonly Publication[]>;
  abstract patch(id: string, ifMatch: string, patch: PublicationPatchInput): Promise<{ etag: string }>;
  abstract consent(id: string, ifMatch: string, consentTextVersion: string): Promise<{ etag: string }>;   // 06 D7/YR9: If-Match + pc.v1 version (10 CR-20)
  abstract cancel(id: string, ifMatch: string): Promise<void>;                                            // 06 D7 (10 CR-20)
}
```
`features/dashboard/dashboard.model.ts` (feature root: the D2 lint rule bars `domain/` from importing another feature) re-uses `EpisodeListItem` + `EpisodesRepository` (as-built names of `EpisodeSummary`/`EpisodeRepository`) **from `@features/episodes`** (`features/episodes/index.ts` is the only import path — D2). The dashboard adds no repository of its own.

### 3.3 Stores (signal stores, `@Injectable()` per routed page unless noted)

| Store | Path | Signals (all `signal`/`computed`) |
|---|---|---|
| `SeriesListStore` | `features/series/application/series-list.store.ts` | `items`, `nextCursor`, `loading`, `loadingMore`, `error: AppError \| null`, `includeArchived`; uses `core/state/cursor-list.ts` for `loadMore()` + in-flight guard |
| `SeriesEditorStore` | `…/series-editor.store.ts` | `mode: 'create' \| 'edit'`, `step: 1\|2\|3\|4`, `form` (typed Reactive Form, owned by the component, the store holds the value signal), `voices`, `templates`, `topic`, `language`, `voiceId`, `styleTemplateId`, `saving`, `error`, `baselineJson`, `isDirty: computed(...)` (D18 pattern: 150 ms debounce) |
| `AutopilotStore` | `…/autopilot.store.ts` | `autopilot: signal<Autopilot \| null>`, `etag`, `saveState: 'idle'\|'dirty'\|'saving'\|'saved'\|'error'`, `error`, `reauthPending`, `authorizationDialogOpen`, `scrolledToEnd` |
| `RunLogStore` | `…/run-log.store.ts` | `runs`, `nextCursor`, `calibrationReport: signal<CalibrationReport \| null>`, `calibrationUnavailable`, `loading`, `loadingMore`, `error`, `lastRefreshedAt`; subscribed through `RealtimeService.subscribeEpisode(episodeId, handler)` for the episodes in the list (D8, WR7) and driven by `visible-poll` (§7) |
| `VetoListStore` | `…/veto-list.store.ts` | `rows: computed<VetoRow[]>`, `tick: signal<number>` (1-minute ticker, visible + `isActive()` gated — WR16, drives `remainingMs` **without** a request), `busyIds`, `error` |
| `DashboardStore` | `features/dashboard/application/dashboard.store.ts` | `needsReview`, `inProgress`, `ready`, `loading`, `error: AppError \| null`, `lastRefreshedAt` |

Realtime: feature stores never open a hub connection; they call `RealtimeService.subscribeEpisode(episodeId, handler)` (or the typed event signal for `RenderProgress`) per WR7/D8. `RunLogStore` is the only store here with a live subscription; it registers `reset()` through `SESSION_SCOPED` (D6) so logout clears it.

Vitest fakes: `features/series/testing/fake-series.repository.ts`, `fake-catalog.repository.ts`, `fake-publication.repository.ts`, `features/dashboard/testing/fake-episode.repository.ts` — hand-written, no mocking library (D15).

## 4. Use cases

Each use case states trigger → API → states → errors. All errors go through `AppErrorMapper` (D9).

| # | Use case | API | UI states | Errors / handling |
|---|---|---|---|---|
| U1 | Open `/dashboard` | 3 parallel `GET /episodes?status=…&limit=…` (see §5) | skeleton (`LoadingState`) → 3 groups; empty group → inline `EmptyState`; whole-page error → `ErrorState` with Retry | `network_error` → `ErrorState`; `rate_limited` → `RetryCountdown`; unknown → `unknown_error` + `traceId` |
| U2 | Browse `/series` | `GET /series?cursor=&limit=25` | list of cards (title, topic chip, language, aspect, `StatusChip` archived), "Load more" via `cursor-list` | 400 `validation_failed` (limit) → generic; 401 → session end (D6) |
| U3 | Archive a series | `DELETE /series/{id}` after `ConfirmDialog` | row leaves the list optimistically, rolled back on failure | 404 → remove the row + toast; 409 `series_archived` → idempotent success |
| U4 | Create a series (4 steps) | step 3 `GET /voices?language=`; step 4 `GET /style-templates?topic=`; submit `POST /series` | `Stepper` 1 topic+language → 2 aspect → 3 voice → 4 style; Next disabled until the step is valid; submit → 201 + `Location` → navigate to `/series/:id` | 400 field errors → `bindFieldErrors` (fields `title`, `voiceId`, `styleTemplateId`, `language`); `voice_language_mismatch` → alert + voice cleared; unknown → alert with `traceId` |
| U5 | Pick a voice with preview | `GET /voices?language=` (ETag/304) | radio list grouped by gender; tag chips render `voices.tag.<tag>`; a play button **only** when `previewUrl !== null`, `@defer (on interaction)` audio element, one playing at a time; **no** speed control | preview load failure → button disabled + `series.voice.previewUnavailable`; 401 → session end |
| U6 | Language change clears an incompatible voice | none (client rule + server re-check) | changing `language` clears `voiceId`, focuses the voice step, shows `series.voice.clearedByLanguage` | server still validates (`voice_language_mismatch`) — the client rule is a convenience, never the guard |
| U7 | Edit a series | `GET /series/{id}` (+`ETag`), `PATCH` with `If-Match` | same 4 steps pre-filled; `language` readonly with `series.language.locked`; topic readonly while `autopilotEnabled` with `autopilot.topic_locked`; Save → `saving` → `saved` (`aria-live`) | 412 `series_conflict` → `ConflictDialog` (Reload latest / Save mine as new version / Cancel); 428 → console + generic (client bug); 409 `series_archived` → banner + archive chip; 409 `series_topic_locked` → topic reverted + hint |
| U8 | Open the Autopilot tab | `GET /series/{id}/autopilot` | settings form + authorization block + next slots + pause banner | 404 `feature_disabled` → the tab hides the autopilot section, refetches `/me`; 404 series → not-found state |
| U9 | Enable autopilot | `POST /auth/reauth` via `ReauthDialog` (10.3.3-T3) → `PUT /series/{id}/autopilot` `{enabled:true}` with `If-Match` | switch on → re-auth dialog → authorize → accept → `enabled` | 401 `reauth_required` → open `ReauthDialog`, retry **once**; 409 `autopilot_authorization_required` → open the authorization dialog; 409 `channel_reauth_required` → banner with the channel link; 400 `autopilot_settings_invalid` → `bindFieldErrors` from JSON-path keys |
| U10 | Accept / replace the authorization | `POST /series/{id}/autopilot/authorization` `{textVersion, textHash}` | dialog: heading + scrollable text (interpolation only), accept enabled after scroll-to-end, `autopilot.authorization.accepted_version` after 201 | 409 with `errors.textHash = "stale_text"` → `autopilot.authorization.stale_text` + reload `GET`; 401 `reauth_required` → `ReauthDialog` |
| U11 | Change a covered setting while enabled | `PUT` with `If-Match` | after save, `requiredAuthorization = true` → banner `autopilot.authorization.required` and the switch shows "authorization required" | 412 → `ConflictDialog`; the backend revokes; the UI never assumes it still has consent |
| U12 | Pause / resume | `POST …/pause` `{note?}` / `POST …/resume` | Pause → `ConfirmDialog` (with note) → banner `autopilot.pause.banner`; Resume disabled while a reason holds, tooltip `autopilot.resume.blocked` | 409 `autopilot_pause_reason_active` `errors.reasons = [...]` → banner lists reasons and keeps Resume disabled; 409 `autopilot_authorization_required` / `channel_reauth_required` → same banners as U9 |
| U13 | Read the run log | `GET /series/{id}/autopilot/runs?limit=20` + poll (§7) | `<table>` (caption, one row per run: slot, state chip, outcome, idea picked, `similarity` %, gate badges with `dry_run` badge, render/publication column, pause reason) + "Load more" | 404 `feature_disabled` → hide block + `/me` refetch; partial failure of calibration → `calibrationUnavailable` row, list still shown |
| U14 | Deep link from a run to the story segment | router `navigate(['/episodes', episodeId], { queryParams: { segment: 'p:3' } })` built by `segment-address.ts` | row action "Open episode"; the `segment` param is the one the editor understands (D18) | unknown `step`/`state`/`stage` → the row still renders, the unknown value is ignored (D8) |
| U15 | Read the calibration panel | same response field `calibrationReport` (+ `gatesDryRun`, `gatesDryRunRemaining` banner) | panel above the table: readiness `evaluated/target` + `ready` chip, `checkStats`, the two histograms (`@defer (on viewport)`), false-block / potential-false-approve rates, recommendation lines | omitted field → `calibrationUnavailable` message `autopilot.calibration.unavailable` (never a 0 % claim) |
| U16 | Act on a veto-window publication (flag `youtube_publish`) | rows from the run log + `GET /episodes/{id}/publications`; `PATCH /publications/{id}` (If-Match), `POST /publications/{id}/consent`, `DELETE /publications/{id}` | row: episode title, privacy, `publish_at` **with the series time zone**, live "time left" countdown, actions Edit (dialog: title/description/privacy/publish_at), Cancel (`ConfirmDialog`), Approve now (`ConfirmDialog` + `cost-line` slot left empty in P per D14) | 412 → `ConflictDialog`; 409 episode/publication state codes → refetch + `errors.<code>`; 404 `feature_disabled` → hide the whole veto block + `/me` refetch; `RetryCountdown` on 429 |

## 5. API and events

**Routes consumed (all relative, same origin — D3).**

| Method | Route | Used by | Notes |
|---|---|---|---|
| `GET` | `/api/v1/episodes?status=&cursor=&limit=` | 10.4.1-T1 | one call per scalar status; five parallel calls cover the five P statuses below |
| `GET` | `/api/v1/series?cursor=&limit=&includeArchived=` | 10.4.2-T1 | `{ items, nextCursor }`, limit ≤ 100 (B1) |
| `POST` | `/api/v1/series` | 10.4.2-T1 | 201 + `Location` + `ETag` (B1) |
| `GET` `PATCH` `DELETE` | `/api/v1/series/{id}` | 10.4.2-T1 | `PATCH` needs `If-Match: "se-<xmin>"` (B1/B2) |
| `GET` | `/api/v1/voices?language=` | 10.4.2-T2 | `{ items }`, cacheable, ETag/304 (B4) |
| `GET` | `/api/v1/style-templates?topic=` | 10.4.2-T1 | `{ items }`, cacheable, ETag/304 (B6) |
| `GET` `PUT` | `/api/v1/series/{id}/autopilot` | 10.4.3-T1 | `PUT` with `If-Match`; body `AutopilotEtag` (B9/B13) |
| `POST` `DELETE` | `/api/v1/series/{id}/autopilot/authorization` | 10.4.3-T1 | 201 `{version, acceptedAt}` / 204 (B9) |
| `POST` | `/api/v1/series/{id}/autopilot/pause` \| `…/resume` | 10.4.3-T1 | 204; pause body `{ note? }` (B9) |
| `GET` | `/api/v1/series/{id}/autopilot/runs?cursor=&limit=` | 10.4.3-T2 | `{ items, nextCursor, calibrationReport? }` (B9/B18) |
| `GET` | `/api/v1/episodes/{id}/publications` | 10.4.3-T2 | veto hydration (B19) — see [OPEN-1] |
| `PATCH` `DELETE` | `/api/v1/publications/{id}` | 10.4.3-T2 | `PATCH` with `If-Match`; only `awaiting_consent`/`scheduled` (B19) |
| `POST` | `/api/v1/publications/{id}/consent` | 10.4.3-T2 | "Approve now" (B19) |
| `POST` | `/api/v1/auth/reauth` | 10.4.3-T1 | through `ReauthDialog` (10.3.3-T3) |
| `GET` | `/api/v1/me` | flags | `features[]` → `hasFeature('youtube_publish')` (B21) |

**Request discipline (no N+1).** The present API accepts one scalar `status`, not a comma-separated set. Dashboard therefore issues exactly five bounded calls in one `Promise.all`: `story_review`, `video_review`, `story_generating`, `rendering`, and `ready`; it merges the two review and two in-progress result sets by id and sorts each group by `updatedAt`. No multi-status request or capability guess is allowed. Veto hydration remains one `GET …/publications` **per episode in the veto window only** (bounded by `daily_cap` × veto window), executed in `Promise.all`; a series-level query is requested as [OPEN-1]. Run log remains one call per page.

**[OPEN-1]** `GET /api/v1/series/{id}/publications?status=awaiting_consent` does not exist (00 §8 lists only `/episodes/{id}/publications` and the per-publication consent/PATCH/DELETE routes, `00-overview.md:362`; 02.8.3/plan.md:344 adds no list route either). Request: **CR to 00 §8** to add a series-scoped publications read (cursor). Until then the veto list is derived from the run log — `AutopilotRunDto` carries `EpisodeId`/`PublicationId` (`02.8.3-T2:49-52`) — and hydrates per episode, with at most 20 rows (`autopilot.veto.maxRows`, §6).

**[RESOLVED-2]** Source snapshot `00e7d5d` exposes a scalar episode `status` filter. The dashboard uses the five-call contract above; no comma-separated fallback remains.

**Events (D8).** Consumed: `EpisodeUpdated` (per-episode subscription via `RealtimeService.subscribeEpisode(episodeId, handler)`, WR7) → the run-log row refetches its episode/publication cell and, coalesced within 250 ms, the whole page once; `resync` after a (re)connect/visibility → the same coalesced refetch, but only if the tab has a live subscription. No `AutopilotRunUpdated` in P (B18/[OPEN] in 02.8.1 §5) → polling (§7). The hub token never appears in a URL and the ticket is never logged (D8).

**Events raised by this folder:** none. The UI never sends outbox messages; every state change goes through the listed routes.

## 6. UI

### 6.1 Routes and files

| Route | Feature file | Task |
|---|---|---|
| `/dashboard` | `features/dashboard/feature/dashboard-page.ts` | 10.4.1-T1 |
| `/series` | `features/series/feature/series-list-page.ts` | 10.4.2-T1 |
| `/series/new` | `features/series/feature/series-editor-page.ts` (`mode: 'create'`) | 10.4.2-T1 |
| `/series/:id` | `features/series/feature/series-detail-page.ts`: overview shell (`loaded`, archived, 404) | 10.4.2-T1; `10.4.3-T1/T2` own only the Autopilot tab body |
| `/series/:id/edit` | `features/series/feature/series-editor-page.ts` (`mode: 'edit'`) | 10.4.2-T1 |

Lazy per route (`loadComponent`), `authGuard` on all five (D6). Tab state in the URL (`?tab=autopilot`) so the pause e-mail deep link (`/series/<id>?tab=autopilot`, `02.8.3-T4:90`) lands on the right tab.

Components (all `OnPush`, inputs/outputs only in `ui/`, no store injection):

| Component | Path | Inputs / outputs |
|---|---|---|
| `AudioPreviewButton` | `features/series/ui/audio-preview-button.ts` | `@Input() previewUrl: string \| null`, `@Input() label`; `@Output() started`; play only when `previewUrl !== null`, `@defer (on interaction)`; produced by 10.4.2-T2 (WR4) |
| `VoicePicker` | `features/series/ui/voice-picker.ts` | `@Input() voices: readonly Voice[]`, `@Input() selectedId: string \| null`, `@Input() disabled: boolean`; `@Output() selected = output<string>()`; renders tag chips + preview control (10.4.2-T2) |
| `StyleGrid` | `features/series/ui/style-grid.ts` | `@Input() templates`, `@Input() selectedId`, `@Input() topic`; `@Output() selected`; radio group, `alt` = label, `@defer (on viewport)` previews |
| `CadenceEditor` | `features/series/ui/cadence-editor.ts` | `@Input() value: Cadence`, `@Input() timeZones: readonly string[]`, `@Input() dstHint: string \| null`; `@Output() valueChange`; weekday chips in a `fieldset role="group"` |
| `AuthorizationDialog` | `features/series/ui/authorization-dialog.ts` | `@Input() text: AuthorizationText`, `@Input() locale`; `@Output() accept = output<{textVersion, textHash}>()`, `@Output() cancelled`; focus-trapped, scroll-to-end gate, interpolation only |
| `PauseBanner` | `features/series/ui/pause-banner.ts` | `@Input() pauseReason`, `@Input() canResume`, `@Input() blockedReasons`; `@Output() resume`, `@Output() openRunLog` |
| `RunLogTable` | `features/series/ui/run-log-table.ts` | `@Input() runs`, `@Input() timeZone`; `@Output() loadMore`, `@Output() openEpisode`; `<table>` with a caption |
| `GateBadges` | `features/series/ui/gate-badges.ts` | `@Input() gates: GateSummary \| null`; renders failing checks first + `dry_run` badge; text + icon, never colour only |
| `CalibrationPanel` | `features/series/ui/calibration-panel.ts` | `@Input() report: CalibrationReport \| null`, `@Input() unavailable: boolean`; histograms behind `@defer (on viewport)` |
| `VetoRow` / `VetoList` | `features/series/ui/veto-list.ts` | `@Input() rows: readonly VetoRow[]`, `@Input() timeZone`, `@Input() busyIds`; `@Output() edit`, `@Output() cancel`, `@Output() approveNow`; actions projected through `ConfirmDialog`'s `cost-line` slot (left empty in P, D14) |
| `SeriesRow` | `features/series/ui/series-row.ts` | `@Input() series: SeriesListItem`; `@Output() open`, `@Output() archive`; semantic ruled `<li>`, not a Material-card surface |

Shared components reused verbatim (D11): `PageHeader`, `EmptyState`, `ErrorState`, `LoadingState`, `StatusChip`, `ConfirmDialog`, `ConflictDialog`, `RetryCountdown`, `CharCounter` (series title 1–120), `Stepper` (the 4 steps), `ReauthDialog` (10.3.3-T3). The copyable-id affordance in the veto/publication rows uses `CopyableCode` (owned by 10.1.3-T1, WR4) and the record id shown to the user for support.

### 6.2 Copy keys

Scope `series` (`web/src/assets/i18n/series/{vi,en}.json`), scope `dashboard`, scope `errors`; cross-plan keys used verbatim (D10).

| Key | vi | en |
|---|---|---|
| `series.list.title` | Series của bạn | Your series |
| `series.list.empty` | Chưa có series nào | No series yet |
| `series.list.new` | Tạo series | Create series |
| `series.list.archived` | Đã lưu trữ | Archived |
| `series.list.includeArchived` | Hiện cả series đã lưu trữ | Include archived |
| `series.step.topic` | Chủ đề và ngôn ngữ | Topic and language |
| `series.step.aspect` | Khung hình | Aspect |
| `series.step.voice` | Giọng đọc | Voice |
| `series.step.style` | Phong cách hình ảnh | Visual style |
| `series.field.title` | Tên series | Series title |
| `series.field.topic` | Chủ đề | Topic |
| `series.field.language` | Ngôn ngữ | Language |
| `series.field.aspect` | Khung hình | Aspect |
| `series.aspect.landscape` | Ngang (video dài) | Landscape (long-form) |
| `series.aspect.portrait` | Dọc (Shorts) | Portrait (Shorts) |
| `series.language.locked` | Không đổi được ngôn ngữ sau khi tạo; hãy tạo series mới | The language cannot change after creation; create a new series |
| `series.voice.clearedByLanguage` | Giọng đọc đã được xoá vì không khớp ngôn ngữ | The voice was cleared because it does not match the language |
| `series.voice.preview` | Nghe thử | Play preview |
| `series.voice.previewUnavailable` | Chưa có bản nghe thử cho giọng này | No preview is available for this voice |
| `series.voice.rateFixed` | Tốc độ đọc được cố định | The speaking rate is fixed |
| `series.style.trueCrimeNotice` | Chỉ các phong cách minh hoạ không có khuôn mặt người thật | Only illustration styles without real faces |
| `series.style.<name>.label` / `.hint` | (supplied by 03.1.1-T1's seed — used verbatim) | (same) |
| `series.save.saved` | Đã lưu | Saved |
| `series.save.conflict` | Series đã thay đổi ở nơi khác | The series changed elsewhere |
| `series.archive.confirm` | Lưu trữ series này? | Archive this series? |
| `voices.tag.<tag>` | (11 keys, supplied by 04 CR-11 — used verbatim) | (same) |
| `dashboard.title` | Tổng quan | Overview |
| `dashboard.needsReview` | Cần bạn xem | Needs your review |
| `dashboard.inProgress` | Đang thực hiện | In progress |
| `dashboard.ready` | Sẵn sàng đăng | Ready to publish |
| `dashboard.needsReview.empty` | Không có gì cần bạn xem | Nothing needs your review |
| `dashboard.inProgress.empty` | Không có tập nào đang chạy | Nothing is in progress |
| `dashboard.ready.empty` | Chưa có tập nào sẵn sàng | Nothing is ready yet |
| `autopilot.tab` | Tự động | Autopilot |
| `autopilot.next_slots.title` | Lịch sắp tới | Upcoming schedule |
| `autopilot.calibration.dryRunBanner` | Đang chạy thử ({remaining} tập nữa) | Running in dry-run ({remaining} episodes left) |
| `autopilot.calibration.title` | Hiệu chỉnh ngưỡng | Threshold calibration |
| `autopilot.calibration.ready` | Đã đủ dữ liệu ({evaluated}/{target}) | Enough data ({evaluated}/{target}) |
| `autopilot.calibration.unavailable` | Chưa có dữ liệu hiệu chỉnh | Calibration data unavailable |
| `autopilot.calibration.falseBlockRate` | Tỉ lệ chặn nhầm | False-block rate |
| `autopilot.calibration.falseApproveRate` | Tỉ lệ có thể đã bỏ sót | Potential false-approve rate |
| `autopilot.run.tableCaption` | Lịch sử chạy tự động | Autopilot run history |
| `autopilot.run.loadMore` | Xem thêm | Load more |
| `autopilot.run.openEpisode` | Mở tập | Open episode |
| `autopilot.veto.title` | Chờ bạn duyệt | Waiting for your review |
| `autopilot.veto.timeLeft` | Còn {time} | {time} left |
| `autopilot.veto.edit` | Sửa | Edit |
| `autopilot.veto.cancel` | Huỷ đăng | Cancel |
| `autopilot.veto.approveNow` | Đăng ngay | Approve now |
| `autopilot.veto.empty` | Không có video nào chờ duyệt | No video is waiting for review |

Cross-plan keys used verbatim (do not re-author): 02.8.3 §6 settings/authorization/pause keys (B14), `autopilot.run.state.*` + `autopilot.gate.failed.<checkId>` (B16), `autopilot.ideas.*` (B17), `autopilot.pause.*` (B15), `autopilot.gate.dry_run_review` (02.8.2-T3), `errors.<code>` for every code in §4 (D9), `voices.tag.<tag>` (B5), `series.style.*` (B7).

### 6.3 A11y (D16, per screen)

- Dashboard: the three groups are `<section>`s with headings; counts are text; `aria-live="polite"` on refresh; each ruled row action is a link (`ready`) or labelled button (`needs review`).
- Series list: ruled rows are a `<ul>`/`<li>` ledger (not Material cards); the archive button has an accessible name including the title; `aria-live` announces "archived".
- Editor: every step's controls labelled; the `Stepper` marks the current step with `aria-current="step"`; invalid controls carry `aria-describedby` to their error; focus moves to the first invalid control on a failed submit; `CharCounter` announced via `aria-live="polite"`.
- Voice picker: radio group (`fieldset`+`legend`); tag chips are text (`Chip` is decorative); the play button toggles `aria-pressed` and stops other audio; controls ≥ 24 px; 360 px layout stacks the list.
- Style grid: radio group; `<img>` `alt` = the template label; selected card has a visible border **and** a check icon; true-crime notice is a `role="note"`.
- Authorization dialog: focus trapped, `aria-labelledby` = heading, Esc = cancel (never accept), the text region is a focusable `role="region"` (`tabindex="0"`) so keyboard users can scroll it; accept is disabled until scroll-to-end with an `aria-live` hint `autopilot.authorization.scrollHint`, **and is enabled immediately when the text fits without scrolling** (checked on first render and on resize/zoom); focus restored to the switch on close.
- Run log: `<table>` with `<caption>`; state cells carry icon + text (no colour-only); `similarity` percentage with the raw value in `title`; the poll never steals focus; refreshed rows announce `aria-live="polite"` once per poll (not per row).
- Calibration panel: histograms have text alternatives (a `<table class="visually-hidden">` per histogram or a text summary); `ready` is a chip with text.
- Veto list: countdowns use `aria-live="off"` (a per-second announcement would be hostile); the time-left value is also a `<time datetime>`; `prefers-reduced-motion` disables the countdown animation.

**Design (owner rule 2026-09-25).** `10.4.1-T0` is the folder's design gate (D19): wireframes + hi-fi mockups for `/dashboard`, `/series`, `/series/new`, `/series/:id` (overview + autopilot tabs), `/series/:id/edit` and the 11 folder components, at 360/768/1280, dark + light, vi + en, with focus order/keyboard path/`aria-live` annotations. **Design link (recorded 2026-10-02):** review deck [`design/ui/index.html`](../../../../design/ui/index.html) → screen 03 [`screens/03-dashboard-series.html`](../../../../design/ui/screens/03-dashboard-series.html): dashboard [`#f-dash-loading`](../../../../design/ui/screens/03-dashboard-series.html#f-dash-loading) · [`#f-dash-data`](../../../../design/ui/screens/03-dashboard-series.html#f-dash-data) · [`#f-dash-empty`](../../../../design/ui/screens/03-dashboard-series.html#f-dash-empty) · [`#f-dash-error`](../../../../design/ui/screens/03-dashboard-series.html#f-dash-error); series [`#f-series-list`](../../../../design/ui/screens/03-dashboard-series.html#f-series-list) · [`#f-series-archived`](../../../../design/ui/screens/03-dashboard-series.html#f-series-archived) · [`#f-series-archive`](../../../../design/ui/screens/03-dashboard-series.html#f-series-archive); `/series/new` [`#f-new-step1`](../../../../design/ui/screens/03-dashboard-series.html#f-new-step1) · [`#f-new-step2`](../../../../design/ui/screens/03-dashboard-series.html#f-new-step2) · [`#f-new-step3`](../../../../design/ui/screens/03-dashboard-series.html#f-new-step3) · [`#f-new-step4`](../../../../design/ui/screens/03-dashboard-series.html#f-new-step4) · [`#f-new-errors`](../../../../design/ui/screens/03-dashboard-series.html#f-new-errors); detail/edit [`#f-detail`](../../../../design/ui/screens/03-dashboard-series.html#f-detail) · [`#f-edit`](../../../../design/ui/screens/03-dashboard-series.html#f-edit) · [`#f-conflict`](../../../../design/ui/screens/03-dashboard-series.html#f-conflict) · [`#f-unsaved`](../../../../design/ui/screens/03-dashboard-series.html#f-unsaved); autopilot [`#f-ap-off`](../../../../design/ui/screens/03-dashboard-series.html#f-ap-off) · [`#f-ap-active`](../../../../design/ui/screens/03-dashboard-series.html#f-ap-active) · [`#f-ap-paused`](../../../../design/ui/screens/03-dashboard-series.html#f-ap-paused) · [`#f-ap-auth-gate`](../../../../design/ui/screens/03-dashboard-series.html#f-ap-auth-gate) · [`#f-ap-reauth`](../../../../design/ui/screens/03-dashboard-series.html#f-ap-reauth) · [`#f-ap-legal`](../../../../design/ui/screens/03-dashboard-series.html#f-ap-legal); run log/calibration/veto [`#f-log-rows`](../../../../design/ui/screens/03-dashboard-series.html#f-log-rows) · [`#f-cal-ready`](../../../../design/ui/screens/03-dashboard-series.html#f-cal-ready) · [`#f-veto-rows`](../../../../design/ui/screens/03-dashboard-series.html#f-veto-rows). Full state → frame matrix: `10.4.1-T0-ui-design.md` §Outputs. **Owner approval: approved by the owner 2026-10-02** (deck v2 "Phòng thu", commit `192cc73`). Every task below lists `10.4.1-T0` as a hard `Depends on`.

### 6.4 Implementation seams and post-approval reconciliation notes (2026-10-02)

The visual source is the approved [`03-dashboard-series.html`](../../../../design/ui/screens/03-dashboard-series.html). These frames are static evidence only. Every screen-03 top-bar balance is an **S-only reference**: P renders the shell with `[header-end]` empty and no credit amount, balance, quote or buy action.

| Existing task | Observed frame ids | Coverage boundary / implementation note |
|---|---|---|
| `10.4.1-T1` | `f-dash-loading`, `f-dash-data`, `f-dash-group-empty`, `f-dash-empty`, `f-dash-error` | Base loading/data/empty/error visuals are observed. Rate-limit, focus-refresh and stale-result rejection reuse approved shared controls and are runtime acceptance. |
| `10.4.2-T1` | `f-series-loading`, `f-series-list`, `f-series-empty`, `f-series-archived`, `f-series-archive`, `f-series-error`; `f-new-step1`, `f-new-step2`, `f-new-step3`, `f-new-step3-empty`, `f-new-step4`, `f-new-step4-truecrime`, `f-new-errors`; `f-edit`, `f-conflict`, `f-unsaved`; `f-detail`, `f-detail-archived`, `f-detail-404` | The ruled list/wizard/edit/detail states are observed. This task owns the detail overview shell and its three states; Autopilot tasks own only the tab body. |
| `10.4.2-T2` | `f-new-step3`, `f-new-step3-empty`, `f-new-errors` | Picker/list/empty/error are observed. A playing preview and preview-load failure follow the locked contract; audio semantics remain runtime acceptance. The locked contract wins over mockup captions; flag to the owner only if a new visual state is needed (this does not reopen the approval or block the task). |
| `10.4.3-T1` | `f-ap-off`, `f-ap-enabling`, `f-ap-active`, `f-ap-paused`, `f-ap-auth-gate`, `f-ap-auth-fits`, `f-ap-auth-stale`, `f-ap-reauth`, `f-ap-legal` | Base Autopilot states are observed. `f-ap-legal` is future `13.2.1-T1` reference, not authority to implement a P legal-acceptance flow before its owner/API/dependency is ready. |
| `10.4.3-T2` | `f-log-loading`, `f-log-rows`, `f-log-empty`, `f-log-poll`, `f-cal-ready`, `f-cal-unavailable`, `f-veto-rows`, `f-veto-empty`, `f-veto-error`, `f-veto-edit` | Base log/calibration/veto states are observed; polling, countdown, event coalescing, permissions and mutations need browser + real-backend proof. |
| `10.4.1-T0` | All ids above | Voice-playing/preview-failed states and S-only balance/legal-reference frames are implementation reconciliation notes; P/S scope and backend capability stay unchanged. The locked contract wins over mockup captions; flag to the owner only if a new visual state is needed (this does not reopen the approval or block the task). |

Containers in `feature/` own routing, route-scoped stores and server coordination; presentational `ui/` consumes immutable view models and emits typed intent unions. Dashboard/series use semantic ruled rows (`NextActionRow`, `SeriesRow`) matching the artifact, not generic Material cards. Existing shared owners remain [implementation-roadmap.md §2](../implementation-roadmap.md); no new shared component is proposed. Typed forms bridge async control/error changes into zoneless notification and clean up through `DestroyRef`; all stores reject results from an old route id or session generation, and audio/poll/realtime/header registrations stop on route/session destruction.

Readiness follows [integration-readiness.md](../integration-readiness.md): series CRUD, scalar-status episode list and unpaged `{items}` voice/style catalogs are present; series `PATCH` returns only `{etag}` plus `ETag`, so adapters never fabricate a full series. Autopilot/story/publication extensions and legal acceptance wait for their named 02/06/13 owners. Static frames cannot close those prerequisites.

## 7. Jobs and integrations

- **Run log polling (D17):** `core/state/visible-poll.ts` with `intervalMs: 30_000`, running only while `document.visibilityState === 'visible'` **and** `isActive()` (D7); immediate run on return to visible/active; single in-flight request; a failed poll keeps the last good data and shows a soft hint, it never replaces the table with an error. Cleared through `DestroyRef` when the tab closes. This is the **only** timer in this folder used for freshness. Poll and tick specs advance the clock with Playwright `page.clock` (never a 90 s wall-clock wait) and the idle gate is asserted in unit tests through a fake `ActivityTracker` port — there is **no** in-app test hook (WR14).
- **Veto countdown (WR16):** a 1-minute local `tick` signal in `VetoListStore` recomputed from `publish_at` (no request), advanced by a timer that runs only while the tab is visible **and** `isActive()` (D7) and is cleared in `DestroyRef`; when a countdown reaches 0 the row refreshes once and the poll takes over. No per-row timers. The tick cadence is tested with Playwright `page.clock` (WR14).
- **Realtime (D8):** `RunLogStore` subscribes through `RealtimeService.subscribeEpisode(episodeId, handler)` (WR7) for the episodes on the current page; each event coalesces a page refetch within 250 ms. `resync` (reconnect/visible) triggers one coalesced refetch when the tab is live. `RenderProgress` is **not** used here (that is the episode page's progress signal).
- **DST display:** `CadenceEditor` shows the DST hint supplied by the backend (`NextSlot`/settings time zone) — the client never recomputes offsets; every schedule value is rendered with `Intl.DateTimeFormat(locale, { timeZone })` and the zone name shown explicitly (D10).
- **E-mail deep link:** the pause alert links to `<app origin>/series/<id>?tab=autopilot` (`02.8.3-T4:90`) — the tab is a query param so the link works without a redirect.
- **Jobs owned by this folder:** none. No outbox, no worker, no background task.

## 8. Security and privacy

| Risk | Control |
|---|---|
| Access token exposure | Token lives only in the `SessionStore` signal (D6); no `localStorage`/`sessionStorage` (D2 lint), no cookie read, no token in a URL or a log line |
| XSS via the authorization text / idea titles / publication titles | Text interpolation only, `[innerHTML]` banned by lint (D2) and by CSP + Trusted Types (D4); the authorization text is rendered as a `<pre>`-like block with preserved whitespace, not HTML |
| Stale consent accepted | Accept sends `textVersion` + `textHash` from the **rendered** `authorizationText` (B12); a mismatch surfaces `autopilot.authorization.stale_text` and forces a reload — the UI never fabricates a hash |
| Lost update on the settings form | `PUT /series/{id}/autopilot` with the `If-Match` from the previous response (header/body, B13); 412 → `ConflictDialog`; the client never retries a `PUT` automatically |
| Double submit (enable / accept / pause / consent) | Buttons disabled while in flight; single-flight promise per action in the store; the four commands are server-side idempotent or state-guarded (B11/B15) — the client adds no idempotency key because none of these routes requires one (P: the `IdempotencyKeys` helper is used only by episode creation in 10.5.2-T1) |
| Re-auth bypass | Enabling and accepting always go through `ReauthDialog` on 401 `reauth_required`; the client never caches the re-auth result across actions; no "remember for 5 minutes" client state |
| Feature-flag leak | The veto/run-log block renders only when `hasFeature('youtube_publish')`; a 404 `feature_disabled` mid-session hides the block and refetches `/me` (D14) |
| Cross-tenant access | Every id comes from the user's own list/detail responses; the server filters by tenant and answers 404 (B1); the UI never constructs another user's id and treats 404 as "not found" without retry loops |
| Content in logs/storage | No story text, idea titles, publication titles or preview URLs in `console.*`; dev logging is limited to ids/codes; nothing is persisted client-side (D13: no client cache for catalogs; the browser HTTP cache honours the API's ETag/`Cache-Control`) |
| Preview media origin | `previewUrl` (public bucket) is loaded as `<img>`/`<audio>`; CSP `img-src`/`media-src` allow the media origins from `NTS_CSP_MEDIA_ORIGINS` (D4) — no `bypassSecurityTrust*`; the URL is used as returned, never rebuilt from a key |
| Open redirect | Not applicable here; `returnUrl` handling belongs to 10.3.3 (D6). The pause e-mail link is a fixed app path with a query param, and `?tab` is validated against a literal allow-list (`overview` \| `autopilot`) |
| Sensitive data in the veto list | Only the owner's own publication metadata (title, privacy, times) + ids; no `platform_payload`, no `external_post_id` beyond the Studio link the backend already exposes |

## 9. Test plan

**Vitest (jsdom, TestBed zoneless, hand-written fakes — D15).**

| Store / component | Success | `AppError` mapping | ETag conflict | Optimistic rollback | Realtime/merge | Other |
|---|---|---|---|---|---|---|
| `SeriesListStore` | load page, `loadMore` appends, `nextCursor` null ends | 400 → field error, 401 → `SessionEnded` | — | archive removes the row, failure restores it | — | in-flight guard (double `loadMore` = one request) |
| `SeriesEditorStore` | 4-step validity, `isDirty` debounce, create → id | `voice_language_mismatch` clears the voice; `validation_failed` maps `voiceId`/`styleTemplateId`/`title`/`language` | 412 → conflict flag + unsaved input kept | — | — | language change clears the voice; `autopilotEnabled` → topic readonly |
| `AutopilotStore` | `GET` → form populated, dirty/saved transitions | `autopilot_settings_invalid` JSON-path → `bindFieldErrors`; `stale_text` → reload | 412 keeps the form | — | — | enable → `reauthPending` then `authorizationDialogOpen`; covered-field change → `requiredAuthorization` banner; pause reason list rendering |
| `RunLogStore` | page + `calibrationReport`; omitted report → `unavailable` | 404 `feature_disabled` → `hidden` | — | — | `EpisodeUpdated` coalesces to one refetch in 250 ms; `resync` refetches once | poll: runs only when visible + active (fake `document`/activity), pauses otherwise, single in-flight |
| `VetoListStore` | rows built from the run list + hydration, `remainingMs` decreases on tick | 409 state code → refetch; 429 → countdown | 412 → `ConflictDialog` state | cancel removes the row, failure restores it | — | actions disabled while `busyIds` |
| `DashboardStore` | five scalar-status result sets merged into 3 groups, empty group flag | `network_error` → `ErrorState` | — | — | refresh on window focus without a request storm | exactly 5 requests per load (asserted with the fake's call log) |
| `VoicePicker` (component) | renders tags as translated keys, no play button when `previewUrl === null`, play pauses the previous audio, `disabled` blocks selection | — | — | — | — | keyboard: radio arrows change selection; `aria-pressed` on the play button |
| `StyleGrid` (component) | radio semantics, `alt` = label, true-crime notice only for `true_crime` | — | — | — | — | topic change re-filters and clears an incompatible selection |
| `AuthorizationDialog` (component) | accept disabled until scroll-to-end, then emits `{textVersion, textHash}` | — | — | — | — | Esc cancels; focus trapped; the text is not parsed as HTML |
| `RunLogTable` (component) | `<caption>`, unknown state ignored, `dry_run` badge, failing checks first | — | — | — | — | `similarity` percentage + raw value in `title` |
| `CalibrationPanel` (component) | readiness chip, rates, recommendation lines | — | — | — | — | `unavailable` renders the message, never `0 %` |

Domain/util tests are pure (no TestBed): topic/style compatibility helper, `remainingMs` formatting, the tab allow-list parse, `CursorPage` shape.

**Playwright e2e (`web/e2e/`, `workers: 1`, owner auth helper, one login per spec file — D15).**

| Spec | Scenario | axe |
|---|---|---|
| `series-create.spec.ts` | create a series end-to-end (4 steps, voice preview played once, style grid radio, true-crime notice path) → appears in the list; language change clears the voice | every visited page |
| `series-edit-conflict.spec.ts` | edit the title in the UI while a second context PATCHes → Save → `ConflictDialog` → "Reload latest" then "Save mine as new version" | ✅ |
| `autopilot-enable.spec.ts` | enable → re-auth → authorization dialog (scroll gate) → `enabled`, accepted version shown; stale-hash path via a settings change in a second context | ✅ |
| `autopilot-pause.spec.ts` | pause with a note → banner; resume blocked while a fake reason holds (seeded DB state) → resume succeeds after the fake connection is active | ✅ |
| `run-log-poll.spec.ts` | open the tab, record the request count, advance the clock with Playwright `page.clock` → exactly one new request per interval (no wall-clock waits; WR14); `EpisodeUpdated` (backend write in a second context) updates the row without a full-page reload | ✅ |
| `veto-window.spec.ts` (flag `youtube_publish`) | a publication in `awaiting_consent` → Edit (title), then Cancel, then Approve now; the same spec asserts the block is absent with the flag off | ✅ |
| `dashboard.spec.ts` | create episodes in three statuses (seed/fake transitions) → the three groups show the right cards, no credit UI anywhere in P | ✅ |
| `csp-fixture.spec.ts` (10.1.6-T1, extended) | all the pages above with no `securitypolicyviolation` and no console CSP/Trusted Types error | ✅ |

Both languages: each spec runs the `vi` project and an `en` smoke variant (key-set equality is enforced by 10.2.5-T1's CI check; the e2e asserts a handful of visible strings per language, e.g. `dashboard.title`, `series.step.voice`, `autopilot.veto.approveNow`).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 10.4.2-T2 | Voice picker with preview audio (04 CR-11) | S (0.5) | 10.1.3-T1, 10.2.2-T1, 09.4.2-T1, 04.2.3-T1 (soft) | 10.4.2-T1 |
| 10.4.1-T0 | UI design (dashboard, series editor, autopilot tab, run log, veto list) | M (1.0) | 10.1.3-T1, 09.4.2-T1 (soft) | 10.4.2-T2, 10.4.2-T1, 10.4.1-T1, 10.4.3-T1, 10.4.3-T2 |
| 10.4.2-T1 | Series list + 4-step editor + style grid (03 CR-7) | M (1.0) | 10.2.6-T1, 10.4.2-T2, 09.4.1-T1, 09.4.2-T1, 03.1.1-T1, 03.1.2-T1 (soft) | 10.4.3-T1, 10.5.7-T1 |
| 10.4.1-T1 | Dashboard | S (0.5) | 10.2.6-T1, 10.5.1-T1 | — |
| 10.4.3-T1 | Autopilot tab: settings, authorization dialog, re-auth, pause/resume | M (1.0) | 10.2.6-T1, 10.3.3-T3, 10.4.2-T1, 02.8.3-T2 | 10.4.3-T2 |
| 10.4.3-T2 | Autopilot run log, calibration panel, veto-window list | M (1.0) | 10.4.3-T1, 10.2.4-T1, 10.5.3-T1, 02.8.2-T3, 02.8.3-T4 (soft), 06.2.1-T1 (soft), 06.2.2-T3 (soft) | 06.5.1-T1 (06 CR-8), 10.10.2-T1 (10 CR-15) |

Total **5.0 d raw** (1.0 `10.4.1-T0` design, D19, + S 2 × 0.5 + M 3 × 1.0) → **6.0 d buffered** (× 1.2). The folder's sizes equal `../README.md` §8 (`10.4-series-autopilot/`: 10.4.2-T2 (S), 10.4.2-T1 (M), 10.4.1-T1 (S), 10.4.3-T1 (M), 10.4.3-T2 (M)); the dependency cells equal §8.1 and the task header rows (10 CR-7 for `10.4.2-T1`/`10.4.2-T2`).

**Re-estimate notes.** No task exceeds M. The two soft dependencies (`04.2.3-T1`, `06.2.1-T1`, `06.2.2-T3`) are backend-side only in the sense that the *contract* (item shape, status graph) is already fixed (B4/B5, B19); the E2E acceptance of the veto spec needs the real API, which the folder schedules after `02.8.3-T2`. If `04.2.3-T1` slips, `10.4.2-T2` still ships: `previewUrl: null` simply hides the play control (B4 edge case).

## 11. Rollout

1. **Order.** `10.4.2-T2` → `10.4.2-T1` (needs the picker) → `10.4.1-T1` (independent, parallel to the previous two once `10.5.1-T1` lands) → `10.4.3-T1` → `10.4.3-T2`. Only the picker and the editor touch `features/series/ui/**` on overlapping lines; the integration owner for `features/series/` is `10.4.2-T1`.
2. **Backend readiness.** `10.4.2-*` need `09.4.1-T1` + `09.4.2-T1` merged (contract-true fakes unblock the component tests, but the E2E acceptance runs against the real API). `10.4.3-T1` needs `02.8.3-T2`; `10.4.3-T2` needs `02.8.2-T3` (calibration fields) and degrades gracefully if `06.2.2-T3`/`06.2.1-T1` are late: the veto block renders from the run log's `publicationId` and hides on 404.
3. **Flag.** The whole autopilot tab's *content* is gated server-side by `youtube_publish` (B9): with the flag off `GET …/autopilot` answers 404 `feature_disabled`, the tab shows only the series overview and refetches `/me`; the veto list additionally requires the flag client-side (D14). No separate client flag is introduced in P (O1).
4. **Gate 10-W1-studio** (`../README.md:256-260`) is the exit criterion for this folder: in the local stack the owner creates a series (voice + style), and the autopilot tab enables, pauses and resumes with the run log visible, in vi and en, axe clean.
5. **Rollback.** Hiding the tab is a one-line guard (`hasFeature`), not a deploy: the routes stay, the read-only series overview works, and the backend flag hides the entire surface. A pure-frontend rollback re-deploys the previous container (D3); no data migration is involved because this folder owns no persisted state.
6. **Monitoring after release.** The two behaviours worth watching are poll frequency (one request / 30 s / open tab, no storm on repeated visibility flips) and the settings form's 412 rate (a high rate means the ETag flow is wrong). Both are visible in the browser network panel and in the API's request metrics; no client telemetry is added in P (NFR-10-5/privacy: no content is sent anywhere).

---
Links: `../README.md` §3 D1–D18, §4 CR-7/CR-11, §8/§8.1, §7 gates; `../../../10-web-ui.md` §4 (10.4.1–10.4.3); `../../09-backend-api/09.4.1-series-crud/plan.md`; `…/09.4.2-5.3-queries/09.4.2-T1-voices-style-templates.md`; `../../02-story-writing/02.8.3-cadence-publishing/02.8.3-T2-autopilot-endpoints-authorization.md` (+ `plan.md` §6); `…/02.8.3-T4-pause-triggers-spend-caps-alerts.md`; `…/02.8.2-auto-approval-gates/02.8.2-T3-thresholds-dry-run-calibration.md` (+ `plan.md` §6); `…/02.8.1-idea-generator/plan.md` §6; `../../03-image-generation/03.1-style-system/plan.md` §6; `../../04-voice-proxy/README.md` CR-11; `../../../00-overview.md` §6/§8.
