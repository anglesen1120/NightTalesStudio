using System.Threading.RateLimiting;

namespace NightTales.Api.Internal.Fetcher;

/// <summary>
/// The per-fetcher-instance request budget of the internal listener (02.5.2-T2, plan §5.2: 60 req/min per instance id).
/// It is its own limiter and not the public <c>UseRateLimiter</c> policies: the internal pipeline has none of the public
/// middleware, and the partition key is the body's <c>workerId</c>, which only the endpoint has parsed.
/// </summary>
/// <remarks>
/// The key is already bounded (the validator caps <c>workerId</c> at 64 characters of a closed alphabet) and only a
/// holder of the token reaches the limiter; idle partitions are reclaimed by the limiter itself.
/// </remarks>
public sealed class FetcherRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    /// <summary>Creates the limiter with the configured budget.</summary>
    /// <param name="options">The validated internal-listener options.</param>
    public FetcherRateLimiter(FetcherInternalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var permits = options.MaxRequestsPerMinutePerWorker;
        _limiter = PartitionedRateLimiter.Create<string, string>(workerId =>
            RateLimitPartition.GetFixedWindowLimiter(
                workerId,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }

    /// <summary>Takes one permit for <paramref name="workerId"/>.</summary>
    /// <param name="workerId">The partition key.</param>
    /// <param name="retryAfterSeconds">When refused, how long the caller should wait, at least 1.</param>
    /// <returns><see langword="true"/> when the request may proceed.</returns>
    public bool TryAcquire(string workerId, out int retryAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(workerId);

        using var lease = _limiter.AttemptAcquire(workerId);
        if (lease.IsAcquired)
        {
            retryAfterSeconds = 0;

            return true;
        }

        retryAfterSeconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;

        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}
