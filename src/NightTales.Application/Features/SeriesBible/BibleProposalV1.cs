using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The <c>bible-proposal.v1</c> model answer (02.6.3-T2, plan §"Prompt and schema files", 02 README D5): the
/// append-only ops the proposal model suggested for one episode. The strict schema is
/// <c>prompts/schemas/bible-proposal.v1.json</c> and the deserialiser is the adapter's snake_case options
/// (<c>OpenAiTextClient</c>), the same convention as <c>FactsV1</c>: the member names are the wire names.
/// </summary>
/// <remarks>
/// <para>
/// The schema's strict mode writes every property of an op, and the ones a kind does not use arrive as
/// <c>null</c> (02 README D3's convention), so the members here are nullable except the kind.
/// </para>
/// <para>
/// <see cref="BibleProposalOpV1.Kind"/> stays a plain string and is deliberately <b>not</b> bound to
/// <see cref="BibleOpKind"/>: the schema restricts it to the four snake_case names, but a model that answers
/// something else must be dropped as one unknown op by <see cref="BibleProposalValidator"/> rather than turn the
/// whole answer into a parse failure (plan §7.1 step 5).
/// </para>
/// </remarks>
/// <param name="Ops">The suggested ops, in the model's order; empty when it suggested nothing.</param>
public sealed record BibleProposalV1(IReadOnlyList<BibleProposalOpV1> Ops)
{
    /// <summary>
    /// Maps the answer to the Domain candidates the validator consumes: the model's untrusted ops, exactly as they
    /// arrived, with no op dropped and no field repaired here. Everything the answer is not allowed to do is
    /// decided by <see cref="BibleProposalValidator"/>, so the mapping never decides content.
    /// </summary>
    /// <returns>One candidate per op, in the model's order.</returns>
    public IReadOnlyList<BibleOpCandidate> ToCandidates() =>
        [.. Ops.Select(op => new BibleOpCandidate(op.Kind, op.Summary, op.Name, op.Description, op.Text, op.Reason))];
}

/// <summary>
/// One raw op of a <c>bible-proposal.v1</c> answer (02.6.3-T2). Each op kind fills the members it needs and the
/// strict schema sends the others as <c>null</c>: <c>summary</c> and <c>name</c> for <c>add_summary</c> (the name
/// is the episode's title), <c>name</c> and <c>description</c> for <c>add_character</c> and <c>add_place</c>,
/// <c>text</c> for <c>add_rule</c>.
/// </summary>
/// <remarks>
/// <c>reason</c> is the model's short explanation. It is never bible content: it is kept only so the panel's diff
/// banner (10.5.7) can show why an op was suggested, and it is checked for storable characters all the same,
/// because it travels in the same stored document.
/// </remarks>
/// <param name="Kind">The op kind, snake_case, as the model named it.</param>
/// <param name="Summary">The summary text of an <c>add_summary</c> op; null otherwise.</param>
/// <param name="Name">The title of a summary op, or the name of a character or place op; null otherwise.</param>
/// <param name="Description">The description of a character or place op; null otherwise.</param>
/// <param name="Text">The text of a rule op; null otherwise.</param>
/// <param name="Reason">The model's short reason for the op; null when it gave none.</param>
public sealed record BibleProposalOpV1(
    string Kind,
    string? Summary,
    string? Name,
    string? Description,
    string? Text,
    string? Reason);
