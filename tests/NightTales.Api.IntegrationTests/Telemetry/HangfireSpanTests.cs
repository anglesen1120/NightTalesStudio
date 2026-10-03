using System.Diagnostics;
using NightTales.Api.IntegrationTests.Jobs;
using NightTales.Infrastructure.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.Api.IntegrationTests.Harness;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Telemetry;

[Collection("Hangfire server")]
public sealed class HangfireSpanTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Job_run_creates_activity_named_job_queue_and_method()
    {
        var spans = await RunProbeJobAsync(Guid.CreateVersion7());

        var span = spans.ShouldHaveSingleItem();
        span.OperationName.ShouldStartWith("job ");
        span.OperationName.ShouldEndWith($":{nameof(RecordingProbeJob.RunAsync)}");
        span.GetTagItem("job.name").ShouldBe(nameof(RecordingProbeJob.RunAsync));
        span.GetTagItem("otel.status_code").ShouldBe("ok");
    }

    [Fact]
    public async Task Job_span_records_no_argument_values()
    {
        var argument = Guid.CreateVersion7();

        var spans = await RunProbeJobAsync(argument);

        var span = spans.ShouldHaveSingleItem();
        span.OperationName.ShouldNotContain(argument.ToString());
        span.TagObjects.ShouldAllBe(tag => !(tag.Value + "").Contains(argument.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<Activity>> RunProbeJobAsync(Guid argument)
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JobTelemetryFilter.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var effects = new ProbeJobEffects();
        await using var host = ProbeHost.New(Database, effects).WithHangfireServer();
        await host.StartAsync(TestContextToken.Value);

        var jobId = host.Scheduler.Enqueue<RecordingProbeJob>((job, ct) => job.RunAsync(argument, ct));
        await host.Observer.WaitForStateAsync(jobId, "Succeeded", Timeout);

        lock (spans)
        {
            return [.. spans];
        }
    }
}
