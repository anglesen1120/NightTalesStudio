using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Commands.ApproveStory;

/// <summary>Locks and approves the reviewed version in the fixed D8/D9/D12 precondition order.</summary>
/// <remarks>
/// The command pipeline owns the transaction. A refreshed blocking verdict is tracked but an error result rolls
/// it back along with all approval changes; a later attempt rechecks it. No independent write-back transaction.
/// </remarks>
public sealed partial class ApproveStoryCommandHandler(
    IAppDbContext db,
    IStoryApprovalGate gate,
    StoryDraftModeration moderation,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<ApproveStoryCommandHandler> logger) : ICommandHandler<ApproveStoryCommand, ErrorOr<ApprovedStoryDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<ApprovedStoryDto>> Handle(ApproveStoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.ApprovedVersionId = null;
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);
        var episode = await db.Episodes.SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        if (command.BodyInvalid)
        {
            return Error.Validation(ErrorCodes.StoryAttestationRequired,
                "The approval body must be a JSON object with an optional attestation containing accepted and textVersion.",
                new Dictionary<string, object> { [ErrorMetadata.Field] = "body" });
        }

        // Keep the topic stable until the command transaction commits, including the gate and attestation audit.
        await db.LockSeriesForShareAsync(episode.SeriesId, cancellationToken).ConfigureAwait(false);

        // Stage 1 precedes ETag validation: only the already approved version is an idempotent success.
        if (episode.Status == EpisodeStatus.StoryApproved
            && episode.ApprovedStoryVersionId is { } approvedId
            && StoryEtag.TryParse(command.IfMatch, out var suppliedId)
            && suppliedId == approvedId
            && episode.ApprovedAt is { } approvedAt)
        {
            return Dto(episode.Id, approvedId, approvedAt);
        }

        if (episode.Status != EpisodeStatus.StoryReview)
        {
            return InvalidState();
        }

        // Stage 2: no header -> 428, malformed/foreign/old story tag -> 412.
        var matched = StoryEtag.RequireAndMatch(command.IfMatch, episode.CurrentStoryVersionId);
        if (matched.IsError)
        {
            return matched.Errors;
        }

        if (episode.SeriesId == Guid.Empty)
        {
            LogMissingSeries(logger, episode.Id);
            return InvalidState();
        }

        var version = await db.StoryVersions.SingleOrDefaultAsync(
            candidate => candidate.Id == matched.Value && candidate.EpisodeId == episode.Id, cancellationToken).ConfigureAwait(false);
        var series = await db.Series.SingleOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, cancellationToken).ConfigureAwait(false);
        if (version is null || series is null)
        {
            return Errors.NotFound();
        }

        // Stages 3–6 are owned by the injected gate. SEC-1 pinned data (02.3.2-T1 hand-over): a story that
        // carries a fact sheet stays true-crime for the gate even after a topic patch to fiction, so the
        // attestation is forwarded under the same predicate the gate and the audit row below use.
        var trueCrime = series.Topic == Topic.TrueCrime || version.FactSheetId is not null;
        var gated = await gate.ValidateAsync(episode, series, version,
            trueCrime ? command.Attestation : null, cancellationToken).ConfigureAwait(false);
        if (gated.IsError)
        {
            return gated.Errors;
        }

        // Stage 7: a missing full report is also unknown evidence and requires one fresh check (owner ruling).
        var report = ReadReport(version.AuditReport);
        var verdict = report?["moderation"];
        if (StoryDraftModeration.NeedsRecheck(verdict))
        {
            if (!StoryContentJson.TryReadStored(version.Content, out var content) || content.Tags is null)
            {
                LogRefreshFailed(logger, episode.Id);
                return Moderated();
            }

            var fresh = await moderation.CheckAsync(episode.UserId, episode.Id, version.Title, version.Body,
                content.Description, content.Tags, cancellationToken).ConfigureAwait(false);
            if (fresh is null || StoryDraftModeration.NeedsRecheck(fresh))
            {
                LogRefreshFailed(logger, episode.Id);
                return Moderated();
            }

            report ??= new JsonObject { ["schema_version"] = "audit.v1" };
            report["moderation"] = fresh;
            if (StoryDraftModeration.BlockingCategoriesOf(fresh).Count == 0
                && report["local"] is JsonObject local && local["issues"] is JsonArray issues)
            {
                // Q-MOD-01 is the checker's only moderation-derived rule. Keep every other issue unchanged.
                for (var index = issues.Count - 1; index >= 0; index--)
                {
                    if (issues[index] is JsonObject issue && issue["rule"] is JsonValue rule
                        && rule.TryGetValue<string>(out var ruleId) && ruleId == StoryQualityChecker.ModerationBlocked)
                    {
                        issues.RemoveAt(index);
                    }
                }
            }

            var updatedJson = report.ToJsonString();
            if (!TextSafety.IsPersistableJson(updatedJson))
            {
                LogRefreshFailed(logger, episode.Id);
                return Moderated();
            }

            version.RefreshModerationVerdict(updatedJson);
            verdict = fresh;
        }

        if (StoryDraftModeration.BlockingCategoriesOf(verdict).Count > 0)
        {
            return Moderated();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        var approved = episode.ApproveStory(version.Id, command.Source, now);
        if (approved.IsError)
        {
            return approved.Errors;
        }

        if (trueCrime && gated.Value.AcceptedTextVersion is { } acceptedTextVersion)
        {
            // The gate is the sole attestation authority; never audit the unvalidated request text.
            if (string.IsNullOrWhiteSpace(acceptedTextVersion)
                || !string.Equals(acceptedTextVersion, TextSafety.Clean(acceptedTextVersion), StringComparison.Ordinal))
            {
                return Error.Validation(ErrorCodes.StoryAttestationRequired, "A valid accepted attestation text version is required.");
            }

            var dataJson = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["story_version_id"] = version.Id,
                ["text_version"] = acceptedTextVersion,
            });
            if (!TextSafety.IsPersistableJson(dataJson))
            {
                return Error.Validation(ErrorCodes.StoryAttestationRequired, "A valid accepted attestation text version is required.");
            }

            await audit.WriteAsync(new AuditEntry(
                currentUser.Id,
                currentUser.Actor == ActorType.System ? ActorType.System : ActorType.User,
                episode.UserId,
                "episode.true_crime_attested",
                Category.Episodes,
                Visibility.User,
                "episode",
                episode.Id,
                dataJson,
                null,
                null,
                null,
                now), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // AuditBehavior sees null on the no-op branch, so this success alone creates the normal audit row.
        command.ApprovedVersionId = version.Id;
        return Dto(episode.Id, version.Id, now);
    }

    private static ApprovedStoryDto Dto(Guid episodeId, Guid versionId, DateTimeOffset now) =>
        new(episodeId, versionId, StoryEtag.For(versionId), "story_approved", now);

    private static Error InvalidState() => Error.Conflict(ErrorCodes.EpisodeInvalidState, "The episode cannot be approved in its current state.");

    private static Error Moderated() => Error.Conflict(ErrorCodes.StoryModerated, "The story must have a fresh nonblocking moderation verdict before approval.");

    private static JsonObject? ReadReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 6021, Level = LogLevel.Warning, Message = "Approval moderation refresh failed for episode {EpisodeId}.")]
    private static partial void LogRefreshFailed(ILogger logger, Guid episodeId);

    [LoggerMessage(EventId = 6022, Level = LogLevel.Warning, Message = "Episode {EpisodeId} has no series id during approval.")]
    private static partial void LogMissingSeries(ILogger logger, Guid episodeId);
}
