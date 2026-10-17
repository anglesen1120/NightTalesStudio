using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The canonical JSON of the two <c>jsonb</c> columns of <c>episode_ideas</c> (02.8.1 plan §3.1):
/// <c>structure</c> (<see cref="IdeaStructure"/>) and <c>novelty</c> (<see cref="IdeaNoveltyDetail"/>).
/// Snake_case property names, no indentation, every property written and the default (non-relaxed) encoder.
/// </summary>
/// <remarks>
/// The EF value converters and the source-generated <c>IdeaJsonContext</c> take their settings from
/// <see cref="Options"/>, so the serialisation of a stored row and of a round-tripped one cannot drift —
/// the same arrangement <c>BibleJson</c> uses for the bible's documents. Nothing model-facing is serialised
/// here: a candidate travels in the prompt's own data object, and these documents only ever hold row data.
/// </remarks>
public static class IdeaJson
{
    /// <summary>The canonical serialiser settings (see the type remarks).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Default,
    };

    /// <summary>
    /// Serialises the structure payload of a row as the FLAT document of plan §3.1:
    /// <c>{ sub_genre, setting, opening, twist_type, narrator_mode, beats, beat_signature }</c>.
    /// </summary>
    /// <remarks>
    /// The five tag keys are <see cref="StoryStructureTags"/>' own names — the one
    /// vocabulary of the story-content structure block — and the outline replaces the tags' <c>beats</c> key,
    /// so the document carries a single beat sequence and the round trip is lossless
    /// (<see cref="IdeaStructure.From"/> keeps the two equal).
    /// </remarks>
    /// <param name="structure">The payload to serialise.</param>
    /// <returns>The canonical JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="structure"/> is <see langword="null"/>.</exception>
    public static string Serialize(IdeaStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);

        var document = JsonSerializer.SerializeToNode(structure.Tags, Options)?.AsObject()
            ?? throw new InvalidOperationException("A structure payload always serialises to a JSON object.");
        document["beats"] = JsonSerializer.SerializeToNode(structure.Beats, Options);
        document["beat_signature"] = structure.BeatSignature;

        return document.ToJsonString(Options);
    }

    /// <summary>Serialises the novelty payload of a row.</summary>
    /// <param name="novelty">The payload to serialise.</param>
    /// <returns>The canonical JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="novelty"/> is <see langword="null"/>.</exception>
    public static string Serialize(IdeaNoveltyDetail novelty)
    {
        ArgumentNullException.ThrowIfNull(novelty);

        return JsonSerializer.Serialize(novelty, Options);
    }

    /// <summary>Reads a stored <c>structure</c> document (the flat shape <see cref="Serialize(IdeaStructure)"/> writes).</summary>
    /// <param name="json">The stored JSON of a NOT NULL column.</param>
    /// <returns>The structure payload.</returns>
    /// <exception cref="InvalidOperationException">The JSON is not an object or deserialised to <see langword="null"/>.</exception>
    public static IdeaStructure DeserializeStructure(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var document = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("A stored structure document is never null: the column is NOT NULL.");

        // The five tag keys are read back through StoryStructureTags' own contract (the extra
        // beat_signature key is ignored by the serialiser), and the document's one beats key is both the
        // outline and the tags' beat sequence.
        var tags = document.Deserialize<StoryStructureTags>(Options)
            ?? throw new InvalidOperationException("A stored structure document is never null: the column is NOT NULL.");
        var beats = document["beats"]?.Deserialize<IReadOnlyList<string>>(Options) ?? [];
        var signature = document["beat_signature"]?.GetValue<string>() ?? IdeaStructure.Signature(beats);

        return new IdeaStructure(tags, beats, signature);
    }

    /// <summary>Reads a stored <c>novelty</c> document.</summary>
    /// <param name="json">The stored JSON, or <see langword="null"/> when the row has none yet.</param>
    /// <returns>The novelty payload, or <see langword="null"/>.</returns>
    public static IdeaNoveltyDetail? DeserializeNovelty(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<IdeaNoveltyDetail>(json, Options);
}
