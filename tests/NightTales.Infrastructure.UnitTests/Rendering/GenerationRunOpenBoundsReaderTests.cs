using System.Globalization;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Pricing;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Rendering;

/// <summary>
/// <see cref="GenerationRunOpenBoundsReader"/> (02.2.2-T2, extended by 09.6.3-T1) over real <c>render_jobs</c> and
/// <c>generation_runs</c> rows: the three open render states are summed beside the two open run states, the
/// <c>ingesting</c> and the terminal rows are not, <c>excludingRunId</c> skips the caller's own row in either table,
/// and the total spans tenants because the cap it serves is platform-wide.
/// </summary>
/// <remarks>
/// Every test asserts the <em>delta</em> against the total it reads first: the reader sums the whole platform and
/// the class database is shared, so an absolute number would depend on the order the tests happen to run in.
/// </remarks>
public sealed class GenerationRunOpenBoundsReaderTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2036, 5, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SumsTheOpenJobs_AndSkipsIngestingAndTerminal()
    {
        var world = await WorldAsync();
        var baseline = await TotalAsync();

        await InsertJobAsync(world, "queued", 1.000001m);
        await InsertJobAsync(world, "submitted", 2.000002m);
        await InsertJobAsync(world, "running", 4.000004m);
        await InsertJobAsync(world, "ingesting", 8.000008m);
        await InsertJobAsync(world, "succeeded", 16.000016m);
        await InsertJobAsync(world, "failed", 32.000032m);
        await InsertJobAsync(world, "cancelled", 64.000064m);

        (await TotalAsync()).ShouldBe(
            baseline + 7.000007m,
            "an ingesting job's images are billed already and every terminal row is settled or refunded, so neither is open");
    }

    [Fact]
    public async Task SumsTheOpenGenerationRuns_AsBefore()
    {
        var world = await WorldAsync();
        var baseline = await TotalAsync();

        var open = await InsertRunAsync(world, "queued", 5.000005m);
        var closed = await InsertRunAsync(world, "queued", 8.000008m);

        (await TotalAsync()).ShouldBe(baseline + 13.000013m);

        await Database.ExecuteAsync(
            $"UPDATE generation_runs SET status = 'succeeded' WHERE id = '{closed}'", Ct);

        (await TotalAsync()).ShouldBe(
            baseline + 5.000005m, "a run that reached a terminal state is not open, whatever bound it started with");
        open.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task ExcludingRunId_SkipsTheNamedJobAndTheNamedRun()
    {
        var world = await WorldAsync();
        var baseline = await TotalAsync();
        var jobId = await InsertJobAsync(world, "queued", 3.000003m);
        var runId = await InsertRunAsync(world, "running", 6.000006m);

        (await TotalAsync()).ShouldBe(baseline + 9.000009m);

        // The retry path inserts its own row in the transaction that checks the cap, so it names that row here and
        // must not count it twice (02 D1). The value excludes the row of whichever table carries it.
        (await TotalAsync(jobId)).ShouldBe(baseline + 6.000006m);
        (await TotalAsync(runId)).ShouldBe(baseline + 3.000003m);
        (await TotalAsync(Guid.CreateVersion7())).ShouldBe(baseline + 9.000009m, "an unknown id excludes nothing");
    }

    [Fact]
    public async Task CountsEveryTenant_BecauseTheCapIsPlatformWide()
    {
        var mine = await WorldAsync();
        var other = await WorldAsync();
        var baseline = await TotalAsync();

        await InsertJobAsync(mine, "queued", 2.000002m);
        await InsertJobAsync(other, "running", 4.000004m);

        (await TotalAsync()).ShouldBe(
            baseline + 6.000006m, "the reader answers the platform cap, so another owner's open render counts");
    }

    private async Task<decimal> TotalAsync(Guid? excludingRunId = null)
    {
        await using var read = Database.CreateReadDbContext();

        return await new GenerationRunOpenBoundsReader(read).SumOpenWorstCaseUsdAsync(Ct, excludingRunId);
    }

    /// <summary>One owner with one series: every job row needs an episode and a story version of its own.</summary>
    private async Task<World> WorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);

        return new World(user.Id, series.Id, actor);
    }

    /// <summary>
    /// One render job in <paramref name="status"/> with the given bound. The row goes in as raw SQL because the
    /// bound and the state are the two things under test and the write model would need the whole
    /// <c>render-params.v1</c> document to write them; BD5's partial unique index allows one active row per
    /// episode, so each row gets its own episode.
    /// </summary>
    private async Task<Guid> InsertJobAsync(World world, string status, decimal worstCaseUsd)
    {
        var episode = await new EpisodeBuilder(Database, world.Actor).CreateAsync(world.UserId, world.SeriesId, ct: Ct);
        var versionId = await InsertVersionAsync(world.UserId, episode.Id);
        var jobId = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worst_case_usd) VALUES ("
            + $"'{jobId}', '{world.UserId}', '{episode.Id}', '{versionId}', '{Guid.CreateVersion7()}', "
            + $"'{status}', 'queued', 0, '{{}}'::jsonb, 1, {Money(worstCaseUsd)})",
            Ct);

        return jobId;
    }

    private async Task<Guid> InsertRunAsync(World world, string status, decimal worstCaseUsd)
    {
        var episode = await new EpisodeBuilder(Database, world.Actor).CreateAsync(world.UserId, world.SeriesId, ct: Ct);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });

        var run = GenerationRun.Queue(
            world.UserId,
            world.SeriesId,
            episode.Id,
            GenerationKind.Story,
            GenerationTrigger.User,
            null,
            EpisodeStatus.Draft,
            null,
            null,
            worstCaseUsd,
            Now).Value;
        db.GenerationRuns.Add(run);
        await db.SaveChangesAsync(Ct);

        if (!string.Equals(status, "queued", StringComparison.Ordinal))
        {
            await Database.ExecuteAsync($"UPDATE generation_runs SET status = '{status}' WHERE id = '{run.Id}'", Ct);
        }

        return run.Id;
    }

    /// <summary>
    /// The story version the job row points at (a same-owner FK, so the row has to exist). Written as raw SQL for the
    /// same reason as the job: only the reference matters here, and <c>schema_version</c> carries the store default.
    /// </summary>
    private async Task<Guid> InsertVersionAsync(Guid userId, Guid episodeId)
    {
        var id = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES ("
            + $"'{id}', '{userId}', '{episodeId}', 1, 'generated', 'Seed title', 'Seed body', '{{}}', 2, "
            + "'{}'::jsonb, now())",
            Ct);

        return id;
    }

    /// <summary>The stored scale of <c>worst_case_usd</c>, written in the invariant culture whatever the machine is.</summary>
    private static string Money(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record World(Guid UserId, Guid SeriesId, TestCurrentUser Actor);
}
