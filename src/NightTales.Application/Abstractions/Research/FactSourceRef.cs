namespace NightTales.Application.Abstractions.Research;

/// <summary>One source a story's facts rest on, as the story page lists it (02 README D21, CR-24; built by 02.5.5-T1).</summary>
/// <param name="SourceId">The source's id.</param>
/// <param name="Title">The source title, or the "your note" label for a user note.</param>
/// <param name="Url">The source URL; null or empty for a user note.</param>
public sealed record FactSourceRef(Guid SourceId, string Title, string? Url);
