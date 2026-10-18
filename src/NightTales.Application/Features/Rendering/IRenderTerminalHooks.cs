using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The two named extension points 03.4.2-T1 fills in on the terminal path of a render (09.6.3-T1 step 5,
/// BD7): the per-image usage recording and the classification of the adapter's failed outcome.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed by BD7 and is the poller's, never this seam's: on a terminal <c>failed</c> status
/// <see cref="RecordImageUsageAsync"/> runs first (the images that were generated are metered and recorded
/// before anything decides the job's fate), then <see cref="ClassifyAsync"/> answers the outcome code and
/// its retryability, and only then does the failure recorder run. Both members are no-ops in the default
/// implementation, so 09.6.3-T1 ships the seam without a second write path.
/// </para>
/// <para>
/// 03.4.2-T1 replaces the registration with its own implementation (usage recorder plus the
/// <c>ImageOutcomeClassifier</c>); that is why the default is registered with <c>TryAdd</c>. Neither member
/// may log or copy story content, a scene prompt or raw adapter text (P6/P8, BD16): a classification is a
/// D5 outcome code and a nullable retryable flag.
/// </para>
/// </remarks>
public interface IRenderTerminalHooks
{
    /// <summary>
    /// Records the image usage the adapter reported for <paramref name="job"/>, idempotent per render job
    /// (03.4.2-T1 runs this before any classification or failure write).
    /// </summary>
    /// <param name="job">The render attempt whose terminal status arrived.</param>
    /// <param name="status">The adapter's status body, including its <c>images</c> report when it carries one.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask RecordImageUsageAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct);

    /// <summary>
    /// Classifies the adapter's outcome into the D5 code the job is failed with and, where the classifier
    /// knows it, whether that outcome may be retried.
    /// </summary>
    /// <param name="job">The render attempt whose terminal status arrived.</param>
    /// <param name="status">The adapter's status body.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The code and the optional per-outcome retryability.</returns>
    ValueTask<RenderFailureClassification> ClassifyAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct);
}

/// <summary>
/// What <see cref="IRenderTerminalHooks.ClassifyAsync"/> answers: the D5 outcome code the attempt is failed
/// with, and the classifier's per-outcome <c>retryable</c> flag when it has one.
/// </summary>
/// <param name="Code">The outcome code for <c>render_jobs.error_code</c> (always one of
/// <see cref="RenderJob.OutcomeCodes"/>).</param>
/// <param name="OutcomeRetryable">The 03 classifier's per-outcome flag for
/// <c>image_generation_failed</c> (rows 5 and 8 retryable, <c>provider_funds_exhausted</c> and a moderated
/// row not), or <see langword="null"/> when the classifier has no opinion — the retry policy then decides
/// from the code and the attempt count alone.</param>
public sealed record RenderFailureClassification(string Code, bool? OutcomeRetryable);
