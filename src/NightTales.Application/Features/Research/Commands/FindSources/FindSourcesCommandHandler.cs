using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Research.Commands.FindSources;

/// <summary>
/// Queues a source search (02.7 plan §4 U1), in the transaction the D5 pipeline opened and behind the episode row
/// lock, so the state read, the flag and allow-list gates, the active-run check and the run insert of two
/// concurrent calls cannot interleave. The gates run in the order the task file fixes: scope, flag, episode,
/// allow-list, state, active run, budget, reservation, run, event.
/// </summary>
/// <remarks>
/// <para>
/// The episode's status is <em>not</em> changed: a source search has no episode state of its own (02.7 plan §3), so
/// the run carries the episode's current status as <c>previous_episode_status</c> only, exactly as the fact-sheet
/// handler does, and nothing compares that value later.
/// </para>
/// <para>
/// The typed query is normalised once here and handed to the episode transition, which raises
/// <c>SourceFindRequested</c>; the outbox payload is the only place it is stored (D19). A null query means the
/// caller typed nothing and the job derives one from the episode title and brief (plan §3).
/// </para>
/// <para>
/// No realtime event is sent when the run is queued. The only consumer of the <c>SourceFindRequested</c> outbox
/// row is the 02.7.2-T3 finder job handler; until it ships the flag stays off in production, so no row is written.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="flags">The <c>source_finder</c> kill switch (09 BD13).</param>
/// <param name="allowList">The reviewed research sites; a language with none approved cannot run.</param>
/// <param name="costEstimator">The worst-case cost of one search.</param>
/// <param name="budgetGuard">The platform spend cap.</param>
/// <param name="creditLedger">The credit reservation (metering-only in phase P).</param>
/// <param name="currentUser">The actor, which decides whether a non-user trigger is allowed.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class FindSourcesCommandHandler(
    IAppDbContext db,
    IFeatureFlags flags,
    IResearchAllowList allowList,
    IFindSourcesCostEstimator costEstimator,
    IBudgetGuard budgetGuard,
    ICreditLedger creditLedger,
    ICurrentUser currentUser,
    IClock clock)
    : ICommandHandler<FindSourcesCommand, ErrorOr<FindSourcesAccepted>>
{
    /// <summary>The flag key of the source finder (02.7, <c>feature_flags</c>).</summary>
    public const string FeatureFlag = "source_finder";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<FindSourcesAccepted>> Handle(
        FindSourcesCommand command,
        CancellationToken cancellationToken)
    {
        // Step 1: only a system scope may name a non-user trigger (the autopilot's SystemContext, 02.8.3).
        if (command.Trigger != GenerationTrigger.User && currentUser.Actor != ActorType.System)
        {
            return Error.Forbidden(
                ErrorCodes.Forbidden,
                "Only the platform can start an automatic source search.");
        }

        // Step 2: the flag first, because it is the kill switch of the whole feature and answers 404 like the
        // route's RequireFeature filter. The lookup fails closed (09 BD13), so a database problem is also a 404.
        if (!await flags.IsEnabledAsync(FeatureFlag, cancellationToken).ConfigureAwait(false))
        {
            return FeatureDisabled();
        }

        // Step 3: the row lock first, then the tenant-filtered load (another user's id and a missing id are both 404).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // A shared lock on the series row before its columns are read, so a concurrent PATCH to the language or the
        // archive state cannot slip past the gates below (SEC-2/3, as in GenerateFactSheetCommandHandler).
        await db.LockSeriesForShareAsync(episode.SeriesId, cancellationToken).ConfigureAwait(false);

        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            return Errors.NotFound();
        }

        // SEC-3: no new run for an archived series (the same refusal as adding a source to it).
        if (series.ArchivedAt is not null)
        {
            return Error.Conflict(
                ErrorCodes.SeriesArchived,
                "The series is archived; sources cannot be searched for it.");
        }

        // Step 4 (AL-S47-1): with the flag on, a language the reviewed allow-list has nothing approved for cannot
        // search anywhere, so the feature is still effectively off for this series and answers the same 404.
        if (allowList.Approved(series.Language).Count == 0)
        {
            return FeatureDisabled();
        }

        // Step 5: a source search is only useful while the story can still change.
        if (episode.Status is not (EpisodeStatus.Draft or EpisodeStatus.StoryReview))
        {
            return Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "Sources cannot be searched for in the episode's current state.");
        }

        // Step 6: the fastest error for an active run of this kind; the D2 partial unique index
        // (`ux_generation_runs_active_episode`, keyed by episode and kind) stays the guarantee and is mapped to this
        // same 409 at commit (EfUnitOfWork).
        var active = await db.GenerationRuns
            .AnyAsync(
                run => run.EpisodeId == episode.Id
                    && run.Kind == GenerationKind.FindSources
                    && (run.Status == GenerationRunStatus.Queued || run.Status == GenerationRunStatus.Running),
                cancellationToken)
            .ConfigureAwait(false);
        if (active)
        {
            return InProgress();
        }

        // Step 7: the platform spend cap answers 429 spend_cap_reached before the reservation and before any row.
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

        // Step 8: the reference id is the episode, as in both existing handlers (the run row does not exist yet).
        var reservation = await creditLedger
            .ReserveAsync(episode.UserId, CreditOperation.SourceFind, episode.Id, bound.Value, cancellationToken)
            .ConfigureAwait(false);
        if (reservation.IsError)
        {
            return reservation.Errors;
        }

        // Step 9: the run row. D2 counts `kind = story` only, so a source search never consumes the FR-02-7 allowance
        // (plan §7: no daily limit on finds besides the budget).
        var now = clock.UtcNow;
        var queued = GenerationRun.Queue(
            episode.UserId,
            episode.SeriesId,
            episode.Id,
            GenerationKind.FindSources,
            command.Trigger,
            command.IdempotencyKey,
            episode.Status,
            null,
            reservation.Value.Value,
            bound.Value.WorstCaseUsd,
            now,
            countsTowardLimit: false);
        if (queued.IsError)
        {
            return queued.Errors;
        }

        var run = queued.Value;
        db.GenerationRuns.Add(run);

        // Step 10: the transition raises SourceFindRequested, the only carrier of the typed query; the pipeline
        // commits it with the run, and the Worker's 02.7.2-T3 consumer enqueues FindSourcesJob(runId) — nothing else.
        var requested = episode.RequestSourceFind(
            run.Id,
            command.Trigger,
            FindSourcesCommand.NormalizeQuery(command.Query),
            now);
        if (requested.IsError)
        {
            return requested.Errors;
        }

        command.Outcome = new FindSourcesOutcome(run.Id, bound.Value.WorstCaseUsd);

        return new FindSourcesAccepted(run.Id);
    }

    private static Error FeatureDisabled() =>
        Error.NotFound(
            ErrorCodes.FeatureDisabled,
            "The source finder is not available for this series.");

    private static Error InProgress() =>
        Error.Conflict(
            ErrorCodes.FindSourcesInProgress,
            "A source search is already queued or running for this episode.");
}
