using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Autopilot.Events;

/// <summary>
/// The shared body of the autopilot's step-trigger handlers (02.8.3 plan §7.2): every event that can advance a run
/// names its episode, so the handler resolves the live run of that episode and lets
/// <see cref="IAutopilotRunOrchestrator.AdvanceWhileProgressingAsync"/> do the work — the events never decide a
/// transition themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>No run, no work.</b> Most episodes are the owner's own and have no <c>autopilot_runs</c> row; that is the
/// normal case and a silent no-op, never an error and never a log line (a user episode's every approval would
/// otherwise write one).
/// </para>
/// <para>
/// <b>One delivery advances the run as far as it goes</b> (<see cref="MaxStepsPerDelivery"/> steps): an event
/// usually opens exactly one edge, but a fast path (a sheet approved while the draft event is still in flight) can
/// open two, and both are free to take here. The orchestrator's own compare-and-set makes the repeated call safe
/// after a redelivery.
/// </para>
/// <para>
/// <b>Failures are surfaced, not swallowed.</b> Only a run that finished between the lookup and the advance
/// (<c>autopilot_run_not_found</c>) is an expected no-op; every other orchestrator error is logged with ids and
/// codes and then thrown, so the outbox dispatcher retries the delivery with backoff and dead-letters it after its
/// attempts — a handler that returned normally would mark the event processed and leave the run stalled (09 README
/// D11).
/// </para>
/// <para>
/// <b>D19.</b> The log line carries the message, run and episode ids and the error codes only: never a story text,
/// a brief, a fact sheet, a source URL or a title.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The domain event this handler consumes.</typeparam>
/// <param name="locator">The episode → live run lookup (ids only).</param>
/// <param name="orchestrator">The one-step state machine driver.</param>
/// <param name="logger">This delivery's log.</param>
public abstract partial class AutopilotStepOutboxHandler<TEvent>(
    IAutopilotRunLocator locator,
    IAutopilotRunOrchestrator orchestrator,
    ILogger logger) : IOutboxHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>The steps one delivery may take, the same bound the design fixes for an event-driven advance.</summary>
    public const int MaxStepsPerDelivery = 6;

    /// <inheritdoc />
    public async ValueTask HandleAsync(TEvent domainEvent, OutboxContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var episodeId = EpisodeIdOf(domainEvent);
        var found = await locator
            .FindActiveByEpisodeAsync(episodeId, cancellationToken)
            .ConfigureAwait(false);
        if (found is not { } run)
        {
            return;
        }

        var advanced = await orchestrator
            .AdvanceWhileProgressingAsync(run.RunId, run.UserId, MaxStepsPerDelivery, cancellationToken)
            .ConfigureAwait(false);
        if (!advanced.IsError)
        {
            return;
        }

        var codes = string.Join(", ", advanced.Errors.Select(error => error.Code));

        if (advanced.Errors.All(error => error.Code == ErrorCodes.AutopilotRunNotFound))
        {
            // The run reached a terminal state between the lookup and the advance: the event arrived too late and
            // there is nothing left to do, so this delivery counts as handled.
            LogRunClosed(context.MessageId, run.RunId, episodeId);

            return;
        }

        LogAdvanceFailed(context.MessageId, run.RunId, episodeId, codes);

        throw new InvalidOperationException(
            $"AutopilotRunOrchestrator.AdvanceWhileProgressingAsync failed for run {run.RunId} of episode {episodeId}: {codes}");
    }

    /// <summary>The episode the event names; every event of this family carries one.</summary>
    /// <param name="domainEvent">The delivered event.</param>
    /// <returns>The episode the run can be resolved with.</returns>
    protected abstract Guid EpisodeIdOf(TEvent domainEvent);

    [LoggerMessage(
        EventId = 6580,
        Level = LogLevel.Warning,
        Message = "Autopilot outbox delivery {MessageId} could not advance run {RunId} of episode {EpisodeId}: {Codes}")]
    private partial void LogAdvanceFailed(Guid messageId, Guid runId, Guid episodeId, string codes);

    [LoggerMessage(
        EventId = 6581,
        Level = LogLevel.Debug,
        Message = "Autopilot outbox delivery {MessageId} found run {RunId} of episode {EpisodeId} already finished")]
    private partial void LogRunClosed(Guid messageId, Guid runId, Guid episodeId);
}
