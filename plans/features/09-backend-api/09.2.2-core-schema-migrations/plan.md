# 09.2.2 — Core schema and migrations

> Refines: `../../../09-backend-api.md` §4 (epic 09.2, story **09.2.2**, L84–86); `../../../00-overview.md` §6 (rows of the P core set).
> Contract: `../README.md` — **F07**, **F08**, **F11**, **F12**, **D3**, **D6**, **D8**, **D9**, **D11**, **D16**; CR-4, CR-5.
> Template: `../../../00-overview.md` §10.1. Legend: ✅ · ⚠️ · [DECISION] · [OPEN] · [INFERENCE].
> Status: detailed plan, 2026-09-24. Nothing implemented yet.

## 1. Scope

**Story.** 09.2.2 *Entity configurations and migrations* — Must · detailed **3.0 d** (T1 L 2.0 + T2 M 1.0).

**In scope.**
- The **P core set** (D9): entity shells and EF configurations.
- The `InitialCore` migration.
- The migration bundle.
- The CI migration check that every later migration must pass.

**Out of scope, owner in brackets (D9):**
- [02] `generation_runs`, `story_sources`, `fetch_tasks`, `fact_sheets`, `source_candidates`, `price_versions`, `series_bibles`, `episode_ideas`, and the CR-4 extra columns of `story_versions` / `autopilot_runs`, including the unique index `(episode_id, version)` (02.2.2 plan §3.2).
- [09.3.x] `refresh_tokens`, `auth_challenges`, `trusted_devices`, `user_consents`.
- [09.3.6-T1] `audit_logs`.
- [09.6.1] `render_jobs`.
- [09.10.1 / 11] `worker_nodes`, `system_settings`, `system_setting_versions`.
- [06] `channel_connections`, `publications`, `platform_quota_usage`.
- [05] `music_tracks` — created by 05.3.2-T3 (`AddMusicTracks`), after 09.2.2-T2 (05 CR-12).
- [13] `data_deletion_requests` (13.3.1-T1); `legal_documents` is S and not created in P (13 CR-1).
- All S-only tables (not in P).
- Behaviour methods on the entities: Series (09.4.1), Episode (09.5.1, 02), autopilot (02.8.3).

## 2. Confirmed facts

| # | Fact | Source |
|---|---|---|
| G1 | 02 expects 09.2.2 to create `story_versions`, `autopilot_runs`, `series_autopilot_authorizations`, `moderation_events`, `usage_records` | 02.2.2 plan L117, 02.8.3 plan L113, 02.2.4-T1 L35, 02.4.1-T1 L85 ✅ |
| G2 | 02 queries `usage_records` by `generation_run_id`, `reservation_id` and `created_at` ranges | 02.6.1-T1, 02.2.2-T2 ✅ |
| G3 | `moderation_events.excerpt` ≤ 300 chars is deleted with the episode | 02.8.2-T2 L180, 02 README ✅ |
| G4 | Identity's default `EmailIndex` on `NormalizedEmail` is **not unique** ⚠️ (Identity `RequireUniqueEmail` is enforced in code) → a unique index is added explicitly (F12) | ASP.NET Core Identity model ⚠️ |

## 3. Domain and data

Conventions for every table (D8): `id uuid` PK (UUIDv7, app-generated) unless noted; `created_at timestamptz NOT NULL`; `updated_at timestamptz NOT NULL` where mutable; enum columns are text with `CHECK`; aggregates carry `xmin`. `IUserOwned` = has `user_id NOT NULL` FK `users` ON DELETE RESTRICT.

**`users`** (Infrastructure `AppUser : IdentityUser<Guid>`; Identity columns in snake_case, plus):

| Column | Type | Null | Constraint |
|---|---|---|---|
| `display_name` | varchar(80) | no | |
| `ui_locale` | text | no | CHECK `vi`/`en`, default `vi` |
| `role` | text | no | CHECK `user`/`admin`/`owner`, default `user` — authoritative role [DECISION]; the Identity role tables stay empty |
| `status` | text | no | CHECK `active`/`suspended`/`deleted` |
| `last_totp_step` | bigint | yes | |
| `owner_dispute_freeze_until`, `last_login_at` | timestamptz | yes | |
| `created_at`, `updated_at` | timestamptz | no | |

