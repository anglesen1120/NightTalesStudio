using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Stories.Length;

namespace NightTales.Domain.Stories.Quality;

/// <summary>
/// Everything <see cref="StoryQualityChecker.Check"/> needs besides the draft (02.3.4-T1, D21). Every member is
/// plain data the caller already holds, so the check does no I/O. The language is <c>Rules.Language</c>.
/// </summary>
/// <param name="Topic">The series topic; true crime switches on Q-NAME-01 and Q-FACT-01.</param>
/// <param name="Tier">The episode's length tier, or null when none is chosen (Q-TIER-01; length rules are skipped).</param>
/// <param name="Length">The resolved length contract of the tier × language cell, or null when unknown (length and segment rules are skipped).</param>
/// <param name="Rules">The run's D3 rules: the data lists the keyword rules match against, and the limits Q-META-01/Q-MD-01 reuse.</param>
/// <param name="FactCount">How many facts the approved sheet holds; 0 for fiction.</param>
/// <param name="FactSheetTitles">The titles of the fact sheet's sources; only Q-NAME-01 reads them and never copies one into an issue.</param>
/// <param name="HasUrlSources">Whether the topic has url sources (02 §5.9); Q-SIM-01/Q-SIM-02 only fire for these and for true crime.</param>
/// <param name="SimilarityScore">The similarity score, or null when unscored.</param>
/// <param name="SimilarityVerdict">The verdict of the single similarity boundary for <paramref name="SimilarityScore"/> (see <see cref="Quality.SimilarityVerdict"/>).</param>
/// <param name="ModerationCategories">The categories the draft-stage moderation flagged, or null/empty when it did not flag.</param>
/// <param name="ModelReviewStale">Whether the stored model review no longer describes this text (F25).</param>
/// <param name="Plan">The render image plan whose hint codes surface as advisory hints (03 CR-14), or null.</param>
/// <param name="Names">
/// The episode's real-person name corpus (03 README D11), loaded by the caller through
/// <c>INameCorpusProvider</c>; <see cref="NameCorpus.Empty"/> is the only value for fiction with no research. An
/// empty corpus is <em>not</em> evidence that the draft is clean: a caller that skips the load turns the name
/// rule off silently, so pass this parameter from the loaded corpus, never to dodge the load.
/// </param>
/// <param name="ModerationUnavailable">
/// Whether there is no usable moderation verdict for this text (a carried-over verdict that was never obtained, or an
/// audit report that could not be read). It fails closed: Q-MOD-01 blocks until the moderator has re-checked the text.
/// </param>
public sealed record StoryCheckContext(
    Topic Topic,
    LengthTier? Tier,
    LengthTarget? Length,
    StoryRules Rules,
    int FactCount,
    IReadOnlyList<string> FactSheetTitles,
    bool HasUrlSources,
    double? SimilarityScore,
    SimilarityVerdict SimilarityVerdict,
    IReadOnlyList<string>? ModerationCategories,
    bool ModelReviewStale,
    RenderImagePlan? Plan,
    NameCorpus Names,
    bool ModerationUnavailable = false);
