namespace NightTales.Application.Features.Episodes.Queries.GetEpisode;

/// <summary>
/// The episode's publication summary (09.5.3 plan §5, 06.2.1-T1 C7/F14): how many of the episode's publications
/// are live, how many are waiting for their upload, and when the most recent one went live.
/// </summary>
/// <remarks>
/// <para>
/// Filled by 06.2.1-T1 from its own third query on the same <c>DbContext</c> — one grouped read over
/// <c>publications</c>, sequential after 09.5.3-T1's two reads (WT46/BD16), and the documented exception to the
/// two-query rule that must not grow a fourth round trip.
/// </para>
/// <para>
/// <see langword="null"/> means the episode has no publication row at all — never "not loaded": an episode with
/// one cancelled publication answers a summary with zeros, which is what lets the page tell "never published"
/// from "published and cancelled" (06 README D6).
/// </para>
/// </remarks>
/// <param name="Published">How many publications of the episode are live.</param>
/// <param name="Scheduled">How many are scheduled but not uploaded yet.</param>
/// <param name="LastPublishedAt">When the most recent publication went live, or null.</param>
public sealed record PublicationsSummaryDto(
    int Published,
    int Scheduled,
    DateTimeOffset? LastPublishedAt);
