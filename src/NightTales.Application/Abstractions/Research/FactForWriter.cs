namespace NightTales.Application.Abstractions.Research;

/// <summary>One approved fact as the writer sees it (02 README D6/D21): a statement and source aliases, never source ids.</summary>
/// <param name="Id">The fact's alias within the sheet, for example <c>f1</c>.</param>
/// <param name="Statement">The fact's text.</param>
/// <param name="SourceAliases">The source aliases (<c>s1</c>…<c>s10</c>) that support the fact.</param>
public sealed record FactForWriter(string Id, string Statement, IReadOnlyList<string> SourceAliases);
