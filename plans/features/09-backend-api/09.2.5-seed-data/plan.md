# 09.2.5 — Seed data

> Refines: `../../../09-backend-api.md` §4 (epic 09.2, story **09.2.5**), §6
> Contract: `../README.md` — **F22**, **D3**, **D8**, **D9**, **D10**, **D13**, **CR-11**
> Template: `../../../00-overview.md` §10.1
> Legend: ✅ read from source/docs · ⚠️ unverified · [DECISION] · [OPEN] · [INFERENCE]
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.2.5 *Seed data* — "As a developer I want catalogs seeded so that local dev works immediately." Must · **S** (detailed **0.75 d**, §10).

**In scope.** Idempotent seeders for the three catalogs `voices`, `style_templates`, `feature_flags` (all publishing flags off) and for the owner user; the Development auto-seed hook in **both** composition roots (Api, Worker); the explicit `nts-seed` command for non-Development environments; the seed report.

**Out of scope.** `GET /voices` / `GET /style-templates` (09.4.2); the catalog **content** itself (plan 04 owns the voices file, plan 03 owns the style templates); autopilot settings and authorizations (02.8.3); series bible seeding (02.6.3); `PATCH /admin/feature-flags/{key}` (09.9.2); demo series or episodes.

**Split** (F22, CR-11). **09.2.5-T1** = catalogs + flags, no Identity dependency. **09.2.5-T2** = owner user, depends on 09.3.1-T1.

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| S1 | §4 09.2.5: "Given a fresh DB, When the app starts in Development, Then `voices`, `style_templates`, `feature_flags` (all publishing off), and one `owner` user (from env; MFA enrolment forced at first login, 09.10.4) are seeded idempotently" | `09-backend-api.md` L101–102 ✅ |
| S2 | `voices` columns: `id` (**text PK**, the VieNeu voice name), `display_name`, `language`, `gender`, `tags text[]`, `preview_storage_key`, `active`. Curated catalog, owner plan 04 | `00-overview.md` §6 L303 ✅ |
| S3 | `style_templates` columns: `name`, `template` (contains `{term}`), `preview_storage_key`, `topic_hint`, `active`. Curated catalog, owner plan 03 | `00-overview.md` §6 L304 ✅ |
| S4 | `feature_flags` columns: `key`, `enabled`, `rules jsonb`; the 00 example names `youtube_publish`, `tiktok_publish` | `00-overview.md` §6 L296 ✅ |
| S5 | D9: `voices`, `style_templates`, `feature_flags` are created by the 09.2.2 `InitialCore` migration; this story adds **no** migration | README D9 ✅ |
| S6 | D10: the `"tenant"` filter is `e => e.UserId == _currentUserId`; catalog tables have no `UserId` and are therefore **not** `IUserOwned` and readable unauthenticated | README D10 ✅ |
| S7 | D13: `PostgresFixture` gives each test class its own database from a migrated template; `ApiFactory` hosts the Api; `WorkerHost` runs Worker services in-process | README D13 ✅ |
| S8 | F22 / CR-11: the owner seed needs Identity (09.3.1-T1) but sits in the persistence epic → split T1 (catalogs) / T2 (owner) | README F22, CR-11 ✅ |
| S9 | D3: `IClock.UtcNow` is the only clock; seeders take `now` or `IClock`, never `DateTimeOffset.UtcNow` directly | README D3 ✅ |
| S10 | D8: enums are stored as snake_case text with a `CHECK` constraint from `builder.HasEnumCheck<TEnum>(column)`; `language`/`gender`/`topic_hint` are validated by that CHECK | README D8 ✅ |
| S11 | `users` carries Identity + `role` (`user`/`admin`/`owner`), `status` (`active`), `two_factor_enabled`, `email_confirmed` via Identity's `EmailConfirmed`; uniqueness comes from Identity's unique `normalized_email` index, **no** `citext` (F12) | `00-overview.md` §6 L265, README F12 ✅ |
| S12 | MFA is mandatory for `admin`/`owner`; `09.10.4-T1` (TOTP) owns enrolment. The owner seed can only force enrolment at first login, it cannot create a TOTP secret | `00-overview.md` §6 L265, README F21 ✅ |
| S13 | Startup seeding is a hosted-service concern; the Worker already runs hosted services (`OutboxDispatcher`, D11) and the Api has a DI root — both are valid hook points | README D11, D13 ✅ |

