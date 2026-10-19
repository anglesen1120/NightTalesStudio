using NightTales.Domain.Common;

namespace NightTales.Domain.Series.Events;

/// <summary>
/// An autopilot run stopped at a gate and the owner must act (02.8.2 plan §5, UC6): the outbox stores it as
/// <c>autopilot_owner_action_required.v1</c> in the same transaction as the gate verdict, and the Worker's
/// handler queues the <c>autopilot_owner_action_required</c> e-mail and publishes <c>EpisodeUpdated</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ids and codes only (D19, 09 README BD16): the stop's reason code and the failing gate check ids, never a
/// story text, a brief, a fact sheet or a source URL. The handler resolves the titles and the slot from the rows
/// this payload names, so the payload cannot carry content even by accident.
/// </para>
/// <para>
/// The episode's status is untouched (plan §4 UC6): a stop leaves it in <c>story_review</c>/<c>video_review</c>
/// and the owner decides, which is what keeps the notice a side effect of the verdict instead of a state change
/// of its own. <c>copyKey</c> is the key of the reason in the owner's locale (plan §6) and is carried for the run
/// log, not for the e-mail: the template has its own fixed copy.
/// </para>
/// </remarks>
/// <param name="UserId">The owner the message runs as.</param>
/// <param name="SeriesId">The series the run belongs to.</param>
/// <param name="RunId">The run whose gate stopped.</param>
/// <param name="EpisodeId">The episode the run is producing.</param>
/// <param name="Reason">The stop's code, one of <c>OwnerActionReasons</c> (02.8.2-T1).</param>
/// <param name="Failures">The failing gate check ids of plan §3.1/§3.2, in the verdict's order.</param>
/// <param name="CopyKey">The copy key the run log shows for the reason, in the owner's locale (plan §6).</param>
/// <param name="OccurredAt">When the aggregate raised it, from the caller's clock.</param>
[OutboxEvent("autopilot_owner_action_required.v1")]
public sealed record AutopilotOwnerActionRequired(
    Guid UserId,
    Guid SeriesId,
    Guid RunId,
    Guid EpisodeId,
    string Reason,
    IReadOnlyList<string> Failures,
    string CopyKey,
    DateTimeOffset OccurredAt) : IDomainEvent;
