using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Stories.Commands.GenerateStory;

/// <summary>
/// Queues a story generation (02.2.2 plan §4 U1, steps 1–12). Everything runs in the transaction the D5 pipeline
/// opened, behind the episode row lock of step 1, so the status read, the daily count, the active-run check and
/// the run insert of two concurrent calls cannot interleave; a failing step returns its error and the pipeline
/// rolls the lock, the reservation, the run row and the outbox row back together.
/// </summary>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="currentUser">The scope's actor; a non-user trigger needs a system scope.</param>
/// <param name="factSheetGuard">The true-crime guard (step 4).</param>
/// <param name="limitGuard">The FR-02-7 rolling-window limit (step 5, user triggers only).</param>
/// <param name="costEstimator">The worst-case price of the run (step 7).</param>
/// <param name="budgetGuard">The platform spend cap (step 8).</param>
/// <param name="creditLedger">The credit reservation (step 9).</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class GenerateStoryCommandHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    IFactSheetGuard factSheetGuard,
    IGenerationLimitGuard limitGuard,
    IStoryCostEstimator costEstimator,
    IBudgetGuard budgetGuard,
    ICreditLedger creditLedger,
    IClock clock)
    : ICommandHandler<GenerateStoryCommand, ErrorOr<GenerateStoryAccepted>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<GenerateStoryAccepted>> Handle(
        GenerateStoryCommand command,
        CancellationToken cancellationToken)
    {
        // Step 1: the row lock first, then the tenant-filtered load (another user's id and a missing id are both 404).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // Step 2: only a system scope may name a non-user trigger.
        if (command.Trigger != GenerationTrigger.User && currentUser.Actor != ActorType.System)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only the platform can start an automatic story generation.");
        }

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            return Errors.NotFound();
        }

        // Step 3 (and the loser of two concurrent calls, which finds the winner's committed state).
        if (episode.Status == EpisodeStatus.StoryGenerating)
        {
            return InProgress();
        }

        if (episode.Status is not (EpisodeStatus.Draft or EpisodeStatus.StoryReview))
        {
            return Error.Conflict(ErrorCodes.EpisodeInvalidState, "A story cannot be generated in the episode's current state.");
        }

        // Step 4: a true-crime episode needs an approved fact sheet; its id is pinned on the run.
        var sheet = await factSheetGuard
            .EnsureApprovedAsync(episode.Id, series.Topic, cancellationToken)
            .ConfigureAwait(false);
        if (sheet.IsError)
        {
            return sheet.Errors;
        }

        // Step 5: FR-02-7 counts user-triggered runs only (02 D2); the autopilot is capped by its own per-run limits.
        if (command.Trigger == GenerationTrigger.User)
        {
            var limit = await limitGuard
                .CheckAsync(episode.Id, episode.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (limit.IsError)
            {
                return limit.Errors;
            }
        }

        // Step 6: the fastest error for an active run; the partial unique index stays the guarantee.
        var active = await db.GenerationRuns
            .AnyAsync(
                run => run.EpisodeId == episode.Id
                    && run.Kind == GenerationKind.Story
                    && (run.Status == GenerationRunStatus.Queued || run.Status == GenerationRunStatus.Running),
                cancellationToken)
            .ConfigureAwait(false);
        if (active)
        {
            return InProgress();
        }

        // Step 7.
        var bound = await costEstimator
            .WorstCaseAsync(episode.LengthTier, series.Language, cancellationToken)
            .ConfigureAwait(false);
        if (bound.IsError)
        {
            return bound.Errors;
        }

        // Step 8.
        var budget = await budgetGuard
            .CheckAsync(new CostBound(bound.Value.WorstCaseUsd), cancellationToken)
            .ConfigureAwait(false);
        if (budget.IsError)
        {
            return budget.Errors;
        }

        // Step 9.
        var reservation = await creditLedger
            .ReserveAsync(episode.UserId, CreditOperation.StoryGenerate, episode.Id, bound.Value, cancellationToken)
            .ConfigureAwait(false);
        if (reservation.IsError)
        {
            return reservation.Errors;
        }

        // Step 10.
        var now = clock.UtcNow;
        var queued = GenerationRun.Queue(
            episode.UserId,
            episode.SeriesId,
            episode.Id,
            GenerationKind.Story,
            command.Trigger,
            command.IdempotencyKey,
            episode.Status,
            sheet.Value.FactSheetId,
            reservation.Value.Value,
            bound.Value.WorstCaseUsd,
            now);
        if (queued.IsError)
        {
            return queued.Errors;
        }

        var run = queued.Value;
        db.GenerationRuns.Add(run);

        // Step 11: the transition raises StoryGenerationRequested; the pipeline commits it with the run.
        var requested = episode.RequestStoryGeneration(run.Id, command.Trigger, now);
        if (requested.IsError)
        {
            return requested.Errors;
        }

        command.Outcome = new GenerateStoryOutcome(
            run.Id,
            Wire(command.Trigger),
            Wire(episode.LengthTier),
            bound.Value.WorstCaseUsd);

        // Step 12.
        return new GenerateStoryAccepted(run.Id, "queued", "queued");
    }

    private static Error InProgress() =>
        Error.Conflict(ErrorCodes.StoryGenerationInProgress, "A story generation is already queued or running for this episode.");

    private static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
