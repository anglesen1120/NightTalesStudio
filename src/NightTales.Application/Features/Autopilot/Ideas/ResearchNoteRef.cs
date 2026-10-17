namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// One research note of the series as the idea prompt sees it inside the data object
/// (<c>research_notes[{kind, text}]</c>, 02.8.1-T1 plan §7.1/§8): the owner's own note.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kind"/> is the snake_case <c>story_sources.kind</c> wire value — <c>user_note</c> or <c>url</c> —
/// and it is the control key the prompt reads, never free text. T1's context provider sends <c>user_note</c>
/// entries only: plan §4 U1 says a <c>url</c> source would contribute its title, but the acceptance check
/// <c>IdeaGenerator_DataObject_ContainsBibleRequiredAndForbiddenTags_NoIdsNoUrls</c> forbids a URL <em>and a
/// source title</em> in the serialised user message, so the conservative reading wins (see the task-file
/// contradiction in the 02.8.1-T1 report).
/// </para>
/// <para>
/// D19: a <c>user_note</c>'s text is the owner's own words, truncated to 400 characters, and the fetched
/// extracted text is never read, let alone sent. The whole record travels inside the one D6 data object, so a
/// note that reads like an instruction is still material, not a command (02 T69).
/// </para>
/// </remarks>
/// <param name="Kind">The source kind, snake_case (<c>user_note</c>; <c>url</c> is declared for the closed vocabulary but contributes nothing in T1).</param>
/// <param name="Text">The note text (≤ 400 characters).</param>
public sealed record ResearchNoteRef(string Kind, string Text);
