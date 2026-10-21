using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Abstractions;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.UnitTests.TestKit;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Persistence;

/// <summary>
/// The 06.1.2-T2 model contract of <c>channel_connections</c> (plan §3.2, README D18), read from the EF model
/// alone: no database and no migration is needed, so this stays green while the orchestrator generates
/// <c>_06_ChannelConnections</c>.
/// </summary>
/// <remarks>
/// The two indexes are the whole of README D18. <c>ux_channel_connections_account</c> is the partial unique
/// index that makes "one live owner per Google channel" a storage fact — and the upsert's read path — and its
/// <c>WHERE status &lt;&gt; 'revoked'</c> predicate is what lets another user connect the channel after a
/// revoke; <c>ix_channel_connections_user_id</c> serves the tenant-filtered <c>GET /channels</c> list.
/// <c>Model_SnapshotHasNoPendingChanges</c> pins the snapshot-drift check
/// (<c>dotnet ef migrations has-pending-model-changes</c>) from the model alone, with no database.
/// </remarks>
public sealed class ChannelConnectionModelTests
{
    private const string AccountIndex = "ux_channel_connections_account";
    private const string UserIndex = "ix_channel_connections_user_id";

    [Fact]
    public void Model_HasPartialUniqueIndexOnPlatformAndAccountFilteredByRevoked()
    {
        var index = Connection().GetIndexes().Single(candidate => candidate.GetDatabaseName() == AccountIndex);

        index.IsUnique.ShouldBeTrue("one live connection per (platform, external_account_id) is a storage rule");
        index.Properties.Select(property => property.GetColumnName()!)
            .ShouldBe(["platform", "external_account_id"]);
        index.GetFilter().ShouldBe(
            "status <> 'revoked'",
            "a revoked row leaves the index, so its channel can be connected (by anyone) afterwards");
    }

    [Fact]
    public void Model_HasUserIndex()
    {
        var index = Connection().GetIndexes().Single(candidate => candidate.GetDatabaseName() == UserIndex);

        index.IsUnique.ShouldBeFalse();
        index.Properties.Select(property => property.GetColumnName()!).ShouldBe(["user_id"]);
        index.GetFilter().ShouldBeNull("the tenant list covers every status, revoked rows included");
    }

    /// <summary>
    /// The drift check of README D20 at the unit level: the model this context builds must be exactly the one
    /// the migrations' snapshot was generated from (EF 10 <c>HasPendingModelChanges</c> compares the two with
    /// <c>IMigrationsModelDiffer</c>), or the next <c>dotnet ef migrations add</c> would scaffold a migration
    /// for a change nobody made. No connection is opened — the differ reads the model and the snapshot.
    /// </summary>
    [Fact]
    public void Model_SnapshotHasNoPendingChanges()
    {
        using var context = new AppDbContext(Options(), new AnonymousCurrentUser());

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "an entity change without a migration must fail here (run `dotnet ef migrations add` and commit it)");
    }

    private static IReadOnlyEntityType Connection() => Model().FindEntityType(typeof(ChannelConnection))!;

    /// <summary>
    /// The production write model: the same provider options <c>AddPersistence</c> uses minus the probe
    /// customizer and the interceptors, which change no mapping. Nothing here connects to a database.
    /// </summary>
    private static IModel Model()
    {
        using var context = new AppDbContext(Options(), new AnonymousCurrentUser());
        return context.Model;
    }

    private static DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ProbeContexts.UnusedConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options;
}
