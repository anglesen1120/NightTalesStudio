namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// The episode's latest render job, as <c>GET /api/v1/episodes/{id}</c> reports it (09.5.3 plan §5,
/// README §B10 WT49/WT53, 10 CR-12).
/// </summary>
/// <remarks>
/// <para>
/// "Latest" is <c>render_jobs.id DESC</c>: the table carries no <c>created_at</c> column and its ids are
/// UUIDv7, so <c>id DESC</c> <em>is</em> creation order ([RULED — README §B10 WT49]). The job's own
/// times come from <c>submitted_at</c>/<c>finished_at</c> (WT53).
/// </para>
/// <para>
/// <see cref="Status"/> and <see cref="Stage"/> are free-form strings, not enums: 00 §7/09.6.1-T1 own the
/// job state machine (<c>queued|submitted|running|ingesting|succeeded|failed|cancelled</c>) and the
/// adapter reports finer stages (<c>voice|subtitles|images|assembling</c>, 05 CR-2) that the backend must
/// pass through rather than refuse. <see cref="ErrorCode"/> and <see cref="Hints"/> carry the failure
/// reason and the quality report (10 CR-12) without a second query.
/// </para>
/// <para>
/// The member list is [INFERENCE] (only the artefact name is pinned by BD1); the values are the
/// <c>render_jobs</c> columns of BD5.
/// </para>
/// </remarks>
/// <param name="RenderJobId">The job's id (<c>render_jobs.id</c>).</param>
/// <param name="Status">The job state, lowercase (<c>queued</c>, <c>submitted</c>, <c>running</c>, <c>ingesting</c>, <c>succeeded</c>, <c>failed</c>, <c>cancelled</c>).</param>
/// <param name="Stage">The adapter's finer stage, or <see langword="null"/> while none is reported.</param>
/// <param name="Percent">The monotonic progress, 0–100 (BD5: <c>UpdateProgress</c> never decreases).</param>
/// <param name="Attempt">The attempt number; the first job of an episode is attempt 1.</param>
/// <param name="ImagesNeeded">How many images the scene plan asks for.</param>
/// <param name="SubmittedAt">When the job reached the adapter, or null while it is still queued.</param>
/// <param name="FinishedAt">When the job reached a terminal state, or null while it runs.</param>
/// <param name="ErrorCode">The catalogued failure code (<c>render_quality_failed</c>, …), or null.</param>
/// <param name="Hints">The quality hints (<c>render-quality.v1</c>) ∪ 03's image hints; empty when there are none.</param>
public sealed record RenderJobSummaryDto(
    Guid RenderJobId,
    string Status,
    string? Stage,
    int Percent,
    int Attempt,
    int ImagesNeeded,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorCode,
    IReadOnlyList<string> Hints);
