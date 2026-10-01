namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// The episode's publication summary (09.5.3 plan §5): a placeholder of the shape 06 will fill.
/// </summary>
/// <remarks>
/// <para>
/// Always <see langword="null"/> in P: <c>publications</c> arrives with 06.2.1-T1, which fills this block
/// from its own documented third query on the same <c>DbContext</c> (sequential, WT46) or from the
/// render/asset batch — this task adds no speculative query ([RULED] 09 README §B10 WT2/BD16, plan §5).
/// </para>
/// <para>
/// The member list is [INFERENCE]: BD1 pins only that the field exists.
/// </para>
/// </remarks>
/// <param name="Published">How many publications of the episode are live.</param>
/// <param name="Scheduled">How many are scheduled but not uploaded yet.</param>
/// <param name="LastPublishedAt">When the most recent publication went live, or null.</param>
public sealed record PublicationsSummaryDto(
    int Published,
    int Scheduled,
    DateTimeOffset? LastPublishedAt);
