using System.Diagnostics.Metrics;

namespace NightTales.Api.RateLimiting;

/// <summary>
/// The limiter's instruments (09.3.5-T1 design): <c>rate_limit_rejections_total{policy}</c>, exported
/// with the platform's other meters by 09.9.3-T1.
/// </summary>
/// <remarks>
/// <para>
/// One tag, and it carries a closed vocabulary — the four policy names, or
/// <see cref="UnattributedPolicy"/> for a rejection no endpoint policy can be attributed to. Nothing
/// else may become a label: a partition key is a hash of an address or a user id and would turn one
/// counter into an unbounded series (09 README BD16).
/// </para>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one
/// counter. A test attaches a <see cref="MeterListener"/> to exactly this host's meter instance.
/// </para>
/// </remarks>
public sealed class RateLimitMetrics : IDisposable
{
    /// <summary>The meter the limiter publishes on.</summary>
    public const string MeterName = "NightTales.RateLimiting";

    /// <summary>The counter of rejected requests, by policy.</summary>
    public const string RejectionsCounterName = "rate_limit_rejections_total";

    /// <summary>The tag carrying the policy name.</summary>
    public const string PolicyTagName = "policy";

    /// <summary>
    /// The tag value of a rejection whose request carried no rate-limit policy in its endpoint
    /// metadata (an endpoint limiter rejects only for a policy, so this should stay at zero).
    /// </summary>
    public const string UnattributedPolicy = "unattributed";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _rejections;

    /// <summary>Creates the meter and its instruments.</summary>
    public RateLimitMetrics()
    {
        _rejections = _meter.CreateCounter<long>(
            RejectionsCounterName,
            unit: "{request}",
            description: "Requests the rate limiter rejected, by policy.");
    }

    /// <summary>The host's meter instance, so a test listens on exactly this one.</summary>
    public Meter Meter => _meter;

    /// <summary>Counts one rejection of <paramref name="policy"/>.</summary>
    /// <param name="policy">The policy that rejected the request; null when none could be attributed.</param>
    public void RecordRejection(string? policy) =>
        _rejections.Add(1, new KeyValuePair<string, object?>(PolicyTagName, policy ?? UnattributedPolicy));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
