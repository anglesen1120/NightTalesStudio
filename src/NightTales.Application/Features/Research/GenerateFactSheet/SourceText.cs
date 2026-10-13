namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The text of one url source of the run's plan, as the verbatim-copy check sees it (02.5.3-T2 rule 4). Every
/// statement is scored against the whole corpus of these, never only against the sources a fact cites, so a
/// fact that cites a note alias is still checked (SEC-5).
/// </summary>
/// <param name="SourceId">The <c>story_sources.id</c> of the source.</param>
/// <param name="Text">The source text the statement is checked against.</param>
public sealed record SourceText(Guid SourceId, string Text);
