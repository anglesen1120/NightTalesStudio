namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>
/// The systemd seam under test control. <see cref="Available"/> is false by default, which is exactly what the
/// adapter sees on a host whose <c>systemctl</c> cannot answer (D6: the restart rule is disabled, the 404 rule
/// stays).
/// </summary>
internal sealed class FakeProcessStartReader : IMptProcessStartReader
{
    /// <summary>When false, every read answers null (rule disabled).</summary>
    internal bool Available { get; set; }

    /// <summary>The MPT service start time reported while <see cref="Available"/> is true.</summary>
    internal DateTimeOffset? Value { get; set; }

    /// <summary>How many times the poller asked for a refresh after a connection error.</summary>
    internal int RefreshCount { get; private set; }

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> ReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Available ? Value : null);

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshCount++;
        return ValueTask.FromResult(Available ? Value : null);
    }
}
