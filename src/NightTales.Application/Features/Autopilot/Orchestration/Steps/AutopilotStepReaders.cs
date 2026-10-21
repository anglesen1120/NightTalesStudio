using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The episode facts the render and publication steps decide on (plan §3.5 rows <c>story_gate → rendering</c>,
/// <c>rendering → video_gate</c>, <c>rendering → failed</c>): its state, the <c>xmin</c> its <c>If-Match</c> tag is
/// built from, and the two metadata defaults a publication is created with.
/// </summary>
/// <param name="Id">The episode row (<c>episodes.id</c>).</param>
/// <param name="Status">The live episode state; the run's steps read it as the render's signal (plan §7.2).</param>
/// <param name="Version">The <c>episodes.xmin</c> value <see cref="Common.ETags.Episode"/> quotes.</param>
/// <param name="Title">The episode title, when the idea plan gave it one; never logged (D19).</param>
/// <param name="Brief">The episode brief; never logged (D19).</param>
public sealed record AutopilotRenderEpisode(Guid Id, EpisodeStatus Status, uint Version, string? Title, string Brief);

/// <summary>
/// The channel facts the publication step decides on (plan §3.6 rows 6 and 6b): the connection the run's
/// authorization names, its platform and its status.
/// </summary>
/// <param name="Id">The <c>channel_connections.id</c> the publication is created against.</param>
/// <param name="Platform">The publishing platform; only <see cref="Platform.YouTube"/> can schedule today (07 adds TikTok).</param>
/// <param name="Status">The connection status; only <see cref="ChannelConnectionStatus.Active"/> may upload.</param>
public sealed record AutopilotChannelSnapshot(
    Guid Id,
    NightTales.Domain.Publishing.Platform Platform,
    ChannelConnectionStatus Status);

/// <summary>
/// The publication facts the three post-publication steps read (plan §3.5 rows from <c>publication_created</c>
/// to <c>scheduled → published</c>): the state, the two timestamps that order the veto and the consent, and the
/// pre-audit stamp that decides "published" from "published_private_locked" (plan §7.4).
/// </summary>
/// <param name="Id">The <c>publications.id</c>.</param>
/// <param name="Status">The live publication state.</param>
/// <param name="CreatedAt">When the row was created: the 30-minute notice deadline of plan §3.6 row 11 counts from here.</param>
/// <param name="ConsentAt">When consent was written, or <see langword="null"/> while the row is <c>awaiting_consent</c>.</param>
/// <param name="VetoNoticeSentAt">When the veto notice was confirmed as delivered (06.2.2-T2); the <c>publication_created → veto_window</c> guard.</param>
/// <param name="VetoEndsAt">When the owner's veto window closes; owned by 06.2.2-T3, read only here.</param>
/// <param name="ErrorCode">The catalogued reason of a <see cref="PublicationStatus.Failed"/> row, or <see langword="null"/>.</param>
/// <param name="PreAudit">Whether the pre-audit override replaced the submitted privacy (06 README D12).</param>
public sealed record AutopilotPublicationSnapshot(
    Guid Id,
    PublicationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConsentAt,
    DateTimeOffset? VetoNoticeSentAt,
    DateTimeOffset? VetoEndsAt,
    string? ErrorCode,
    bool PreAudit);

/// <summary>
/// The live publication of an episode, as the duplicate-create adoption needs it: the row and the authorization it
/// was created from (a manual create carries <see langword="null"/>).
/// </summary>
/// <param name="Id">The <c>publications.id</c>.</param>
/// <param name="AutopilotAuthorizationId">The authorization the row was created from, or <see langword="null"/>.</param>
public sealed record AutopilotActivePublication(Guid Id, Guid? AutopilotAuthorizationId);

/// <summary>
/// The read side of the render/publish steps: the four rows they inspect, behind one narrow port so a step is
/// unit-testable without an <see cref="IAppDbContext"/> double and so each query stays one place.
/// </summary>
/// <remarks>
/// Every method runs in the caller's already pinned tenant scope (<c>ISystemContext.For(run.UserId)</c>, the
/// orchestrator's per-run scope), so no implementation may add <c>IgnoreQueryFilters</c>: a row of another owner
/// must read as absent. The rows are read without tracking — the step changes only the run, and the orchestrator
/// saves exactly that one row.
/// </remarks>
public interface IAutopilotStepReader
{
    /// <summary>Reads one episode of the caller's scope.</summary>
    /// <param name="episodeId">The episode id.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The facts, or <see langword="null"/> when the row is not in this scope.</returns>
    ValueTask<AutopilotRenderEpisode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct);

    /// <summary>
    /// Resolves the channel connection a run's authorization names (plan §3.7: the authorization snapshots it).
    /// </summary>
    /// <param name="authorizationId">The run's snapped authorization, or <see langword="null"/> when it has none.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The facts, or <see langword="null"/> when there is no authorization, it was revoked, it records no channel
    /// (S66-7 leaves the column nullable), or the connection is not in this scope.
    /// </returns>
    ValueTask<AutopilotChannelSnapshot?> FindChannelAsync(Guid? authorizationId, CancellationToken ct);

    /// <summary>
    /// Resolves the series' current authorization: the revision <c>series.current_autopilot_authorization_id</c>
    /// names, provided that row exists, was never revoked and belongs to this series (plan §3.7, S71 SEC-5).
    /// </summary>
    /// <param name="series">The run's series, with its current pointer.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>
    /// The authorized revision, or <see langword="null"/> when the series has none to publish under.
    /// </returns>
    ValueTask<Guid?> FindCurrentAuthorizationAsync(
        NightTales.Domain.Series.Series series,
        CancellationToken ct);

    /// <summary>Reads the publication a run attached.</summary>
    /// <param name="publicationId">The publication id.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The facts, or <see langword="null"/> when the row is not in this scope.</returns>
    ValueTask<AutopilotPublicationSnapshot?> FindPublicationAsync(Guid publicationId, CancellationToken ct);

    /// <summary>The episode's newest live publication (not failed, not cancelled), if any.</summary>
    /// <param name="episodeId">The episode id.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The row with its authorization id, or <see langword="null"/> when the episode has none.</returns>
    ValueTask<AutopilotActivePublication?> FindActivePublicationAsync(Guid episodeId, CancellationToken ct);

    /// <summary>Counts the series' runs that ended <c>published_private_locked</c> (plan §3.6 row 14's N).</summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The number of locked-private outcomes of the series.</returns>
    ValueTask<int> CountPrivateLockedRunsAsync(Guid seriesId, CancellationToken ct);
}

