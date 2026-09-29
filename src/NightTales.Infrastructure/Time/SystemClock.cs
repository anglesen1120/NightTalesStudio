using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Time;

/// <summary>
/// The production <see cref="IClock"/> (09 README D3): UTC only, so a domain operation and its audit
/// stamps always share one time source. Tests replace it with <c>FakeClock</c>.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