**Closed here.** The Development auto-seed runs in **both** hosts (Api and Worker) because either may start first against a fresh database; the seeders are idempotent so a double run is harmless. Non-Development environments never auto-seed — an explicit `nts-seed` invocation is required [DECISION].

## 3. Domain and data

### 3.1 Types — `src/NightTales.Infrastructure/Persistence/Seeding/`

| File | Type / signature | Notes |
|---|---|---|
| `ISeeder.cs` | `interface ISeeder { string Name { get; } int Order { get; } ValueTask<SeedReport> SeedAsync(CancellationToken ct); }` | one implementation per catalog; `Order` fixes the run sequence |
| `SeedReport.cs` | `record SeedReport(string Seeder, int Inserted, int Updated, int Skipped)` | counts only; never carries a value read from the database |
| `SeedRunner.cs` | `sealed class SeedRunner(IEnumerable<ISeeder> seeders, ILogger<SeedRunner> logger) { ValueTask<SeedReport[]> RunAsync(CancellationToken ct); }` | runs seeders in `Order`, logs one line per seeder, continues after a failed seeder only when `Seed:ContinueOnError` is true |
| `VoiceSeeder.cs` | `sealed class VoiceSeeder(IAppDbContext db, IClock clock) : ISeeder` | upserts by natural key `id` (the text PK); end state after 04.2.2-T2: reads `seed/voices.v1.json` in every environment, validates with `VoiceCatalogRules`, refuses `revision` 0 outside Development, upserts `preview_storage_key` too (04 CR-9) |
| `StyleTemplateSeeder.cs` | `sealed class StyleTemplateSeeder(IAppDbContext db, IClock clock) : ISeeder` | upserts by natural key `name` |
| `FeatureFlagSeeder.cs` | `sealed class FeatureFlagSeeder(IAppDbContext db, IClock clock) : ISeeder` | upserts by natural key `key`; **never** flips an existing `enabled` |
| `OwnerSeeder.cs` (T2) | `sealed class OwnerSeeder(UserManager<AppUser> users, IOptions<SeedOptions> options, IClock clock) : ISeeder` | no-op when any owner exists |

### 3.2 Semantics (frozen)

- **Upsert by natural key, never delete.** A row whose natural key is absent is inserted; a present row is updated only in the fields the catalog owns; **no** row is ever deleted, so a user-edited or admin-toggled row survives a reseed.
- **`feature_flags` exception.** `FeatureFlagSeeder` inserts a missing key with `enabled = false`; when the key exists it leaves `enabled` and `rules` untouched [DECISION]. Only the flags listed in §3.4 are seeded; other flags are never created or removed.
- **Idempotence.** Two consecutive runs on the same database produce byte-identical rows: the second run reports `Inserted = 0` and every row `Skipped` (or `Updated` with no field change → still `Skipped`).
- **Seeders do not open transactions.** They run inside the existing `AppDbContext` scope and call `SaveChangesAsync` themselves — they are **not** commands and do not go through `MediatorPipeline.Behaviors` (D4). A seeder failure is logged and surfaces as a non-zero process exit for `nts-seed`; the Development auto-seed logs and continues boot [DECISION].
- **No `IUserOwned`.** The three catalog tables are platform data (D10, S6): the `"tenant"` filter does not apply and `IgnoreQueryFilters` is not needed.

### 3.3 Config

