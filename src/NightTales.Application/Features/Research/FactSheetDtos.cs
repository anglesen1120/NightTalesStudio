using System.Text.Json;
using NightTales.Application.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research;

/// <summary>
/// The fact-sheet DTOs of 02.5.4-T1 (plan §4, 02.5.3 plan §4 DTO block). They are the whole wire contract of
/// the three routes of this task, so every member is named for the client, never for the aggregate: the
/// <c>fs-</c> ETag of the version, the resolved sources of every fact with their alias, and the effective
/// <c>confidence</c> of a fact (T53).
/// </summary>
/// <remarks>
/// The status, origin and confidence members travel as the snake_case member name of the enum
/// (<c>draft</c>/<c>approved</c>/<c>superseded</c>, <c>model</c>/<c>user</c>, <c>high</c>/<c>medium</c>/
/// <c>low</c>), written through <see cref="FactSheetWire"/> so both the read and the write path spell them
/// the same way.
/// </remarks>
/// <param name="Id">The version id; the ETag names it (<c>"fs-&lt;id&gt;"</c>, D9).</param>
/// <param name="Version">The 1-based version number.</param>
/// <param name="Status">The version's lifecycle: <c>draft</c>, <c>approved</c> or <c>superseded</c>.</param>
/// <param name="Etag">The strong ETag of this version, quotes included.</param>
/// <param name="FactCount">How many facts the version stores.</param>
/// <param name="CreatedAt">When the version was created.</param>
public sealed record FactSheetSummaryDto(
    Guid Id,
    int Version,
    string Status,
    string Etag,
    int FactCount,
    DateTimeOffset CreatedAt) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}

/// <summary>
/// The current fact-sheet version with its facts and their resolved sources
/// (<c>GET /api/v1/episodes/{id}/fact-sheet</c>).
/// </summary>
/// <param name="Id">The version id; the ETag names it.</param>
/// <param name="Version">The 1-based version number.</param>
/// <param name="Status">The version's lifecycle: <c>draft</c>, <c>approved</c> or <c>superseded</c>.</param>
/// <param name="Etag">The strong ETag of this version, quotes included.</param>
/// <param name="Facts">Every stored fact, in stored order.</param>
/// <param name="PromptVersion">The D5 prompt version, or <c>"user"</c> for a hand edit.</param>
/// <param name="CreatedAt">When the version was created.</param>
/// <param name="ApprovedAt">When it was approved, or <see langword="null"/> while it is a draft.</param>
/// <param name="MaxFactId">The episode's fact-id watermark: the next added fact gets <c>f{MaxFactId + 1}</c> (F12).</param>
public sealed record FactSheetDto(
    Guid Id,
    int Version,
    string Status,
    string Etag,
    IReadOnlyList<FactItemDto> Facts,
    string PromptVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    int MaxFactId) : IHasETag
{
    /// <inheritdoc />
    string IHasETag.ETag => Etag;
}

/// <summary>One stored fact as the research tab renders it.</summary>
/// <param name="Id">The backend-assigned id, <c>f1…fN</c>; stable across versions (F12).</param>
/// <param name="Statement">The fact, NFC-normalised with collapsed whitespace.</param>
/// <param name="Sources">The cited sources, in citation order, resolved to their title and URL.</param>
/// <param name="SourceOverlap">The 02.5.6 overlap score of the statement against its cited sources; null when unscored or user-edited.</param>
/// <param name="Origin">Who produced the statement: <c>model</c> or <c>user</c>.</param>
/// <param name="Confidence">The effective confidence the version stores (<c>high</c>/<c>medium</c>/<c>low</c>, T53).</param>
public sealed record FactItemDto(
    string Id,
    string Statement,
    IReadOnlyList<FactSourceRefDto> Sources,
    decimal? SourceOverlap,
    string Origin,
    string Confidence);

/// <summary>
/// One source a fact cites, resolved through the episode's <c>story_sources</c>.
/// </summary>
/// <param name="SourceId">The <c>story_sources.id</c> the fact cites.</param>
/// <param name="Alias">The <c>s&lt;N&gt;</c> alias the version's <c>source_aliases</c> gave this source (CR-4).</param>
/// <param name="Title">The source title; the neutral <c>note</c> label for a user note, or the
/// <c>unavailable</c> placeholder when the source was deleted after the version was saved.</param>
/// <param name="Url">The source URL, or <see langword="null"/> for a user note and for a source that no longer exists.</param>
/// <param name="IsUserNote">Whether the cited source is the owner's own note (<c>kind = user_note</c>).</param>
public sealed record FactSourceRefDto(
    Guid SourceId,
    string Alias,
    string Title,
    string? Url,
    bool IsUserNote);

/// <summary>The body of the edit route: the full fact list of the next version.</summary>
/// <param name="Facts">Every fact the new version must hold, in display order; an empty list saves an empty draft.</param>
public sealed record FactSheetContentDto(IReadOnlyList<FactItemInputDto> Facts);

/// <summary>
/// One fact of an edit body. The backend recomputes every derived field: the id, the alias map, the overlap
/// score and the content hash are never read from the request.
/// </summary>
/// <param name="Id">A kept fact's existing id (<c>f1…fN</c>), or <see langword="null"/> for a new fact.</param>
/// <param name="Statement">The statement, validated with the same rules as the extraction output.</param>
/// <param name="SourceIds">The cited source ids, at least one, each belonging to this episode.</param>
/// <param name="Confidence">The confidence to store; null keeps the stored value of a kept fact and defaults a new one to <c>high</c> (T53).</param>
public sealed record FactItemInputDto(
    string? Id,
    string Statement,
    IReadOnlyList<Guid> SourceIds,
    FactConfidence? Confidence);

/// <summary>
/// The wire spelling of the fact sheet's enums and the two neutral labels the client localises (02.5.4-T1
/// decisions 12 and 16): one place, so the edit, the approve and the read path never spell a status
/// differently.
/// </summary>
public static class FactSheetWire
{
    /// <summary>
    /// The title of a <c>user_note</c> source, whose row has no title of its own: a neutral, key-free
    /// <c>note</c> the SPA localises (the focus state's copy is the client's).
    /// </summary>
    public const string UserNoteTitle = "note";

    /// <summary>
    /// The title of a cited source that no longer exists on the episode (deleted after the version was
    /// saved): the version keeps the citation, and the client renders it as a source that is gone.
    /// </summary>
    public const string MissingSourceTitle = "unavailable";

    /// <summary>The wire value of a version status (<c>draft</c>/<c>approved</c>/<c>superseded</c>).</summary>
    /// <param name="status">The stored status.</param>
    /// <returns>The snake_case wire spelling.</returns>
    public static string Status(FactSheetStatus status) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());

    /// <summary>The wire value of a fact's confidence (<c>high</c>/<c>medium</c>/<c>low</c>, T53).</summary>
    /// <param name="confidence">The stored confidence.</param>
    /// <returns>The snake_case wire spelling.</returns>
    public static string Confidence(FactConfidence confidence) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(confidence.ToString());

    /// <summary>The wire value of a fact's origin (<c>model</c>/<c>user</c>).</summary>
    /// <param name="origin">The stored origin.</param>
    /// <returns>The snake_case wire spelling.</returns>
    public static string Origin(FactOrigin origin) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(origin.ToString());
}
