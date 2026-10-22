using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// What the Worker's two Hangfire servers announce to the storage (06.2.3-T2 review PERF-1): the main
/// server holds every queue but <c>publish</c>, and the publish queue is served by its own server whose
/// worker count is <c>Jobs:Workers:publish</c> — the real bound on concurrent uploads.
/// </summary>
/// <remarks>
/// The assertion reads the storage's own server registry (the row <c>AnnounceServer</c> writes and the
/// dashboard renders), not this host's configuration, so it holds for any server that actually runs.
/// In the <c>Hangfire server</c> collection for the reason <see cref="HangfireJobSchedulerTests"/> gives.
/// </remarks>
[Collection("Hangfire server")]
public sealed class WorkerHangfireServerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The harness gives every queue one worker; the publish queue is raised to two here.</summary>
    private const int PublishWorkers = 2;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RealServerMode_BoundsPublishOnItsOwnServer()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects)
            .WithHangfireServer()
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.Workers)}:{JobQueues.Name(JobQueue.Publish)}", "2");
        await host.StartAsync(TestContextToken.Value);

        var api = host.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        var servers = await WaitForServersAsync(api, TestContextToken.Value);

        var publish = JobQueues.Name(JobQueue.Publish);
        var publishServer = servers.Single(server => server.Queues.Contains(publish));
        var mainServer = servers.Single(server => !ReferenceEquals(server, publishServer));

        // The uploads have their own pool of exactly the configured size, so a burst of minutes-long
        // uploads cannot take more threads than this.
        publishServer.Queues.ShouldBe([publish]);
        publishServer.WorkersCount.ShouldBe(PublishWorkers);

        // Every other queue stays on the main server, whose pool is the sum of *its* queues (1 + 1 + 1 in
        // the harness): 5 here would mean the publish workers are still counted into the shared pool.
        mainServer.Queues.ShouldBe(
        [
            JobQueues.Name(JobQueue.Default),
            JobQueues.Name(JobQueue.Render),
            JobQueues.Name(JobQueue.Email),
        ]);
        mainServer.WorkersCount.ShouldBe(3);
    }

    private static async Task<IReadOnlyList<ServerDto>> WaitForServersAsync(
        IMonitoringApi api,
        CancellationToken cancellationToken)
    {
        // Each server announces itself from its own dispatcher after the host has started, so the row is
        // written a moment later than StartAsync returns.
        var deadline = DateTime.UtcNow + Timeout;

        while (true)
        {
            var servers = api.Servers();

            if (servers.Count == 2)
            {
                return [.. servers];
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"expected 2 Hangfire servers in the storage, it listed {servers.Count}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
