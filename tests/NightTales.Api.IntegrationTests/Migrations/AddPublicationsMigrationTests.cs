using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The <c>_06_Publications</c> migration (06.2.1-T1, 06 README D6/D18/D20): the <c>publications</c> table, one
/// row per platform post of one episode — the metadata YouTube is given, the consent that allowed it and where
/// the upload stands. <c>Up</c> is expand-only — one <c>CreateTable</c>, the four D18 indexes, the two indexes
/// EF adds for the foreign keys, the four <c>CHECK</c>s and the primary key, no other schema change — and
/// <c>Down</c> drops the table and nothing else. 02.8.3-T1 appended <c>_02_AutopilotRunsStateAndSlot</c> after it, so
/// the last-migration check (<c>migrations[^1]</c>) now belongs to
/// <c>AddAutopilotRunsStateAndSlotMigrationTests</c> and this class only proves its own migration is compiled in.
/// </summary>
/// <remarks>
/// The table-wide expectations are read from the live catalog (<c>information_schema</c>, <c>pg_indexes</c>,
/// <c>pg_constraint</c>, <c>pg_attribute</c>) rather than from the model, because the model is what the
/// migration is supposed to have moved: the shape, the defaults, the partial predicates and the four
/// <c>ON DELETE</c> actions are the 06 README contract, and the row-version source (<c>xmin</c>) is a system
/// column no EF property can prove on its own.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddPublicationsMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "06_Publications";

    private const string Table = "publications";

    /// <summary>
    /// The 27 columns of the table, in name order — the shape D20 fixes. <c>xmin</c> is deliberately absent:
    /// it is the row version's system column (<see cref="AggregateRoot.Version"/>), so
    /// <c>information_schema.columns</c> never lists it; the test below reaches it through <c>pg_attribute</c>.
    /// </summary>
    private static readonly string[] ExpectedColumns =
    [
        "autopilot_authorization_id", "channel_connection_id", "consent_at", "consent_source",
        "consent_text_version", "contains_synthetic_media", "created_at", "description", "episode_id",
        "error_code", "external_post_id", "external_url", "id", "next_attempt_at", "platform",
        "platform_payload", "privacy", "publish_at", "published_at", "self_declared_made_for_kids", "status",
        "tags", "title", "updated_at", "user_id", "veto_ends_at", "veto_notice_sent_at",
    ];

    /// <summary>
    /// The columns in <c>name|type|length|nullable|default</c> form: exactly the task's SQL. Two defaults are
    /// schema statements and not code paths — <c>tags</c> keeps a bare insert at the empty array and
    /// <c>self_declared_made_for_kids</c> is <c>false</c> for every row (D12). <c>platform</c> is
    /// <c>NOT NULL</c> with <b>no</b> store default: the aggregate computes it, and the model fills it through
    /// <c>PublicationPlatformGenerator</c>, so a bare raw insert has to supply it (proved below).
    /// </summary>
    private static readonly string[] ExpectedColumnSpecs =
    [
        "autopilot_authorization_id|uuid||YES|<none>",
        "channel_connection_id|uuid||NO|<none>",
        "consent_at|timestamp with time zone||YES|<none>",
        "consent_source|text||YES|<none>",
        "consent_text_version|text||YES|<none>",
        "contains_synthetic_media|boolean||NO|<none>",
        "created_at|timestamp with time zone||NO|<none>",
        "description|text||NO|<none>",
        "episode_id|uuid||NO|<none>",
        "error_code|text||YES|<none>",
        "external_post_id|text||YES|<none>",
        "external_url|text||YES|<none>",
        "id|uuid||NO|<none>",
        "next_attempt_at|timestamp with time zone||YES|<none>",
        "platform|text||NO|<none>",
        "platform_payload|jsonb||YES|<none>",
        "privacy|text||NO|<none>",
        "publish_at|timestamp with time zone||YES|<none>",
        "published_at|timestamp with time zone||YES|<none>",
        "self_declared_made_for_kids|boolean||NO|false",
        "status|text||NO|<none>",
        "tags|ARRAY||NO|'{}'::text[]",
        "title|text||NO|<none>",
        "updated_at|timestamp with time zone||NO|<none>",
        "user_id|uuid||NO|<none>",
        "veto_ends_at|timestamp with time zone||YES|<none>",
        "veto_notice_sent_at|timestamp with time zone||YES|<none>",
    ];

    /// <summary>
    /// Every index of the table: the four of README D18, the two EF adds for the foreign key columns
    /// (<c>autopilot_authorization_id</c> and the composite <c>episode_id, user_id</c>) and the primary key.
    /// No separate <c>ix_publications_user_id</c> exists, because the owner index's leading column covers that
    /// key.
    /// </summary>
    private static readonly string[] ExpectedIndexes =
    [
        "ix_publications_autopilot_authorization_id",
        "ix_publications_channel_connection_id",
        "ix_publications_episode_id_user_id",
        "ix_publications_status_next_attempt_at",
        "ix_publications_user_id_created_at",
        "pk_publications",
        "ux_publications_active_episode_platform",
    ];

    /// <summary>
    /// The same set as <c>pg_indexes.indexdef</c> renders it, verbatim: a predicate is compared as
    /// PostgreSQL normalised it (<c>IN</c> becomes <c>= ANY (ARRAY[…])</c>, <c>NOT IN</c> becomes
    /// <c>&lt;&gt; ALL (ARRAY[…])</c>), and the <c>DESC</c> marker of the owner index is part of the contract.
    /// </summary>
    private static readonly string[] ExpectedIndexDefinitions =
    [
        "ix_publications_autopilot_authorization_id|CREATE INDEX ix_publications_autopilot_authorization_id "
        + "ON public.publications USING btree (autopilot_authorization_id)",
        "ix_publications_channel_connection_id|CREATE INDEX ix_publications_channel_connection_id "
        + "ON public.publications USING btree (channel_connection_id)",
        "ix_publications_episode_id_user_id|CREATE INDEX ix_publications_episode_id_user_id "
        + "ON public.publications USING btree (episode_id, user_id)",
        "ix_publications_status_next_attempt_at|CREATE INDEX ix_publications_status_next_attempt_at "
        + "ON public.publications USING btree (status, next_attempt_at) "
        + "WHERE (status = ANY (ARRAY['awaiting_consent'::text, 'scheduled'::text, 'uploading'::text, "
        + "'processing'::text]))",
        "ix_publications_user_id_created_at|CREATE INDEX ix_publications_user_id_created_at "
        + "ON public.publications USING btree (user_id, created_at DESC)",
        "pk_publications|CREATE UNIQUE INDEX pk_publications ON public.publications USING btree (id)",
        "ux_publications_active_episode_platform|CREATE UNIQUE INDEX ux_publications_active_episode_platform "
        + "ON public.publications USING btree (episode_id, platform) "
        + "WHERE (status <> ALL (ARRAY['failed'::text, 'cancelled'::text]))",
    ];

    /// <summary>
    /// The four <c>CHECK</c>s as <c>pg_get_constraintdef</c> renders them: the three the configuration
    /// hand-writes and the status set <c>HasEnumCheck</c> emits from <see cref="PublicationStatus"/>.
    /// </summary>
    private static readonly string[] ExpectedCheckDefinitions =
    [
        "ck_publications_consent_source|CHECK ((consent_source = ANY (ARRAY['user'::text, 'autopilot'::text])))",
        "ck_publications_platform|CHECK ((platform = ANY (ARRAY['youtube'::text, 'tiktok'::text])))",
        "ck_publications_privacy|CHECK ((privacy = ANY (ARRAY['public'::text, 'unlisted'::text, "
        + "'private'::text])))",
        "ck_publications_status|CHECK ((status = ANY (ARRAY['awaiting_consent'::text, 'cancelled'::text, "
        + "'failed'::text, 'processing'::text, 'published'::text, 'scheduled'::text, 'uploading'::text])))",
    ];

    /// <summary>
    /// The four foreign keys as <c>name|definition|confdeltype</c>: the account cascades (D20), the episode
    /// cascades through the same-owner composite key <c>(id, user_id)</c> every episode child uses
    /// (NTS-922-SEC-001), the connection refuses the delete because D15 cancels its publications first, and a
    /// removed autopilot authorization only drops the reference.
    /// </summary>
    private static readonly string[] ExpectedForeignKeys =
    [
        "fk_publications_asp_net_users_user_id|FOREIGN KEY (user_id) REFERENCES users(id) "
        + "ON DELETE CASCADE|c",
        "fk_publications_channel_connections_channel_connection_id|FOREIGN KEY (channel_connection_id) "
        + "REFERENCES channel_connections(id) ON DELETE RESTRICT|r",
        "fk_publications_episodes_episode_id_user_id|FOREIGN KEY (episode_id, user_id) "
        + "REFERENCES episodes(id, user_id) ON DELETE CASCADE|c",
        "fk_publications_series_autopilot_authorizations_autopilot_auth|FOREIGN KEY "
        + "(autopilot_authorization_id) REFERENCES series_autopilot_authorizations(id) ON DELETE SET NULL|n",
    ];

    /// <summary>The statuses the partial unique index keeps out of the key: a terminal row frees its episode.</summary>
    private static readonly string[] TerminalStatuses = ["cancelled", "failed"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsApplied_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        // 02.8.3-T1 appended a migration after this one, so this class no longer owns the last-migration
        // check (the newest migration's own test does); it still proves its own migration is compiled in.
        migrations.ShouldContain(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");

        var entity = context.Model.FindEntityType(typeof(Publication))!;
        entity.GetTableName().ShouldBe(Table);

        // The row version is what `ETags.Publication` publishes as `"pu-<xmin>"`, so the mapping to the system
        // column is part of the contract; the catalog side of the same fact is asserted below.
        var version = entity.FindProperty(nameof(AggregateRoot.Version))!;
        version.GetColumnName().ShouldBe("xmin");
        version.IsConcurrencyToken.ShouldBeTrue();

        // `platform` is a shadow property (the aggregate computes it), NOT NULL, and has no store default: the
        // generator below fills it on insert, so D20's column and the partial unique index can rely on it.
        var platform = entity.FindProperty("platform")!;
        platform.IsShadowProperty().ShouldBeTrue();
        platform.IsNullable.ShouldBeFalse();
        platform.GetDefaultValue().ShouldBeNull("the value comes from the model's generator, not from the store");
    }

    [Fact]
    public void Applies_WithTheColumnsNullabilityAndDefaults()
    {
        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
                + $"AND table_name = '{Table}' ORDER BY column_name")
            .ShouldBe(ExpectedColumns);

        SqlProbe.QueryStrings(
                Database,
                "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
                + "|| '|' || is_nullable || '|' || coalesce(column_default, '<none>') "
                + "FROM information_schema.columns WHERE table_schema = 'public' "
                + $"AND table_name = '{Table}' ORDER BY column_name")
            .ShouldBe(ExpectedColumnSpecs);
    }

    [Fact]
    public void Applies_WithTheXminRowVersionColumn()
    {
        // `xmin` is a system column, so `information_schema.columns` never shows it and the shape above cannot
        // pin it: the row version — and therefore the ETag every consent/cancel/PATCH echoes — is read here.
        SqlProbe.QueryStrings(
                Database,
                "SELECT attname || '|' || atttypid::regtype::text || '|' || attnotnull::text FROM pg_attribute "
                + $"WHERE attrelid = '{Table}'::regclass AND attname = 'xmin'")
            .ShouldBe(["xmin|xid|true"]);
    }

    [Fact]
    public async Task Applies_WithTheDefaultsOfABareInsert_AndRefusesAMissingPlatform()
    {
        var parents = await ParentsAsync();
        var id = Guid.CreateVersion7();

        // The two store defaults: a row that writes neither `tags` nor `self_declared_made_for_kids` — nor a
        // consent — comes out with the empty array, `false` and an unconsented NULL.
        SqlProbe.Execute(Database, InsertSql(parents, id, "awaiting_consent"));
        SqlProbe.QueryStrings(
                Database,
                $"SELECT tags::text || '|' || self_declared_made_for_kids::text || '|' "
                + $"|| coalesce(consent_source, 'null') FROM {Table} WHERE id = '{id}'")
            .ShouldBe(["{}|false|null"]);

        // `platform` has no store default, so an insert that omits it is refused by the NOT NULL constraint —
        // the column cannot silently take the model's generator value outside the model.
        var refused = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database,
            $"INSERT INTO {Table} (id, user_id, episode_id, channel_connection_id, status, title, description, "
            + "privacy, contains_synthetic_media, created_at, updated_at) VALUES ("
            + $"'{Guid.CreateVersion7()}', '{parents.UserId}', '{parents.EpisodeId}', "
            + $"'{parents.ChannelConnectionId}', 'awaiting_consent', 'Seed title', 'Seed description', "
            + "'private', true, now(), now())"));
        refused.SqlState.ShouldBe("23502");
        refused.ColumnName.ShouldBe("platform");
    }

    [Fact]
    public void Applies_WithTheIndexSetAndItsExactDefinitions()
    {
        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}' "
                + "ORDER BY indexname")
            .ShouldBe(ExpectedIndexes);

        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname || '|' || indexdef FROM pg_indexes WHERE schemaname = 'public' "
                + $"AND tablename = '{Table}' ORDER BY indexname")
            .ShouldBe(ExpectedIndexDefinitions);
    }

    [Fact]
    public async Task Schema_RejectsAnUnknownStatusPlatformPrivacyAndConsentSource()
    {
        var parents = await ParentsAsync();

        // `status` is the domain's closed set, not free text: the CHECK is the only thing between a typo in a
        // later handler and a row no poller can ever claim.
        var badStatus = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), "pending")));
        badStatus.SqlState.ShouldBe("23514");
        badStatus.ConstraintName.ShouldBe("ck_publications_status");

        var badPlatform = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), "awaiting_consent", platform: "vimeo")));
        badPlatform.SqlState.ShouldBe("23514");
        badPlatform.ConstraintName.ShouldBe("ck_publications_platform");

        var badPrivacy = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), "awaiting_consent", privacy: "friends")));
        badPrivacy.SqlState.ShouldBe("23514");
        badPrivacy.ConstraintName.ShouldBe("ck_publications_privacy");

        var badConsent = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), "awaiting_consent", consentSource: "robot")));
        badConsent.SqlState.ShouldBe("23514");
        badConsent.ConstraintName.ShouldBe("ck_publications_consent_source");

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{parents.UserId}'")
            .ShouldBe(0, "a refused row is not written at all — the CHECKs are not default-filling ones");

        // Every value the four sets allow inserts: the positive half of the same contract.
        SqlProbe.Execute(Database, InsertSql(parents, Guid.CreateVersion7(), "published", platform: "tiktok"));
        SqlProbe.Execute(
            Database,
            InsertSql(parents, Guid.CreateVersion7(), "scheduled", privacy: "unlisted", consentSource: "user"));
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{parents.UserId}'").ShouldBe(2);
    }

    [Fact]
    public void Applies_WithTheFourForeignKeysAndTheirDeleteActions()
    {
        // The account's CASCADE is asserted by its definition only: `episodes.user_id` and `series.user_id`
        // refuse the delete (RESTRICT), so no test can reach `users` through a publication row; the episode and
        // the connection are exercised live below.
        SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(oid) || '|' || confdeltype::text "
                + $"FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'f' ORDER BY conname")
            .ShouldBe(ExpectedForeignKeys);

        SqlProbe.QueryStrings(
                Database,
                "SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint "
                + $"WHERE conrelid = '{Table}'::regclass AND contype = 'p'")
            .ShouldBe([$"pk_{Table}|PRIMARY KEY (id)"]);
    }

    [Fact]
    public async Task ActiveEpisodePlatformIndex_RefusesTwoLiveRows_AndAllowsTheNextOneAfterATerminalStatus()
    {
        var parents = await ParentsAsync();
        var live = InsertRow(parents, "awaiting_consent");

        // "One live publication per (episode, platform)" is the storage rule (D6) and the arbiter the create
        // command translates to 409 `publication_invalid_state`, not a handler convention.
        var refused = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), "scheduled")));
        refused.SqlState.ShouldBe("23505");
        refused.ConstraintName.ShouldBe("ux_publications_active_episode_platform");

        // A terminal row leaves the partial index, so the episode can be published again: `cancelled` first,
        // then `failed`, both leaving the historical rows behind.
        foreach (var terminal in TerminalStatuses)
        {
            SqlProbe.Execute(Database, $"UPDATE {Table} SET status = '{terminal}' WHERE id = '{live}'");
            live = InsertRow(parents, "scheduled");
        }

        // `processing` is still a live status, so the last row holds the key.
        var stillLive = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, InsertSql(parents, Guid.CreateVersion7(), "processing")));
        stillLive.SqlState.ShouldBe("23505");
        stillLive.ConstraintName.ShouldBe("ux_publications_active_episode_platform");

        // The key is per (episode, platform): a TikTok row of the same episode is a different entry, and 07
        // relies on that.
        SqlProbe.Execute(Database, InsertSql(parents, Guid.CreateVersion7(), "scheduled", platform: "tiktok"));

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE episode_id = '{parents.EpisodeId}'")
            .ShouldBe(4, "two terminal rows, one live YouTube row and the live TikTok row");
    }

    [Fact]
    public async Task DeletingTheConnection_IsRefused_WhileAPublicationRowReferencesIt()
    {
        var parents = await ParentsAsync();
        InsertRow(parents, "published");

        // D15 cancels a connection's publications before the row may go, so the reference is RESTRICT: the
        // delete of a connection that still has posts fails loudly instead of taking them with it.
        var refused = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database, $"DELETE FROM channel_connections WHERE id = '{parents.ChannelConnectionId}'"));
        refused.SqlState.ShouldBe("23503");
        refused.ConstraintName.ShouldBe("fk_publications_channel_connections_channel_connection_id");

        // A refused row is not written and the connection survives it.
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE user_id = '{parents.UserId}'").ShouldBe(1);
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM channel_connections WHERE id = '{parents.ChannelConnectionId}'")
            .ShouldBe(1);

        // Even the historical rows block it, because the key is not partial.
        SqlProbe.Execute(Database, $"DELETE FROM {Table} WHERE user_id = '{parents.UserId}'");
        SqlProbe.Execute(Database, $"DELETE FROM channel_connections WHERE id = '{parents.ChannelConnectionId}'");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM channel_connections WHERE id = '{parents.ChannelConnectionId}'")
            .ShouldBe(0, "with no publication left the connection is deletable");
    }

    [Fact]
    public async Task DeletingTheEpisode_CascadesThePublicationRow()
    {
        var parents = await ParentsAsync();
        InsertRow(parents, "scheduled");

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE episode_id = '{parents.EpisodeId}'")
            .ShouldBe(1);

        SqlProbe.Execute(Database, $"DELETE FROM episodes WHERE id = '{parents.EpisodeId}'");

        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE episode_id = '{parents.EpisodeId}'")
            .ShouldBe(0, "the episode's hard delete takes its posts with it through the same-owner composite key");
    }

    [Fact]
    public async Task DeletingTheAuthorization_NullsTheReferenceAndKeepsThePublication()
    {
        var parents = await ParentsAsync();
        var authorization = InsertAuthorization(parents);
        var id = InsertRow(parents, "awaiting_consent", autopilotAuthorizationId: authorization);

        SqlProbe.QueryStrings(
                Database,
                $"SELECT coalesce(autopilot_authorization_id::text, 'null') FROM {Table} WHERE id = '{id}'")
            .ShouldBe([authorization.ToString()]);

        SqlProbe.Execute(
            Database, $"DELETE FROM series_autopilot_authorizations WHERE id = '{authorization}'");

        SqlProbe.QueryStrings(
                Database,
                $"SELECT coalesce(autopilot_authorization_id::text, 'null') FROM {Table} WHERE id = '{id}'")
            .ShouldBe(["null"], "a removed authorization only drops the reference");
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE id = '{id}'")
            .ShouldBe(1, "the publication itself stays");
    }

    [Fact]
    public async Task Migration_HasOnlyCreateOperations()
    {
        await using var context = Database.CreateAppDbContext();

        MigrationGuard.FindViolations([CompiledMigration(context, MigrationName)]).ShouldBeEmpty();
    }

    /// <summary>
    /// The schema one release behind with the rows the seed harness writes into it (<see cref="SeedPrevious"/>):
    /// the inherited rows come out untouched and <c>publications</c> joins them empty. The seed skips a table
    /// the previous schema does not have, so this run writes no publication row of its own — that row lands in
    /// the next migration's run.
    /// </summary>
    [Fact]
    public async Task Applies_OnThePreviousSchemaWithSeededData()
    {
        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);
        var fixture = Database.Fixture;
        var name = Database.Name;

        try
        {
            // One release behind is 09.7.1-T1's `_09_EpisodeVideoReview`; the seed has data of its own there
            // (`channel_connections`, `oauth_states`, …), so the comparison is over a populated schema.
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
                .ShouldBe("gone");

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldNotContain(Table, "the previous schema has no table to seed a publication into");
            seeded.ShouldContain(
                "channel_connections", "the migration must be exercised against data, not an empty schema");
            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            SqlProbe.Count(Database, $"SELECT count(*) FROM {Table}").ShouldBe(0);

            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsTheTableAndItsIndexesOnly_AndReApplies()
    {
        // A row of the schema the test starts from, so the Down is exercised over data and the tables it must
        // leave alone are non-empty.
        var parents = await ParentsAsync();
        InsertRow(parents, "published");

        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{Table}')::text, 'gone')")
                .ShouldBe("gone", "Down drops the table");
            SqlProbe.Count(
                    Database,
                    $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}'")
                .ShouldBe(0, "the drop takes the table's indexes with it");

            // Expand-only: the migration reverts nothing but its own table — neither the sibling tables of the
            // wave nor the parents the dropped rows pointed at.
            foreach (var sibling in new[] { "channel_connections", "oauth_states", "episodes", "series" })
            {
                SqlProbe.ScalarText(Database, $"SELECT coalesce(to_regclass('public.{sibling}')::text, 'gone')")
                    .ShouldNotBe("gone", "the Down of this migration must not touch another table");
            }

            SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id = '{parents.EpisodeId}'")
                .ShouldBe(1, "the episode survives the Down of the table that pointed at it");
            SqlProbe.Count(
                    Database,
                    $"SELECT count(*) FROM channel_connections WHERE id = '{parents.ChannelConnectionId}'")
                .ShouldBe(1, "the connection survives the Down of the table that pointed at it");
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        // Re-applied (NFR-09-6 expand-only), the table and its index set are back; only its own data is lost.
        SqlProbe.Count(Database, $"SELECT count(*) FROM {Table}").ShouldBe(0, "the recreated table is empty");
        SqlProbe.QueryStrings(
                Database,
                $"SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{Table}'")
            .ShouldBe(ExpectedIndexes, ignoreOrder: true);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    /// <summary>
    /// One user with a series, an episode and one live connection — the three parents a publication row needs,
    /// written through the TestKit builders (the channel row through the aggregate's own factory, so its
    /// columns are production shaped).
    /// </summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);
        var connection = await new ChannelConnectionBuilder(Database, actor).CreateActiveAsync(user.Id, ct: Ct);

        return new Parents(user.Id, series.Id, episode.Id, connection.Id);
    }

    /// <summary>
    /// The one-row insert every behaviour case uses: each column the table requires, and only the status, the
    /// platform, the privacy and the two consent columns under the caller's control, so nothing else can make
    /// the insert fail first. <c>tags</c> and <c>self_declared_made_for_kids</c> are left to their defaults.
    /// </summary>
    private static string InsertSql(
        Parents parents,
        Guid id,
        string status,
        string platform = "youtube",
        string privacy = "private",
        string? consentSource = null,
        Guid? autopilotAuthorizationId = null)
    {
        var consent = consentSource is null ? "NULL" : $"'{consentSource}'";
        var authorization = autopilotAuthorizationId is null
            ? "NULL"
            : $"'{autopilotAuthorizationId.Value}'";

        return $"INSERT INTO {Table} (id, user_id, episode_id, channel_connection_id, status, title, description, "
            + "privacy, contains_synthetic_media, created_at, updated_at, platform, consent_source, "
            + "autopilot_authorization_id) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.EpisodeId}', '{parents.ChannelConnectionId}', '{status}', "
            + $"'Seed title', 'Seed description', '{privacy}', true, now(), now(), '{platform}', {consent}, "
            + $"{authorization})";
    }

    private Guid InsertRow(
        Parents parents,
        string status,
        string? consentSource = null,
        Guid? autopilotAuthorizationId = null)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            InsertSql(parents, id, status, consentSource: consentSource,
                autopilotAuthorizationId: autopilotAuthorizationId));

        return id;
    }

    /// <summary>
    /// One autopilot authorization of the same owner and series, with the columns plan §3 marks NOT NULL — the
    /// parent the SET NULL key points at.
    /// </summary>
    private Guid InsertAuthorization(Parents parents)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, cadence, privacy_status, accepted_at, created_at, updated_at) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.SeriesId}', 1, 'pa.v1', repeat('c', 64), 'ghost', 'weekly', "
            + "'private', now(), now(), now())");

        return id;
    }

    private static (IMigrator Migrator, List<string> Migrations, int Own) MigrationOf(AppDbContext context)
    {
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        return (context.Database.GetService<IMigrator>(), migrations, own);
    }

    private static Migration CompiledMigration(AppDbContext context, string idFragment)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations
            .Single(pair => pair.Key.Contains(idFragment, StringComparison.Ordinal))
            .Value;

        return (Migration)Activator.CreateInstance(type, nonPublic: true)!;
    }

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint lookup.</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);

    private sealed record Parents(Guid UserId, Guid SeriesId, Guid EpisodeId, Guid ChannelConnectionId);
}
