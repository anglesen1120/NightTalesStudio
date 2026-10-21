using Microsoft.Extensions.Logging;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>veto_window</c> step (02.8.3 plan §3.5 rows <c>veto_window → scheduled</c> and
/// <c>veto_window → cancelled_by_owner</c>): waits out the owner's window and hands the publication to the go-live
/// step once consent is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consent is 06.2.2-T3's write.</b> The job that closes the window and consents does not exist yet, so the step
/// asks <see cref="IAutopilotConsentGate"/> — today's answer is "the row carries <c>consent_at</c>" — and retries on
/// the next tick while the answer is no. Nothing here consents on the owner's behalf: the window closing is the
/// owner's silence, and the write belongs to the job that owns that rule.
/// </para>
/// <para>
/// <b>A publication that is already scheduled.</b> When the row moved on (scheduled, uploading, processing or
/// published) the consent was written, so the run follows it. This is also the recovery path when the consent tick's
/// event was lost.
/// </para>
/// <para>
/// <b>A veto.</b> Cancel inside the window is the owner's veto (06.2.2-T1's cancel route), so the run ends
/// <c>cancelled_by_owner</c> (plan §3.5) — a terminal outcome, never a retry and never a resend.
/// </para>
/// </remarks>
public sealed partial class VetoWindowStep : IAutopilotStepHandler
{
    private readonly IAutopilotStepReader _reader;
    private readonly IAutopilotConsentGate _consentGate;
    private readonly ILogger<VetoWindowStep> _logger;

    /// <summary>Builds the step.</summary>
    /// <param name="reader">The publication read the step decides on.</param>
    /// <param name="consentGate">The consent seam 06.2.2-T3 replaces.</param>
    /// <param name="logger">The ids-only log of this step.</param>
    public VetoWindowStep(
        IAutopilotStepReader reader,
        IAutopilotConsentGate consentGate,
        ILogger<VetoWindowStep> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(consentGate);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _consentGate = consentGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.VetoWindow;

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
                run.Finish(AutopilotRunOutcome.CancelledByOwner, now);
                LogCancelled(run.Id, publication.Id);

                return StepResult.Stopped(AutopilotStepCodes.CancelledByOwner);

            case PublicationStatus.Failed:
                run.Fail(publication.ErrorCode ?? AutopilotStepCodes.PublicationFailed, now);

                return StepResult.Stopped(publication.ErrorCode ?? AutopilotStepCodes.PublicationFailed);

            case PublicationStatus.AwaitingConsent:
                if (!await _consentGate.IsConsentedAsync(publication, now, ct).ConfigureAwait(false))
                {
                    return StepResult.Idle;
                }

                return EnterScheduled(run, publication.Id, now);

            default:
                // Scheduled/Uploading/Processing/Published: consent exists (a go-live cannot precede it).
                return EnterScheduled(run, publication.Id, now);
        }
    }

    /// <summary>Records the consent step and moves the run to <c>scheduled</c>.</summary>
    private StepResult EnterScheduled(AutopilotRun run, Guid publicationId, DateTimeOffset now)
    {
        run.AdvanceTo(AutopilotRunState.Scheduled, AutopilotRunStep.Consent, now);
        LogConsented(run.Id, publicationId);

        return StepResult.Advanced(AutopilotRunStep.Consent);
    }

    [LoggerMessage(
        EventId = 6576,
        Level = LogLevel.Information,
        Message = "Consent was written for publication {PublicationId}; autopilot run {RunId} is scheduled.")]
    private partial void LogConsented(Guid runId, Guid publicationId);

    [LoggerMessage(
        EventId = 6577,
        Level = LogLevel.Information,
        Message = "The owner vetoed publication {PublicationId}; autopilot run {RunId} ends cancelled_by_owner.")]
    private partial void LogCancelled(Guid runId, Guid publicationId);
}
