# Backend integration readiness — Angular plan 10

Audit date: 2026-10-02. Source snapshot: `develop` at `00e7d5d5415579c7df55a66dc2177cbc2e31eb78`. This is a source/contract audit, **not** a runtime endpoint certification. Concurrent task worktrees are excluded. Recheck the merged API artifact before starting each integration task; this table must not freeze later backend progress.

Authority: [web shared contracts](README.md), [backend contracts](../09-backend-api/README.md), and the existing feature/task headers. A surface marked **present** may be adapted against the pinned OpenAPI artifact; its real-system acceptance still has to run. A surface marked **planned** is a signed target contract with a backend owner, not permission to fabricate generated fields, grant capabilities in the browser, or claim completion using fakes.

## 1. Present source contracts

All HTTP paths below include their actual prefix. WebSocket paths are separate.

| Surface / web consumers | Present response and behavior | Source evidence (repository-relative) |
|---|---|---|
| Session — `10.2.1-T1`, login/reauth `10.3.3-T1/T3` | `POST /api/v1/auth/login` → `{accessToken,expiresAt,role,amr}`; refresh → `{accessToken,expiresAt}`; logout → 204; reauth → `{reauthenticatedAt}`. Refresh/logout enforce same origin. MFA-required codes do not yet imply MFA challenge payload readiness. | `src/NightTales.Api/Endpoints/AuthEndpoints.cs:61–100,106–134,145–224,235–287,309–327` |
| MFA management — `10.3.3-T3` (the S `10.9.5-T1` consumer is deleted by DEC 10.14) | `POST /api/v1/me/mfa/setup` → `{secret,otpauthUri}`; verify/regenerate → recovery-code response; disable → 204. Scope/reauth rules apply. The `/me` group is not proof that profile GET/PATCH exists. | `src/NightTales.Api/Endpoints/MeEndpoints.cs:38–90,96–170` |
| Series — `10.4.2-T1`, dashboard `10.4.1-T1` | CRUD under `/api/v1/series`. List `{items,nextCursor}`; PATCH requires `If-Match` and returns `{etag}` plus `ETag`, **not** a full series. DELETE archives. Map explicit null versus absent `storyPreset`; language is immutable after creation. | `src/NightTales.Api/Endpoints/SeriesEndpoints.cs:52–88,94–144,155–216`; `src/NightTales.Application/Features/Series/Queries/GetSeries/SeriesDto.cs:30–47` |
| Episodes — `10.5.1-T1`, detail `10.5.2-T1` | Create `/api/v1/series/{seriesId}/episodes` body `{title?,brief,lengthTier}` → JSON UUID scalar + `Location`/`ETag`, **not** `{id}`. List `/api/v1/episodes` → `{items,nextCursor}`; limit 1–100, snake_case status. DELETE cancels, optional `If-Match`. | `src/NightTales.Api/Endpoints/EpisodeEndpoints.cs:43–110,124–154,210–227` |
| Basic episode detail — `10.5.2-T1` | `{id,seriesId,title,brief,lengthTier,status,nextActions,etag,renderJob,finalVideo,publications}`. `If-None-Match` supports 304; `Vary: Authorization`, `Cache-Control: no-cache`. Current projection sets `renderJob`/`publications` null; `finalVideo` has metadata, not a playable URL. Editor fields are planned extensions. | `src/NightTales.Api/Endpoints/EpisodeEndpoints.cs:181–208`; `src/NightTales.Application/Features/Episodes/Queries/GetEpisode/EpisodeDetailDto.cs:46–61`; `GetEpisodeByIdHandler.cs` in the same directory `:72–76,105–126` |
| Voice/style catalogs — `10.4.2-T1/T2` | `/api/v1/voices?language=` and `/api/v1/style-templates?topic=` → `{items}`, not cursor pages. HTTP cache/ETag/304 are supported. | `src/NightTales.Api/Endpoints/CatalogEndpoints.cs:56–123,142–159` |
| Realtime transport — `10.2.4-T1` | `POST /api/v1/hubs/ticket` → `{ticket,expiresAt}`, no-store; socket `/hubs/episodes?ticket=…`, WebSockets/skipNegotiation. Hub automatically joins `user:{id}`; `subscribeEpisode` is a **client-side filter**, not a hub RPC. Single-use ticket/reconnect/activity contracts remain mandatory. | `src/NightTales.Api/Endpoints/HubEndpoints.cs:32–78`; `src/NightTales.Api/Realtime/EpisodeHub.cs:17–55`; `src/NightTales.Api/Program.cs:159–166` |
| Realtime envelope | `RealtimeEvent(Name,Ids,Data?)`; only `RenderProgress` forwards data; other events invalidate by ids. Transport readiness is not event-producer readiness. | `src/NightTales.Application/Abstractions/Realtime/RealtimeEvent.cs:17–29` |
| Error mapping — `10.2.2-T1`, all adapters | `application/problem+json`: `{type,title,status,code,traceId,errors?}`, allowlisted scalar metadata at the **root**. `errors` maps field → string[]. `Retry-After` is a header. No universal success envelope. | `src/NightTales.Api/Http/ProblemDetailsWriter.cs:85–91,106–178,356–422,549–565` |

