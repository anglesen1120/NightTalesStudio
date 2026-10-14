#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// The D3 structure block of one episode as the series bible stores it (02 README D3, 02.6.3 plan §3.1):
/// the four vocabulary-mapped tags, the narrator mode and the body paragraph beats in narrative order.
/// </summary>
/// <remarks>
/// <para>
/// The story's own wire block is <see cref="StoryStructureV1"/> (<c>story.v1</c>; the strict schema has no
/// <c>beats</c> property, because D3 stores a beat per paragraph). The bible keeps the derived copy of that
/// block plus the beat sequence, which is what the proposal payload and the stored
/// <c>episode_summaries[].structure</c> carry (02.6.3 plan §3.1, 02.6.3-T2 §"Data object").
/// </para>
/// <para>
/// It is the type the vocabulary guard (02.8.4-T1) and the idea generator (02.8.1-T1) name, so it is never
/// cloned per feature.
/// </para>
/// </remarks>
/// <param name="SubGenre">The <c>sub_genre</c> value, as stored by the D3 mapper (a vocabulary id or <c>other:&lt;text&gt;</c>).</param>
/// <param name="Setting">The <c>setting</c> value (free text).</param>
/// <param name="Opening">The <c>opening</c> value.</param>
/// <param name="TwistType">The <c>twist_type</c> value.</param>
/// <param name="NarratorMode">The <c>narrator_mode</c> value (<c>first_person</c> or <c>third_person</c>).</param>
/// <param name="Beats">The body paragraphs' beats, in paragraph order.</param>
public sealed record StoryStructureTags(
    string SubGenre,
    string Setting,
    string Opening,
    string TwistType,
    string NarratorMode,
    IReadOnlyList<string> Beats)
{
    /// <summary>
    /// The all-empty placeholder: five empty strings and no beats.
    /// </summary>
    /// <remarks>
    /// A client PUT never sends <c>structure</c> (the backend derives it), so the request contract carries
    /// this value and a handler replaces it with <see cref="FromStoryContent"/> or the stored summary's own
    /// tags. It is deliberately valid — no rule in <c>BibleContentValidator</c> constrains the structure block
    /// or the narrator mode — so a placeholder can never turn a missing episode into a second field error.
    /// </remarks>
    public static StoryStructureTags Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, []);

    /// <summary>
    /// The tags of a stored <c>story.v1</c> document: the five values of its <c>structure</c> block and the
    /// non-empty <c>beat</c> of every body paragraph, in paragraph order.
    /// </summary>
    /// <param name="content">The deserialised stored story content.</param>
    /// <returns>The tags the bible summary stores for that episode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static StoryStructureTags FromStoryContent(StoryContentV1 content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var beats = new List<string>();
        IReadOnlyList<StorySegmentV1>? paragraphs = content.Paragraphs;
        foreach (var paragraph in paragraphs ?? [])
        {
            string? beat = paragraph?.Beat;
            if (beat is { Length: > 0 })
            {
                beats.Add(beat);
            }
        }

        // A story row written before D3, or a hand-edited one, may carry no structure block at all: the
        // bible then stores empty tags rather than failing the approval path that feeds this call.
        StoryStructureV1? structure = content.Structure;
        if (structure is null)
        {
            return Empty with { Beats = beats };
        }

        string? subGenre = structure.SubGenre;
        string? setting = structure.Setting;
        string? opening = structure.Opening;
        string? twistType = structure.TwistType;
        string? narratorMode = structure.NarratorMode;

        return new StoryStructureTags(
            subGenre ?? string.Empty,
            setting ?? string.Empty,
            opening ?? string.Empty,
            twistType ?? string.Empty,
            narratorMode ?? string.Empty,
            beats);
    }
}
