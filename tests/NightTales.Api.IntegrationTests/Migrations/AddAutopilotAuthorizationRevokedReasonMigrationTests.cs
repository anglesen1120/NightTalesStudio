using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Series.Autopilot;
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
/// The <c>_02_AutopilotAuthorizationRevokedReason</c> migration (02.8.3-T2, plan §3.7): the nullable
/// <c>revoked_reason</c> of <c>series_autopilot_authorizations</c> and the <c>CHECK</c> over its six wire values.
/// <c>Up</c> is expand-only — one <c>AddColumn</c> and one <c>AddCheckConstraint</c>, nothing else — and it rewrites
/// no row: a pre-existing authorization was never revoked, so its reason is <c>NULL</c> ("still live"), which the
/// new <c>CHECK</c> accepts because <c>NULL IN (…)</c> is never false. <c>Down</c> drops exactly what it added.
/// </summary>
/// <remarks>
/// <para>
/// The reasons are the snake_case wire values of <see cref="AutopilotAuthorizationRevokeReason"/> — the same
/// spellings <c>HasEnumCheck</c> puts in the constraint from the model — so a later handler that stores a member
/// name (<c>channel_unsupported</c>) or a free-form string fails here instead of in an audit query.
/// </para>
/// <para>
/// This class owns the last-migration check (<c>migrations[^1]</c>) and the no-pending-model-change check for the
/// tip: 02.8.3-T1's <see cref="AddAutopilotRunsStateAndSlotMigrationTests"/> handed both over and now only proves
/// its own migration is compiled in, exactly as 06.2.1-T1's <see cref="AddPublicationsMigrationTests"/> did before
/// it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Migration")]
public sealed class AddAutopilotAuthorizationRevokedReasonMigrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string MigrationName = "02_AutopilotAuthorizationRevokedReason";

    private const string Table = "series_autopilot_authorizations";

    private const string Column = "revoked_reason";

    private const string CheckName = "ck_series_autopilot_authorizations_revoked_reason";

    /// <summary>
    /// The <c>series_autopilot_authorizations</c> row <see cref="SeedPrevious"/> writes into schema N-1 (the table's
    /// shape before this migration): the live authorization of the seed series, whose inherited values this class
    /// proves untouched and whose empty reason the migration must leave empty.
    /// </summary>
    private static readonly Guid SeededAuthorization = Guid.Parse("01920000-0000-7000-8000-000000000020");

    /// <summary>
    /// The six reasons plan §3.7 allows, as the wire stores them — one per member of
    /// <see cref="AutopilotAuthorizationRevokeReason"/>, in the enum's own order.
    /// </summary>
    private static readonly string[] WireReasons =
        ["disabled", "text_changed", "topic_changed", "channel_changed", "superseded", "owner_revoked"];

    /// <summary>
    /// The constraint as <c>pg_get_constraintdef</c> renders the <c>IN</c> list of the migration: PostgreSQL stores
    /// it as <c>= ANY (ARRAY[…])</c> in the written order, which is the sorted wire list the generator emits.
    /// </summary>
    private static readonly string[] ExpectedCheckDefinition =
    [
        CheckName + "|CHECK ((revoked_reason = ANY (ARRAY["
        + "'channel_changed'::text, 'disabled'::text, 'owner_revoked'::text, 'superseded'::text, "
        + "'text_changed'::text, 'topic_changed'::text])))",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_IsTheLastOne_AndModelHasNoPendingChange()
    {
        await using var context = Database.CreateAppDbContext();
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        migrations[^1].ShouldEndWith("_" + MigrationName);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add`)");

        // The contract is read from the design-time model — the one `dotnet ef migrations` diffs against — so the
        // mapping and the migration cannot disagree without a pending change.
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(SeriesAutopilotAuthorization))!;
        entity.GetTableName().ShouldBe(Table);

        var reason = entity.FindProperty(nameof(SeriesAutopilotAuthorization.RevokedReason))!;
        reason.GetColumnName().ShouldBe(Column);
        reason.IsNullable.ShouldBeTrue("a live authorization carries no revoke reason");
        reason.GetColumnType().ShouldBe("text", "plan §3.7 stores the wire value, not an integer");
    }

    [Fact]
    public async Task Applies_WithTheNullableRevokedReasonColumnAndNoDefault()
    {
        (await ColumnSpecsAsync()).ShouldBe([Column + "|text||YES|<none>"]);
    }

    [Fact]
    public async Task Applies_WithTheCheckOverTheSixWireReasons()
    {
        (await CheckDefinitionsAsync()).ShouldBe(ExpectedCheckDefinition);
    }

    [Fact]
    public async Task Schema_AcceptsTheSixWireReasonsAndAReasonlessRow_AndRejectsAnotherReason()
    {
        var parents = await ParentsAsync();
        var authorization = InsertAuthorization(parents);

        // A live authorization is the no-reason case: the row above stores none, and the CHECK lets it through.
        (await RevokedReasonAsync(authorization)).ShouldBe(["null"], "a row inserted without a reason is still live");

        // The positive half of the closed set: every wire value the enum stores is accepted.
        foreach (var reason in WireReasons)
        {
            SqlProbe.Execute(Database, $"UPDATE {Table} SET {Column} = '{reason}' WHERE id = '{authorization}'");

            (await RevokedReasonAsync(authorization)).ShouldBe([reason]);
        }

        // The negative half: a plausible-looking spelling that is not a wire value is refused by PostgreSQL, so no
        // handler can store a reason the audit copy has no key for.
        var refused = Should.Throw<PostgresException>(() => SqlProbe.Execute(
            Database,
            $"UPDATE {Table} SET {Column} = 'channel_unsupported' WHERE id = '{authorization}'"));
        refused.SqlState.ShouldBe("23514");
        refused.ConstraintName.ShouldBe(CheckName);

        // The refused statement wrote nothing: the row keeps the last accepted value.
        (await RevokedReasonAsync(authorization)).ShouldBe([WireReasons[^1]]);
    }

    /// <summary>
    /// The schema one release behind with the seed harness's rows (<see cref="SeedPrevious"/>), including one live
    /// <c>series_autopilot_authorizations</c> row in the table's old shape: every inherited column comes out
    /// untouched — the migration only adds the column — and the new column is <c>NULL</c> for that row, because it
    /// predates the reason and was never revoked.
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
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            var inherited = await SeedPrevious.CaptureTablesAsync(fixture, name, Ct);
            var seeded = await SeedPrevious.ExecuteAsync(fixture, name, Ct);
            seeded.ShouldContain(
                Table, "the migration must be exercised against an old-shape authorization, not an empty schema");

            (await ColumnSpecsAsync()).ShouldBeEmpty("schema N-1 has no revoked_reason");

            var rowBefore = await Database.QueryStringsAsync(
                $"SELECT to_jsonb(a)::text FROM {Table} a WHERE id = '{SeededAuthorization}'", Ct);
            rowBefore.Count.ShouldBe(1, "the seed dataset writes exactly one authorization row");

            var before = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));

            await migrator.MigrateAsync(migrations[own], Ct);

            // The inherited columns are untouched: the fingerprint hashes exactly the projection schema N-1 had, and
            // the row itself minus the new column is byte for byte the row that was seeded.
            var after = Snapshot(await SeedPrevious.SnapshotAsync(fixture, name, inherited, Ct));
            foreach (var table in inherited.Select(shape => shape.Table))
            {
                after[table].ShouldBe(before[table], $"the migration must not touch the rows of {table}");
            }

            (await Database.QueryStringsAsync(
                    $"SELECT (to_jsonb(a) - '{Column}')::text FROM {Table} a WHERE id = '{SeededAuthorization}'",
                    Ct))
                .ShouldBe(rowBefore, "the migration adds the column and rewrites nothing it inherited");

            (await RevokedReasonAsync(SeededAuthorization)).ShouldBe(
                ["null"], "the authorization predates the reason column and was never revoked");
        }
        finally
        {
            // The class database is always left at the latest migration.
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }
    }

    [Fact]
    public async Task Down_DropsTheColumnAndItsCheckOnly_AndReApplies()
    {
        var parents = await ParentsAsync();
        var authorization = InsertAuthorization(parents);

        await using var context = Database.CreateAppDbContext();
        var (migrator, migrations, own) = MigrationOf(context);
        var columnsBefore = await ColumnsAsync();

        try
        {
            await migrator.MigrateAsync(migrations[own - 1], Ct);

            (await ColumnsAsync()).ShouldBe(
                columnsBefore.Where(column => column != Column).ToList(), "Down drops the column this migration added");
            (await ColumnSpecsAsync()).ShouldBeEmpty();
            (await CheckDefinitionsAsync()).ShouldBeEmpty("the check goes with the column it constrains");

            // The row itself survives the Down: only its reason column goes.
            SqlProbe.Count(Database, $"SELECT count(*) FROM {Table} WHERE id = '{authorization}'").ShouldBe(1);
        }
        finally
        {
            await migrator.MigrateAsync(migrations[^1], CancellationToken.None);
        }

        // Re-applied (NFR-09-6 expand-only), the column and its check are back at the same shape; the row that
        // outlived the Down is live again, because no reason was carried over.
        (await ColumnsAsync()).ShouldBe(columnsBefore);
        (await CheckDefinitionsAsync()).ShouldBe(ExpectedCheckDefinition);
        (await RevokedReasonAsync(authorization)).ShouldBe(["null"]);
    }

    /// <summary>The column as <c>name|type|length|nullable|default</c>, one row or none.</summary>
    private Task<IReadOnlyList<string>> ColumnSpecsAsync() => Database.QueryStringsAsync(
        "SELECT column_name || '|' || data_type || '|' || coalesce(character_maximum_length::text, '') "
        + "|| '|' || is_nullable || '|' || coalesce(column_default, '<none>') "
        + "FROM information_schema.columns WHERE table_schema = 'public' "
        + $"AND table_name = '{Table}' AND column_name = '{Column}'",
        Ct);

    /// <summary>The table's columns, in name order.</summary>
    private Task<IReadOnlyList<string>> ColumnsAsync() => Database.QueryStringsAsync(
        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' "
        + $"AND table_name = '{Table}' ORDER BY column_name",
        Ct);

    /// <summary>The reason check as <c>name|definition</c>, one row or none.</summary>
    private Task<IReadOnlyList<string>> CheckDefinitionsAsync() => Database.QueryStringsAsync(
        "SELECT conname || '|' || pg_get_constraintdef(oid) FROM pg_constraint "
        + $"WHERE conrelid = '{Table}'::regclass AND contype = 'c' AND conname = '{CheckName}'",
        Ct);

    /// <summary>The stored reason of one row, <c>null</c> spelled out so a missing value can be asserted.</summary>
    private Task<IReadOnlyList<string>> RevokedReasonAsync(Guid authorization) => Database.QueryStringsAsync(
        $"SELECT coalesce({Column}, 'null') FROM {Table} WHERE id = '{authorization}'",
        Ct);

    /// <summary>One user with a series: the parents an authorization row needs, written through the TestKit builders.</summary>
    private async Task<Parents> ParentsAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);

        return new Parents(user.Id, series.Id);
    }

    /// <summary>
    /// The row the accept path writes, without the column under test: schema N-1 of this migration has no
    /// <c>revoked_reason</c>, so the insert must not name it — that omission is the live (unrevoked) case, and the
    /// same statement stays valid on the migrated schema, where the column is nullable.
    /// </summary>
    private Guid InsertAuthorization(Parents parents)
    {
        var id = Guid.CreateVersion7();
        SqlProbe.Execute(
            Database,
            $"INSERT INTO {Table} (id, user_id, series_id, version, text_version, text_hash, topic, cadence, "
            + "privacy_status, accepted_at, created_at, updated_at) VALUES ("
            + $"'{id}', '{parents.UserId}', '{parents.SeriesId}', 1, 'autopilot-auth.v1', repeat('b', 64), "
            + "'ghost', '{\"daily_cap\":1}', 'private', now(), now(), now())");

        return id;
    }

    private static (IMigrator Migrator, List<string> Migrations, int Own) MigrationOf(AppDbContext context)
    {
        var migrations = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
        var own = migrations.FindIndex(name => name.EndsWith("_" + MigrationName, StringComparison.Ordinal));
        own.ShouldBeGreaterThan(0, "the migration must exist and have a previous one to revert to");

        return (context.Database.GetService<IMigrator>(), migrations, own);
    }

    /// <summary>Turns <c>table=count:md5</c> snapshot lines into a table → fingerprint lookup.</summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyList<string> lines) => lines.ToDictionary(
        line => line[..line.IndexOf('=', StringComparison.Ordinal)],
        line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
        StringComparer.Ordinal);

    private sealed record Parents(Guid UserId, Guid SeriesId);
}
