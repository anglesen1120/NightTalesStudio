using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Platform.Jobs;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// <c>IdempotencyKeyCleanupJob</c> (09.1.3 plan §7): it removes exactly the rows that outlived their
/// 24 h window by a further day, in batches, and it is registered as a daily 04:00 UTC recurring job.
/// It also proves the race against the request path: a row a twin renewed while the sweep's
/// <c>DELETE</c> waited on that row's lock is not swept.
/// </summary>
public sealed class IdempotencyKeyCleanupJobTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>One more than <see cref="IdempotencyCleanupStore.BatchSize"/>, so the loop runs twice.</summary>
    private const int OverOneBatch = IdempotencyCleanupStore.BatchSize + 1;

    [Fact]
    public async Task IdempotencyKeyCleanup_DeletesOnlyExpiredRows()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);

        // The predicate is expires_at + 1 day < now: the grace day is what separates the two.
        await InsertKeyAsync(user.Id, "beyond-grace", now - TimeSpan.FromHours(25) - TimeSpan.FromMinutes(1), ct);
        await InsertKeyAsync(user.Id, "at-grace", now - TimeSpan.FromDays(1), ct);
        await InsertKeyAsync(user.Id, "expired", now - TimeSpan.FromMinutes(5), ct);
        await InsertKeyAsync(user.Id, "live", now + TimeSpan.FromHours(5), ct);

        // More expired rows than one batch: one run must drain them all. The stamps come from the fake
        // clock — the sweep's cutoff does — or the rows would sit after the cutoff and never be swept.
        var bulkExpiry = now - TimeSpan.FromDays(3);
        await Database.ExecuteAsync(
            $"""
             INSERT INTO idempotency_keys (id, user_id, key, request_hash, created_at, expires_at)
             SELECT gen_random_uuid(), '{user.Id}', 'bulk-' || g.n, repeat('a', 64), '{bulkExpiry:O}', '{bulkExpiry:O}'
             FROM generate_series(1, {OverOneBatch}) AS g(n)
             """,
            ct);

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, clock);
        await new IdempotencyKeyCleanupJob(new IdempotencyCleanupStore(context), clock).RunAsync(ct);

        CountKeys(user.Id, "beyond-grace").ShouldBe(0);
        CountKeys(user.Id, "at-grace").ShouldBe(1, "the grace day is inclusive: expires_at + 1 day == now is kept");
        CountKeys(user.Id, "expired").ShouldBe(1);
        CountKeys(user.Id, "live").ShouldBe(1);
        BulkKeys(user.Id).ShouldBe(
            0,
            $"one run drains every batch: {OverOneBatch + 1} expired rows are more than one batch of {IdempotencyCleanupStore.BatchSize}");
    }

    [Fact]
    public async Task IdempotencyKeyCleanup_RenewalThatWinsTheRace_IsNotSwept()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        const string key = "renewed-under-the-sweep";
        var renewedAt = now + TimeSpan.FromHours(24);

        // The tests of this class share one database, so drain what they left behind first: the sweep
        // under test then has exactly one candidate row and its count is part of the assertion.
        await using var drainContext = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, clock);
        await new IdempotencyCleanupStore(drainContext).DeleteExpiredAsync(now, ct);

        // The oldest row of the table, so the sweep's batch always names it first.
        await InsertKeyAsync(user.Id, key, now - TimeSpan.FromDays(30), ct);

        // A twin that read the row while it was expired: it holds the row's lock (the same
        // `SELECT … FOR UPDATE` the store's ClaimAsync runs) and renews it (the same UPDATE its
        // ReuseAsync runs), uncommitted — the two statements are hand-rolled because the interleaving
        // below needs the statement boundary the store hides.
        await using var claim = new NpgsqlConnection(Database.ConnectionString);
        await claim.OpenAsync(ct);
        await using var claimTransaction = await claim.BeginTransactionAsync(ct);

        await using (var lockRow = new NpgsqlCommand(
            "SELECT id FROM idempotency_keys WHERE user_id = @user_id AND key = @key FOR UPDATE",
            claim,
            claimTransaction))
        {
            lockRow.Parameters.AddWithValue("user_id", user.Id);
            lockRow.Parameters.AddWithValue("key", key);
            (await lockRow.ExecuteScalarAsync(ct)).ShouldNotBeNull("the expired row is there to be locked");
        }

        await using (var renew = new NpgsqlCommand(
            "UPDATE idempotency_keys SET expires_at = @expires_at WHERE user_id = @user_id AND key = @key",
            claim,
            claimTransaction))
        {
            renew.Parameters.AddWithValue("expires_at", renewedAt);
            renew.Parameters.AddWithValue("user_id", user.Id);
            renew.Parameters.AddWithValue("key", key);
            (await renew.ExecuteNonQueryAsync(ct)).ShouldBe(1);
        }

        // The sweep snapshots the row as expired and blocks on the claim's lock; only then is the
        // renewal committed, which is the exact interleaving EvalPlanQual decides.
        await using var sweepContext = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, clock);
        var sweeping = new IdempotencyCleanupStore(sweepContext).DeleteExpiredAsync(now, ct);

        await WaitUntilTheSweepWaitsOnTheRowLockAsync(ct);
        await claimTransaction.CommitAsync(ct);

        (await sweeping).ShouldBe(
            0,
            "the renewed row is no longer expired, so the racing DELETE must leave it — the subquery alone "
            + "would still name it and the claim would vanish under its owner");
        CountKeys(user.Id, key).ShouldBe(1, "the row itself survives");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{user.Id}' AND key = '{key}' "
                + $"AND expires_at > '{now:O}'")
            .ShouldBe(1, "the surviving row is the renewed one, not a stale copy");
    }

    [Fact]
    public void IdempotencyKeyCleanupRegistration_RegistersDailyUtcCron()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        new IdempotencyKeyCleanupJobRegistration().Register(scheduler);

        scheduler.RecurringUpserts.ShouldHaveSingleItem().ShouldBe(new RecordedRecurringCall(
            IdempotencyKeyCleanupJobRegistration.RecurringId,
            nameof(IdempotencyKeyCleanupJob),
            JobQueues.Name(JobQueue.Default),
            IdempotencyKeyCleanupJobRegistration.Cron));
    }

    /// <summary>
    /// Waits until the sweep's <c>DELETE</c> is blocked on the claim's row lock. The race is only
    /// exercised once the statement has taken its snapshot and is waiting on that row: an unpolled start
    /// could run the whole sweep before the claim commits and prove nothing.
    /// </summary>
    private async Task WaitUntilTheSweepWaitsOnTheRowLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var waiting = SqlProbe.Count(
                Database,
                "SELECT count(*) FROM pg_stat_activity WHERE state = 'active' AND wait_event_type = 'Lock' "
                + "AND query LIKE 'DELETE FROM idempotency_keys%'");

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new InvalidOperationException(
            "The sweep's DELETE never blocked on the claim's row lock, so the renewal race was not exercised.");
    }

    private Task InsertKeyAsync(Guid userId, string key, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        Database.ExecuteAsync(
            $"""
             INSERT INTO idempotency_keys (id, user_id, key, request_hash, created_at, expires_at)
             VALUES ('{Guid.CreateVersion7()}', '{userId}', '{key}', '{new string('a', 64)}', '{expiresAt:O}', '{expiresAt:O}')
             """,
            cancellationToken);

    private long CountKeys(Guid userId, string key) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{userId}' AND key = '{key}'");

    private long BulkKeys(Guid userId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{userId}' AND key LIKE 'bulk-%'");
}
