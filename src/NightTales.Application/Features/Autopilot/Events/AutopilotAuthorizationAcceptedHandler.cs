using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Authorization;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series.Events;

namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// Consumes <c>autopilot_authorization_accepted.v1</c> and lets the series' waiting runs continue (02.8.3-T2 note
/// S71-7): an acceptance is not a state the T1 step machine observes by itself — <c>AwaitingAuthorizationStep</c>
/// only re-checks once per tick — so the event is routed to the orchestrator, one call per open run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an event and not a direct call.</b> The accept command must commit before a run may advance: the run's
/// step reads the authorization pointer, and a run advanced inside the accepting transaction would read a pointer its
/// own transaction is still holding. The outbox delivery happens after the commit, which is exactly the ordering the
/// step needs.
/// </para>
/// <para>
/// <b>The consent also releases what its absence paused</b> (S77 review C-2/S-1). <c>SeriesMaySpendAsync</c> pauses a
/// producing run with <c>owner_paused</c> when the current authorization is missing or revoked, because the §3.5 table
/// has no <c>→ awaiting_authorization</c> row outside <c>planned</c>. A <c>paused</c> row is not due, so without this
/// step those runs would stay paused until the owner pressed <c>POST …/resume</c> — and that brake is not always at
/// hand (the series itself is not paused, so a client may show no resume affordance). The acceptance is the event that
/// brings the consent back, so the runs go back through <c>AutopilotRun.Resume</c> — the domain's own
/// <c>paused → previous state</c> edge, no state the table lacks — and then through the same advance loop as every
/// other open run. Only <c>owner_paused</c> runs of an enabled, unpaused, live series are released; the other
/// §3.6 reasons belong to 02.8.3-T4's trigger ladder.
/// </para>
/// <para>
/// <b>Only the current pointer releases</b> (S77 delta N-1). The delivery can arrive after the owner revoked this
/// authorization or after a newer acceptance superseded it, so the handler first checks that the event's row is still
/// <c>series.current_autopilot_authorization_id</c>. If it is not, the delivery is a no-op — nothing is released and
/// nothing is advanced — because consent the series no longer holds must not un-pause a run.
/// </para>
/// <para>
/// <b>The release is committed before the advance.</b> <c>IAutopilotRunOrchestrator.AdvanceAsync</c> opens its
/// own scope, so it only sees rows this delivery has saved. The save comes first, and a redelivery is harmless: a run
/// already released is no longer <c>paused</c>, and <c>AdvanceAsync</c> is a no-op on a run whose next step is not due.
/// This handler calls <c>SaveChangesAsync</c> itself: 09 README D5's "handlers never save" rule is about Mediator
/// handlers inside the unit-of-work pipeline, and an outbox delivery owns its scope (the <c>SendEmailJob</c> precedent).
/// </para>
/// <para>
/// <b>Idempotent.</b> Delivery is at least once; a redelivery re-advances runs that are already running or held, and
/// <c>AdvanceAsync</c> is a no-op on a run whose next step is not due. The release is the only row this delivery writes,
/// and only for a run still <c>paused</c>; the dispatcher owns the <c>outbox_messages</c> row and the orchestrator owns
/// the advance.
/// </para>
/// <para>
/// <b>Ids only in the log</b> (D19): the series, the authorization, the released and the advanced counts.
/// </para>
/// </remarks>
/// <param name="db">The tenant-filtered read model the open runs are selected from, and the tracked model the release writes.</param>
/// <param name="orchestrator">T1's advance entry point.</param>
/// <param name="logger">The outcome log of one delivery.</param>
public sealed partial class AutopilotAuthorizationAcceptedHandler(
    IAppDbContext db,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<AutopilotAuthorizationAcceptedHandler> logger)
    : IOutboxHandler<AutopilotAuthorizationAccepted>
{
    /// <summary>
    /// How many runs one delivery releases and advances: the same bound
    /// <see cref="AutopilotRunHold.MaxRunsPerPass"/> uses, which is above the widest plan a settings document can
    /// produce, so a normal series is served in one delivery.
    /// </summary>
    private const int MaxAdvancedRuns = AutopilotRunHold.MaxRunsPerPass;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        AutopilotAuthorizationAccepted domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == domainEvent.SeriesId, cancellationToken)
            .ConfigureAwait(false);

        // S77 delta N-1: the delivery can arrive after the owner revoked this authorization, or after a newer acceptance
        // superseded it. Restoring consent the series no longer holds would resume runs for a snapshot that is gone and
        // advance them without a live authorization, so a delivery whose row is not the current pointer is a no-op.
        if (series is null || series.CurrentAutopilotAuthorizationId != domainEvent.AuthorizationId)
        {
            LogNotCurrent(domainEvent.SeriesId, domainEvent.AuthorizationId);
            return;
        }

        // S77 review C-2/S-1: the runs the missing consent paused go back to the state their step implies, and the
        // save has to land before the orchestrator opens its own scope below. The rows are stamped with the event's
        // own instant — the moment the owner accepted — which is what the delivery carries and what a redelivery
        // repeats.
        var release = await AutopilotRunHold
            .ReleaseOwnerPausedAsync(db, domainEvent.SeriesId, context.OccurredAt, cancellationToken)
            .ConfigureAwait(false);

        if (release.Changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var open = await db.AutopilotRuns
            .Where(run => run.SeriesId == domainEvent.SeriesId && run.FinishedAt == null)
            .OrderBy(run => run.SlotAt)
            .Select(run => run.Id)
            .Take(MaxAdvancedRuns)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var runId in open)
        {
            // A refused advance is retried by the next tick; failing the delivery would only redeliver the same
            // refusal and block the rows behind it.
            _ = await orchestrator
                .AdvanceAsync(runId, domainEvent.UserId, cancellationToken)
                .ConfigureAwait(false);
        }

        LogRouted(
            domainEvent.SeriesId,
            domainEvent.AuthorizationId,
            domainEvent.Version,
            release.Changed,
            open.Count);

        if (release.Refused > 0 || release.Untouched > 0)
        {
            LogReleaseIncomplete(domainEvent.SeriesId, release.Refused, release.Untouched);
        }
    }

    // 02.8.3-T2: 6695-6699 is the block this task takes (see PutAutopilotHandler): 6347 already belonged to
    // `ProposeBibleUpdateHandler.LogTrueCrimeAutopilot`.
    /// <summary>Logs what one delivery released and routed.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="authorizationId">The accepted row.</param>
    /// <param name="version">Its version.</param>
    /// <param name="released">How many runs the restored consent un-paused.</param>
    /// <param name="runs">How many open runs were advanced.</param>
    [LoggerMessage(EventId = 6697, Level = LogLevel.Information, Message = "Autopilot authorization {authorizationId} v{version} accepted for series {seriesId}; released {released} paused run(s), advanced {runs} open run(s).")]
    private partial void LogRouted(Guid seriesId, Guid authorizationId, int version, int released, int runs);

    /// <summary>Logs the runs one release pass could not move; the next acceptance or tick retries them.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="refused">How many rows the domain refused to resume.</param>
    /// <param name="untouched">How many paused rows lie beyond the pass bound.</param>
    [LoggerMessage(EventId = 6699, Level = LogLevel.Warning, Message = "Autopilot authorization accepted for series {seriesId}: {refused} paused run(s) could not be released and {untouched} lie beyond the release bound.")]
    private partial void LogReleaseIncomplete(Guid seriesId, int refused, int untouched);

    /// <summary>Logs a delivery whose authorization is no longer the series' current one: nothing was released.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="authorizationId">The accepted row the delivery carries.</param>
    [LoggerMessage(EventId = 6702, Level = LogLevel.Information, Message = "Autopilot authorization {authorizationId} accepted for series {seriesId} is no longer current; nothing released.")]
    private partial void LogNotCurrent(Guid seriesId, Guid authorizationId);
}
