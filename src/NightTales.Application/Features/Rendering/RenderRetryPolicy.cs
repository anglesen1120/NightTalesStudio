using System.Collections.Frozen;
using ErrorOr;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Series;

namespace NightTales.Application.Features.Rendering;

/// <summary>
/// The retry table of BD7 and the creation of the next attempt (09.6.3-T1 step 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The table.</b> A code is retried when the attempt still has budget (<c>attempt &lt; 2</c>), it is not on
/// the never-retried list, and either the 03 classifier said it is retryable or it is one of 05 D5's transient
/// codes. <c>image_generation_failed</c> is deliberately <em>not</em> on the never-retried list: 03 D7 and
/// 09 §10's plan-03 hand-over make it retryable for the image rows the classifier marks so, and non-retryable
/// for <c>provider_funds_exhausted</c> and a moderated row (BD7's blanket wording is the plan's §10 [OPEN]).
/// </para>
/// <para>
/// <b>The new attempt.</b> Each row owns its own reservation (WT50), so the retry re-runs the 03 D8 sequence —
/// price version, worst-case image bound, platform spend cap, <c>ReserveAsync</c> — and inserts a
/// <c>queued</c> row with the failed row's episode, story version and params. <c>RenderJob.Create</c> raises
/// <c>RenderRequested</c>, whose outbox row is what queues <c>SubmitRenderJob</c> in this same transaction; the
/// job is never enqueued directly (D11/D12).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model the new row is added to.</param>
/// <param name="clock">The clock the retry row and its reservation are stamped from.</param>
/// <param name="prices">The price catalogue the new attempt is costed against.</param>
/// <param name="images">The worst-case image bound of the new attempt (03 D8/CR-4).</param>
/// <param name="budget">The platform spend cap, re-checked before the retry is queued (D1).</param>
/// <param name="ledger">The credit ledger: the retry's own reservation comes from here.</param>
/// <param name="imagesOptions">The <c>Images</c> section, for the reserve bound's token members.</param>
/// <param name="metrics">The retry counter, incremented once per created retry row.</param>
public sealed class RenderRetryPolicy(
    IAppDbContext db,
    IClock clock,
    IPriceCatalog prices,
    IImageCostEstimator images,
    IBudgetGuard budget,
    ICreditLedger ledger,
    ImagesOptions imagesOptions,
    RenderMetrics metrics) : IRenderRetryPolicy
{
    /// <summary>How many attempts one episode's render may reach: attempt 1, and one automatic retry.</summary>
    public const int MaxAttempts = 2;

    /// <summary>
    /// The 05 README D5 codes a failed attempt may be retried on (the contract's table, minus
    /// <c>contract_violation</c>, which is never one of the ten outcome codes).
    /// </summary>
    private static readonly FrozenSet<string> Transient = new[]
    {
        "tts_failed",
        "render_assembly_failed",
        "render_node_error",
        "render_lost",
        "render_timeout",
        "render_output_invalid",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The codes that are never retried, whatever the attempt count (03 D7).</summary>
    private static readonly FrozenSet<string> Never = new[]
    {
        "image_moderated",
        "story_moderated",
        "render_quality_failed",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool ShouldRetry(string code, int attempt, bool? outcomeRetryable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return attempt < MaxAttempts
            && !Never.Contains(code)
            && (outcomeRetryable ?? Transient.Contains(code));
    }

    /// <inheritdoc />
    public async Task<ErrorOr<RenderJob>> CreateRetryAsync(RenderJob failed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(failed);

        // The retry is created for a row the failure recorder has already failed: the code is on the row (it is
        // what ShouldRetry was answered for) and a row that is not failed has no verdict to retry.
        if (failed.Status != RenderJobStatus.Failed || failed.ErrorCode is not { } code)
        {
            return Error.Conflict(
                RenderJob.InvalidStateCode,
                "A retry is created for an attempt that already failed.");
        }

        var now = clock.UtcNow;

        var priceVersion = await prices.GetActiveAsync(now, ct).ConfigureAwait(false);
        if (priceVersion.IsError)
        {
            return priceVersion.Errors;
        }

        var bound = await images
            .WorstCaseAsync(AspectOf(failed.Params.Aspect), failed.Params.ImagePlan.MaxTerms, priceVersion.Value.Version, ct)
            .ConfigureAwait(false);

        // The failed attempt's own bound is not open any more (it was refunded before this ran), so the guard
        // sums the other open work without it.
        var allowed = await budget.CheckAsync(bound, ct, failed.Id).ConfigureAwait(false);
        if (allowed.IsError)
        {
            return allowed.Errors;
        }

        // The reservation is keyed by the retry row's id, so the ingest of the new attempt settles exactly it
        // (WT50). The token members are the same documented placeholder estimate the command reserved with
        // (03 D8): render_jobs stores no token columns.
        var retryJobId = Guid.CreateVersion7();
        var usageBound = new UsageBound(
            bound.WorstCaseUsd,
            failed.Params.ImagePlan.MaxTerms * ImagesOptions.PromptTextTokens,
            imagesOptions.PlaceholderTokensPerImage);

        var reservation = await ledger
            .ReserveAsync(failed.UserId, CreditOperation.Render, retryJobId, usageBound, ct)
            .ConfigureAwait(false);
        if (reservation.IsError)
        {
            return reservation.Errors;
        }

        var retry = RenderJob.Create(
            retryJobId,
            failed.UserId,
            failed.EpisodeId,
            failed.StoryVersionId,
            reservation.Value.Value,
            bound.WorstCaseUsd,
            failed.Params,
            failed.Attempt + 1,
            now);
        if (retry.IsError)
        {
            return retry.Errors;
        }

        db.RenderJobs.Add(retry.Value);

        // Counted here and only here: the instrument answers "how many retries were really queued", so a
        // refused reservation or a spend cap that stopped the retry does not touch it.
        metrics.CountRetry(code);

        return retry.Value;
    }

    /// <summary>
    /// The series aspect a stored <c>render-params.v1</c> names, for the worst-case image bound.
    /// </summary>
    /// <remarks>
    /// Only the two wire names <see cref="SubtitlePresetCatalog"/> defines are ever stored:
    /// <c>RenderSubmitMapper.BuildParams</c> writes the series' own aspect and <c>SubmitRenderJob</c> refuses any
    /// other text before it calls an adapter, so a row this policy retries always carries one of them.
    /// </remarks>
    /// <param name="aspectWire">The stored <c>aspect</c> member.</param>
    /// <returns>The aspect the estimator is keyed with.</returns>
    private static Aspect AspectOf(string aspectWire) =>
        string.Equals(aspectWire, SubtitlePresetCatalog.PortraitAspect, StringComparison.Ordinal)
            ? Aspect.Portrait
            : Aspect.Landscape;
}
