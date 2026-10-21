# 09.5.1 — Episodes

> Story: `../../../09-backend-api.md` §4 epic 09.5.1. Contract: `../README.md` (D1–D17, F14, F16, §8 index, §9 re-estimate). One task: `09.5.1-T1` (M 1.0). Phase P · no feature flag.
> Status: task **09.5.1-T1 implemented 2026-10-01** (branch `feature/09.5.1-T1-episodes`). Integration tests live in `tests/NightTales.Api.IntegrationTests/EpisodeEndpoints/` (a `…IntegrationTests.Episodes` namespace would shadow `NightTales.Domain.Episodes` for the assembly, as `SeriesEndpoints/` avoids for `Domain.Series`); the POST body is the separate wire record `CreateEpisodeRequest` (no `seriesId`/`userId` member) while the command returns the id (`ErrorOr<Guid>`), so the 201 body is the id string and `Location` is the canonical pointer; `?status=` is bound as a snake_case string and parsed in the endpoint (the built-in enum binder refuses `story_generating`), an unknown name is 400 field `status`, and an empty `?cursor=` is "no cursor" per `Cursor.TryDecode`'s contract (the series route answers 400 there) [DECISION]; `DELETE /episodes/{id}` skips the precondition only for an absent `If-Match` — every present value, empty included, is compared, so it answers 412 `episode_conflict` and the row stays (CR-1); `ListEpisodesValidator` owns the `limit`/`cursor` shapes and the handler repeats them because it must decode the cursor anyway; the delete-cell `episode_conflict` (412) and `series_archived` (409) were already catalogued by the 09.4.1-T1 wave, so no `ErrorCodes`/`ErrorCatalogue` change was needed; `EpisodeCancelledAssetRetentionHandler` is `public` (the solution has no `InternalsVisibleTo`, so the Worker composition root must name it); plan 02/06 extend `EpisodeDto` (09.5.3) and the partial `Episode.*` files.

## 1. Scope

Deliver the episode surface M1 of plan 02 codes against, in wave 1.

In scope:
- `Episode` aggregate root (`Episode.cs` owned here; plan 02 adds partial files `Episode.Story.cs`, `Episode.FactSheet.cs`, …).
- `CreateEpisodeCommand`, `ListEpisodesQuery`, `GetEpisodeQuery`, `DeleteEpisodeCommand` with handlers and endpoint group.
- Cursor (keyset) pagination over episodes, tenant-scoped.
- `EpisodeCreated` / `EpisodeCancelled` domain events and the outbox handler `EpisodeCancelledAssetRetentionHandler`.
- Validation, error codes, ETag `"ep-<xmin>"`, `[Audited]` name reservations.
- Tests that defend the observable behaviours in §9.

Out of scope (other tasks/plans):
- Story commands (09.5.2), episode detail with next actions / `currentStory` / generation block (09.5.3).
- Series CRUD and archive semantics (09.4.1), autopilot wiring (02.8.3).
- Physical asset deletion (09.7.3), hard delete (13.3.1).
- Auth endpoints (09.3.x) and the audit behaviour implementation (09.3.6-T1).

## 2. Confirmed facts

