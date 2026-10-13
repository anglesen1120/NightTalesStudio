using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.ReadModels;

/// <summary>Shared column projection and fail-safe parsing for story detail reads.</summary>
internal static partial class StoryReadProjection
{
    internal static IQueryable<VersionRow> Versions(IReadDb readDb, Guid episodeId, Guid versionId) =>
        readDb.Query<StoryVersion>().AsNoTracking()
            .Where(row => row.EpisodeId == episodeId && row.Id == versionId)
            .Select(row => new VersionRow(
                row.Id, row.Version, row.Source, row.CreatedAt, row.Content, row.Body,
                row.Keywords, row.WordCount, row.AuditReport, row.SimilarityScore, row.FactSheetId));

    internal static async Task<CurrentStoryDto?> CurrentAsync(
        IReadDb readDb, Guid episodeId, Guid? currentVersionId, EpisodeStatus status,
        Guid? approvedVersionId, ILogger logger, CancellationToken cancellationToken)
    {
        if (currentVersionId is not { } versionId)
        {
            return null;
        }

        var row = await Versions(readDb, episodeId, versionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var detail = Detail(row, episodeId, logger);
        return new CurrentStoryDto(
            detail.VersionId, detail.Version, detail.Source, detail.Etag, status, detail.Content,
            detail.Body, detail.Keywords, detail.WordCount, detail.QualityHints, detail.AuditReport,
            detail.SimilarityScore, detail.FactSheetId, approvedVersionId, Review(detail.AuditReport));
    }

    internal static StoryVersionDetailDto Detail(VersionRow row, Guid episodeId, ILogger logger)
    {
        var content = Parse(row.Content, episodeId, row.VersionId, logger, isAudit: false);
        var audit = Parse(row.AuditReport, episodeId, row.VersionId, logger, isAudit: true);
        return new StoryVersionDetailDto(
            row.VersionId, row.Version, Wire(row.Source), row.CreatedAt,
            row.Source == StoryVersionSource.Edited ? "user" : "system", StoryEtag.For(row.VersionId),
            content, row.Body, row.Keywords, row.WordCount,
            Hints(audit), audit, row.SimilarityScore, row.FactSheetId);
    }

    internal static string Wire(StoryVersionSource source) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(source.ToString());

    private static JsonElement? Parse(string? json, Guid episodeId, Guid versionId, ILogger logger, bool isAudit)
    {
        if (json is null && isAudit)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // Catch only malformed JSON. Cancellation and unrelated failures must propagate.
        }

        if (isAudit)
        {
            LogAuditUnreadable(logger, episodeId, versionId);
        }
        else
        {
            LogContentUnreadable(logger, episodeId, versionId);
        }

        return null;
    }

    private static List<QualityHintDto> Hints(JsonElement? audit)
    {
        if (audit is not { } root || !Object(root, "local", out var local)
            || !Array(local, "issues", out var issues))
        {
            return [];
        }

        var hints = new List<QualityHintDto>();
        foreach (var issue in issues.EnumerateArray())
        {
            var rule = Text(issue, "rule") ?? Text(issue, "ruleId");
            var messageKey = Text(issue, "messageKey") ?? Text(issue, "message_key");
            if (string.IsNullOrWhiteSpace(messageKey) && rule is not null)
            {
                StoryQualityChecker.MessageKeys.TryGetValue(rule, out messageKey);
            }

            if (rule is null || string.IsNullOrWhiteSpace(messageKey))
            {
                // Pipeline issues without a catalogue key stay verbatim in auditReport.local.issues (R8).
                // Do not invent an i18n key or log them on every read.
                continue;
            }

            hints.Add(new QualityHintDto(
                rule, Text(issue, "severity") ?? "advisory", Text(issue, "kind") ?? "objective",
                Text(issue, "segment") ?? "", messageKey, Text(issue, "evidence") ?? "",
                Text(issue, "repair_direction") ?? Text(issue, "repairDirection"),
                Number(issue, "measured"), Number(issue, "threshold")));
        }

        // Stable within each kind, matching QualityReportMapping.ToHints; no checks are recomputed.
        return hints.OrderBy(hint => hint.Kind == "objective" ? 0 : 1).ToList();
    }

