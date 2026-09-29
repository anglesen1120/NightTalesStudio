using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The real Hangfire server on PostgreSQL storage (09.2.6-T1): enqueue → run, queue routing, the
/// <c>[JobPolicy]</c> retry policy, and the shutdown token Hangfire substitutes at performance time.
/// </summary>
/// <remarks>
/// Every assertion waits on <see cref="JobStateObserver"/> or <see cref="ProbeJobEffects"/>, both
/// event-driven, so nothing here sleeps. The server runs with the harness's one-second poll
/// (<see cref="WorkerHost.WithHangfireServer"/>), and the classes that start one share the
/// <c>Hangfire server</c> collection so parallel test classes do not compete for the test server's
/// connections.
/// </remarks>
[Collection("Hangfire server")]
public sealed class HangfireJobSchedulerTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HangfirePostgres_OnNpgsql10_EnqueueAndRun()
    {
        await Database.ExecuteAsync("CREATE TABLE IF NOT EXISTS probe_job_effects (job_id uuid PRIMARY KEY)", TestContextToken.Value);
        var effects = new ProbeJobEffects();
        var probeId = Guid.CreateVersion7();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<EffectRowProbeJob>((job, ct) => job.RunAsync(probeId, ct));

        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);

        var rows = await Database.QueryStringsAsync("SELECT job_id::text FROM probe_job_effects", TestContextToken.Value);
        rows.ShouldBe([probeId.ToString()]);
    }

    [Fact]
    public async Task Job_RoutedToPolicyQueue()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<RenderQueueProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        // The probe job blocks, so the job is still in Processing and the storage still counts it as
        // fetched: the fetched counter is what proves which queue the server took the job from. (The
        // Enqueued state's own queue stays "default"; Hangfire uses Job.Queue in that case.)
        await host.Observer.WaitForStateAsync(jobId, "Processing", Timeout);

        var api = host.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        var render = JobQueues.Name(JobQueue.Render);
        api.JobDetails(jobId)!.Job.Queue.ShouldBe(render);
        api.FetchedCount(render).ShouldBe(1);
        api.FetchedCount(JobQueues.Name(JobQueue.Default)).ShouldBe(0);

        effects.AllowCompletion();
        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);
    }

    [Fact]
    public async Task Job_WithRetriesZero_FailsWithoutRetry()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<FailingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await host.Observer.WaitForStateAsync(jobId, "Failed", Timeout);

        effects.Runs.ShouldBe(1);
        host.Observer.CountOf(jobId, "Scheduled").ShouldBe(0);
    }

    [Fact]
    public async Task Job_WithoutPolicy_FailsWithoutRetry()
    {
        // The platform default is 0 retries: Hangfire's global 10-attempt default is removed.
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<DefaultFailingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await host.Observer.WaitForStateAsync(jobId, "Failed", Timeout);

        effects.Runs.ShouldBe(1);
        host.Observer.CountOf(jobId, "Scheduled").ShouldBe(0);
    }

    [Fact]
    public async Task Job_WithRetriesTwo_RetriesTwice()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var client = host.Services.GetRequiredService<IBackgroundJobClient>();
        var queue = JobQueues.Name(JobQueue.Default);
        var jobId = host.Scheduler.Enqueue<RetryingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        // Hangfire's default backoff puts the next attempt tens of seconds out, and these tests never
        // sleep: the retry election is real (the observer sees the Scheduled state Hangfire chose),
        // and the test moves the scheduled job forward itself so all three attempts happen now.
        await host.Observer.WaitForStatesAsync(jobId, "Scheduled", 1, Timeout);
        await effects.WaitForRunsAsync(1, Timeout);
        client.ChangeState(jobId, new EnqueuedState(queue), "Scheduled").ShouldBeTrue();

        await host.Observer.WaitForStatesAsync(jobId, "Scheduled", 2, Timeout);
        await effects.WaitForRunsAsync(2, Timeout);
        client.ChangeState(jobId, new EnqueuedState(queue), "Scheduled").ShouldBeTrue();

        await host.Observer.WaitForStateAsync(jobId, "Failed", Timeout);

        effects.Runs.ShouldBe(3);
        host.Observer.CountOf(jobId, "Scheduled").ShouldBe(2);
    }

    [Fact]
    public async Task Job_ReceivesServerCancellationToken()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);

        effects.LastToken.ShouldNotBe(CancellationToken.None);
        effects.LastToken.CanBeCanceled.ShouldBeTrue();
    }

    [Fact]
    public async Task Schedule_RunsAfterTheDelay()
    {
        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Schedule<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct), TimeSpan.Zero);

        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);

        effects.Runs.ShouldBe(1);
    }
}
