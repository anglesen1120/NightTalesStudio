# 10.7.3 — TikTok posting (R2)

> Detailed plan for the R2 TikTok screen family of `plans/10-web-ui.md:189/381`. Contract: `../README.md` **Part C** (§C0 folder brief `:566`, §C1 facts SC7/SC19/SC20/SC28, §C2 findings FC13/FC18, §C3 SD10/SD11–SD16, §C6 template, §C7 R2 gates, `### C8.1` rows `:749-751`, §C9 re-estimate `:776`, §C10 rulings S-O5/ST-6), Parts A (D1–D18) and B (WT-W1…WT-W15) signed context. Backend contracts consumed verbatim: `../../07-tiktok-posting/README.md` §3 (D1–D19), `../../06-youtube-posting/README.md:107` (publication wire), `../../09-backend-api/README.md` Part C (09 S wire), `plans/00-overview.md:99,289,308,363,364`.
> Status: **draft for TL DoR** — the design gate of §11 is not yet recorded as approved (see §6).

## 1. Scope

Ship the three R2 UI tasks of the TikTok area, inside the existing `publishing` feature (no cross-feature import, §C0:566):

| In scope | Out of scope |
|---|---|
| `10.7.3-T0` — UI design for both screens (hard gate, SD11) | Every backend route: connect/callback/creator-info (07.1.2-T1/T2, 07.2.1-T1), validation + consent texts (07.2.2-T1), init + `PULL_FROM_URL` (07.3.1-T1), poll job (07.3.2-T1), audit evidence (07.4.1-T1) |
| `10.7.3-T1` — TikTok connection surface on `/channels`: connect, callback result (`?connected=tiktok`, `?error=…`), connection card with `status`, reconnect card, creator info | The YouTube connect/disconnect rows and `/channels` shell themselves (`10.7.1-T1`); `DELETE /channels/{id}` (06.4.1-T1, already wired by 10.7.1-T1) |
| `10.7.3-T2` — composer `/episodes/:id/publish/tiktok`: caption, disclosure variant + consent text, `privacy = SELF_ONLY` notice, create, consent, status follow | Any browser-side upload, upload progress, chunking or `FILE_UPLOAD` UI (07 D2: `PULL_FROM_URL` only); `publishAt` scheduling (07 D3: manual publishing, no date field) |
| The `tiktok` Transloco scope (SD13), the flag-gated nav entry and the two routes | Credits/billing copy (07 D16: "posting never spends credits" — no `cost-line`; the required `Idempotency-Key` is contract, not scope), moderation UI, the autopilot orchestrator (07 D3/CR-5) |

