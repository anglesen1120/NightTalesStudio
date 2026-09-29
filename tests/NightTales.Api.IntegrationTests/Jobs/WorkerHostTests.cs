using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The two <see cref="WorkerHost"/> modes (09 README D13, 09 W3 CR-42), the Worker's start-time
/// job-resolution check, the observer's wait contract, and what the harness does with a host whose
/// start failed.
/// </summary>
[Collection("Hangfire server")]
public sealed class WorkerHostTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WorkerHost_InlineMode_IsTheDefault()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects);
        await host.StartAsync(TestContextToken.Value);

        host.UsesHangfireServer.ShouldBeFalse();
        host.Scheduler.ShouldBeOfType<InlineJobScheduler>();
        host.Scheduler.ShouldBeSameAs(host.Jobs);

        host.Jobs.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));
        (await host.Jobs.RunPendingAsync(TestContextToken.Value)).ShouldBe(1);

        effects.Runs.ShouldBe(1);
        host.Jobs.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task WorkerHost_RealServerMode_RunsQueuedJob()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        host.UsesHangfireServer.ShouldBeTrue();
        host.Scheduler.ShouldBeOfType<HangfireJobScheduler>();
        Should.Throw<InvalidOperationException>(() => host.Jobs);

        var jobId = host.Scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);

        effects.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task WorkerHost_BootFailsWhenAJobIsNotResolvable()
    {
        // DependencyProbeJob is the only job that needs ProbeJobDependency, so removing it makes
        // exactly one scanned job unresolvable and the Worker boot fails with its name.
        await using var host = ProbeHost.New(Database, new ProbeJobEffects())
            .ConfigureServices(services => services.RemoveAll<ProbeJobDependency>());

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(TestContextToken.Value));

        exception.Message.ShouldContain(nameof(DependencyProbeJob));
        exception.Message.ShouldContain("is not resolvable");
    }

    [Fact]
    public async Task WorkerHost_ObserverWait_ReturnsOnlyAfterTheStateIsCommitted()
    {
        // The observer's filter runs inside Hangfire's state transaction, before it commits, so a wait
        // that returned on the in-process signal alone would race the commit: the storage read below (or
        // the ChangeState the retry test issues next) could still see the previous state (CR 09.2.6-T1).
        await using var host = ProbeHost.New(Database, new ProbeJobEffects()).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<RetryingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await host.Observer.WaitForStatesAsync(jobId, "Scheduled", 1, Timeout);

        using var connection = host.Services.GetRequiredService<JobStorage>().GetConnection();
        connection.GetStateData(jobId)!.Name.ShouldBe("Scheduled");
    }

    [Fact]
    public async Task WorkerHost_ObserverWait_MatchesAStateTheJobHasMovedPast()
    {
        // A wait is about a state that was applied and persisted, not about the current state: a fast job
        // has already left Processing when the test asks for it, and that wait must return instead of
        // timing out (DR 09.2.6-T1).
        await using var host = ProbeHost.New(Database, new ProbeJobEffects()).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);
        await host.Observer.WaitForStateAsync(jobId, "Processing", Timeout);

        using var connection = host.Services.GetRequiredService<JobStorage>().GetConnection();
        connection.GetStateData(jobId)!.Name.ShouldBe("Succeeded");
    }

    [Fact]
    public async Task WorkerHost_FailedStart_StopsTheStartedServices_AndKeepsTheBootError()
    {
        // A host whose start failed after some of its hosted services started is stopped best effort — the
        // Worker's Hangfire log release and a Hangfire server are process-wide — and the caller still sees
        // the boot error itself, never a secondary stop failure (DR 09.2.6-T1). The throwing stop is
        // registered first, so in reverse it stops last and its failure is the one that must not win.
        var recorder = new RecordingHostedService();

        await using (var host = new WorkerHost(Database, new FakeClock())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(new ThrowingStopHostedService());
                services.AddSingleton<IHostedService>(recorder);
                services.AddSingleton<IHostedService>(new FailingStartHostedService());
            }))
        {
            var exception = await Should.ThrowAsync<InvalidOperationException>(
                () => host.StartAsync(TestContextToken.Value));

            exception.Message.ShouldBe(FailingStartHostedService.BootFailure);
        }

        recorder.StopCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TwoHosts_InOneProcess_DoNotCrossDeliverJobs()
    {
        // D13: two Worker hosts share one process. Each owns its JobStorage and JobActivator; a
        // regression to Hangfire's process-statics would make host B execute a job host A enqueued
        // (B's scope, B's effects) or deliver it from the wrong schema. Two schemas isolate the two
        // hosts' job sets, so any cross-delivery shows up in the effect counters.
        var effectsA = new ProbeJobEffects();
        var effectsB = new ProbeJobEffects();
        await using var hostA = ProbeHost.New(Database, effectsA)
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.SchemaName)}", "hangfire_a")
            .WithHangfireServer();
        await using var hostB = ProbeHost.New(Database, effectsB)
            .WithSetting($"{JobsOptions.SectionName}:{nameof(JobsOptions.SchemaName)}", "hangfire_b")
            .WithHangfireServer();
        await hostA.StartAsync(TestContextToken.Value);
        await hostB.StartAsync(TestContextToken.Value);

        var jobId = hostA.Scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await hostA.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);

        effectsA.Runs.ShouldBe(1);
        effectsB.Runs.ShouldBe(0);
    }

    /// <summary>A hosted service that only counts how often the host stopped it.</summary>
    private sealed class RecordingHostedService : IHostedService
    {
        private int _stopCalls;

        /// <summary>How often <see cref="StopAsync"/> ran.</summary>
        public int StopCalls => Volatile.Read(ref _stopCalls);

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopCalls);

            return Task.CompletedTask;
        }
    }

    /// <summary>A hosted service that starts cleanly and fails on stop.</summary>
    private sealed class ThrowingStopHostedService : IHostedService
    {
        /// <summary>Thrown by <see cref="StopAsync"/>; the boot error must win over it.</summary>
        public const string StopFailure = "probe stop failure (09.2.6-T1)";

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(StopFailure);
    }

    /// <summary>A hosted service whose start fails, the way a Worker boot error does.</summary>
    private sealed class FailingStartHostedService : IHostedService
    {
        /// <summary>The message the caller must see.</summary>
        public const string BootFailure = "probe boot failure (09.2.6-T1)";

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(BootFailure);

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
