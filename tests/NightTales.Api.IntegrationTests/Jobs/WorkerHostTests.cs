using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The two <see cref="WorkerHost"/> modes (09 README D13, 09 W3 CR-42) and the Worker's start-time
/// job-resolution check.
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
}
