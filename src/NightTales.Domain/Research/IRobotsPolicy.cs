namespace NightTales.Domain.Research;

/// <summary>The robots.txt port (02 D11, RFC 9309 subset of the 02.5.2-T0 spike).</summary>
public interface IRobotsPolicy
{
    /// <summary>Whether <paramref name="url"/> may be fetched, and the interval to keep between requests to its host.</summary>
    /// <param name="url">The absolute http(s) URL of the page about to be fetched.</param>
    /// <param name="ct">Cancels the lookup; cancellation propagates.</param>
    /// <returns>The decision. A robots failure is a denial, never an exception.</returns>
    ValueTask<RobotsDecision> IsAllowedAsync(Uri url, CancellationToken ct);
}
