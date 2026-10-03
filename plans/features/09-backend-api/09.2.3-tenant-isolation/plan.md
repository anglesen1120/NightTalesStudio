# 09.2.3 — Tenant filter and `SystemContext`

> Refines: `../../../09-backend-api.md` §4 (epic 09.2, story **09.2.3**, L88–95), §2 T6, §7; `../../../00-overview.md` §5.2 tenant isolation.
> Contract: `../README.md` — **N9**, **D3**, **D8**, **D10**, **D13**.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: 09.2.3-T1 **implemented 2026-09-30** (PR #52) — independent QA, code/performance/security reviews passed. 09.2.3-T2 (cross-tenant route table, phase S) not started. Security-relevant: review by the TL plus a second reader (00 §10.1).

## 1. Scope

**Story.** 09.2.3 — "As the platform I want every user-owned row filtered by owner, in the API and in the Worker, so that tenants cannot see each other." Must · detailed **2.0 d** (T1 P M 1.0, T2 **S** M 1.0).

**In scope.**
- The EF named filter `"tenant"` on every `IUserOwned` entity.
  - Intentionally unfiltered (global catalogs, no `user_id`, not `IUserOwned`): `MusicTrack` / `music_tracks` (05.3.2-T3, 05 D8). A tenant filter there would empty the candidate set and silently yield `music_none`.
- `ICurrentUser` (Api and Worker implementations).
- `ISystemContext.For(userId)` and `ISystemWorkQuery`.
- The allow-list entries for `IgnoreQueryFilters` (the rule itself is in 09.1.2-T1).
- The 404 policy.
- In phase S: the cross-tenant route table generated from `openapi.json` (T2).

**Out of scope.** JWT and claims issuance (09.3.2). Admin endpoints (09.9.1).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| T1f | EF Core 10 named query filters: `HasQueryFilter("tenant", …)`, disable one filter with `IgnoreQueryFilters(["tenant"])` | README N9 ✅ (syntax ⚠️ confirm against the sample) |
| T2f | A filter that references a `DbContext` instance member is parameterized per query, not baked into the cached model | EF Core docs "Global Query Filters" ⚠️ |
| T3f | Every user-owned table carries `user_id` and implements `IUserOwned`; child `user_id` is copied from the parent, never from the body | 09 L89–93 ✅ |

## 3. Domain and data

No new tables. Types:

| Type | Path | Notes |
|---|---|---|
| `ICurrentUser` | `Application/Abstractions/ICurrentUser.cs` | `Guid? Id`, `bool IsAuthenticated`, `PlatformRole Role`, `ActorType Actor` |
| `ActorType` | `Domain/Common/ActorType.cs` | `User`, `Admin`, `System` |
| `ISystemContext` | `Application/Abstractions/ISystemContext.cs` | `IDisposable For(Guid userId)` |
| `ISystemWorkQuery` | `Application/Abstractions/ISystemWorkQuery.cs` | `IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(Expression<Func<T,bool>> where, int batch, CancellationToken ct) where T : Entity, IUserOwned` |
| `ITenantScopeObserver` | `Application/Abstractions/ITenantScopeObserver.cs` | `void MarkObserved()`; the two `DbContext`s report "a `DbContext` already exists in this scope" through it, so `ISystemContext.For` after a context was resolved throws without persistence depending on a concrete type |
| `HttpCurrentUser` | `Api/Security/HttpCurrentUser.cs` | reads `sub`, `role` claims; `role` only behind a valid, non-empty `sub` (no user → `user`/`user`) |
| `ScopedCurrentUser` | `Infrastructure/Security/ScopedCurrentUser.cs` | settable once; implements `ICurrentUser`, `ISystemContext` and `ITenantScopeObserver` |
| `SystemWorkQuery` | `Infrastructure/Persistence/Queries/SystemWorkQuery.cs` | the only type in `src` allowed to call `IgnoreQueryFilters` outside Admin (allow-list) |
| `TenantFilterConvention` | `Infrastructure/Persistence/Conventions/TenantFilterConvention.cs` | adds `"tenant"` to every `IUserOwned` entity type in both contexts |
| `ITenantFilterContext` (internal) | `Infrastructure/Persistence/Conventions/TenantFilterConvention.cs` | `Guid? CurrentUserId`; implemented by `AppDbContext`/`ReadDbContext` so one convention filters both, and its body references the *context instance* member (plan §2 T2f parameterization) |

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | User A reads B's episode by id | the filter hides the row → handler returns `not_found` → 404 |
| UC2 | Anonymous context queries a user-owned set | 0 rows (the filter compares with `NULL`) |
| UC3 | A Worker handler runs for A's outbox message | `ISystemContext.For(A)` before resolving contexts; B's rows invisible |
| UC4 | A sweep across users | `ISystemWorkQuery` yields `(id, user_id)` pairs; each item is processed in its own scope under `For(user_id)` |
| UC5 | Code outside the allow-list calls `IgnoreQueryFilters` | architecture test fails (09.1.2-T1) |

## 5. API and events

No routes. The 404 policy applies to every `{id}` route of every feature.

## 6. UI

None (backend only).

## 7. Jobs and integrations

`ISystemContext` is used by `OutboxDispatcher` (09.2.4-T1), by the recurring jobs, and by the 02 autopilot orchestrator (`SystemContext.For(run.UserId)`, 02.8.3-T1).

## 8. Security and privacy

| Threat | Control |
|---|---|
| Cross-tenant read by a missing `Where` | the filter is on by default for every `IUserOwned` type (convention, not per-query code) |
| Cross-tenant write via a body-supplied `userId` | `user_id` is set from the loaded parent aggregate in domain factories; request DTOs have no `userId` |
| Worker job runs unfiltered | there is no "no user" mode: without `For(...)`, `Id` is null and nothing is visible; `For` refuses `Guid.Empty` and a second call |
| Id probing | 404 for others' resources, never 403 |
| Filter lost on the read context | both contexts apply `TenantFilterConvention`; a test covers `IReadDb` |

## 9. Test plan

Integration tests for UC1–UC4 on both contexts; a unit test for `ScopedCurrentUser` guards. T2 (S): the generated route table.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.2.3-T1 | Tenant filter, `ICurrentUser`, `ISystemContext`, `ISystemWorkQuery` | M (1.0) | 09.2.2-T1, 09.2.1-T2 | 09.1.3-T2, 09.2.4-T1, 09.4.1-T1, 09.2.3-T2 (09 W2 CR-9), 13.3.3-T1 |
| 09.2.3-T2 | Cross-tenant route table from `openapi.json` (phase S) | M (1.0) | 09.2.3-T1, 09.9.5-T1 | M2 gate |

Total **2.0 d** (unchanged).

## 11. Rollout

- T1 ships before the first feature entity is queried (09.4.1-T1).
- T2 activates in phase S. Until then, each P feature adds explicit 404 tests for its own `{id}` routes (09.4.1-T1, 09.5.1-T1 and the 02 tasks already do).
