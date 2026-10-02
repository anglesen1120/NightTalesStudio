using System.Diagnostics;
using Hangfire.Common;
using Hangfire.Server;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// One span per Hangfire job attempt (09 README BD12, 09.9.3-T1). The span is named
/// <c>job {queue}:{method}</c> and carries the attempt number and the outcome — never an argument value,
/// because D12 puts ids only in job arguments and a span is an export surface.
/// </summary>
/// <remarks>
/// The filter is a server filter, so it runs wherever a Hangfire server runs (the Worker). The Api's
/// dashboard registration shares the provider but executes no job.
/// </remarks>
public sealed class JobTelemetryFilter : IServerFilter
{
    /// <summary>The activity source the tracer provider listens to (<c>AddSource</c>).</summary>
    public const string ActivitySourceName = "NightTales.Hangfire";

    /// <summary>The key under which the running span travels between the two filter callbacks.</summary>
    private const string ActivityItemKey = "nts.job.activity";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>The span name of a job: queue and method name only.</summary>
    /// <param name="queue">The job's queue, or <see langword="null"/> for the default queue.</param>
    /// <param name="jobName">The job method's name.</param>
    public static string SpanName(string? queue, string jobName) => $"job {queue ?? "default"}:{jobName}";

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var job = context.BackgroundJob.Job;
        var activity = Source.StartActivity(SpanName(job.Queue, job.Method.Name), ActivityKind.Consumer);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("job.queue", job.Queue ?? "default");
        activity.SetTag("job.name", job.Method.Name);
        activity.SetTag("job.attempt", context.GetJobParameter<int>("RetryCount") + 1);
        context.Items[ActivityItemKey] = activity;
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Items.TryGetValue(ActivityItemKey, out var item) || item is not Activity activity)
        {
            return;
        }

        if (context.Exception is { } exception && !context.ExceptionHandled)
        {
            // The type name only: a message can carry payload text.
            activity.SetTag("otel.status_code", "error");
            activity.SetTag("exception.type", exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error);
        }
        else
        {
            activity.SetTag("otel.status_code", "ok");
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        activity.Dispose();
    }
}

/// <summary>Hands <see cref="JobTelemetryFilter"/> to Hangfire's filter pipeline for every job.</summary>
public sealed class JobTelemetryFilterProvider : IJobFilterProvider
{
    private static readonly JobFilter[] Filters =
        [new JobFilter(new JobTelemetryFilter(), JobFilterScope.Global, order: null)];

    /// <inheritdoc />
    public IEnumerable<JobFilter> GetFilters(Job job) => Filters;
}