- **F14** fixes DELETE: `DELETE /episodes/{id}` → `cancelled`, hidden from reads (404); `rendering` and `story_generating` → 409 `episode_invalid_state`; hard delete is 13.3.1. **D16** fixes `Episode.Cancel(now)` and the event set.
- **F16** gives the new code `series_archived` (409) for creating an episode in an archived series; **D17** lists it.
- **D3** `IClock`, `IDomainEvent`, `IUserOwned`, `Entity`, `AggregateRoot` with `Version` → `xmin`. `Entity.Id` is `Guid.CreateVersion7()` in factories, `ValueGeneratedNever`.
- **D4** pipeline `Logging → Authorization → Validation → UnitOfWork → Idempotency → Audit`; queries skip UoW/Idempotency/Audit.
- **D5** the UoW behaviour opens the transaction before the handler; handlers never call `SaveChangesAsync`.
- **D7** `ErrorCodes` catalogue + `Api/Http/ErrorCatalogue.cs`; factories `ErrorOrFactory<T>`, `ToCreatedResult(location, etag)`, `ToNoContentResult()`.
- **D8** snake_case naming, `DateTimeOffset` UTC → `timestamptz`, enums snake_case text + `CHECK`, `IAppDbContext` exposes `DbSet<T>`.
- **D9** `episodes` is created by the `InitialCore` migration (09.2.2-T1); this story adds **no** migration.
- **D10** EF named filter `"tenant"` on `IUserOwned`; other users' rows answer 404, never 403.
- **D14** JSON camelCase, enums snake_case, cursor `?cursor=&limit=` (1–100, default 20), response `{ items, nextCursor }`, cursor opaque base64url JSON `{ "c": createdAt, "i": id }`.
- **D15** episode ETag `"ep-<xmin>"`, in the `ETag` header and the DTO field `etag`.
- **00 §6** `episodes` columns, **00 §7** episode state machine, **00 §8** episodes routes.
- **N13** `Guid.CreateVersion7()`; **N8** `xmin` via `IsRowVersion()`.

## 3. Domain and data

`src/NightTales.Domain/Episodes/Episode.cs` (owned by this story):

```csharp
public sealed class Episode : AggregateRoot, IUserOwned, IAuditable
{
    public Guid UserId { get; private init; }
    public Guid SeriesId { get; private init; }
    public string? Title { get; private set; }
    public string Brief { get; private set; }
    public LengthTier LengthTier { get; private init; }
    public EpisodeStatus Status { get; private set; }
    public Guid? CurrentStoryVersionId { get; private set; }   // plan 02 owns updates in Episode.Story.cs
    public Guid? ApprovedStoryVersionId { get; private set; }
    public Guid? FinalVideoAssetId { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Episode Create(
        Guid userId, Guid seriesId, string? title, string brief, LengthTier tier, DateTimeOffset now);
    public ErrorOr<Success> Cancel(DateTimeOffset now);
}
```

- `Create` sets `Status = EpisodeStatus.Draft`, id via `Guid.CreateVersion7()`, `CreatedAt = UpdatedAt = now`, raises `EpisodeCreated`. `Episode` implements `IAuditable` (09.2.2-T1, 09 W3 CR-38); the explicit stamps stay because `AuditableSaveChangesInterceptor` only fills a `CreatedAt` that is still `default`, and `Cancel` keeps stamping `UpdatedAt = now` (the interceptor would do the same for a tracked `Modified` entity).
- `Cancel` per D16: from `StoryGenerating`, `Rendering`, `Cancelled`, `Published` → `Error.Conflict(ErrorCodes.EpisodeInvalidState)`; otherwise (including `Draft`, `StoryReview`, `StoryApproved`, `VideoReview`, `Ready`, `Scheduled`, `Failed`) → `Status = Cancelled`, `UpdatedAt = now`, raises `EpisodeCancelled { PreviousStatus }`.
- Status is `EpisodeStatus` (D16 names), stored snake_case text with a `CHECK` generated by `HasEnumCheck<EpisodeStatus>`.
- Table `episodes` (00 §6 / D9): `user_id`, `series_id` FK `series`, `title`, `brief`, `length_tier` (+`CHECK`), `status` (+`CHECK`), `current_story_version_id`, `approved_story_version_id`, `final_video_asset_id`, `created_at`, `updated_at`, `xmin`. Indexes come from 09.2.2-T1 (§9): `(user_id, created_at DESC, id DESC)`, `(user_id, status, created_at DESC, id DESC)` and `(series_id, created_at DESC, id DESC)`; the list query uses them.
- No EF configuration file in this story: 09.2.2-T1 owns the `episodes` entity configuration and migration; this story only relies on `DbSet<Episode> Episodes` from `IAppDbContext`.

## 4. Use cases

- **UC1 (create)** — creator or autopilot creates an episode under a series. The series MUST be the caller's (else 404) and not archived (else 409 `series_archived`). `user_id` and `series_id` are copied from the loaded series, never from the request body.
- **UC2 (list)** — cursor page of the caller's episodes, optional `seriesId` and `status` filters, cancelled excluded.
- **UC3 (get)** — one episode by id, basic DTO; cancelled → 404.
- **UC4 (delete)** — cancel an episode that is not `story_generating`/`rendering`; assets scheduled for deletion through the retention event.
- **UC5 (autopilot create)** — same as UC1 executed under `ISystemContext.For(userId)` (02.8.3), so the tenant filter resolves to the run's user.

