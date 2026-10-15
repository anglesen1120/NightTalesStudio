using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The one JSON data object of a bible-proposal call (02 README D6, 02.6.3-T2 plan §7.1 step 3): the task, the
/// series' language and topic, the bible as it stands and the approved episode.
/// </summary>
/// <remarks>
/// The bible block is the writer's id-free view in the same shape (plan §7.1:
/// <c>{ narrator, recurring_characters, places, rules, recent_episodes: [{title, summary}] }</c>): no
/// <c>ch1</c>/<c>pl1</c>/<c>rl1</c> id, no episode UUID and no <c>schema_version</c>, so an id can never reach
/// the model or come back in an op (D6's alias principle). It is deliberately not
/// <see cref="BibleSnapshot"/>: that record carries the bible's <c>version</c> and its writer-budget
/// <c>truncated</c> flag, and this call has neither — the window below is the prompt's own rule, not a size
/// budget that dropped content.
/// </remarks>
/// <param name="Task">Always <c>propose_bible_update</c>.</param>
/// <param name="Language">The series' language, snake_case.</param>
/// <param name="Topic">The series' narrative topic, snake_case.</param>
/// <param name="Bible">The bible, ids stripped, newest summaries first.</param>
/// <param name="Episode">The approved episode the ops are derived from.</param>
public sealed record BibleProposalDataObject(
    string Task,
    string Language,
    string Topic,
    BibleProposalBible Bible,
    BibleProposalEpisode Episode);

/// <summary>The bible block of the data object: the same id-free view the writer prompt carries.</summary>
/// <param name="Narrator">The narrator; required by the bible's validator.</param>
/// <param name="RecurringCharacters">The recurring characters, newest last.</param>
/// <param name="Places">The recurring places.</param>
/// <param name="Rules">The standing rules, as text.</param>
/// <param name="RecentEpisodes">The newest summaries, without episode ids.</param>
public sealed record BibleProposalBible(
    BibleProposalNarrator Narrator,
    IReadOnlyList<BibleProposalNamed> RecurringCharacters,
    IReadOnlyList<BibleProposalNamed> Places,
    IReadOnlyList<string> Rules,
    IReadOnlyList<BibleProposalSummary> RecentEpisodes);

/// <summary>The narrator of the data object.</summary>
/// <param name="Name">The narrator's name, if any.</param>
/// <param name="Persona">The persona text.</param>
/// <param name="VoiceNotes">Free-form voice notes.</param>
public sealed record BibleProposalNarrator(string? Name, string Persona, string VoiceNotes);

/// <summary>A character or a place of the data object: the name and the description, never an id.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">The description.</param>
public sealed record BibleProposalNamed(string Name, string Description);

/// <summary>One earlier episode of the data object: the title and the summary, never an id.</summary>
/// <param name="Title">The episode title.</param>
/// <param name="Summary">The summary text.</param>
public sealed record BibleProposalSummary(string Title, string Summary);

/// <summary>The approved episode of the data object.</summary>
/// <param name="Title">The story's title.</param>
/// <param name="Structure">The D3 structure tags and the body beats of the approved story version.</param>
/// <param name="Body">The story body, truncated to <c>Bible:Proposal:MaxStoryChars</c> from the end.</param>
public sealed record BibleProposalEpisode(string Title, StoryStructureTags Structure, string Body);

/// <summary>The serialised data object and what shaping it needed (02.6.3-T2 plan §7.1 step 3).</summary>
/// <param name="Json">The one data object, the exact bytes the client sends.</param>
/// <param name="BodyTruncated">Whether the story body had to be cut, so the caller can count it.</param>
public sealed record BibleProposalPayload(string Json, bool BodyTruncated);

/// <summary>
/// Builds and serialises the one data object of a bible-proposal call (02 README D6, 02.6.3-T2 plan §7.1 step 3):
/// the bible with its newest summaries and the approved story body cut to the prompt ceiling.
/// </summary>
/// <remarks>
/// <para>
/// Every string here is material the model may read, never an instruction: the bible and the story body are
/// values of a JSON document (the system prompt states the two-tier rule), so nothing can break the object or
/// the turn structure. The encoder is pinned to all Unicode ranges, so Vietnamese text travels readably rather
/// than escaped, and the property order is the declaration order — control keys first, then the bible, then the
/// episode — so the static prefix stays a stable cache prefix (O9).
/// </para>
/// <para>
/// The body is cut <em>from the end</em> (plan §7.1 step 3): the opening carries the hook and the twist is
/// usually early, so the prefix is what the model needs. A cut never splits a surrogate pair, because half a
/// pair is not a character and <c>System.Text.Json</c> would rewrite it.
/// </para>
/// </remarks>
public static class BibleProposalDataObjectJson
{
    /// <summary>The <c>task</c> control value of a bible-proposal call.</summary>
    public const string ProposeBibleUpdateTask = "propose_bible_update";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Builds and serialises the data object of one proposal.</summary>
    /// <param name="request">The bible and the approved episode of the call.</param>
    /// <param name="maxSummaries">The newest summaries the payload carries (<c>Bible:Prompt:MaxSummaries</c>).</param>
    /// <param name="maxStoryChars">The most characters of the story body the payload carries (<c>Bible:Proposal:MaxStoryChars</c>).</param>
    /// <returns>The data object JSON and whether the body was cut.</returns>
    /// <exception cref="ArgumentException">The summary window or the body ceiling is not positive.</exception>
    public static BibleProposalPayload Build(BibleProposalRequest request, int maxSummaries, int maxStoryChars)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSummaries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStoryChars);

        var (body, truncated) = TruncateBody(request.StoryBody, maxStoryChars);

        var dataObject = new BibleProposalDataObject(
            ProposeBibleUpdateTask,
            request.Language,
            request.Topic,
            BibleBlock(request.Bible, maxSummaries),
            new BibleProposalEpisode(request.StoryTitle, request.Structure, body));

        return new BibleProposalPayload(JsonSerializer.Serialize(dataObject, Options), truncated);
    }

    /// <summary>
    /// Cuts a story body to the prompt ceiling from the end, never splitting a surrogate pair.
    /// </summary>
    /// <param name="body">The approved story's body.</param>
    /// <param name="maxChars">The ceiling (<c>Bible:Proposal:MaxStoryChars</c>).</param>
    /// <returns>The body to send and whether it was cut.</returns>
    public static (string Body, bool Truncated) TruncateBody(string body, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        if (body.Length <= maxChars)
        {
            return (body, false);
        }

        var cut = char.IsHighSurrogate(body[maxChars - 1]) ? maxChars - 1 : maxChars;

        return (body[..cut], true);
    }

    /// <summary>The bible block: the newest summaries, no ids anywhere.</summary>
    private static BibleProposalBible BibleBlock(SeriesBibleContent content, int maxSummaries)
    {
        var summaries = content.EpisodeSummaries
            .OrderByDescending(summary => summary.ApprovedAt)
            .Take(maxSummaries)
            .Select(summary => new BibleProposalSummary(summary.Title, summary.Summary))
            .ToArray();

        return new BibleProposalBible(
            new BibleProposalNarrator(content.Narrator.Name, content.Narrator.Persona, content.Narrator.VoiceNotes),
            content.RecurringCharacters.Select(character => new BibleProposalNamed(character.Name, character.Description)).ToArray(),
            content.Places.Select(place => new BibleProposalNamed(place.Name, place.Description)).ToArray(),
            content.Rules.Select(rule => rule.Text).ToArray(),
            summaries);
    }
}
