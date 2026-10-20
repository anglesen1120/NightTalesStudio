using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Planning;

/// <summary>
/// Turns each enabled, unpaused series' cadence into <c>autopilot_runs</c> rows (02.8.3 plan §3.2/§3.4, §7.1,
/// task file step 4): exactly one row per slot, <c>UNIQUE (series_id, slot_at)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only ids cross the tenant boundary.</b> The candidate series come from
/// <see cref="ISystemWorkQuery.PendingAsync{T}"/> as <c>(id, user_id)</c> pairs; each pair is then processed in
/// its own DI scope, pinned to the owner with <c>ISystemContext.For(userId)</c> <em>before</em> the first
/// <c>DbContext</c> use (09 README D10). There is no unfiltered read here and no
/// <c>IgnoreQueryFilters</c> anywhere in this feature.
/// </para>
/// <para>
/// <b>The unique index is the final arbiter.</b> Slots the series already has are skipped by a read; the insert
/// still catches the provider's <c>23505</c> and treats it as "already planned", because a second tick can
/// overlap the first. One bad series never stops the others: every failure is logged by id and type only.
/// </para>
/// <para>
/// <b>The daily cap is per channel.</b> <see cref="AutopilotSettings.DailyCap"/> is applied twice: by
/// <see cref="ISlotCalculator.PlanSlots"/> inside one series, and here across every series that shares the
/// authorization's channel connection on that local day (task file §Failure handling: "the second series' slot
/// is skipped that day"). The count of that local day is read once per day from the rows already in the table
/// and then kept in memory for the rest of the tick.
/// </para>
/// <para>
/// <b>No authorization is not a pause.</b> A series without a current, unrevoked authorization still gets its
/// rows; each one starts in <see cref="AutopilotRunState.AwaitingAuthorization"/> (plan §3.5 row
/// <c>planned → awaiting_authorization</c>, task file failure table: "the series is not paused").
/// </para>
/// </remarks>
public sealed partial class AutopilotSlotPlanner(
    ISystemWorkQuery systemWork,
    IServiceScopeFactory scopes,
    ISlotCalculator slots,
    AutopilotRunOptions options,
    AutopilotRunMetrics metrics,
    ILogger<AutopilotSlotPlanner> logger)
{
    /// <summary>
    /// How many candidate series one system read yields. Deliberately far above any real deployment: the port's
    /// read is a bare <c>Take</c> with no cursor, and the eligible-series set never shrinks, so a small batch
    /// would starve every series past the limit forever instead of draining it (unlike the due-run scan, whose
    /// set shrinks as runs advance). Only ids and owner ids cross the boundary, a few KB per thousand series.
    /// Hitting the limit exactly is logged, because that is the first sign of starvation.
    /// </summary>
    public const int SeriesBatchSize = 10_000;

    /// <summary>
    /// Plans every slot of every eligible series in the horizon (plan §3.2) and returns how many rows it
    /// created.
    /// </summary>
    /// <param name="now">The tick's clock reading, read once.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The number of <c>autopilot_runs</c> rows inserted by this pass.</returns>
    public async Task<int> PlanAsync(DateTimeOffset now, CancellationToken ct)
    {
        var series = new List<(Guid Id, Guid UserId)>();

        await foreach (var pair in systemWork
            .PendingAsync<SeriesAggregate>(Eligible(), SeriesBatchSize, ct)
            .ConfigureAwait(false))
        {
            series.Add(pair);
        }

        if (series.Count >= SeriesBatchSize)
        {
            // The read is a bare Take: a full batch means at least this many eligible series and possibly more,
            // which the next tick cannot distinguish from the same set. Make it visible before it starves.
            LogSeriesScanTruncated(series.Count);
        }

        var channelDays = new ChannelDayCounts();
        var created = 0;

        foreach (var (seriesId, userId) in series)
        {
            try
            {
                created += await PlanSeriesAsync(seriesId, userId, now, channelDays, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogSeriesFailed(seriesId, exception.GetType().Name);
            }
        }

        LogCompleted(series.Count, created);

        return created;
    }

    /// <summary>
    /// The eligible-series predicate: autopilot on, not paused by the owner and not archived (task file
    /// <c>PlanJob_SkipsPausedAndArchivedSeries</c>). The paused check is on the series, never on a run: a paused
    /// run is the owner's hold on one slot.
    /// </summary>
    /// <returns>The filter for <see cref="ISystemWorkQuery.PendingAsync{T}"/>.</returns>
    public static System.Linq.Expressions.Expression<Func<SeriesAggregate, bool>> Eligible() =>
        series => series.AutopilotEnabled && series.AutopilotPausedAt == null && series.ArchivedAt == null;

    private async Task<int> PlanSeriesAsync(
        Guid seriesId,
        Guid userId,
        DateTimeOffset now,
        ChannelDayCounts channelDays,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();

        // Before any DbContext in this scope: every user-owned row reads against this owner from here on.
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);

        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == seriesId, ct)
            .ConfigureAwait(false);

        // Re-checked under the filter: the pair list is a snapshot, and the owner may have paused the series
        // between the scan and this scope.
        if (series is not { AutopilotEnabled: true, AutopilotPausedAt: null, ArchivedAt: null })
        {
            return 0;
        }

        var parsed = AutopilotSettingsValidator.Validate(series.AutopilotSettings);
        if (parsed.IsError)
        {
            metrics.SlotSkipped(AutopilotRunMetrics.SettingsInvalidReason);
            LogSettingsInvalid(series.Id, parsed.FirstError.Code);

            return 0;
        }

        var settings = parsed.Value;

        if (!TryResolveZone(settings.Cadence.TimeZoneId, out var zone))
        {
            metrics.SlotSkipped(AutopilotRunMetrics.SettingsInvalidReason);
            LogSettingsInvalid(series.Id, ErrorCodes.AutopilotSettingsInvalid);

            return 0;
        }

        var authorization = await CurrentAuthorizationAsync(db, series, ct).ConfigureAwait(false);

        // The plan's cap unit is the series' channel (`plan.md` §3.2: "when two series share a channel
        // (`series.default_channel_id`)"), which the revocation rule keeps equal to the live authorization's
        // connection; the authorization is only the fall-back for a series whose default channel is unset.
        var channelId = series.DefaultChannelId ?? authorization?.ChannelConnectionId;
        var horizonDays = AutopilotSettingsValidator.HorizonDays(settings);
        var planned = slots.PlanSlots(settings.Cadence, now, horizonDays, settings.DailyCap);
        var existing = (await db.AutopilotRuns
                .AsNoTracking()
                .Where(run => run.SeriesId == series.Id && run.SlotAt >= now)
                .Select(run => run.SlotAt)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet();

        var timingOptions = options.ToTimingOptions();
        var created = 0;

        foreach (var slot in planned)
        {
            if (slot.At <= now)
            {
                // The calculator returns only future slots; this is the defence that keeps a rounded instant from
                // becoming a run for a slot the upload job can no longer make (task file §Failure handling).
                metrics.SlotSkipped(AutopilotRunMetrics.SlotInPastReason);
                continue;
            }

            if (existing.Contains(slot.At))
            {
                continue;
            }

            if (channelId is { } channel
                && !await HasChannelCapacityAsync(db, channel, settings, slot.At, zone, channelDays, ct)
                    .ConfigureAwait(false))
            {
                metrics.SlotSkipped(AutopilotRunMetrics.DailyCapReason);
                continue;
            }

            if (slot.Dst != SlotDstAdjustment.None)
            {
                metrics.SlotDstAdjusted(
                    settings.Cadence.TimeZoneId,
                    slot.Dst == SlotDstAdjustment.GapShifted
                        ? AutopilotRunMetrics.DstGapKind
                        : AutopilotRunMetrics.DstOverlapKind);
            }

            var timing = AutopilotTiming.Compute(slot.At, settings, timingOptions);
            var run = AutopilotRun.Plan(userId, series.Id, slot.At, timing.StartAt, authorization?.Id, now);

            if (authorization is null)
            {
                // Plan §3.5 row `planned → awaiting_authorization`: the run exists, the series keeps planning,
                // and the owner's re-accept moves it on (the step handler re-checks this state every tick).
                run.AdvanceTo(AutopilotRunState.AwaitingAuthorization, AutopilotRunStep.Authorization, now);
            }

            db.AutopilotRuns.Add(run);
            existing.Add(slot.At);
            channelDays.Increment(channelId, zone, slot.At);
            created++;
        }

        if (created == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (AutopilotSlotConflict.IsSlotUniqueViolation(exception))
        {
            // A concurrent tick planned the same slot first: the index is the arbiter and this is not an error.
            LogAlreadyPlanned(series.Id);
        }

        return created;
    }

    /// <summary>The series' current authorization, when it points at a row that still stands.</summary>
    private static async Task<SeriesAutopilotAuthorization?> CurrentAuthorizationAsync(
        IAppDbContext db,
        SeriesAggregate series,
        CancellationToken ct)
    {
        if (series.CurrentAutopilotAuthorizationId is not { } authorizationId)
        {
            return null;
        }

        return await db.SeriesAutopilotAuthorizations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                authorization => authorization.Id == authorizationId
                    && authorization.SeriesId == series.Id
                    && authorization.RevokedAt == null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the slot's local day still has room on that channel: the rows already stored for the channel's
    /// series on that local day, plus the slots this tick already accepted, must stay below the cap.
    /// </summary>
    /// <remarks>
    /// <c>PlanJob_RespectsDailyCapPerChannel</c> (integration, later wave) must seed the second series with
    /// <c>DefaultChannelId</c> set to the shared channel while this series' <c>ChannelConnectionId</c>-bearing
    /// authorization differs, or a seed that sets both fields equal passes on the old, inconsistent query too.
    /// </remarks>
    private static async Task<bool> HasChannelCapacityAsync(
        IAppDbContext db,
        Guid channelId,
        AutopilotSettings settings,
        DateTimeOffset slotAt,
        TimeZoneInfo zone,
        ChannelDayCounts channelDays,
        CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slotAt, zone).DateTime);

        if (channelDays.TryGet(channelId, day, out var used))
        {
            return used < settings.DailyCap;
        }

        var (start, end) = LocalDay(day, zone);
        var seriesOfChannel = db.Series
            .Where(candidate => candidate.DefaultChannelId == channelId)
            .Select(candidate => candidate.Id);

        // Occupied means non-cancelled, exactly as `SlotCalculator.ApplyDailyCap` defines it: an owner-cancelled
        // run must not burn the channel's whole local day for the other series.
        var stored = await db.AutopilotRuns
            .CountAsync(
                run => seriesOfChannel.Contains(run.SeriesId)
                    && run.State != AutopilotRunState.CancelledByOwner
                    && run.SlotAt >= start
                    && run.SlotAt < end,
                ct)
            .ConfigureAwait(false);

        channelDays.Set(channelId, day, stored);

        return stored < settings.DailyCap;
    }

    /// <summary>
    /// The UTC bounds of one local calendar day in <paramref name="zone"/>. Midnight is read with
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/>, which answers the standard offset for a local time a
    /// DST gap removed instead of throwing; the day's own slots are resolved by the calculator, not here.
    /// </summary>
    private static (DateTimeOffset Start, DateTimeOffset End) LocalDay(DateOnly day, TimeZoneInfo zone)
    {
        var start = AtMidnight(day, zone);

        return (start, AtMidnight(day.AddDays(1), zone));
    }

    private static DateTimeOffset AtMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool TryResolveZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);

            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.Utc;

            return false;
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;

            return false;
        }
    }

    [LoggerMessage(
        EventId = 6533,
        Level = LogLevel.Warning,
        Message = "autopilot_plan_series_scan_truncated series={Series}")]
    private partial void LogSeriesScanTruncated(int series);

    [LoggerMessage(
        EventId = 6536,
        Level = LogLevel.Warning,
        Message = "autopilot_plan_settings_invalid seriesId={SeriesId} code={Code}")]
    private partial void LogSettingsInvalid(Guid seriesId, string code);

    [LoggerMessage(
        EventId = 6537,
        Level = LogLevel.Warning,
        Message = "autopilot_plan_series_failed seriesId={SeriesId} exceptionType={ExceptionType}")]
    private partial void LogSeriesFailed(Guid seriesId, string exceptionType);

    [LoggerMessage(
        EventId = 6538,
        Level = LogLevel.Information,
        Message = "autopilot_plan_already_planned seriesId={SeriesId}")]
    private partial void LogAlreadyPlanned(Guid seriesId);

    [LoggerMessage(
        EventId = 6539,
        Level = LogLevel.Information,
        Message = "autopilot_plan_completed series={Series} created={Created}")]
    private partial void LogCompleted(int series, int created);

    /// <summary>
    /// The per-tick count of slots accepted per <c>(channel, local day)</c>, seeded once from the table so a
    /// tick does not re-read the same day for every slot of every series.
    /// </summary>
    private sealed class ChannelDayCounts
    {
        private readonly Dictionary<(Guid Channel, DateOnly Day), int> _counts = [];

        public bool TryGet(Guid channel, DateOnly day, out int count) =>
            _counts.TryGetValue((channel, day), out count);

        public void Set(Guid channel, DateOnly day, int count) => _counts[(channel, day)] = count;

        public void Increment(Guid? channel, TimeZoneInfo zone, DateTimeOffset slotAt)
        {
            if (channel is not { } id)
            {
                return;
            }

            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slotAt, zone).DateTime);
            _counts[(id, day)] = _counts.TryGetValue((id, day), out var used) ? used + 1 : 1;
        }
    }
}

