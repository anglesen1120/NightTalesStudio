using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.Research.Sources;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Research;
using NightTales.TestKit.Time;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>Shared seeding and handler construction of the source handler tests (real PostgreSQL, one scope per test).</summary>
internal static class SourceHandlerHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    internal static readonly DomainBlocklist Blocklist = new(new HashSet<string>(StringComparer.Ordinal) { "youtube.com", "youtu.be" });

    internal sealed record Scope(Guid UserId, Guid SeriesId, Guid EpisodeId);

    internal static async Task<Scope> NewEpisodeAsync(
        TestDatabase database,
        EpisodeStatus status = EpisodeStatus.Draft,
        CancellationToken ct = default)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(database, actor).CreateAsync(ct: ct);
        var series = await new SeriesBuilder(database, actor).CreateAsync(user.Id, ct: ct);
        var episode = await new EpisodeBuilder(database, actor).CreateAsync(user.Id, series.Id, status: status, ct: ct);

        return new Scope(user.Id, series.Id, episode.Id);
    }

    internal static async Task<Guid> NewEpisodeOfAsync(TestDatabase database, Scope owner, CancellationToken ct)
    {
        var episode = await new EpisodeBuilder(database, new TestCurrentUser()).CreateAsync(owner.UserId, owner.SeriesId, ct: ct);

        return episode.Id;
    }

    /// <summary>Inserts one url source (and its queued task) of <paramref name="episodeId"/> created at <paramref name="createdAt"/>.</summary>
    internal static async Task<StorySource> SeedUrlAsync(
        TestDatabase database,
        Guid userId,
        Guid episodeId,
        string url,
        DateTimeOffset createdAt,
        bool failed = false,
        CancellationToken ct = default)
    {
        var parsed = SourceUrl.Parse(url, new FakePublicSuffixList()).Value;
        var source = StorySource.CreateUrl(Guid.CreateVersion7(), userId, episodeId, parsed, createdAt).Value;
        var task = FetchTask.CreateFetchSource(Guid.CreateVersion7(), userId, episodeId, source.Id, parsed.Value, parsed.RegistrableDomain, createdAt);
        if (failed)
        {
            task.Lease("seed", createdAt, TimeSpan.FromSeconds(90));
            task.Fail("source_unreachable", createdAt);
            source.MarkFailed("source_unreachable", 503, createdAt);
        }

        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        db.StorySources.Add(source);
        db.FetchTasks.Add(task);
        db.SourceCreations.Add(SourceCreation.Record(Guid.CreateVersion7(), userId, createdAt));
        await db.SaveChangesAsync(ct);

        return source;
    }

    internal static AddSourceHandler AddHandler(AppDbContext db, Guid userId, DateTimeOffset? now = null, SourcesOptions? options = null)
        => new(db, new TestCurrentUser { Id = userId }, new FakeClock(now ?? Now), new FakePublicSuffixList(), Blocklist, options ?? new SourcesOptions());

    internal static async Task<int> CountSourcesAsync(TestDatabase database, Scope scope, CancellationToken ct)
    {
        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });

        return await db.StorySources.CountAsync(source => source.EpisodeId == scope.EpisodeId, ct);
    }

    internal static async Task<int> CountTasksAsync(TestDatabase database, Scope scope, CancellationToken ct)
    {
        await using var db = database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });

        return await db.FetchTasks.CountAsync(task => task.EpisodeId == scope.EpisodeId, ct);
    }
}
