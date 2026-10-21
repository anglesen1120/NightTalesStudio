using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Commands.AddSourceCandidate;
using NightTales.Application.Features.Research.Commands.FindSources;
using NightTales.Application.Features.Research.Commands.GenerateFactSheet;
using NightTales.Domain.Research;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The research half of a true-crime run (02.8.3 plan §3.5 rows <c>idea_picked → researching</c>,
/// <c>researching → waiting_owner</c> and F33): it finds sources when the platform has a go, drafts the fact sheet
/// once at least one source is readable, and stops the run for the owner when the sheet is drafted or when the
/// run needs the owner to paste the sources themselves.
/// </summary>
/// <remarks>
/// <para>
/// <b>One unit of work per call</b>, in this order — the first match wins:
/// </para>
/// <list type="number">
/// <item><description>
/// a draft or approved fact sheet exists → the research is over: <c>waiting_owner</c> with
/// <see cref="WaitingFor.FactSheet"/> (plan §3.5 row 205). True crime never auto-approves a sheet.
/// </description></item>
/// <item><description>a fetched source exists and the sheet step is behind → send <c>GenerateFactSheetCommand</c> (key <c>…:fact_sheet</c>);</description></item>
/// <item><description>proposed source candidates exist → add them through <c>AddSourceCandidateCommand</c>, the same path the owner's UI uses;</description></item>
/// <item><description>candidates were proposed and every fetch failed, or the finder run ended without candidates → <c>waiting_owner</c> with <see cref="WaitingFor.Sources"/> (plan §7.3 row: the F33 behaviour even when the flag is on);</description></item>
/// <item><description>nothing was found yet → <c>FindSourcesCommand</c> (key <c>…:find_sources</c>) when <see cref="ISourceFinderGo"/> answers a go;</description></item>
/// <item><description>no go → <c>waiting_owner</c> with <see cref="WaitingFor.Sources"/>, step <c>sources_required</c> (F33: no finder call is attempted).</description></item>
/// </list>
/// <para>
/// <b>No step is written while the state stays <c>researching</c>.</b> The plan §3.5 transition table has no
/// <c>researching → researching</c> row, so a dispatch cannot record <c>find_sources</c> in the run's
/// <c>step</c> column; the paid commands are protected by their own <see cref="AutopilotIdempotencyKeys"/> keys
/// instead, and a repeated tick replays the first answer (both commands are <c>IIdempotentCommand</c>). The
/// <c>researching → waiting_owner</c> transition records <c>fact_sheet</c> and is the only write of this state.
/// </para>
/// <para>
/// <b>D19.</b> The search query is derived from the episode's brief, and neither the brief, the query, a source
/// URL nor a sheet's content is logged or tagged: the step returns closed step names and error codes only.
/// </para>
/// </remarks>
/// <param name="sender">The in-process mediator, already inside the run owner's scope.</param>
/// <param name="db">The tenant-filtered write model of the run's owner.</param>
/// <param name="finderGo">The 02.7.1 go, defaulting to the <c>source_finder</c> flag.</param>
public sealed class ResearchStep(ISender sender, IAppDbContext db, ISourceFinderGo finderGo) : IAutopilotStepHandler
{
    /// <summary>The code of a run whose episode is gone; the run cannot research anything any more.</summary>
    public const string EpisodeMissingCode = "episode_missing";

    /// <summary>How many proposed candidates one call adds, so one tick cannot open an unbounded number of fetches.</summary>
    public const int MaxCandidatesPerCall = 5;

    /// <inheritdoc />
    public AutopilotRunState State => AutopilotRunState.Researching;

    /// <inheritdoc />
    public async ValueTask<StepResult> HandleAsync(AutopilotStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        // Both dispatches of this state are paid (a source search and a fact sheet), so the series, its autopilot and
        // its authorization are re-checked here (task rows SEC-1, PF-5) instead of only at the idea step.
        var spend = await AutopilotStepSupport.SeriesMaySpendAsync(db, context, ct).ConfigureAwait(false);

        if (spend.Stop is { } refusal)
        {
            return refusal;
        }

        // The slot's research must be requested while the slot is still publishable (plan §3.4, task row CR-2): a
        // source search or a fact sheet bought past the cutoff is thrown away by the next step's own check.
        if (AutopilotStepSupport.SlotIsMissed(context))
        {
            return AutopilotStepSupport.FailSlotMissed(context);
        }

        if (run.EpisodeId is not { } episodeId)
        {
            // Researching is only reachable after the idea step created the episode; a row without one is a bug
            // the run cannot recover from, and the machine's `any → failed` row is the honest end.
            run.Fail(EpisodeMissingCode, context.Now);
            return StepResult.Stopped(EpisodeMissingCode);
        }

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);
        if (episode is null)
        {
            run.Fail(EpisodeMissingCode, context.Now);
            return StepResult.Stopped(EpisodeMissingCode);
        }

        // 1. A sheet means the research is over; the owner approves it next (true crime never auto-approves).
        if (await HasSheetAsync(episodeId, ct).ConfigureAwait(false))
        {
            return StopForFactSheet(run, context.Now);
        }

        // 2. At least one readable source: draft the sheet (its own step guard, and its own key).
        if (AutopilotRunStep.IsBehind(run.Step, AutopilotRunStep.FactSheet)
            && await HasFetchedSourceAsync(episodeId, ct).ConfigureAwait(false))
        {
            var drafted = await sender
                .Send(
                    new GenerateFactSheetCommand(
                        episodeId,
                        AutopilotIdempotencyKeys.For(run, AutopilotRunStep.FactSheet)),
                    ct)
                .ConfigureAwait(false);

            // A dispatch changes no column of the run: the sheet's own event is what moves it next.
            return drafted.IsError
                ? StepResult.Retry(drafted.FirstError.Code)
                : StepResult.Idle;
        }

        var candidates = await db.SourceCandidates
            .Where(candidate => candidate.EpisodeId == episodeId)
            .Select(candidate => new { candidate.Id, candidate.Status })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // 3. Candidates the finder proposed: add them the way the owner's "add" button does.
        var proposed = candidates
            .Where(candidate => candidate.Status == SourceCandidateStatus.Proposed)
            .Take(MaxCandidatesPerCall)
            .Select(candidate => candidate.Id)
            .ToArray();
        if (proposed.Length > 0)
        {
            return await AddCandidatesAsync(episodeId, proposed, ct).ConfigureAwait(false);
        }

        if (candidates.Count > 0)
        {
            // A candidate was added and its source is still fetching, or the add was refused by the cap: wait for
            // the fetcher rather than spending a second search.
            if (candidates.Exists(candidate => candidate.Status == SourceCandidateStatus.Added)
                || await HasPendingSourceAsync(episodeId, ct).ConfigureAwait(false))
            {
                return StepResult.Idle;
            }

            // Every candidate was dismissed and nothing is readable: the owner pastes the links (plan §7.3).
            return StopForSources(run, context.Now);
        }

        // 4. Nothing proposed yet. A finished finder run that found nothing is the plan §7.3 owner ask; otherwise
        // the search itself is the next unit of work, and without a go it never happens (F33).
        if (await HasFinishedFinderAsync(episodeId, ct).ConfigureAwait(false))
        {
            return StopForSources(run, context.Now);
        }

        if (!await finderGo.HasGoAsync(ct).ConfigureAwait(false))
        {
            return StopForSources(run, context.Now);
        }

        if (!AutopilotRunStep.IsBehind(run.Step, AutopilotRunStep.FindSources))
        {
            return StepResult.Idle;
        }

        var found = await sender
            .Send(
                new FindSourcesCommand(
                    episodeId,
                    Query: SearchQuery(episode.Brief),
                    Trigger: GenerationTrigger.Autopilot,
                    IdempotencyKey: AutopilotIdempotencyKeys.For(run, AutopilotRunStep.FindSources)),
                ct)
            .ConfigureAwait(false);

        return found.IsError
            ? StepResult.Retry(found.FirstError.Code)
            : StepResult.Idle;
    }

    /// <summary>
    /// The query the autopilot searches with: the episode's brief, normalised by the find command's own rule and
    /// bounded by the same 300-character cap the route validates, so a long brief cannot make every tick a 400.
    /// </summary>
    /// <param name="brief">The episode's brief.</param>
    /// <returns>The storable query, never logged.</returns>
    private static string? SearchQuery(string brief)
    {
        var query = FindSourcesCommand.NormalizeQuery(brief);
        if (query is null || query.Length <= FindSourcesCommand.MaxQueryLength)
        {
            return query;
        }

        var length = FindSourcesCommand.MaxQueryLength;

        // Never split a surrogate pair: the cap is expressed in UTF-16 units and half a pair is not text.
        if (char.IsHighSurrogate(query[length - 1]))
        {
            length--;
        }

        return query[..length];
    }

    /// <summary>Adds the proposed candidates one command at a time; a refusal the owner can resolve does not stop the rest.</summary>
    private async ValueTask<StepResult> AddCandidatesAsync(
        Guid episodeId,
        IReadOnlyList<Guid> proposed,
        CancellationToken ct)
    {
        foreach (var candidateId in proposed)
        {
            var added = await sender
                .Send(new AddSourceCandidateCommand(episodeId, candidateId), ct)
                .ConfigureAwait(false);
            if (!added.IsError)
            {
                continue;
            }

            var code = added.FirstError.Code;
            if (code is ErrorCodes.SourceCandidateNotProposed
                or ErrorCodes.SourceDuplicate
                or ErrorCodes.SourceLimitReached)
            {
                // A concurrent add, an already pasted link or the per-episode cap: the candidate is not addable
                // now, and the run keeps what it has instead of failing the tick.
                continue;
            }

            return StepResult.Retry(code);
        }

        return StepResult.Idle;
    }

    /// <summary>Waits for the owner to approve the drafted sheet (or every approved sheet is stale and a new one is needed).</summary>
    private static StepResult StopForFactSheet(AutopilotRun run, DateTimeOffset now)
    {
        run.SetWaitingFor(WaitingFor.FactSheet);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.FactSheet, now);

        return StepResult.Stopped(AutopilotRunStep.FactSheet);
    }

    /// <summary>The F33 ask: the owner pastes the links, and no finder call is made.</summary>
    private static StepResult StopForSources(AutopilotRun run, DateTimeOffset now)
    {
        run.SetWaitingFor(WaitingFor.Sources);
        run.AdvanceTo(AutopilotRunState.WaitingOwner, AutopilotRunStep.SourcesRequired, now);

        return StepResult.Stopped(AutopilotRunStep.SourcesRequired);
    }

    private Task<bool> HasSheetAsync(Guid episodeId, CancellationToken ct) =>
        db.FactSheets.AnyAsync(
            sheet => sheet.EpisodeId == episodeId
                && (sheet.Status == FactSheetStatus.Draft || sheet.Status == FactSheetStatus.Approved),
            ct);

    private Task<bool> HasFetchedSourceAsync(Guid episodeId, CancellationToken ct) =>
        db.StorySources.AnyAsync(
            source => source.EpisodeId == episodeId && source.Status == StorySourceStatus.Fetched,
            ct);

    private Task<bool> HasPendingSourceAsync(Guid episodeId, CancellationToken ct) =>
        db.StorySources.AnyAsync(
            source => source.EpisodeId == episodeId && source.Status == StorySourceStatus.Pending,
            ct);

    private Task<bool> HasFinishedFinderAsync(Guid episodeId, CancellationToken ct) =>
        db.GenerationRuns.AnyAsync(
            run => run.EpisodeId == episodeId
                && run.Kind == GenerationKind.FindSources
                && (run.Status == GenerationRunStatus.Succeeded
                    || run.Status == GenerationRunStatus.Failed
                    || run.Status == GenerationRunStatus.Cancelled),
            ct);
}
