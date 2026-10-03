using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Commands.AddSource;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources</c> (02.5.1 plan §4 UC1/UC2): add one pasted URL or one note.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="Url"/> and <see cref="NoteText"/> is set; the validator enforces it. A URL source
/// inserts its <c>story_sources</c> row and its <c>fetch_tasks</c> row in the command's one transaction.
/// </para>
/// <para>
/// The <c>Idempotency-Key</c> is optional on this route: a replayed key returns the first response and creates no
/// second row (09 README D6).
/// </para>
/// </remarks>
/// <param name="EpisodeId">The route value; the episode must belong to the caller.</param>
/// <param name="Url">The URL as pasted, or <see langword="null"/>.</param>
/// <param name="NoteText">The user's note, or <see langword="null"/>.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> header, or <see langword="null"/> when absent.</param>
[Audited("episode.source_added", Category.Episodes, Visibility.User)]
public sealed record AddSourceCommand(Guid EpisodeId, string? Url, string? NoteText, string? IdempotencyKey = null)
    : ICommand<ErrorOr<SourceDto>>, IAuditDataFromResponse<ErrorOr<SourceDto>>, IIdempotentCommand
{
    /// <summary>
    /// The projection without a response (D13: ids and codes only — never the URL or the note): the episode is the
    /// entity and the kind is the one fact the command itself knows. <c>AuditBehavior</c> uses
    /// <see cref="ToAuditData(ErrorOr{SourceDto})"/> instead, once the handler has produced the source.
    /// </summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(
        null,
        "episode",
        EpisodeId,
        new Dictionary<string, object> { ["kind"] = string.IsNullOrWhiteSpace(Url) ? "user_note" : "url" });

    /// <summary>
    /// The row's projection (02.5.1-T1 Design step 8): <c>{ sourceId, kind, registrableDomain, status }</c> of the
    /// created source. <c>registrableDomain</c> is omitted for a note; the URL, the note and the title
    /// never appear (D13/D19).
    /// </summary>
    /// <param name="response">The created source.</param>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData? ToAuditData(ErrorOr<SourceDto> response)
    {
        var source = response.Value;
        var summary = new Dictionary<string, object>
        {
            ["sourceId"] = source.Id,
            ["kind"] = source.Kind == StorySourceKind.Url ? "url" : "user_note",
            ["status"] = source.Status.ToString().ToLowerInvariant(),
        };

        if (source.RegistrableDomain is { } domain)
        {
            summary["registrableDomain"] = domain;
        }

        return new AuditData(null, "episode", EpisodeId, summary);
    }
}
