#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// One approved episode of a series as the idea steps read it (02.8.1 plan §3.5): the structure block, the
/// outline signature and the title — never the story body.
/// </summary>
/// <remarks>
/// <para>
/// The record lives in the Domain beside <see cref="StoryStructureTags"/> because the variety guard is pure and
/// the Domain cannot reference the Application port that reads the history (02.8.4-T1): the Application
/// <c>IEpisodeStructureHistory</c> returns this type, it is not defined there.
/// </para>
/// <para>
/// 02.8.1-T1 uses <see cref="Title"/> (with <see cref="ApprovedAt"/>) as the fallback the prompt reads when the
/// bible carries no summaries. 02.8.4-T1's variety guard reads <see cref="Structure"/> and
/// <see cref="BeatSignature"/> for its rotation and template-repeat rules, which is why the record carries the
/// whole structure block and not just the title.
/// </para>
/// <para>
/// <see cref="EpisodeId"/> stays in the record even though T1 sends no id to the model: the guard's run log
/// (10.4.3) names the episode a rejection traces to, and the DTO is the peer contract 02.8.4-T1 consumes.
/// </para>
/// <para>
/// <see cref="ApprovedAt"/> is the approved story version's creation time, the plan's proxy for the approval
/// stamp: a version can only be approved while it is current, so the order it imposes is the order in practice.
/// </para>
/// </remarks>
/// <param name="EpisodeId">The episode's id.</param>
/// <param name="Title">The approved story version's title.</param>
/// <param name="ApprovedAt">The approved story version's creation time.</param>
/// <param name="Structure">The D3 structure tags of that version.</param>
/// <param name="BeatSignature">The version's paragraph beats joined with <c>&gt;</c>.</param>
public sealed record EpisodeStructureRef(
    Guid EpisodeId,
    string Title,
    DateTimeOffset ApprovedAt,
    StoryStructureTags Structure,
    string BeatSignature);
