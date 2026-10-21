using NightTales.Domain.Stories;

namespace NightTales.Application.Features.SeriesBible.Events;

/// <summary>
/// The deferred delivery of one approved episode's bible proposal (02.6.3-T2 plan §7.1 step 7): the
/// <c>(series_id, kind)</c> active-run index admitted another proposal run of the same series, so the work is
/// re-attempted later instead of failing the outbox message. It is the ids-only request of
/// <c>BibleProposalHandoffJob</c> (09 README D12), which re-enters
/// <see cref="StoryApprovedBibleProposalHandler"/> after <c>IJobScheduler.Schedule</c>'s two-minute delay.
/// </summary>
/// <remarks>
/// <para>
/// A handoff carries no user id: the job resolves the owner of the episode and pins
/// <c>ISystemContext.For</c> before the handler runs, so the delivery reads only the owner's rows (D10).
/// </para>
/// <para>
/// <see cref="ApprovalSource"/> is the Domain enum of the approval the event carried, not its wire text: a job
/// method parameter may be an enum but no arbitrary string (09.2.6 ids-only rule), and the enum is what the
/// handler branches on (autopilot fiction applies its ops immediately, a user approval waits for accept).
/// </para>
/// </remarks>
/// <param name="SeriesId">The series the proposal belongs to.</param>
/// <param name="EpisodeId">The approved episode the proposal is built from.</param>
/// <param name="StoryVersionId">The approved story version; with <paramref name="EpisodeId"/> it forms the idempotency key.</param>
/// <param name="ApprovalSource">Who approved the story.</param>
/// <param name="Tries">
/// How many deliveries this work already spent, counting the outbox delivery as the first: the handler stops
/// re-scheduling after its maximum and drops the proposal (a missed summary line is acceptable, plan §7.1 step 7).
/// </param>
public sealed record BibleProposalHandoff(
    Guid SeriesId,
    Guid EpisodeId,
    Guid StoryVersionId,
    ApprovalSource ApprovalSource,
    int Tries);