## 5. API and events

Minimal API group `Api/Endpoints/EpisodeEndpoints.cs : IEndpointGroup`, base `/api/v1`:

| Method | Route | Command/Query | Success | Errors |
|---|---|---|---|---|
| POST | `/series/{id}/episodes` | `CreateEpisodeCommand(SeriesId, Title, Brief, LengthTier)` | 201 + `Location: /api/v1/episodes/{id}` + `ETag: "ep-<xmin>"` | 404 series not found/other tenant · 409 `series_archived` · 400 `validation_failed` |
| GET | `/episodes?seriesId=&status=&cursor=&limit=` | `ListEpisodesQuery(SeriesId, Status, Cursor, Limit)` | 200 `{ items, nextCursor }` | 400 `validation_failed` (invalid cursor/limit, field `cursor`/`limit`) |
| GET | `/episodes/{id}` | `GetEpisodeQuery(Id)` | 200 + `ETag: "ep-<xmin>"` | 404 `not_found` (missing, other tenant, or cancelled) |
| DELETE | `/episodes/{id}` | `DeleteEpisodeCommand(Id, IfMatch)` | 204 | 404 `not_found` · 409 `episode_invalid_state` · 412 `episode_conflict` (stale `If-Match`, see §Notes) |

DTOs (`Application/Features/Episodes/Dtos/`):

```csharp
public sealed record EpisodeListItemDto(Guid Id, Guid SeriesId, string? Title, EpisodeStatus Status,
    LengthTier LengthTier, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Etag);
public sealed record EpisodeDto(Guid Id, Guid SeriesId, string? Title, string Brief, EpisodeStatus Status,
    LengthTier LengthTier, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Etag);
public sealed record EpisodePageDto(IReadOnlyList<EpisodeListItemDto> Items, string? NextCursor);
```

`GetEpisodeQuery` returns the basic `EpisodeDto`; 09.5.3 extends it with `nextActions`, `currentStory`, `generation`, render/publications summary. ETag string is `"ep-" + episode.Version`.

Events (`Domain/Episodes/Events/`), outbox names per D16:

| Event | `[OutboxEvent]` | Payload |
|---|---|---|
| `EpisodeCreated` | `episode_created.v1` | `EpisodeId`, `SeriesId`, `UserId`, `OccurredAt` |
| `EpisodeCancelled` | `episode_cancelled.v1` | `EpisodeId`, `SeriesId`, `UserId`, `PreviousStatus`, `OccurredAt` |

`/hubs/episodes` `EpisodeUpdated` push is 09.5.3's; this story only emits the events above.

## 6. UI

None (backend only) — this story exposes the API the Angular studio consumes; no front-end is defined in plan 09.

## 7. Jobs and integrations

- No new background job; no recurring registration.
- Outbox handler `Application/Features/Episodes/Outbox/EpisodeCancelledAssetRetentionHandler.cs : IOutboxHandler<EpisodeCancelled>`: sets `assets.retention_until = now` for every asset of the episode (looked up by `episode_id`), which marks them for the 09.7.3 sweep. It changes data, so it dispatches a command through `ISender` (D11) rather than writing with a raw `SaveChanges`. Whichever shape it takes, the asset rows' `UpdatedAt` must end up at `now`: a tracked update gets it from `AuditableSaveChangesInterceptor`, while a set-based `ExecuteUpdateAsync` bypasses the interceptor and must add `SetProperty(a => a.UpdatedAt, now)` (09 W3 CR-38).
- `[Audited]` names reserved for 09.3.6-T1: `episode.created` on `CreateEpisodeCommand`, `episode.deleted` on `DeleteEpisodeCommand`.
- Idempotency: `POST /series/{id}/episodes` does **not** require `Idempotency-Key` — the route spends no credits and calls no platform (00 §8). [DECISION]

## 8. Security and privacy

