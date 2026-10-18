using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Persistence;

/// <summary>
/// The bounded half of
/// <see cref="NightTales.Application.Abstractions.Persistence.IAppDbContext.LockRenderJobForUpdateAsync(Guid, TimeSpan, CancellationToken)"/>:
/// the wait it refuses and the setting it must put back. <c>lock_timeout</c> is transaction-local, so a bound left
/// on would silently cover the episode update, the audit and outbox inserts and the ledger refund of the request
/// transaction, none of which the caller maps (<c>55P03</c> there is a failed cancel, not a 409). Five cases:
/// the setting in force is restored, a contended lock is refused at the caller's bound (the positive control for
/// the bound itself), the smallest accepted bound is not rejected by the guard (the positive control for the
/// guard), a sub-millisecond bound is, and a call outside a transaction is refused. Precedent:
/// <see cref="RenderJobLockTests"/> for the fixture and the row.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LockRenderJobForUpdateBoundTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The bound the cancel path uses (09.6.4-T1), also the value that must never be left behind.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockRenderJobForUpdateAsync_WithABound_PutsBackTheSettingItFound(bool seeded)
    {
        const string Seeded = "7000";
        var job = await JobAsync();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = job.UserId });
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        if (seeded)
        {
            // A bound an earlier statement of this transaction already set: the value captured before the lock
            // must be this one, not the connection's default, so "restored" is distinguishable from "untouched".
            await context.Database
                .ExecuteSqlInterpolatedAsync($"SELECT set_config('lock_timeout', {Seeded}, true)", Ct);
        }

        var before = await LockTimeoutAsync(context, Ct);
        before.ShouldNotBe("2s", "the bound under test must not already be in force");
        if (seeded)
        {
            before.ShouldBe("7s", "the seed (7000 ms, shown by the server as 7s) must be in force before the call or this fact proves nothing");
        }

        await context.LockRenderJobForUpdateAsync(job.JobId, Bound, Ct);

        (await LockTimeoutAsync(context, Ct)).ShouldBe(
            before,
            "the later statements of the request transaction must keep the bound they started with");

        // Nothing here is meant to be committed, and the class shares one database with no per-fact reset.
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task LockRenderJobForUpdateAsync_WithAContendedRow_RefusesTheWaitAtTheCallersBound()
    {
        var job = await JobAsync();
        var user = new TestCurrentUser { Id = job.UserId };
        var bound = TimeSpan.FromMilliseconds(300);

        await using var holder = Database.CreateAppDbContext(user);
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        await holder.LockRenderJobForUpdateAsync(job.JobId, Ct);

        await using var contender = Database.CreateAppDbContext(user);
        {
            await using var waiting = await contender.Database.BeginTransactionAsync(Ct);

            var started = Stopwatch.GetTimestamp();
            var refused = await Should.ThrowAsync<PostgresException>(
                () => contender.LockRenderJobForUpdateAsync(job.JobId, bound, Ct));
            var waited = Stopwatch.GetElapsedTime(started);

            // The holder never releases the row, so only the caller's 300 ms bound can have ended this wait, and
            // only the lock statement was meant to be under it.
            refused.SqlState.ShouldBe("55P03");
            waited.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(250), "the bound must be honoured, not skipped");
            waited.ShouldBeLessThan(TimeSpan.FromSeconds(30));

            await waiting.RollbackAsync(Ct);
        }

        // The refusal aborted the transaction, so the setting it applied must have reverted with the rollback and
        // must not sit on the pooled connection either. Not a vacuous reading: the value this fact put in force
        // was 300 ms (which the wait above proves reached the server), so a leak would be readable here as "300ms"
        // or "300" and only the reverted session default passes.
        await using (var next = await contender.Database.BeginTransactionAsync(Ct))
        {
            (await LockTimeoutAsync(contender, Ct)).ShouldBe("0", "the refusal must not leak its bound");
            await next.RollbackAsync(Ct);
        }

        await held.CommitAsync(Ct);
    }

    [Fact]
    public async Task LockRenderJobForUpdateAsync_WithTheSmallestAcceptedBound_LocksAndPutsTheSettingBack()
    {
        var job = await JobAsync();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = job.UserId });
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        await context.LockRenderJobForUpdateAsync(job.JobId, TimeSpan.FromMilliseconds(1), Ct);

        // Positive control for the guard below: one millisecond is accepted, so the guard rejects a bound and not
        // the method, and the lock statement it bounds really ran (a second connection now cannot take the row).
        SqlProbe.SqlState(Database, $"SELECT id FROM render_jobs WHERE id = '{job.JobId}' FOR UPDATE NOWAIT")
            .ShouldBe("55P03", "the lock the smallest bound was spent on must have been taken");
        (await LockTimeoutAsync(context, Ct)).ShouldBe("0");

        await transaction.RollbackAsync(Ct);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(TimeSpan.TicksPerMillisecond - 1)]
    public async Task LockRenderJobForUpdateAsync_WithASubMillisecondBound_Throws(long ticks)
    {
        var job = await JobAsync();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = job.UserId });

        var thrown = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => context.LockRenderJobForUpdateAsync(job.JobId, TimeSpan.FromTicks(ticks), Ct));

        // Truncated, a sub-millisecond bound is 0, which the provider reads as "no timeout at all".
        thrown.ParamName.ShouldBe("lockTimeout", "the guard must reject the bound, not the provider");
    }

    [Fact]
    public async Task LockRenderJobForUpdateAsync_OutsideATransaction_Throws()
    {
        var job = await JobAsync();
        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = job.UserId });

        // Outside a transaction every statement runs in its own, so the bound would die with its own statement and
        // the lock would wait unboundedly: the port refuses instead of silently losing the bound it was handed.
        // Positive control: the facts above make the same call inside a transaction and are not refused.
        await Should.ThrowAsync<InvalidOperationException>(
            () => context.LockRenderJobForUpdateAsync(job.JobId, Bound, Ct));
    }

    /// <summary>The bound in force in <paramref name="context"/>'s connection and transaction.</summary>
    private static Task<string> LockTimeoutAsync(AppDbContext context, CancellationToken ct) =>
        context.Database
            .SqlQuery<string>($"SELECT current_setting('lock_timeout') AS \"Value\"")
            .SingleAsync(ct);

    /// <summary>
    /// One owner with one series and one queued job, copied from <see cref="RenderJobLockTests"/>: the row is
    /// written as raw SQL because the lock only needs the identities and the two same-owner foreign keys.
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
