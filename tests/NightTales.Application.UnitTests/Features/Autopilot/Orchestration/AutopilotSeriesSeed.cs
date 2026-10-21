using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NightTales.Infrastructure.Persistence;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// The series state every paid autopilot step re-reads before it spends (task rows SEC-1, SEC-5, PF-5): an accepted
/// authorization with the series' pointer to it, and the paused / switched-off / archived flags the guard answers on.
/// </summary>
/// <remarks>
/// The step tests cannot reach <c>AutopilotStepSupport</c> (internal, and this solution has no
/// <c>InternalsVisibleTo</c>), so they drive the guard through a real step; what they need is a series that may spend,
/// which is what this helper writes — the same rows 02.8.3-T3's accept route writes. The series instance a test hands
/// to the step is the one this helper mutates, because the context carries that instance and the step reads it.
/// </remarks>
internal static class AutopilotSeriesSeed
{
    /// <summary>
    /// Inserts one authorization row for <paramref name="series"/> and points the (tracked) series at it, which is the
    /// state a step's <c>SeriesMaySpendAsync</c> guard requires before it dispatches.
    /// </summary>
    /// <param name="db">The scope whose series instance the step will read; the entity is attached to it.</param>
    /// <param name="series">The series the authorization belongs to.</param>
    /// <param name="at">The acceptance instant.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <param name="revokedAt">The revocation instant when the case needs a revoked revision.</param>
    /// <returns>The authorization id the series now points at.</returns>
    public static async Task<Guid> AcceptAsync(
        AppDbContext db,
        SeriesAggregate series,
        DateTimeOffset at,
        CancellationToken ct,
        DateTimeOffset? revokedAt = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(series);

        var id = await InsertAsync(db, series.Id, series.UserId, at, revokedAt, ct);

        Attach(db, series);
        Set(db, series, nameof(SeriesAggregate.CurrentAutopilotAuthorizationId), id);

        // The pointer lives on the series row, so it must be saved: a step opens its own scope and reads it from the
        // database, and an unsaved pointer leaves the run looking unauthorized.
        await db.SaveChangesAsync(ct);

        return id;
    }

    /// <summary>
    /// Inserts a live authorization belonging to <b>another</b> series of the same owner and writes it into
    /// <paramref name="series"/>' pointer, which is the shape S71 SEC-5 protects against: a valid revision of a
    /// different series.
    /// </summary>
    /// <param name="db">The scope whose series instance the step will read.</param>
    /// <param name="series">The series whose pointer is written.</param>
    /// <param name="otherSeriesId">The other series the revision belongs to.</param>
    /// <param name="at">The acceptance instant.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>The foreign authorization id.</returns>
    public static async Task<Guid> AcceptForeignAsync(
        AppDbContext db,
        SeriesAggregate series,
        Guid otherSeriesId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(series);

        var id = await InsertAsync(db, otherSeriesId, series.UserId, at, revokedAt: null, ct);

        Attach(db, series);
        Set(db, series, nameof(SeriesAggregate.CurrentAutopilotAuthorizationId), id);

        // The pointer lives on the series row, so it must be saved: a step opens its own scope and reads it from the
        // database, and an unsaved pointer leaves the run looking unauthorized.
        await db.SaveChangesAsync(ct);

        return id;
    }

    /// <summary>Records the §3.6 pause the series carries (reason in wire form, as <c>series.autopilot_pause_reason</c> holds it).</summary>
    /// <param name="db">The scope whose series instance the step will read.</param>
    /// <param name="series">The series to pause.</param>
    /// <param name="reason">The stored wire value of the trigger, e.g. <c>gate_failed_streak</c>.</param>
    /// <param name="at">The pause instant.</param>
    public static void Pause(AppDbContext db, SeriesAggregate series, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(series);

        Attach(db, series);
        Set(db, series, nameof(SeriesAggregate.AutopilotPausedAt), at);
        Set(db, series, nameof(SeriesAggregate.AutopilotPauseReason), reason);
    }

    /// <summary>Switches autopilot off on the series, the way <c>PUT /series/{id}/autopilot</c> does.</summary>
    /// <param name="db">The scope whose series instance the step will read.</param>
    /// <param name="series">The series to switch off.</param>
    public static void Disable(AppDbContext db, SeriesAggregate series)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(series);

        Attach(db, series);
        Set(db, series, nameof(SeriesAggregate.AutopilotEnabled), value: false);
    }

    /// <summary>Writes one mapped column of the tracked series (its hands-on setters are 02.8.3-T3's).</summary>
    private static void Set(AppDbContext db, SeriesAggregate series, string column, object? value) =>
        db.Entry(series).Property(column).CurrentValue = value;

    private static void Attach(AppDbContext db, SeriesAggregate series)
    {
        if (db.Entry(series).State == EntityState.Detached)
        {
            db.Attach(series);
        }
    }

    private static async Task<Guid> InsertAsync(
        AppDbContext db,
        Guid seriesId,
        Guid userId,
        DateTimeOffset at,
        DateTimeOffset? revokedAt,
        CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var version = 1;
        var textVersion = "ai.v1";
        var hash = new string('a', 64);
        var topic = "ghost";
        Guid? channelConnectionId = null;
        var cadence = "weekly";
        var privacy = "public";
        string? revokedStamp = revokedAt?.ToString("O", CultureInfo.InvariantCulture);

        // Interpolated, so every value travels as a parameter: the analyzer refuses a concatenated ExecuteSqlRaw here.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, text_hash,
                topic, channel_connection_id, cadence, privacy_status, accepted_at, revoked_at, created_at, updated_at)
            VALUES ({id}, {userId}, {seriesId}, {version}, {textVersion}, {hash}, {topic}, {channelConnectionId},
                {cadence}, {privacy}, {at}, CAST({revokedStamp} AS timestamptz), {at}, {at})
            """,
            ct).ConfigureAwait(false);

        return id;
    }
}
