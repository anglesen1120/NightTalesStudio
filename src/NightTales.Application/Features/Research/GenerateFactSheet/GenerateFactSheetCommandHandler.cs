using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Research.GenerateFactSheet;

/// <summary>
/// Queues a fact-sheet extraction (02.5.3 plan §4.1 U4.1), in the transaction the D5 pipeline opened and
/// behind the episode row lock, so the state read, the fetched-source gate, the active-run check and the run
/// insert of two concurrent calls cannot interleave. D1: phase P is metering-only, so a reservation is always
/// granted and there is no insufficient-credit path (the story handler has none either).
/// </summary>
/// <remarks>
/// The episode's status is <em>not</em> changed: a fact sheet has no episode state of its own (02.5.3 plan §4.1),
/// so the run carries the episode's current status as <c>previous_episode_status</c> and the job compares it at
/// start (<c>sources_changed</c>). The <c>EpisodeUpdated</c> NOTIFY comes from the
/// <c>FactSheetGenerationRequested</c> outbox row the domain event writes, never from this handler.
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="costEstimator">The worst-case cost of one facts call.</param>
/// <param name="budgetGuard">The platform spend cap.</param>
/// <param name="creditLedger">The credit reservation (metering-only in phase P).</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class GenerateFactSheetCommandHandler(
    IAppDbContext db,
    IFactSheetCostEstimator costEstimator,
    IBudgetGuard budgetGuard,
    ICreditLedger creditLedger,
    IClock clock)
    : ICommandHandler<GenerateFactSheetCommand, ErrorOr<FactSheetRunDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<FactSheetRunDto>> Handle(
        GenerateFactSheetCommand command,
        CancellationToken cancellationToken)
    {
        // The row lock first, then the tenant-filtered load (another user's id and a missing id are both 404).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // A shared lock on the series row before Topic is read, so a concurrent PATCH to true_crime cannot slip
        // past the topic gate of the step below (SEC-2, as in GenerateStoryCommandHandler).
        await db.LockSeriesForShareAsync(episode.SeriesId, cancellationToken).ConfigureAwait(false);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            return Errors.NotFound();
        }

        // A fact sheet is the true-crime path only (02.5.3 plan §4.1); there is no dedicated wrong-topic code in the
        // catalogue, so the refusal is the state code of every other unmet precondition on this route.
        if (series.Topic != Topic.TrueCrime)
        {
            return Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "A fact sheet can only be built for a true-crime series.");
        }

        if (episode.Status is not (EpisodeStatus.Draft or EpisodeStatus.StoryReview))
        {
            return Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "A fact sheet cannot be built in the episode's current state.");
        }

        // At least one fetched source: a url source whose text is stored, or the user's own note (02.5.3 plan §4.1).
        var hasFetchedSource = await db.StorySources
            .AnyAsync(
                source => source.EpisodeId == episode.Id && source.Status == StorySourceStatus.Fetched,
                cancellationToken)
            .ConfigureAwait(false);
        if (!hasFetchedSource)
        {
            return Error.Conflict(
                ErrorCodes.FactSheetSourcesInsufficient,
                "At least one fetched source is required before a fact sheet can be built.");
        }

        // The fastest error for an active run; the D2 partial unique index (`ux_generation_runs_active_episode`)
        // stays the guarantee and is mapped to this same 409 at commit (EfUnitOfWork).
        var active = await db.GenerationRuns
            .AnyAsync(
                run => run.EpisodeId == episode.Id
                    && run.Kind == GenerationKind.FactSheet
                    && (run.Status == GenerationRunStatus.Queued || run.Status == GenerationRunStatus.Running),
                cancellationToken)
            .ConfigureAwait(false);
        if (active)
        {
            return InProgress();
        }

        var bound = await costEstimator.WorstCaseAsync(cancellationToken).ConfigureAwait(false);
        if (bound.IsError)
        {
            return bound.Errors;
        }

        var budget = await budgetGuard
            .CheckAsync(new CostBound(bound.Value.WorstCaseUsd), cancellationToken)
            .ConfigureAwait(false);
        if (budget.IsError)
        {
            return budget.Errors;
        }

        var reservation = await creditLedger
            .ReserveAsync(episode.UserId, CreditOperation.FactSheet, episode.Id, bound.Value, cancellationToken)
            .ConfigureAwait(false);
        if (reservation.IsError)
        {
            return reservation.Errors;
        }

        var now = clock.UtcNow;
        var queued = GenerationRun.Queue(
            episode.UserId,
            episode.SeriesId,
            episode.Id,
            GenerationKind.FactSheet,
            GenerationTrigger.User,
            command.IdempotencyKey,
            episode.Status,
            null,
            reservation.Value.Value,
            bound.Value.WorstCaseUsd,
            now,
            // D2 counts `kind = story` only: a fact-sheet run never consumes the FR-02-7 daily allowance.
            countsTowardLimit: false);
        if (queued.IsError)
        {
            return queued.Errors;
        }

        var run = queued.Value;
        db.GenerationRuns.Add(run);

        // The transition raises FactSheetGenerationRequested; the pipeline commits it with the run, and the
        // Worker's consumer enqueues GenerateFactSheetJob(runId) and sends the EpisodeUpdated NOTIFY.
        var requested = episode.RequestFactSheetGeneration(run.Id, GenerationTrigger.User, now);
        if (requested.IsError)
        {
            return requested.Errors;
        }

        command.Outcome = new FactSheetRunOutcome(run.Id, bound.Value.WorstCaseUsd);

        return new FactSheetRunDto(run.Id, null, "queued");
    }

    private static Error InProgress() =>
        Error.Conflict(
            ErrorCodes.FactSheetGenerationInProgress,
            "A fact-sheet generation is already queued or running for this episode.");
}
