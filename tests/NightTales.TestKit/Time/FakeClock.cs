using NightTales.Domain.Common;

namespace NightTales.TestKit.Time;

/// <summary>
/// Mutable <see cref="IClock"/> for tests (09 README D3, D13): integration tests move time explicitly
/// and never sleep.
/// </summary>
/// <param name="start">The initial <see cref="UtcNow"/>.</param>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    /// <summary>A fixed, obviously synthetic instant for tests that do not care about the value.</summary>
    public static DateTimeOffset DefaultStart { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a clock at <see cref="DefaultStart"/>.</summary>
    public FakeClock()
        : this(DefaultStart)
    {
    }

    public DateTimeOffset UtcNow { get; private set; } = start;

    /// <summary>Moves the clock forward (or backward for a negative span).</summary>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Pins the clock to <paramref name="at"/>.</summary>
    public void Set(DateTimeOffset at) => UtcNow = at;
}