Deliverables: this `plan.md` + `10.7.3-T0-ui-design.md` + `10.7.3-T1-connect-creator-info.md` + `10.7.3-T2-composer-status.md`. R2 exit criterion (§C7) is met by T1+T2 plus 07.4.1-T1's screencast, which `Depends on` **10.7.3-T2** (§C4 10 CR-27).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| K1 | The area is gated by `feature_flags.tiktok_publish` — nav entry, routes and copy; `tiktok_publish` is owner-only while `TikTokOptions.AuditStatus ≠ approved` (07 CR-6/D4), so a non-owner sees `404 feature_disabled`. | SD10 `../README.md:673`, 07 README D1/D4 ✅ |
| K2 | `POST /channels/tiktok/connect` (bearer, re-auth ≤ 5 min, legal acceptance, flag, **`Idempotency-Key` required** — 07.1.2-T1:24) → `{ authorizationUrl }`; the browser **navigates top-level**; 401 `reauth_required` → `ReauthDialog` + retry once. | SD10 `:673`, 07 D12, `10.7.1-T1:38` (same pattern) ✅ |
| K3 | `GET /channels/tiktok/callback` redirects to fixed paths: `/channels?connected=tiktok`, or `/channels?error=<oauth_state_invalid\|oauth_denied\|tiktok_scope_missing\|oauth_exchange_failed\|channel_already_connected>`; a denied `video.publish` scope is a **302**, never JSON (07 F25). | 07 D12, SD10 `:673` ✅ |
| K4 | `GET /channels/{id}/tiktok/creator-info` → `TikTokCreatorInfoDto { channelId, nickname, avatarUrl, privacyLevelOptions[], commentDisabled, duetDisabled, stitchDisabled, maxVideoPostDurationSec, canPost, blockReason? }`; never cached across users; `canPost = false` + `blockReason` on an error-code 200 or a cap. | 07 D11, §C1 SC19 `:600` ✅ |
| K5 | Consent text: `GET /publications/consent-text?locale=vi\|en&variant=music\|branded` → `{ version, text }`; versions `tt.v1.music` / `tt.v1.branded`; sent as `consentTextVersion` in `POST /publications/{id}/consent` with `If-Match: "pu-<xmin>"`; mismatch → 400 `consent_text_outdated`, refetch + a new explicit click, no auto-retry. | §C1 SC28 `:608`, SD10 `:673`, 07 D5 ✅ |
| K6 | Create: `POST /episodes/{id}/publications` (**`Idempotency-Key` required** — 07.2.2-T1:20,50; 06 wire `:107`, `201 PublicationDto + ETag`) with `tiktok: { disableComment, disableDuet, disableStitch, disclosureEnabled, brandOrganic, brandedContent }`, `title` = caption ≤ **2200 UTF-16 code units**, `description`/`tags`/`publishAt` null, `privacy = SELF_ONLY` the only option, `is_aigc` always on. | SD10 `:673`, SC28 `:608`, 06 README `:107`, 07 D5/D8 ✅ |
| K7 | Transfer is `PULL_FROM_URL` only: 07.3.1-T1 mints a ≥ 2 h presigned URL and TikTok pulls — **the browser uploads nothing and our side shows no progress**. | §C1 SC28 `:608`, §C2 FC13 `:629`, 07 D2 ✅ |
| K8 | Status arrives as `PublicationUpdated` ids `{ publicationId, episodeId }` → refetch `GET /episodes/{id}/publications`; `resync` on reconnect/tab return; no new hub event in this part. | SD10 `:673`, SD16 `:685`, 06 D13, `10.7.4-T1:13` ✅ |
| K9 | Error/copy pairs: 400 `tiktok_invalid_post_settings`/`tiktok_video_too_long`/`consent_text_outdated`, 409 `tiktok_cannot_post_now`/`channel_already_connected`, 429 `tiktok_creator_cap`/`tiktok_rate_limited`, 401 `reauth_required`; publication `error_code` `tiktok_reauth_required` with copy key `errors.tiktok.reauthRequired`; job-time codes `tiktok_active_user_cap`, `tiktok_creator_banned`, `tiktok_content_rejected`, `tiktok_video_invalid`, `tiktok_poll_timeout`, `platform_incident`. | §C1 SC28/SC13, §C2 FC18 `:634`, 07 D9/D10/D18 ✅ |
| K10 | Reuse (do not rebuild): `Channel`/`ChannelStatus`/`ChannelRepository.list()`/`ChannelsStore`/`publishing.routes.ts`/`index.ts` + the `channels` i18n scope (10.7.1-T1), the publication status panel and its `PublicationsStore`/`PublicationDetail`/`TERMINAL`/`canCancel` (10.7.4-T1, `:13,27,45,98`), `StatusChip`/`EmptyState`/`ErrorState`/`LoadingState`/`ConfirmDialog`/`CharCounter` (10.1.3-T1), `AppErrorMapper`/`ConflictDialog`/`RetryCountdown` (10.2.2-T1), `ReauthDialog` (10.3.3-T3), `LegalAcceptanceDialog` (13.2.1-T1), `withIfMatch`/`readEtag` (10.2.3-T1), `RealtimeService` (10.2.4-T1), shell `NAV_ITEMS` (10.2.6-T1), Vitest fakes (D15). | §C3 SD14 `:681`, 10.7.1-T1 `:58,131,133`, 10.7.4-T1 `:136` ✅ |
| K11 | An unaudited client posts private-only (`privacy = SELF_ONLY`, `00:99`); a screen offering public privacy, a browser upload or a fake progress bar is a policy breach (FC13). | §C2 FC13 `:629`, §C1 SC19/SC20 `:600`, S-O5 `:790` ✅ |
| K12 | Estimates: `10.7.3-T0` S (0.5), `10.7.3-T1` M (1.0), `10.7.3-T2` M (1.0) = 2.5 d buffered (`2.0` raw + 0.5 design; the old L split, totals unchanged). | `### C8.1 :749-751`, §C9 `:776`, §C4 10 CR-24 ✅ |
| K13 | Design-first is a hard gate: T1/T2 list `10.7.3-T0` in `Depends on`, and T0's output is the design link + screen/state inventory + the owner approval recorded in this file §6. | §C3 SD11 `:675`, §C6 `:716`, §C7 `:722` ✅ |

