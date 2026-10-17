using ErrorOr;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Autopilot.Ideas.Abstractions;

/// <summary>
/// The winner of one ideas run (02.8.1 plan §4): the fields 02.8.3-T1 needs to create the episode, plus the
/// measurements the run log shows.
/// </summary>
/// <remarks>
/// <para>
/// 02.8.1-T1 ships the skeleton — context, one paid call, validation, persistence, the first valid candidate as
/// the pick — so <see cref="Score"/> and <see cref="Similarity"/> are the conservative <c>0</c> placeholder:
/// nothing has scored the batch yet, and the pickup is "the first valid candidate", never a ranked choice.
/// 02.8.1-T2 fills both with the weighted score and <c>max(lexical, judge)</c>; the members are therefore part
/// of the contract, not of T1's arithmetic.
/// </para>
/// <para>
/// <see cref="RequiresResearch"/> is F33's hand-off: a <c>true_crime</c> series' idea is an angle whose facts
/// arrive from sources later, so 02.8.3-T1 must have sources before it can write.
/// </para>
/// <para>
/// The text fields are model output: they are returned to the caller, never logged (D19).
/// </para>
/// </remarks>
/// <param name="IdeaId">The <c>episode_ideas</c> row the run persisted.</param>
/// <param name="Title">The picked idea's title, as proposed.</param>
/// <param name="Logline">The picked idea's one-line premise, as proposed.</param>
/// <param name="Hook">The picked idea's opening hook, as proposed.</param>
/// <param name="Setting">The picked idea's setting in words, as proposed.</param>
/// <param name="Structure">The picked idea's D3 structure block, its beats included.</param>
/// <param name="RequiresResearch">True when the series' topic is <c>true_crime</c> (F33).</param>
/// <param name="Score">The weighted score, 0…1; <c>0</c> until 02.8.1-T2 scores the batch.</param>
/// <param name="Similarity">The similarity the run log shows; <c>0</c> until 02.8.1-T2 measures the corpus.</param>
public sealed record IdeaPick(
    Guid IdeaId,
    string Title,
    string Logline,
    string Hook,
    string Setting,
    StoryStructureTags Structure,
    bool RequiresResearch,
    decimal Score,
    decimal Similarity);

/// <summary>
/// The one entry point of the idea steps (02.8.1 plan §4): the service 02.8.3-T1's autopilot tick calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The caller owns the <c>generation_runs</c> row (one writer per table, task file
/// [DECISION]): it creates it (<c>kind = ideas</c>, <c>episode_id = NULL</c>, <c>counts_toward_limit = false</c>),
/// reserves the credit, checks the budget and writes the run's outcome. The planner owns <c>episode_ideas</c>,
/// meters nothing itself (the generator records the call's usage row, D14) and settles nothing: it returns the
/// outcome as an <see cref="Error"/> and the caller records it.
/// </para>
/// <para>
/// The returned errors are the D7 mapping: <c>ideas_invalid_output</c>, <c>ideas_moderated</c>,
/// <c>ideas_generation_failed</c> and the shared <c>provider_funds_exhausted</c> (02.8.3 pauses the series on
/// the last one). Tenant isolation is explicit: a series that is not the run owner's answers
/// <c>not_found</c>, like every other read of another user's row (09 README D10).
/// </para>
/// </remarks>
public interface IIdeaPlanner
{
    /// <summary>
    /// Runs the idea step of one slot: read the context, make the (at most twice attempted) paid call, validate,
    /// persist every surviving candidate as <c>proposed</c>, and answer the first one as the pick.
    /// </summary>
    /// <remarks>
    /// 02.8.1-T1 implements plan §4 U1 steps 1, 3, 4, 5 and 10. The variety steering, the novelty check, the
    /// content-policy pre-check, the scoring and the rejection of the losers are 02.8.1-T2's and 02.8.4-T1's
    /// steps and are deliberately absent: every surviving candidate is persisted <c>proposed</c>, including the
    /// ones T2 will later reject, because the run log shows the whole pool.
    /// </remarks>
    /// <param name="seriesId">The series the slot belongs to; must be the run's series.</param>
    /// <param name="slotAt">
    /// The slot's instant. It is the caller's idempotency key (<c>autopilot:{seriesId}:{slotAt:O}:idea</c>, F28):
    /// this step never reads it, and it is carried so the signature matches the AutopilotOpsPlan contract.
    /// </param>
    /// <param name="run">The caller's run facts: owner, run id and attempt (the invalid-output retry rewrites only the attempt).</param>
    /// <param name="ct">Cancels the step.</param>
    /// <returns>
    /// The pick — the first valid candidate — or the typed outcome the caller records on the run:
    /// <c>not_found</c> (the series is unknown or another user's), <c>ideas_invalid_output</c> (no valid
    /// candidate after the one retry), <c>ideas_moderated</c> (the provider refused the call; one
    /// <c>moderation_events</c> row with <c>stage = ideas</c> is written), <c>provider_funds_exhausted</c>, or
    /// <c>ideas_generation_failed</c>.
    /// </returns>
    ValueTask<ErrorOr<IdeaPick>> ProposeAndPickAsync(Guid seriesId, DateTimeOffset slotAt, RunContext run, CancellationToken ct);

    /// <summary>
    /// Records that the picked idea became an episode (plan §4 U4): <c>picked</c> → <c>used</c> with
    /// <c>episode_id</c> set.
    /// </summary>
    /// <remarks>
    /// Called by 02.8.3-T1 after it created the episode, under a system context pinned to the series' owner, so
    /// the row is read through the tenant filter of that scope. The partial unique index on <c>episode_id</c> is
    /// the invariant one level down: a second idea for the same episode — or the same idea twice — answers the
    /// same 409 as a transition from a status that cannot be used, instead of surfacing a provider-level
    /// constraint error. The caller treats that conflict as a bug, not as a user error (plan §4 U4).
    /// </remarks>
    /// <param name="ideaId">The picked idea.</param>
    /// <param name="episodeId">The episode the idea became.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>Success, <c>not_found</c> when the idea is not in the caller's tenant, or the 409 conflict.</returns>
    ValueTask<ErrorOr<Success>> MarkUsedAsync(Guid ideaId, Guid episodeId, CancellationToken ct);
}