/// <summary>The <see cref="IAutopilotStepReader"/> over the caller's tracked write model, read-only.</summary>
/// <param name="db">The per-run scope's context, already pinned to the run's owner.</param>
internal sealed class EfAutopilotStepReader(IAppDbContext db) : IAutopilotStepReader
{
    /// <inheritdoc />
    public async ValueTask<AutopilotRenderEpisode?> FindEpisodeAsync(Guid episodeId, CancellationToken ct)
    {
        // Read as the entity and map in memory: `Version` is the mapped `xmin` concurrency token, which a
        // constructor projection would have to translate.
        var episode = await db.Episodes
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == episodeId, ct)
            .ConfigureAwait(false);

        return episode is null
            ? null
            : new AutopilotRenderEpisode(episode.Id, episode.Status, episode.Version, episode.Title, episode.Brief);
    }

    /// <inheritdoc />
    public async ValueTask<AutopilotChannelSnapshot?> FindChannelAsync(Guid? authorizationId, CancellationToken ct)
    {
        if (authorizationId is not { } authorization)
        {
            return null;
        }

        var connectionId = await db.SeriesAutopilotAuthorizations
            .AsNoTracking()
            .Where(row => row.Id == authorization && row.RevokedAt == null)
            .Select(row => row.ChannelConnectionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (connectionId is not { } resolved)
        {
            return null;
        }

        var connection = await db.ChannelConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == resolved, ct)
            .ConfigureAwait(false);

        return connection is null
            ? null
            : new AutopilotChannelSnapshot(connection.Id, connection.Platform, connection.Status);
    }

    /// <inheritdoc />
    public ValueTask<Guid?> FindCurrentAuthorizationAsync(
        NightTales.Domain.Series.Series series,
        CancellationToken ct) =>
        // One implementation of the §3.7 rule: the run's own guard reads the same filtered pointer.
        new(AutopilotStepSupport.CurrentAuthorizationAsync(db, series, ct));

    /// <inheritdoc />
    public async ValueTask<AutopilotPublicationSnapshot?> FindPublicationAsync(
        Guid publicationId,
        CancellationToken ct)
    {
        var publication = await db.Publications
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == publicationId, ct)
            .ConfigureAwait(false);

        if (publication is null)
        {
            return null;
        }

        return new AutopilotPublicationSnapshot(
            publication.Id,
            publication.Status,
            publication.CreatedAt,
            publication.ConsentAt,
            publication.VetoNoticeSentAt,
            publication.VetoEndsAt,
            publication.ErrorCode,
            // The stamp 06.2.1 writes when it replaced the submitted privacy (06 README D12/YR20); absent on a row
            // whose privacy was committed as it was.
            PublicationPlatformPayload.ReadPreAudit(publication.PlatformPayloadJson) is not null);
    }

    /// <inheritdoc />
    public async ValueTask<AutopilotActivePublication?> FindActivePublicationAsync(
        Guid episodeId,
        CancellationToken ct) =>
        await db.Publications
            .AsNoTracking()
            .Where(row => row.EpisodeId == episodeId
                && row.Status != PublicationStatus.Failed
                && row.Status != PublicationStatus.Cancelled)
            // UUIDv7 ids sort by creation, so the newest row is the one a duplicate create collided with.
            .OrderByDescending(row => row.Id)
            .Select(row => new AutopilotActivePublication(row.Id, row.AutopilotAuthorizationId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<int> CountPrivateLockedRunsAsync(Guid seriesId, CancellationToken ct) =>
        await db.AutopilotRuns
            .AsNoTracking()
            .CountAsync(
                row => row.SeriesId == seriesId && row.Outcome == AutopilotRunOutcome.PublishedPrivateLocked,
                ct)
            .ConfigureAwait(false);
}
