using System.Diagnostics;

namespace NightTales.Api.Http;

/// <summary>
/// The timing floor of <c>POST /auth/forgot-password</c> (09 README §B10 WT40, 09.3.3-T1): both the
/// known-address and the unknown-address path finish in at least <see cref="Floor"/>, plus a jitter of at
/// most <see cref="MaxJitter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The floor is what keeps the endpoint from being an account oracle by stopwatch: the handler's own work
/// differs (a lookup plus a queue versus a lookup), and a client measuring the response would otherwise
/// learn whether the address exists (BD16). The jitter keeps the response time from being a constant a
/// caller could subtract.
/// </para>
/// <para>
/// It pads in the Api, after the command's transaction has committed: the delay must not hold a database
/// connection open, and the handler's outcome is already final. A client that disconnects cancels the
/// wait like any other request work.
/// </para>
/// <para>
/// Public because the timing contract is unit-tested from another assembly and this solution has no
/// <c>InternalsVisibleTo</c> (the same reason <c>ErrorCatalogue</c> is public).
/// </para>
/// </remarks>
public static class AuthTiming
{
    /// <summary>The fixed floor every forgot-password response is padded to (WT40: 300 ms).</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(300);

    /// <summary>The largest jitter added on top of the floor (WT40: ≤ 50 ms).</summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Waits until <see cref="Floor"/> plus a jitter has elapsed since <paramref name="startedTimestamp"/>
    /// (a <see cref="Stopwatch.GetTimestamp"/> reading taken when the endpoint began).
    /// </summary>
    /// <param name="startedTimestamp">The request's start reading.</param>
    /// <param name="cancellationToken">Cancels the wait when the client goes away.</param>
    public static async Task PadAsync(long startedTimestamp, CancellationToken cancellationToken)
    {
        var target = Floor + Jitter();
        var remaining = target - Stopwatch.GetElapsedTime(startedTimestamp);

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One jitter value in <c>[0, MaxJitter]</c>.</summary>
    public static TimeSpan Jitter() =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)MaxJitter.TotalMilliseconds + 1));
}
