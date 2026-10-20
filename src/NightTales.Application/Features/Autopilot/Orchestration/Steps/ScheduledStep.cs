using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The <c>scheduled</c> step (02.8.3 plan §3.5 rows <c>scheduled → published</c> and
/// <c>scheduled → published_private_locked</c>, §7.4): reads the publication the upload job drives and ends the run
/// with the outcome its row reports.
/// </summary>
/// <remarks>
/// <para>
/// <b>The publication is the signal.</b> 06.2.3-T2's upload job moves the row <c>scheduled → uploading →
/// processing → published</c> and raises <c>PublicationUpdated</c>; the run follows the row on that event's tick and
/// on every tick after <c>upload_at</c> (plan §7.2's time fallback), so a lost message delays the run's end by one
/// interval and never strands it.
/// </para>
/// <para>
/// <b>Two live outcomes.</b> A row published while the project is pre-audit stays private on the channel
/// (<c>platform_payload</c> carries the pre-audit stamp 06.2.1 wrote), so the run ends
/// <c>published_private_locked</c> and the series counts it towards <c>pause_after_n_private</c> (plan §3.6 row 14);
/// a normal row ends <c>published</c>.
/// </para>
/// <para>
/// <b>Failures.</b> A row that failed uploads or was rejected by the platform ends the run <c>failed</c> with a
/// closed code (§3.6 rows 8–10): <c>youtube_quota_exhausted</c>, <c>upload_failed</c>, <c>video_rejected</c>, or
/// whatever catalogued code the row carries. The pause of the series that follows those triggers is 02.8.3-T4's,
/// not this step's.
/// </para>
/// </remarks>
public sealed partial class ScheduledStep : IAutopilotStepHandler
{
    private readonly IAutopilotStepReader _reader;
    private readonly ILogger<ScheduledStep> _logger;

    /// <summary>Builds the step.</summary>
    /// <param name="reader">The publication read the step decides on.</param>
    /// <param name="logger">The ids-only log of this step.</param>
    public ScheduledStep(IAutopilotStepReader reader, ILogger<ScheduledStep> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.Scheduled;

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
            case PublicationStatus.Published:
                {
                    var outcome = publication.PreAudit
                        ? AutopilotRunOutcome.PublishedPrivateLocked
                        : AutopilotRunOutcome.Published;
                    var code = publication.PreAudit
                        ? AutopilotStepCodes.PublishedPrivateLocked
                        : AutopilotStepCodes.Published;

                    run.Finish(outcome, now);
                    LogLive(run.Id, publication.Id, code);

                    return StepResult.Stopped(code);
                }

            case PublicationStatus.Cancelled:
                run.Fail(AutopilotStepCodes.PublicationCancelled, now);

                return StepResult.Stopped(AutopilotStepCodes.PublicationCancelled);

            case PublicationStatus.Failed:
                {
                    var code = FailureCodeOf(publication.ErrorCode);
                    run.Fail(code, now);
                    LogLive(run.Id, publication.Id, code);

                    return StepResult.Stopped(code);
                }

            default:
                // AwaitingConsent/Scheduled/Uploading/Processing: the row has not gone live yet. The tick re-reads it;
                // after `upload_at` the time fallback has already been reached and this is the plan §7.2 path.
                return StepResult.Idle;
        }
    }

    /// <summary>The closed code of a failed publication (plan §3.6 rows 8–10, plan §7.3).</summary>
    private static string FailureCodeOf(string? errorCode) => errorCode switch
    {
        YouTubeErrorCodes.QuotaExceeded => AutopilotStepCodes.YoutubeQuotaExhausted,
        YouTubeErrorCodes.Rejected => AutopilotStepCodes.VideoRejected,
        YouTubeErrorCodes.UploadFailed => AutopilotStepCodes.UploadFailed,
        null or "" => AutopilotStepCodes.PublicationFailed,
        _ => errorCode,
    };

    [LoggerMessage(
        EventId = 6578,
        Level = LogLevel.Information,
        Message = "Publication {PublicationId} settled; autopilot run {RunId} ends {OutcomeCode}.")]
    private partial void LogLive(Guid runId, Guid publicationId, string outcomeCode);
}
