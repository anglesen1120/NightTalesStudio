using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Stories.Commands.ReopenStory;

/// <summary>Serialises the owner-scoped transition; the command pipeline commits the aggregate, audit and outbox together.</summary>
/// <param name="db">The tracked, tenant-filtered write model.</param>
/// <param name="clock">The transition clock.</param>
public sealed class ReopenStoryCommandHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<ReopenStoryCommand, ErrorOr<EpisodeStoryStateDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<EpisodeStoryStateDto>> Handle(ReopenStoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);
        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // State precedes If-Match: even an absent tag cannot unlock a rendering episode.
        if (episode.Status != EpisodeStatus.StoryApproved)
        {
            return Error.Conflict(ErrorCodes.EpisodeInvalidState, "Only an approved story can be reopened.");
        }

        var matched = StoryEtag.RequireAndMatch(command.IfMatch, episode.ApprovedStoryVersionId ?? episode.CurrentStoryVersionId);
        if (matched.IsError)
        {
            return matched.Errors;
        }

        if (episode.CurrentStoryVersionId is not { } currentVersionId || currentVersionId == Guid.Empty)
        {
            return Error.Conflict(ErrorCodes.EpisodeInvalidState, "The episode has no current story version.");
        }

        var reopened = episode.ReopenStory(clock.UtcNow);
        if (reopened.IsError)
        {
            return reopened.Errors;
        }

        command.ReopenedStoryVersionId = matched.Value;
        return new EpisodeStoryStateDto(episode.Id, currentVersionId, StoryEtag.For(currentVersionId), "story_review");
    }
}