Unique index `ux_users_normalized_email` on `normalized_email` (G4, F12). The Identity `security_stamp` and `two_factor_enabled` columns cover 00 §6.

**`series`** (aggregate, `IUserOwned`): `title varchar(120)`; `topic` CHECK `ghost`/`spiritual`/`true_crime`; `language` CHECK `vi`/`en`; `aspect` CHECK `landscape`/`portrait`; `voice_id text` FK `voices` RESTRICT; `style_template_id uuid` FK `style_templates` RESTRICT; `story_preset text` null; `default_channel_id uuid` null (FK added by 06); `archived_at` null; `autopilot_enabled bool` default false; `autopilot_settings jsonb` null; `current_autopilot_authorization_id uuid` null FK `series_autopilot_authorizations` SET NULL; `autopilot_paused_at` null; `autopilot_pause_reason text` null. Indexes `(user_id, created_at DESC, id DESC)`, `(user_id) WHERE archived_at IS NULL`.

**`episodes`** (aggregate, `IUserOwned`): `series_id` FK `series` RESTRICT (series are archived, never deleted [DECISION]); `title varchar(120)` null; `brief varchar(1000)` NOT NULL; `length_tier` CHECK `short`/`standard`/`long`; `status` CHECK (D16 names, snake_case); `current_story_version_id`, `approved_story_version_id` FK `story_versions` NO ACTION, null; `final_video_asset_id` FK `assets` SET NULL, null. Indexes `(user_id, created_at DESC, id DESC)`, `(user_id, status, created_at DESC, id DESC)`, `(series_id, created_at DESC, id DESC)`.

**`story_versions`** (entity, `IUserOwned`, immutable): `episode_id` FK CASCADE; `version int`; `source` CHECK `generated`/`revised`/`edited`; `title text`; `body text`; `keywords text[]`; `word_count int`; `model text` null; `prompt_version text` null; `content jsonb`; `audit_report jsonb` null; `similarity_score numeric(5,4)` null. Index `(episode_id)`. 02.2.2-T1 adds its columns and the unique index.

**`series_autopilot_authorizations`** (entity, `IUserOwned`): `series_id` FK RESTRICT; `version int`; `text_version text`; `text_hash char(64)`; `topic` CHECK; `channel_connection_id uuid` null (FK by 06); `cadence text`; `privacy_status` CHECK `private`/`unlisted`/`public`; `accepted_at`; `revoked_at` null. Unique `(series_id, version)`.

**`autopilot_runs`** (entity, `IUserOwned`): `series_id` FK RESTRICT; `started_at`; `idea_id uuid` null (FK by 02.8.1); `episode_id` FK SET NULL, null; `outcome` CHECK (00 §6 values) null; `gate_results jsonb` null; `pause_reason text` null. Index `(series_id, started_at DESC)`. 02.8.3-T1 adds its columns.

**`moderation_events`** (entity, `IUserOwned`): `episode_id` FK CASCADE, null (G3); `source` CHECK `openai_image`/`openai_text`/`internal`; `stage text`; `categories text[]`; `excerpt varchar(300)` null. Index `(user_id, created_at DESC)`.

**`usage_records`** (entity, `IUserOwned`, append-only): `episode_id` FK SET NULL, null; `series_id uuid` null FK `series` RESTRICT; `generation_run_id uuid` null (FK added by 02.2.2-T1, G2); `render_job_id uuid` null (FK by 09.6.1); `reservation_id uuid` null; `provider text`; `operation` CHECK `story`/`story_review`/`story_revision`/`fact_sheet`/`image`/`tts`/`render`/`bible_proposal`/`idea`/`source_find`/`moderation`; `model text` null; `input_tokens`, `cached_input_tokens`, `output_tokens`, `image_count int` default 0; `gpu_seconds numeric(10,2)` default 0; `estimated bool`; `cost_usd numeric(12,6)`; `price_version int` null; `provider_request_id text` null. Indexes `(created_at)`, `(reservation_id)`, `(generation_run_id)`, `(user_id, created_at DESC)`.

