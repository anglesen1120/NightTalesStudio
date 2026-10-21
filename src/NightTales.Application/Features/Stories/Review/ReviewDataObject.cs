using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories.Review;

/// <summary>
/// The one JSON data object of a review call (02 README D6, 02.6.1-T1). <c>task</c>, <c>language</c>, <c>topic</c>,
/// <c>tier</c>, <c>length</c>, <c>facts[].id</c> and <c>ear_rules.questions[].question_id</c> are control keys the
/// model follows; <c>story</c>, <c>facts[].statement</c>, <c>bible</c> and the question texts are material it judges
/// and never obeys. There are no URLs, source text or source aliases.
/// </summary>
/// <param name="Task">Always <c>review_story</c>.</param>
/// <param name="Language">The content language, <c>vi</c> or <c>en</c>.</param>
/// <param name="Topic">The topic, snake_case, for example <c>true_crime</c>.</param>
/// <param name="Tier">The tier, snake_case.</param>
/// <param name="Length">The token and segment bands.</param>
/// <param name="Story">The draft as the writer returned it.</param>
/// <param name="Facts">The approved facts without sources; empty for fiction.</param>
/// <param name="Bible">The bible, or <see langword="null"/> (omitted).</param>
/// <param name="EarRules">The naturalness questions.</param>
public sealed record ReviewDataObject(
    string Task,
    string Language,
    string Topic,
    string Tier,
    ReviewDataLength Length,
    StoryContentV1 Story,
    IReadOnlyList<ReviewDataFact> Facts,
    BibleSnapshot? Bible,
    ReviewDataEarRules EarRules);

/// <summary>The <c>length</c> control object of the review data object.</summary>
/// <param name="Tokens">The token band, <c>[min, max]</c>.</param>
/// <param name="Segments">The segment band, <c>[min, max]</c>.</param>
public sealed record ReviewDataLength(IReadOnlyList<int> Tokens, IReadOnlyList<int> Segments);

/// <summary>One fact of the review data object: the alias and the statement, never a source.</summary>
/// <param name="Id">The fact alias, a control key.</param>
/// <param name="Statement">The fact text, material.</param>
public sealed record ReviewDataFact(string Id, string Statement);

/// <summary>The <c>ear_rules</c> block of the review data object.</summary>
/// <param name="Questions">The naturalness questions.</param>
public sealed record ReviewDataEarRules(IReadOnlyList<ReviewDataQuestion> Questions);

/// <summary>One naturalness question.</summary>
/// <param name="QuestionId">The stable id, a control key.</param>
/// <param name="Text">The question text.</param>
public sealed record ReviewDataQuestion(string QuestionId, string Text);
