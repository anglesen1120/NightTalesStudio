# 09.4.2, 09.5.3 — Catalog queries and episode detail

> Parent: ../README.md (Part B)

## 1. Scope

**Stories.** 09.4.2 *"As a creator I want voices and styles listed so that I can pick them."* Must · **S 0.5**. 09.5.3 *"As the UI I want one call for the episode page so that it loads fast."* Must · **S 0.5** (both sizes from README B8.1/B9).

**Refines.** `../../../09-backend-api.md` §4 epic 09.4 (story **09.4.2**, L158–161) and epic 09.5 (story **09.5.3**, L165–166); `../../../00-overview.md` §6 (`voices`, `style_templates`, `episodes`, `assets`), §7 (episode state machine), §8 routes (L353, L360).
**Contract.** `../README.md` — **D4**, **D7**–**D10**, **D14**, **D15**, **D16**, **D17**, **BD1**, **BD16**; 04 README **D9** (voice item shape); 03 README **D2** + **CR-2**, 03.1 plan L53 (`StyleTopicPolicy` signature).
**Template.** `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
**Status.** detailed plan, 2026-09-24. Nothing implemented yet.

**In scope.**

- `GET /voices?language=` — only `active` rows; item `{ id, displayName, language, gender, tags, previewUrl }` (04 D9 ✅); `previewUrl` = public URL of `preview_storage_key` in bucket `nt-public-<env>`, or `null` (09 L161 ✅).
- `GET /style-templates?topic=` — `{ id, name, topicHint, previewUrl }`, filtered by `StyleTopicPolicy.IsAllowed(Topic, string?)` (03 CR-2 ✅, 03.1 plan L53 ✅); `active = false` rows are hidden but stay usable by existing series (03 README L98 ✅).
- `GET /episodes/{id}` — **extends the query that 09.5.1-T1 already ships** (`GetEpisodeQuery(Guid Id) : IQuery<ErrorOr<EpisodeDto>>`, 09.5.1-T1 L37; route `GET /api/v1/episodes/{id}` → `200 + ETag`, L71; `Cancelled` → 404, L63; and L126 "09.5.3 extends `EpisodeDto` with `nextActions`, `currentStory` and `generation`"). 09.5.3-T1 renames that DTO to **`EpisodeDetailDto`** (the name BD1 gives 09.5.3-T1 ✅) and adds: `status`, `nextActions` computed by the domain, latest render-job progress (`stage`/`percent`, 00 §8 L353 ✅), final video asset metadata, a publications summary placeholder for 06, and the `etag` field.
- Conditional GET: `ETag: "ep-<xmin>"` (D15 ✅) plus `If-None-Match` → `304` [INFERENCE: 304 is not in D7's helper list]; catalog routes get cache headers + a cheap ETag [INFERENCE].
- Documented extension points: 02.3.4-T2 adds `currentStory`/`generation` (BD1 ✅, 02 CR-7 ✅); 03.4.2-T1 adds `renderJob.images` (BD1 ✅).

**Out of scope.**

- Any write route, any new table, any migration: `voices`, `style_templates`, `episodes`, `assets` are created by 09.2.2 `InitialCore` (D9 ✅); `render_jobs` by 09.6.1-T1; `publications` by 06 (not in P).
- `GET /episodes` list and pagination (09.5.1-T1), `GET /episodes/{id}/video-url` (09.7.2-T1), `GET /episodes/{id}/publications` (06), `GET /episodes/{id}/activity` (S, 02 CR-3).
- `IBlobStore`/presigning (BD10, 09.7.2-T1): this folder builds **public** catalog URLs from configuration only, so 09.4.2-T1 keeps B8.1's single dependency and does not take an edge on 09.7.2-T1.
- Seeding the two catalogs (09.2.5-T1, 04.2.2-T2, 03.1.1-T1/03.1.2-T1) and any real-time publishing (`EpisodeUpdated` → 09.8.1-T1).

## 2. Confirmed facts

| Id | Fact | Source |
|---|---|---|
| Q1 | Queries read through `IReadDb.Query<T>()` (no tracking, tenant filter applied); `ReadDbContext.SaveChanges*` throws | D8 ✅ |
| Q2 | `voices`, `style_templates`, `assets` (+ `episodes`) exist in the 09.2.2 `InitialCore` migration; 09 adds no migration here | D9 ✅ |
| Q3 | Other users' rows answer **404 `not_found`**, never 403 | D10 ✅ |
| Q4 | camelCase JSON, snake_case enum strings, UTC ISO-8601, base `/api/v1`, `IEndpointGroup` | D14 ✅ |
| Q5 | Episode ETag = `"ep-<episodes.xmin>"`, returned in the `ETag` header **and** the DTO field `etag` | D15 ✅ |
| Q6 | `Topic { Ghost, Spiritual, TrueCrime }` (+ `IsFiction()`), `Language { Vi, En }`, `EpisodeStatus` (11 values) live in Domain | D16 ✅ |
| Q7 | `StyleTopicPolicy.IsAllowed(Topic topic, string? topicHint)`: `true_crime` → hint must be `true_crime`; other topics → hint ≠ `true_crime`; `src/NightTales.Domain/Catalog/StyleTopicPolicy.cs` | 03.1.1-T2 L54, 03.1 plan L53 ✅ |
| Q8 | Voice item shape + `previewUrl` from `preview_storage_key` in `nt-public-<env>` or `null`; only `active` voices | 04 D9; 09 L161 ✅ |
| Q9 | `GET /style-templates?topic=` filters by the same policy as series create/update; result `{ id, name, topicHint, previewUrl }`; labels come from i18n keys `series.style.<name>.label` / `.hint`, not the DB | 03 CR-2; 03.1 plan §5 ✅ |
| Q10 | Baseline episode-detail content: status, allowed next actions (computed by the domain), current story, latest render-job progress, final video asset metadata, publications summary, ETag | 09 L166 ✅ |
| Q11 | 02 CR-7 adds `generation { kind, status, stage, errorCode, startedAt, processingPaused }` and `currentStory { … }` | 02 README CR-7; 09 L166 ✅ |
| Q12 | Ownership: `GET /voices` + `GET /style-templates` = 09.4.2-T1; `GET /episodes/{id}` + `EpisodeDetailDto` + `EpisodeNextActions` = 09.5.3-T1; consumers 10.4.x, 04.2.3-T1, 10, 02.3.4-T2, 03.4.2-T1 | BD1 ✅ |
| Q13 | Performance rule: episode detail = **one projection query + one render/job query**, no N+1 — and the two run **sequentially** on the one `DbContext` ([RULED — README §B10 WT46]) | BD16 ✅ |
| Q14 | `IReadDb` cannot write, so the post-commit `xmin` read pattern of 09.4.1-T1 (D15 delegate) is reused verbatim for the episode ETag | 09.4.1-T1 L49, L105 ✅ |
| Q15 | The 09.4.2/09.5.3 estimate stays **1.0 d**; no re-estimate (both S) | B9 ✅ |
| Q16 | No Part B **W**-fact changes these two tasks; they are governed by the **BD** decisions above ⚠️ | B1, B3 ✅ |

## 3. Domain and data

**No new tables, no migration.** Reads only, on tables owned elsewhere (D9). `voices` / `style_templates` rows are seeded by 09.2.5-T1 (`VoiceSeeder`, `StyleTemplateSeeder`) and replaced later by 04.2.2-T2 / 03.1.1-T1; both seeders upsert the same columns this folder reads: `id`, `display_name`, `language`, `gender`, `tags` (text[]), `preview_storage_key`, `active`, plus `name` / `topic_hint` for templates (04 D9, 03 D2 ✅).

**The only Domain code added by this folder** — one new partial file, so 09.5.1-T1's `src/NightTales.Domain/Episodes/Episode.cs` is never edited (D16 keeps `Episode.cs` under 09.5.1):

```csharp
// src/NightTales.Domain/Episodes/EpisodeNextActions.cs   (new; artefact name from BD1)
public enum EpisodeNextAction
{
    GenerateStory, EditStory, ApproveStory, ReopenStory,
    Render, RetryRender, Cancel, ApproveVideo, RejectVideo, Publish, Download
}

