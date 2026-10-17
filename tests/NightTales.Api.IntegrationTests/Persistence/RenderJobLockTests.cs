using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// <see cref="NightTales.Application.Abstractions.Persistence.IAppDbContext.LockRenderJobForUpdateAsync"/> (09.6.3-T1):
/// the isolated half of BD7's submit handshake, taken before the adapter call and re-checked after it, so two
/// deliveries of <c>render_requested.v1</c> cannot both reach a node. It follows the three cases of
/// <c>LockEpisodeForUpdateAsync</c> (GenerationRunsMigrationTests): the lock is held until the transaction ends, a
/// second submit waits for it, and a scope that is not the row's owner locks nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RenderJobLockTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LockRenderJobForUpdateAsync_HoldsTheRowLockUntilTheTransactionEnds()
    {
        var job = await JobAsync();
        var lockStatement = $"SELECT id FROM render_jobs WHERE id = '{job.JobId}' FOR UPDATE NOWAIT";

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = job.UserId });
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        await context.LockRenderJobForUpdateAsync(job.JobId, Ct);

        SqlProbe.SqlState(Database, lockStatement)
            .ShouldBe("55P03", "a second delivery of the same request must wait for (here: fail on) the job row");

        await transaction.CommitAsync(Ct);

        SqlProbe.Execute(Database, lockStatement);
    }

    [Fact]
    public async Task LockRenderJobForUpdateAsync_SerializesAnotherLocker()
    {
        var job = await JobAsync();
        var user = new TestCurrentUser { Id = job.UserId };
        await using var holder = Database.CreateAppDbContext(user);
        await using var heldTransaction = await holder.Database.BeginTransactionAsync(Ct);
        await holder.LockRenderJobForUpdateAsync(job.JobId, Ct);

        await using var contender = Database.CreateAppDbContext(user);
        await using (var waitingTransaction = await contender.Database.BeginTransactionAsync(Ct))
        {
            await contender.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '200ms'", Ct);
            var blocked = await Should.ThrowAsync<Npgsql.PostgresException>(
                () => contender.LockRenderJobForUpdateAsync(job.JobId, Ct));
            blocked.SqlState.ShouldBe("55P03", "the second submit waits for the row the first one is holding");
            await waitingTransaction.RollbackAsync(Ct);
        }

        await heldTransaction.CommitAsync(Ct);
        await contender.LockRenderJobForUpdateAsync(job.JobId, Ct);
    }

    [Fact]
    public async Task LockRenderJobForUpdateAsync_ForAnotherUsersOrAnAnonymousScope_LocksNothing()
    {
        var job = await JobAsync();
        var lockStatement = $"SELECT id FROM render_jobs WHERE id = '{job.JobId}' FOR UPDATE NOWAIT";

        await using var other = Database.CreateAppDbContext(new TestCurrentUser { Id = Guid.CreateVersion7() });
        await using var otherTransaction = await other.Database.BeginTransactionAsync(Ct);
        await other.LockRenderJobForUpdateAsync(job.JobId, Ct);

        await using var anonymous = Database.CreateAppDbContext();
        await using var anonymousTransaction = await anonymous.Database.BeginTransactionAsync(Ct);
        await anonymous.LockRenderJobForUpdateAsync(job.JobId, Ct);

        // The owner's own lock is still free on a second connection (it would be 55P03 had either scope locked the row).
        SqlProbe.Execute(Database, lockStatement);
    }

    /// <summary>
    /// One owner with one series and one queued job: the row is written as raw SQL because the lock only needs the
    /// identities and the two same-owner foreign keys of the table, not a whole <c>render-params.v1</c> document.
    /// </summary>
    private async Task<Job> JobAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);
        var versionId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();

        await Database.ExecuteAsync(
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, "
            + "word_count, content, created_at) VALUES ("
            + $"'{versionId}', '{user.Id}', '{episode.Id}', 1, 'generated', 'Seed title', 'Seed body', '{{}}', 2, "
            + "'{}'::jsonb, now())",
            Ct);

        await Database.ExecuteAsync(
            "INSERT INTO render_jobs (id, user_id, episode_id, story_version_id, reservation_id, status, stage, "
            + "percent, params, attempt, worst_case_usd) VALUES ("
            + $"'{jobId}', '{user.Id}', '{episode.Id}', '{versionId}', '{Guid.CreateVersion7()}', "
            + "'queued', 'queued', 0, '{}'::jsonb, 1, 1.200000)",
            Ct);

        return new Job(user.Id, jobId);
    }

    private sealed record Job(Guid UserId, Guid JobId);
}
