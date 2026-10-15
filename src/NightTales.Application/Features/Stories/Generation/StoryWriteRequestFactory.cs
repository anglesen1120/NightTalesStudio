using System.Globalization;
using ErrorOr;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Usage;

namespace NightTales.Application.Features.Stories.Generation;

/// <summary>The request the writer takes, with the price version it was built against.</summary>
/// <param name="Request">The writer request; <c>Request.Run</c> is the <see cref="RunContext"/> of the attempt.</param>
/// <param name="Price">The price version active at the run's <c>queued_at</c>; its limits drive the length target and the story rules.</param>
public sealed record BuiltWriteRequest(StoryWriteRequest Request, PriceVersion Price);

/// <summary>
/// The only place that builds a <see cref="StoryWriteRequest"/> from a queued run (02.2.2-T3, D21): it resolves the length
/// target from the price version of the run's <c>queued_at</c> and sets <see cref="RunContext.PriceVersion"/> to that
/// version, so a price change after queueing never reprices a queued run.
/// </summary>
/// <remarks>
/// <b>Hand-over (02.6.3-T3):</b> the caller reads the run's bible once and passes it as <paramref name="bible"/>. Its
/// snapshot becomes <see cref="StoryWriteRequest.Bible"/> and its avoid-plots list becomes
/// <see cref="StoryWriteRequest.AvoidPlots"/>. The structure plan (02.5.2) is still <see langword="null"/>.
/// </remarks>
/// <param name="prices">The price catalog.</param>
public sealed class StoryWriteRequestFactory(IPriceCatalog prices)
{
    /// <summary>The error code when the run has no credit reservation, which a story run always has.</summary>
    public const string ReservationMissingCode = "generation_run_reservation_missing";

    /// <summary>Builds the writer request of one attempt.</summary>
    /// <param name="run">The story run.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="topic">The series topic.</param>
    /// <param name="language">The series language.</param>
    /// <param name="facts">The approved facts (true-crime), or <see langword="null"/> for fiction.</param>
    /// <param name="attempt">0 for the first attempt, 1 for the invalid-output retry.</param>
    /// <param name="violatedRules">The rule ids of the previous invalid output (never its text), or <see langword="null"/>.</param>
    /// <param name="bible">The series bible read once for this run; carries the writer snapshot and the avoid-plots list.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The request, or the price-catalog / reservation error.</returns>
    public async ValueTask<ErrorOr<BuiltWriteRequest>> BuildAsync(
        GenerationRun run,
        Episode episode,
        Topic topic,
        Language language,
        IReadOnlyList<FactForWriter>? facts,
        int attempt,
        IReadOnlyList<string>? violatedRules,
        BibleForWriter bible,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(episode);

        if (run.ReservationId is not { } reservation)
        {
            return Error.Failure(ReservationMissingCode, "The generation run has no credit reservation.");
        }

        var price = await prices.GetActiveAsync(run.QueuedAt, ct).ConfigureAwait(false);
        if (price.IsError)
        {
            return price.Errors;
        }

        var context = new RunContext(
            run.Id,
            run.UserId,
            run.SeriesId,
            run.EpisodeId,
            new ReservationId(reservation),
            run.Trigger,
            attempt,
            price.Value.Version.ToString(CultureInfo.InvariantCulture));

        var length = LengthTargetResolver.Resolve(episode.LengthTier, language, price.Value.ParseLimits());

        // Hand-over point: the structure plan (02.5.2) and the bible snapshot (02.6.3-T3) are supplied here; see the remarks.
        var request = new StoryWriteRequest(
            context,
            new SeriesSnapshot(run.SeriesId, topic, language),
            new EpisodeSnapshot(episode.Id, episode.Brief, episode.LengthTier, Structure: null),
            length,
            Bible: bible.Bible,
            facts,
            AvoidPlots: bible.AvoidPlots,
            violatedRules);

        return new BuiltWriteRequest(request, price.Value);
    }
}
