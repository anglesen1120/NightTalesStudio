using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Commands.AddSource;

/// <summary>
/// One source on the wire (02.5.1 plan §5.1). <see cref="Url"/> is the normalised URL; the pasted one is only in the
/// request. The extracted text is never part of it.
/// </summary>
public sealed record SourceDto(
    Guid Id,
    StorySourceKind Kind,
    string? Url,
    string? NormalizedUrl,
    string? RegistrableDomain,
    string? NoteText,
    StorySourceStatus Status,
    string? ErrorCode,
    int? HttpStatus,
    string? Title,
    string? FinalUrl,
    string? ContentType,
    int? TextChars,
    string? ContentSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FetchedAt)
{
    /// <summary>Projects a source row.</summary>
    /// <param name="source">The tracked source.</param>
    internal static SourceDto From(StorySource source) => new(
        source.Id,
        source.Kind,
        source.Url,
        source.NormalizedUrl,
        source.RegistrableDomain,
        source.NoteText,
        source.Status,
        source.ErrorCode,
        source.HttpStatus,
        source.Title,
        source.FinalUrl,
        source.ContentType,
        source.TextChars,
        source.ContentSha256,
        source.CreatedAt,
        source.FetchedAt);
}
