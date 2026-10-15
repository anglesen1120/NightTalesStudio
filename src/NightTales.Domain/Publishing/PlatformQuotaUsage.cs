using NightTales.Domain.Common;

namespace NightTales.Domain.Publishing;

/// <summary>
/// One <c>platform_quota_usage</c> row (06.3.1-T1): the units one platform has spent on one Pacific calendar day
/// against that day's limit. The primary key is <c>(platform, quota_day)</c>, and the row is a platform counter, so it
/// has no <c>id</c>, is not <c>IUserOwned</c> and carries no tenant filter.
/// </summary>
/// <remarks>
/// This type is a shell for EF Core. The ledger drives every write with set-based SQL (a seed insert plus one
/// conditional update), so the domain holds no behaviour here; the business rules live in the statements and in the
/// CHECK constraints of <c>PlatformQuotaUsageConfiguration</c>.
/// </remarks>
public sealed class PlatformQuotaUsage : IAuditable
{
    private PlatformQuotaUsage()
    {
    }

    /// <summary>The platform whose quota the row counts. Only <c>youtube</c> exists in this task.</summary>
    public string Platform { get; private set; } = "youtube";

    /// <summary>The Pacific calendar day the row covers (<c>PacificQuotaDay.Day</c>).</summary>
    public DateOnly QuotaDay { get; private set; }

    /// <summary>The units reserved so far that day; never negative (CHECK).</summary>
    public int UnitsUsed { get; private set; }

    /// <summary>
    /// The day's limit, copied from the option when the row is seeded and kept for the rest of the day, so a
    /// mid-day option change never moves the limit of a row already in use; always positive (CHECK).
    /// </summary>
    public int UnitsLimit { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }
}
