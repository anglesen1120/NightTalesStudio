# Angular implementation sequence and shared contracts

Planning amendment, 2026-10-02. Read with [README](README.md) and [backend readiness](integration-readiness.md). This refines the **existing 73 tasks / 18 feature folders**; it creates no new task IDs, estimates, implementation authorization, or dependency graph. Folder task headers remain the exact owner/dependency authority. Existing P/S/R2 gates and owner-approved `-T0` designs still apply.

## 1. Execution order and exit evidence

Order below is a partial order, not a requirement to implement every feature serially. Only split tasks whose headers have satisfied prerequisites and whose writes do not overlap. Backend availability is reassessed at the task's start; a design frame is not an API dependency.

| Stage | Existing feature folders | Responsibility and concrete exit evidence |
|---|---|---|
| A — reproducible workspace | `10.1.1-workspace-ci` | Angular 22/Node/npm pins, standalone zoneless shell, Vitest builder, layer-based lint, same-origin dev proxy, locked installs/build/CI/hosting. Lint negative fixtures cover plain `.ts` UI files; production deep-link reload and headers work. |
| B — primitives and transport contracts | `10.1.3-design-api-csp` | Approved tokens/Material theme/vi+en/self-hosted font; existing shared kit; committed API generation/provenance/drift; CSP/Trusted Types and safe downloads. Generated bootstrap auth is permitted eager; feature operations remain lazy by production emitted graph. No invented policy enumeration, CDN, or backend fields. |
| C — session and application lifecycle | `10.2.1-session-errors`, `10.2.4-realtime-i18n-shell` | Memory-only session/restore/refresh/revocation; canonical typed error/form mapping; idempotency intents; realtime ticket/socket and activity-aware resync; locale/theme/protected shell. Deferred restore, form/server error notification, stale `/me` after logout, socket lifecycle and protected child navigation are real tests. `GET /me` integration waits for its owner. |
| D — entry and basic workspace | `10.3-auth-legal`, `10.4-series-autopilot`, `10.5.1-episodes-editor` | Approved auth/legal designs, owner MFA and reauth; static legal content versus acceptance API; dashboard/series/basic episode reads and create/edit forms. Preserve scalar episode create id, partial series PATCH, catalog cache envelope. Story/autopilot sections wait for their own owners rather than deriving capability from basic detail. |
| E — editorial composition | `10.5.4-versions-research-bible` | Version selection/diff, reopen conflict, fact-sheet attestation/annotations, bible and external source workflows. Feature-local editor vocabulary; share only existing named diff/annotation/segment APIs. Validate server-owned version/ETag/approval gates and late route-result rejection against merged story contracts. |
| F — production and distribution | `10.6-render-review`, `10.7.1-channels`, `10.7.2-publish` | Enqueue/progress/cancel, signed media expiry, review/reject, channel connect/reauth/legal checks, publication/consent/ETA/public-cancel. Media URLs and populated summaries/events must come from real owners. A render DTO declaration or socket connection cannot close these flows. |
| G — owner release | `10.10.2-journeys` P task; P acceptance in all previous owners | Real API/PostgreSQL/Worker/mock-MPT at approved external seams; conflict in two genuine tabs; session end and cleanup; complete owner story→review→publish journey only after all relevant backend owners. Run shared-shell/core a11y in P (the former public-site audit task is deleted by DEC 10.14). |
| ~~H — authorized SaaS wave~~ | — (was `10.3-public-signup`, `10.8-billing-account`, `10.9-admin-core`, `10.9-admin-ops`, `10.10.1-a11y`, the `10.10.2` S task) | **Dropped by DEC 10.14** (owner 2026-10-21): public registration/CAPTCHA, profile/security/activity/data rights, credits/payment, role-gated administration and the public-site WCAG audit are deleted — none of the four task files an earlier draft kept as delivered had a commit on `develop`. Nothing replaces this stage; the personal studio needs no go/no-go and no public-surface checks. |
| I — TikTok lane (R2, now P) | `10.7.3-tiktok-post` | R2 flag/owner/design/backend prerequisites; separate platform-specific compose/post/consent/queue behavior. Do not import route/nav/chunks into P just because the deck includes frames. |

No size changes: P **41.0** raw days + the **2.5** TikTok lane (now P) = **43.5 raw / 52.2 buffered**; P wave 1 **31.5**, wave 2 **9.5**. The former S 20.0 is deleted by DEC 10.14 and is not counted. This amendment specifies correctness detail, not additional estimated tasks.

## 2. Component ownership and reusable API boundaries

Keep the five-layer feature structure. Routed/container composition in `feature/` may inject an application store. Presentational `ui/` accepts typed view contracts and emits intent; it does not import generated DTOs, call HTTP, inject repositories/stores, or decide server permission. `shared/ui` is not a repository for feature business widgets. `core` owns cross-cutting lifecycle/session/HTTP/i18n/realtime and shell composition.

D11/WR4 already approved shared components remain shared regardless of the two-consumer rule below. Promote a **new** feature widget only when two named independent consumers need the same semantic contract; record owner, inputs/outputs, accessibility behavior and downstream edges before adoption. Admin-only repetition stays admin-local. Do not add universal table/form/schema DSLs, base repositories, SharedModule, Nx/NgRx or parallel clients.

