using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The recurring-job reconciliation of the Worker start (09.2.6-T1, 09 W3 CR-42): every registration is
/// upserted, every stored id no registration produces is deleted — with no <c>manual:</c> exemption.
/// </summary>
/// <remarks>
/// The registrar runs inside <see cref="WorkerHost.StartAsync"/>, so an assertion after the start is
/// deterministic: no wait, no sleep. These tests start real servers, so they share the
/// <c>Hangfire server</c> collection with the other server-owning classes.
/// </remarks>
[Collection("Hangfire server")]
public sealed class RecurringJobRegistrarTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Cron = "0 3 * * *";

    [Fact]
    public async Task RecurringRegistrar_IsIdempotent()
    {
        var registration = new StubRegistration("probe:recurring", Cron);

        await using (var first = ProbeHost.New(Database, new ProbeJobEffects()).WithHangfireServer().WithRecurringJobRegistration(registration))
        {
            await first.StartAsync(TestContextToken.Value);

            (await RecurringIdsAsync(first)).ShouldBe(["probe:recurring"]);
        }

        await using (var second = ProbeHost.New(Database, new ProbeJobEffects()).WithHangfireServer().WithRecurringJobRegistration(registration))
        {
            await second.StartAsync(TestContextToken.Value);

            (await RecurringIdsAsync(second)).ShouldBe(["probe:recurring"]);
        }
    }

    [Fact]
    public async Task RecurringRegistrar_RemovesStaleIds()
    {
        await using (var first = ProbeHost.New(Database, new ProbeJobEffects())
            .WithHangfireServer()
            .WithRecurringJobRegistration(new StubRegistration("probe:keep", Cron))
            .WithRecurringJobRegistration(new StubRegistration("probe:drop", Cron)))
        {
            await first.StartAsync(TestContextToken.Value);

            (await RecurringIdsAsync(first)).ShouldBe(["probe:drop", "probe:keep"], ignoreOrder: true);
        }

        await using (var second = ProbeHost.New(Database, new ProbeJobEffects())
            .WithHangfireServer()
            .WithRecurringJobRegistration(new StubRegistration("probe:keep", Cron)))
        {
            await second.StartAsync(TestContextToken.Value);

            (await RecurringIdsAsync(second)).ShouldBe(["probe:keep"]);
        }
    }

    [Fact]
    public async Task RecurringRegistrar_RemovesUnregisteredId_OnNextStart()
    {
        // `manual:` was the carve-out 09 W3 CR-42 dropped: an id no registration produces is deleted.
        await using (var first = ProbeHost.New(Database, new ProbeJobEffects())
            .WithHangfireServer()
            .WithRecurringJobRegistration(new StubRegistration("manual:legacy", Cron)))
        {
            await first.StartAsync(TestContextToken.Value);

            (await RecurringIdsAsync(first)).ShouldBe(["manual:legacy"]);
        }

        await using (var second = ProbeHost.New(Database, new ProbeJobEffects()).WithHangfireServer())
        {
            await second.StartAsync(TestContextToken.Value);

            (await RecurringIdsAsync(second)).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task RecurringRegistrar_ThrowsOnDuplicateId()
    {
        await using var host = ProbeHost.New(Database, new ProbeJobEffects())
            .WithHangfireServer()
            .WithRecurringJobRegistration(new StubRegistration("probe:duplicate", Cron))
            .WithRecurringJobRegistration(new StubRegistration("probe:duplicate", Cron));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain("probe:duplicate");
    }

    private static async Task<IReadOnlyList<string>> RecurringIdsAsync(WorkerHost host)
    {
        using var connection = host.Services.GetRequiredService<JobStorage>().GetConnection();

        return [.. connection.GetRecurringJobs().Select(job => job.Id).OrderBy(id => id, StringComparer.Ordinal)];
    }

    private sealed class StubRegistration(string id, string cron) : IRecurringJobRegistration
    {
        public string Id => id;

        public void Register(IJobScheduler scheduler) =>
            scheduler.AddOrUpdateRecurring<RecordingProbeJob>(id, cron, (job, ct) => job.RunAsync(Guid.Empty, ct));
    }
}
