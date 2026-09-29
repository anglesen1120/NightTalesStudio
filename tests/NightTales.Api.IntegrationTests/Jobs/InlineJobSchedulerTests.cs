using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Jobs;
using NightTales.TestKit.Threading;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The inline job port of the default <see cref="WorkerHost"/> mode (09.2.1-T2 Test table, 09 README
/// D13): it records every scheduling call and runs jobs synchronously on demand.
/// </summary>
public sealed class InlineJobSchedulerTests
{
    [Fact]
    public async Task InlineJobScheduler_RecordsAndRunsJobs()
    {
        var effects = new ProbeJobEffects();
        using var provider = BuildProvider(effects);
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var jobId = scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        scheduler.Enqueued.ShouldBe([new RecordedEnqueue(nameof(RecordingProbeJob), "default", jobId, Delay: null)]);
        scheduler.RecurringUpserts.ShouldBeEmpty();
        effects.Runs.ShouldBe(0);

        (await scheduler.RunPendingAsync(CancellationToken.None)).ShouldBe(1);
        effects.Runs.ShouldBe(1);
        scheduler.Failures.ShouldBeEmpty();

        (await scheduler.RunPendingAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task InlineJobScheduler_RecordsRecurringAndScheduledCalls()
    {
        var effects = new ProbeJobEffects();
        using var provider = BuildProvider(effects);
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        var jobId = scheduler.Schedule<RecordingProbeJob>(
            (job, ct) => job.RunAsync(Guid.Empty, ct),
            TimeSpan.FromMinutes(5));
        scheduler.AddOrUpdateRecurring<RenderQueueProbeJob>("probe:recurring", "0 3 * * *", (job, ct) => job.RunAsync(Guid.Empty, ct));
        scheduler.RemoveRecurringIfExists("probe:stale");

        scheduler.Enqueued.ShouldBe([new RecordedEnqueue(nameof(RecordingProbeJob), "default", jobId, TimeSpan.FromMinutes(5))]);
        scheduler.RecurringUpserts.ShouldBe([new RecordedRecurringCall("probe:recurring", nameof(RenderQueueProbeJob), "render", "0 3 * * *")]);
        scheduler.RecurringRemovals.ShouldBe(["probe:stale"]);

        (await scheduler.RunPendingAsync(CancellationToken.None)).ShouldBe(1);
        effects.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task InlineJobScheduler_RecordsFailuresAndKeepsRunning()
    {
        var effects = new ProbeJobEffects();
        using var provider = BuildProvider(effects);
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        scheduler.Enqueue<FailingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));
        scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        (await scheduler.RunPendingAsync(CancellationToken.None)).ShouldBe(1);

        scheduler.Failures.ShouldHaveSingleItem().Message.ShouldBe(DefaultFailingProbeJob.FailureMessage);
        effects.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task InlineJobScheduler_RecordsAnUnresolvableJobAsFailure()
    {
        using var provider = BuildProvider(new ProbeJobEffects());
        var scheduler = new InlineJobScheduler(provider.GetRequiredService<IServiceScopeFactory>());

        scheduler.Enqueue<DependencyProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct));

        (await scheduler.RunPendingAsync(CancellationToken.None)).ShouldBe(0);
        scheduler.Failures.ShouldHaveSingleItem().Message.ShouldContain(nameof(DependencyProbeJob));
    }

    private static ServiceProvider BuildProvider(ProbeJobEffects effects)
    {
        var services = new ServiceCollection();
        services.AddSingleton(effects);
        services.AddScoped<RecordingProbeJob>();
        services.AddScoped<FailingProbeJob>();
        services.AddScoped<RenderQueueProbeJob>();
        services.AddScoped<DependencyProbeJob>();

        return services.BuildServiceProvider();
    }
}
