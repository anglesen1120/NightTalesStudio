using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Features.Stories.Approval;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Quality;
using Topic = NightTales.Domain.Series.Topic;

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
        Guid? approvedVersionId, AttestationRequest attestation, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attestation);

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
        var current = new CurrentStoryDto(
            detail.VersionId, detail.Version, detail.Source, detail.Etag, status, detail.Content,
            detail.Body, detail.Keywords, detail.WordCount, detail.QualityHints, detail.AuditReport,
            detail.SimilarityScore, detail.FactSheetId, approvedVersionId, Review(detail.AuditReport),
            Attestation: null);

        // The attestation member is the only part of this read that needs a second table (02.3.3-T1):
        // it is read after the story row so the version, its fact-sheet pin and the approval pointer are
        // already known. The approval pointer comes from this same read — no caller signature changes.
        return current with
        {
            Attestation = await AttestationAsync(
                readDb, attestation, versionId, approvedVersionId, row.FactSheetId, logger, cancellationToken)
                .ConfigureAwait(false),
        };
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

    /// <summary>
    /// The attestation member of a current story (02.3.3-T1), or <see langword="null"/> when the episode
    /// is not true-crime-governed.
    /// </summary>
    /// <remarks>
    /// Goal of the two reads: the episode read that produced this projection decides governance and the
    /// content language, and one bounded <c>audit_logs</c> read finds the live tick of this very version —
    /// the member is never a story column, because an attestation is an event with a time. Nothing here
    /// throws: a text the release no longer ships and a malformed audit payload both read as "no text"
    /// and "no tick", because the approval gate is what enforces them, never the read model.
    /// </remarks>
    private static async Task<AttestationViewDto?> AttestationAsync(
        IReadDb readDb, AttestationRequest request, Guid versionId, Guid? approvedVersionId, Guid? factSheetId,
        ILogger logger, CancellationToken cancellationToken)
    {
        // The gate's pinned-data rule (SEC-1): a true-crime topic governs now, and a version that
        // carries a fact sheet id stays governed even if the series topic was patched afterwards. The
        // topic and language arrive with the episode read that produced this projection, so a fiction
        // episode costs no query beyond the story row, and a true-crime one only its attestation rows.
        if (request.Topic != Topic.TrueCrime && factSheetId is null)
        {
            return null;
        }

        var configured = request.Options.AttestationVersion;
        string? text = null;

        try
        {
            var (manifestVersion, shipped) = await request.Provider.GetAsync(request.Language, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(manifestVersion, configured, StringComparison.Ordinal))
            {
                text = shipped;
            }
            else
            {
                // The configured version is the single accepted value, so the member keeps it and shows
                // no wording: text of another version would invite a tick the gate then rejects.
                WarnTextUnavailableOnce(logger, request.EpisodeId, configured, manifestVersion);
            }
        }
        catch (PromptCatalogException)
        {
            // The release does not track the attestation text at all.
            WarnTextUnavailableOnce(logger, request.EpisodeId, configured, MissingManifestVersion);
        }

        var acceptedAt = await AcceptedAtAsync(readDb, request, versionId, approvedVersionId, cancellationToken)
            .ConfigureAwait(false);

        return new AttestationViewDto(true, configured, text, acceptedAt);
    }

    /// <summary>
    /// When <paramref name="versionId"/> was attested under the configured text version, or
    /// <see langword="null"/> when it was not (02.3.3-T1): the newest attestation rows of the episode,
    /// then the pair of the row that is the live tick of this very version.
    /// </summary>
    /// <remarks>
    /// <c>audit_logs</c> is not <see cref="IUserOwned"/> and therefore carries no tenant filter, so the
    /// episode's owner is part of the predicate — never <c>IgnoreQueryFilters</c> and never a read across
    /// users. The window bounds what is loaded: an approval writes one row per accepted version.
    /// </remarks>
    private static async Task<DateTimeOffset?> AcceptedAtAsync(
        IReadDb readDb, AttestationRequest request, Guid versionId, Guid? approvedVersionId,
        CancellationToken cancellationToken)
    {
        // A tick is live only for the version that is approved right now (02.3.3-T1, CR-2/S4): reopening
        // clears `approved_story_version_id` but keeps the current version, and the gate demands a fresh
        // tick on re-approval, so the stored rows of a reopened version must not read as accepted. One
        // comparison saves the whole audit read in that case, and it cannot hide a tick: a re-approval
        // writes a new row against the version it approves, which is the pointer this reads.
        if (approvedVersionId != versionId)
        {
            return null;
        }

        var configured = request.Options.AttestationVersion;

        var rows = await readDb.Query<AuditLog>().AsNoTracking()
            .Where(row => row.EntityType == EpisodeEntityType
                && row.EntityId == request.EpisodeId
                && row.Action == TrueCrimeAttestedAction
                && row.SubjectUserId == request.UserId)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(AttestationRowWindow)
            .Select(row => new AttestationRow(row.CreatedAt, row.DataJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            var tick = AttestedTick(row.DataJson);

            // The version must match and so must the wording it was ticked against: a wording change
            // ships a new AttestationVersion, and every stored tick of the old text stops counting
            // (02.3.3-T1, CR-1). Both keys are matched in memory over the bounded window.
            if (tick.VersionId == versionId && string.Equals(tick.TextVersion, configured, StringComparison.Ordinal))
            {
                return row.CreatedAt;
            }
        }

        return null;
    }

    /// <summary>
    /// The (version id, text version) pair of an attestation row's <c>data</c>, both null when the payload
    /// is unreadable or carries no such member (02.3.3-T1, D13).
    /// </summary>
    private static (Guid? VersionId, string? TextVersion) AttestedTick(string dataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var payload = document.RootElement;
                return (ReadGuid(payload, StoryVersionIdMember), ReadText(payload, TextVersionMember));
            }
        }
        catch (JsonException)
        {
            // A payload this read cannot parse carries no tick; the next row may still be the live one.
        }

        return (null, null);
    }

    /// <summary>A GUID member of an audit payload, or null when it is absent or not a GUID string.</summary>
    private static Guid? ReadGuid(JsonElement payload, string member) =>
        payload.TryGetProperty(member, out var value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var id)
                ? id
                : null;

    /// <summary>A string member of an audit payload, or null when it is absent or not a string.</summary>
    private static string? ReadText(JsonElement payload, string member) =>
        payload.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Warns once per process when the shipped attestation text is not the configured version
    /// (02.3.3-T1): the condition is one configuration bug, and warning on every episode read would fill
    /// the log with it. The log carries ids and version markers only.
    /// </summary>
    private static void WarnTextUnavailableOnce(ILogger logger, Guid episodeId, string configured, string manifestVersion)
    {
        if (Interlocked.Exchange(ref _attestationTextWarningLogged, 1) == 0)
        {
            LogAttestationTextUnavailable(logger, episodeId, configured, manifestVersion);
        }
    }

    /// <summary>The action of the approval-time attestation row (02.3.3-T1, D13).</summary>
    private const string TrueCrimeAttestedAction = "episode.true_crime_attested";

    /// <summary>
    /// The entity an attestation row names (02.3.3-T1, D13; the approval handler writes the same value, so
    /// the predicate matches <c>ix_audit_logs_entity (entity_type, entity_id)</c> instead of scanning).
    /// </summary>
    private const string EpisodeEntityType = "episode";

    /// <summary>The <c>data</c> member naming the story version the tick belongs to.</summary>
    private const string StoryVersionIdMember = "story_version_id";

    /// <summary>The <c>data</c> member naming the attestation text version the tick was made against.</summary>
    private const string TextVersionMember = "text_version";

    /// <summary>How many attestation rows of one episode are examined for the current version's tick.</summary>
    private const int AttestationRowWindow = 20;

    /// <summary>What the warning reports when the release tracks no attestation text at all.</summary>
    private const string MissingManifestVersion = "untracked";

    /// <summary>Whether the "attestation text is not the configured version" warning has been logged.</summary>
    private static int _attestationTextWarningLogged;

    /// <summary>One bounded attestation row: when it happened and the payload naming its version and wording.</summary>
    private sealed record AttestationRow(DateTimeOffset CreatedAt, string DataJson);

    /// <summary>
    /// The inputs the attestation member needs beyond the story row (02.3.3-T1): the episode and its
    /// owner for the audit predicate, the series' topic and language (read with the episode, never as a
    /// second query), the validated options and the text provider.
    /// </summary>
    /// <param name="EpisodeId">The episode the read is scoped to; also the audit row's entity id.</param>
    /// <param name="UserId">The episode's owner; the audit row's subject, and the tenant predicate of a table without a filter.</param>
    /// <param name="Topic">The series topic that governs the member: true crime, or any topic whose version pins a fact sheet.</param>
    /// <param name="Language">The content language of the series, which selects the clause text.</param>
    /// <param name="Options">The validated <c>Story:TrueCrime</c> options.</param>
    /// <param name="Provider">The attestation text provider of the host.</param>
    internal sealed record AttestationRequest(
        Guid EpisodeId,
        Guid UserId,
        Topic Topic,
        Language Language,
        StoryTrueCrimeOptions Options,
        IAttestationTextProvider Provider);

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

    [LoggerMessage(EventId = 6022, Level = LogLevel.Warning,
        Message = "story.read.attestation-text-unavailable episode={EpisodeId} configured={ConfiguredVersion} manifest={ManifestVersion}")]
    private static partial void LogAttestationTextUnavailable(
        ILogger logger, Guid episodeId, string configuredVersion, string manifestVersion);
}