### Adapter acceptance, not generated-type guesses

1. Consume full HTTP response variants inside data-access when headers/status matter; expose mapped domain values, never `HttpResponse`, to stores.
2. Preserve scalar create IDs, partial PATCH `{etag}`, unpaged catalog `{items}`, and bodyless 204/304. A 304 retains the previous representation; it is not an empty entity.
3. Map wire `errors` → internal `fieldErrors`; parse `Retry-After` → `retryAfterSec`; retain sanitized trace/reference data. `AppError.problem` is an internal field, not a nested server envelope. Do not show raw server `detail` or arbitrary metadata as UI text.
4. Explicit DTO-to-model and form-to-command mapping handles absent/null fields and closed enums. Unknown actions/stages fail closed at the presentation boundary; permissions and state transitions remain server-authoritative.
5. ETags belong to the resource/version actually returned. Mutations preserve `If-Match`, intent-scoped idempotency and duplicate-submit prevention; do not normalize all endpoints to the same response shape.
6. Use generated artifacts from the backend SHA recorded in the API lock/provenance. Never hand-edit generated models to add future fields. Adapter tests may use fixtures; final feature acceptance uses the real merged API.

## 2. Planned surfaces and hard integration prerequisites

These rows summarize owners; **task headers remain the exact dependency authority**. Multiple owners on a row do not add every one as a new hard edge to every consumer.

