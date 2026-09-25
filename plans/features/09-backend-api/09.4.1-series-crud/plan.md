# 09.4.1 — Series CRUD

> Refines: `../../../09-backend-api.md` §4 (epic 09.4, story **09.4.1**, L155–157); `../../../00-overview.md` §6 `series` row (L265), §8 Series routes (L349).
> Contract: `../README.md` — **F15**, **D3**, **D4**–**D10**, **D14**–**D17**; 02 README D9 (`sa-` ETag), 02.6.3-T1 (bible hook).
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.4.1 — "As a creator I want a series per channel idea so that episodes share settings." Must · detailed **M 1.0**.

**In scope.**
- `Series` behaviour: `Create`, `Update`, `Archive`.
- `CreateSeries`, `UpdateSeries`, `ArchiveSeries`, `GetSeries`, `ListSeries`.
- The routes `GET/POST /series`, `GET/PATCH/DELETE /series/{id}`.
- Validation (including the voice language), the ETag `"se-<xmin>"` with required `If-Match` on PATCH, cursor listing, and the 404 policy.
- The reserved `[Audited]` names.
- The hook point for 02.6.3.

**Out of scope.**
- Autopilot settings and authorization routes (02.8.3).
- Bible routes (02.6.3; its default-bible insert is added to `CreateSeriesHandler` by 02.6.3-T1).
- `GET /voices`, `GET /style-templates` (09.4.2).
- `default_channel_id` validation (06, once `channel_connections` exists; until then the field is rejected if non-null [DECISION]).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| S1 | The voice language must match the series language, else 400 `voice_language_mismatch` | 09 L156 ✅ |
| S2 | `DELETE` sets `archived_at`; the series disappears from `GET /series`; its episodes stay accessible | 09 L157, 00 §6 ✅ |
| S3 | `topic` is immutable while autopilot is enabled | 00 §6 L265 ✅ |
| S4 | 02.8.3 uses `"sa-<series.xmin>"` for the autopilot settings; this story uses `"se-<xmin>"` for the series resource (same xmin, different routes) | 02 README D9, README D15 ✅ |

## 3. Domain and data

`Domain/Series/Series.cs` (the shell exists from 09.2.2-T1; this story adds the behaviour):

```csharp
public sealed record SeriesDraft(string Title, Topic Topic, Language Language, Aspect Aspect,
    string VoiceId, Guid StyleTemplateId, string? StoryPreset);
public sealed record SeriesPatch(string? Title, Topic? Topic, Aspect? Aspect, string? VoiceId,
    Guid? StyleTemplateId, string? StoryPreset);        // Language is immutable after create [DECISION]

public static Series Create(Guid userId, SeriesDraft d, DateTimeOffset now);           // raises SeriesCreated
public ErrorOr<Success> Update(SeriesPatch p, DateTimeOffset now);                     // topic change + AutopilotEnabled → series_topic_locked
public ErrorOr<Success> Archive(DateTimeOffset now);                                   // idempotent: already archived → Success; raises SeriesArchived once
```

- [DECISION] `language` cannot change after creation. Episodes, voices and prompts depend on it; a user creates a new series instead. An attempt → 400 `validation_failed` (field `language`).
- Updating an archived series → 409 `series_archived`.
- No new table or column (09.2.2).

## 4. Use cases

| # | Command / query | Validation | Errors |
|---|---|---|---|
| U1 | `CreateSeriesCommand(string Title, Topic Topic, Language Language, Aspect Aspect, string VoiceId, Guid StyleTemplateId, string? StoryPreset)` → `SeriesCreated(Guid Id)` (ETag read after commit by the endpoint) | title 1–120 trimmed; enums; voice exists, active, `voice.language == language`; style template exists, active, and `StyleTopicPolicy.IsAllowed(topic, topicHint)` (03 CR-2); preset null or a known 02 D5 preset key (checked through `IStoryPresetCatalog`, implemented by 02.1.4, a null catalog in wave 1 that accepts only null [DECISION]) | 400 `validation_failed`, 400 `voice_language_mismatch` |
| U2 | `UpdateSeriesCommand(Guid Id, string IfMatch, SeriesPatch Patch)` → `Success` (ETag read after commit) | as U1 for the present fields; `If-Match` equals `"se-<xmin>"` | 404, 409 `series_archived`, 409 `series_topic_locked`, 412 `series_conflict`, 428 |
| U3 | `ArchiveSeriesCommand(Guid Id)` → `Success` | — | 404 |
| U4 | `GetSeriesQuery(Guid Id)` → `SeriesDto` (includes archived, with `archivedAt`) | — | 404 |
| U5 | `ListSeriesQuery(string? Cursor, int Limit, bool IncludeArchived = false)` → `Page<SeriesListItemDto>` | D14 | 400 |

- Authorization: authenticated user (any role).
- Idempotency: none, since there are no paid effects.
- `[Audited]` names reserved for 09.3.6-T1: `series.created`, `series.updated`, `series.archived`.

## 5. API and events

| Method | Route | Success | Notes |
|---|---|---|---|
| POST | `/api/v1/series` | 201 + `Location: /api/v1/series/{id}` + `ETag` | body = U1 |
| GET | `/api/v1/series?cursor=&limit=&includeArchived=` | 200 `{ items, nextCursor }` | archived excluded by default |
| GET | `/api/v1/series/{id}` | 200 + `ETag` | |
| PATCH | `/api/v1/series/{id}` | 200 `{ etag }` + `ETag` | `RequireIfMatch()`; JSON merge semantics: absent = unchanged, `null` for `storyPreset` clears it |
| DELETE | `/api/v1/series/{id}` | 204 | archive |

`SeriesDto(Guid Id, string Title, Topic Topic, Language Language, Aspect Aspect, string VoiceId, Guid StyleTemplateId, string? StoryPreset, bool AutopilotEnabled, DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Etag)`. The list item omits `StoryPreset`.

Events: `SeriesCreated` (`series_created.v1`), `SeriesArchived` (`series_archived.v1`). Payloads are `SeriesId`, `UserId`, `OccurredAt`. No handler in this wave.

## 6. UI

None (backend only). 10.4 consumes these routes.

## 7. Jobs and integrations

None. The 02.6.3 default-bible insert runs inside `CreateSeriesHandler` in the same transaction (02.6.3-T1 adds it).

## 8. Security and privacy

| Threat | Control |
|---|---|
| Reading or archiving another user's series | tenant filter → 404 (D10) |
| Lost update | `If-Match` required on PATCH; xmin mismatch → 412; a race after the check → 409 `concurrency_conflict` (D5) |
| Topic switch to escape the true-crime rules while autopilot runs | `series_topic_locked` (S3) |

## 9. Test plan

Domain unit tests for `Create`/`Update`/`Archive`; integration tests through `ApiFactory` for every route, the validation boundaries, the ETag rules, archive visibility and tenant isolation.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.4.1-T1 | Series behaviour, commands/queries, endpoints | M (1.0) | 09.1.3-T2, 09.1.4-T1, 09.2.3-T1, 09.2.5-T1, 03.1.1-T2 soft (style topic policy) (09 W2 CR-9) | 09.5.1-T1, 02.6.3-T1, 02.8.3-T2, 10.4.2-T1 (10 CR-8) |

Total **1.0 d** (unchanged).

## 11. Rollout

- No flag. Needs the 09.2.5-T1 catalogs in Development (voice and style ids).
- Rollback: routes can be unmapped; data is plain rows.
