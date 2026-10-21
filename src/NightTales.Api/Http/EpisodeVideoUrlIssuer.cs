using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NightTales.Api.Contracts.Video;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Assets;
using NightTales.Domain.Assets;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;

namespace NightTales.Api.Http;

/// <summary>
/// Issues the presigned read URL of an episode's final video and records that it was issued
/// (09.7.2 plan §5, 09 README BD10/BD15).
/// </summary>
/// <remarks>
/// <para>
/// It lives here, not in <c>Api/Endpoints</c>, because the architecture rule
/// <c>Endpoints_DoNotReferenceDbContexts</c> forbids an endpoint type from naming <c>IReadDb</c>
/// (09.1.2-T1) — exactly like <see cref="PostCommitETagReader"/>. The endpoint therefore binds the
/// route and turns the outcome into a response, and this type does the work.
/// </para>
/// <para>
/// A <c>GET</c> has no Mediator pipeline (D5), so this is the one place that opens the unit of work
/// itself: <c>IAuditWriter.WriteAsync</c> joins the ambient transaction and never commits, and it
/// throws when there is none. The transaction wraps the audit insert only — no lock is taken on the
/// episode read — and a commit that fails answers 503 without handing out a URL, because an unaudited
/// presigned URL must not exist.
/// </para>
/// <para>
/// The audit row carries ids and the expiry only: never the URL, never the signature, never a key
/// (BD16). <c>ip</c>/<c>user_agent</c> stay null — issuing a read URL is not a security event — while
/// <c>subject_user_id</c> is always set, because <c>ck_audit_logs_user_visible_has_subject</c> forbids
/// a null subject on a <c>visibility = user</c> row.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model; its D10 tenant filter is what makes a foreign episode a 404.</param>
/// <param name="blobs">The store that signs the URL.</param>
/// <param name="storage">The storage settings: buckets, endpoint and the clamped read-URL lifetime.</param>
/// <param name="unitOfWork">The transaction of the audit write.</param>
/// <param name="audit">The audit writer; it joins the ambient transaction and never commits.</param>
/// <param name="currentUser">The caller: the subject of the audit row.</param>
/// <param name="clock">The instant the signature is built from; the same one <c>expiresAt</c> reports.</param>
public sealed class EpisodeVideoUrlIssuer(
    IReadDb readDb,
    IBlobStore blobs,
    IOptions<StorageSettings> storage,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>The audit action of this route.</summary>
    public const string AuditAction = "episode.video_url_issued";

    /// <summary>The entity type the audit row names.</summary>
    public const string AuditEntityType = "episode";

    private static readonly JsonSerializerOptions AuditJson = new();

    /// <summary>
    /// Issues the URL of the episode's final video asset.
    /// </summary>
    /// <param name="episodeId">The route's episode id.</param>
    /// <param name="cancellationToken">Cancels the reads and the audit commit.</param>
    /// <returns>
    /// The URL and its expiry; <c>not_found</c> when the episode is unknown, foreign, or has no live
    /// final video asset (the honest answer for an asset the sweep already deleted); or
    /// <c>service_unavailable</c> when the store cannot sign or the audit cannot commit.
    /// </returns>
    public async Task<ErrorOr<VideoUrlDto>> IssueAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        var episode = await readDb.Query<Episode>()
            .Where(candidate => candidate.Id == episodeId)
            .Select(candidate => new { candidate.Title, candidate.FinalVideoAssetId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Unknown, foreign (the tenant filter of D10 hides it) or not rendered yet: 404 for all three, so
        // a caller cannot probe for another user's episode ids (D10).
        if (episode is null || episode.FinalVideoAssetId is not { } assetId)
        {
            return Errors.NotFound();
        }

        var asset = await readDb.Query<Asset>()
            .Where(candidate => candidate.Id == assetId && candidate.DeletedAt == null)
            .Select(candidate => new { candidate.Kind, candidate.StorageKey })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (asset is null)
        {
            return Errors.NotFound();
        }

        var settings = storage.Value;
        var ttl = settings.ReadUrlTtl;
        var expiresAt = clock.UtcNow.Add(ttl);

        var url = blobs.CreateReadUrl(
            BlobLocator.BucketFor(asset.Kind, settings),
            asset.StorageKey,
            ttl,
            $"{VideoSlug.From(episode.Title)}.mp4");

        if (url.IsError)
        {
            return url.FirstError;
        }

        if (currentUser.Id is not { } subjectId)
        {
            // The route requires authentication, so this only happens for a scope with no actor at all;
            // answering 404 keeps the D10 rule that a caller never learns whether a row exists.
            return Errors.NotFound();
        }

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        await audit.WriteAsync(
            new AuditEntry(
                ActorUserId: subjectId,
                ActorType: currentUser.Actor,
                SubjectUserId: subjectId,
                Action: AuditAction,
                Category: Category.Episodes,
                Visibility: Visibility.User,
                EntityType: AuditEntityType,
                EntityId: episodeId,
                DataJson: JsonSerializer.Serialize(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["episodeId"] = episodeId.ToString(),
                        ["assetId"] = assetId.ToString(),
                        ["expiresAt"] = expiresAt.ToString("O", CultureInfo.InvariantCulture),
                    },
                    AuditJson),
                Ip: null,
                UserAgent: null,
                ActorLabel: null,
                OccurredAt: clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (committed.IsError)
        {
            return committed.FirstError;
        }

        return new VideoUrlDto(url.Value.ToString(), expiresAt);
    }
}
