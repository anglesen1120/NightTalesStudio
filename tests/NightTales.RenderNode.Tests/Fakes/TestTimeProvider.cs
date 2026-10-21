namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// The clock the throttle and the submit path read, so a window of one minute can be crossed without waiting
/// (xunit.v3 has no time provider package on the pinned list, and this is the whole surface needed).
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    internal void Advance(TimeSpan delta) => _now = _now.Add(delta);

    /// <summary>Pins the clock to an instant.</summary>
    internal void Set(DateTimeOffset now) => _now = now;
}
