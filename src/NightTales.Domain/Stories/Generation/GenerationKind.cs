namespace NightTales.Domain.Stories.Generation;

/// <summary>
/// What a <see cref="GenerationRun"/> generates (02 D2), stored as snake_case text: <c>story</c>,
/// <c>fact_sheet</c>, <c>bible_proposal</c>, <c>ideas</c>, <c>find_sources</c>. <see cref="Ideas"/> and
/// <see cref="BibleProposal"/> are series-level (no episode); the other three are episode-scoped (TL T17).
/// </summary>
public enum GenerationKind
{
    Story,
    FactSheet,
    BibleProposal,
    Ideas,
    FindSources,
}
