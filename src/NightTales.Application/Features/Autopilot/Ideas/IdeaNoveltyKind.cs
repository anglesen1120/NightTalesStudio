namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The two kind labels of the novelty corpus (02.8.1-T2 task Design "Novelty"): what the lexical pass measured a
/// candidate against, and what the stored <c>novelty</c> document reports back as <c>max_overlap_kind</c>.
/// </summary>
/// <remarks>
/// The label is the caller's, never the domain's: <c>LexicalCorpus</c> echoes whatever kind it was built with, so
/// the one place that gives it a meaning is here, and the checker and the planner both take it from here.
/// </remarks>
public static class IdeaNoveltyKind
{
    /// <summary>The corpus row is one of the series' approved episodes.</summary>
    public const string Episode = "episode";

    /// <summary>The corpus row is a pending idea of the series, from an earlier run.</summary>
    public const string PendingIdea = "pending_idea";
}