- Every query is scoped by the EF named filter `"tenant"` (`user_id == ICurrentUser.Id`, D10). A missing or foreign episode/series is 404 `not_found`, never 403.
- `user_id` and `series_id` on UC1 come from the loaded `Series`, never from the body: a client cannot create an episode for another tenant or another series id.
- Authorization requires an authenticated user (`platform role` any). Autopilot creation runs under `ISystemContext.For(userId)` (UC5); the context is set before `AppDbContext` is resolved in that scope (D10).
- No content leaves the API in errors or logs: descriptions are English constants; `brief` never appears in a ProblemDetails `detail`.
- Deleting (cancelling) is a user action; the row is retained for retention/audit, its assets are only marked (`retention_until`), never hard-deleted here.

## 9. Test plan

Integration (postgres `PostgresFixture` + `ApiFactory`, `TestAuthHandler`) and unit where noted. See the task file for method names.

- UC1: create happy path 201 + `Location` + `ETag`; `user_id`/`series_id` copied from the series (a body attempt to set them is impossible — DTO has no such field, asserted by the command shape); archived series → 409 `series_archived`; other tenant's series → 404; brief 1,000 accepted / 1,001 rejected; blank-after-trim brief rejected; title 120 accepted / 121 rejected; unknown `lengthTier` rejected.
- UC2: keyset order `(created_at DESC, id DESC)`; no duplicate and no skipped row when a new episode is inserted between page 1 and page 2; `status` filter; `seriesId` filter; `cancelled` excluded; limit bounds 1/100 ok, 0/101 → 400; invalid cursor → 400.
- UC3: basic DTO; cancelled → 404; other tenant → 404.
- UC4: delete while `rendering` → 409; while `story_generating` → 409; from `draft` → 204 then GET → 404; second delete → 404; other tenant → 404; stale `If-Match` → 412 `episode_conflict`; `If-Match` absent → succeeds (unless stale-guard decision changes).
- Outbox: `EpisodeCancelled` → `EpisodeCancelledAssetRetentionHandler` sets `assets.retention_until` for the episode's assets (run through `WorkerHost`).
- Domain unit (no DB): `Episode.Create` raises `EpisodeCreated` and starts `Draft`; `Cancel` invalid from each of the four states; `Cancel` from each allowed state raises `EpisodeCancelled` with the right `PreviousStatus`.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.5.1-T1 | Episode aggregate, commands/queries, cursor pagination, retention event | M (1.0) | 09.4.1-T1, 09.2.4-T1, 09.1.3-T2, 09.1.4-T1 | 09.5.3-T1, 02 M1 development, 09.6.1-T1 (09 W2 CR-9), 10.5.1-T1 (10 CR-8) |

Story total: **M 1.0** (README §9: `09.5.1-T1 M 1.0`, unchanged from the high-level estimate). No new S work.

## 11. Rollout

- Phase **P**, no feature flag. Feature-complete when the four routes work against the local Postgres and the tests in §9 pass.
- Critical path: `09.4.1-T1 → 09.5.1-T1`; the outbox dependency (`09.2.4-T1`) gates the retention handler.
- Exit gate **02-M1-dev** (README §7): `09.4.1-T1 + 09.5.1-T1` merged lets plan 02 start M1 feature work.
- No migration in this story; 09.2.2-T1 already created `episodes` and its indexes. If a column is missing at implementation, raise it against 09.2.2 rather than adding a migration here.

### Notes

- [DECISION] `If-Match` on `DELETE /episodes/{id}` is **optional** (the assignment's recommendation). When present and stale → 412 with code `episode_conflict`. `episode_conflict` (412) is added to README D17 / CR-7.
- [DECISION] `episode_created` is not a credit-spending action, so no `Idempotency-Key` requirement (00 §8). If the owner wants create idempotency, add `IIdempotentCommand` to `CreateEpisodeCommand`.
- [OPEN] Merge policy after `Cancel`: a cancelled episode keeps its row (F14) and its assets until 09.7.3; a hard delete is 13.3.1. Do not add a `DELETE` physical path here.
- The `episodes` index names/columns are pinned by 09.2.2-T1; verify before relying on them for the keyset plan.
