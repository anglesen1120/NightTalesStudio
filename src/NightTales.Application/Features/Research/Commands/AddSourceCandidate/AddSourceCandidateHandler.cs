using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Commands.AddSourceCandidate;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources/candidates/{candidateId}/add</c> (02.7.2 T2 Design): add one
/// proposed candidate through the normal source path and flag it <c>added</c> in the same unit of work.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract: the episode row lock and the source state gate of <c>POST /sources</c>
/// (<see cref="ISourceAdder.LockEditableEpisodeAsync"/>), then the candidate — another user's id, another episode's
/// id and an unknown id all answer 404 <c>not_found</c> through the tenant filter and the
/// <c>(id, episode_id)</c> match — then 409 <c>source_candidate_not_proposed</c> for a candidate that is already
/// <c>added</c> or <c>dismissed</c>, then the 02.5.1-T1 checks of <see cref="ISourceAdder.AddLockedAsync"/>.
/// </para>
/// <para>
/// The lock is taken <em>before</em> the candidate is read, so two concurrent adds of one candidate serialise on the
/// episode and the loser reads the status the winner committed — 409 instead of a second source. On any adder error
/// the 02.5.1 error is returned unchanged and no mutation touches the candidate, so it stays <c>proposed</c>.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The caller, for the rolling 24 h cap.</param>
/// <param name="adder">The shared source write path; it runs in this command's transaction.</param>
public sealed class AddSourceCandidateHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    ISourceAdder adder) : ICommandHandler<AddSourceCandidateCommand, ErrorOr<SourceDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<SourceDto>> Handle(
        AddSourceCandidateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.Id is not { } userId)
        {
            return Errors.NotFound();
        }

        var locked = await adder.LockEditableEpisodeAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);
        if (locked.IsError)
        {
            return locked.Errors;
        }

        // After the lock: a concurrent add of this same candidate has committed by now, so this read sees its status.
        var candidate = await db.SourceCandidates
            .SingleOrDefaultAsync(
                row => row.Id == command.CandidateId && row.EpisodeId == command.EpisodeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return Errors.NotFound();
        }

        if (candidate.Status != SourceCandidateStatus.Proposed)
        {
            return Error.Conflict(
                ErrorCodes.SourceCandidateNotProposed,
                "Only a proposed candidate can be added to the episode.");
        }

        var added = await adder
            .AddLockedAsync(userId, locked.Value, candidate.Url, null, cancellationToken)
            .ConfigureAwait(false);
        if (added.IsError)
        {
            return added.Errors;
        }

        if (candidate.MarkAdded(added.Value.Id) is { IsError: true } marked)
        {
            return marked.Errors;
        }

        return added.Value;
    }
}