public static class EpisodeNextActions
{
    /// Computed by the domain from 00 §7 (D16 statuses). Never reads the clock, never returns ErrorOr.
    public static IReadOnlyList<EpisodeNextAction> For(EpisodeStatus status);
}
```

⚠️ Member list and the `For` signature are **[INFERENCE]** (BD1 fixes only the artefact name `EpisodeNextActions` and "computed by the domain"): the members are derived from 00 §7 / D16 and must be re-checked against 00 §7 at implementation. `EpisodeNextActions` is deliberately **not** a `[Flags]` enum, because D14 serializes enums as snake_case strings and a flags value would produce a comma-joined pseudo-value; the API therefore exposes `nextActions: string[]`.

Projection shapes (single round trip each, Q13):

```csharp
// voices: one projection, ordered by id for a stable ETag
IReadDb.Query<Voice>().Where(v => v.Active && (language == null || v.Language == language))
     .OrderBy(v => v.Id)
     .Select(v => new { v.Id, v.DisplayName, v.Language, v.Gender, v.Tags, v.PreviewStorageKey });

// style_templates: policy is applied in memory after the row read (small, closed set — see §4 UC2)
IReadDb.Query<StyleTemplate>().Where(t => t.Active)
     .OrderBy(t => t.Id)
     .Select(t => new { t.Id, t.Name, t.TopicHint, t.PreviewStorageKey });