```jsonc
{
  "Seed": {
    "CatalogFile": "seed/catalog.dev.json",   // dev minimal set; the full voice catalog comes from plan 04
    "StyleTemplatesFile": "seed/style-templates.v1.json",   // plan 03 catalogue, every environment (03 CR-1)
    "ContinueOnError": false,
    "Owner": { "Email": "", "Password": "" }  // bound from Seed__Owner__Email / Seed__Owner__Password (T2)
  }
}
```

`SeedOptions` (Infrastructure) is bound from `Seed` and validated `ValidateOnStart`: `CatalogFile` must exist when the auto-seed is enabled. `Seed:Owner:Password` is never bound into a log-visible object; only T2 reads it, from configuration, at seed time.

### 3.4 Seed content (dev minimal set) ⚠️

| Catalog | Rows | Natural key |
|---|---|---|
| `voices` | 2 × `Language.Vi` + 2 × `Language.En`, all `active = true`, ⚠️ names to confirm from plan 04 | `id` (VieNeu voice name) |
| `style_templates` | the 7 templates of `seed/style-templates.v1.json` (plan 03 D2), every environment, validated by `StyleTemplateRules` (03 CR-1) | `name` |
| `feature_flags` | `youtube_publish`, `tiktok_publish`, `source_finder` — all `enabled = false`, `rules = {}` | `key` |

`source_finder` is the 02.7 flag key ✅ (`../02-story-writing/README.md` §0); `youtube_publish` and `tiktok_publish` are the 00 §6 / 06 publishing keys ✅. A flag key that is absent from the table reads as **off** (09.9.2 default), so a misspelt key cannot silently enable a feature.

### 3.5 Data

| Object | Access | Owner |
|---|---|---|
| `voices`, `style_templates`, `feature_flags` | insert/update via `IAppDbContext` | 09.2.2-T2 (tables) · **here** the seed rows |
| Identity `users` + `user_roles` | write via `UserManager`/`RoleManager` (T2) | 09.3.1-T1 |

No migration, no new table.

## 4. Use cases

| # | Use case | Entry point | Behaviour |
|---|---|---|---|
| UC1 | Fresh dev database | Api or Worker start, `IHostEnvironment.IsDevelopment()` | `SeedRunner.RunAsync` in a startup hosted service; idempotent; a failure logs and boot continues |
| UC2 | Non-dev environment | `nts-seed` command (operator) | the same `SeedRunner`, `ContinueOnError = false`; process exit code 0 on success, 1 on failure |
| UC3 | Reseed after a catalog edit | `nts-seed` | missing keys inserted; existing rows updated only in catalog-owned fields; an existing flag's `enabled` is preserved |
| UC4 | Owner bootstrap (T2) | `nts-seed` with `Seed__Owner__*` set | creates exactly one `owner` when none exists; no-op otherwise |

**Authorization/idempotency.** No HTTP route, so no authorization behaviour and no `IIdempotentCommand`; idempotence is the seeders' own contract (§3.2). `nts-seed` is an operator tool, not a user-facing command.

## 5. API and events

**No route, no DTO, no outbox event.** The catalogs are read later by 09.4.2 (`GET /voices`, `GET /style-templates`) and mutated later by 09.9.2 (`PATCH /admin/feature-flags/{key}`); this story only writes the initial rows. Seeding raises no domain event and writes no `outbox_messages` row.

## 6. UI

None (backend only): the seed produces rows for endpoints owned by 09.4.2 and 09.6+; it renders nothing.

## 7. Jobs and integrations

| Item | Detail |
|---|---|
| Hosted service | `SeedHostedService` (`Api` + `Worker` composition roots) — runs only when `IHostEnvironment.IsDevelopment()` **and** `Seed:Enabled != false` [DECISION] |
| CLI | `nts-seed` — the host's `--seed` switch (a `Program.cs` branch that runs `SeedRunner` and exits), so it needs no extra project |
| Job | **None.** No Hangfire job, no queue, no retry policy; `IJobScheduler` is untouched |
| External calls | None. No OpenAI, no MPT, no storage upload — `preview_storage_key` values are seeded as strings, the preview objects themselves belong to plan 03/04 ⚠️ |
| Metrics / logging | one log line per seeder: `seeder`, `inserted`, `updated`, `skipped`, `durationMs`; the owner seeder logs the e-mail domain only, **never** the password (T2, §8) |