| Existing shared owner | Reusable contract to implement once | Consumer boundary |
|---|---|---|
| `10.1.3-T1` | `PageHeader`, semantic loading/empty/error states, `StatusChip`, `ConfirmDialog`, `Stepper`, `CharCounter`, `CopyableCode`, inline icons | Readonly typed inputs, signal outputs for intent; translated visible/accessibility names. Static actions may use projection; dynamically opened dialogs require typed data and an optional typed `TemplateRef` for cost content, not imaginary caller `ng-content`. P does not contain credit content. |
| `10.2.2-T1` | `ConflictDialog`, `RetryCountdown`, `bindFieldErrors`, `FormErrorAlert` | One canonical namespaced server-error shape, preserve local validation, clear on edit/resubmit, retain unmapped messages in form alert. Countdown starts after input binding, resets/cleans timers and emits elapsed deterministically. |
| `10.2.3-T1` | `IdempotencyKeys` | Key represents one business intent; reuse only for its retry, never across changed request/user/entity. Store owns submit state; response cannot commit after session generation changes. |
| `10.2.6-T1` | Cursor/page/activity-poll helpers and shell header composition | Catalog `{items}` is not a cursor endpoint. Abort/ignore obsolete route/session results, stop hidden/destructed polling, bounded resync. Page-scoped header template registration must unregister on navigation/destruction. |
| `10.1.6-T1` | Safe filenames/download and existing reviewed sanitizer boundary | No ad-hoc blob URL or raw HTML sinks. Preserve WR12/D4 sanitizer contract; an unused approved policy is **not** permission to implement speculative sinks or bypass sanitization. Any actual use needs a reviewed source→sink and supported-browser behavior. |
| `10.3.3-T3` | Reauth service/dialog | Full-session versus enrolment-only scope; no secrets in route/query/logs; action replay follows existing owner rule. |
| `10.4.2-T2` | Audio preview | One active preview, stop on route/session/destroy, fetch/cache only within approved catalog media contract; keyboard and visible playback state. |
| `10.5.3-T1`, `10.5.10-T1`, `10.5.4-T1` | Segment/address contracts, annotation/keyword flags, DiffView/text diff | Feature-semantic components remain within their owning feature export contract. Diff is optional heavy/deferred content with reserved layout and accessible fallback; importing it must not make base editor eagerly load all research/history code. |
| `10.5.5-T1` | `EPISODE_GATE` | Typed projection of server actions/states, never a second authorization engine. Unknown action/state disables unsupported intent; actual authorization remains backend-owned. |

If a consumer is not downstream of its shared owner, update the existing task dependency row explicitly; linking this roadmap is not a substitute for that edge. Keep exports narrow enough not to accidentally pull deferred/phase-inactive widgets into eager closures.

## 3. Common implementation acceptance contract

### Angular data and change notification

- Private writable signals/public readonly signals; mutation through store methods. Replace arrays/maps/objects, do not deep-mutate a `ReadonlyMap` and assume notification. Use `computed` for derivation; reserve `effect` for imperative bridges with teardown.
- Keep typed Reactive Forms. Non-null semantics, disabled field serialization and reset behavior are explicit per form-to-command mapper. Each async form boundary bridges control events to template-read signals or schedules `markForCheck`; never hide missing zoneless notification with timers or repeated forced `detectChanges`.
- Generated HttpClient → data-access mapper → Promise domain port remains the default. No direct `httpResource` from presentation/application, no new client style. Track request/route/session generation so obsolete success/failure cannot revive previous user data, show wrong entity, or reopen realtime.
- Tests use host input binding/`setInput`, `whenStable`, real `HttpTestingController` with `provideHttpClient` before testing provider and outstanding-request verification; router restoration/child guards use `RouterTestingHarness`. jsdom does not prove layout/font/contrast/media/CSP or full focus behavior.

### Visual and behavioral evidence

Every UI implementation task records its **exact approved design frames**, states and backend prerequisites before starting. Existing `-T0` approval is not inferred from a clean design branch. Cover 360/768/1280, vi/en, light/dark, keyboard/focus-visible and loading/empty/error/success/disabled/permission/session-ended states that the surface exposes. Distinguish static-frame coverage, interactive browser proof, and real backend acceptance in reports.

Shared shell has skip link, active navigation semantics and route-change focus; dialogs trap/restore focus and clear scoped actions after navigation. New controls have native semantics and translated accessible names. Automated axe/responsive/DPR checks do not certify WCAG; manual actual zoom/reflow and assistive-technology evidence remain separate.

Production graph proves lazy/defer eligibility, dynamic route closure and deduplicated shared/vendor costs; HMR and minified-name grep are not evidence. Keep reviewed byte ceilings fixed, phase-aware inventory and hidden source-map handling. Media dimensions/alt/sizes/LCP priority are per-screen; never invent signed-URL resizing/CDN loaders. Performance acceptance runs cold-cache production pages under declared network/CPU/viewport/CSP, not just a gzip count.

### Source provenance and version guard

Research provenance: official [Angular style guide](https://angular.dev/style-guide), [zoneless](https://angular.dev/guide/zoneless), [typed forms](https://angular.dev/guide/forms/typed-forms), [signals](https://angular.dev/guide/signals), [routing guards](https://angular.dev/guide/routing/route-guards), [HTTP testing](https://angular.dev/guide/http/testing), [security](https://angular.dev/best-practices/security), [defer](https://angular.dev/guide/templates/defer), [image optimization](https://angular.dev/guide/image-optimization), [a11y](https://angular.dev/best-practices/a11y), and [Playwright practices](https://playwright.dev/docs/best-practices).

Pinned official repository guidance: [angular/angular guidelines at d5f409c](https://github.com/angular/angular/blob/d5f409cfab6a8221266ecaaa35bd469222daed47/adev/src/context/guidelines.md). Moving Angular/CLI/CDK and generator maintainer schemas inform research but do not establish APIs for the pinned release: verify options against the installed package/schema before implementation. Project five-layer architecture, no icon fonts, typed forms and CSP policy are deliberate decisions, not universal Angular requirements.
