using System.Text.Json.Nodes;
using NightTales.Domain.Rendering;
using NightTales.RenderNode.Contract;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The named extension points 03.4.2-T1 fills in on the two terminal paths of a render (09.6.3-T1 step 5 and
/// 09.6.3-T2 steps 8-9, BD7): the per-image usage recording and the classification of the adapter's failed
/// outcome on the poll path, and the <c>images</c> block plus the moderation rows of a kept video on the ingest
/// path.
/// </summary>
/// <remarks>
/// <para>
/// The order is fixed by BD7 and is the calling job's, never this seam's. On a terminal <c>failed</c> status
/// <see cref="RecordImageUsageAsync"/> runs first (the images that were generated are metered and recorded
/// before anything decides the job's fate), then <see cref="ClassifyAsync"/> answers the outcome code and
/// its retryability, and only then does the failure recorder run. On an ingested <c>succeeded</c> status
/// <see cref="MergeImagesAsync"/> runs inside the final transaction, before the verdict is stored, and
/// <see cref="RecordKeptVideoAsync"/> after the row is marked <c>succeeded</c> and before the outbox event, in
/// that same transaction. Every member is a no-op in the default implementation, so these tasks ship the seam
/// without a second write path.
/// </para>
/// <para>
/// 03.4.2-T1 replaces the registration with its own implementation (usage recorder, <c>ImageOutcomeClassifier</c>,
/// the <c>images.v1</c> block and the <c>ImageModerationRecorder</c>); that is why the default is registered with
/// <c>TryAdd</c>. No member may log or copy story content, a scene prompt or raw adapter text (P6/P8, BD16): a
/// classification is a D5 outcome code, an images block is <c>images.v1</c>'s own fields and a moderation row is
/// an id.
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

    /// <summary>
    /// Builds the <c>quality_report.images</c> block of a video the ingest is keeping (03.4.2-T1, D7 row 1), so
    /// the verdict the owner reads carries the image outcome beside the measured checks.
    /// </summary>
    /// <remarks>
    /// Called inside the ingest's final transaction, before the report is written: whatever this returns is what
    /// <c>render_jobs.quality_report</c> stores, so a hook that writes its own rows does so atomically with the
    /// verdict they explain. <see langword="null"/> means "this platform has no images outcome to record" and
    /// leaves the member out of the document — never a block assembled by the ingest itself (05 D9/D10: the
    /// <c>images</c> block has one owner, the 03 pipeline).
    /// </remarks>
    /// <param name="job">The attempt whose output is being kept; 03.4.2-T1 reads its params there.</param>
    /// <param name="status">The adapter's <c>succeeded</c> body, whose <c>images</c> report is the raw material.</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>The <c>images.v1</c> block to store, or <see langword="null"/>.</returns>
    ValueTask<JsonNode?> MergeImagesAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct);

    /// <summary>
    /// Records what the moderation of a kept video's generated slots decided (03.4.2-T1, D7 row 2), after the
    /// ingest marked the row <c>succeeded</c> and before it raises <c>RenderSucceeded</c>.
    /// </summary>
    /// <remarks>
    /// Called inside the same transaction as the verdict and the episode's move to <c>video_review</c>: a video is
    /// only kept once its blocked slots are on record, so the two states can never disagree.
    /// </remarks>
    /// <param name="job">The attempt whose output is being kept.</param>
    /// <param name="status">The adapter's <c>succeeded</c> body.</param>
    /// <param name="ct">Cancels the writes.</param>
    ValueTask RecordKeptVideoAsync(RenderJob job, RenderStatusV1 status, CancellationToken ct);
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
