using NightTales.Domain.Common;

namespace NightTales.Domain.Rendering;

/// <summary>
/// A render attempt reached the terminal <c>failed</c> state (BD5, WT28). The outbox stores it as
/// <c>render_failed.v1</c> in the same transaction as the reservation refund and the episode's return to
/// <c>story_approved</c>, which is what the notifications of 05/09.8 read. The payload is ids, the outcome code and
/// the attempt number — never a content fragment and never the node's raw error text (D11/D19, 05 C8).
/// </summary>
/// <param name="EpisodeId">The episode the failed attempt belonged to.</param>
/// <param name="RenderJobId">The failed <c>render_jobs</c> row (the attempt, not the episode's newest job).</param>
/// <param name="ErrorCode">The 05 D5 outcome code stored on the row (<c>RenderJob.OutcomeCodes</c>).</param>
/// <param name="Attempt">The attempt number of the failed row, counted from 1.</param>
/// <param name="Final">
/// <see langword="true"/> when no retry follows, so the episode really stays out of <c>rendering</c>;
/// <see langword="false"/> while <c>RenderRetryPolicy</c> queues the next attempt (WT28/WT50). A consumer that
/// alerts the creator on every failure must key on this flag, not on the event alone.
/// </param>
/// <param name="OccurredAt">When the attempt ended, from the command's or the Worker's clock.</param>
[OutboxEvent("render_failed.v1")]
public sealed record RenderFailed(
    Guid EpisodeId,
    Guid RenderJobId,
    string ErrorCode,
    int Attempt,
    bool Final,
    DateTimeOffset OccurredAt) : IDomainEvent;
