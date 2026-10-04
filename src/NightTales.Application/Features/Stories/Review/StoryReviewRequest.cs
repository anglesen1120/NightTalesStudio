using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Length;

namespace NightTales.Application.Features.Stories.Review;

/// <summary>Everything one review call needs (02 README D21, 02.6.1-T1).</summary>
/// <param name="Run">The run context the call is metered against (D14).</param>
/// <param name="Draft">The <c>story.v1</c> draft under review.</param>
/// <param name="Language">The content language.</param>
/// <param name="Tier">The episode's length tier.</param>
/// <param name="Length">The resolved length contract; <c>ReviewMaxOutput</c> bounds the call.</param>
/// <param name="Topic">The series topic; <c>factual_care</c> is scored for true crime only.</param>
/// <param name="Facts">The approved facts; empty for fiction or when there is no approved sheet.</param>
/// <param name="Bible">The series bible, or <see langword="null"/> for a series without one.</param>
/// <param name="EarRuleQuestions">The naturalness questions the review must answer.</param>
/// <param name="PresetKey">The preset's manifest key, for example <c>story/ghost.vi.v1.md</c>; names the prompt-cache key and the prompt version.</param>
public sealed record StoryReviewRequest(
    RunContext Run,
    StoryContentV1 Draft,
    Language Language,
    LengthTier Tier,
    LengthTarget Length,
    Topic Topic,
    IReadOnlyList<FactForWriter> Facts,
    BibleSnapshot? Bible,
    IReadOnlyList<EarRuleQuestion> EarRuleQuestions,
    string PresetKey);
