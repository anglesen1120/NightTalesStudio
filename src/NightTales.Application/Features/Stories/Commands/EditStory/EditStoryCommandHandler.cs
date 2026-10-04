using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Commands.EditStory;

/// <summary>
/// Saves the owner's full edit as a new <c>edited</c> <see cref="StoryVersion"/> (02.3.1-T1, plan §Design). Nothing is
/// written before the last early exit: the episode row lock (the same one the generation command takes) serialises
/// two edits, so the second one finds the first one's new current version and its ETag no longer matches (412).
/// </summary>
/// <remarks>
/// <para>
/// No paid call and no network call: the local check (<see cref="StoryQualityChecker"/>) is pure. The model review is only
/// flipped to <c>stale</c>, its scores stay (F25).
/// </para>
/// <para>
/// Two inputs of the plan do not exist yet on this base: the content moderator (02.4.1-T1) and the similarity scorer and
/// corpus (02.5.6). Until they land the previous <c>moderation</c> verdict is carried over marked <c>stale</c> (the plan's
/// own outage rule, so the approve gate re-checks it; when the previous <c>audit_report</c> is missing or unreadable there is
/// no verdict to carry and the new version gets <c>{ stale: true, unavailable: true }</c>, which the checker reports as a
/// blocking Q-MOD-01, never as clean) and <c>similarity</c> is the unscorable
/// <c>{ score: null, n: 0 }</c>. The fact ids a client may cite are the ones the current version already cites, and
/// <c>fact_sources</c> are carried over from it: the id-to-source mapping lives in the fact sheet (02.5.5) that this
/// base does not have, and a client-sent <c>fact_sources</c> or URL is never read.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="checker">The local quality checker (02.3.4-T1).</param>
/// <param name="prices">The active price version, which carries the D4 length limits.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="logger">Logs a current version that has no <c>audit_report</c>.</param>
public sealed partial class EditStoryCommandHandler(
    IAppDbContext db,
    StoryQualityChecker checker,
    IPriceCatalog prices,
    IClock clock,
    ILogger<EditStoryCommandHandler> logger)
    : ICommandHandler<EditStoryCommand, ErrorOr<StoryVersionDto>>
{
    private const string AuditSchemaVersion = "audit.v1";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StoryVersionDto>> Handle(EditStoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: the row lock first, then the tenant-filtered load (another user's id and a missing id are both 404).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // Step 2: draft and story_review only; any other status gets the same code (never story_not_in_review, CR-8).
        if (!episode.CanEditStory)
        {
            return Episode.EditNotAllowed();
        }

        // Step 3: 428 without the header, 412 for a stale, malformed or foreign ETag.
        var matched = StoryEtag.RequireAndMatch(command.IfMatch, episode.CurrentStoryVersionId);
        if (matched.IsError)
        {
            return matched.Errors;
        }

        var previous = await db.StoryVersions
            .SingleOrDefaultAsync(candidate => candidate.Id == matched.Value, cancellationToken)
            .ConfigureAwait(false);
        var series = await db.Series
            .SingleOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (previous is null || series is null)
        {
            return Errors.NotFound();
        }

        var now = clock.UtcNow;
        var active = await prices.GetActiveAsync(now, cancellationToken).ConfigureAwait(false);
        if (active.IsError)
        {
            return active.Errors;
        }

        var previousContentRead = StoryContentJson.TryReadStored(previous.Content, out var previousContent);
        var rules = new StoryRules
        {
            Length = LengthTargetResolver.Resolve(episode.LengthTier, series.Language, active.Value.ParseLimits()),
            Language = series.Language,
            AllowedFactRefs = previousContentRead ? FactRefsOf(previousContent) : new HashSet<string>(StringComparer.Ordinal),
        };

        // Step 4: the D3 rules. A blocking issue fails the whole save.
        var validation = StoryDraftValidator.Validate(command.Content, rules);
        if (!validation.IsValid)
        {
            return validation.Issues
                .Where(issue => issue.Severity == IssueSeverity.Blocking)
                .Select(issue => Error.Validation(
                    ErrorCodes.StoryInvalidContent,
                    $"{issue.RuleId}: {issue.RepairDirection}",
                    new Dictionary<string, object> { [ErrorMetadata.Field] = issue.Segment }))
                .ToList();
        }

        var previousAudit = ParseObject(previous.AuditReport);
        var hasUrlSources = await db.StorySources
            .AnyAsync(
                source => source.EpisodeId == episode.Id && source.Kind == StorySourceKind.Url,
                cancellationToken)
            .ConfigureAwait(false);

        // Step 5: a canonically equal story is not saved; the caller gets the current version and the same ETag.
        if (previousContentRead && StoryContentComparer.CanonicalEquals(command.Content, previousContent))
        {
            var unchangedReport = checker.Check(
                command.Content,
                CheckContext(series, episode.LengthTier, rules, hasUrlSources, previousAudit, ReviewIsStale(previousAudit)));
            return ToDto(previous, unchangedReport);
        }

        // Step 6: recompute every derived and local field on the new copy.
        if (previousAudit is null)
        {
            LogAuditReportMissing(logger, previous.Id);
        }

        var report = checker.Check(
            command.Content,
            CheckContext(series, episode.LengthTier, rules, hasUrlSources, previousAudit, modelReviewStale: true));
        var contentJson = StoryContentJson.Write(command.Content, StoryContentJson.ReadFactSources(previous.Content));
        var auditJson = BuildAuditReport(previousAudit, report);

        // Step 7: the new row, the pointer move and the event; the pipeline commits them with the audit row.
        var maxVersion = await db.StoryVersions
            .Where(candidate => candidate.EpisodeId == episode.Id)
            .MaxAsync(candidate => candidate.Version, cancellationToken)
            .ConfigureAwait(false);

        var created = StoryVersion.Create(
            episode.UserId,
            episode.Id,
            maxVersion + 1,
            StoryVersionSource.Edited,
            command.Content,
            contentJson,
            model: null,
            promptVersion: null,
            generationRunId: null,
            parentVersionId: previous.Id,
            factSheetId: previous.FactSheetId,
            auditReportJson: auditJson,
            similarityScore: null,
            now);
        db.StoryVersions.Add(created);

        var saved = episode.SaveEditedStoryVersion(created.Id, created.Version, now);
        if (saved.IsError)
        {
            return saved.Errors;
        }

        command.Outcome = new EditStoryOutcome(created.Id, created.Version);
        return ToDto(created, report);
    }

    private static StoryCheckContext CheckContext(
        NightTales.Domain.Series.Series series,
        LengthTier tier,
        StoryRules rules,
        bool hasUrlSources,
        JsonObject? previousAudit,
        bool modelReviewStale) =>
        new(
            series.Topic,
            tier,
            rules.Length,
            rules,
            rules.AllowedFactRefs.Count,
            [],
            hasUrlSources,
            null,
            SimilarityVerdict.Pass,
            ModerationCategoriesOf(previousAudit),
            modelReviewStale,
            null,
            ModerationIsUnavailable(previousAudit));

    private static HashSet<string> FactRefsOf(StoryContentV1 content)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in Segments(content))
        {
            foreach (var factRef in segment.FactRefs ?? [])
            {
                refs.Add(factRef);
            }
        }

        return refs;
    }

    private static IEnumerable<StorySegmentV1> Segments(StoryContentV1 content)
    {
        if (content.Hook is not null)
        {
            yield return content.Hook;
        }

        foreach (var paragraph in content.Paragraphs ?? [])
        {
            yield return paragraph;
        }

        if (content.Ending is not null)
        {
            yield return content.Ending;
        }
    }

    private static JsonObject? ParseObject(string? json)
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

    private static bool ReviewIsStale(JsonObject? audit) =>
        audit?["model_review"] is not JsonObject review
        || review["stale"] is not JsonValue stale
        || !stale.TryGetValue<bool>(out var flag)
        || flag;

    private static List<string>? ModerationCategoriesOf(JsonObject? audit)
    {
        if (audit?["moderation"] is not JsonObject moderation || moderation["categories"] is not JsonArray categories)
        {
            return null;
        }

        return categories
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .OfType<string>()
            .ToList();
    }

    /// <summary>
    /// True when there is no moderation verdict to carry: the previous <c>audit_report</c> is missing or unreadable, or it
    /// already carries the <c>unavailable</c> marker. The checker then reports the text as not moderated (fails closed).
    /// </summary>
    private static bool ModerationIsUnavailable(JsonObject? audit) =>
        audit is null
        || (audit["moderation"] is JsonObject moderation
            && moderation["unavailable"] is JsonValue unavailable
            && unavailable.TryGetValue<bool>(out var flag)
            && flag);

    /// <summary>
    /// The new <c>audit.v1</c> document (02 README D22): <c>local</c> recomputed, <c>model_review</c> kept with
    /// <c>stale = true</c>, the previous <c>moderation</c> verdict kept marked stale, <c>revision</c> carried over and
    /// <c>similarity</c> reset to the unscorable shape.
    /// </summary>
    private static string BuildAuditReport(JsonObject? previous, QualityReport report)
    {
        var root = new JsonObject
        {
            ["schema_version"] = AuditSchemaVersion,
            ["local"] = new JsonObject
            {
                ["issues"] = new JsonArray(report.ToAuditIssues()
                    .Select(issue => (JsonNode)new JsonObject
                    {
                        ["rule"] = issue.Rule,
                        ["severity"] = issue.Severity,
                        ["kind"] = issue.Kind,
                        ["segment"] = issue.Segment,
                        ["evidence"] = issue.Evidence,
                        ["repair_direction"] = issue.RepairDirection,
                    })
                    .ToArray()),
            },
        };

        if (previous is null)
        {
            // NTS-EDIT-L1: no readable previous report means no verdict to carry. A missing key would read as "never
            // flagged", so the marker says the verdict is unavailable and stale; the approve gate and the moderator
            // (02.3.2 / 02.4.1) must treat it as not moderated and re-check.
            root["moderation"] = new JsonObject { ["stale"] = true, ["unavailable"] = true };
        }
        else if (previous["moderation"] is JsonObject moderation)
        {
            var kept = (JsonObject)moderation.DeepClone();
            kept["stale"] = true;
            root["moderation"] = kept;
        }

        var review = previous?["model_review"] is JsonObject previousReview
            ? (JsonObject)previousReview.DeepClone()
            : new JsonObject { ["scores"] = null };
        review["stale"] = true;
        root["model_review"] = review;

        if (previous?["revision"] is JsonObject revision)
        {
            root["revision"] = revision.DeepClone();
        }

        root["similarity"] = new JsonObject { ["score"] = null, ["n"] = 0 };

        return root.ToJsonString();
    }

    private static StoryVersionDto ToDto(StoryVersion version, QualityReport report)
    {
        using var content = JsonDocument.Parse(version.Content);
        return new StoryVersionDto(
            version.Id,
            version.Version,
            Wire(version.Source),
            version.ParentVersionId,
            version.FactSheetId,
            version.Title,
            version.WordCount,
            version.Keywords,
            version.CreatedAt,
            content.RootElement.Clone(),
            report.ToHints(),
            StoryEtag.For(version.Id));
    }

    private static string Wire(StoryVersionSource source) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(source.ToString());

    [LoggerMessage(
        EventId = 6010,
        Level = LogLevel.Warning,
        Message = "Story version {StoryVersionId} has no readable audit_report; the edit builds a fresh audit.v1 document.")]
    private static partial void LogAuditReportMissing(ILogger logger, Guid storyVersionId);
}
