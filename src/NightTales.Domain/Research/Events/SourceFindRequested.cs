using NightTales.Domain.Common;
using NightTales.Domain.Stories.Generation;

namespace NightTales.Domain.Research.Events;

/// <summary>
/// A source search was requested for an episode (02.7.2-T2, plan §3/§5). The outbox stores it as
/// <c>source_find_requested.v1</c> in the command's transaction; the Worker dispatcher turns it into
/// <c>FindSourcesJob(generationRunId)</c> (02.7.2-T3), the run id being the job's only argument (D19/D2).
/// </summary>
/// <remarks>
/// <para>
/// The typed <paramref name="Query"/> travels here and nowhere else (02.7 plan §3, D19): not in
/// <c>generation_runs</c>, not in the audit row, not in a log line, not in a Hangfire argument. It is bounded to
/// 300 characters by the command's validator and
/// <see cref="NightTales.Domain.Episodes.Episode.RequestSourceFind"/>, and it
/// disappears with the outbox row. <see langword="null"/> means the caller typed nothing: the job derives the
/// query again from the episode title and brief (plan §3), so the text never has to be precomputed here.
/// </para>
/// <para>
/// A find-sources run does not move its episode (<c>EpisodeStatus</c> has no source-finder value), so this event
/// is the only thing the request writes beyond the <c>generation_runs</c> row itself.
/// </para>
/// </remarks>
/// <param name="GenerationRunId">The <c>generation_runs</c> row the job runs; the job's only argument.</param>
/// <param name="EpisodeId">The episode the search is for.</param>
/// <param name="SeriesId">Its series, whose language and topic shape the search.</param>
/// <param name="UserId">The owner the job runs as.</param>
/// <param name="Trigger">Who asked: the user, the autopilot or the system.</param>
/// <param name="Query">The user's typed query, or <see langword="null"/>; the only place it is stored (D19).</param>
/// <param name="OccurredAt">When the aggregate raised it, from the command's clock.</param>
[OutboxEvent("source_find_requested.v1")]
public sealed record SourceFindRequested(
    Guid GenerationRunId,
    Guid EpisodeId,
    Guid SeriesId,
    Guid UserId,
    GenerationTrigger Trigger,
    string? Query,
    DateTimeOffset OccurredAt) : IDomainEvent;
