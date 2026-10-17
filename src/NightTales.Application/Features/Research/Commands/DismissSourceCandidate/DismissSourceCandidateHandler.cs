using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Commands.DismissSourceCandidate;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources/candidates/{candidateId}/dismiss</c> (02.7.2 T2 Design): flag one
/// proposed candidate <c>dismissed</c>; the episode's sources are untouched.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract: the episode row lock and the tenant-filtered load (another user's id, an unknown id and
/// a cancelled episode answer 404 <c>not_found</c>), then the candidate by <c>(id, episode_id)</c> — another user's
/// or another episode's candidate is the same 404 — then the Domain's own <c>Dismiss</c>: <c>proposed</c> →
/// <c>dismissed</c>, an already <c>dismissed</c> row is a no-op success (R8), an <c>added</c> row is 409
/// <c>source_candidate_not_proposed</c>.
/// </para>
/// <para>
/// The episode lock keeps a dismissal from committing between a concurrent add's status check and its
/// <c>MarkAdded</c>, so the candidate's status and its <c>source_id</c> can never disagree. It is deliberately not
/// the source state gate: a candidate is research bookkeeping, not content, so dismissing one on an approved story
/// is allowed (the plan's failure table lists the state gate for add only).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
public sealed class DismissSourceCandidateHandler(IAppDbContext db)
    : ICommandHandler<DismissSourceCandidateCommand, ErrorOr<Success>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> Handle(
        DismissSourceCandidateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var locked = await SourceGate.LoadEpisodeLockedAsync(db, command.EpisodeId, cancellationToken).ConfigureAwait(false);
        if (locked.IsError)
        {
            return locked.Errors;
        }

        var candidate = await db.SourceCandidates
            .SingleOrDefaultAsync(
                row => row.Id == command.CandidateId && row.EpisodeId == command.EpisodeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return Errors.NotFound();
        }

        var wasProposed = candidate.Status == SourceCandidateStatus.Proposed;
        if (candidate.Dismiss() is { IsError: true } dismissed)
        {
            return dismissed.Errors;
        }

        // The audit projection is built after the pipeline's success, so the no-op is known only here.
        command.Changed = wasProposed;

        return Result.Success;
    }
}
