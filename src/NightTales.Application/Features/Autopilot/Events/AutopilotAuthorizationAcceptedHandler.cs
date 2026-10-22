using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Persistence;
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
/// <b>Idempotent.</b> Delivery is at least once; a redelivery re-advances runs that are already running or held, and
/// <c>AdvanceAsync</c> is a no-op on a run whose next step is not due. No row is written here — the dispatcher owns
/// the <c>outbox_messages</c> row and the orchestrator owns the run.
/// </para>
/// <para>
/// <b>Ids only in the log</b> (D19): the series, the authorization and the count.
/// </para>
/// </remarks>
/// <param name="db">The tenant-filtered read model the open runs are selected from.</param>
/// <param name="orchestrator">T1's advance entry point.</param>
/// <param name="logger">The outcome log of one delivery.</param>
public sealed partial class AutopilotAuthorizationAcceptedHandler(
    IAppDbContext db,
    IAutopilotRunOrchestrator orchestrator,
    ILogger<AutopilotAuthorizationAcceptedHandler> logger)
    : IOutboxHandler<AutopilotAuthorizationAccepted>
{
    /// <summary>How many runs one delivery advances; the planner keeps far fewer per series.</summary>
    private const int MaxAdvancedRuns = 20;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        AutopilotAuthorizationAccepted domainEvent,
        OutboxContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

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

        LogRouted(domainEvent.SeriesId, domainEvent.AuthorizationId, domainEvent.Version, open.Count);
    }

    // 02.8.3-T2: 6695-6699 is the block this task takes (see PutAutopilotHandler): 6347 already belonged to
    // `ProposeBibleUpdateHandler.LogTrueCrimeAutopilot`.
    /// <summary>Logs what one delivery routed.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="authorizationId">The accepted row.</param>
    /// <param name="version">Its version.</param>
    /// <param name="runs">How many open runs were advanced.</param>
    [LoggerMessage(EventId = 6697, Level = LogLevel.Information, Message = "Autopilot authorization {authorizationId} v{version} accepted for series {seriesId}; advanced {runs} open run(s).")]
    private partial void LogRouted(Guid seriesId, Guid authorizationId, int version, int runs);
}
