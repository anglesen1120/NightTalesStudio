using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;
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
/// No paid text call: the local check (<see cref="StoryQualityChecker"/>) is pure. Draft moderation runs once for a
/// material edit, unmetered and outside a generation scope. The model review is marked stale; its scores stay (F25).
/// </para>
/// <para>
/// A fresh draft moderation verdict replaces the previous one. On provider failure the previous verdict is carried
/// marked stale for approval to re-check; a missing verdict gets the fail-closed unavailable marker. The similarity
/// scorer and corpus (02.5.6) remain unavailable, so similarity is the unscorable <c>{ score: null, n: 0 }</c>.
/// The fact ids a client may cite are the ones the current version already cites, and
/// <c>fact_sources</c> are carried over from it: the id-to-source mapping lives in the fact sheet (02.5.5) that this
/// base does not have, and a client-sent <c>fact_sources</c> or URL is never read.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="checker">The local quality checker (02.3.4-T1).</param>
/// <param name="nameCorpus">The episode's real-person corpus, read once per save (03 D11, 03.2.2-T2).</param>
/// <param name="moderation">The unmetered D12 draft moderation helper.</param>
/// <param name="prices">The active price version, which carries the D4 length limits.</param>
/// <param name="clock">The command's clock reading.</param>
/// <param name="logger">Logs a current version that has no <c>audit_report</c>.</param>
public sealed partial class EditStoryCommandHandler(
    IAppDbContext db,
    StoryQualityChecker checker,
    INameCorpusProvider nameCorpus,
    StoryDraftModeration moderation,
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

        // The two checker inputs the draft cannot supply itself (03.2.2-T2). The corpus read happens once,
        // after every early exit passed, and is shared by both check sites below; a read failure is not
        // caught here, so a transient DB failure stays the documented 503 (09 D5) instead of the check
        // silently running against an empty corpus (03 D11).
        var names = await nameCorpus.LoadAsync(episode.Id, cancellationToken).ConfigureAwait(false);
        var plan = StoryCheckInputs.PlanFor(command.Content, series.Language, episode.LengthTier);

        // Step 5: a canonically equal story is not saved; the caller gets the current version and the same ETag.
        if (previousContentRead && StoryContentComparer.CanonicalEquals(command.Content, previousContent))
        {
            var unchangedReport = checker.Check(
                command.Content,
                CheckContext(
                    series,
                    episode.LengthTier,
                    rules,
                    hasUrlSources,
                    previousAudit,
                    ReviewIsStale(previousAudit),
                    plan,
                    names));
            return ToDto(previous, unchangedReport);
        }

        // Step 6: recompute every derived and local field on the new copy.
        if (previousAudit is null)
        {
            LogAuditReportMissing(logger, previous.Id);
        }

        var derived = StoryVersion.Derive(command.Content);
        var freshModeration = await moderation.CheckAsync(
            episode.UserId,
            episode.Id,
            command.Content.Title,
            derived.Body,
            command.Content.Description,
            command.Content.Tags ?? [],
            cancellationToken).ConfigureAwait(false);
        var checkedAudit = new JsonObject();
        checkedAudit["moderation"] = freshModeration
            ?? (previousAudit?["moderation"] is JsonObject kept
                ? kept.DeepClone()
                : new JsonObject { ["stale"] = true, ["unavailable"] = true });
        if (freshModeration is null)
        {
            checkedAudit["moderation"]!["stale"] = true;
        }

        var report = checker.Check(
            command.Content,
            CheckContext(
                series,
                episode.LengthTier,
                rules,
                hasUrlSources,
                checkedAudit,
                modelReviewStale: true,
                plan,
                names));
        var contentJson = StoryContentJson.Write(command.Content, StoryContentJson.ReadFactSources(previous.Content));
        var auditJson = BuildAuditReport(previousAudit, report, (JsonObject)checkedAudit["moderation"]!);

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
        bool modelReviewStale,
        RenderImagePlan? plan,
        NameCorpus names)
    {
        var categories = ModerationCategoriesOf(previousAudit);
        return new(
            series.Topic,
            tier,
            rules.Length,
            rules,
            rules.AllowedFactRefs.Count,
            [],
            hasUrlSources,
            null,
            SimilarityVerdict.Pass,
            categories,
            modelReviewStale,
            plan,
            names,
            categories.Contains(StoryQualityChecker.UnspecifiedModerationCategory));
    }

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

    private static List<string> ModerationCategoriesOf(JsonObject? audit)
    {
        var blockers = StoryDraftModeration.BlockingCategoriesOf(audit?["moderation"]);
        if (blockers.Contains(StoryQualityChecker.UnspecifiedModerationCategory))
        {
            return blockers.ToList();
        }

        // BlockingCategoriesOf has validated every flag and category, including contradictory empty blockers.
        return ((JsonArray)audit!["moderation"]!["categories"]!)
            .Select(node => node!.GetValue<string>())
            .ToList();
    }

    /// <summary>
    /// The new <c>audit.v1</c> document (02 README D22): <c>local</c> recomputed, <c>model_review</c> kept with
    /// <c>stale = true</c>, the checked <c>moderation</c> verdict kept, <c>revision</c> carried over and
    /// <c>similarity</c> reset to the unscorable shape.
    /// </summary>
    private static string BuildAuditReport(JsonObject? previous, QualityReport report, JsonObject moderationVerdict)
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

        root["moderation"] = moderationVerdict.DeepClone();

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