| Surface | Backend owner / target | Web consumers / readiness boundary |
|---|---|---|
| MFA sign-in/challenge/enrolment token | `09.3.2-T3` | `10.3.3-T3`; cannot complete owner sign-in from existing password-login/MFA-management routes alone. |
| Profile `GET/PATCH /api/v1/me`, `uiLocale`, allowlisted `features[]` | `09.3.4-T1`; flags prerequisite `09.9.2-T2` per current lane | `10.2.6-T1`, account; shell must not invent a profile or enable flags from mockups. |
| Forgot/reset password | `09.3.3-T1` after mail owner `09.2.6-T3` | `10.3.3-T1`; mail infrastructure is present but reset routes are not. Fragment clearing/neutral responses remain planned acceptance. |
| Static privacy/terms versus consent API | Content `13.1.1-T1/13.1.2-T1`; API/dialog `13.2.1-T1` | Static `10.3.1-T1` does not require a runtime API during prerender. Legal acceptance/publishing gates do require their backend owner. |
| Registration/verification/CAPTCHA (S) | `09.3.1-T3`, hardening `09.3.1-T2`, CAPTCHA `09.3.3-T2` | No P web consumer: `10.3.2-T1` and `10.3.3-T2` are deleted by DEC 10.14; closed registration is the P behaviour. |
| Story/currentStory/generation/edit/approval/history | `02.3.4-T2`, `02.2.2-T2`, `02.3.1-T1`, `02.3.2-T1`, `02.3.3-T1`; source/bible/autopilot owners in existing `02.5–02.8` dependency rows | Editor `10.5.2–10.5.10`, autopilot `10.4.3`; basic episode detail is insufficient. Follow each task's owner edge rather than fabricating a combined DTO. |
| Images and detail extensions | `03.4.2-T1` and owning image tasks | Editor/render consumers wait for real projection, media and moderation contracts. |
| Render enqueue/progress/cancel/output | `09.6.1-T1`, `09.6.2-T1`, `09.6.3-T1/T2`, `09.6.4-T1` | `10.6.1-T1/T2`. `RenderJobSummaryDto` already declares `errorCode`/`hints`, but the current detail projection returns null: **schema present, producer/projection not ready**. |
| Signed video URL and review | `09.7.2-T1` (storage prerequisite `11.1.1-T2`); `09.7.1-T1` (review) | `10.6.2-T1`; `{url,expiresAt}`/expiry/review are planned. Storage code in another worktree is not merged availability. |
| Channels, YouTube publishing, consent, ETA, public cancel | `06.1.2-T1/T2`, `06.4.1-T1`, `06.2.1-T1`, `06.2.2-T1`, `06.3.1-T1`, `06.2.5-T1`, `06.2.6-T1` | `10.7.1-T1`, `10.7.2-T1`, `10.7.4-T1/T2`; P includes only flag-authorized target flows after owners land. |
| TikTok | Owning `07.*` tasks, exact edges in `10.7.3-T1/T2` | P (R2 lane, flag `tiktok_publish`); mockup coverage does not bring a screen in by itself. |
| Account activity/sessions/export/deletion | `09.3.6-T2`, `09.3.7-T1`, `13.3.3-T1`, `13.3.2-T1` | **No web consumer**: `10.8.2-T1` (account settings/export/deletion) and the rest of `10.8` are deleted with phase S by DEC 10.14 — none of them had a commit on `develop` (verified 2026-10-21). The owner is the only user and runs export/deletion from the backend surfaces directly. |
| Admin/flags/settings/nodes/governance | The S write paths and settings API (`09.10.5-T1`, `09.9.1-T1`, `09.9.2-T1`, `09.10.2-T1/T2`, `09.10.4-T2/T4`, `09.10.8-T1`) had their task files deleted with phase S (DEC 10.14), so no admin write path, flag write path or settings API is planned. `09.9.2-T2` (flag read service) stays P. | **No web consumer**: `10.9.3-T1` (owner settings nodes) is deleted too — verified 0 commits on `develop` — and `10.9` admin shell/user-management/settings screens are withdrawn with phase S. Integration keys and base URLs stay in the runtime secret manager (DECISIONS §10.11). Existing reauth/MFA is not proof of admin endpoints or authorization policy readiness. |
| Credits/billing/payment/refund/funding | Plan 12 is deleted (DEC 10.14), including `12.3.2-T1`, `12.7.4-T1`, `12.1.3-T1`, `12.2.2-T1`, `12.7.3-T1`, `12.6.1-T1`. | No web consumer: `10.8.1-T1`, `10.8.3-T1`, `10.8.4-T1` and `10.9.2-T1` are deleted by DEC 10.14; never display balance/quotes/buy controls in P. The owner's own OpenAI spend metering (`usage_records`, spend caps) is backend-only and is not a billing surface. |

Evidence for planned owners: web README dependency tables (§7, §Wave-2 dependencies, §Wave-3 dependencies), backend README inventory/dependency rows, and API composition `src/NightTales.Api/Program.cs:142–166`. Reconfirm readiness after each owner merges; a name-only grep cannot establish absence or compatibility.

## 3. Per-feature integration exit gate

Before a web task is called integrated: record backend SHA/OpenAPI provenance; compare method/path, parameters, body keys, enum casing, optional/null semantics, response/header/status variants, catalogued error codes and event payloads; run positive plus conflict/permission/session-ended/duplicate/late-response cases against the real system. Freeze only the DTO subset the task actually consumes. Unsupported planned operations remain named blockers, not fake success fallbacks.

Backend ownership stays in its current session. This planning audit does not change its schedule, implement missing routes, certify deployed behavior, or close any feature acceptance check.
