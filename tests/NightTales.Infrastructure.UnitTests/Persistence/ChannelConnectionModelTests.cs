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
/// revoke; <c>ix_channel_connections_user_id</c> serves the tenant-filtered <c>GET /channels</c> list. The
/// snapshot-drift check (<c>dotnet ef migrations has-pending-model-changes</c>) is a separate, later test.
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

    private static IReadOnlyEntityType Connection() => Model().FindEntityType(typeof(ChannelConnection))!;

    /// <summary>
    /// The production write model: the same provider options <c>AddPersistence</c> uses minus the probe
    /// customizer and the interceptors, which change no mapping. Nothing here connects to a database.
    /// </summary>
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ProbeContexts.UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new AppDbContext(options, new AnonymousCurrentUser());
        return context.Model;
    }
}
