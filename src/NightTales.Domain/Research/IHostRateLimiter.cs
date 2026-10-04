namespace NightTales.Domain.Research;

/// <summary>The per-host politeness limiter (02 D11: one request per second per registrable domain).</summary>
public interface IHostRateLimiter
{
    /// <summary>Waits until a request to <paramref name="registrableDomain"/> is allowed, then records it.</summary>
    /// <param name="registrableDomain">The registrable domain the request goes to.</param>
    /// <param name="minInterval">The minimum gap since the previous request to the same domain.</param>
    /// <param name="ct">Cancels the wait.</param>
    ValueTask WaitAsync(string registrableDomain, TimeSpan minInterval, CancellationToken ct);
}
