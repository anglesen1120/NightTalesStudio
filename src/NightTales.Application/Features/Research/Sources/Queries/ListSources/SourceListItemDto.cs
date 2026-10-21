using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Queries.ListSources;

/// <summary>
/// A list row: the source DTO minus <c>normalizedUrl</c> and <c>contentSha256</c>, plus <c>isCited</c> and the
/// UI copy key of the error. The extracted text is not returned.
/// </summary>
/// <param name="IsCited">False until 02.5.4-T1 fills it from the fact sheet.</param>
/// <param name="ErrorMessageKey">The UI copy key for <paramref name="ErrorCode"/>, or <see langword="null"/>.</param>
public sealed record SourceListItemDto(
    Guid Id,
    StorySourceKind Kind,
    string? Url,
    string? RegistrableDomain,
    string? NoteText,
    StorySourceStatus Status,
    string? ErrorCode,
    string? ErrorMessageKey,
    int? HttpStatus,
    string? Title,
    string? FinalUrl,
    string? ContentType,
    int? TextChars,
    bool IsCited,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FetchedAt);
