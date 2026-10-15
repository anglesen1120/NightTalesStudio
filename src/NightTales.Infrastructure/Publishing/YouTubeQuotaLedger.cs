using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Common.Errors;
using NightTales.Application.Publishing;
using NightTales.Application.Publishing.Quota;
using NightTales.Application.Telemetry;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Publishing;

/// <summary>
/// The <see cref="IQuotaLedger"/> over <c>platform_quota_usage</c> (06.3.1-T1). One row per platform and Pacific
/// quota day holds the units used and the day's limit; a reservation is one atomic conditional increment, so two
/// concurrent publishes can never both take the last unit.
/// </summary>
/// <remarks>
/// <para>
/// The row is seeded in the same call (<c>INSERT … ON CONFLICT DO NOTHING</c>) so the first publish of a day needs no
/// separate setup. The limit is read from the options at seed time only: a later change to
/// <c>YouTube:Quota:UnitsPerDay</c> does not move an existing day.
/// </para>
/// <para>
/// The ledger runs on the caller's scoped <see cref="IAppDbContext"/> and so joins its ambient transaction, if any.
/// The table is a platform counter, not <c>IUserOwned</c>, so no tenant filter applies.
/// </para>
/// </remarks>
public sealed partial class YouTubeQuotaLedger(
    IAppDbContext db,
    IOptionsMonitor<YouTubeOptions> options,
    NightTalesMetrics metrics,
    IClock clock,
    ILogger<YouTubeQuotaLedger> logger) : IQuotaLedger
{
    private const string GaugeSavepoint = "youtube_quota_gauge";

    /// <inheritdoc />
    public async Task<ErrorOr<QuotaReservation>> TryReserveAsync(string platform, DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        var day = PacificQuotaDay.Day(now);
        var limit = options.CurrentValue.Quota.UnitsPerDay;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO platform_quota_usage (platform, quota_day, units_used, units_limit, created_at, updated_at)
            VALUES ({platform}, {day}, 0, {limit}, {now}, {now})
            ON CONFLICT (platform, quota_day) DO NOTHING
            """,
            ct).ConfigureAwait(false);

        // One statement decides the race: the predicate and the increment are evaluated under the row lock, so
        // exactly one of two concurrent reservers of the last unit sees units_used < units_limit.
        var reserved = await db.PlatformQuotaUsage
            .Where(row => row.Platform == platform && row.QuotaDay == day && row.UnitsUsed < row.UnitsLimit)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.UnitsUsed, row => row.UnitsUsed + 1)
                    .SetProperty(row => row.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (reserved == 0)
        {
            LogExhausted(logger, platform, day);

            return Error.Failure(
                code: ErrorCodes.YoutubeQuotaExhausted,
                description: "The YouTube upload quota for today is used up.",
                metadata: new Dictionary<string, object>
                {
                    [ErrorMetadata.RetryAfterSeconds] = RetryAfterSeconds(now),
                });
        }

        await PublishGaugeAsync(platform, day, ct).ConfigureAwait(false);

        return new QuotaReservation(platform, day);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> ReleaseAsync(QuotaReservation reservation, CancellationToken ct = default)
    {
        var nowUtc = clock.UtcNow;

        // A release of a unit that is no longer counted (missing row, or already at zero) is a no-op success: the
        // caller is undoing a publish that never happened, so it must not fail on the bookkeeping.
        await db.PlatformQuotaUsage
            .Where(row => row.Platform == reservation.Platform && row.QuotaDay == reservation.Day && row.UnitsUsed > 0)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.UnitsUsed, row => row.UnitsUsed - 1)
                    .SetProperty(row => row.UpdatedAt, nowUtc),
                ct)
            .ConfigureAwait(false);

        await PublishGaugeAsync(reservation.Platform, PacificQuotaDay.Day(nowUtc), ct).ConfigureAwait(false);

        return Result.Success;
    }

    /// <summary>
    /// Publishes the day's used/limit ratio on <c>youtube_quota_used</c>. The gauge is best effort and the whole step
    /// (read-back and recorder) is guarded: a failure is logged and never reaches the caller, because the reservation
    /// or release it follows has already succeeded. Only cancellation propagates.
    /// </summary>
    private async Task PublishGaugeAsync(string platform, DateOnly day, CancellationToken ct)
    {
        try
        {
            var (used, limit) = await ReadUsageAsync(platform, day, ct).ConfigureAwait(false);
            metrics.SetYoutubeQuotaUsed(limit == 0 ? 0d : (double)used / limit);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogGaugeFailed(logger, exception, platform, day);
        }
    }

    /// <summary>
    /// Reads the day's counters. PostgreSQL aborts a whole transaction on one failed statement, and the ledger joins
    /// the caller's ambient transaction, so under one the read runs inside a savepoint that is rolled back on failure:
    /// a failed read-back cannot poison the unit of work that holds the reservation. Without an ambient transaction
    /// the read is a plain autocommit statement.
    /// </summary>
    private async Task<(int Used, int Limit)> ReadUsageAsync(string platform, DateOnly day, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction;
        var guarded = transaction is { SupportsSavepoints: true };
        if (guarded)
        {
            await transaction!.CreateSavepointAsync(GaugeSavepoint, ct).ConfigureAwait(false);
        }

        try
        {
            var row = await db.PlatformQuotaUsage
                .AsNoTracking()
                .Where(usage => usage.Platform == platform && usage.QuotaDay == day)
                .Select(usage => new { usage.UnitsUsed, usage.UnitsLimit })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (guarded)
            {
                await transaction!.ReleaseSavepointAsync(GaugeSavepoint, ct).ConfigureAwait(false);
            }

            return (row?.UnitsUsed ?? 0, row?.UnitsLimit ?? 0);
        }
        catch when (guarded)
        {
            await transaction!.RollbackToSavepointAsync(GaugeSavepoint, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The whole seconds until the Pacific day rolls over, rounded up and at least 1, so a client that waits that long
    /// finds a fresh day.
    /// </summary>
    private static int RetryAfterSeconds(DateTimeOffset now)
    {
        var seconds = Math.Ceiling((PacificQuotaDay.NextResetAt(now) - now).TotalSeconds);

        return (int)Math.Max(1, seconds);
    }

    [LoggerMessage(EventId = 2370, Level = LogLevel.Information, Message = "publishing.quota.exhausted platform={Platform} quota_day={QuotaDay:yyyy-MM-dd}")]
    private static partial void LogExhausted(ILogger logger, string platform, DateOnly quotaDay);

    [LoggerMessage(EventId = 2371, Level = LogLevel.Warning, Message = "publishing.quota.gauge-failed platform={Platform} quota_day={QuotaDay:yyyy-MM-dd}")]
    private static partial void LogGaugeFailed(ILogger logger, Exception exception, string platform, DateOnly quotaDay);
}
