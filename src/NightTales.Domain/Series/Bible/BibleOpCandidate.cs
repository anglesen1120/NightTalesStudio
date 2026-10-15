namespace NightTales.Domain.Series.Bible;

/// <summary>
/// One raw op of a <c>bible-proposal.v1</c> model answer (02.6.3-T2, plan §3.3, §7.1 step 5): the model's
/// suggestion exactly as it arrived, before <see cref="BibleProposalValidator"/> decides whether any part of it
/// may become a <see cref="BibleOp"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kind"/> is a plain string on purpose. The strict schema restricts it to the four snake_case names,
/// but a model that answers outside the closed list must be dropped as one unknown op rather than turn the whole
/// answer into an unreadable parse failure (plan §7.1 step 5: "keep only the four kinds").
/// </para>
/// <para>
/// Every other member is optional because each kind fills only the ones it needs, and the strict schema sends the
/// unused ones as <c>null</c> (02 README D3's convention).
/// </para>
/// <para>
/// Nothing in this record is trusted content: it is model output derived from untrusted bible text and story
/// body, so it is never logged (D19) and never stored before <see cref="BibleProposalValidator"/> has checked it.
/// </para>
/// </remarks>
/// <param name="Kind">The op kind the model named, snake_case, or anything else it invented.</param>
/// <param name="Summary">The summary text of an <c>add_summary</c> op; null otherwise.</param>
/// <param name="Name">The title of a summary op, or the name of a character or place op; null otherwise.</param>
/// <param name="Description">The description of a character or place op; null otherwise.</param>
/// <param name="Text">The text of a rule op; null otherwise.</param>
/// <param name="Reason">The model's short reason for the op, shown in the panel's diff banner; never content.</param>
public sealed record BibleOpCandidate(
    string? Kind,
    string? Summary,
    string? Name,
    string? Description,
    string? Text,
    string? Reason);
