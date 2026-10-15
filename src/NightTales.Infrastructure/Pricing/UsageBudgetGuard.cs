using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Pricing;

/// <summary>
/// The <see cref="IBudgetGuard"/> over <c>usage_records</c> and the <see cref="IOpenBoundsReader"/> (02.2.2-T2):
/// denies when <c>spent + open bounds + worst case</c> passes the daily (UTC day) or the monthly (UTC calendar
/// month) cap with <c>spend_cap_reached</c>.
/// </summary>
/// <remarks>
/// <para>
/// The spend is platform-wide, so the sum bypasses the tenant filter and returns one number only (the type is on
/// the D10 allow-list). It is read on the read model in its own transaction (D14).
/// </para>
/// <para>
/// Concurrency (TL review T61): before reading, the guard takes <c>pg_advisory_xact_lock(hashtext('budget_guard'))</c>
/// on the write context. Inside a command that is the command's transaction, so the lock lives until the run row
/// commits and the next check sees its open bound; outside a transaction the lock releases at once and the check
/// is merely best effort.
/// </para>
/// </remarks>
/// <param name="readDb">The read model.</param>
/// <param name="openBounds">The open worst-case bounds.</param>
/// <param name="db">The write context whose transaction carries the advisory lock.</param>
/// <param name="options">The bound <c>Budget</c> section.</param>
/// <param name="clock">The application clock.</param>
public sealed class UsageBudgetGuard(
    IReadDb readDb,
    IOpenBoundsReader openBounds,
    IAppDbContext db,
    BudgetOptions options,
    IClock clock) : IBudgetGuard
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
    {
        ArgumentNullException.ThrowIfNull(worstCaseUsd);

        if (!options.Enabled)
        {
            return Result.Success;
        }

        await db.Database
            .ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext('budget_guard'))", ct)
            .ConfigureAwait(false);

        var instant = clock.UtcNow;
        var now = instant.UtcDateTime;
        var dayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var open = await openBounds.SumOpenWorstCaseUsdAsync(ct, excludingRunId).ConfigureAwait(false);
        var spentToday = await SpentSinceAsync(dayStart, ct).ConfigureAwait(false);
        var spentThisMonth = await SpentSinceAsync(monthStart, ct).ConfigureAwait(false);

        var bound = worstCaseUsd.WorstCaseUsd;
        var dayBreached = spentToday + open + bound > options.MaxPlatformCostUsdPerDay;
        var monthBreached = spentThisMonth + open + bound > options.MaxPlatformCostUsdPerMonth;
        if (dayBreached || monthBreached)
        {
            var resetAt = monthBreached ? monthStart.AddMonths(1) : dayStart.AddDays(1);

            return Error.Failure(
                ErrorCodes.SpendCapReached,
                "The platform's spending limit has been reached; try again later.",
                new Dictionary<string, object> { [ErrorMetadata.RetryAfterSeconds] = SecondsUntil(resetAt, instant) });
        }

        return Result.Success;
    }

    private async Task<decimal> SpentSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var total = await readDb
            .Query<UsageRecord>()
            .IgnoreQueryFilters([TenantFilterConvention.FilterName])
            .Where(record => record.CreatedAt >= since)
            .SumAsync(record => (decimal?)record.CostUsd, ct)
            .ConfigureAwait(false);

        return total ?? 0m;
    }

    private static int SecondsUntil(DateTimeOffset resetAt, DateTimeOffset now)
    {
        var ticks = (resetAt - now).Ticks;
        var seconds = (ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;

        return (int)Math.Max(1L, seconds);
    }
}
