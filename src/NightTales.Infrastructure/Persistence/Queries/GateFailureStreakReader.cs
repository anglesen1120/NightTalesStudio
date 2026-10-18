using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Queries;

/// <summary>
/// The <see cref="IGateFailureStreakReader"/> over <c>autopilot_runs</c> (02.8.2 plan §3.5): the newest terminal
/// runs of one series in slot order, and the series' newest run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ordering column.</b> The plan's SQL orders by the run's slot column and takes two rows; this reader
/// orders by <c>autopilot_runs.started_at DESC</c> with the v7 id as the tie-break, because the run shell of
/// 09.2.2-T1 has no <c>slot_at</c> column yet (02.8.3-T1 adds the state machine and the slot with it). The
/// existing descending index <c>(series_id, started_at DESC)</c> serves the read, and the id tie-break keeps the
/// answer deterministic when two runs of one series were started in the same instant.
/// </para>
/// <para>
/// <b>The scope is the caller's.</b> It runs on the scope's write model, so the D10 tenant filter applies and the
/// explicit <c>series_id</c> predicate is the only further limit; there is no <c>IgnoreQueryFilters</c> here and
/// none is allowed outside <c>Application/Features/Admin</c> (09.1.2-T1). The scope check below is the
/// fail-closed part: a filtered read outside a user scope returns no rows at all, which the pause rule would read
/// as the answer "this series never failed the gate" — the one wrong answer that must never be silent. The
/// precedent is <c>GenerationLimitGuard</c>, which refuses the same way.
/// </para>
/// </remarks>
/// <param name="db">The write model of the caller's scope; its tenant filter is what limits the rows to the owner.</param>
public sealed class GateFailureStreakReader(IAppDbContext db) : IGateFailureStreakReader
{
    /// <summary>The plan §3.5 <c>LIMIT 2</c>: only the two most recent terminal runs are counted.</summary>
    public const int StreakWindow = 2;

    /// <inheritdoc />
    public async ValueTask<int> GetStreakAsync(Guid seriesId, CancellationToken ct)
    {
        EnsureUserScope();

        var outcomes = await NewestTerminalOutcomesAsync(seriesId, StreakWindow, ct).ConfigureAwait(false);

        var streak = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome != AutopilotRunOutcome.GateFailed)
            {
                break;
            }

            streak++;
        }

        return streak;
    }

    /// <inheritdoc />
    public async ValueTask<AutopilotRunGateSnapshot?> GetLatestAsync(Guid seriesId, CancellationToken ct)
    {
        EnsureUserScope();

        return await db.AutopilotRuns
            .AsNoTracking()
            .Where(run => run.SeriesId == seriesId)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Select(run => new AutopilotRunGateSnapshot(
                run.Id,
                run.EpisodeId,
                run.StartedAt,
                run.Outcome,
                run.PauseReason,
                run.GateResults))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The outcomes of the <paramref name="take"/> most recent runs of the series that have stopped, newest first.
    /// A run whose <c>outcome</c> is still null has not stopped and is not part of the answer, so it cannot be
    /// mistaken for a failure it never was (nor for the end of a streak).
    /// </summary>
    /// <param name="seriesId">The series to read.</param>
    /// <param name="take">How many outcomes to read; the caller's window.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    private async Task<List<AutopilotRunOutcome?>> NewestTerminalOutcomesAsync(
        Guid seriesId,
        int take,
        CancellationToken ct) =>
        await db.AutopilotRuns
            .AsNoTracking()
            .Where(run => run.SeriesId == seriesId && run.Outcome != null)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Select(run => run.Outcome)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Refuses the read when the scope has no user (09 README D10): the tenant filter would drop every row, and a
    /// streak of 0 is the answer that lets a series keep publishing after two gate failures.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope's current user is anonymous.</exception>
    private void EnsureUserScope()
    {
        if (db is not ITenantFilterContext tenant || tenant.CurrentUserId is null)
        {
            throw new InvalidOperationException(
                "The gate-failure streak was read outside a user scope, where the tenant filter hides every row "
                + "of the series and the streak would be reported as 0 (09 README D10).");
        }
    }
}
