using ErrorOr;
using Mediator;
using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Episodes.Commands.CreateEpisode;

/// <summary>
/// <c>POST /api/v1/series/{seriesId}/episodes</c> (09.5.1 plan §4 UC1, §5): create one episode under one
/// of the caller's series.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SeriesId"/> is the route value, never a body member: the wire body
/// (<c>EpisodeEndpoints.CreateEpisodeRequest</c>) carries only <c>title</c>, <c>brief</c> and
/// <c>lengthTier</c>, so a client cannot name another series or another owner (09.5.1 plan §8). The
/// handler copies <c>user_id</c> and <c>series_id</c> from the series it loaded.
/// </para>
/// <para>
/// The response is the created id only: <c>xmin</c> — the ETag source — is assigned by PostgreSQL at
/// commit, after the handler returned, so the endpoint reads it back with the post-commit delegate of
/// <c>ToCreatedResult</c> (09 README D15).
/// </para>
/// <para>
/// No <c>Idempotency-Key</c>: creating an episode spends no credits and calls no platform
/// ([DECISION] 09.5.1 plan §5/§Notes, 00 §8).
/// </para>
/// </remarks>
/// <param name="SeriesId">The series the episode belongs to; the route value, from the tenant-filtered load.</param>
/// <param name="Title">The optional title, ≤ 120 characters after trimming (an empty one is stored as <see langword="null"/>).</param>
/// <param name="Brief">The brief, 1–1,000 characters after trimming (00 §6).</param>
/// <param name="LengthTier">The story length tier; drives 02's story length and 12's price tier.</param>
public sealed record CreateEpisodeCommand(
    Guid SeriesId,
    string? Title,
    string Brief,
    LengthTier LengthTier) : ICommand<ErrorOr<Guid>>;