```

`previewUrl` is composed from configuration, not from storage code (BD10 not required):

```csharp
public interface IPublicAssetUrl   // Application/Abstractions/Storage/IPublicAssetUrl.cs  [INFERENCE: name]
{
    Uri? Build(string? storageKey);   // null key, empty base URL or an unsafe key → null
}
public sealed class ConfigurationPublicAssetUrl : IPublicAssetUrl { /* IOptions<StorageOptions>.PublicBaseUrl */ }
```

Config keys with defaults:

| Key | Default | Notes |
|---|---|---|
| `Storage:PublicBaseUrl` | `""` (empty) | Base URL of bucket `nt-public-<env>` (11 README L253 ✅). Empty → every `previewUrl` is `null` and a **startup warning** is logged (dev is allowed to be empty). [INFERENCE: key name; no such key exists in any signed file yet] |
| `Catalog:PublicCacheSeconds` | `3600` | `max-age` for `GET /voices` and `GET /style-templates`. [INFERENCE] |

Key safety: reject a stored key that is absolute (`Uri.IsWellFormedUriString(key, Absolute)`) or contains `..`/`\`/a leading `/`, and reject a base URL that is not `https` outside Development → treat as `null` + one warning (never echo the key back to the client).

## 4. Use cases

| # | Actor → goal | Flow | Result |
|---|---|---|---|
| UC1 | 10.4.x voice picker → list selectable voices | `GET /voices?language=vi` → projection → `Build(preview_storage_key)` per row | `200 { items: […] }`, `Cache-Control`, `ETag` |
| UC2 | 10.4.2 style grid → list styles allowed for the series topic | read active templates → `StyleTopicPolicy.IsAllowed(topic, t.TopicHint)` per row (03 CR-2) | `200 { items: […] }`; `?topic=true_crime` → only `true_crime` templates; `?topic=ghost\|spiritual` → none of them; omitted → all active |
| UC3 | 10.5.2 episode page → one call for the whole page | `GetEpisodeByIdQuery` → one projection (episode + `xmin`) + one render-job query + `EpisodeNextActions.For(status)` | `200 EpisodeDetailDto` with `etag` |
| UC4 | client → skip a redundant body | `If-None-Match: "ep-<xmin>"` equal to the current value → 304, no body, same `ETag` | `304` |
| UC5 | another user → probe for someone else's episode | tenant filter (D10) hides the row | `404 not_found` (never 403) |
| UC6 | 10.4.2 / 10.5.2 → cheaper repeat load | catalog cache headers + ETag; a template/voice de-activated later disappears on the next cache expiry, and 09.2.5-T1/03 seeder runs bump the catalog ETag | `304` for the catalog when unchanged |

`nextActions` drives the UI only; it grants nothing. Every command re-checks its own state transition in the domain (D16/00 §7) — the DTO must never be treated as authorization.

## 5. API and events

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| GET | `/api/v1/voices` | bearer (`RequireAuthorization`) | `200` + `ETag` + `Cache-Control: public, max-age=3600` | `400 validation_failed` (`language` not `vi`/`en`), `401` |
| GET | `/api/v1/style-templates` | bearer | `200` + `ETag` + `Cache-Control: public, max-age=3600` | `400 validation_failed` (`topic` not one of the three), `401` |
| GET | `/api/v1/episodes/{id}` | bearer + tenant filter | `200` + `ETag: "ep-<xmin>"`; `304` on `If-None-Match` | `404 not_found` (unknown, other user's, or `Cancelled` — 09.5.1-T1 L63), `401`, `400 validation_failed` (malformed id) |

Envelope [DECISION, plan-local]: both catalogs answer `{ "items": [ … ] }` with **no** `cursor`/`limit`, because the sets are closed and bounded (catalog size is fixed by the seed files: 04 D9 voices, 03 D2 seven templates) and 10.4.2 needs the whole list in one call. D14's cursor contract therefore does not apply here.

Wire examples (camelCase, snake_case enums, D14):

```json
GET /api/v1/voices?language=vi
{ "items": [ { "id": "Thiện Minh", "displayName": "Thiện Minh", "language": "vi", "gender": "male",
               "tags": ["storyteller","deep","northern"],
               "previewUrl": "https://nt-public-dev.<host>/voice-previews/thien-minh.v1.mp3" } ] }

