using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Application.Features.Realtime.Jobs;
using NightTales.Infrastructure.Realtime;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Realtime;

/// <summary>
/// <c>HubTicketCleanupJob</c> and its registration (09 README BD11, 09.8.1 plan §7): the sweep removes
/// only tickets that outlived the retention, one configured batch per run, and the Worker registers it
/// hourly — never the Api (D12/R7).
/// </summary>
/// <remarks>
/// The job is resolved from the Worker's own graph (<see cref="WorkerHost"/> + the harness's
/// <see cref="InlineJobScheduler"/>), so the store registration this task added to
/// <c>AddRealtime</c> is part of what these tests prove. Time comes from
/// <see cref="FakeClock"/>, so retention is driven explicitly and the run never sleeps.
/// </remarks>
/// <param name="database">This class's isolated, migrated database.</param>
public sealed class HubTicketCleanupTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CleanupJob_Deletes_Only_Rows_Older_Than_Retention()
    {
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var userId = await CreateUserAsync();

        // The shipped retention is 24 hours, so the cutoff is now − 1 day: the first row is one hour
        // past it, the second one hour inside it.
        await InsertTicketAsync(userId, "past-retention", now - TimeSpan.FromHours(25));
        await InsertTicketAsync(userId, "inside-retention", now - TimeSpan.FromHours(23));

        await using var host = new WorkerHost(Database, clock);
        await host.StartAsync(Ct);
        await RunJobOnceAsync(host);

        (await CountAsync("ticket_hash = 'past-retention'")).ShouldBe(0);
        (await CountAsync("ticket_hash = 'inside-retention'")).ShouldBe(1, "only the retention decides");
    }

    [Fact]
    public async Task CleanupJob_Is_Batched()
    {
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var userId = await CreateUserAsync();

        for (var row = 0; row < 5; row++)
        {
            await InsertTicketAsync(userId, $"batch-{row}", now - TimeSpan.FromHours(30));
        }

        await using var host = new WorkerHost(Database, clock)
            .WithSetting(
                $"{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.TicketCleanupBatchSize)}",
                "2");
        await host.StartAsync(Ct);

        await RunJobOnceAsync(host);
        (await CountAsync("ticket_hash LIKE 'batch-%'"))
            .ShouldBe(3, "one run deletes at most Realtime:TicketCleanupBatchSize rows");

        await RunJobOnceAsync(host);
        (await CountAsync("ticket_hash LIKE 'batch-%'"))
            .ShouldBe(1, "the next run continues where the previous one stopped");
    }

    [Fact]
    public async Task CleanupJob_Leaves_Live_And_Used_Recent_Tickets()
    {
        var clock = new FakeClock();
        var now = clock.UtcNow;
        var userId = await CreateUserAsync();

        await InsertTicketAsync(userId, "live", now + TimeSpan.FromSeconds(60));
        await InsertTicketAsync(userId, "used-recent", now - TimeSpan.FromHours(1), usedAt: now - TimeSpan.FromHours(2));
        await InsertTicketAsync(
            userId,
            "used-and-expired",
            now - TimeSpan.FromHours(40),
            usedAt: now - TimeSpan.FromHours(41));

        await using var host = new WorkerHost(Database, clock);
        await host.StartAsync(Ct);
        await RunJobOnceAsync(host);

        (await CountAsync("ticket_hash = 'live'")).ShouldBe(1, "a claimable ticket is never swept");
        (await CountAsync("ticket_hash = 'used-recent'")).ShouldBe(1, "its row is still inside the retention");
        (await CountAsync("ticket_hash = 'used-and-expired'"))
            .ShouldBe(0, "used_at keeps nothing alive: only expires_at decides");
    }

    [Fact]
    public async Task Recurring_Registration_Id_Is_Stable()
    {
        var clock = new FakeClock();

        // The Worker's own graph: the registrar upserts every IRecurringJobRegistration at start, so the
        // hourly job appears in the harness's recorded calls keyed by its stable id.
        await using (var host = new WorkerHost(Database, clock))
        {
            await host.StartAsync(Ct);

            var upsert = host.Jobs.RecurringUpserts
                .Single(call => call.RecurringId == HubTicketCleanupJobRegistration.RecurringId);

            upsert.JobType.ShouldBe(nameof(HubTicketCleanupJob));
            upsert.Queue.ShouldBe(JobQueues.Name(JobQueue.Default));
            upsert.Cron.ShouldBe("0 * * * *", "the sweep is hourly (BD11)");
            HubTicketCleanupJobRegistration.RecurringId.ShouldBe("hub-ticket-cleanup");
        }

        // The Api never schedules it (D12/R7): it has no recurring registration at all, because only the
        // Worker's composition root scans for them.
        using var api = new ApiFactory(Database, clock);
        api.Services.GetServices<IRecurringJobRegistration>().ShouldBeEmpty();
    }

    private static async Task RunJobOnceAsync(WorkerHost host)
    {
        using var scope = host.CreateScope();

        await scope.ServiceProvider.GetRequiredService<HubTicketCleanupJob>().RunAsync(Ct);
    }

    private async Task<long> CountAsync(string predicate)
    {
        var rows = await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM hub_tickets WHERE {predicate}",
            Ct);

        return long.Parse(rows[0], CultureInfo.InvariantCulture);
    }

    private async Task<Guid> CreateUserAsync()
    {
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: Ct);

        return user.Id;
    }

    /// <summary>
    /// Writes one ticket row with the timestamps a case needs — the FakeClock's value, never
    /// PostgreSQL's <c>now()</c>, because that is what the store's cutoff is computed from.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="hash">The row's primary key.</param>
    /// <param name="expiresAt">When the row expires.</param>
    /// <param name="usedAt">When it was claimed, or null.</param>
    private Task InsertTicketAsync(Guid userId, string hash, DateTimeOffset expiresAt, DateTimeOffset? usedAt = null)
    {
        var created = expiresAt - TimeSpan.FromSeconds(RealtimeOptions.DefaultTicketTtlSeconds);
        var used = usedAt is { } value ? Timestamp(value) : "null";

        return Database.ExecuteAsync(
            $"""
             INSERT INTO hub_tickets (ticket_hash, user_id, created_at, expires_at, used_at)
             VALUES ('{hash}', '{userId}', {Timestamp(created)}, {Timestamp(expiresAt)}, {used})
             """,
            Ct);
    }

    private static string Timestamp(DateTimeOffset value) =>
        $"'{value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}+00'::timestamptz";
}
