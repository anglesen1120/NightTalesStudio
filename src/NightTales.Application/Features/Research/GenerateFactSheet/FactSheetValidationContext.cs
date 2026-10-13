namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// Everything <see cref="FactSheetValidator"/> needs that a fact sheet version does not carry itself
/// (02.5.3-T2): the alias map the extraction ran with, the highest fact id assigned so far, the overlap
/// threshold, the fact cap and the text of the resolvable sources.
/// </summary>
/// <param name="AliasToSourceId">The alias→<c>story_sources.id</c> map of the run's plan (T1 <c>FactInputPlan</c>).</param>
/// <param name="MaxFactId">The highest fact id already assigned in the episode; ids continue from here (F12).</param>
/// <param name="OverlapBlockThreshold">The 02.5.6 <c>Similarity:FactOverlapBlock</c> threshold above which a fact is a verbatim copy.</param>
/// <param name="MaxFacts">The fact cap of one version (40); survivors beyond it are dropped as <c>over_cap</c>.</param>
/// <param name="SourceTexts">
/// The text of every url source of the run's plan, by id — the corpus a statement is scored against. The user
/// note is deliberately not one of them (F18), so a cited id the map does not carry is the note and is kept;
/// a cited id the map carries with a blank text is a url source without text and fails the copy check
/// (SEC-5).
/// </param>
public sealed record FactSheetValidationContext(
    IReadOnlyDictionary<string, Guid> AliasToSourceId,
    int MaxFactId,
    decimal OverlapBlockThreshold,
    int MaxFacts,
    IReadOnlyDictionary<Guid, SourceText> SourceTexts);