GET /api/v1/style-templates?topic=true_crime
{ "items": [ { "id": "case-file-illustration", "name": "case-file-illustration",
               "topicHint": "true_crime",
               "previewUrl": "https://nt-public-dev.<host>/style-previews/case-file-illustration.v1.webp" } ] }
```

```csharp
// Application/Features/Catalog/Queries/ListVoices/{ListVoicesQuery,ListVoicesHandler,VoiceListItemDto}.cs
public sealed record ListVoicesQuery(string? Language) : IQuery<ErrorOr<IReadOnlyList<VoiceListItemDto>>>;
public sealed record ListStyleTemplatesQuery(string? Topic) : IQuery<ErrorOr<IReadOnlyList<StyleTemplateListItemDto>>>;

public sealed record VoiceListItemDto(string Id, string DisplayName, string Language, string Gender,
                                      IReadOnlyList<string> Tags, string? PreviewUrl);
public sealed record StyleTemplateListItemDto(string Id, string Name, string? TopicHint, string? PreviewUrl);
```

`GET /episodes/{id}` (existing route from 09.5.1-T1, extended — one route, one handler, never a second DTO):

```csharp
// Application/Features/Episodes/Queries/GetEpisode/   (folder created by 09.5.1-T1)
public sealed record GetEpisodeByIdQuery(Guid Id) : IQuery<ErrorOr<EpisodeDetailDto>>;   // renamed from GetEpisodeQuery

public sealed record EpisodeDetailDto(
    Guid Id, Guid SeriesId, string? Title, string Brief, string LengthTier,
    EpisodeStatus Status,                    // JSON: "video_review"
    IReadOnlyList<EpisodeNextAction> NextActions,
    string Etag,                             // D15: also the ETag header
    RenderJobSummaryDto? RenderJob,          // latest render job (BD5 columns)
    FinalVideoAssetDto? FinalVideo,          // assets row of episodes.final_video_asset_id
    PublicationsSummaryDto? Publications);   // null in P — 06 owns publications

public sealed record RenderJobSummaryDto(Guid RenderJobId, string Status, string? Stage, int Percent,
    int Attempt, int ImagesNeeded, DateTimeOffset? SubmittedAt, DateTimeOffset? FinishedAt,
    string? ErrorCode, IReadOnlyList<string> Hints);   // 10 CR-12   // [INFERENCE: field list]; WT53: job times come from `submitted_at`/`finished_at`
