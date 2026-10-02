using System.Text.Json;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The one row the node keeps per render task (05.4.1-T2 §4 "durable store", 11 README D5). Every field is
/// a stored fact or a verbatim block: no node path, MPT URI, script, term or prompt ever appears here
/// (05 D4 "never in a status", F11).
/// </summary>
/// <param name="TaskId">The adapter's UUIDv7 task id (<c>POST /v1/renders</c> mints it).</param>
/// <param name="RenderJobId">The backend's job id — the idempotency key with <paramref name="BodySha256"/>.</param>
/// <param name="BodySha256">Lowercase hex sha256 of the raw submit body.</param>
/// <param name="BackendTaskId">MPT's own task id; internal, logged only (05 C3).</param>
/// <param name="SubmittedAt">When the adapter accepted the submit.</param>
/// <param name="BackendStartedAt">MPT's start time for the task; the lost rule compares MPT's process start against it (§4).</param>
/// <param name="State">One of <see cref="RenderStates"/>.</param>
/// <param name="Stage">One of <see cref="RenderStages"/>.</param>
/// <param name="Percent">MPT progress; never decreases (invariant of <see cref="Merge"/>).</param>
/// <param name="CancelRequested">True after <c>POST …/cancel</c>; the only last-writer-wins field of a merge.</param>
/// <param name="Outcome">Present on <c>failed</c> only.</param>
/// <param name="Output">Present once <c>succeeded</c>.</param>
/// <param name="Check">The verbatim <c>render-check.v1</c> block (05 D9).</param>
/// <param name="Images">The verbatim <c>images.v1</c> block (03 D6).</param>
/// <param name="FinishedAt">Set when the task became terminal.</param>
/// <param name="CleanedAt">Set by <c>DELETE</c>; the row is purgeable 7 days later (D5/D15 P4).</param>
/// <param name="MptLastSnapshot">The last raw MPT observables, kept even when MPT is gone.</param>
public sealed record RenderTaskRecord(
    Guid TaskId,
    Guid RenderJobId,
    string BodySha256,
    string? BackendTaskId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? BackendStartedAt,
    string State,
    string Stage,
    int Percent,
    bool CancelRequested,
    RenderOutcomeV1? Outcome,
    RenderOutputV1? Output,
    JsonElement? Check,
    JsonElement? Images,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? CleanedAt,
    MptStateV1? MptLastSnapshot)
{
    /// <summary>The 05 D4 stage that may report 100 while the task is still running (post-processing).</summary>
    private const int NonTerminalPercentCap = 99;

    /// <summary>True when this row is allowed to publish 100: a terminal state or the adapter's own checking stage.</summary>
    private bool CanReach100() => IsTerminal || string.Equals(Stage, RenderStages.Checking, StringComparison.Ordinal);

    /// <summary>The index of a stage in the §4 order, so a stale observation cannot move the stored stage backwards.</summary>
    private static int StageRank(string stage)
    {
        for (var index = 0; index < RenderStages.All.Count; index++)
        {
            if (string.Equals(RenderStages.All[index], stage, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>True for <c>succeeded</c>/<c>failed</c>/<c>cancelled</c>: the capacity gate counts the complement.</summary>
    public bool IsTerminal => RenderStates.IsTerminal(State);

    /// <summary>
    /// Merges a newer observation into this row under the 05 D4 status invariants: <c>percent</c> never
    /// decreases and never reaches 100 before <c>checking</c> or a terminal state, a terminal row is final,
    /// an already-observed block is never lost, and only <c>cancel_requested</c> is last-writer-wins.
    /// Both the stores and the endpoints use this one merge, so a slow poller and a cancel cannot clobber
    /// each other (Failure handling, 11.3.5-T1).
    /// </summary>
    public RenderTaskRecord Merge(RenderTaskRecord observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.TaskId != TaskId)
        {
            throw new ArgumentException("A merge needs two observations of the same task.", nameof(observed));
        }

        if (IsTerminal)
        {
            // A terminal row is final except for the cleaning mark a DELETE adds and the cancel flag, the
            // only field the contract makes last-writer-wins (Failure handling, 11.3.5-T1).
            return this with
            {
                CleanedAt = observed.CleanedAt ?? CleanedAt,
                CancelRequested = observed.CancelRequested,
            };
        }

        // The cap is the higher of the two observations: a stale stage (e.g. `assembling` arriving after the
        // port already reached `checking`) must never pull an already-100 percent back below it. `Math.Max`
        // with the stored Percent makes progress monotonic even for a row that is already inconsistent.
        var percent = Math.Max(Percent, observed.Percent);
        var cap = CanReach100() || observed.CanReach100() ? 100 : NonTerminalPercentCap;
        var stage = StageRank(observed.Stage) >= StageRank(Stage) ? observed.Stage : Stage;

        return observed with
        {
            Stage = stage,
            Percent = Math.Max(Percent, Math.Clamp(percent, 0, cap)),
            BackendTaskId = observed.BackendTaskId ?? BackendTaskId,
            BackendStartedAt = observed.BackendStartedAt ?? BackendStartedAt,
            CancelRequested = observed.CancelRequested,
            Outcome = observed.Outcome ?? Outcome,
            Output = observed.Output ?? Output,
            Check = observed.Check ?? Check,
            Images = observed.Images ?? Images,
            FinishedAt = observed.FinishedAt ?? FinishedAt,
            CleanedAt = observed.CleanedAt ?? CleanedAt,
            MptLastSnapshot = observed.MptLastSnapshot ?? MptLastSnapshot,
        };
    }
}
