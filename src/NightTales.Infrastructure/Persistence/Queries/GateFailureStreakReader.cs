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
/// <b>The ordering column.</b> The plan's SQL orders by the run's slot column and takes two rows; 02.8.3-T1's
/// <c>UNIQUE (series_id, slot_at)</c> makes that column the slot's identity, so this reader orders by
/// <c>autopilot_runs.slot_at DESC</c> with the v7 id as the tie-break (STATUS S67-6). The
/// <c>(series_id, slot_at DESC)</c> index serves the read, and the id tie-break keeps the answer deterministic even
/// though a series' slots are unique.
/// </para>
/// <para>
/// <b>The pause reason.</b> <see cref="AutopilotRun.PauseReason"/> is the 02.8.3 enum; the snapshot 02.8.3-T4
/// consumes keeps its snake_case wire value (<see cref="AutopilotPauseReasonWire"/>), which is what the column
/// stores and what the alert copy key names.
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

    // The terminal marker is `finished_at`, not `outcome`: 02.8.3 writes the outcome on every stop, including the
    // non-terminal ones (`waiting_owner`, `paused`, `awaiting_authorization`), so filtering on `outcome` would put a
    // stopped-but-open run (a buffered slot waiting for the owner's consent) inside the two-row window and reset the
    // streak although the last two finished runs both failed the gate.

    /// <inheritdoc />
    public async ValueTask<int> GetStreakAsync(Guid seriesId, CancellationToken ct)
    {
        EnsureUserScope();

        var outcomes = await NewestTerminalOutcomesAsync(seriesId, StreakWindow, ct).ConfigureAwait(false);

        // A terminal run that did not fail the gate ends the streak, so the answer is the window itself and only
        // when every run in it failed: `… gate_failed, published, gate_failed …` is not a run of two failures, and
        // the task acceptance wants 0 for it — the pause rule of 02.8.3-T4 must see a run of failures, not the
        // newest failure alone.
        if (outcomes.Count == 0 || outcomes.Exists(outcome => outcome != AutopilotRunOutcome.GateFailed))
        {
            return 0;
        }

        return outcomes.Count;
    }

    /// <inheritdoc />
    public async ValueTask<AutopilotRunGateSnapshot?> GetLatestAsync(Guid seriesId, CancellationToken ct)
    {
        EnsureUserScope();

        var latest = await db.AutopilotRuns
            .AsNoTracking()
            .Where(run => run.SeriesId == seriesId)
            .OrderByDescending(run => run.SlotAt)
            .ThenByDescending(run => run.Id)
            .Select(run => new LatestRun(
                run.Id,
                run.EpisodeId,
                run.StartedAt,
                run.Outcome,
                run.PauseReason,
                run.GateResults))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return latest is null
            ? null
            : new AutopilotRunGateSnapshot(
                latest.RunId,
                latest.EpisodeId,
                latest.StartedAt,
                latest.Outcome,
                latest.PauseReason is { } reason ? AutopilotPauseReasonWire.ToWire(reason) : null,
                latest.GateResults);
    }

    /// <summary>
    /// The outcomes of the <paramref name="take"/> most recent runs of the series that have finished, newest first.
    /// A run that has not finished (<c>finished_at IS NULL</c>) is not part of the answer, so neither a run in
    /// flight nor one merely stopped on the owner or the consent gate can be mistaken for a failure it never was
    /// (nor for the end of a streak).
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
            .Where(run => run.SeriesId == seriesId && run.FinishedAt != null)
            .OrderByDescending(run => run.SlotAt)
            .ThenByDescending(run => run.Id)
            .Select(run => run.Outcome)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// The columns <see cref="GetLatestAsync"/> reads, before the pause reason is turned into its wire value. The
    /// projection stays a record of the entity's own types so EF translates it to one <c>SELECT</c>; the enum-to-text
    /// step is client-side because the column's value converter is a store detail of the write model.
    /// </summary>
    /// <param name="RunId">The run row.</param>
    /// <param name="EpisodeId">The episode it is producing, or <see langword="null"/> before one exists.</param>
    /// <param name="StartedAt">The instant the row was planned.</param>
    /// <param name="Outcome">The stored outcome.</param>
    /// <param name="PauseReason">The stored pause trigger, or <see langword="null"/>.</param>
    /// <param name="GateResults">The stored <c>gates.v1</c> document, or <see langword="null"/>.</param>
    private sealed record LatestRun(
        Guid RunId,
        Guid? EpisodeId,
        DateTimeOffset StartedAt,
        AutopilotRunOutcome? Outcome,
        AutopilotPauseReason? PauseReason,
        string? GateResults);

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