public sealed record FinalVideoAssetDto(Guid AssetId, long SizeBytes, string Sha256, string ContentType,
    double? DurationSec, DateTimeOffset? RetentionUntil);                                      // [INFERENCE: field list]; WT53: asset block from `size_bytes`/`duration_sec` (no created_at column exists)
public sealed record PublicationsSummaryDto(int Published, int Scheduled, DateTimeOffset? LastPublishedAt); // [INFERENCE: placeholder for 06]
```

`FinalVideoDto` deliberately carries **no URL**: the only signed media URL comes from `GET /episodes/{id}/video-url` (09.7.2-T1, 15 min, audited — BD10/WF16 ✅).

Projection (Q13: exactly two queries, no N+1, no `Include`):

```csharp
var row = await db.Query<Episode>().Where(e => e.Id == id)
    .Select(e => new { e.Id, e.SeriesId, e.Title, e.Brief, e.LengthTier, e.Status,
                       Etag = EF.Property<uint>(e, "xmin") })          // D15 / N8
    .SingleOrDefaultAsync(ct);
if (row is null) return Error.NotFound(ErrorCodes.NotFound, "…");

var render = await db.Query<RenderJob>().Where(j => j.EpisodeId == id)
    .OrderByDescending(j => j.Id)                                       // WT49: render_jobs has no created_at; UUIDv7 ⇔ creation order
    .Select(j => new RenderJobSummaryDto(...)).FirstOrDefaultAsync(ct);

var finalVideo = row.FinalVideoAssetId is { } assetId
    ? await db.Query<Asset>()… // M3: a second sequential query on the same DbContext (WT46: never concurrent)
    : null;

