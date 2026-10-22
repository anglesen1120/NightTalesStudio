using NightTales.TestKit.Database;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The dataset <c>Migrations_ApplyOnPreviousSchemaWithSeed</c> writes into schema N-1 *before* the
/// migration under test runs, so a migration that breaks existing data fails there instead of in
/// production (09.2.2-T2 by way of 02.2.2 plan L738).
/// </summary>
/// <remarks>
/// <para>
/// The rows are raw SQL, not the write model: the point is a schema that is one release behind the
/// model, which no current <c>AppDbContext</c> can express. Every insert must therefore stay valid for
/// the schema *before* the migration it guards — a column a later migration adds must not appear here
/// until that migration has shipped.
/// </para>
/// <para>
/// The dataset is the core set plan §3 promises 02 — one user, one series, two episodes, one story
/// version and one usage record, plus the two catalog rows the series row needs (a <c>voices</c> and a
/// <c>style_templates</c> row). A feature migration that adds a table (05.3.2-T3 <c>AddMusicTracks</c>,
/// 11.3.3-T1 <c>WorkerNodes</c>, …) extends <see cref="Dataset"/> with a row of its own table, so the
/// migration after it is exercised against data. With only <c>InitialCore</c> compiled, N-1 is the empty
/// schema, nothing is inserted, and the run degenerates to "apply on empty" — <see cref="ExecuteAsync"/>
/// skips a table the previous schema does not have instead of failing.
/// </para>
/// <para>
/// The same N-1 rule applies to the columns: <see cref="CaptureTablesAsync"/> records the columns each
/// table has *before* the migration runs, and <see cref="SnapshotAsync"/> hashes exactly that projection
/// on both sides. A migration that adds a column therefore does not disturb the comparison, while a
/// rewrite of an inherited column still does. A migration that adds columns to an *existing* table (with
/// or without a backfill) is exercised by a row of that table in its old shape — see the
/// <c>autopilot_runs</c> row 02.8.3-T1 added, which pins the backfill of `_02_AutopilotRunsStateAndSlot`, and
/// the <c>series_autopilot_authorizations</c> row 02.8.3-T2 added for the nullable `revoked_reason` of
/// `_02_AutopilotAuthorizationRevokedReason`.
/// </para>
/// </remarks>
internal static class SeedPrevious
{
    /// <summary>One seed row: the table it needs, and an insert valid for the schema that has it.</summary>
    private static readonly (string Table, string Sql)[] Dataset =
    [
        (
            "voices",
            """
            INSERT INTO voices (id, display_name, language, gender, tags, active, created_at)
            VALUES ('seed-voice', 'Seed voice', 'vi', 'female', '{}', true, now())
            """),
        (
            "style_templates",
            """
            INSERT INTO style_templates (id, name, template, active, created_at)
            VALUES ('01920000-0000-7000-8000-000000000003', 'Seed template', 'Một câu chuyện về {term}.', true, now())
            """),
        (
            "users",
            """
            INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at,
                               email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled,
                               access_failed_count)
            VALUES ('01920000-0000-7000-8000-000000000001', 'Seed owner', 'vi', 'user', 'active', now(), now(),
                    true, false, false, false, 0)
            """),
        (
            "series",
            """
            INSERT INTO series (id, user_id, title, topic, language, aspect, voice_id, style_template_id,
                                created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000001', 'Seed series',
                    'ghost', 'vi', 'landscape', 'seed-voice', '01920000-0000-7000-8000-000000000003', now(), now())
            """),
        (
            "episodes",
            """
            INSERT INTO episodes (id, user_id, series_id, brief, length_tier, status, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', 'Seed episode one', 'short', 'draft', now(), now()),
                   ('01920000-0000-7000-8000-000000000005', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', 'Seed episode two', 'standard', 'draft', now(), now())
            """),
        (
            "story_versions",
            """
            INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count,
                                        content, created_at)
            VALUES ('01920000-0000-7000-8000-000000000006', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', 1, 'generated', 'Seed story', 'Seed body', '{}', 2,
                    '{}'::jsonb, now());

            -- 02.3.2-T1: only schema N-1 after 02_EpisodeApprovedAt has this column. Reuse the
            -- existing owner/series/episode/version so the next migration must preserve approval data.
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'public' AND table_name = 'episodes'
                             AND column_name = 'approved_at') THEN
                    UPDATE episodes
                    SET approved_at = TIMESTAMPTZ '2026-10-05 08:00:00+00', status = 'story_approved',
                        current_story_version_id = '01920000-0000-7000-8000-000000000006',
                        approved_story_version_id = '01920000-0000-7000-8000-000000000006'
                    WHERE id = '01920000-0000-7000-8000-000000000004'
                      AND user_id = '01920000-0000-7000-8000-000000000001'
                      AND series_id = '01920000-0000-7000-8000-000000000002';
                END IF;
            END $$;
            """),
        (
            "usage_records",
            """
            INSERT INTO usage_records (id, user_id, episode_id, series_id, provider, operation, input_tokens,
                                       cached_input_tokens, output_tokens, image_count, gpu_seconds, estimated,
                                       cost_usd, created_at)
            VALUES ('01920000-0000-7000-8000-000000000007', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000002', 'openai',
                    'story', 100, 0, 200, 0, 0, false, 0.05, now())
            """),
        (
            // 09.3.1-T1: the table the `_09_UserConsents` migration creates. The insert is skipped while that
            // migration is the last one (schema N-1 lacks the table) and runs once a later migration exists,
            // so every migration after it is exercised against a consent row.
            "user_consents",
            """
            INSERT INTO user_consents (id, user_id, document, version, accepted_at, ip)
            VALUES ('01920000-0000-7000-8000-000000000008', '01920000-0000-7000-8000-000000000001', 'tos', 'v1',
                    now(), '203.0.113.7')
            """),
        (
            // 09.3.2-T1: the table the `_09_RefreshTokens` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised against a live
            // session (a root row, still unrevoked, with the idle clock started).
            "refresh_tokens",
            """
            INSERT INTO refresh_tokens (id, user_id, family_id, token_hash, created_at, last_used_at, expires_at,
                                        reauth_attempts)
            VALUES ('01920000-0000-7000-8000-000000000009', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-00000000000a', 'seed-refresh-token-hash', now(), now(),
                    now() + interval '14 days', 0)
            """),
        (
            // 09.9.4-T1: the table the `_09_DataProtectionKeys` migration creates. Same rule as above:
            // skipped while that migration is the last one, then every later migration is exercised over a
            // key-ring row.
            "data_protection_keys",
            """
            INSERT INTO data_protection_keys (friendly_name, xml)
            VALUES ('seed-dp-key', '<key id="seed" />')
            """),
        (
            // 09.8.1-T2: the table the `_09_8_1_HubTickets` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a live,
            // unclaimed ticket row.
            "hub_tickets",
            """
            INSERT INTO hub_tickets (ticket_hash, user_id, created_at, expires_at)
            VALUES ('seed-hub-ticket-hash', '01920000-0000-7000-8000-000000000001', now(),
                    now() + interval '60 seconds')
            """),
        (
            // 09.2.6-T3: the table the `_09_EmailMessages` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a delivered
            // message (its `params` are already null, as a sent row is).
            "email_messages",
            """
            INSERT INTO email_messages (id, user_id, template, locale, to_hash, status, attempts, created_at,
                                        sent_at)
            VALUES ('01920000-0000-7000-8000-00000000000b', '01920000-0000-7000-8000-000000000001',
                    'password_changed', 'vi',
                    '9f2c1b0a7d6e5f40312a9b8c7d6e5f40312a9b8c7d6e5f40312a9b8c7d6e5f40', 'sent', 1, now(), now())
            """),
        (
            // 09.3.2-T3: the table the `_09_AuthChallenges` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a live
            // challenge row (both secrets stored hashed, as the store writes them).
            "auth_challenges",
            """
            INSERT INTO auth_challenges (id, user_id, token_hash, preauth_cookie_hash, user_agent, ip, attempts,
                                         expires_at, used_at)
            VALUES ('01920000-0000-7000-8000-00000000000c', '01920000-0000-7000-8000-000000000001',
                    'seed-mfa-token-hash', 'seed-preauth-cookie-hash', 'seed-agent', '203.0.113.9', 0,
                    now() + interval '5 minutes', NULL)
            """),
        (
            // 11.3.3-T1: the table the `_11_WorkerNodes` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a
            // registered node (an `https` row with a pin, the shape the seeder writes outside Development).
            "worker_nodes",
            """
            INSERT INTO worker_nodes (id, name, base_url, capacity, state, last_heartbeat_at, gpu_model,
                                      tls_cert_sha256, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-00000000000d', 'seed-node', 'https://node.example:8443', 2,
                    'active', now(), 'RTX 4090',
                    '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF', now(), now())
            """),
        (
            // 09.3.3-T1: the table the `_09_UserActionTokens` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over an
            // unspent reset token (only its SHA-256 hash is stored, as the store writes it).
            "user_action_tokens",
            """
            INSERT INTO user_action_tokens (id, user_id, kind, subject_id, token_hash, created_at, expires_at,
                                            used_at)
            VALUES ('01920000-0000-7000-8000-00000000000e', '01920000-0000-7000-8000-000000000001',
                    'password_reset', NULL,
                    '5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8', now(),
                    now() + interval '60 minutes', NULL)
            """),
        (
            // 02.2.2-T1: the table the `_02_GenerationRuns` migration creates. Same rule as above: skipped while
            // that migration is the last one, then every later migration is exercised over a finished story run
            // of the seed episode (queued, started and finished, so the time check and the failed/error rule
            // are exercised by real values).
            "generation_runs",
            """
            INSERT INTO generation_runs (id, user_id, series_id, episode_id, kind, "trigger", status, stage,
                                         previous_episode_status, counts_toward_limit, worst_case_usd, cost_usd,
                                         attempt, queued_at, started_at, finished_at, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-00000000000f', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000004', 'story',
                    'user', 'succeeded', 'done', 'draft', true, 0.250000, 0.120000, 0, now(), now(), now(),
                    now(), now())
            """),
        (
            // 02.2.4-T1: the table the `_02_PriceVersions` migration creates. Same rule as above: skipped while
            // that migration is the last one, then every later migration is exercised over one price version
            // (the key is the integer version, so there is no uuid seed id to allocate).
            "price_versions",
            """
            INSERT INTO price_versions (version, active_from, rates, limits, config_hash, created_at)
            VALUES (9001, now(), '{"text":{}}'::jsonb, '{"story":{}}'::jsonb, 'seed-previous-price-version', now())
            """),
        (
            // 05.3.2-T3: the table the `_05_MusicTracks` migration creates. Same rule as above: skipped while
            // that migration is the last one, then every later migration is exercised over a catalog row (a
            // `CC-BY-4.0` track, so the attribution CHECK and the gain formula see real values: -20 LUFS -> 0.200).
            "music_tracks",
            """
            INSERT INTO music_tracks (id, track_id, file_name, title, artist, source_url, license, attribution,
                                      platform_scope, moods, duration_sec, loudness_lufs, bgm_volume, sha256,
                                      storage_key, catalog_revision, active, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000010', 'seed-track', 'seed-track.mp3', 'Seed Track',
                    'Seed Artist', 'https://example.invalid/seed-track', 'CC-BY-4.0', 'Seed Track by Seed Artist',
                    'any', ARRAY['calm', 'meditation'], 120.5, -20.00, 0.200,
                    '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
                    'music/seed-track.mp3', 1, true, now(), now())
            """),
        (
            // 13.3.1-T1: the table the `_13_DataDeletionRequests` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over an open
            // account-scope request (NULL target, the row the NULLS NOT DISTINCT index guards).
            "data_deletion_requests",
            """
            INSERT INTO data_deletion_requests (id, user_id, scope, target_id, reason, status, requested_at, due_at,
                                                attempts)
            VALUES ('01920000-0000-7000-8000-000000000011', '01920000-0000-7000-8000-000000000001', 'account', NULL,
                    'account_delete', 'pending', now(), now() + interval '7 days', 0)
            """),
        (
            // 02.5.1-T1: the tables the `_02_SourcesAndFetchTasks` migration creates. Same rule as above: skipped
            // while that migration is the last one, then every later migration is exercised over a pending url
            // source of the seed episode and its queued fetch task (the live-task partial unique index sees a row).
            "story_sources",
            """
            INSERT INTO story_sources (id, user_id, episode_id, kind, url, normalized_url, registrable_domain, status,
                                       created_at)
            VALUES ('01920000-0000-7000-8000-000000000012', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', 'url', 'https://example.com/seed',
                    'https://example.com/seed', 'example.com', 'pending', now())
            """),
        (
            "fetch_tasks",
            """
            INSERT INTO fetch_tasks (id, user_id, episode_id, source_id, kind, url, registrable_domain, status,
                                     attempts, not_before, created_at)
            VALUES ('01920000-0000-7000-8000-000000000013', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000012', 'fetch_source',
                    'https://example.com/seed', 'example.com', 'queued', 0, now(), now())
            """),
        (
            // 02.5.1-T1 (CR-31): the append-only url-source creation ledger the 24 h cap counts; it has no episode or
            // source reference, so it needs only the seed user.
            "source_creations",
            """
            INSERT INTO source_creations (id, user_id, created_at)
            VALUES ('01920000-0000-7000-8000-000000000014', '01920000-0000-7000-8000-000000000001', now())
            """),
        (
            // 02.5.3-T2: the table the `_02_AddFactSheets` migration creates. Same rule as above: skipped while
            // that migration is the last one, then every later migration is exercised over a draft fact sheet of
            // the seed episode that points at the seed run (the CR-29 composite run key sees a live row). The
            // hash is the SHA-256 of the empty fact list, so `ck_fact_sheets_sha256` sees a real value.
            "fact_sheets",
            """
            INSERT INTO fact_sheets (id, user_id, episode_id, version, status, facts, source_aliases,
                                     input_truncated, max_fact_id, content_sha256, generation_run_id, prompt_version,
                                     created_by, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000015', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', 1, 0, '[]'::jsonb, '{}'::jsonb, false, 0,
                    'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
                    '01920000-0000-7000-8000-00000000000f', 'facts.v1',
                    '01920000-0000-7000-8000-000000000001', now(), now())
            """),
        (
            // 02.6.3-T1: the table the `_02_AddSeriesBible` migration creates. Same rule as above: skipped while
            // that migration is the last one, then every later migration is exercised over the bible of the seed
            // series, its row as the create path writes it — version 1 and the canonical empty `bible.v1`
            // document the backfill of that migration writes too.
            "series_bibles",
            """
            INSERT INTO series_bibles (id, user_id, series_id, version, content, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000016', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', 1,
                    jsonb_build_object('schema_version','bible.v1','narrator',
                      jsonb_build_object('name', NULL, 'persona', '', 'voice_notes', ''),
                      'recurring_characters','[]'::jsonb,'places','[]'::jsonb,'rules','[]'::jsonb,
                      'episode_summaries','[]'::jsonb),
                    now(), now())
            """),
        (
            // 09.6.1-T1: the table the `_09_RenderJobs` migration creates. Same rule as above: skipped while that
            // migration is the last one, then every later migration is exercised over the queued job of the seed
            // story version — the row the render command writes, carrying the `render-params.v1` document of that
            // version (05 D11) and the reservation id the ledger returned (NOT NULL, W2 CR-14/WT50).
            "render_jobs",
            """
            INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage,
                                     percent, params, attempt)
            VALUES ('01920000-0000-7000-8000-000000000017', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-000000000006',
                    '01920000-0000-7000-8000-000000000018', 'queued', 'queued', 0,
                    jsonb_build_object(
                      'schema', 'render-params.v1',
                      'mapper_version', 1,
                      'story_version_id', '01920000-0000-7000-8000-000000000006',
                      'aspect', 'landscape',
                      'language', 'vi',
                      'voice_id', 'seed-voice',
                      'style_template_id', '01920000-0000-7000-8000-000000000003',
                      'style_prompt_version', 'style=seed@00000000;neg=v1',
                      'image_plan', jsonb_build_object(
                        'clip_seconds', 5, 'needed', 0, 'max_terms', 0, 'planned', 0, 'trimmed', 0,
                        'hints', '[]'::jsonb, 'slot_segments', '[]'::jsonb,
                        'style_prompt_version', 'style=seed@00000000;neg=v1'),
                      'subtitles', jsonb_build_object('enabled', true, 'preset', 'landscape@1'),
                      'music', NULL::text,
                      'clip_seconds', 5),
                    1)
            """),
        (
            // 06.3.1-T1: the table the `_06_PlatformQuotaUsage` migration creates. Same rule as above: skipped while
            // the previous schema lacks it, so the row lands only on a schema that has the table.
            "platform_quota_usage",
            """
            INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at)
            VALUES ('youtube', DATE '2026-10-15', 0, 10000, now(), now())
            """),
        (
            // 02.7.2-T2: the table the `_02_AddSourceCandidates` migration creates. Same rule as above: skipped while
            // the previous schema lacks it, so the row lands only on a schema that has the table. It is an `added`
            // candidate of the seed episode, pointing at the seed run and the seed source, so both same-owner keys and
            // the `status = 'added'` ⇔ `source_id IS NOT NULL` CHECK see real values.
            "source_candidates",
            """
            INSERT INTO source_candidates (id, user_id, episode_id, generation_run_id, url, normalized_url,
                                           registrable_domain, title, site_name, rank, status, source_id, created_at,
                                           updated_at)
            VALUES ('01920000-0000-7000-8000-000000000019', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-00000000000f',
                    'https://example.com/seed', 'https://example.com/seed', 'example.com', 'Seed candidate',
                    'Example', 1, 'added', '01920000-0000-7000-8000-000000000012', now(), now())
            """),
        (
            // 02.8.1-T1: the table the `_02_AddEpisodeIdeas` migration creates. Same rule as above: skipped
            // while the previous schema lacks it, so the row lands only on a schema that has the table. It is
            // a `proposed` idea of the seed series whose run is the seed run, and its `structure` is the flat
            // document the canonical `IdeaJson` writes, so both same-owner keys and the jsonb NOT NULL column
            // see real values.
            "episode_ideas",
            """
            INSERT INTO episode_ideas (id, user_id, series_id, generation_run_id, title, logline, hook, setting,
                                       structure, status, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-00000000001a', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-00000000000f',
                    'Seed idea', 'Seed logline', 'Seed hook', 'Seed setting',
                    '{"sub_genre":"x","setting":"x","opening":"x","twist_type":"x","narrator_mode":"x","beats":["a","b","c"],"beat_signature":"a>b>c"}'::jsonb,
                    'proposed', now(), now())
            """),
        (
            // 06.1.2-T1: the table the `_06_OAuthStates` migration creates. Same rule as above: skipped while
            // the previous schema lacks it, so the row lands only on a schema that has the table. It is one
            // unused, unexpired connect attempt of the seed user — the row needs no parent beyond `users`,
            // because it is not `IUserOwned` — and the caller's purge index (`user_id, created_at DESC`) sees
            // real values for both of its columns. The next migration is exercised over this row, and the
            // callback's claim (`used_at IS NULL AND expires_at > now()`) still finds it usable.
            "oauth_states",
            """
            INSERT INTO oauth_states (id, user_id, platform, state_hash, code_verifier_protected, oauth_client_id,
                                      refresh_family_id, browser_nonce_hash, created_at, expires_at)
            VALUES ('01920000-0000-7000-8000-00000000001b', '01920000-0000-7000-8000-000000000001', 'youtube',
                    'seed-state-hash-aaaa', 'seed-verifier-protected-aaaa', 'seed-client',
                    '01920000-0000-7000-8000-00000000001c', 'seed-nonce-hash-aaaa',
                    now(), now() + interval '10 minutes')
            """),
        (
            // 06.1.2-T2: the table the `_06_ChannelConnections` migration creates. Same rule as above: skipped
            // while the previous schema lacks it, so the row lands only on a schema that has the table. It is one
            // live connection of the seed user as the connect path writes it — both tokens as
            // `ITokenProtector` ciphertext, the OAuth client that minted the code pinned, the channel identity
            // filled — so every later migration is exercised over a publishable row that is also the only member
            // of the partial unique index `ux_channel_connections_account`. It needs no parent beyond `users`:
            // the table has no foreign key at all, because the row outlives the account (13 §5.4).
            "channel_connections",
            """
            INSERT INTO channel_connections (id, user_id, platform, external_account_id, display_name, status,
                                             access_token_enc, refresh_token_enc, token_expires_at, scope,
                                             oauth_client_id, last_verified_at, connected_at)
            VALUES ('01920000-0000-7000-8000-00000000001d', '01920000-0000-7000-8000-000000000001', 'youtube',
                    'seed-channel', 'Seed channel', 'active', 'seed-access-token-enc', 'seed-refresh-token-enc',
                    now() + interval '1 hour',
                    'https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly',
                    'seed-client', now(), now())
            """),
        (
            // 06.2.1-T1: the table the `_06_Publications` migration creates. Same rule as above: skipped while
            // the previous schema lacks it, so the row lands only on a schema that has the table. It is one
            // unpublished post of the seed episode as the create command leaves it — `awaiting_consent`, the
            // metadata the owner submitted, `private` until the consent comes — and it names the seed user's
            // own episode and connection, so both the same-owner composite key to `episodes(id, user_id)` and
            // the `channel_connections` key resolve. It is the only live member of
            // `ux_publications_active_episode_platform` for that pair, and `platform` is supplied because D20
            // gives the column no store default (the model's generator fills it on insert only).
            "publications",
            """
            INSERT INTO publications (id, user_id, episode_id, channel_connection_id, status, title, description,
                                      privacy, contains_synthetic_media, created_at, updated_at, platform)
            VALUES ('01920000-0000-7000-8000-00000000001e', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000004', '01920000-0000-7000-8000-00000000001d',
                    'awaiting_consent', 'Seed publication', 'Seed publication description', 'private', true,
                    now(), now(), 'youtube')
            """),
        (
            // 06.2.3-T2: the table the `_06_PlatformIncidents` migration creates. Same rule as above: skipped while
            // the previous schema lacks it, so the row lands only on a schema that has the table. It is one open
            // incident of the seed channel's OAuth client, as an `invalid_client`/`unauthorized_client` refusal leaves
            // it — `cleared_at`, `cleared_by` and `note` are NULL, so it is the single open member of
            // `ux_platform_incidents_open` for that client and the row a later migration must not disturb.
            "platform_incidents",
            """
            INSERT INTO platform_incidents (id, platform, oauth_client_id, error_code, raised_at)
            VALUES ('01920000-0000-7000-8000-000000000020', 'youtube', 'seed-client', 'invalid_client',
                    TIMESTAMPTZ '2026-10-22 03:00:00+00')
            """),
        (
            // 02.8.3-T1: no table is created here — the row is the OLD shape of an existing table, which is what
            // `_02_AutopilotRunsStateAndSlot` backfills. The table has existed since `InitialCore`, so this insert
            // runs from the first migration after it. Its columns are conditional because the dataset outlives the
            // migration under test: schema N-1 of `_02_AutopilotRunsStateAndSlot` has only the shell's columns (the
            // case the backfill is proved against), while the N-1 of every later migration has them NOT NULL, so an
            // old-shape insert there would fail with 23502. Same shape as the `episodes.approved_at` block above.
            "autopilot_runs",
            """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'public' AND table_name = 'autopilot_runs'
                             AND column_name = 'slot_at') THEN
                    INSERT INTO autopilot_runs (id, user_id, series_id, episode_id, started_at, slot_at, start_at,
                                                state, outcome, pause_reason, attempt, render_attempts,
                                                last_transition_at, finished_at, created_at, updated_at)
                    VALUES ('01920000-0000-7000-8000-00000000001f', '01920000-0000-7000-8000-000000000001',
                            '01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000005',
                            TIMESTAMPTZ '2026-10-19 09:00:00+00', TIMESTAMPTZ '2026-10-19 09:00:00+00',
                            TIMESTAMPTZ '2026-10-19 09:00:00+00', 'gate_failed', 'gate_failed',
                            'gate_failed_streak', 0, 0, TIMESTAMPTZ '2026-10-19 10:00:00+00',
                            TIMESTAMPTZ '2026-10-19 10:00:00+00', TIMESTAMPTZ '2026-10-19 08:00:00+00',
                            TIMESTAMPTZ '2026-10-19 10:00:00+00');
                ELSE
                    INSERT INTO autopilot_runs (id, user_id, series_id, episode_id, started_at, outcome, pause_reason,
                                                created_at, updated_at)
                    VALUES ('01920000-0000-7000-8000-00000000001f', '01920000-0000-7000-8000-000000000001',
                            '01920000-0000-7000-8000-000000000002', '01920000-0000-7000-8000-000000000005',
                            TIMESTAMPTZ '2026-10-19 09:00:00+00', 'gate_failed', 'gate_failed_streak',
                            TIMESTAMPTZ '2026-10-19 08:00:00+00', TIMESTAMPTZ '2026-10-19 10:00:00+00');
                END IF;
            END $$;
            """),
        (
            // 02.8.3-T2: no table is created here either — `series_autopilot_authorizations` has existed since
            // `InitialCore`, and this row is its OLD shape, which is what `_02_AutopilotAuthorizationRevokedReason`
            // extends with `revoked_reason`. The insert names only that shape's columns, so it stays valid on both
            // sides of the migration: the added column is nullable and a row without a reason is a live
            // authorization. `channel_connection_id` is NULL on purpose — the `channel_connections` table and the
            // seed connection only exist from `_06_ChannelConnections` on, while this row is written from the first
            // schema that has the table. `version` is 1, the only one the unique `(series_id, version)` key allows
            // for the seed series, `text_hash` is a real 64-character SHA-256 digest because the column is
            // fixed-length, and `cadence` is the covered snapshot the accept path writes (README D-7).
            "series_autopilot_authorizations",
            """
            INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash,
                                                         topic, channel_connection_id, cadence, privacy_status,
                                                         accepted_at, revoked_at, created_at, updated_at)
            VALUES ('01920000-0000-7000-8000-000000000020', '01920000-0000-7000-8000-000000000001',
                    '01920000-0000-7000-8000-000000000002', 1, 'autopilot-auth.v1',
                    'fa35d3944dfc56f92e7b69f806a97f554fe8e94ee4af0d38fc1f26b47b9a4a91',
                    'ghost', NULL,
                    '{"cadence":{"days_of_week":["monday"],"publish_time":"09:00","time_zone":"Asia/Ho_Chi_Minh"},"daily_cap":1,"privacy":"private","veto_window_hours":24,"pre_audit_mode":"private","pre_audit_pending":false}',
                    'private', TIMESTAMPTZ '2026-10-20 08:00:00+00', NULL,
                    TIMESTAMPTZ '2026-10-20 08:00:00+00', TIMESTAMPTZ '2026-10-20 08:00:00+00')
            """),
    ];

