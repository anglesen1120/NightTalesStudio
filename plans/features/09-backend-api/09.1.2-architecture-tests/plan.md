# 09.1.2 — Architecture tests

> Refines: `../../../09-backend-api.md` §4 (epic 09.1, story **09.1.2**, L61–64), §7 (architecture tests).
> Contract: `../README.md` — **F01**, **F02**, **F18**, **D1**, **D2**, **D4**, **D10**, **D11**, **D12**.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.1.2 *Architecture tests* — "As the Tech Lead I want the layer rules tested so that they cannot erode." Must · detailed **M 1.0**.

**In scope.** One test project, `tests/NightTales.Architecture.Tests`, with every structural rule the contract names:
- the layer and package allow-lists (D1);
- handler placement and sealing (D4);
- Hangfire confinement and ids-only job parameters (D12);
- `[OutboxEvent]` on every domain event (D11);
- `IUserOwned` on episode/series children, and the `IgnoreQueryFilters` allow-list (D10);
- Fetcher isolation (02 CR-19);
- a ban on `AddDbContextPool` (09.2.1 decision).

**Out of scope.** Rules that need runtime data, such as the cross-tenant route table (09.2.3-T2). Rules that belong to later waves are defined here but only become active when their types exist (the rules are written against namespaces and markers, so an empty set passes).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| A1 | ArchUnitNET 0.13.4 + `TngTech.ArchUnitNET.xUnitV3` adapter, Apache-2.0, actively maintained; NetArchTest's last release is 2021 | README N10, F18 ✅ |
| A2 | ArchUnitNET loads assemblies and checks type dependencies (fields, parameters, base types, attributes, calls to members of other types) | ArchUnitNET docs ⚠️ (member-call detail to confirm in the T1 spike) |
| A3 | Package rules must be checked on **assembly references**, not type dependencies: a package referenced but unused leaves no type dependency | [INFERENCE] |
| A4 | Domain may reference `ErrorOr` only; Application may reference Domain, `Mediator.Abstractions`, `FluentValidation`, `ErrorOr`, `Microsoft.EntityFrameworkCore` | README D1, F01, F02 ✅ |

## 3. Domain and data

No domain types or tables. Test-only types:

| File | Purpose |
|---|---|
| `tests/NightTales.Architecture.Tests/ArchitectureModel.cs` | one static `Architecture` loaded from all `src` assemblies (`ArchLoader().LoadAssemblies(...)`) |
| `tests/NightTales.Architecture.Tests/AssemblyReferenceRules.cs` | package/project allow-lists via `Assembly.GetReferencedAssemblies()` (A3) |
| `tests/NightTales.Architecture.Tests/LayerRules.cs` | type-dependency rules between layers |
| `tests/NightTales.Architecture.Tests/ConventionRules.cs` | handlers, events, jobs, `IUserOwned` |
| `tests/NightTales.Architecture.Tests/CallSiteRules.cs` | `IgnoreQueryFilters` and `AddDbContextPool` call sites, via IL scan (`System.Reflection.Metadata`) when A2 is insufficient |
| `tests/NightTales.Architecture.Tests/AllowLists.cs` | the named allow-lists (single place to edit) |

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | A developer adds a forbidden reference (for example Npgsql in Application) | `dotnet test` fails, naming the assembly and the rule |
| UC2 | A developer adds a handler outside `Features/*/Commands|Queries/*` or unsealed | the test fails with the type name |
| UC3 | A later feature adds a job method taking a `string story` | the ids-only rule fails |
| UC4 | A feature needs a new `IgnoreQueryFilters` site | it must edit `AllowLists.cs`, which is visible in review |

## 5. API and events

None. The tests read compiled assemblies only.

## 6. UI

None (backend only).

## 7. Jobs and integrations

None. The suite runs in the default `dotnet test NightTales.ci.slnf` (09.1.5-T1) and needs no Docker.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Tenant bypass by an unreviewed `IgnoreQueryFilters` | call-site rule + allow-list (D10) |
| Content or secrets in Hangfire job arguments (stored in plain text in the `hangfire` schema) | ids-only parameter rule (D12) |
| Fetcher gaining DB/OpenAI access | Fetcher references Domain/Fetcher.Core only (02 CR-19) |
| Pooled context leaking the tenant field | `AddDbContextPool` banned (09.2.1) |

## 9. Test plan

Each rule has one test (names in T1). The suite proves itself once: a deliberate violation per rule family is committed on a scratch branch, CI fails, and the branch is deleted. The evidence (CI run link) goes in the PR description.

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.1.2-T1 | Architecture rules: packages, layers, conventions, call sites | M (1.0) | 09.1.1-T1 | 09.1.5-T1 |

Total **1.0 d** (high-level S 0.5; +0.5 for the D1/D10/D11/D12 rules, README §9).

### Open items for the TL

- ⚠️ A2: if ArchUnitNET cannot see method calls on generic extension methods (`IgnoreQueryFilters`), `CallSiteRules` uses an IL scan [DECISION]; the result is recorded in the T1 notes.

## 11. Rollout

- Ships with the skeleton; CI blocks merges from then on (gate `09-skeleton`).
- New rules are added by the task that introduces the concept; allow-list edits need TL review (CODEOWNERS on `AllowLists.cs` [DECISION]).
- Rollback: none needed; a false positive is fixed in the rule, never by skipping the test.
