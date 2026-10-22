# Phase 7 — route and state coverage

**Đã bổ sung 9 màn hình, giữ nguyên tokens.css và CSS thành phần của index.html.** Bảng dưới đối chiếu toàn bộ route và trạng thái trong `plans/10-web-ui.md` §5.2, cùng hành trình §5.3. Đây là mẫu HTML/CSS: các liên kết mở trạng thái minh hoạ, không thực hiện xác thực, lưu, đăng hoặc huỷ thật. Các giới hạn dữ liệu và tích hợp được ghi rõ ở cuối.

## Source evidence

Root: `/Volumes/Project/NightTalesStudio/`.

- `plans/10-web-ui.md` §5.2, §5.3 and tasks 10.4.3-T1/T2, 10.5.3-T1/T2, 10.5.9-T1, 10.5.10-T1, 10.5.6-T1, 10.6.1-T1/T2, 10.7.2-T1, 10.7.3-T1/T2, 10.7.4-T2, 10.3.3-T3.
- `DECISIONS.md` §10.14: personal studio, no billing/credits/payments surface; provider metering and spend caps remain.
- `web/src/app/features/episodes/domain/episode.model.ts`, `render.model.ts`, `generation-state.ts`; `episodes/episodes.routes.ts`; `application/make-video.store.ts`.
- `web/src/app/features/episodes/ui/story-editor.component.html`, `render-timeline/render-timeline.component.html`; `feature/new-episode.page.html`.
- `web/src/app/features/account/ui/legal-acceptance-dialog/legal-acceptance-dialog.html`; `auth/ui/mfa-setup-view/mfa-setup-view.component.html` (feature directory inventory was inspected; plan-led surfaces are identified below).
- Detailed plans: `plans/features/10-web-ui/10.7.2-publish/plan.md`, `10.7.3-tiktok-post/plan.md`; `plans/features/06-youtube-posting/README.md`; `plans/features/02-story-writing/02.8.3-cadence-publishing/02.8.3-T2-autopilot-endpoints-authorization.md`, `02.8.3-T1-slot-planner-run-state-machine-buffer.md`; `02.8.2-auto-approval-gates/02.8.2-T3-thresholds-dry-run-calibration.md`.
- YouTube consent is copied from `src/NightTales.Application/Templates/Consent/pc.v1.vi.md` and `pc.v1.en.md`. Their compliance-review-pending note is preserved as a visible limitation.

## Full §5.2 inventory

“Covered” means a visible specimen exists, not that a static file implements the route or API. Hash links below are real project anchors. Paths such as `/reset-password#t=` describe the runtime contract; no real token is embedded.

