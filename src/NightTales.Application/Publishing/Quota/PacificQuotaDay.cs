namespace NightTales.Application.Publishing.Quota;

/// <summary>
/// The Pacific quota day of the platform upload quota (06.3.1-T1, plan §3, README D9): the day runs from local
/// midnight in <c>America/Los_Angeles</c>, and the offset of each instant comes from the zone's rules (never a fixed
/// -07:00), so the reset falls on the right UTC instant across the March and November transitions.
/// </summary>
public static class PacificQuotaDay
{
    private const string ZoneId = "America/Los_Angeles";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    /// <summary>The Pacific calendar date of <paramref name="utc"/>.</summary>
    public static DateOnly Day(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone).DateTime);

    /// <summary>
    /// The first instant of the Pacific day after the one containing <paramref name="utc"/>: local midnight of the next
    /// Pacific date, converted with the offset that applies at that local time. Always strictly after
    /// <paramref name="utc"/>. The result is a UTC instant (offset zero), the form the ETA contract requires.
    /// </summary>
    public static DateTimeOffset NextResetAt(DateTimeOffset utc)
    {
        var localMidnight = Day(utc).AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(localMidnight, ResolveOffset(localMidnight)).ToUniversalTime();
    }

    private static TimeSpan ResolveOffset(DateTime localMidnight)
    {
        // Ambiguous midnight (clocks fall back): the earlier instant is the one with the larger offset.
        if (Zone.IsAmbiguousTime(localMidnight))
        {
            return Zone.GetAmbiguousTimeOffsets(localMidnight).Max();
        }

        // Invalid midnight (clocks spring forward into it): the day starts at the transition, which is the
        // local time read with the offset in force just before the gap.
        if (Zone.IsInvalidTime(localMidnight))
        {
            return Zone.GetUtcOffset(localMidnight.AddHours(-1));
        }

        return Zone.GetUtcOffset(localMidnight);
    }
}