    /// <summary>The tables the dataset covers, in insert order — the contract a feature migration extends.</summary>
    internal static IReadOnlyList<string> Tables => [.. Dataset.Select(row => row.Table)];

    /// <summary>
    /// The shape of the schema the dataset is written into: every dataset table it has, with the columns
    /// it has there, in ordinal order.
    /// </summary>
    /// <remarks>
    /// Captured against schema N-1, before the migration under test runs, and handed to both
    /// <see cref="SnapshotAsync"/> calls. A migration that adds a column therefore leaves the fingerprint
    /// of the inherited columns alone — which is the point: "the migration must not touch the rows" is a
    /// statement about the data that existed, not about columns the migration itself creates. A table the
    /// schema does not have yet is not captured: the insert for it belongs to the migration that creates
    /// it.
    /// </remarks>
    internal static async Task<IReadOnlyList<SeedTable>> CaptureTablesAsync(
        PostgresFixture fixture,
        string databaseName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var tables = new List<SeedTable>();

        foreach (var (table, _) in Dataset)
        {
            if (!await ExistsAsync(fixture, databaseName, table, ct))
            {
                continue;
            }

            var columns = await fixture.QueryStringsAsync(
                databaseName,
                "SELECT column_name FROM information_schema.columns "
                + $"WHERE table_schema = 'public' AND table_name = {Literal(table)} ORDER BY ordinal_position",
                ct);

            tables.Add(new SeedTable(table, columns));
        }

        return tables;
    }

