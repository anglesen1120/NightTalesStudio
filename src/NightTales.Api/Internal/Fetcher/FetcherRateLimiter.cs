using System.Threading.RateLimiting;
using NightTales.Application.Features.Research.Fetcher;

namespace NightTales.Api.Internal.Fetcher;

/// <summary>
/// The request budgets of the internal listener (02.5.2-T2, plan §5.2): 60 req/min per instance id, plus a global
/// ceiling over the whole internal pipeline. It is its own limiter and not the public <c>UseRateLimiter</c> policies: the
/// internal pipeline has none of the public middleware, and the per-worker partition key is the body's
/// <c>workerId</c>, which only the endpoint has parsed.
/// </summary>
/// <remarks>
/// The per-worker key space is bounded: an id that fails <see cref="WorkerIdRules.IsValid"/> shares the single
/// <c>-</c> partition, so a token holder cannot mint partitions (and their timers) by rotating strings. Valid ids are
/// still free to rotate, which is what the global limiter is for: <see cref="TryEnter"/> caps requests in flight and
/// requests per minute across every worker, and runs before the body is read. The global constants are fixed on
/// purpose (no configuration keys): 4 platform-wide leases and one fetcher fleet never need more.
/// </remarks>
public sealed class FetcherRateLimiter : IDisposable
{
    /// <summary>The most internal requests in flight at once; the next one is refused, never queued.</summary>
    public const int MaxConcurrentRequests = 8;

    /// <summary>The most internal requests per minute across all workers.</summary>
    public const int MaxRequestsPerMinuteGlobal = 300;

    private const string SharedPartition = "-";

    private readonly PartitionedRateLimiter<string> _perWorker;
    private readonly ConcurrencyLimiter _concurrency;
    private readonly FixedWindowRateLimiter _window;

    /// <summary>Creates the limiter with the configured per-worker budget.</summary>
    /// <param name="options">The validated internal-listener options.</param>
    public FetcherRateLimiter(FetcherInternalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var permits = options.MaxRequestsPerMinutePerWorker;
        _perWorker = PartitionedRateLimiter.Create<string, string>(workerId =>
            RateLimitPartition.GetFixedWindowLimiter(
                workerId,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        _concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = MaxConcurrentRequests,
            QueueLimit = 0,
        });
        _window = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = MaxRequestsPerMinuteGlobal,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>
    /// Takes one global in-flight slot and one global per-minute permit. Dispose the returned slot when the request ends.
    /// </summary>
    /// <param name="slot">The slot to dispose when the request finishes; <see langword="null"/> when refused.</param>
    /// <param name="retryAfterSeconds">When refused, how long the caller should wait, at least 1.</param>
    /// <returns><see langword="true"/> when the request may proceed.</returns>
    public bool TryEnter(out IDisposable? slot, out int retryAfterSeconds)
    {
        var concurrency = _concurrency.AttemptAcquire();
        if (!concurrency.IsAcquired)
        {
            concurrency.Dispose();
            slot = null;
            retryAfterSeconds = 1;

            return false;
        }

        using var window = _window.AttemptAcquire();
        if (!window.IsAcquired)
        {
            concurrency.Dispose();
            slot = null;
            retryAfterSeconds = RetryAfter(window);

            return false;
        }

        slot = concurrency;
        retryAfterSeconds = 0;

        return true;
    }

    /// <summary>Takes one permit of the per-worker budget; an invalid id uses the shared partition.</summary>
    /// <param name="workerId">The id from the request body, validated here only for its shape.</param>
    /// <param name="retryAfterSeconds">When refused, how long the caller should wait, at least 1.</param>
    /// <returns><see langword="true"/> when the request may proceed.</returns>
    public bool TryAcquire(string? workerId, out int retryAfterSeconds)
    {
        using var lease = _perWorker.AttemptAcquire(WorkerIdRules.IsValid(workerId) ? workerId! : SharedPartition);
        if (lease.IsAcquired)
        {
            retryAfterSeconds = 0;

            return true;
        }

        retryAfterSeconds = RetryAfter(lease);

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _perWorker.Dispose();
        _concurrency.Dispose();
        _window.Dispose();
    }

    private static int RetryAfter(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;
}
