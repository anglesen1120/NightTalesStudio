namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// The text of one cited source, as the verbatim-copy check sees it (02.5.3-T2 rule 4). Only the sources a
/// fact actually cites are passed to <see cref="FactSheetValidator"/>, never the whole corpus.
/// </summary>
/// <param name="SourceId">The <c>story_sources.id</c> the fact cites.</param>
/// <param name="Text">The source text the statement is checked against.</param>
public sealed record SourceText(Guid SourceId, string Text);