    private static StoryReviewSummaryDto? Review(JsonElement? audit)
    {
        if (audit is not { } root || !Object(root, "model_review", out var review))
        {
            return null;
        }

        // 02.6.2-T1 (D22): an applied revision's review #2 is the current state and lives in
        // revision.review_after; model_review still holds review #1, i.e. the blocking issues the revision fixed.
        // Scores and issue counts therefore read review_after when it is present and well formed, and fall back
        // to model_review otherwise (fail closed). The stale flag and the drift advisory are unchanged.
        var source = RevisionReviewAfter(root, out var reviewAfter) ? reviewAfter : review;

        JsonElement? scores = Object(source, "scores", out var scoreObject) ? scoreObject : null;
        var blocking = 0;
        var advisory = 0;
        if (Array(source, "issues", out var issues))
        {
            foreach (var issue in issues.EnumerateArray())
            {
                switch (Text(issue, "severity"))
                {
                    case "blocking": blocking++; break;
                    case "advisory": advisory++; break;
                }
            }
        }

        var stale = !review.TryGetProperty("stale", out var flag) || flag.ValueKind != JsonValueKind.False;
        return new StoryReviewSummaryDto(scores, new StoryIssueCountsDto(blocking, advisory), Drift(root), stale);
    }

    /// <summary>
    /// The accepted review #2 of an applied revision (02.6.2-T1, D22), or <c>false</c> when the block is missing or
    /// malformed: <c>revision.applied</c> must be the boolean <c>true</c> and <c>revision.review_after</c> an object
    /// carrying both the <c>scores</c> object and the <c>issues</c> array of the <c>audit.v1</c> layout. A
    /// <c>false</c> result makes the caller read <c>model_review</c> instead; nothing here throws.
    /// </summary>
    private static bool RevisionReviewAfter(JsonElement root, out JsonElement review)
    {
        review = default;
        if (!Object(root, "revision", out var revision)
            || !Property(revision, "applied", out var applied) || applied.ValueKind != JsonValueKind.True
            || !Object(revision, "review_after", out var after)
            || !Object(after, "scores", out _) || !Array(after, "issues", out _))
        {
            return false;
        }

        review = after;
        return true;
    }

    /// <summary>
    /// Whether the audit carries a revision-drift advisory (02.6.2-T1, D22): the <c>revision_drift</c> issue inside
    /// <c>revision.review_after.issues</c>, or any set flag of the additive <c>revision.drift</c> detail. A missing or
    /// malformed block reads as no drift, and nothing here throws (fail-closed to false).
    /// </summary>
    private static bool Drift(JsonElement audit)
    {
        if (!Object(audit, "revision", out var revision))
        {
            return false;
        }

        if (Object(revision, "review_after", out var after) && Array(after, "issues", out var issues))
        {
            foreach (var issue in issues.EnumerateArray())
            {
                if (string.Equals(Text(issue, "type"), "revision_drift", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        if (!Object(revision, "drift", out var drift))
        {
            return false;
        }

        return DriftFlag(drift, "segment_count_changed")
            || DriftItems(drift, "changed_segments")
            || DriftItems(drift, "unflagged_changed")
            || DriftItems(drift, "added_findings");
    }

    private static bool DriftFlag(JsonElement drift, string name) =>
        Property(drift, name, out var flag) && flag.ValueKind == JsonValueKind.True;

    private static bool DriftItems(JsonElement drift, string name) =>
        Array(drift, name, out var items) && items.GetArrayLength() > 0;

    private static bool Object(JsonElement root, string name, out JsonElement value) =>
        Property(root, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool Array(JsonElement root, string name, out JsonElement value) =>
        Property(root, name, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool Property(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value);
    }

    private static string? Text(JsonElement root, string name) =>
        Property(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Number(JsonElement root, string name) =>
        Property(root, name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number) ? number : null;

    internal sealed record VersionRow(
        Guid VersionId, int Version, StoryVersionSource Source, DateTimeOffset CreatedAt,
        string Content, string Body, IReadOnlyList<string> Keywords, int WordCount,
        string? AuditReport, decimal? SimilarityScore, Guid? FactSheetId);

    [LoggerMessage(EventId = 6020, Level = LogLevel.Warning,
        Message = "story.read.content-unreadable episode={EpisodeId} version={VersionId}")]
    private static partial void LogContentUnreadable(ILogger logger, Guid episodeId, Guid versionId);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Warning,
        Message = "story.read.audit-unreadable episode={EpisodeId} version={VersionId}")]
    private static partial void LogAuditUnreadable(ILogger logger, Guid episodeId, Guid versionId);
}
