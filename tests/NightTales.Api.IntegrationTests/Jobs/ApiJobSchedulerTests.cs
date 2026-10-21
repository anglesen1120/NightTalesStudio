using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.DependencyInjection;
using NightTales.Application.Abstractions.Jobs;
using NightTales.Infrastructure.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// The Api's job port (09.2.6-T1 Tests table, 09 README D12/H4): it only throws, because the Api
/// writes the outbox and the Worker dispatches it.
/// </summary>
public sealed class ApiJobSchedulerTests
{
    [Fact]
    public void ApiJobScheduler_Throws()
    {
        var services = new ServiceCollection();
        services.AddApiJobScheduler();
        using var provider = services.BuildServiceProvider();

        var scheduler = provider.GetRequiredService<IJobScheduler>();
        scheduler.ShouldBeOfType<ThrowingJobScheduler>();

        Should.Throw<InvalidOperationException>(() => scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct)))
            .Message.ShouldBe(ThrowingJobScheduler.Message);
        Should.Throw<InvalidOperationException>(() => scheduler.Schedule<RecordingProbeJob>((job, ct) => job.RunAsync(Guid.Empty, ct), TimeSpan.FromMinutes(1)))
            .Message.ShouldBe(ThrowingJobScheduler.Message);
        Should.Throw<InvalidOperationException>(() => scheduler.AddOrUpdateRecurring<RecordingProbeJob>("probe:recurring", "0 3 * * *", (job, ct) => job.RunAsync(Guid.Empty, ct)))
            .Message.ShouldBe(ThrowingJobScheduler.Message);
        Should.Throw<InvalidOperationException>(() => scheduler.RemoveRecurringIfExists("probe:recurring"))
            .Message.ShouldBe(ThrowingJobScheduler.Message);
    }
}