| Route | Inventory state | Page and section | Coverage / boundary |
|---|---|---|---|
| `/privacy`, `/vi/privacy`, `/en/privacy` | Static, both languages | [legal.html#privacy](legal.html#privacy), VI/EN selector | Existing reading surface; runtime aliases are deployment work |
| `/terms`, `/vi/terms`, `/en/terms` | Static, both languages | [legal.html#terms](legal.html#terms), VI/EN selector | Existing reading surface; runtime aliases are deployment work |
| `/login` | Form | [auth.html#password](auth.html#password) | Existing |
| `/login` | Submitting, error, rate-limited | `auth.html#password` → disclosed error/loading specimens | Existing, including retry-after copy |
| `/login` | Success | [dashboard.html](dashboard.html) after sign-in | Existing destination |
| `/login/mfa` | Form, submitting, error | [auth.html#totp](auth.html#totp) → disclosed state specimens | Existing |
| `/login/mfa` | Rate-limited | `auth.html#totp` → rate-limit block in its disclosed examples: 30 s retry-after countdown, wait and fetch a fresh code | Covered in Phase 8; still a local specimen, not server enforcement |
| `/login/mfa` | Success | [dashboard.html](dashboard.html) | Existing destination |
| `/forgot-password` | Form, submitting, error | [auth.html#forgot](auth.html#forgot) → disclosed state specimens | Existing |
| `/forgot-password` | Success | [auth.html#forgot-sent](auth.html#forgot-sent) | Existing, enumeration-safe confirmation |
| `/forgot-password` | Rate-limited | `auth.html#forgot` → rate-limit block: countdown, resend once, check spam | Covered in Phase 8; still a local specimen, not server enforcement |
| `/reset-password#t=` | Form, submitting, error | [auth.html#reset](auth.html#reset) → disclosed state specimens | Existing, invalid/expired-link copy; no actual fragment token |
| `/reset-password#t=` | Success | [auth.html#reset-done](auth.html#reset-done) | Existing |
| `/reset-password#t=` | Rate-limited | `auth.html#reset` → rate-limit block: countdown, retry or request a new link | Covered in Phase 8; still a local specimen, not server enforcement |
| `/dashboard` | Loading | `dashboard.html` → state selector “Đang tải / Loading” | Existing |
| `/dashboard` | Empty, first-run guide | [dashboard.html#first-run](dashboard.html#first-run) | Existing |
| `/dashboard` | Data | [dashboard.html](dashboard.html) | Existing three table lanes |
| `/series` | Loading, empty, error | [series-list.html#loading](series-list.html#loading), [#empty](series-list.html#empty), [#error](series-list.html#error) | Existing |
| `/series` | List | [series-list.html#default](series-list.html#default) | Existing |
| `/series/new` | Form | [series-editor.html#create-1](series-editor.html#create-1) through `#create-4` | Existing four steps |
| `/series/new` | Loading, empty, error | `series-editor.html#loading`, `#empty`, `#error`, `#validation` | Existing |
| `/series/:id` | Form / overview | [series-detail.html#default](series-detail.html#default), [series-editor.html#edit-1](series-editor.html#edit-1) through `#edit-4` | Existing |
| `/series/:id` | Loading, empty, error | `series-detail.html#loading`, `#empty`, `#error` | Existing |
| `/episodes` | Loading, empty, list | [episodes.html#loading](episodes.html#loading), [#empty](episodes.html#empty), [#default](episodes.html#default) | Existing |
| `/series/:id/episodes/new` | Form | [new-episode.html#default](new-episode.html#default) | Cost/quote block removed; other fields retained |
| `/series/:id/episodes/new` | Generating | [new-episode.html#loading](new-episode.html#loading), [render.html#story-generating](render.html#story-generating) | Existing creation state + new story-generation stage |
| `/series/:id/episodes/new` | Spend-capped | [new-episode.html#spend-capped](new-episode.html#spend-capped) | New pause, preserved idea, return path; no checkout/quote |
| `/episodes/:id` | Generating | [render.html#story-generating](render.html#story-generating) | `story_generating`; backend generation sub-stages, not new episode statuses |
| `/episodes/:id` | Story review | [story-review.html#default](story-review.html#default), [story-editor.html#default](story-editor.html#default) | Existing reader + new explicit editor |
| `/episodes/:id` | Rendering | [render.html#default](render.html#default), [#waiting](render.html#waiting) | `rendering`; worker unavailable is waiting, not failure |
| `/episodes/:id` | Video review | [video-review.html#default](video-review.html#default), [render.html#finished](render.html#finished) | Existing player / new handoff |
| `/episodes/:id` | Ready | [publish-youtube.html#default](publish-youtube.html#default), [publish-tiktok.html#default](publish-tiktok.html#default) | `ready` chip, platform forms; YouTube includes local MP4 download |
| `/episodes/:id` | Published | [publish-and-channels.html#published](publish-and-channels.html#published), [publish-tiktok.html#post-status](publish-tiktok.html#post-status) | Existing published result + new per-post result |
| `/episodes/:id` | Failed | [render.html#failed](render.html#failed), [#stage-failed](render.html#stage-failed), [#story-failed](render.html#story-failed) | Moderation/edit path, render retry, story retry |
| `/episodes/:id` | Paused | [render.html#paused](render.html#paused) | `generation.processingPaused`; **not** an invented `paused` episode wire status |
| `/episodes/:id/publish/youtube` | Form | [publish-youtube.html#default](publish-youtube.html#default) | Title, description, tags, privacy, schedule, made-for-kids, synthetic disclosure |
| Same | Consent | [publish-youtube.html#consent](publish-youtube.html#consent), [#consent-outdated](publish-youtube.html#consent-outdated) | Exact pc.v1 wording; explicit checkbox, stale wording needs renewed consent |
| Same | Submitted | [publish-youtube.html#submitted](publish-youtube.html#submitted) | Publication table; no duplicate send |
| Same | Quota wait | [publish-youtube.html#quota-wait](publish-youtube.html#quota-wait) | Project-wide quota; no fabricated ETA |
| Same | Scheduled / pre-audit | [publish-youtube.html#scheduled](publish-youtube.html#scheduled), [#preaudit](publish-youtube.html#preaudit) | Scheduled uploads private first; pre-audit private lock explicit |
| `/episodes/:id/publish/tiktok` | Creator blocked | [publish-tiktok.html#creator-blocked](publish-tiktok.html#creator-blocked) | Disabled publish; exact runtime blockReason required |
| Same | Form | [publish-tiktok.html#default](publish-tiktok.html#default) | No default privacy; creator constraints, interaction permissions, commercial disclosure, AI label |
| Same | Consent | [publish-tiktok.html#consent](publish-tiktok.html#consent) | Explicit confirmation; full versioned server text still needed, see gaps |
| Same | Submitted | [publish-tiktok.html#submitted](publish-tiktok.html#submitted), [#post-status](publish-tiktok.html#post-status), [#post-failed](publish-tiktok.html#post-failed) | Per-post processing/published/failed; no fake browser transfer progress |
| `/channels` | List | [publish-and-channels.html#default](publish-and-channels.html#default) | Existing channel table; revoked and unverified states retained |
| `/channels/:platform/callback` | Connecting | [channel-callback.html#default](channel-callback.html#default) | Verification pending |
| Same | Callback result | [channel-callback.html#connected](channel-callback.html#connected), [#denied](channel-callback.html#denied), [#error](channel-callback.html#error), [#expired](channel-callback.html#expired) | Four distinct results; no token displayed |
| `/publications/cancel` | Confirm | [publication-links.html#default](publication-links.html#default), [#confirm](publication-links.html#confirm) | Public layout, no session/owner menu; token remains a runtime fragment contract |
| Same | Done | [publication-links.html#done](publication-links.html#done) | Cancellation confirmation; used link invalidated contract |
| Same | Invalid / expired | [publication-links.html#invalid](publication-links.html#invalid), [#expired](publication-links.html#expired) | No account detail leakage |
| Same | Already published | [publication-links.html#already-published](publication-links.html#already-published) | Cannot cancel after publication |
| `/publications/approve` | Owner guarded | [publication-links.html#owner-guard](publication-links.html#owner-guard) | Sign-in path precedes preview in the runtime; static specimen is not an access control |
| Same | Preview | [publication-links.html#preview](publication-links.html#preview) | Existing local MP4, channel/schedule context |
| Same | Consent text | [publication-links.html#consent](publication-links.html#consent) | Exact YouTube text; explicit agreement |
| Same | Approved | [publication-links.html#approved](publication-links.html#approved) | Confirmation for this publication/version |
| Same | Refusal | [publication-links.html#refusal](publication-links.html#refusal) | No consent/no submission |
| `/account/security` | MFA status / enrol | [account-security.html#default](account-security.html#default), [#enrol](account-security.html#enrol) | No owner MFA-disable action; demo QR labelled non-enrolling |
| Same | Verify | [account-security.html#verify](account-security.html#verify), [#verify-error](account-security.html#verify-error) | Numeric six-digit code and invalid-code state |
| Same | Recovery codes shown once | [account-security.html#recovery](account-security.html#recovery), [#regenerate-confirm](account-security.html#regenerate-confirm) | Ten visibly invalid demo codes, local download, old codes revoked on regeneration contract |
| Same | Re-auth dialog | [account-security.html#reauth](account-security.html#reauth), [#reauth-error](account-security.html#reauth-error) | Password + TOTP before sensitive action; pending request retried once |

## Additional named task coverage

| Task | Page / state | Contract shown |
|---|---|---|
| 10.4.3-T1 | `autopilot.html#default`, `#authorization`, `#reauth`, `#enabled`, `#pause`, `#paused`, `#resume`, `#disable` | Cadence, time zone, weekly count, daily cap, buffer days, veto hours, privacy, pre-audit, versioned authorization, topic lock, revoke/pause/resume |
| 10.4.3-T2 | `autopilot.html#run-log`, `#run-detail`, `#calibration`, `#veto` | Run table, gate context, report metric table, veto approve/cancel; polling/event refresh explained |
| 10.5.3 / 10.5.9 | `story-editor.html#default`, `#edit-paragraph`, `#scene-edit`, `#flagged`, `#reorder`, `#reordered`, `#delete-confirm`, `#deleted`, `#dirty-guard` | Paragraph/scene editing, native drag affordance + keyboard move controls, reorder result, delete/undo, explicit dirty guard |
| 10.5.10 | `story-editor.html#conflict`, `#saved`; existing `story-review.html#conflict` | Retain unsaved draft; reload latest or save mine as new version, no silent overwrite |
| 10.5.3-T2 | `story-editor.html#default` | Objective blocking vs taste advisory, evidence/repair hint, corrected-once badge, similarity highlight |
| 10.5.6 / DEC 10.14 | `story-editor.html#approve`, `#reopen`, `#attest` | No cost copy; true-crime current-version attestation |
| 10.6.1-T1/T2 | `render.html#default`, `#waiting`, `#make-video`, `#make-confirm`, `#keyword-gate`, `#failed`, `#stage-failed`, `#cancel`, `#cancelled` | Three guaranteed stages, optional sub-steps, progress fixture, keyword gating, edit/retry paths, cancellation confirmation |
| 10.7.2-T1 | `publish-youtube.html` | Full form, version-bound consent, quota wait, submitted, scheduled, pre-audit and download |
| 10.7.3-T1/T2 | `publish-tiktok.html` | Creator restrictions, pre-audit SELF_ONLY, no schedule, no privacy default, interactions off, branded incompatibility, AI default, music confirmation, per-post status |
| 10.7.4-T2 | `publication-links.html` | Public cancel and guarded owner approve remain separate flows |
| 10.3.3-T3 | `account-security.html` | Enrol, verify, shown-once recovery, regenerate, re-auth |
| Shared overlays | `dialogs.html#default`, `#reauth`, `#session-expired`, `#reconnecting`, `#offline`, `#connected`, `#not-found`, `#error` | New-version legal acceptance, identity confirmation, preserved draft, realtime recovery/re-fetch, offline, 404, generic error |

## Status integrity

Episode wire statuses read from `episode.model.ts`: `draft`, `story_generating`, `story_review`, `story_approved`, `rendering`, `video_review`, `ready`, `scheduled`, `published`, `failed`, `cancelled`. Existing catalog still documents all eleven. New episode chips reuse its markup. `paused` is only a UI condition, never a new wire-status value. Autopilot `waiting_owner` is a run state, not an episode state. Publication `awaiting_consent`, `scheduled`, `processing`, `published`, `failed` are publication statuses. Render job stages are `queued`, `running`, `ingesting`; optional `voice`, `subtitles`, `images`, `assembling`, `checking` appear only if the worker reports them. Unknown sub-steps are not invented.

## Verification and explicit gaps

- Static audit: **9 new pages, 7 collection tables, 73 icon controls including both locale copies and table-state specimens; zero broken internal links, duplicate IDs, unlabelled fields/icon actions or external dependencies.** Phase 8 adds two small local scripts (auth countdown, recovery-code copy) and collection tables on render, account security, story editor and auth. Evidence: `assets/audit/phase-7-static-checks.json`.
- Each new page starts in Vietnamese/dark, links the unchanged `tokens.css`, and includes exactly the existing index component CSS before layout-only additions. Existing 32/36/44 controls, 24px chips, 32px icon buttons, SVG set, focus rings and theme/locale selectors remain bound. Every episode/publication table has Status, Stage and Next action columns.
- Responsive rules inherit the project’s current phase-6 behavior: 1280 sidebar and table rows; 768 icon rail and stacked table fields (existing breakpoint is ≤1000); 360 mobile disclosure navigation, stacked forms and labelled cards. One rendered desktop review of the story editor checked hierarchy and collisions. Localization/theme/breakpoint selectors and mobile shrink constraints were checked in source; **this is not a claim of measured browser coverage across every state and viewport**.
- State navigation, consent checkbox gates, local video playback/download, disclosure menus and language/theme switches use HTML/CSS only. Authentication, OAuth, token handling, live polling/SignalR, drag persistence, undo persistence, dirty/beforeunload guards, API saves, ETag/idempotency, true consent, focus trapping/restoration and shown-once secrecy require Angular. They are not simulated as successful network operations.
- Source implementation is incomplete for several autopilot/publishing/account flows. Those screens are **plan-led**, not claims that matching Angular pages already exist. Creator nickname/options/duration/blockReason and calibration metrics have no supplied fixture; visible unavailable states replace invented values. The calibration report uses the supplied run-response report contract and adds no endpoint.
- TikTok’s complete versioned music/branded consent text is not supplied in the inspected source. The screen labels its confirmation specimen and requires the runtime’s `consent-text` response. **Gap: final exact TikTok consent wording cannot be verified here.** YouTube pc.v1 wording exists but remains marked compliance-review pending in source. No legally final copy is claimed.
- Published post URLs and real MFA enrolment/recovery secrets are not supplied. External open is disabled with an explanation; QR/codes are visibly non-operational demos. No real account/secret/token was embedded.
- Auth rate-limit specimens for password, MFA, forgot and reset now exist inside `auth.html` (Phase 8): 30 s countdown, flow-specific next step and an enabled retry control at zero. They are static specimens; server-side rate limiting and Retry-After enforcement remain runtime work. Runtime route aliases, routing guards and prerendering remain implementation work, not additional HTML screens.
