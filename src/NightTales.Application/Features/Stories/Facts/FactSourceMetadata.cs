using NightTales.Domain.Research;

namespace NightTales.Application.Features.Stories.Facts;

/// <summary>
/// One <c>story_sources</c> row as <see cref="FactSourcesBuilder"/> needs it (02.5.5-T1, D2): the id, the kind and the
/// display metadata only. The caller projects it from the row, so the fetched <c>extracted_text</c> and the owner's
/// <c>note_text</c> are deliberately absent and can never reach the stored story, a log or <c>error_detail</c>
/// (D19, F12).
/// </summary>
/// <param name="Id">The source's id.</param>
/// <param name="Kind">Whether the source is a pasted URL or the owner's own note.</param>
/// <param name="Title">The source title, already falling back to the final URL; null when neither is known.</param>
/// <param name="Url">The source URL; null or empty for a user note.</param>
public sealed record FactSourceMetadata(Guid Id, StorySourceKind Kind, string? Title, string? Url);
