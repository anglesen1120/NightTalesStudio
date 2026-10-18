using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Events;

namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// One autopilot run of a series (00 overview §6 <c>autopilot_runs</c>). This is the shell 09.2.2-T1
/// owns — the base columns of plan §3; 02.8.3-T1 adds its state-machine columns and the behaviour.
/// </summary>
public sealed class AutopilotRun : AggregateRoot, IUserOwned, IAuditable
{
    private AutopilotRun()
    {
    }

    /// <summary>The code of the one validation error this aggregate can answer for <c>gate_results</c>.</summary>
    public const string InvalidGateResultsCode = "autopilot_run_invalid_gate_results";

    public Guid UserId { get; private set; }

    public Guid SeriesId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Placeholder FK to <c>episode_ideas</c>, whose constraint 02.8.1 adds.</summary>
    public Guid? IdeaId { get; private set; }

    public Guid? EpisodeId { get; private set; }

    public AutopilotRunOutcome? Outcome { get; private set; }

    /// <summary>The <c>gate_results</c> JSON document (<c>gates.v1</c>, 02.8.2 owns its shape).</summary>
    public string? GateResults { get; private set; }

    public string? PauseReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Stores the merged <c>gates.v1</c> document of one gate verdict (02.8.2-T1, plan §3.4). The caller
    /// (<c>GateResultWriter</c>) reads the stored document, merges the half it owns and hands the whole document
    /// back: the aggregate keeps the column, the Application keeps the document's shape — the same split
    /// <c>RenderJob.RecordQualityReport</c> makes, because the Domain may not reference the Application (D1).
    /// </summary>
    /// <remarks>
    /// The merge happens before this call, on purpose: the story and the video verdict arrive at different moments
    /// of the same run and neither may erase the other, so the read-modify-write belongs beside the document's
    /// shape rather than on this aggregate. Nothing here validates the shape beyond "there is a document".
    /// </remarks>
    /// <param name="gateResultsJson">The merged <c>gates.v1</c> document, already serialised.</param>
    /// <returns>Success, or the validation error of a blank document.</returns>
    public ErrorOr<Success> RecordGateResults(string gateResultsJson)
    {
        if (string.IsNullOrWhiteSpace(gateResultsJson))
        {
            return Error.Validation(
                InvalidGateResultsCode,
                "A gate verdict carries its gates.v1 document (02.8.2 plan §3.4).");
        }

        GateResults = gateResultsJson;

        return Result.Success;
    }

    /// <summary>
    /// Raises the owner-action notice of one gate stop (02.8.2-T1, plan §4 UC6). The outbox interceptor turns it
    /// into the <c>autopilot_owner_action_required.v1</c> row of the caller's own <c>SaveChangesAsync</c>, so the
    /// notice and the gate verdict it belongs to commit or roll back together (00 §5.2).
    /// </summary>
    /// <remarks>
    /// Nothing of the episode changes here: a stop leaves it in <c>story_review</c>/<c>video_review</c> and the
    /// owner decides. The payload carries the reason code and the failing check ids only — never a title, a story
    /// text, a brief or a source URL (D19) — and the handler resolves what else the e-mail shows.
    /// </remarks>
    /// <param name="reason">The stop's code, one of <c>OwnerActionReasons</c>.</param>
    /// <param name="failures">The failing gate check ids; empty for a stop that failed no check.</param>
    /// <param name="copyKey">The copy key the run log shows for the reason (plan §6).</param>
    /// <param name="now">The caller's clock reading; the aggregate never reads the clock itself.</param>
    /// <exception cref="InvalidOperationException">The run has no episode, so no notice can name one.</exception>
    public void RaiseOwnerActionRequired(
        string reason,
        IReadOnlyList<string> failures,
        string copyKey,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyKey);

        var episodeId = EpisodeId ?? throw new InvalidOperationException(
            $"Autopilot run {Id} has no episode, so no owner-action notice can name one.");

        Raise(new AutopilotOwnerActionRequired(
            UserId,
            SeriesId,
            Id,
            episodeId,
            reason,
            failures.ToArray(),
            copyKey,
            now));
    }
}
