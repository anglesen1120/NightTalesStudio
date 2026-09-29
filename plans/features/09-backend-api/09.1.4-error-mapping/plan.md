# 09.1.4 — Result → HTTP mapping

> Refines: `../../../09-backend-api.md` §4 (epic 09.1, story **09.1.4**, L71–73), §5.1 (endpoint example), §5.4 (catalogue); `../../../00-overview.md` §8.
> Contract: `../README.md` — **D7**, **D14**, **D15**, **D17**; 02 CR-20 (`Retry-After`), 02 README D8/D9.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.1.4 — "As the Angular client I want consistent errors so that one mapper handles them." Must · detailed **S 0.5**.

**In scope.**
- `ErrorCodes` (closed constants) and `ErrorCatalogue` (code → HTTP status).
- The ProblemDetails writer.
- The result helpers `ToHttpResult`, `ToCreatedResult`, `ToAcceptedResult`, `ToNoContentResult`.
- The endpoint filters `RequireIdempotencyKey()` and `RequireIfMatch()`.
- The `ETags` helper.
- Unhandled-exception mapping to 500.

**Out of scope.** i18n messages (10, client side). The codes of later plans: each feature adds its constants and catalogue rows in its own task, and this plan only fixes the mechanism and the codes known today (09 §5.4 + D17 + 02 D8).

**Rename.** `ToHttpResult202` in 09 §5.1 → `ToAcceptedResult` (D7).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| E1 | ProblemDetails (RFC 9457) with a stable `code` is the 00 §8 error format | 00 §8 ✅ |
| E2 | Status comes from the code, not from `ErrorType` (02 already writes `Error.NotFound("fact_sheet_required", 409)`-style mixes, fixed by 09 CR-9) | README D7, F17 ✅ |
| E3 | 202 bodies (`setting_change_pending`, `owner_action_pending`, `grant_requires_approval`, `objection_recorded`) are success values, not errors | 09 §4 09.1.4 L72 ✅ |
| E4 | ASP.NET Core `IProblemDetailsService` / `AddProblemDetails()` writes `application/problem+json` ✅ verified 2026-09-29 against `dotnet/aspnetcore` `release/10.0` (see the 09.1.4-T1 Notes for the exact hooks) | ASP.NET Core docs + source ✅ |

## 3. Domain and data

No tables. Types (`src/NightTales.Application/Common/Errors/` and `src/NightTales.Api/Http/`):

| File | Content |
|---|---|
| `Application/Common/Errors/ErrorCodes.cs` | `public static class ErrorCodes { public const string ValidationFailed = "validation_failed"; … }`, one constant per catalogue code |
| `Api/Http/ErrorCatalogue.cs` | `static readonly FrozenDictionary<string, int> Status` built from 09 §5.4 + D17 + 02 D8 |
| `Api/Http/ProblemDetailsWriter.cs` | `IResult ToProblem(List<Error> errors, HttpContext http)` |
| `Api/Http/ResultExtensions.cs` | `ToHttpResult`, `ToCreatedResult(Func<T,string> location, Func<T,CancellationToken,Task<string?>> etag)` (post-commit ETag read), `ToAcceptedResult(Func<T,string?>? location = null)`, `ToNoContentResult` |
| `Api/Http/Filters/{RequireIdempotencyKeyFilter,RequireIfMatchFilter}.cs` | endpoint filters + `RouteHandlerBuilder` extensions `RequireIdempotencyKey()`, `RequireIfMatch()` |
| `Application/Common/ETags.cs` (handlers compare, D7) | `Episode(uint xmin)` → `"ep-{xmin}"`, `Series(uint xmin)` → `"se-{xmin}"`, `Matches(string? ifMatch, string current)` (strong, exact) |

## 4. Use cases

| # | Use case | Result |
|---|---|---|
| UC1 | A handler returns a validation error list | 400 `validation_failed`, `errors: { field: [messages] }` |
| UC2 | A handler returns `episode_invalid_state` | 409 ProblemDetails with `code`, `traceId` |
| UC3 | A handler returns a 429 code with `retryAfterSeconds` | 429 + `Retry-After: <n>` |
| UC4 | A paid POST without `Idempotency-Key` | 400 `idempotency_key_required` before Mediator runs |
| UC5 | A D9/D15 route without `If-Match` | 428 `if_match_required` |
| UC6 | An unhandled exception | 500 `internal_error`, no exception text in the body |

## 5. API and events

ProblemDetails body:

```json
{ "type": "https://nighttales.app/errors/episode_invalid_state", "title": "Episode state does not allow this action",
  "status": 409, "code": "episode_invalid_state", "traceId": "00-…", "errors": null }
```

The type URI host is ⚠️ a placeholder until 11 fixes the public domain. Extra metadata keys from `Error.Metadata` are copied as top-level members only when they are on the allow-list `retryAfterSeconds`, `field`, `limit` [DECISION], so that no internal data leaks.

## 6. UI

None (backend only). 10 maps every `code` to an i18n key (00 §5.4).

## 7. Jobs and integrations

None.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Stack traces or SQL in responses | the 500 path writes a fixed title; details are logged with the `traceId` only |
| Id probing via 403 vs 404 | the catalogue maps other-tenant misses to `not_found` 404 (D10) |
| Metadata leakage | metadata allow-list (§5) |

## 9. Test plan

Unit tests for the mapper and the catalogue invariants; integration tests for the filters on a probe endpoint through `ApiFactory`.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.1.4-T1 | `ErrorCodes`, `ErrorCatalogue`, ProblemDetails writer, result helpers, endpoint filters, `ETags` | S (0.5) | 09.1.3-T1 | 09.4.1-T1, 09.5.1-T1, 10.2.2-T1 (10 CR-8) |

Total **0.5 d** (unchanged).

## 11. Rollout

- No flag. From now on every new code needs a constant and a catalogue row in the same PR; the unit test enforces this.
- `openapi.json` gets a shared `ProblemDetails` schema component (09.9.5 diff).
