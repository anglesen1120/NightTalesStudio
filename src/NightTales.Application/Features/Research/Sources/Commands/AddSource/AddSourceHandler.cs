using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Commands.AddSource;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources</c> (02.5.1 T1 Design): the order of the checks is the contract,
/// because the acceptance checks assert the codes —
/// 404 → state gate 409 → URL syntax 400 → blocklist 400 → episode cap 409 → 24 h cap 429 → duplicate 409 → insert.
/// </summary>
/// <remarks>
/// The episode row lock is taken first, so the counts and the insert of two parallel adds cannot interleave. A
/// rejected URL is never persisted. Nothing here logs the URL, the note or a title (02 D19).
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The caller.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="psl">The public-suffix list.</param>
/// <param name="blocklist">The blocked domains.</param>
/// <param name="options">The caps.</param>
public sealed class AddSourceHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IPublicSuffixList psl,
    DomainBlocklist blocklist,
    SourcesOptions options)
    : ICommandHandler<AddSourceCommand, ErrorOr<SourceDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceDto>> Handle(AddSourceCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return Errors.NotFound();
        }

        var loaded = await SourceGate.LoadEpisodeLockedAsync(db, command.EpisodeId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
        {
            return loaded.Errors;
        }

        var episode = loaded.Value;
        if (SourceGate.EnsureEditable(episode) is { IsError: true } gate)
        {
            return gate.Errors;
        }

        var now = clock.UtcNow;
        var isUrl = !string.IsNullOrWhiteSpace(command.Url);

        SourceUrl? sourceUrl = null;
        if (isUrl)
        {
            var parsed = SourceUrl.Parse(command.Url, psl);
            if (parsed.IsError)
            {
                return parsed.Errors;
            }

            sourceUrl = parsed.Value;
            if (blocklist.IsBlocked(sourceUrl.RegistrableDomain))
            {
                return SourceErrors.SourceDomainNotAllowed;
            }
        }

        var onEpisode = await db.StorySources
            .CountAsync(source => source.EpisodeId == episode.Id, cancellationToken)
            .ConfigureAwait(false);
        if (onEpisode >= options.MaxPerEpisode)
        {
            return Error.Conflict(
                ErrorCodes.SourceLimitReached,
                $"An episode holds at most {options.MaxPerEpisode} sources.",
                new Dictionary<string, object> { [ErrorMetadata.Limit] = options.MaxPerEpisode });
        }

        if (sourceUrl is not null)
        {
            var cap = await SourceGate.CheckUrlCapAsync(db, userId, options, now, cancellationToken).ConfigureAwait(false);
            if (cap.IsError)
            {
                return cap.Errors;
            }

            var duplicate = await db.StorySources
                .AnyAsync(
                    source => source.EpisodeId == episode.Id && source.NormalizedUrl == sourceUrl.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicate)
            {
                return Error.Conflict(ErrorCodes.SourceDuplicate, "This URL is already a source of the episode.");
            }
        }

        var sourceId = Guid.CreateVersion7();
        var created = sourceUrl is not null
            ? StorySource.CreateUrl(sourceId, episode.UserId, episode.Id, sourceUrl, now)
            : StorySource.CreateNote(sourceId, episode.UserId, episode.Id, command.NoteText, now);
        if (created.IsError)
        {
            return created.Errors;
        }

        var source = created.Value;
        db.StorySources.Add(source);

        if (sourceUrl is not null)
        {
            db.FetchTasks.Add(FetchTask.CreateFetchSource(
                Guid.CreateVersion7(),
                episode.UserId,
                episode.Id,
                source.Id,
                sourceUrl.Value,
                sourceUrl.RegistrableDomain,
                now));

            SourceGate.RecordCreation(db, userId, now);
        }

        return SourceDto.From(source);
    }
}
