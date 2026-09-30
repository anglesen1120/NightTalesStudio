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
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// <c>IdempotencyKeyCleanupJob</c> (09.1.3 plan §7): it removes exactly the rows that outlived their
/// 24 h window by a further day, in batches, and it is registered as a daily 04:00 UTC recurring job.
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
