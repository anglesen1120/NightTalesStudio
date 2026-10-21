using System.Diagnostics.Metrics;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// The blob store's counters (09.7.2 plan §5: <c>blob_store_operations_total{op,outcome}</c>), exported
/// by 09.9.3-T1 like every other meter of the platform.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one
/// counter (the <c>AuthMetrics</c> rule). Both tags are closed vocabularies — an operation name
/// (<c>put</c>, <c>read</c>, <c>delete</c>, <c>exists</c>, <c>list</c>, <c>presign</c>) and an outcome
/// (<c>ok</c>, <c>failed</c>, <c>denied</c>) — so a bucket, a key, a signature or a credential can
/// never become a tag value.
/// </para>
/// <para>
/// Only the denied outcome is named in the task file (BD16), but every operation is counted: an outage
/// that only ever produced <c>failed</c> would otherwise be invisible in a dashboard that looks for the
/// counter without a filter.
/// </para>
/// </remarks>
public sealed class StorageMetrics : IDisposable
{
    /// <summary>The meter every counter of this task publishes on.</summary>
    public const string MeterName = "NightTales.Storage";

    /// <summary>The operations counter.</summary>
    public const string OperationsCounterName = "blob_store_operations_total";

    /// <summary>The tag carrying the operation name.</summary>
    public const string OperationTagName = "op";

    /// <summary>The tag carrying the outcome.</summary>
    public const string OutcomeTagName = "outcome";

    /// <summary>The outcome of an operation the store completed.</summary>
    public const string OkOutcome = "ok";

    /// <summary>The outcome of an operation the store failed — unreachable, timed out or refused.</summary>
    public const string FailedOutcome = "failed";

    /// <summary>The outcome of an operation the store refused with <c>AccessDenied</c>.</summary>
    public const string DeniedOutcome = "denied";

    /// <summary>The operation name of <c>PutAsync</c>.</summary>
    public const string PutOperation = "put";

    /// <summary>The operation name of <c>OpenReadAsync</c>.</summary>
    public const string ReadOperation = "read";

    /// <summary>The operation name of <c>DeleteAsync</c>.</summary>
    public const string DeleteOperation = "delete";

    /// <summary>The operation name of <c>ExistsAsync</c>.</summary>
    public const string ExistsOperation = "exists";

    /// <summary>The operation name of <c>ListKeysAsync</c>.</summary>
    public const string ListOperation = "list";

    /// <summary>The operation name of <c>CreateReadUrl</c>.</summary>
    public const string PresignOperation = "presign";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _operations;

    /// <summary>Creates the meter and its counter.</summary>
    public StorageMetrics()
    {
        _operations = _meter.CreateCounter<long>(
            OperationsCounterName,
            "1",
            "Blob store operations by operation and outcome.");
    }

    /// <summary>Counts one finished operation.</summary>
    /// <param name="operation">The closed operation name.</param>
    /// <param name="outcome">The closed outcome.</param>
    public void Operation(string operation, string outcome) =>
        _operations.Add(
            1,
            new KeyValuePair<string, object?>(OperationTagName, operation),
            new KeyValuePair<string, object?>(OutcomeTagName, outcome));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
