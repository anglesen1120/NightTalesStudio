using NightTales.Domain.Research;

namespace NightTales.TestKit.Fetching;

/// <summary><see cref="IHostRateLimiter"/> that never waits and records each call, so a fetch test does not sleep.</summary>
public sealed class FakeHostRateLimiter : IHostRateLimiter
{
    private readonly List<(string Domain, TimeSpan MinInterval)> _calls = [];

    /// <summary>The calls made, in order.</summary>
    public IReadOnlyList<(string Domain, TimeSpan MinInterval)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask WaitAsync(string registrableDomain, TimeSpan minInterval, CancellationToken ct)
    {
        lock (_calls)
        {
            _calls.Add((registrableDomain, minInterval));
        }

        return ValueTask.CompletedTask;
    }
}