## 8. Security and privacy

| Threat / rule | Control |
|---|---|
| Owner password leakage | read from `Seed__Owner__Password` at seed time; never logged, never in a `SeedReport`, never in an exception message; the `SeedOptions` password property is not `ToString`-ed |
| Seeding in production | the auto-seed is Development-gated; non-dev seeding is an explicit operator command; a production boot never writes catalog rows |
| Privilege escalation | the owner seed creates exactly one `owner` and is a no-op when any owner exists, so it cannot add a second privileged account |
| Catalog tampering | seeders never delete; an admin-disabled flag or a deactivated voice survives a reseed (UC3) |
| MFA bypass | the owner seed cannot mark MFA enrolled; it only ensures the account exists with `EmailConfirmed = true`, and 09.10.4-T1 forces enrolment at first login (S12) |

## 9. Test plan

| Level | Cases |
|---|---|
| Unit (Infrastructure) | natural-key upsert inserts a missing row and leaves a present row untouched; `FeatureFlagSeeder` preserves an existing `enabled`; `SeedReport` counts |
| Integration (Api.IntegrationTests) | `SeedRunner` twice on one database → identical row sets and `Inserted = 0` on the second run; Development boot seeds; non-Development boot does not; `--seed` seeds |
| Integration (owner, T2) | owner created once with role `owner` + `EmailConfirmed`; second run is a no-op; the password never appears in a captured log |
| Manual | `nts-seed` on a fresh non-dev database → exit 0, catalogs present, flags off |

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.2.5-T1 | Catalog and flag seeders + Development/`nts-seed` hook | **S (0.5)** | 09.2.2-T2 | 09.4.1-T1, 04.2.2-T2 (04 CR-9) |
| 09.2.5-T2 | Owner seed from env | **XS (0.25)** | 09.3.1-T1 | — |

Total **0.75 d** — matches README §9 (`09.2.5-T1 S 0.5`, `09.2.5-T2 XS 0.25`).

### Open items for the TL

- **[RESOLVED] Voice names (04 CR-9).** 09.2.5-T1 ships the 2 vi + 2 en placeholder voices. Plan 04 task 04.2.2-T2 (after this task) moves the voice source to `seed/voices.v1.json` in every environment and removes the placeholder block; owner-approved 2026-09-24 (04 §10).
- **[OPEN] Style-template names/templates.** Same shape for plan 03; `template` must keep its `{term}` placeholder or the writer cannot substitute.
- **[DECISION] Seed in both hosts.** Api **and** Worker run the Development auto-seed, because either may start first on a fresh database; idempotence makes the double run harmless.
- **[DECISION] Flag `enabled` is never overwritten on reseed.** A reseed repairs missing keys and catalogs, it does not undo an admin toggle (UC3, §3.2).

## 11. Rollout

- **Flag:** none — the auto-seed is Development-gated and the catalog rows are data.
- **Order:** 09.2.2-T2 (tables) → 09.2.5-T1 → 09.4.1-T1 → 09.5.1-T1; 09.2.5-T2 after 09.3.1-T1.
- **Migration order:** none (S5).
- **Environment note:** the seed content file is dev-only; after 04.2.2-T2, `seed/voices.v1.json` is read in every environment and must be revision ≥ 1 outside Development (04 CR-9); production catalogs are loaded by the operator's `nts-seed` run with the plan 03/04 catalog files, or by the admin endpoints (09.9.1).
- **Rollback:** `Seed:Enabled = false` disables the auto-seed without a deploy; seeded rows are data and are not removed automatically.
