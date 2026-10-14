using System.Globalization;
using NightTales.Domain.Research;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;

namespace NightTales.Api.IntegrationTests.StoryGeneration;

/// <summary>
/// The real guard's fixtures (02.5.5-T1): real <c>story_sources</c> and <c>fact_sheets</c> rows for the
/// <c>RealGuard...</c> suites, so the guard and the approval gate read production-shaped rows instead of a
/// scripted <see cref="NightTales.Application.Abstractions.Research.ApprovedFactSheet"/>.
/// </summary>
internal static class RealGuardFixtures
{
    /// <summary>Inserts one fetched url source of <paramref name="episodeId"/> with the given content hash (F13).</summary>
    internal static async Task<StorySource> SeedFetchedSourceAsync(
        TestDatabase database,
        Guid userId,
        Guid episodeId,
        string sha256,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var url = SourceUrl.Parse($"https://news.example.com/{id:N}", new FakePublicSuffixList()).Value;
        var source = StorySource.CreateUrl(id, userId, episodeId, url, now).Value;
        source.MarkFetched(url.Value, "Article", "text/html", sha256, $"research/{id:N}", 4_000, now);

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        db.StorySources.Add(source);
        await db.SaveChangesAsync(ct);

        return source;
    }

    /// <summary>One stored fact citing exactly one alias and its id (the shape the domain factory requires).</summary>
    internal static FactItem CitedFact(string id, string statement, Guid sourceId, string alias) =>
        new(id, statement, [sourceId], [alias], null, FactConfidence.High, FactOrigin.Model);

    /// <summary>The alias→source-id map a row stores (CR-4/D21).</summary>
    internal static IReadOnlyDictionary<string, Guid> Aliases(params (string Alias, Guid SourceId)[] pairs)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (alias, sourceId) in pairs)
        {
            map[alias] = sourceId;
        }

        return map;
    }

    /// <summary>
    /// Inserts one <c>fact_sheets</c> row through the domain factory (so every stored invariant holds) with an
    /// explicit version and lifecycle state.
    /// </summary>
    internal static async Task<FactSheet> SheetAsync(
        TestDatabase database,
        Guid userId,
        Guid episodeId,
        int version,
        IReadOnlyList<FactItem> facts,
        IReadOnlyDictionary<string, Guid> aliases,
        FactSheetStatus status,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var maxFactId = facts.Count == 0
            ? 0
            : facts.Max(fact => int.Parse(fact.Id.AsSpan(1), CultureInfo.InvariantCulture));

        return await new FactSheetBuilder(database, new TestCurrentUser { Id = userId })
            .CreateVersionAsync(
                userId,
                episodeId,
                version,
                facts,
                aliases,
                maxFactId: maxFactId,
                status: status,
                approvedBy: status == FactSheetStatus.Approved ? userId : null,
                approvedAt: status == FactSheetStatus.Approved ? now : null,
                ct: ct);
    }
}
