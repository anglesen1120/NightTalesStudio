namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// The last known state of the blob store (09 README BD12), as the background prober observed it.
/// </summary>
/// <remarks>
/// Readiness must never touch S3 (BD16: "health endpoints read caches only"), so the probe writes this
/// and <c>/health/ready</c> reads it. <see cref="ErrorCode"/> is a closed, non-sensitive code — never a
/// message, a bucket name or a key.
/// </remarks>
/// <param name="IsHealthy">Whether the last probe reached the store and found its marker.</param>
/// <param name="CheckedAt">When the probe finished; the check ages the value out against 3 × the interval.</param>
/// <param name="ErrorCode">The closed code of the last failure, or <see langword="null"/> when healthy.</param>
public sealed record StorageHealth(bool IsHealthy, DateTimeOffset CheckedAt, string? ErrorCode)
{
    /// <summary>The state before the first probe finished; it is stale by construction.</summary>
    public const string UnprobedErrorCode = "storage_unprobed";

    /// <summary>The code of a store that could not be reached or refused the operation.</summary>
    public const string UnavailableErrorCode = "storage_unavailable";

    /// <summary>The value every host starts with, so a probe-less host reports degraded rather than healthy.</summary>
    public static StorageHealth NotChecked { get; } =
        new(IsHealthy: false, DateTimeOffset.MinValue, UnprobedErrorCode);
}

/// <summary>
/// The singleton cache between the storage prober and the readiness check (09 README BD12, the
/// <c>INodeHealthCache</c> shape of 11's <c>NodeHealthPoller</c>).
/// </summary>
/// <remarks>
/// Both members are thread-safe: the prober writes on its own thread while a request thread reads.
/// </remarks>
public interface IStorageHealthCache
{
    /// <summary>The last observed state; never <see langword="null"/> (starts at <see cref="StorageHealth.NotChecked"/>).</summary>
    StorageHealth Current { get; }

    /// <summary>
    /// Publishes a new observation. It is not called <c>Set</c> because CA1716 forbids an interface
    /// member whose name is a reserved keyword in another .NET language.
    /// </summary>
    /// <param name="health">The observation to publish.</param>
    void Publish(StorageHealth health);
}