/// <summary>
/// The one place the slot insert recognises the table refusing it (02.8.3 plan §3.2 "insert missing runs,
/// <c>ON CONFLICT (series_id, slot_at) DO NOTHING</c>"): <c>UNIQUE (series_id, slot_at)</c> is the arbiter of
/// "already planned", so a second tick that overlaps the first is not an error.
/// </summary>
/// <remarks>
/// The SQLSTATE is read off the provider's <see cref="DbException"/> through the BCL only — this layer must not
/// reference Npgsql (the shape of <c>EpisodeIdeaConflict</c> and <c>FactSheetFlushConflict</c>). Only
/// <c>23505</c> is matched and not the constraint's name: the slot insert is the one write here and a primary
/// key collision of a UUIDv7 is not a case that can happen, so naming the index would only couple this file to
/// a name the EF configuration owns. Public because the unit tests of this feature exercise it and the solution
/// has no <c>InternalsVisibleTo</c>.
/// </remarks>
public static class AutopilotSlotConflict
{
    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation (<c>unique_violation</c>).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>Whether the flush was refused by a unique constraint — the slot index in this write path.</summary>
    /// <param name="exception">The refused statement's exception.</param>
    /// <returns><c>true</c> when the provider answered <c>23505</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static bool IsSlotUniqueViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }
}