    /// <summary>
    /// Inserts every dataset row whose table exists in the schema under test and returns the tables it
    /// actually wrote, in dataset order. A table the previous schema does not have yet is skipped: the
    /// insert for it belongs to the migration that creates it.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ExecuteAsync(
        PostgresFixture fixture,
        string databaseName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var seeded = new List<string>();

        foreach (var (table, sql) in Dataset)
        {
            if (!await ExistsAsync(fixture, databaseName, table, ct))
            {
                continue;
            }

            await fixture.ExecuteAsync(databaseName, sql, ct);
            seeded.Add(table);
        }

        return seeded;
    }

    /// <summary>
    /// One <c>table=count:md5</c> line per table of <paramref name="inherited"/>: the row count and a hash
    /// of the rows *as that schema projected them*. Taking this before and after the migration under test
    /// proves the migration did not disturb the data it inherited — a backfill that rewrites a row changes
    /// the hash, a delete changes the count, and a column the migration adds is not part of the
    /// fingerprint at all. The same <paramref name="inherited"/> shape must be passed to both calls: the
    /// after-side fingerprint hashes the same columns as the before-side one.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SnapshotAsync(
        PostgresFixture fixture,
        string databaseName,
        IReadOnlyList<SeedTable> inherited,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(inherited);

        var snapshot = new List<string>();

        foreach (var (table, columns) in inherited)
        {
            if (!await ExistsAsync(fixture, databaseName, table, ct))
            {
                // Only a drop reaches this, and it must fail the compare in the test rather than as an
                // SQL error: the before-side line for this table was `count:md5`, so the mismatch names
                // the table. The guard's DropTable check rejects such a migration as well.
                snapshot.Add($"{table}=dropped");
                continue;
            }

            var rows = await fixture.QueryStringsAsync(databaseName, FingerprintSql(table, columns), ct);

            snapshot.Add($"{table}={rows[0]}");
        }

        return snapshot;
    }

    /// <summary>
    /// The fingerprint query: the row count, and an md5 over the rows projected onto
    /// <paramref name="columns"/> in a stable order, so a column a later migration adds cannot enter the
    /// hash.
    /// </summary>
    private static string FingerprintSql(string table, IReadOnlyList<string> columns)
    {
        var projection = string.Join(", ", columns.Select(column => $"{Literal(column)}, t.{Identifier(column)}"));

        return "SELECT count(*)::text || ':' || coalesce(md5(string_agg(row_projection, '|' ORDER BY row_projection)), '') "
            + $"FROM (SELECT jsonb_build_object({projection})::text AS row_projection "
            + $"FROM {Identifier(table)} t) projected";
    }

    /// <summary>A SQL string literal; the values it quotes come from the catalog or from this file.</summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>A quoted SQL identifier; the values it quotes come from the catalog or from this file.</summary>
    private static string Identifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<bool> ExistsAsync(
        PostgresFixture fixture,
        string databaseName,
        string table,
        CancellationToken ct)
    {
        var registered = await fixture.QueryStringsAsync(
            databaseName,
            $"SELECT to_regclass('public.{table}')::text",
            ct);

        return registered.Count == 1 && registered[0] == table;
    }
}

/// <summary>
/// A seed table as the schema under test has it: its name, and its columns in ordinal order. The column
/// list is the projection <see cref="SeedPrevious.SnapshotAsync"/> fingerprints, captured before the
/// migration under test runs so an added column cannot change the comparison.
/// </summary>
/// <param name="Table">The table's name.</param>
/// <param name="Columns">The column names the schema has for it, in ordinal order.</param>
internal sealed record SeedTable(string Table, IReadOnlyList<string> Columns);
