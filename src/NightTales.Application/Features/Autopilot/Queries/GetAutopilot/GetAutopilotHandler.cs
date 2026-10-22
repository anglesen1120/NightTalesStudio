using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Dtos;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Queries.GetAutopilot;

/// <summary>
/// Builds the autopilot page of one series (02.8.3-T2 task step 3): the stored settings, the pause state, the live
/// authorization, the text the owner may accept now, the channel and the next three slots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Ownership first (a foreign or missing series is the same 404, 09 README D10), then the read-only
/// covered-field comparison, then the render. The read performs no write: no revocation, no pointer change and no
/// orchestrator call, so the page may poll it (contract D-2).
/// </para>
/// <para>
/// <b>Nullable members, and why.</b> A series with no <c>autopilot_settings</c> document, or with no
/// <c>default_channel_id</c>, has no settings to show and no channel title to put in a consent sentence; the DTO
/// answers <see langword="null"/> for those and for the text, and <c>requiredAuthorization</c> is
/// <see langword="true"/>, so the page renders the empty form rather than an invented consent sentence (plan §3.7).
/// </para>
/// <para>
/// <b>The slots need no run rows.</b> They are the shipped slot planner's next three instants plus
/// <c>AutopilotTiming.Compute</c>'s start, so the schedule is visible before the first tick; a planned run's state is
/// attached when one exists at that instant.
/// </para>
/// </remarks>
/// <param name="db">The tenant-filtered read model.</param>
/// <param name="slots">The shipped slot planner of plan §3.2 (never re-implemented here).</param>
/// <param name="options">The run policy the timing formula reads, as the plain value each host registers from its
/// bound <c>Autopilot:Run</c> section (<c>AddAutopilotRunOptions</c>): Application may not reference the options
/// binder, and the Api binds the same section as the Worker, so the preview and the planning agree.</param>
/// <param name="auditPolicy">The channel's YouTube audit state, which decides the pre-audit note.</param>
/// <param name="renderer">The single render point of the authorization text.</param>
/// <param name="gateOptions">The bound <c>Autopilot:Gates</c> section, which owns the dry-run switch.</param>
/// <param name="clock">The clock the "future" of a slot and the text's freshness are read from.</param>
public sealed class GetAutopilotHandler(
    IAppDbContext db,
    ISlotCalculator slots,
    AutopilotRunOptions options,
    IChannelAuditPolicy auditPolicy,
    AuthorizationTextRenderer renderer,
    AutopilotGateOptions gateOptions,
    IClock clock) : IQueryHandler<GetAutopilotQuery, ErrorOr<AutopilotDto>>
{
    /// <summary>How many future slots the page previews (task §DTOs: `nextSlots`).</summary>
    private const int PreviewedSlots = 3;

    /// <summary>The largest number of run rows read to label those slots.</summary>
    private const int PreviewedRunRows = 20;

    /// <inheritdoc />
    public async ValueTask<ErrorOr<AutopilotDto>> Handle(
        GetAutopilotQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == query.Id, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            // D10: another owner's series and an absent one answer the same 404.
            return Errors.NotFound();
        }

        var settings = AutopilotSettingsValidator.Validate(series.AutopilotSettings);
        var usable = settings.IsError ? null : settings.Value;
        var now = clock.UtcNow;

        var channel = series.DefaultChannelId is { } channelId
            ? await db.ChannelConnections
                .SingleOrDefaultAsync(candidate => candidate.Id == channelId, cancellationToken)
                .ConfigureAwait(false)
            : null;

        var channelRef = channel is null
            ? null
            : new ChannelRefDto(
                channel.Id,
                channel.DisplayName,
                JsonNamingPolicy.SnakeCaseLower.ConvertName(channel.Status.ToString()));

        var authorization = series.CurrentAutopilotAuthorizationId is { } authorizationId
            ? await db.SeriesAutopilotAuthorizations
                .SingleOrDefaultAsync(candidate => candidate.Id == authorizationId, cancellationToken)
                .ConfigureAwait(false)
            : null;

        // A pointer to a revoked row is a state no command leaves behind; it is answered as "no authorization" so
        // the page asks for a fresh accept instead of showing a dead row as current.
        if (authorization is { IsRevoked: true })
        {
            authorization = null;
        }

        var preAuditPending = channel is not null
            && await auditPolicy.IsPreAuditAsync(channel.Id, cancellationToken).ConfigureAwait(false);

        AuthorizationTextDto? authorizationText = null;
        if (usable is not null && channel is not null)
        {
            var locale = ResolveLocale(query.Locale, series.Language);
            var rendered = renderer.Render(new AuthorizationTextRequest(
                locale,
                series.Title,
                series.Topic,
                channel.DisplayName ?? string.Empty,
                usable,
                preAuditPending));

            if (!rendered.IsError)
            {
                authorizationText = new AuthorizationTextDto(
                    rendered.Value.TextVersion,
                    rendered.Value.Text,
                    rendered.Value.TextHash,
                    rendered.Value.Locale);
            }
        }

        var requiredAuthorization = authorization is null
            || usable is null
            || channel is null
            || AutopilotAuthorizationCoverage.FirstCoveredChange(
                authorization.Cadence,
                authorization.Topic,
                authorization.ChannelConnectionId,
                series.Topic,
                series.DefaultChannelId,
                usable,
                preAuditPending) is not null;

        return new AutopilotDto(
            series.AutopilotEnabled,
            usable is null ? null : AutopilotSettingsDto.From(usable),
            series.AutopilotPausedAt,
            series.AutopilotPauseReason,
            authorization is null
                ? null
                : new AuthorizationDto(
                    authorization.Version,
                    authorization.TextVersion,
                    authorization.TextHash,
                    authorization.AcceptedAt),
            authorizationText,
            requiredAuthorization,
            channelRef,
            // The gate profile's dry-run switch is the bound `Autopilot:Gates` option (02.8.2); it is a platform
            // setting, not a per-series one, so it is read here rather than stored on the series.
            GatesDryRun: gateOptions.DryRun,
            // No calibration status type exists yet (02.8.2-T3's `GateCalibrationStatus` is not in the codebase; the
            // run page omits its sibling `calibrationReport` for the same reason), so the remaining count has no true
            // source and is answered as 0 — a recorded gap, not a count of `gate_results`.
            GatesDryRunRemaining: 0,
            await NextSlotsAsync(series, usable, now, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The next three slots with the run state attached where a run already exists (task §DTOs: `nextSlots`).
    /// </summary>
    /// <param name="series">The series the slots belong to.</param>
    /// <param name="settings">The stored settings, or <see langword="null"/> when the series has none.</param>
    /// <param name="now">The clock reading the horizon and the start times are computed from.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The previewed slots; empty when there are no settings to plan from.</returns>
    private async Task<IReadOnlyList<NextSlotDto>> NextSlotsAsync(
        SeriesAggregate series,
        AutopilotSettings? settings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (settings is null)
        {
            return [];
        }

        var instants = slots.NextSlots(
            settings.Cadence,
            now,
            AutopilotSettingsValidator.HorizonDays(settings),
            settings.DailyCap);

        if (instants.Count == 0)
        {
            return [];
        }

        var upcoming = await db.AutopilotRuns
            .Where(run => run.SeriesId == series.Id && run.SlotAt >= now)
            .OrderBy(run => run.SlotAt)
            .Take(PreviewedRunRows)
            .Select(run => new { run.SlotAt, run.State })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bySlot = new Dictionary<DateTimeOffset, string>(upcoming.Count);
        foreach (var run in upcoming)
        {
            bySlot[run.SlotAt] = JsonNamingPolicy.SnakeCaseLower.ConvertName(run.State.ToString());
        }

        var timingOptions = options.ToTimingOptions();
        var preview = new List<NextSlotDto>(PreviewedSlots);
        foreach (var slotAt in instants.Take(PreviewedSlots))
        {
            preview.Add(new NextSlotDto(
                slotAt,
                AutopilotTiming.Compute(slotAt, settings, timingOptions, now).StartAt,
                bySlot.TryGetValue(slotAt, out var state) ? state : FreeSlotState));
        }

        return preview;
    }

    /// <summary>The state of a previewed slot no run row occupies yet.</summary>
    private const string FreeSlotState = "free";

    /// <summary>
    /// The locale the text is rendered in: the request's, when it is one the renderer ships, else the series' own
    /// content language (the only one its owner is guaranteed to read).
    /// </summary>
    /// <param name="requested">The <c>locale</c> query value, or <see langword="null"/>.</param>
    /// <param name="language">The series' content language.</param>
    /// <returns><c>vi</c> or <c>en</c>.</returns>
    private static string ResolveLocale(string? requested, Language language)
    {
        if (requested is not null
            && AuthorizationTextRenderer.Locales.Contains(requested, StringComparer.Ordinal))
        {
            return requested;
        }

        return language == Language.Vi ? "vi" : "en";
    }
}
