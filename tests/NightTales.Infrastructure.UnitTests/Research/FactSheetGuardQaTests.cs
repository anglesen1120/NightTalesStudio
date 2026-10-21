using System.Globalization;
using ErrorOr;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// QA gap test for <c>FactSheetGuard</c> (02.5.5-T1, D6/D8/D21): a newer draft or superseded version of a true-crime
/// sheet must never displace the episode's approved version. Complements <see cref="FactSheetGuardTests"/>, which
/// covers each non-approved status only when it is the sole version.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FactSheetGuardQaTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(FactSheetStatus.Draft)]
    [InlineData(FactSheetStatus.Superseded)]
    public async Task QA_NewerNonApprovedVersion_DoesNotDisplaceTheApprovedOne(FactSheetStatus newerStatus)
    {
        var world = await SeedAsync();
        var first = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('a', 64));
        var second = await SeedFetchedSourceAsync(world, world.EpisodeId, new string('b', 64));
        var approved = await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f1", "One.", first.Id, "s1")],
            Aliases(("s1", first.Id)),
            version: 1);
        await ApprovedSheetAsync(
            world,
            world.EpisodeId,
            [CitedFact("f2", "Two.", second.Id, "s2")],
            Aliases(("s2", second.Id)),
            version: 2,
            status: newerStatus);

        var result = await EnsureAsync(world, world.EpisodeId, Topic.TrueCrime);

        result.IsError.ShouldBeFalse();
        result.Value.FactSheetId.ShouldBe(approved.Id);
        result.Value.Version.ShouldBe(1);
        result.Value.Facts.Select(fact => fact.Id).ShouldBe(["f1"], "the newer non-approved version's facts never reach the writer");
    }

    private async Task<ErrorOr<ApprovedFactSheet>> EnsureAsync(World world, Guid episodeId, Topic topic)
    {
        await using var read = Database.CreateReadDbContext(new TestCurrentUser { Id = world.UserId });

        return await new FactSheetGuard(read).EnsureApprovedAsync(episodeId, topic, Ct);
    }

    private static FactItem CitedFact(string id, string statement, Guid sourceId, string alias) =>
        new(id, statement, [sourceId], [alias], null, FactConfidence.High, FactOrigin.Model);

    private static Dictionary<string, Guid> Aliases(params (string Alias, Guid SourceId)[] pairs)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (alias, sourceId) in pairs)
        {
            map[alias] = sourceId;
        }

        return map;
    }

    private async Task<World> SeedAsync(Topic topic = Topic.TrueCrime)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, topic: topic, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        return new World(user.Id, series.Id, episode.Id);
    }

    private async Task<StorySource> SeedFetchedSourceAsync(World world, Guid episodeId, string sha256)
    {
        var id = Guid.CreateVersion7();
        var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
        var source = StorySource.CreateUrl(id, world.UserId, episodeId, url, Now).Value;
        source.MarkFetched(url.Value, "Article", "text/html", sha256, $"research/{id:N}", 4_000, Now);

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        db.StorySources.Add(source);
        await db.SaveChangesAsync(Ct);

        return source;
    }

    private async Task<FactSheet> ApprovedSheetAsync(
        World world,
        Guid episodeId,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> aliases,
        int version = 1,
        FactSheetStatus status = FactSheetStatus.Approved)
    {
        var maxFactId = facts.Count == 0
            ? 0
            : facts.Max(fact => int.Parse(fact.Id.AsSpan(1), CultureInfo.InvariantCulture));

        return await new FactSheetBuilder(Database, new TestCurrentUser { Id = world.UserId })
            .CreateVersionAsync(
                world.UserId,
                episodeId,
                version,
                facts,
                aliases,
                maxFactId: maxFactId,
                status: status,
                approvedBy: status == FactSheetStatus.Approved ? world.UserId : null,
                approvedAt: status == FactSheetStatus.Approved ? Now : null,
                ct: Ct);
    }

    private sealed record World(Guid UserId, Guid SeriesId, Guid EpisodeId);
}
