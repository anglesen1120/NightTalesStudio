using System.Globalization;
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
/// riskiest text sits last. The batch itself is never cut: it is what the model is asked to score. An object
/// over <c>Story:Prompt:MaxDataObjectBytes</c> is instead trimmed by dropping whole past episodes, oldest
/// first, from the corpus' newest-first order — the corpus is the one part a judge can lose without being asked
/// a different question — and the survivors keep their 1-based <c>past_episodes[].index</c>.
/// <see cref="BuildFitting"/> answers <see langword="null"/> when not even the candidates alone fit, and the
/// judge then refuses the call the way it refuses an answer the provider rejected: the batch degrades, and no
/// exception reaches the caller's job.
/// </para>
/// <para>
/// The candidate and episode texts are model- or owner-written and are therefore treated as untrusted (D19):
/// each is NFC-normalised, has its control characters and its Unicode format characters dropped and is capped
/// at the width its source contract pins before it is serialised, so a stray control byte, a bidi override or
/// an over-long field cannot change the shape of the document. The JSON encoder escapes whatever is left, so no
/// text can ever leave its string value.
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

    /// <summary>Serialises the data object of one judge call with the whole corpus.</summary>
    /// <param name="request">The run, the series' control values, the batch and the corpus.</param>
    /// <returns>The one data object, the exact string the client sends.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static string Build(NoveltyJudgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Serialise(request, Ideas(request), Episodes(request.PastEpisodes));
    }

    /// <summary>
    /// Builds and serialises the data object of one judge call within a byte ceiling, dropping the corpus'
    /// <b>oldest</b> past episodes until it fits: the references the model answers with are 1-based positions
    /// into the list it received, so what is sent must stay a prefix of the newest-first corpus for every index
    /// it reads to resolve.
    /// </summary>
    /// <param name="request">The run, the series' control values, the batch and the corpus.</param>
    /// <param name="maxBytes">The ceiling (<c>Story:Prompt:MaxDataObjectBytes</c>).</param>
    /// <returns>
    /// The fitted object, or <see langword="null"/> when not even the candidates alone fit the ceiling — nothing
    /// may be dropped without changing what the model is asked, so the caller refuses the call.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    public static FittedDataObject? BuildFitting(NoveltyJudgeRequest request, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var ideas = Ideas(request);
        var episodes = Episodes(request.PastEpisodes);
        var sent = episodes.Length;
        var json = Serialise(request, ideas, episodes);

        while (Encoding.UTF8.GetByteCount(json) > maxBytes)
        {
            if (sent == 0)
            {
                // The candidates alone are over the ceiling: they are never cut, so the call cannot be sent.
                return null;
            }

            sent--;
            json = Serialise(request, ideas, episodes.AsSpan(0, sent));
        }

        return new FittedDataObject(json, sent);
    }

    /// <summary>The data object and the part of the corpus it carries.</summary>
    /// <param name="Json">The exact string the client sends.</param>
    /// <param name="EpisodesSent">How many of the request's past episodes it carries, from the newest.</param>
    public sealed record FittedDataObject(string Json, int EpisodesSent);

    /// <summary>The batch's candidates, cleaned and addressed by their 0-based batch index.</summary>
    /// <param name="request">The run, the series' control values, the batch and the corpus.</param>
    /// <returns>The candidates of the data object.</returns>
    private static NoveltyDataIdea[] Ideas(NoveltyJudgeRequest request)
    {
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

        return ideas;
    }

    /// <summary>The corpus' past episodes, cleaned and addressed by their 1-based position in this list.</summary>
    /// <param name="episodes">The corpus, newest first.</param>
    /// <returns>The past episodes of the data object.</returns>
    private static NoveltyDataEpisode[] Episodes(IReadOnlyList<CorpusEpisode> episodes)
    {
        var mapped = new NoveltyDataEpisode[episodes.Count];
        for (var index = 0; index < mapped.Length; index++)
        {
            var episode = episodes[index];
            // 1-based: the judge answers `max_overlap_episode_id` as an index into this list (task Design).
            mapped[index] = new NoveltyDataEpisode(
                index + 1,
                Clean(episode.Title, EpisodeTitleMaxChars),
                Clean(episode.Summary, SummaryMaxChars));
        }

        return mapped;
    }

    /// <summary>Writes the one document of the control values, the batch and the episodes that fit.</summary>
    /// <param name="request">The run, the series' control values, the batch and the corpus.</param>
    /// <param name="ideas">The cleaned batch.</param>
    /// <param name="episodes">The cleaned episodes to carry, from the newest.</param>
    /// <returns>The serialised document.</returns>
    private static string Serialise(
        NoveltyJudgeRequest request,
        NoveltyDataIdea[] ideas,
        ReadOnlySpan<NoveltyDataEpisode> episodes) =>
        JsonSerializer.Serialize(
            new NoveltyDataObject(ScoreIdeasTask, request.Language, request.Topic, ideas, episodes.ToArray()),
            Options);

    /// <summary>
    /// Normalises, de-controls and caps one untrusted text: NFC via <see cref="StoryText.Normalize"/> (which also
    /// replaces unpaired surrogates), control and format characters dropped, then a surrogate-safe cut at
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

        return IdeaDataObjectJson.TruncateNote(Drop(normalised), maxChars);
    }

    /// <summary>
    /// Drops the characters that must never reach the model, keeping the rest of the text in order: the control
    /// characters (a raw NUL or a stray C0 byte) and the Unicode format characters
    /// (<see cref="UnicodeCategory.Format"/>) — the bidi overrides such as U+202E, the zero-width space and
    /// joiner families, and the tag characters of the astral plane (U+E0000…U+E007F) that can carry a hidden
    /// instruction inside a summary an owner approved. The walk is by <see cref="Rune"/>, so a character outside
    /// the BMP is judged as one character rather than as its surrogate pair.
    /// </summary>
    /// <param name="text">The text to clean.</param>
    /// <returns>The same instance when nothing needed dropping.</returns>
    private static string Drop(string text)
    {
        StringBuilder? builder = null;
        var keptFrom = 0;
        var walked = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.Utf16SequenceLength;

            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                // The text kept so far is copied in before the dropped character, so the builder holds the whole
                // surviving text rather than only its tail.
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, keptFrom, walked - keptFrom);
                keptFrom = walked + length;
            }

            walked += length;
        }

        return builder is null ? text : builder.Append(text, keptFrom, walked - keptFrom).ToString();
    }
}
