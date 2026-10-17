using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Content;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The one JSON data object of an idea-novelty judge call (02 README D6, 02.8.1-T2 task Design): the control
/// keys first, then the batch's candidates and the corpus they are measured against.
/// </summary>
/// <remarks>
/// <para>
/// Every string here is material the model may read, never an instruction (the two-tier rule the novelty prompt
/// states): the candidate fields and the past-episode titles and summaries are values of a JSON document, so
/// nothing can break out of the object or the turn structure. The encoder is pinned to all Unicode ranges
/// (D6/T68), so Vietnamese text travels readably rather than escaped.
/// </para>
/// <para>
/// No id ever appears (D6's alias principle, D19): the candidates are addressed by their 0-based batch
/// <c>index</c> and the past episodes by their 1-based <c>index</c> into <c>past_episodes</c> — the model never
/// sees an episode id, an idea id or a URL.
/// </para>
/// </remarks>
/// <param name="Task">Always <c>score_ideas</c>.</param>
/// <param name="Language">The series' content language, snake_case.</param>
/// <param name="Topic">The series' narrative topic, snake_case.</param>
/// <param name="Ideas">The batch's candidates, 0-based by position, each carrying no id.</param>
/// <param name="PastEpisodes">The corpus' approved episodes, 1-based by position, each carrying no id.</param>
public sealed record NoveltyDataObject(
    string Task,
    string Language,
    string Topic,
    IReadOnlyList<NoveltyDataIdea> Ideas,
    IReadOnlyList<NoveltyDataEpisode> PastEpisodes);

/// <summary>One candidate of the data object: its batch index and the four texts, never its id.</summary>
/// <param name="Index">The candidate's 0-based position in the batch (<c>ideas[].index</c>).</param>
/// <param name="Title">The episode title.</param>
/// <param name="Logline">The one-line premise.</param>
/// <param name="Hook">The opening hook.</param>
/// <param name="Setting">The setting in words.</param>
public sealed record NoveltyDataIdea(int Index, string Title, string Logline, string Hook, string Setting);

/// <summary>One past episode of the data object: its corpus index, the title and the summary, never its id.</summary>
/// <param name="Index">The episode's 1-based position in <c>past_episodes</c> (<c>past_episodes[].index</c>).</param>
/// <param name="Title">The approved story version's title.</param>
/// <param name="Summary">The content description, at most <see cref="NoveltyDataObjectJson.SummaryMaxChars"/> characters.</param>
public sealed record NoveltyDataEpisode(int Index, string Title, string Summary);

/// <summary>
/// Builds and serialises the one data object of an idea-novelty judge call (02 README D6, 02.8.1-T2 task Design).
/// </summary>
/// <remarks>
/// <para>
/// The property order is the declaration order — control keys, then the batch, then the corpus — so the
/// riskiest text sits last. Nothing here truncates the serialised object: the judge refuses an object over
/// <c>Story:Prompt:MaxDataObjectBytes</c> instead of silently dropping content.
/// </para>
/// <para>
/// The candidate and episode texts are model- or owner-written and are therefore treated as untrusted (D19):
/// each is NFC-normalised, has its control characters dropped and is capped at the width its source contract
/// pins before it is serialised, so a stray control byte or an over-long field cannot change the shape of the
/// document. The JSON encoder escapes whatever is left, so no text can ever leave its string value.
/// </para>
/// </remarks>
public static class NoveltyDataObjectJson
{
    /// <summary>The <c>task</c> control value of an idea-novelty judge call.</summary>
    public const string ScoreIdeasTask = "score_ideas";

    /// <summary>The most characters of one past-episode summary the data object carries (task Design, D19).</summary>
    public const int SummaryMaxChars = 200;

    /// <summary>The most characters of one past-episode title the data object carries (the story title width).</summary>
    public const int EpisodeTitleMaxChars = 200;

    /// <summary>The candidate caps, read from the Domain record's own defaults so the two cannot drift.</summary>
    private static readonly IdeaLimits Caps = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Builds and serialises the data object of one judge call.</summary>
    /// <param name="request">The run, the series' control values, the batch and the corpus.</param>
    /// <returns>The one data object, the exact string the client sends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static string Build(NoveltyJudgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ideas = new NoveltyDataIdea[request.Ideas.Count];
        for (var index = 0; index < ideas.Length; index++)
        {
            var candidate = request.Ideas[index];
            ideas[index] = new NoveltyDataIdea(
                index,
                Clean(candidate.Title, Caps.TitleChars),
                Clean(candidate.Logline, Caps.LoglineChars),
                Clean(candidate.Hook, Caps.HookChars),
                Clean(candidate.Setting, Caps.SettingChars));
        }

        var episodes = new NoveltyDataEpisode[request.PastEpisodes.Count];
        for (var index = 0; index < episodes.Length; index++)
        {
            var episode = request.PastEpisodes[index];
            // 1-based: the judge answers `max_overlap_episode_id` as an index into this list (task Design).
            episodes[index] = new NoveltyDataEpisode(
                index + 1,
                Clean(episode.Title, EpisodeTitleMaxChars),
                Clean(episode.Summary, SummaryMaxChars));
        }

        var dataObject = new NoveltyDataObject(ScoreIdeasTask, request.Language, request.Topic, ideas, episodes);

        return JsonSerializer.Serialize(dataObject, Options);
    }

    /// <summary>
    /// Normalises, de-controls and caps one untrusted text: NFC via <see cref="StoryText.Normalize"/> (which also
    /// replaces unpaired surrogates), control characters dropped, then a surrogate-safe cut at
    /// <paramref name="maxChars"/>.
    /// </summary>
    /// <param name="text">The model- or owner-written text; <see langword="null"/> reads as empty.</param>
    /// <param name="maxChars">The ceiling, from the Domain contract that pins the field's width.</param>
    /// <returns>The text to send.</returns>
    private static string Clean(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalised = StoryText.Normalize(text);
        var stripped = normalised.Any(char.IsControl) ? Drop(normalised) : normalised;

        return IdeaDataObjectJson.TruncateNote(stripped, maxChars);
    }

    /// <summary>Drops every control character, keeping the rest of the text in order.</summary>
    private static string Drop(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