**`assets`** (entity, `IUserOwned`): `episode_id` FK CASCADE; `kind` CHECK `video`/`thumbnail`/`audio`/`subtitle`/`image`; `storage_key text` unique; `content_type text`; `size_bytes bigint`; `duration_sec numeric(9,2)` null; `sha256 char(64)`; `retention_until` null; `deleted_at` null. Indexes `(episode_id)`, `(retention_until) WHERE deleted_at IS NULL`.

**`voices`** (platform, not user-owned): `id text` PK; `display_name text`; `language` CHECK; `gender` CHECK `female`/`male`/`neutral`; `tags text[]`; `preview_storage_key text` null; `active bool`.

**`style_templates`** (platform): `name text` unique; `template text` CHECK `position('{term}' in template) > 0`; `preview_storage_key` null; `topic_hint` CHECK topic values, null; `active bool`.

**`feature_flags`** (platform): `key text` PK; `enabled bool`; `rules jsonb` default `'{}'`; `updated_at`.

**`outbox_messages`** (D11): `type text`; `payload jsonb`; `user_id uuid` null (no FK — the row must survive user deletion for dispatch [DECISION]); `occurred_at`; `next_attempt_at`; `attempts int` default 0; `processed_at`, `failed_at` null; `error varchar(2000)` null; `trace_parent text` null. Partial index `(next_attempt_at) WHERE processed_at IS NULL AND failed_at IS NULL`; `(processed_at)` for cleanup.

**`idempotency_keys`** (D6): see 09.1.3 plan §3.

Migration strategy: `InitialCore` is one migration. All later changes are expand-only migrations named `<yyyyMMddHHmm>_<Owner>_<What>` (D9). Contract steps (drops) wait one release (NFR-09-6).

## 4. Use cases

| # | Use case | Behaviour |
|---|---|---|
| UC1 | A new environment | `nts-migrate` (the bundle) applies `InitialCore` |
| UC2 | A feature adds a migration | CI proves: empty DB → all; previous schema + seed → new; model has no pending changes |
| UC3 | A developer changes an entity without a migration | CI fails `Model_HasNoPendingChanges` |

## 5. API and events

None.

## 6. UI

None (backend only).

## 7. Jobs and integrations

Migration bundle `nts-migrate` (a self-contained executable from `dotnet ef migrations bundle`), run as a one-shot step before rollout (11.5.1). The API never migrates at start in non-Development environments [DECISION]; in Development `dotnet ef database update` or the TestKit template.

## 8. Security and privacy

| Threat | Control |
|---|---|
| Destructive migration | expand-only rule + review checklist (T2); `DROP` only in a contract migration one release later |
| Content kept after episode deletion | `moderation_events`, `story_versions` and `assets` cascade with the episode; `usage_records` keep numbers only (SET NULL) |
| Tenant columns set from requests | `user_id` is set by domain factories from the parent aggregate (D10) |

## 9. Test plan

T1: CHECKs and FKs behave (constraint tests on raw inserts). T2: migration tests (empty DB, previous schema + seed, no pending changes).

## 10. Tasks and re-estimate

| Task | Title | Size | Depends on | Blocks |
|---|---|---|---|---|
| 09.2.2-T1 | Core entity shells and EF configurations | L (2.0) | 09.2.1-T1 | 09.2.2-T2, 09.2.3-T1, 09.2.4-T1 |
| 09.2.2-T2 | `InitialCore` migration, bundle, CI migration check | M (1.0) | 09.2.2-T1, 09.1.5-T1 | 09.1.3-T2, 09.2.5-T1, every feature migration (incl. 11.3.3-T1, 11 CR-11), 09.10.1-T1 (09 W3 CR-17) |

Total **3.0 d** (unchanged; the scope moved from "every table" to the core set, and the freed time pays for the full CHECK/index set).

## 11. Rollout

- `InitialCore` ships once. After the first deploy it is never edited, only followed by new migrations.
- Rollback: before the first production deploy, regenerate freely; after it, only forward fixes.