NextActions = EpisodeNextActions.For(row.Status);
```

[DECISION → RULED WT46] The render-job and final-asset projections run **sequentially** on the one `DbContext` — EF forbids concurrent operations on a single context, so **no `Task.WhenAll`/`Task` pair** ([RULED — README §B10 WT46]); the endpoint still issues two round trips total (BD16): (1) episode + `xmin`, (2) the render job (or the render job **and** its asset as a second sequential query). The `publications` block stays `null` in P and must **not** add a third query when 06 lands — 06 either adds its block to query (2) or the detail route gains a documented third query approved by the TL then.

Events: **none**. This folder raises no domain/outbox event and sends no `NOTIFY` (queries are read-only, D5 applies to commands only). `EpisodeUpdated` comes from the commands that change state (02, 09.6.3) through 09.8.1-T1's notifier.

## 6. UI

Consumers only; no UI work in this folder.

- 10.4.2 step 4 "visual style": grid of previews (radio group, `alt` = the label), labels/hints from i18n `series.style.<name>.label` / `.hint` (03 CR-7/03.1 plan §6), plus the true-crime notice `series.style.trueCrimeNotice`.
- 10.4.x voice picker: groups by `language`, shows `gender` and translated `voices.tag.<tag>` chips (04 CR-11), plays `previewUrl`; when it is `null` the play control is hidden.
- 10.5.2 episode page: renders `nextActions` as the only enabled buttons, `renderJob.stage`/`percent` as the progress bar, `finalVideo` as metadata (the play control calls `GET /episodes/{id}/video-url`), and refreshes on `EpisodeUpdated`/`RenderProgress` (09.8.1-T1).
- Both catalogs are cacheable and stable-keyed, so the SPA may keep them for the session; a newly de-activated template/voice disappears after at most `Catalog:PublicCacheSeconds`.

## 7. Jobs and integrations

- **No background job, no schedule, no outbox handler** is added here (D12: the Api never schedules jobs).
- **Extension points** (BD1 ✅) — file-level, so both downstream tasks edit exactly one DTO + one projection and must not add a new route or handler:
  - **02.3.4-T2** adds `currentStory` and `generation { kind, status, stage, errorCode, startedAt, processingPaused }` to `EpisodeDetailDto` (02 CR-7 ✅). Its data comes from `story_versions` / `generation_runs` (both created by 02), so the members are nullable and stay `null` until that task lands. ⚠️ 00 §8 L353 lists `processingPaused` at the **top level** while 02 CR-7 nests it in `generation`: [OPEN] Q-O2 keeps it inside `generation` only (one owner).
  - **03.4.2-T1** adds `renderJob.images` (the image set merged into `quality_report`) to `RenderJobSummaryDto` (BD1 ✅).
- **Integrations read-only:** `IReadDb` (D8), `StyleTopicPolicy` (03.1.1-T2 — Domain, already implemented by plan 03), `IOptions<StorageOptions>` [INFERENCE], `ICurrentUser` implicitly through the tenant filter (D10).
- `04.2.3-T1` (soft Blocks) consumes `GET /voices` for the proxy allow-list display; `10.4.x` consumes both catalogs.

## 8. Security and privacy

- **Least privilege.** Both catalog routes and the episode route require an authenticated bearer token; no `[AllowAnonymousRequest]` (D4 marker) anywhere in this folder. The episode route relies on the named `"tenant"` filter (D10) — no `IgnoreQueryFilters` and no admin path.
- **No cross-tenant leakage.** Another user's episode, an unknown id and a `Cancelled` episode are indistinguishable: `404 not_found` with the same body (D10 ✅, 09.5.1-T1 L63). `SeriesId` is returned only for the caller's own episode.
- **No data in logs.** The handlers log the route, the outcome and the row count only — never `brief`/`title` (user content), never the full `previewUrl` with its query string (public bucket URLs carry none), never tokens. The episode `xmin` leaks nothing beyond "this episode changed", and only to its owner.
- **URL composition is an injection surface.** `preview_storage_key` is seeded data, but the builder still validates it (no scheme, no `..`, no backslash, root-relative only) and validates `Storage:PublicBaseUrl` (`https` outside Development) before composing — a bad key/url yields `null`, never a redirect or an absolute URL from attacker-controlled text.
- **No secrets.** The response carries no storage credential and no signed URL (public CDN only; BD10/WF16 keep signed URLs behind 09.7.2-T1). The public bucket has no write access from the app key (11 README H9 ✅).
- **Cache safety.** Catalog responses are identical for every user (`public` is safe: no per-user data). The episode response is **not** cacheable by shared proxies → no `Cache-Control: public` on it; it carries `ETag` only, and `Vary: Authorization` [INFERENCE].
- **Bounded work.** Both catalogs are read in full (closed, bounded sets: ≤ a few dozen rows, D2/04 D9). No `limit` is accepted, so a client cannot force an unbounded scan; the episode projection is a single-row lookup by primary key plus one indexed render-job lookup on `episode_id` ordered by `id DESC` (UUIDv7) — **no `created_at` index is claimed** ([RULED — README §B10 WT49]; the index is owned by 09.6.1-T1's migration — see Open items).

## 9. Test plan

Harness: D13 (`ApiFactory`, `TestAuthHandler` (`X-Test-User`/`X-Test-Role`), `PostgresFixture`, `FakeClock`, builders; hand-written fakes only, **no mocking library**). No test sleeps.

`tests/NightTales.Domain.UnitTests/Episodes/EpisodeNextActionsTests.cs`
- `For_Draft_Allows_GenerateStory_And_Cancel`
- `For_StoryGenerating_Allows_CancelOnly`
- `For_StoryReview_Allows_ApproveStory_ReopenStory_Cancel`
- `For_VideoReview_Allows_ApproveVideo_RejectVideo_Cancel`
- `For_Rendering_Allows_Cancel`
- `For_Published_Allows_None`
- `For_UnknownStatus_Empty` (guards against a new `EpisodeStatus` value being missed)
- `Table_Matches_00_S7` (theory over all 11 `EpisodeStatus` values)

`tests/NightTales.Api.IntegrationTests/Catalog/ListVoicesTests.cs`
- `Returns_Only_Active_Voices`
- `Filter_Language_Vi_Returns_Only_Vi` · `Filter_Language_En_Returns_Only_En`
- `Unknown_Language_Returns_400_ValidationFailed_Field_Language`
- `PreviewUrl_Uses_Public_Base_Url_And_Key`
- `PreviewUrl_Is_Null_When_Key_Missing` · `PreviewUrl_Is_Null_When_Base_Url_Empty`
- `PreviewUrl_Is_Null_When_Key_Is_Absolute_Or_Contains_DotDot`
- `CacheControl_Is_Public_MaxAge`
- `IfNoneMatch_Same_Etag_Returns_304_Without_Body`
- `Requires_Authentication` (401 without a token)

`tests/NightTales.Api.IntegrationTests/Catalog/ListStyleTemplatesTests.cs`
- `Topic_TrueCrime_Returns_Only_TrueCrime_Templates` (03 CR-2 table)
- `Topic_Ghost_Excludes_TrueCrime_Templates`
- `Topic_Spiritual_Excludes_TrueCrime_Templates`
- `No_Topic_Returns_All_Active_Templates`
- `Inactive_Template_Is_Hidden`
- `Unknown_Topic_Returns_400_ValidationFailed_Field_Topic`
- `Item_Shape_Is_Id_Name_TopicHint_PreviewUrl`

`tests/NightTales.Api.IntegrationTests/Episodes/GetEpisodeDetailTests.cs`
- `Returns_All_Blocks_For_Own_Episode`
- `Other_Users_Episode_Returns_404_NotFound`
- `Unknown_Id_Returns_404_NotFound` · `Malformed_Id_Returns_400_ValidationFailed`
- `Cancelled_Episode_Returns_404_NotFound`
- `Etag_Has_Ep_Prefix_And_Matches_Xmin` (asserts header **and** DTO `etag` are equal, D15)
- `IfNoneMatch_Current_Etag_Returns_304_Without_Body`
- `IfNoneMatch_Stale_Etag_Returns_200_With_New_Etag`
- `NextActions_Match_Domain_For_Status` (theory over statuses, seeded through the builder)
- `Render_Job_Summary_Shows_Latest_Job_Stage_And_Percent` (latest by `id DESC`; times from `submittedAt`/`finishedAt`; asset block from `sizeBytes`/`durationSec` — [RULED — README §B10 WT49/WT53])
- `Queries_RunSequentially_OnOneDbContext_NoWhenAll` — asserts the render and asset reads never overlap (a `CountingDbCommandInterceptor` records start/end order; EF throws on concurrent operations on one context — [RULED — README §B10 WT46])
- `Publications_Block_Is_Null_In_P`
- `Uses_Two_Database_Commands_Only` — assertion via a hand-written `CountingDbCommandInterceptor` in `NightTales.TestKit` (D13: no mocking library) counting one episode query + one render/asset query, over an episode with 3 render jobs and 2 assets (proves no N+1, BD16)

`tests/NightTales.Architecture.Tests/ApiContractTests.cs`
- `Detail_Dto_Does_Not_Expose_Domain_Entities_Or_Ef_Types`
- `Catalog_Queries_Have_No_Write_Dependency` (no `IUnitOfWork`/`SaveChanges` reference in the Catalog query handlers)

## 10. Tasks and re-estimate

| Task | Title | Area | Size | Depends on | Blocks |
|---|---|---|---|---|---|
| 09.4.2-T1 | Voices and style-template catalog queries | BE | S (0.5) | 09.2.5-T1; 04.2.2-T2 soft (voice seeder), 04.2.3-T1 soft (preview keys), 03.1.1-T2 soft (`StyleTopicPolicy`) | 10.4.2-T2, 10.4.2-T1 (10 CR-8), 10.4.1-T0 |
| 09.5.3-T1 | Episode detail query (`EpisodeDetailDto`, ETag, render block) | BE | S (0.5) | 09.5.1-T1; 09.6.1-T1 soft (render block) | 02.3.4-T2 soft, 10.5.2-T1 (10 CR-8), 06.2.1-T1 (06 CR-8), 10.6.1-T1 (10 CR-15), 10.5.1-T0, 10.6.1-T0, 10.5.4-T0 |

Both rows are **copied verbatim from README B8.1** ✅. Folder total **1.0 d raw**, matching B9 ("09.4.2, 09.5.3 | 1.0 | 1.0 | —") — no re-estimate.

**Open items.**

1. [OPEN] **09.4.2-T1 needs `StyleTopicPolicy` but has no edge to its owner.** `src/NightTales.Domain/Catalog/StyleTopicPolicy.cs` is delivered by **03.1.1-T2** (03.1.1-T2 L80/L88, 03.1 plan L53), yet README B8.1 lists `09.4.2-T1` with `Depends on 09.2.5-T1` only. `GET /style-templates?topic=` (03 CR-2, 09 L160) cannot be written without it. Recommendation: add a **soft** edge `03.1.1-T2` to `09.4.2-T1` (do not duplicate the policy in 09).
2. [OPEN] **09.5.3-T1 reads `render_jobs`, which 09.6.1-T1 creates.** B8.1 gives `09.5.3-T1 | … | Depends on 09.5.1-T1 | 02.3.4-T2 soft`; the `render_jobs` table + migration arrive with **09.6.1-T1** (BD5, migration `<ts>_09_RenderJobs`), and the index the detail query relies on is specified there. Recommendation: either add a soft edge `09.6.1-T1`, or order 09.6.1-T1 before 09.5.3-T1 inside P·M1 (then the render block is `null` only on an episode that never rendered).
3. [OPEN] **DTO name conflict.** 09.5.1-T1 defines `EpisodeDto` (L37, L63, L126) while BD1 gives `EpisodeDetailDto` to 09.5.3-T1. Resolution applied in this plan: 09.5.3-T1 **renames** the type (`EpisodeDto` → `EpisodeDetailDto`) in the same folder, one route, no second DTO; confirm the rename (alternative: keep `EpisodeDto` and record it as the artefact name).
4. [OPEN] `304`/`If-None-Match` and the catalog cache headers are not in D7/D14: the 304 path uses `Results.StatusCode(304)` in the endpoint so 09.1.4-T1's `ResultExtensions` file is not edited. Confirm both additions, and the `?limit`-less `{ items }` envelope (§5) and the `Catalog:PublicCacheSeconds` default.
5. [OPEN] Config key `Storage:PublicBaseUrl` and `IPublicAssetUrl` are **[INFERENCE]** — no signed file names them yet; 11 config (11.2.2-T2) must set it per environment to the `nt-public-<env>` base URL, or `previewUrl` is always `null`.
6. [OPEN] `EpisodeNextActions` member list/shape (§3) is derived from 00 §7, not pinned by BD1; confirm against 00 §7 before implementation. Related: 00 §8 L353 puts `processingPaused` at the top level of the DTO, 02 CR-7 nests it in `generation` — this plan keeps it in `generation` only (owned by 02.3.4-T2).

## 11. Rollout

- **Phase/flag:** both tasks are **P · M1**, no feature flag (B8.1 ✅). They land after 09.2.5-T1 (seeded catalogs) and 09.5.1-T1 (episode route).
- **Migration:** none (read-only). Rollback = revert the commit; no data change, so no rollback script and no expand-only concern.
- **Ordering inside M1:** 09.4.2-T1 can land as soon as 09.2.5-T1 + 03.1.1-T2 exist; 09.5.3-T1 lands after 09.5.1-T1 and (open item 2) after the `render_jobs` migration.
- **Verification before "done":** the integration suites in §9 against local Postgres (`ApiFactory` + `PostgresFixture`) — including the `If-None-Match` 304 path, the 404 for another user's episode and the two-command assertion.
- **Observability:** no new metric; the standard request logs + Npgsql spans (BD12, 09.9.3-T1) cover both routes. A `null` `previewUrl` rate worth watching after go-live: it means `Storage:PublicBaseUrl` is unset or a seed key is wrong (log once at startup, not per request).
- **Cache invalidation:** catalog answers may be cached by the client/LB for `Catalog:PublicCacheSeconds`; a re-seed (04.2.2-T2, 03.1.1-T1) changes the ETag, so browsers revalidate and pick the new list within one TTL. No purge hook is required for P.
