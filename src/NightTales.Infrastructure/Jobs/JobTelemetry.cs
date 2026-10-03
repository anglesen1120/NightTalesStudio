using System.Diagnostics;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// One span per Hangfire job attempt (09 README BD12, 09.9.3-T1). The span is named
/// <c>job {queue}:{method}</c> and carries the attempt number and the outcome — never an argument value,
/// because D12 puts ids only in job arguments and a span is an export surface.
/// </summary>
/// <remarks>
/// As a client filter it stores the enqueuing activity's W3C <c>traceparent</c>/<c>tracestate</c> as two job
/// parameters; as a server filter it restores them as the parent of the job span, so a request, the outbox
/// delivery it raised and the job that delivery enqueued share one trace. The filter runs wherever a
/// Hangfire client or server runs; the Api's dashboard registration shares the provider but executes no job.
/// </remarks>
public sealed class JobTelemetryFilter : IServerFilter, IClientFilter
{
    /// <summary>The activity source the tracer provider listens to (<c>AddSource</c>).</summary>
    public const string ActivitySourceName = "NightTales.Hangfire";

    /// <summary>The key under which the running span travels between the two filter callbacks.</summary>
    private const string ActivityItemKey = "nts.job.activity";

    /// <summary>The job parameters carrying the enqueuer's W3C trace context.</summary>
    private const string TraceParentParameter = "nts.trace_parent";
    private const string TraceStateParameter = "nts.trace_state";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>The span name of a job: queue and method name only.</summary>
    /// <param name="queue">The job's queue, or <see langword="null"/> for the default queue.</param>
    /// <param name="jobName">The job method's name.</param>
    public static string SpanName(string? queue, string jobName) => $"job {queue ?? "default"}:{jobName}";

    /// <inheritdoc />
    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var current = Activity.Current;
        if (current is not { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
        {
            return;
        }

        context.SetJobParameter(TraceParentParameter, traceParent);
        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            context.SetJobParameter(TraceStateParameter, current.TraceStateString);
        }
    }

    /// <inheritdoc />
    public void OnCreated(CreatedContext context)
    {
    }

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var job = context.BackgroundJob.Job;
        var name = SpanName(job.Queue, job.Method.Name);
        var traceParent = context.GetJobParameter<string>(TraceParentParameter);
        var activity = !string.IsNullOrEmpty(traceParent)
            && ActivityContext.TryParse(
                traceParent, context.GetJobParameter<string>(TraceStateParameter), isRemote: true, out var parent)
                ? Source.StartActivity(name, ActivityKind.Consumer, parent)
                : Source.StartActivity(name, ActivityKind.Consumer);
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
