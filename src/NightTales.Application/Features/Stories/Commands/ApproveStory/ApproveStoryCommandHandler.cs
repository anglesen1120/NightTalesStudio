using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Rendering;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Commands.ApproveStory;

/// <summary>
/// Locks and approves the reviewed version in the fixed D8/D9/D12 precondition order, with the 03 D10 keyword
/// lint as its last precondition.
/// </summary>
/// <remarks>
/// <para>
/// The command pipeline owns the transaction. A refreshed blocking verdict is tracked but an error result rolls
/// it back along with all approval changes; a later attempt rechecks it. No independent write-back transaction.
/// </para>
/// <para>
/// The preconditions run in one fixed order and never reorder: the idempotent already-approved success, the
/// state check, If-Match (428/412), the gate's stages 3-6, the 03 D10 keyword lint against the episode's
/// <see cref="NameCorpus"/> (03 D11), and only then stage 7 (the moderation refresh) and the state change. The
/// lint sits after If-Match so a stale tab still gets its 412, and before every write so a story that names a
/// real person can never be approved. Only the lint's issues are read: neither a keyword nor a person's name
/// leaves this handler (03 §8, 02 D19).
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the command pipeline owns its transaction.</param>
/// <param name="gate">The injected gate, which owns the precondition stages 3-6.</param>
/// <param name="nameCorpus">The episode's real-person corpus (03 D11); legitimately empty for fiction.</param>
/// <param name="moderation">The draft moderation refresh of stage 7.</param>
/// <param name="audit">The audit writer of the true-crime attestation row.</param>
/// <param name="currentUser">The actor the attestation audit row is written for.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="logger">The approval warnings.</param>
public sealed partial class ApproveStoryCommandHandler(
    IAppDbContext db,
    IStoryApprovalGate gate,
    INameCorpusProvider nameCorpus,
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

        // Keyword lint (03 D10/CR-14, 03.2.2-T2): it runs on the version being approved, after the gate's
        // stages 3-6 and before the moderation refresh, the state change and the audit row below, so a
        // stale tab still gets its 428/412 and a story that names a real person is never approved. The corpus is
        // the episode's own research (03 D11); fiction is the provider's decision, not this handler's, so the
        // load is unconditional and an empty corpus is not evidence of a clean story. The load is not caught:
        // a corpus read failure is transient and the D5 pipeline answers it as a 503, never as a pass.
        var corpus = await nameCorpus.LoadAsync(episode.Id, cancellationToken).ConfigureAwait(false);

        // Fail closed, like the refresh below: the lint cannot be evaluated over content that cannot be read,
        // so the approval stays retryable rather than silently passing the check.
        if (!StoryContentJson.TryReadStored(version.Content, out var story))
        {
            return KeywordCheckUnavailable();
        }

        // Only the issues are read. ToFlags maps each one to its D22 segment address, keyword index and rule id,
        // so no keyword and no person's name can reach the 409 body or a log line (03 §8, 02 D19).
        var lintIssues = RenderKeywordLinter.Lint(KeywordLintMapping.SanitiseKeywords(Segments(story)), corpus);
        if (lintIssues.Count > 0)
        {
            return Errors.KeywordsFlagged(KeywordLintMapping.ToFlags(lintIssues));
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

    /// <summary>
    /// Fail closed when the keyword lint cannot be evaluated: the same 503 the gate answers for a precondition it
    /// cannot complete, so a client retries the approval instead of reading a keyword verdict it never got.
    /// </summary>
    private static Error KeywordCheckUnavailable() => Error.Failure(
        ErrorCodes.ServiceUnavailable,
        "The keyword check could not be completed; retry the approval.");

    /// <summary>
    /// The version's keywords in the D22 story order the linter reads: <c>hook</c>, <c>p:&lt;i&gt;</c> per
    /// paragraph, then <c>ending</c> (03 §4, CR-13), mirroring the reader of <c>StoryRenderInputProvider</c>.
    /// That provider is not reused here: it resolves the approved pointer row itself, while this path already
    /// holds the version being approved and must lint that one, not the approved one.
    /// </summary>
    /// <param name="content">The stored draft of the version being approved.</param>
    /// <returns>The ordered segments; a segment with no keyword is still addressed, in order.</returns>
    private static SegmentKeywords[] Segments(StoryContentV1 content)
    {
        var segments = new SegmentKeywords[content.Paragraphs.Count + 2];
        segments[0] = new SegmentKeywords("hook", content.Hook.Keywords);
        for (var index = 0; index < content.Paragraphs.Count; index++)
        {
            segments[index + 1] = new SegmentKeywords(
                "p:" + index.ToString(CultureInfo.InvariantCulture),
                content.Paragraphs[index].Keywords);
        }

        segments[^1] = new SegmentKeywords("ending", content.Ending.Keywords);
        return segments;
    }

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
