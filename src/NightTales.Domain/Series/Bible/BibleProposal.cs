using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Series.Bible;

/// <summary>
/// The pending proposal of one series (02.6.3 plan §3.3, F27): an <b>append-only</b> op list the model
/// returned, the content version it was computed against and when. It is stored in
/// <c>series_bibles.proposed_content</c> and never bumps the bible's version; accepting it does
/// (<c>SeriesBible.ApplyProposal</c>).
/// </summary>
/// <param name="Ops">The proposed ops, in model order; applied in that order on accept.</param>
/// <param name="BasedOnVersion">The <c>version</c> the ops were computed against; manual edits after it never invalidate them (append-only).</param>
/// <param name="ProposedAt">When the proposal was created.</param>
public sealed record BibleProposal(
    IReadOnlyList<BibleOp> Ops,
    int BasedOnVersion,
    DateTimeOffset ProposedAt);

/// <summary>
/// One append-only proposal op (02.6.3 plan §3.3). Exactly one payload member is meaningful per
/// <see cref="Kind"/> — <see cref="Summary"/> and <see cref="Name"/> for a summary,
/// <see cref="Name"/> and <see cref="Description"/> for a character or a place, <see cref="Text"/> for a rule —
/// and <see cref="Reason"/> is the model's short explanation, shown in the diff banner (10.5.7).
/// </summary>
/// <param name="Kind">What the op adds.</param>
/// <param name="Summary">The summary text of an <see cref="BibleOpKind.AddSummary"/> op; null otherwise.</param>
/// <param name="Name">The title of a summary op, or the name of a character/place op; null otherwise.</param>
/// <param name="Description">The description of a character/place op; null otherwise.</param>
/// <param name="Text">The text of a rule op; null otherwise.</param>
/// <param name="Reason">The model's short reason for the op; never persisted as bible content.</param>
public sealed record BibleOp(
    BibleOpKind Kind,
    string? Summary,
    string? Name,
    string? Description,
    string? Text,
    string? Reason);

/// <summary>
/// The op kinds a proposal may carry (02.6.3 plan §3.3). There is deliberately no update or delete kind and no
/// narrator kind: the ops only append, and the narrator changes only through a manual edit (plan §4 U4).
/// </summary>
/// <remarks>
/// The persisted and wire spelling is the snake_case of the member name
/// (<c>add_summary</c>, <c>add_character</c>, <c>add_place</c>, <c>add_rule</c>), which is what the strict
/// <c>bible-proposal.v1</c> enum restricts the model to (02.6.3-T2 §"Prompt and schema files").
/// </remarks>
[JsonConverter(typeof(BibleOpKindJsonConverter))]
public enum BibleOpKind
{
    /// <summary>A summary of the episode the proposal was created from.</summary>
    AddSummary = 0,

    /// <summary>A new recurring character.</summary>
    AddCharacter = 1,

    /// <summary>A new recurring place.</summary>
    AddPlace = 2,

    /// <summary>A new standing rule.</summary>
    AddRule = 3,
}

/// <summary>
/// Reads and writes <see cref="BibleOpKind"/> as its snake_case wire name, so the strict
/// <c>bible-proposal.v1</c> enum round-trips. A value outside the closed list, or a non-string token, is a
/// <see cref="JsonException"/> the caller maps to a failed proposal instead of a silently wrong op.
/// </summary>
public sealed class BibleOpKindJsonConverter : JsonConverter<BibleOpKind>
{
    /// <inheritdoc />
    public override BibleOpKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A bible op kind must be a string.");
        }

        return reader.GetString() switch
        {
            "add_summary" => BibleOpKind.AddSummary,
            "add_character" => BibleOpKind.AddCharacter,
            "add_place" => BibleOpKind.AddPlace,
            "add_rule" => BibleOpKind.AddRule,
            var other => throw new JsonException($"Unknown bible op kind '{other}'."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BibleOpKind value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            BibleOpKind.AddSummary => "add_summary",
            BibleOpKind.AddCharacter => "add_character",
            BibleOpKind.AddPlace => "add_place",
            BibleOpKind.AddRule => "add_rule",
            _ => throw new JsonException("Unknown bible op kind."),
        });
    }
}

/// <summary>
/// What one <c>SeriesBible.ApplyProposal</c> did (02.6.3 plan §4 U4): the ops that changed the content and how
/// many were skipped, so the accept path can log <c>applied</c> vs <c>skipped</c> counts (ids and counts only,
/// D13/D19).
/// </summary>
/// <param name="Applied">The ops whose entry was appended, in model order.</param>
/// <param name="Skipped">The ops that added nothing: a summary already stored for the episode, a duplicate after <c>BibleOpNormalizer</c>, or an array already at its limit.</param>
public sealed record BibleApplyResult(IReadOnlyList<BibleOp> Applied, int Skipped);

/// <summary>
/// The two members of a summary the client does not own (02.6.3 plan §4 U2/U4): the episode the summary belongs
/// to, the structure tags of its approved story version and the instant it was approved. They are supplied by the
/// caller of <c>SeriesBible.ApplyProposal</c>, because the op list carries no episode id and the Domain holds no
/// episode scope.
/// </summary>
/// <param name="EpisodeId">The episode the summary ops are recorded for (the row's <c>proposed_from_episode_id</c>).</param>
/// <param name="Structure">The D3 structure tags of the episode's approved story version, plus its beats.</param>
/// <param name="ApprovedAt">The instant the episode was approved.</param>
public sealed record BibleSummarySource(Guid EpisodeId, StoryStructureTags Structure, DateTimeOffset ApprovedAt);
