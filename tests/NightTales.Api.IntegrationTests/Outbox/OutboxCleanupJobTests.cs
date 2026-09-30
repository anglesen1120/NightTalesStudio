using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Platform.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Outbox;

/// <summary>
/// <c>OutboxCleanupJob</c> (09.2.4 plan §7): it removes the delivered rows that outlived the retention and
/// keeps the ones the dispatcher still owes — the pending and the dead-lettered. The job is resolved from
/// the Worker's own graph, so the sweep's registration (its port and its job class) is part of what it
/// proves.
/// </summary>
public sealed class OutboxCleanupJobTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The stored type of this class's rows, so its counts never see another class's rows.</summary>
    private const string ProbeType = "probe_cleanup.v1";

    [Fact]
    public async Task OutboxCleanup_DeletesOnlyOldProcessedRows()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var occurredAt = now - TimeSpan.FromDays(30);

        // The host starts first: its dispatcher loop claims once at start, so rows written after it started
        // are never delivered by the loop (which then sleeps for the parked poll) and stay as inserted.
        await using var host = new WorkerHost(Database, clock);
        await host.StartAsync(ct);

        // One row per fate, relative to the retention of the job: only "delivered and older than the
        // retention" is garbage; everything the dispatcher still owes is kept.
        var deliveredLongAgo = Guid.CreateVersion7();
        var deliveredYesterday = Guid.CreateVersion7();
        var pending = Guid.CreateVersion7();
        var deadLettered = Guid.CreateVersion7();

        await InsertAsync(deliveredLongAgo, occurredAt, now - OutboxCleanupJob.Retention - TimeSpan.FromMinutes(1), failedAt: null, ct);
        await InsertAsync(deliveredYesterday, occurredAt, now - TimeSpan.FromDays(1), failedAt: null, ct);
        await InsertAsync(pending, occurredAt, processedAt: null, failedAt: null, ct);
        await InsertAsync(deadLettered, occurredAt, processedAt: null, failedAt: occurredAt, ct);

        using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OutboxCleanupJob>().RunAsync(ct);
        }

        Count(deliveredLongAgo).ShouldBe(0, "a delivered row older than the retention is swept");
        Count(deliveredYesterday).ShouldBe(1, "a delivered row inside the retention is kept");
        Count(pending).ShouldBe(1, "the dispatcher still owes this row");
        Count(deadLettered).ShouldBe(1, "a dead letter is evidence somebody has to look at");
    }

    [Fact]
    public void OutboxCleanupRegistration_RegistersDailyUtcCron()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        new OutboxCleanupJobRegistration().Register(scheduler);

        scheduler.RecurringUpserts.ShouldHaveSingleItem().ShouldBe(new RecordedRecurringCall(
            OutboxCleanupJobRegistration.RecurringId,
            nameof(OutboxCleanupJob),
            JobQueues.Name(JobQueue.Default),
            OutboxCleanupJobRegistration.Cron));
    }

    /// <summary>Writes one row of this class's type with the delivery state the sweep is judged on.</summary>
    private Task InsertAsync(
        Guid id,
        DateTimeOffset occurredAt,
        DateTimeOffset? processedAt,
        DateTimeOffset? failedAt,
        CancellationToken ct) =>
        Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts, processed_at, failed_at)
             VALUES ('{id}', '{ProbeType}', jsonb_build_object(), NULL, '{occurredAt:O}', '{occurredAt:O}', 0,
                     {(processedAt is null ? "NULL" : $"'{processedAt:O}'")},
                     {(failedAt is null ? "NULL" : $"'{failedAt:O}'")})
             """,
            ct);

    private long Count(Guid id) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE id = '{id}'");
}
