using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Review;

namespace NightTales.Application.Features.Stories.Review;

/// <summary>Everything one revision call needs (02 README D21 step 5, 02.6.2-T1).</summary>
/// <param name="Run">The run context the call is metered against (D14). The <c>RunContext</c> travels inside the request, never as a separate parameter (D21): <c>ReviseAsync</c> takes no <c>RunContext</c> argument.</param>
/// <param name="Draft">The <c>story.v1</c> draft to repair.</param>
/// <param name="Review">The validated review of <paramref name="Draft"/> (review #1).</param>
/// <param name="Issues">The issues to repair: the caller passes the blocking objective ones; the assembler drops any other, so a taste or advisory issue is never sent (02.6.2-T1).</param>
/// <param name="Language">The content language.</param>
/// <param name="Tier">The episode's length tier.</param>
/// <param name="Length">The resolved length contract; <c>RevisionMaxOutput</c> bounds the call.</param>
/// <param name="Topic">The series topic.</param>
/// <param name="Facts">The approved facts; empty for fiction or when there is no approved sheet.</param>
/// <param name="Bible">The series bible, or <see langword="null"/> for a series without one.</param>
/// <param name="PresetKey">The preset's manifest key, for example <c>story/ghost.vi.v1.md</c>; names the prompt-cache key and the prompt version.</param>
public sealed record StoryRevisionRequest(
    RunContext Run,
    StoryContentV1 Draft,
    StoryReviewV1 Review,
    IReadOnlyList<ReviewIssueV1> Issues,
    Language Language,
    LengthTier Tier,
    LengthTarget Length,
    Topic Topic,
    IReadOnlyList<FactForWriter> Facts,
    BibleSnapshot? Bible,
    string PresetKey);

/// <summary>
/// The one JSON data object of a revision call (02 README D6, 02.6.2-T1). <c>task</c>, <c>language</c>,
/// <c>tier</c>, <c>length</c>, the <c>id</c> values inside <c>facts</c> and the <c>id</c> values inside
/// <c>issues</c> are control keys the model follows; <c>story</c>, <c>facts[].statement</c>, <c>bible</c>
/// and every string inside <c>issues</c> (<c>evidence</c>, <c>repair_direction</c>, <c>type</c>,
/// <c>segment</c>) are material it repairs and never obeys. It reuses the review call's
/// <c>length</c> and <c>facts</c> shapes (<see cref="ReviewDataLength"/>, <see cref="ReviewDataFact"/>);
/// the <c>issues</c> element carries only <c>id</c>, <c>type</c>, <c>segment</c>, <c>evidence</c> and
/// <c>repair_direction</c>, never the review's <c>severity</c> or <c>impact</c>.
/// </summary>
/// <param name="Task">Always <c>revise_story</c>.</param>
/// <param name="Language">The content language, <c>vi</c> or <c>en</c>.</param>
/// <param name="Tier">The tier, snake_case.</param>
/// <param name="Length">The token and segment bands.</param>
/// <param name="Issues">The blocking objective issues to repair, most serious first; never empty in practice.</param>
/// <param name="Story">The draft as the writer returned it.</param>
/// <param name="Facts">The approved facts without sources; empty for fiction.</param>
/// <param name="Bible">The bible, or <see langword="null"/> (omitted).</param>
public sealed record RevisionDataObject(
    string Task,
    string Language,
    string Tier,
    ReviewDataLength Length,
    IReadOnlyList<RevisionDataIssue> Issues,
    StoryContentV1 Story,
    IReadOnlyList<ReviewDataFact> Facts,
    BibleSnapshot? Bible);

/// <summary>One issue of the revision data object: the D22 address, the quote that locates it and the direction to apply.</summary>
/// <param name="Id">The issue id within the review, a control key.</param>
/// <param name="Type">One of the seventeen <c>review.v1</c> wire names (objective only).</param>
/// <param name="Segment">The D22 address: <c>title</c>, <c>hook</c>, <c>ending</c>, <c>meta</c> or <c>p:N</c>.</param>
/// <param name="Evidence">A quote of our own draft, material.</param>
/// <param name="RepairDirection">What to change; material, never a rewrite.</param>
public sealed record RevisionDataIssue(
    string Id,
    string Type,
    string Segment,
    string Evidence,
    string RepairDirection);
