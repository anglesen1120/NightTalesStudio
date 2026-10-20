using Microsoft.Extensions.Logging;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>publication_created</c> step (02.8.3 plan §3.5 row <c>publication_created → veto_window</c>, §3.6 row 11):
/// waits for the veto notice to be confirmed as delivered, then starts the veto window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is the trigger, time is the fallback.</b> 06.2.2-T2 confirms the notice by writing
/// <c>veto_notice_sent_at</c> and raising <c>PublicationUpdated</c>; the run then moves to <c>veto_window</c> on the
/// delivery handler's tick. The same tick reads the row, so a lost message costs one interval, not the run — the
/// plan's §7.2 rule for every step. A publication that is already past <c>awaiting_consent</c> proves the notice was
/// delivered (no consent can exist without it), so the run follows it.
/// </para>
/// <para>
/// <b>Thirty minutes, then a pause.</b> Plan §3.6 row 11: when <c>publication_create_at + 30 min</c> passes with no
/// delivery, the series pauses on <see cref="AutopilotPauseReason.VetoNoticeUndelivered"/> rather than publishing
/// behind an owner who was never told. The deadline counts from the row's own <c>created_at</c>, so a create that
/// ran late is not judged against a window that had already half elapsed.
/// </para>
/// <para>
/// A publication that failed or was cancelled while the run waited ends the run with that code (plan §7.3), and a
/// missing row is <see cref="StepResultKind.Retry"/> — the create's save is visible on the next tick.
/// </para>
/// </remarks>
public sealed partial class PublicationCreatedStep : IAutopilotStepHandler
{
    /// <summary>
    /// How long a created publication may wait for its veto notice before the series pauses (plan §3.6 row 11 and
    /// the transition table's "within 30 min").
    /// </summary>
    private static readonly TimeSpan NoticeDeliveryMargin = TimeSpan.FromMinutes(30);

    private readonly IAutopilotStepReader _reader;
    private readonly ILogger<PublicationCreatedStep> _logger;

    /// <summary>Builds the step.</summary>
    /// <param name="reader">The publication read the step decides on.</param>
    /// <param name="logger">The ids-only log of this step.</param>
    public PublicationCreatedStep(IAutopilotStepReader reader, ILogger<PublicationCreatedStep> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.PublicationCreated;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;
        var now = context.Now;

        if (run.PublicationId is not { } publicationId)
        {
            return StepResult.Retry(AutopilotStepCodes.PublicationMissing);
        }

        var publication = await _reader.FindPublicationAsync(publicationId, ct).ConfigureAwait(false);
        if (publication is null)
        {
            return StepResult.Retry(AutopilotStepCodes.PublicationMissing);
        }

        switch (publication.Status)
        {
            case PublicationStatus.Cancelled:
                run.Fail(AutopilotStepCodes.PublicationCancelled, now);

                return StepResult.Stopped(AutopilotStepCodes.PublicationCancelled);

            case PublicationStatus.Failed:
                return Fail(run, publication.ErrorCode ?? AutopilotStepCodes.PublicationFailed, now);

            case PublicationStatus.AwaitingConsent:
                if (publication.VetoNoticeSentAt is not null)
                {
                    return EnterVetoWindow(run, publication.Id, now);
                }

                if (now >= publication.CreatedAt + NoticeDeliveryMargin)
                {
                    run.Pause(AutopilotPauseReason.VetoNoticeUndelivered, now);
                    LogNoticeUndelivered(run.Id, publication.Id);

                    return StepResult.Stopped(AutopilotStepCodes.VetoNoticeUndelivered);
                }

                return StepResult.Idle;

            default:
                // Scheduled/Uploading/Processing/Published: consent exists, so the notice was delivered.
                return EnterVetoWindow(run, publication.Id, now);
        }
    }

    /// <summary>Moves the run into its veto window.</summary>
    private StepResult EnterVetoWindow(AutopilotRun run, Guid publicationId, DateTimeOffset now)
    {
        // The veto window has no idempotency key of the ladder (no command is sent), so the step name stays as it is.
        run.AdvanceTo(AutopilotRunState.VetoWindow, step: null, now);
        LogNoticeDelivered(run.Id, publicationId);

        return StepResult.Advanced();
    }

    /// <summary>Ends the run with the code of a failed publication.</summary>
    private static StepResult Fail(AutopilotRun run, string code, DateTimeOffset now)
    {
        run.Fail(code, now);

        return StepResult.Stopped(code);
    }

    [LoggerMessage(
        EventId = 6574,
        Level = LogLevel.Information,
        Message = "The veto notice of publication {PublicationId} reached the owner; autopilot run {RunId} enters its veto window.")]
    private partial void LogNoticeDelivered(Guid runId, Guid publicationId);

    [LoggerMessage(
        EventId = 6575,
        Level = LogLevel.Warning,
        Message = "Autopilot run {RunId} waited 30 minutes for the veto notice of publication {PublicationId}; the series pauses.")]
    private partial void LogNoticeUndelivered(Guid runId, Guid publicationId);
}