⚠️ **Naming note (interface, not a defect in this folder).** §C1 SC19 `:600` names the creator-info fields in TikTok's snake_case (`privacy_level_options`, `max_video_post_duration_sec`); the owning plan 07 D11 fixes the DTO as `TikTokCreatorInfoDto { … privacyLevelOptions[], … maxVideoPostDurationSec, … }`. This plan consumes **07's DTO names** (camelCase, 09's serialization convention) and uses the snake_case strings only when quoting TikTok's own API vocabulary. Reported to the TL with the §C1 line.

## 3. Domain and data

New files, all inside the existing `web/src/app/features/publishing/` (D2 layering: `domain/` — `data-access/` — `application/` — `ui/` — `feature/`; `ui/` files never inject a store, D2/README:110):

```
domain/tiktok.model.ts            TikTokCreatorInfo, TikTokChannelView, TikTokPostSettings,
                                  DisclosureVariant, TikTokValidation, TikTokPostError
domain/tiktok.repository.ts       abstract class TikTokRepository
data-access/tiktok.mapper.ts      toCreatorInfo(dto), toPostBody(input)  (explicit, camelCase)
data-access/tiktok.http-repository.ts   TikTokHttpRepository implements TikTokRepository
application/tiktok.store.ts       TikTokStore (connect + creator info + callback result)
application/tiktok-composer.store.ts    TikTokComposerStore (caption + variant + consent + create)
ui/tiktok-connect-card.ts         presentational; inputs + `connect`/`reconnect` outputs
ui/tiktok-creator-info-card.ts    presentational; inputs only
ui/tiktok-private-notice.ts       the persistent SELF_ONLY notice (shared by both screens)
ui/tiktok-consent-block.ts        consent text + variant radios; inputs + `variantChange` output
feature/tiktok-composer.page.ts   routed container for `/episodes/:id/publish/tiktok`
testing/fake-tiktok.repository.ts hand-written fake (D15)
```

Interfaces (verbatim contract fields, camelCase per 09/07 DTOs):

```ts
export type DisclosureVariant = 'music' | 'branded';
export type TikTokPrivacyLevel = string;                    // wire enum, verbatim (SELF_ONLY today)

export interface TikTokCreatorInfo {                        // 07 D11 TikTokCreatorInfoDto
  readonly channelId: string;
  readonly nickname: string;
  readonly avatarUrl: string | null;
  readonly privacyLevelOptions: readonly TikTokPrivacyLevel[];
  readonly commentDisabled: boolean;
  readonly duetDisabled: boolean;
  readonly stitchDisabled: boolean;
  readonly maxVideoPostDurationSec: number;
  readonly canPost: boolean;
  readonly blockReason: string | null;                      // server text, rendered as-is (07 D18)
}

export interface TikTokPostSettings {                       // SD10's wire object (:673)
  disableComment: boolean; disableDuet: boolean; disableStitch: boolean;
  disclosureEnabled: boolean; brandOrganic: boolean; brandedContent: boolean;
}

export interface TikTokPostInput {
  channelConnectionId: string; title: string;               // caption ≤ 2200 UTF-16 code units
  privacy: 'SELF_ONLY';                                     // the only allowed value while unaudited
  description: null; tags: null; publishAt: null;
  tiktok: TikTokPostSettings;
}

export abstract class TikTokRepository {
  abstract connect(idempotencyKey: string): Promise<Result<{ authorizationUrl: string }>>;  // key REQUIRED (07.1.2-T1:24)
  abstract creatorInfo(channelId: string): Promise<Result<TikTokCreatorInfo>>;
  abstract consentText(variant: DisclosureVariant, locale: 'vi' | 'en'):
    Promise<Result<{ version: string; text: string }>>;
  abstract createPublication(episodeId: string, body: TikTokPostInput, idempotencyKey: string):
    Promise<Result<{ publication: PublicationDetail; etag: string }>>;
  abstract consent(publicationId: string, consentTextVersion: string, ifMatch: string):
    Promise<Result<void>>;
}
```

Store surfaces (signals + `OnPush`, D13 — same shape as `10.7.1-T1:44` and `10.7.4-T1:27`):

```ts
class TikTokStore {                     // provided by the /channels page section, not root-scoped
  readonly creatorInfo = signal<TikTokCreatorInfo | null>(null);   // cached per page (SD10)
  readonly creatorStatus = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  readonly connectState = signal<'idle' | 'starting' | 'navigating' | 'reauth'>('idle');
  readonly callback = signal<{ kind: 'connected' } | { kind: 'error'; code: TikTokCallbackCode } | null>(null);
  readonly featureEnabled = computed(() => this.session.hasFeature('tiktok_publish'));
  connect(): Promise<Result<'navigating' | 'blocked'>>;            // 401 → ReauthDialog → retry once
  loadCreatorInfo(channelId: string, opts?: { force?: boolean }): Promise<Result<void>>;
  readCallback(query: ReadonlyMap<string, string>): void;          // read once, then history.replaceState
  dismissCallback(): void;
}

class TikTokComposerStore {             // provided by the composer route
  readonly caption = signal('');                                  // seeded from episode title
  readonly variant = signal<DisclosureVariant>('music');
  readonly consent = signal<{ version: string; text: string } | null>(null);
  readonly settings = signal<TikTokPostSettings>({ disableComment: false, disableDuet: false,
    disableStitch: false, disclosureEnabled: false, brandOrganic: false, brandedContent: false });
  readonly creator = signal<TikTokCreatorInfo | null>(null);
  readonly submitState = signal<'idle' | 'submitting' | 'created' | 'consenting' | 'done'>('idle');
  readonly publication = signal<PublicationDetail | null>(null);
  readonly error = signal<AppError | null>(null); readonly fieldErrors = signal<FieldError[]>([]);
  readonly validation = computed(() => validateTikTokPost(this.caption(), this.settings(),
    this.creator()));                                             // local mirror, server re-validates
  readonly canSubmit = computed(() => this.validation().ok && this.submitState() === 'idle');
  load(episodeId: string): Promise<Result<void>>;                  // episode + creator info + consent text
  setVariant(v: DisclosureVariant): Promise<Result<void>>;         // refetches consent text (K5)
  submit(): Promise<Result<void>>;                                 // create → consent → status
}
```

`validateTikTokPost` (client mirror only, 07 D5/D8 is authoritative): caption UTF-16 length 1…2200 (`CaptionMaxUtf16Units`, 07 D19), privacy ∈ `creator().privacyLevelOptions`, `brandedContent === true` ⇒ variant `branded` and privacy ≠ `SELF_ONLY`, `disclosureEnabled === false` ⇒ both disclosure flags false, `disableDuet`/`disableStitch` forced `false → true` when `creator().duetDisabled`/`stitchDisabled` (the control is disabled and explained, never silently changed). No client-side duration check beyond showing `maxVideoPostDurationSec` — the video length belongs to the render (07 D5, `tiktok_video_too_long` is the server's answer).

No new table, no SQL and no persistence in this folder: every value is either a server DTO or client form state. `localStorage` is not used; the composer's caption is not persisted (§8).

## 4. Use cases

| Id | Use case | Main flow | States |
|---|---|---|---|
| UC-T1 | Connect TikTok | `/channels` → **Connect TikTok** (`POST /channels/tiktok/connect` + `Idempotency-Key`) → validate `authorizationUrl` (allow-list, §8) → `location.assign` (top-level) | `starting` (button disabled + `aria-busy`), `navigating`, `blocked` (bad URL / legal pending / re-auth) |
| UC-T2 | Callback result | TikTok → `GET /channels/tiktok/callback` → `302 /channels?connected=tiktok` or `?error=<code>` → read once → banner → `history.replaceState('/channels')` → `ChannelRepository.list()` | connected, the five error codes, `tiktok_scope_missing` (K3) |
| UC-T3 | Reconnect a dead grant | connection row `status = 'expired'` or a publication with `tiktok_reauth_required` → reconnect card → same connect flow | expired card, 401 `reauth_required` → `ReauthDialog` + retry once |
| UC-T4 | Creator info | `/channels` mounts the TikTok section → `GET /channels/{id}/tiktok/creator-info` once per page (cached; `force` on user retry) | loading, ready, `canPost = false` + `blockReason`, error |
| UC-T5 | Compose | `/episodes/:id/publish/tiktok` → caption seeded from `EpisodeDetailStore.episode().title` → disclosure variant radios → consent text fetched for the variant and shown **above** the Post button | loading, form, caption/counter, validation error, `consent_text_outdated` |
| UC-T6 | Create | Post → `POST /episodes/{id}/publications` + `Idempotency-Key` with `privacy = SELF_ONLY` + `tiktok{…}` → `201` + ETag → **no upload, no progress** | submitting, created, 400/409/429 mapped |
| UC-T7 | Consent | `POST /publications/{id}/consent { consentTextVersion }` + `If-Match` → `204`? → status follows via `PublicationUpdated` → `GET /episodes/{id}/publications` | consenting, done (status panel embedded), `consent_text_outdated` → refetch + new click |
| UC-T8 | Lost response / double submit | create times out → the same `Idempotency-Key` is replayed on retry (§8), and the store refetches `GET /episodes/{id}/publications` and adopts the existing non-terminal TikTok publication instead of posting a second one | recovering, replayed, adopted, retry allowed |
| UC-T9 | Flag off / role | `tiktok_publish` off (non-owner) → route `canMatch` false → 404 page, nav entry hidden; a 404 `feature_disabled` from an API call hides the area with an explanatory banner | hidden, `feature_disabled` |

Given/When/Then for the two money-adjacent-critical ones are in the task files' `## Acceptance checks` (T1: UC-T1…UC-T4; T2: UC-T5…UC-T9).

## 5. API and events

Consumed verbatim (never re-declared). All requests go through the wave-1 auth interceptor (Bearer, in-memory token, D6) and the D9 error mapper. **Both POSTs that call a platform require an `Idempotency-Key`** (07 C4/D7; 07.1.2-T1:24,57 connect — missing key is 400 `validation_failed`; 07.2.2-T1:20,50,89 create — `RequireIdempotencyKey()`, missing key is 400 `idempotency_key_required`, the same key + body returns the same `201` + `ETag` with no second row, the same key + a different body is 409 `idempotency_conflict`), produced by 10.2.3-T1's `IdempotencyKeys.begin(intent)` / `withIdempotency(intent)` / `settle()` with 10.5.2-T1's discipline: the key is kept while the outcome is unknown (network error, 5xx, timeout) and a fresh key is minted for a new click after a terminal answer (`idempotencyInterceptor` writes the header only when absent, so a 401 re-auth retry keeps it). Consent needs **no** key — it is idempotent on `xmin` and guarded by `If-Match` (07.2-post-screen plan:78). 07 D16 states posting spends no credits, so no `cost-line`.

| # | Route (verb) | Body / query | Success | Client-visible failures | UI reaction |
|---|---|---|---|---|---|
| A1 | `POST /channels/tiktok/connect` | none + **`Idempotency-Key`** | `200 { authorizationUrl }` | 401 `reauth_required`; 403 `forbidden`/`account_under_review`; 409 `legal_acceptance_required`; 404 `feature_disabled`; 400 `validation_failed` (missing key — a client bug) | `ReauthDialog` + retry **once** (same key); `LegalAcceptanceDialog`; hide area |
| A2 | `GET /channels/tiktok/callback` | `?code&state` | `302 /channels?connected=tiktok` | `302 …?error=<oauth_state_invalid\|oauth_denied\|tiktok_scope_missing\|oauth_exchange_failed\|channel_already_connected>` | mapped copy in the result banner (K3) |
| A3 | `GET /channels` | — | `{ items: ChannelConnectionDto[] }` (10.7.1-T1) | — | TikTok rows filtered with `platform === 'tiktok'` |
| A4 | `GET /channels/{id}/tiktok/creator-info` | — | `200 TikTokCreatorInfoDto` (K4) | 401 `reauth_required`; 404 (foreign channel, tenant filter); 404 `feature_disabled` | creator card / blocked card |
| A5 | `GET /publications/consent-text` | `?locale=vi\|en&variant=music\|branded` | `200 { version, text }` | 404 `feature_disabled` | consent block (text is dynamic, never an i18n key — SD13) |
| A6 | `POST /episodes/{id}/publications` | 06 body + `tiktok{…}` (K6) + **`Idempotency-Key`** | `201 PublicationDto + ETag` | 400 `tiktok_invalid_post_settings`, 400 `tiktok_video_too_long`, 409 `tiktok_cannot_post_now`, 409 `channel_already_connected`, 429 `tiktok_creator_cap`, 400 `idempotency_key_required`, 409 `idempotency_conflict`, 403/404 `feature_disabled` | field-level + banner copy (K9), no optimistic row |
| A7 | `POST /publications/{id}/consent` | `{ consentTextVersion }` + `If-Match: "pu-<xmin>"` (no `Idempotency-Key` — idempotent on `xmin`, 07.2-post-screen plan:78) | `204` (10.7.4-T1 `:44` pattern) | 400 `consent_text_outdated`, 409 `publication_invalid_state`, 409 `concurrency_conflict`, 412 stale ETag, 428 `if_match_required` → refetch | refetch consent text + require a **new explicit click** |
| A8 | `GET /episodes/{id}/publications` | — | `{ items }` (no paging) | — | status after consent (via 10.7.4-T1's panel/store) |
| A9 | `GET /episodes/{id}` | — | episode detail | — | caption seed + render readiness |

Events: `PublicationUpdated { publicationId, episodeId }` (ids only, 06 D13) → refetch A8 through 10.7.4-T1's `PublicationsStore`; `resync` (10.2.4-T1) on reconnect/tab return. **No polling** is added by this folder (SD16 `:685` allows `visible-poll` only for `/billing/return` and `/me/exports/{id}`); no new hub event; no SignalR frame is raised for a channel change (06 `plan.md:253`), so the channel list is re-read on page entry and after each action only.

## 6. UI

**Design (SD11 hard gate).** [10.7.3-T0 — UI design](./10.7.3-T0-ui-design.md) — wireframes + hi-fi mockups, screen × state inventory, three breakpoints, vi+en, dark+light, focus order and a11y annotations, built from the D11 tokens/components. **Design link (recorded 2026-10-02):** review deck [`design/ui/index.html`](../../../../design/ui/index.html) → screen 07 [`screens/07-tiktok-billing-account.html`](../../../../design/ui/screens/07-tiktok-billing-account.html): TikTok section [`#f-tt-no-conn`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-no-conn) · [`#f-tt-connecting`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-connecting) · [`#f-tt-connected`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-connected) · [`#f-tt-expired`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-expired) · [`#f-tt-creator-loading`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-creator-loading) · [`#f-tt-creator-error`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-creator-error) · [`#f-tt-blocked`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-blocked) · [`#f-tt-callbacks`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-callbacks) · [`#f-tt-flag-off`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-tt-flag-off); composer [`#f-b-form`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-form) · [`#f-b-validation`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-validation) · [`#f-b-consent`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-consent) · [`#f-b-outdated`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-outdated) · [`#f-b-created`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-created) · [`#f-b-done`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-done) · [`#f-b-failures`](../../../../design/ui/screens/07-tiktok-billing-account.html#f-b-failures). Full state → frame matrix: `10.7.3-T0-ui-design.md` §Outputs.
**Owner approval: 2026-10-02** — approved by the owner, the full review deck v2 "Phòng thu", commit `192cc73` (record in `10.7.3-T0-ui-design.md` §Outputs). The §C7 design gate is satisfied; implementation tasks may start (ST-3 exempts only 10.10.1-*).

Screens (SD10 `:673`, route table `:654` — `/episodes/:id/publish/tiktok (R2)` is the R2 row):

| Screen | Route | Feature | Data (A#) | States to design/implement |
|---|---|---|---|---|
| TikTok section of the channels page | `/channels#tiktok` (section of 10.7.1-T1's page) | `publishing` | A1, A2, A3, A4 | no connection (CTA), connecting, connected (`active`), **expired → reconnect card**, `canPost = false` + `blockReason`, creator info loading/ready/error, callback banner for the five error codes, flag off (area hidden) |
| TikTok composer | `/episodes/:id/publish/tiktok` | `publishing` | A5–A9 | loading, episode not render-ready, creator blocked, form (caption + counter), validation error, consent text loading/missing, `consent_text_outdated`, submitting, created + consent pending, consenting, done (status panel), 400/409/429 mapped, flag off |

Shell integration (each task appends its own entry, WT-W6 precedent, SD1 `:655`): `NAV_ITEMS` gains `TikTok` with `labelKey: 'shell.nav.tiktok'` and `visible: () => hasFeature('tiktok_publish')`; the composer route is registered in `publishing.routes.ts` with `canMatch` `tiktokEnabledGuard` (`hasFeature('tiktok_publish')`, absent/empty `features[]` means **off**) and `data: { titleKey: 'tiktok.composer.title' }`.

Layout and responsive: 360 px single column (caption textarea full width, consent text after the notice, Post button sticky at the bottom of the form), 768 px two columns (creator/notice left, form right), 1280 px the same with a max-width content column; every interactive target ≥ 24 × 24 px (SD12). Dark + light come from the D11 tokens only — no hard-coded colour. `prefers-reduced-motion` disables the notice/CTA transitions; spinners become static text.

i18n (SD13 `:679`): new scope `tiktok` in `web/src/assets/i18n/tiktok/{vi,en}.json` (`tiktok.section.title`, `.connect`, `.reconnect`, `.connectedAs`, `.privateNotice`, `.blocked`, `.creator.*`, `.composer.title`, `.caption.label`, `.caption.counter`, `.variant.music`, `.variant.branded`, `.consent.title`, `.consent.loading`, `.consent.outdated`, `.settings.disableComment|disableDuet|disableStitch|disclosure|organic|brandedContent`, `.submit`, `.submitted`, `.status.*`, `.result.dismiss`, `.error.*` — the last two cover the flag-gated callback banner's dismiss label and the blocked-navigation copy `tiktok.error.badAuthorizationUrl`), plus append-only additions to the `errors` scope: `errors.tiktok.invalidSettings`, `errors.tiktok.tooLong`, `errors.tiktok.cannotPostNow`, `errors.tiktok.scopeMissing`, `errors.tiktok.creatorCap`, `errors.tiktok.reauthRequired`, `errors.tiktok.activeUserCap`, `errors.tiktok.creatorBanned`, `errors.tiktok.contentRejected`, `errors.consent.outdated`, `errors.rateLimited`, `errors.reauthRequired`, `errors.channel_already_connected`, `errors.oauth_state_invalid`, `errors.oauth_denied`, `errors.oauth_exchange_failed`, `errors.feature_disabled`; `core`: `shell.nav.tiktok`. vi is the default and every key exists in both languages (the 10.2.5-T1 CI equality check covers the new scope); `errors.tiktok.*` names are 07 D10/D18's copy keys verbatim. Consented text, `blockReason` and error `detail` are dynamic server text and are **never** keys.

A11y (SD12 `:677` / D16): the private-only notice is a persistent, non-dismissible `role="note"` next to the Post button and is referenced by `aria-describedby` from it; the creator-blocked card is `role="alert"`; the callback banner is `role="status" aria-live="polite"` and focusable; the caption counter is `aria-live="polite"` with the limit exposed as `maxlength`-independent text (UTF-16 units, `CharCounter`); the consent text is a `<section aria-labelledby>` with the variant radios in a `fieldset`/`legend`; while submitting, the Post button carries `aria-busy` and the form is `inert`-free but disabled; the status panel after consent is 10.7.4-T1's (its own announcements).

## 7. Jobs and integrations

No client job, timer, service worker or queue. The only asynchronous integrations:

| Integration | Direction | Contract |
|---|---|---|
| OAuth top-level navigation | out | `authorizationUrl` from A1 → `location.assign` after the §8 allow-list check; the `__Host-nts_oauth` cookie is set by the API response and is never read or written by the SPA (07 D12, `10.7.1-T1:38(c)`) |
| Callback redirect | in | A2's fixed paths; the query is read once and cleared with `history.replaceState` (WD5) |
| Server publishing pipeline | out of scope | 07.3.1-T1 mints the presigned URL, TikTok pulls, 07.3.2-T1's `PollTikTokStatusJob` (20 s, ≤ 30 min) drives the publication to `published`/`failed`; the UI only refetches (K7, K8) |
| Realtime | in | `PublicationUpdated` ids (existing hub only, SD16) |
| Feature flag | in | `feature_flags.tiktok_publish` via `hasFeature` (D14) |
| Caching | in | creator info cached **per page** in `TikTokStore` only (SD10, K4) — no cross-user cache, nothing persisted |

## 8. Security and privacy

- **Authorization**: both routes are behind `authGuard`; the area additionally behind `tiktokEnabledGuard`; the server authorizes every call (tenant filter: a foreign channel id is `404`, never `409`/`403` — 07 D14). The client never assumes a mutation succeeded: every action ends with a refetch (A3/A8).
- **Flag off = hidden**: `404 feature_disabled` (or an absent feature) hides the section/route with an explanatory banner instead of an error state (K1, 10.7.4-T1's `hidden` pattern `:29`).
- **Connect URL allow-list** (extends `10.7.1-T1:38(a)`/WT-W13): before `location.assign`, parse with `new URL(u)`: `protocol === 'https:'`, host exactly `www.tiktok.com`, path exactly `/v2/auth/authorize/`, empty `username`/`password`, no explicit port, and `redirect_uri` parsed to `origin === location.origin` and `pathname === '/api/v1/channels/tiktok/callback'` (the dev origin stays `http://localhost:4200`). A failing value blocks the navigation and shows `tiktok.error.badAuthorizationUrl` (dev-only console error, no `bypassSecurityTrust*`).
- **Never logged / never in the DOM**: `code`, `state`, tokens, `open_id`, the presigned URL and `platform_payload`. `console.*` in this folder is limited to the allow-list diagnostic; the callback query is redacted then replaced (the API already drops the query from the LB log, 07 D14/CR-10).
- **CSP/Trusted Types**: no third-party script, no CDN, no `frame-src` need (K7 means no embedded player/uploader), no `innerHTML`; the composer renders the server's consent `text` as plain text (`{{ }}` binding, never `[innerHTML]`), and no `bypassSecurityTrust*` is used (D4/ST-2). The CSP fixture assertion is part of each task's e2e (10.1.6-T1).
- **Privacy**: an unaudited client posts private-only — the notice states it and `privacy` is not user-selectable (K11, S-O5). Creator `nickname`/`avatarUrl` render from the DTO; the avatar loads from the API/vendor URL without referrer leakage (`referrerpolicy="no-referrer"`), and no caption/nickname/`open_id` is put into URLs, logs or telemetry (07 D14/D18).
- **Secrets**: none. This folder reads no key, no token and no site key (the CAPTCHA/runtime-config item is 10.3.3-T2's, CR-28).
- **Double-submit / CSRF**: connect and create are state-changing `POST`s through the interceptors with the in-memory Bearer and a required `Idempotency-Key`; the UI disables the button while in flight, the intent (and therefore the key) survives a network error / 5xx / timeout so a retry replays the same logical command, and a new click after a terminal answer mints a fresh key. A missing-key 400, `422 idempotency_key_reused` or 409 `idempotency_conflict` is a client bug: dev console error + `errors.unknown_error` with `traceId` to the user (10.5.2-T1:67 pattern). Consent carries `If-Match` and no key; on a lost response the client refetches instead of resubmitting (§4 UC-T8).
- **Audit**: consent is audited server-side (`publishing.consented`, 07 D5); this folder emits no client audit and no analytics.

## 9. Test plan

Projects: Vitest + Angular TestBed for components/stores (D15: hand-written `fake-*` repositories from `NightTales.TestKit`-style fakes — **no mocking library**), Playwright + axe for e2e (10.1.5-T1's `e2e.yml`, WR3), and the existing CSP fixture (10.1.6-T1) and per-route budget check (`tools/web/check-gzip-budget.mjs`, FC8 — the assertion for the `tiktok` chunk is 10.10.1-T2's).

| Level | Spec | Behaviour asserted |
|---|---|---|
| Unit (store) | `tiktok.store.spec.ts` | allow-list rejects a non-`www.tiktok.com` URL and does not navigate; 401 `reauth_required` → `ReauthDialog` → exactly **one** retry carrying the **same** `Idempotency-Key`, and a new click after a terminal answer carrying a **fresh** one; callback values map to the five codes and to `connected`; creator info fetched once per page and `force` refetches |
| Unit (store) | `tiktok-composer.store.spec.ts` | the create keeps its `Idempotency-Key` across a timeout/replay (one logical Post = one key) and mints a new one for a new click; 409 `idempotency_conflict` surfaces as a client bug; caption > 2200 UTF-16 units blocks submit; the variant change refetches consent text; `Is-aigc`/`privacy` are always `SELF_ONLY`; `disclosureEnabled = false` clears both disclosure flags; duet/stitch forced off when the creator info says so; `consent_text_outdated` clears the text and requires a new click (no auto-retry); a create timeout adopts an existing non-terminal publication instead of resubmitting |
| Component | `tiktok-connect-card.spec.ts`, `tiktok-creator-info-card.spec.ts`, `tiktok-consent-block.spec.ts`, `tiktok-composer.page.spec.ts` | every SD1 state renders (loading/empty/error/success/blocked/expired/flag-off); the captions counter shows UTF-16 units; the Post button is disabled until the consent text is displayed; the notice is present and non-dismissible |
| e2e | `web/e2e/tiktok-post.spec.ts` | UCT-1…UCT-9 against the compose stack: connect → callback banner → connected card; the composer against 07's stubs/FakeTikTokServer contract; **no upload UI and no progress element exists** (negative assertion on the DOM); axe tags `wcag2a`,`wcag2aa`,`wcag21a`,`wcag21aa`,`wcag22aa` (SD12); zero `securitypolicyviolation` events on both pages; vi + en runs; the `tiktok` chunk is not referenced by the initial bundle |

Test data: creator info with `privacyLevelOptions: ['SELF_ONLY']` and one public option (audit-approved case), `commentDisabled`/`duetDisabled`/`stitchDisabled` in both polarities, `canPost: false` + `blockReason`, both consent variants, and the create failures of K9. No test asserts wiring, DTO copying or source text; every test names a behaviour.

## 10. Tasks and re-estimate

| Task | Size | Area | Phase | Depends on | Blocks |
|---|---|---|---|---|---|
| 10.7.3-T0 | S (0.5) | UX | S · R2 | 10.1.3-T1, 07.2.1-T1 (soft) | 10.7.3-T1, 10.7.3-T2 |
| 10.7.3-T1 | M (1.0) | FE | S · R2 · flag `tiktok_publish` | 10.7.3-T0, 07.1.2-T1, 07.1.2-T2 (soft), 10.7.1-T1, 06.5.2-T1, 07.2.1-T1 | 10.7.3-T2, 07.4.1-T1 |
| 10.7.3-T2 | M (1.0) | FE | S · R2 · flag `tiktok_publish` | 10.7.3-T0, 10.7.3-T1, 07.2.1-T1, 07.2.2-T1 (soft), 07.3.1-T1 (soft), 07.3.2-T1 (soft), 10.7.4-T1 | 07.4.1-T1 |

2.0 raw + 0.5 design = **2.5** (§C9 `:776`; the high-level L of `plans/10-web-ui.md:381` splits into two M per 10 CR-24 with the total unchanged). No task exceeds L; nothing here is sequential beyond T0 → T1 → T2.

Interfaces this folder produces for others: the `tiktok` i18n scope, `TikTokPostInput`/`TikTokCreatorInfo`, `TikTokRepository` + `HttpTikTokRepository` + `FakeTikTokRepository`, `TikTokStore`/`TikTokComposerStore` and the composer route — consumed by 07.4.1-T1 (audit evidence, which depends on T2) and audited by 10.10.1-T2 (publishing screens + per-route budget).

Open items to close at DoR ([OPEN], no invented contract):
1. **[OPEN] `?connected=tiktok` reader.** SD10 (`:673`) and 07 D12 fix the callback redirect to `/channels?connected=tiktok` with five error codes, but §C1/§C3 assign that page's query reading to 10.7.1-T1, whose closed code set covers only YouTube. This plan therefore specifies a **minimal additive edit** to 10.7.1-T1's `channels.store.ts`/`ChannelResult` (accept `'tiktok'` and `tiktok_scope_missing`, no behaviour change for the YouTube values) and records it in T1's Changed list with the owner notified (WT-W6/WT-W10 precedent). TL confirmation requested — reported with the README lines.
2. **[OPEN] Nav entry target.** SD1 (`:655`) requires a `TikTok` nav entry under `hasFeature('tiktok_publish')`, while the R2 surface is the TikTok section of `/channels`; this plan points the entry at `/channels` (no new route is invented; the composer needs an episode id). A distinct `/tiktok` route is a DoR decision, not assumed here.
3. **[OPEN] `tiktok` wire spelling.** §C1 SC19's snake_case field names vs 07 D11's camelCase DTO — resolved in favour of 07 D11 (K-note in §2). TL to confirm the §C1 wording.
4. **[OPEN] `tiktok_publish` rollout precondition.** 07 D4/CR-6 keep the flag owner-only until 07.4.1-T2 flips `AuditStatus` to `approved`; the R2 exit run must use the owner's own private test account (07 D17).
5. **[OPEN] Composer route mount.** The R2 route `/episodes/:id/publish/tiktok` is fixed by §C3 SD1 `:654`/SD10, but 10.7.2-T1 rendered the analogous YouTube form as an additive `@case` panel in `features/episodes/feature/story-review.page.ts` ("No new route", `10.7.2-T1:68`) instead of a route. T2 registers the composer as a **child of the `/episodes/:id` route** (so the wave-1 `EpisodeDetailStore` provider is inherited and the episode is not fetched twice) — one additive line outside this folder — with the `@case` panel as the documented fallback. TL decision requested.
6. **[OPEN] `feature_disabled` status drift.** 07.1.2-T1:24/plan:118 return **404** `feature_disabled` for the channel routes while 07.2-post-screen plan:77 lists `feature_disabled` as **403** for the publications routes. This plan treats both as "area off" (hide, never an error state) and asks the TL which status the SPA should expect; the wire `code` is identical, so no client branch is added for it.

## 11. Rollout

1. **Gate check (blocking).** Timeline facts: 07.1.2-T1/T2 + 07.2.1-T1/T2 + 07.3.1-T1/T2 merged, `06.5.2-T1` exists and `tiktok_publish` is settable (`nts-worker flags set tiktok_publish true` for the owner), 10.1.3-T1 (theme + UI kit) and 10.7.1-T1 (`/channels`, `ChannelRepository`, `index.ts`, `publishing.routes.ts`) merged. Without 07.2.1-T1 the creator-info contract is a stub and T0's design cannot be signed (`10.7.3-T0 Depends on 07.2.1-T1`, §C8.1).
2. **Order**: `10.7.3-T0` (design, in parallel with 10.7.3-T1's read-only prep) → **owner approval recorded in §6** → `10.7.3-T1` → `10.7.3-T2` → `07.4.1-T1` (audit evidence/screencast) → `10.10.1-T2` (axe + per-route budget). Nothing here blocks the M2 exit (§C5 step 6).
3. **Rollout state**: the flag ships **off**; enabled first for the owner's own account only (07 D4/CR-6), then for the `approved` audit state via 07.4.1-T2. A rollback is `nts-worker flags set tiktok_publish false`: nav entry, the composer route and the `/channels` TikTok section disappear; existing TikTok publications stay visible through 10.7.4-T1's panel (they are ordinary publications) and in-flight server jobs keep running (07 D16).
4. **Post-deploy checks**: connect → callback → connected card on the real sandbox (or 07's stubs, §C7 R2 exit); composer against a private test account with `privacy = SELF_ONLY`; the R2 exit checklist of §C7 signed; 10.10.1-T2's axe + budget rows for `/channels` and `/episodes/:id/publish/tiktok` in the release report.

## 12. Angular implementation amendment (2026-10-02)

### Design evidence and implementation reconciliation notes

- Static evidence is in the approved deck: TikTok frames in [`07-tiktok-billing-account.html`](../../../../design/ui/screens/07-tiktok-billing-account.html): channel `#f-tt-*` and composer `#f-b-*`, including `#f-b-private-notice`, `#f-b-consent`, `#f-b-outdated` and `#f-b-failures`.
- Consent-text-missing, mapped alert states for each required 400/409/429 outcome, and private-only notice persistence in every submit-capable state follow the locked contract; flag to the owner only if a new visual state is needed (does not reopen the approval or block the task). Composite `#f-b-failures` and a separate private-notice specimen are insufficient for those invariants.

### Angular boundaries and integration readiness

- The TikTok section composes into `/channels`; the composer is its own lazy R2 container. `TikTokStore` and `TikTokComposerStore` are page/route scoped; their writable signals are private and public state readonly/immutable. Repository/data-access owns generated DTO mapping, ETag/full-response handling and command headers. Request/session/episode/variant generations discard late creator-info, consent-text, create or consent results; cleanup removes callbacks/listeners and keeps the R2 chunk absent from active P/S closures.
- The typed composer form uses an explicit mapper for UTF-16 caption semantics, nullable `description`/`tags`/`publishAt`, fixed `SELF_ONLY` and the six TikTok fields. Async creator/consent updates, control disablement and server field errors use the zoneless form bridge. Consent outdated always refetches and waits for a new explicit click; unknown/absent capabilities fail closed.
- TikTok is R2-only and remains separate from YouTube P/S repositories, commands and rollout flags. No frame authorizes browser upload, progress, public privacy, scheduling, export or any new capability. Real integration waits for the named `07.*` owners and audit state; mockup coverage never activates the flag.

No task ID, size, phase, dependency or estimate changes in this amendment.
