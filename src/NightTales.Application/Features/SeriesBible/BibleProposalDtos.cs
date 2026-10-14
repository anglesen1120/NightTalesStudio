using NightTales.Domain.Series.Bible;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The pending bible proposal of a series (02.6.3-T1, plan §4 U1): the append-only op list the proposal
/// job wrote, with the counts the panel needs to render its diff banner before it resolves op semantics
/// (10.5.7).
/// </summary>
/// <remarks>
/// The envelope follows the camelCase of every other DTO of this layer; the op members keep their own
/// names. The proposal's episode is the row's <c>proposed_from_episode_id</c>, which is also the episode
/// an <c>add_summary</c> op belongs to, so <see cref="BibleProposalPreviewDto.SummaryForEpisodeId"/> is
/// derived from it rather than repeated per op.
/// </remarks>
/// <param name="BasedOnVersion">The content version the ops were computed against; it does not move while the proposal pends.</param>
/// <param name="ProposedFromEpisodeId">The episode whose approval created or last merged this proposal, when the row records one.</param>
/// <param name="ProposedAt">When the proposal was written.</param>
/// <param name="Ops">The pending ops, in the order they will apply.</param>
/// <param name="Preview">The counts the banner shows without resolving the ops.</param>
public sealed record BibleProposalDto(
    int BasedOnVersion,
    Guid? ProposedFromEpisodeId,
    DateTimeOffset ProposedAt,
    IReadOnlyList<BibleOpDto> Ops,
    BibleProposalPreviewDto Preview)
{
    /// <summary>The wire proposal of a stored one.</summary>
    /// <param name="proposal">The stored proposal.</param>
    /// <param name="proposedFromEpisodeId">The episode the row records for this proposal, if any.</param>
    /// <returns>The DTO.</returns>
    public static BibleProposalDto FromDomain(BibleProposal proposal, Guid? proposedFromEpisodeId)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        return new BibleProposalDto(
            proposal.BasedOnVersion,
            proposedFromEpisodeId,
            proposal.ProposedAt,
            [.. proposal.Ops.Select(BibleOpDto.FromDomain)],
            BibleProposalPreviewDto.FromOps(proposal.Ops, proposedFromEpisodeId));
    }
}

/// <summary>
/// One pending bible op (02.6.3-T1, plan §3.1, §4 U4). Every field is optional because each op kind fills
/// the ones it needs: a summary op carries <c>summary</c> and a reason, a character or place op
/// <c>name</c>/<c>description</c>, a rule op <c>text</c>.
/// </summary>
/// <remarks>
/// The wire <c>kind</c> is the snake_case member name of the Domain enum (<c>add_summary</c>,
/// <c>add_character</c>, <c>add_place</c>, <c>add_rule</c>), like every other enum this layer puts on the
/// wire.
/// </remarks>
/// <param name="Kind">The op kind, snake_case.</param>
/// <param name="Summary">The summary text of an <c>add_summary</c> op, already truncated to the limit.</param>
/// <param name="Name">The name of an <c>add_character</c> or <c>add_place</c> op.</param>
/// <param name="Description">The description of an <c>add_character</c> or <c>add_place</c> op.</param>
/// <param name="Text">The text of an <c>add_rule</c> op.</param>
/// <param name="Reason">Why the proposal model suggested the op, for the panel's diff.</param>
public sealed record BibleOpDto(
    string Kind,
    string? Summary,
    string? Name,
    string? Description,
    string? Text,
    string? Reason)
{
    /// <summary>The wire op of a stored one.</summary>
    /// <param name="op">The stored op.</param>
    /// <returns>The DTO.</returns>
    public static BibleOpDto FromDomain(BibleOp op)
    {
        ArgumentNullException.ThrowIfNull(op);

        return new BibleOpDto(Wire(op.Kind), op.Summary, op.Name, op.Description, op.Text, op.Reason);
    }

    /// <summary>The snake_case wire name of an op kind.</summary>
    /// <param name="kind">The Domain op kind.</param>
    /// <returns>The member name the client reads.</returns>
    public static string Wire(BibleOpKind kind) => kind switch
    {
        BibleOpKind.AddSummary => "add_summary",
        BibleOpKind.AddCharacter => "add_character",
        BibleOpKind.AddPlace => "add_place",
        BibleOpKind.AddRule => "add_rule",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bible op kind."),
    };
}

/// <summary>
/// The counts of a pending proposal (02.6.3-T1, plan §4 U1): how many characters, places and rules the
/// ops would add, and the episode the summary op belongs to, so the banner needs no op semantics.
/// </summary>
/// <param name="Characters">How many <c>add_character</c> ops are pending.</param>
/// <param name="Places">How many <c>add_place</c> ops are pending.</param>
/// <param name="Rules">How many <c>add_rule</c> ops are pending.</param>
/// <param name="SummaryForEpisodeId">The episode an <c>add_summary</c> op describes, when one is pending.</param>
public sealed record BibleProposalPreviewDto(int Characters, int Places, int Rules, Guid? SummaryForEpisodeId)
{
    /// <summary>Counts the ops of a stored proposal.</summary>
    /// <param name="ops">The pending ops.</param>
    /// <param name="proposedFromEpisodeId">The episode the proposal was written for, if the row records one.</param>
    /// <returns>The counts.</returns>
    public static BibleProposalPreviewDto FromOps(IReadOnlyList<BibleOp> ops, Guid? proposedFromEpisodeId)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var characters = 0;
        var places = 0;
        var rules = 0;
        var hasSummary = false;

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case BibleOpKind.AddSummary:
                    hasSummary = true;
                    break;
                case BibleOpKind.AddCharacter:
                    characters++;
                    break;
                case BibleOpKind.AddPlace:
                    places++;
                    break;
                case BibleOpKind.AddRule:
                    rules++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ops), op.Kind, "Unknown bible op kind.");
            }
        }

        return new BibleProposalPreviewDto(characters, places, rules, hasSummary ? proposedFromEpisodeId : null);
    }
}
