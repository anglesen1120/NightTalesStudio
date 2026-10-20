using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// QA probes of 02.8.2-T2 over the real host, written by the QA agent and not by the implementer: the shipped
/// configuration path (<c>Autopilot:Gates:Profiles:*:Moderation/RealWorld</c> as the two <c>appsettings.json</c>
/// files carry it), the independence of check 6 from check 7, the revised-text detection that closes STATUS
/// <c>S67-1</c>, the fail-closed style-template path, the 300-character excerpt cap on an over-cap chunk, the
/// single moderation call of a live pass (STATUS <c>S67-2</c>) and the mode-independence of a non-content check.
/// </summary>
public sealed class ContentGateQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string NoticeType = "autopilot_owner_action_required.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Both host settings files carry the plan §3.3 <c>default</c> moderation block and the real-world switches, so
    /// a deployment that copies either file configures the same gate the code defaults describe.
    /// </summary>
    /// <param name="relativePath">The settings file, relative to the repository root.</param>
    [Theory]
    [InlineData("src/NightTales.Api/appsettings.json")]
    [InlineData("src/NightTales.Worker/appsettings.json")]
    public void ContentGateQa_AppSettingsFiles_CarryThePlanModerationAndRealWorldBlock(string relativePath)
    {
        var file = Path.Combine(RepositoryRoot(), relativePath);
        File.Exists(file).ShouldBeTrue($"{file} must exist");

        var defaultProfile = JsonNode.Parse(
            File.ReadAllText(file),
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!["Autopilot"]!["Gates"]!["Profiles"]!["default"];

        defaultProfile.ShouldNotBeNull($"{relativePath} must configure the default gate profile");

        var moderation = defaultProfile!["Moderation"]!;
        moderation["BlockedCategories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(
            ["sexual/minors", "self-harm/instructions", "illicit/violent", "hate/threatening"]);

        var scores = moderation["MaxScore"]!.AsObject();
        scores["harassment"]!.GetValue<double>().ShouldBe(0.5);
        scores["hate"]!.GetValue<double>().ShouldBe(0.3);
        scores["illicit"]!.GetValue<double>().ShouldBe(0.5);
        scores["self-harm"]!.GetValue<double>().ShouldBe(0.3);
        scores["sexual"]!.GetValue<double>().ShouldBe(0.5);
        scores["violence"]!.GetValue<double>().ShouldBe(0.6);
        scores["violence/graphic"]!.GetValue<double>().ShouldBe(0.3);

        var realWorld = defaultProfile["RealWorld"]!;
        realWorld["RequireIncidentCoOccurrence"]!.GetValue<bool>().ShouldBeTrue("a date alone is never a signal");
        realWorld["IncidentWords"]!.AsArray().Count.ShouldBe(23, "the twelve vi and eleven en words of the task file");
    }

    /// <summary>
    /// The Api host binds that block through the production options path: the resolved <c>default</c> profile is the
    /// plan's numbers, <c>strict</c> still resolves and inherits the content-gate half, and the shipped validator
    /// accepts the file.
    /// </summary>
    [Fact]
    public void ContentGateQa_ApiHost_ResolvesThePlanModerationAndRealWorldProfile()
    {
        using var factory = new ApiFactory(Database, new FakeClock(GateRunSeed.Start));

        var options = factory.Services.GetRequiredService<IOptionsMonitor<AutopilotGateOptions>>().CurrentValue;
        options.DryRun.ShouldBeTrue("the shipped gate records only until the owner flips the key");

        var resolved = options.Resolve(profileRef: null);

        resolved.Moderation.BlockedCategories.ShouldBe(
            ["sexual/minors", "self-harm/instructions", "illicit/violent", "hate/threatening"]);
        resolved.Moderation.MaxScore.Count.ShouldBe(7);
        resolved.Moderation.MaxScore["violence"].ShouldBe(0.6);
        resolved.Moderation.MaxScore["violence/graphic"].ShouldBe(0.3);
        resolved.Moderation.MaxScore["hate"].ShouldBe(0.3);
        resolved.RealWorld.RequireIncidentCoOccurrence.ShouldBeTrue();
        resolved.RealWorld.IncidentWords.Count.ShouldBe(23);
        resolved.RealWorld.IncidentWords.ShouldContain("vụ án");
        resolved.RealWorld.IncidentWords.ShouldContain("murder case");

        var strict = options.Resolve("strict");
        strict.Story.MinMeanReviewScore.ShouldBe(4.0m, "strict still overlays its own story keys");

        // ModerationThresholds is a record over a list and a dictionary, which the record's generated Equals compares
        // by reference: two Resolve() calls build equal-but-distinct instances, so the contents are what must match.
        strict.Moderation.BlockedCategories.ShouldBe(
            resolved.Moderation.BlockedCategories,
            "strict names no Moderation block, so it inherits the default one rather than losing it");
        strict.Moderation.MaxScore.Count.ShouldBe(resolved.Moderation.MaxScore.Count);
        foreach (var (category, ceiling) in resolved.Moderation.MaxScore)
        {
            strict.Moderation.MaxScore[category].ShouldBe(ceiling, $"strict inherits the default ceiling of {category}");
        }

        strict.RealWorld.IncidentWords.ShouldBe(resolved.RealWorld.IncidentWords);

        AutopilotGateOptionsValidator.Validate(options).ShouldBeEmpty("the shipped file must pass the start-up gate");
    }

    /// <summary>
    /// Check 6 and check 7 read different evidence: a stored draft verdict that is flagged fails check 6 while the
    /// content gate still passes the clean current content, and the failed list names only check 6.
    /// </summary>
    [Fact]
    public async Task ContentGateQa_FlaggedStoredVerdict_FailsCheckSix_WhileCheckSevenPassesCleanContent()
    {
        var world = await GateRunSeed.SeedRunAsync(
            Database,
            auditReport: GateRunSeed.AuditReport(
                GateRunSeed.ReviewJson(4),
                """{"flagged":true,"categories":["hate"],"blocking":[],"stale":false,"unavailable":false}"""),
            ct: Ct);
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeFalse();
        evaluated.Value.Result.Failures.ShouldBe([StoryGateCheckIds.ModerationDraft]);
        CheckPass(world.RunId, StoryGateCheckIds.ModerationDraft).ShouldBeFalse();
        CheckPass(world.RunId, StoryGateCheckIds.ModerationGate).ShouldBeTrue(
            "check 7 never reads the stored block, so a flagged draft verdict does not taint the fresh check");
        moderator.Calls.ShouldBe(1, "check 7 runs the content gate on the current content regardless of check 6");
        StoredStatus(world.EpisodeId).ShouldBe("story_review");
        AutoApproveAuditRows(world).ShouldBe(0);
    }

    /// <summary>
    /// The reverse direction: a clean stored draft verdict passes check 6 and does not stand in for check 7 — the
    /// content gate still runs, and a flagged scene prompt fails the run.
    /// </summary>
    [Fact]
    public async Task ContentGateQa_CleanStoredVerdict_CheckSevenStillRunsAndFailsOnAFlaggedScenePrompt()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var scenePrompt = GateRunSeed.StyleTemplateText(Database, world.SeriesId)
            .Replace("{term}", GateRunSeed.HarbourKeyword, StringComparison.Ordinal);
        var moderator = new FakeContentModerator().EnqueueFlaggedWhen(
            ModerationStage.AutopilotGate,
            texts => texts.Any(text => text.Contains(scenePrompt, StringComparison.Ordinal)),
            "sexual");
        using var factory = Factory(moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        CheckPass(world.RunId, StoryGateCheckIds.ModerationDraft).ShouldBeTrue("the stored verdict is clean");
        CheckPass(world.RunId, StoryGateCheckIds.ModerationGate).ShouldBeFalse();
        evaluated.Value.Result.Failures.ShouldBe([StoryGateCheckIds.ModerationGate]);
        moderator.Calls.ShouldBeGreaterThan(1, "the union call plus the attribution pass that narrows the failing text");
        EventExcerpts(world.EpisodeId).ShouldBe([scenePrompt]);
    }

    /// <summary>
    /// STATUS <c>S67-1</c>: a framing phrase that the revision introduced is detected on the revised text. The stored
    /// review of the revision is all 5s, so every non-content check passes and check 8 is the only failure.
    /// </summary>
    [Fact]
    public async Task ContentGateQa_RevisedStory_FramingPhraseInTheRevisedText_FailsCheckEight()
    {
        var world = await GateRunSeed.SeedRunAsync(
            Database,
            auditReport: GateRunSeed.RevisedAudit(),
            draft: GateRunSeed.RealWorldDraft(),
            ct: Ct);
        using var factory = Factory();

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Failures.ShouldBe(
            [StoryGateCheckIds.RealWorldSignal],
            "the revised text is what the detector reads, so the revision cannot hide a new phrase");
        RealWorldBlock(world.RunId).ShouldContain("\"hit\": true");
        RealWorldBlock(world.RunId).ShouldContain("framing_lexicon");
        RealWorldBlock(world.RunId).ShouldContain("p:1");
        StoredStatus(world.EpisodeId).ShouldBe("story_review");
        AutoApproveAuditRows(world).ShouldBe(0);
    }

    /// <summary>
    /// The failure table's "style template is gone" row: check 7 fails closed with <c>field_absent</c> and the
    /// moderator is never called, because there is nothing honest to moderate. The template row and the series
    /// foreign key are restored afterwards, so the rest of the class runs on an intact schema.
    /// </summary>
    [Fact]
    public async Task ContentGateQa_MissingStyleTemplate_FailsCheckSevenFieldAbsentWithoutACall()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var moderator = new FakeContentModerator();

        var definition = await RemoveStyleTemplateAsync(world.SeriesId, Ct);
        ErrorOr<StoryGateOutcome> evaluated;
        try
        {
            using var factory = Factory(moderator);
            evaluated = await EvaluateAsync(factory, world);
        }
        finally
        {
            await RestoreStyleTemplateAsync(definition, Ct);
        }

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Failures.ShouldBe([StoryGateCheckIds.ModerationGate]);
        CheckDetail(world.RunId, StoryGateCheckIds.ModerationGate).ShouldBe(GateDetailCodes.FieldAbsent);
        moderator.Calls.ShouldBe(0, "no scene prompt can be expanded without the template, so nothing is sent");
        StoredStatus(world.EpisodeId).ShouldBe("story_review");
    }

    /// <summary>
    /// A flagged paragraph longer than the cap still stores at most 300 characters of excerpt (D12), and the excerpt
    /// is the head of that paragraph — the domain's cut, reached through the gate's own pre-split. The overlapped
    /// pieces the pre-split produces are one attribution unit (the same segment and category set), so the run writes
    /// exactly one evidence row rather than one per piece (P2: overlapped pieces are not separate findings).
    /// </summary>
    [Fact]
    public async Task ContentGateQa_LongFlaggedParagraph_StoresAtMost300CharsPerExcerpt()
    {
        var longParagraph = string.Concat(
            Enumerable.Repeat("the lantern keeper walked along the quiet pier ", 250));
        longParagraph.Length.ShouldBeGreaterThan(8_000, "the paragraph must exceed the moderator cap");

        var draft = new StoryContentV1(
            "The lantern on the pier",
            new StorySegmentV1("A quiet hook about the pier.", ["quiet night by the harbour"], [], null),
            [new StorySegmentV1(longParagraph, ["old wooden pier at dawn"], [], "setup")],
            new StorySegmentV1("A quiet ending.", ["cold morning by the water"], [], null),
            "A quiet story about a forgotten lantern.",
            ["supernatural"],
            [],
            new StoryStructureV1("ghost", "pier", "a light", "the keeper", "first_person"));
        var world = await GateRunSeed.SeedRunAsync(Database, draft: draft, ct: Ct);

        // Below the cap the gate never splits, and the 200-character overlap keeps the second piece over a fifth of
        // the cap: both pieces are flagged, so the single evidence row can only come from the (segment set,
        // categories) de-duplication of `WriteEvidenceAsync`. A threshold near the piece length would leave the
        // second piece clean and make this row a one-failure coincidence instead of the P2 fix under test.
        var moderator = new FakeContentModerator().EnqueueFlaggedWhen(
            ModerationStage.AutopilotGate,
            texts => texts.Any(text => text.Length > 1_000),
            "violence");
        using var factory = Factory(moderator);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Failures.ShouldContain(StoryGateCheckIds.ModerationGate);

        moderator.Checks
            .Count(check => check.Texts.Count == 1 && check.Texts[0].Length > 1_000)
            .ShouldBe(2, "both overlapped pieces were re-checked and both failed, so the de-duplication wrote one row");

        var excerpts = EventExcerpts(world.EpisodeId);
        excerpts.Count.ShouldBe(
            1,
            "one row per failing attribution unit: the two overlapped pieces share the segment and the category set, "
            + "and WriteEvidenceAsync de-duplicates that pair");
        excerpts.ShouldAllBe(excerpt => excerpt.Length == ModerationEvent.ExcerptMaxLength);
        excerpts.ShouldAllBe(excerpt => longParagraph.Contains(excerpt, StringComparison.Ordinal));
        ExcerptRowCount(world.EpisodeId).ShouldBe(
            1,
            "overlapped pieces of the same segment and category set are one evidence row");
    }

    /// <summary>
    /// STATUS <c>S67-2</c>: a live pass moderates the content once (the content gate's batch call) and never reaches
    /// the approve command's draft-stage refresh, because check 6 could only pass on a usable stored verdict and
    /// <c>StoryDraftModeration.NeedsRecheck</c> is then false.
    /// </summary>
    [Fact]
    public async Task ContentGateQa_LivePass_ModeratesOnceAndNeverRefreshesTheDraftVerdict()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, ct: Ct);
        var moderator = new FakeContentModerator();
        using var factory = Factory(moderator, dryRun: false);

        var evaluated = await EvaluateAsync(factory, world);

        evaluated.IsError.ShouldBeFalse(Describe(evaluated));
        evaluated.Value.Result.Pass.ShouldBeTrue();
        evaluated.Value.AutoApproved.ShouldBeTrue();
        StoredStatus(world.EpisodeId).ShouldBe("story_approved");

        moderator.Calls.ShouldBe(1, "a live pass is exactly one content-gate batch call");
        moderator.Checks.ShouldAllBe(check => check.Stage == ModerationStage.AutopilotGate);
        moderator.Checks.ShouldNotContain(
            check => check.Stage == ModerationStage.Draft,
            "the approve command's stage 7 re-moderates only when the stored verdict needs a recheck");
    }

    /// <summary>
    /// Dry run and live mode do not change what a non-content check decides: the same failing review fails check 2
    /// in both modes, with the same failing id list, and nothing is approved either way.
    /// </summary>
    [Fact]
    public async Task ContentGateQa_DryRunAndLive_EvaluateTheSameNonContentChecks()
    {
        var world = await GateRunSeed.SeedRunAsync(Database, auditReport: GateRunSeed.FailingAudit(), ct: Ct);

        using (var dryFactory = Factory(new FakeContentModerator(), dryRun: true))
        {
            var dry = await EvaluateAsync(dryFactory, world, attempt: 1);

            dry.IsError.ShouldBeFalse(Describe(dry));
            dry.Value.Result.Pass.ShouldBeFalse();
            dry.Value.Result.Failures.ShouldBe([StoryGateCheckIds.SelfReviewMeanScore]);
            dry.Value.AutoApproved.ShouldBeFalse();

            using var liveFactory = Factory(new FakeContentModerator(), dryRun: false);
            var live = await EvaluateAsync(liveFactory, world, attempt: 2);

            live.IsError.ShouldBeFalse(Describe(live));
            live.Value.Result.Pass.ShouldBeFalse();
            live.Value.Result.Failures.ShouldBe(dry.Value.Result.Failures);
            live.Value.Result.Checks.Count.ShouldBe(dry.Value.Result.Checks.Count);
            live.Value.Result.Checks.Select(check => (check.Id, check.Pass)).ShouldBe(
                dry.Value.Result.Checks.Select(check => (check.Id, check.Pass)),
                "the mode decides only whether a pass is acted on, never what a check measures");
            live.Value.AutoApproved.ShouldBeFalse();
        }

        // The mode is a key of the gate_results document itself (GateResultWriter.ModeKeyName), not of its story block.
        SqlProbe.ScalarText(Database, $"SELECT gate_results ->> 'mode' FROM autopilot_runs WHERE id = '{world.RunId}'")
            .ShouldBe("live");
        StoredStatus(world.EpisodeId).ShouldBe("story_review");
    }

    private static string Describe(ErrorOr<StoryGateOutcome> evaluated) =>
        evaluated.IsError ? string.Join("; ", evaluated.Errors.Select(error => $"{error.Code}: {error.Description}")) : "ok";

    /// <summary>Walks up from the test binary to the repository root that owns <c>src/</c>.</summary>
    /// <returns>The repository root.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "NightTales.Api", "appsettings.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"The repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    private static async Task<ErrorOr<StoryGateOutcome>> EvaluateAsync(
        ApiFactory factory,
        GateRunSeed.GateWorld world,
        int attempt = 1)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(world.UserId);
        var gate = scope.ServiceProvider.GetRequiredService<IAutoApprovalGate>();

        return await gate.EvaluateStoryAsync(world.RunId, attempt, Ct);
    }

    private ApiFactory Factory(FakeContentModerator? moderator = null, bool dryRun = true)
    {
        var factory = new ApiFactory(Database, new FakeClock(GateRunSeed.Start))
            .WithSetting("Autopilot:Gates:DryRun", dryRun ? "true" : "false")
            .WithSetting("Similarity:Corpus:MaxPastEpisodes", "50")
            .WithServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddScoped<IEmailSender>(provider => new FakeEmailSender(
                    new FakeEmailSenderState(),
                    (AppDbContext)provider.GetRequiredService<IAppDbContext>(),
                    provider.GetRequiredService<IClock>()));

                if (moderator is not null)
                {
                    // Registered under the port, not under FakeContentModerator: ApiFactory already replaces the
                    // production adapter with its own fake (ApiFactory.cs), so the removal would otherwise leave
                    // IContentModerator unresolvable and the host would fail its own build validation.
                    services.RemoveAll<IContentModerator>();
                    services.AddSingleton<IContentModerator>(moderator);
                }
            });

        return factory;
    }

    private bool CheckPass(Guid runId, string checkId) =>
        string.Equals(
            SqlProbe.ScalarText(
                Database,
                "SELECT entry ->> 'pass' FROM autopilot_runs, jsonb_array_elements(gate_results -> 'story' -> 'checks') AS entry "
                + $"WHERE id = '{runId}' AND entry ->> 'id' = '{checkId}'"),
            "true",
            StringComparison.Ordinal);

    private string CheckDetail(Guid runId, string checkId) =>
        SqlProbe.ScalarText(
            Database,
            "SELECT coalesce(entry ->> 'detail', '') FROM autopilot_runs, jsonb_array_elements(gate_results -> 'story' -> 'checks') AS entry "
            + $"WHERE id = '{runId}' AND entry ->> 'id' = '{checkId}'");

    private string RealWorldBlock(Guid runId) =>
        SqlProbe.ScalarText(Database, $"SELECT (gate_results -> 'real_world')::text FROM autopilot_runs WHERE id = '{runId}'");

    private string StoredStatus(Guid episodeId) =>
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id = '{episodeId}'");

    private List<string> EventExcerpts(Guid episodeId) =>
        SqlProbe.QueryStrings(
            Database,
            $"SELECT coalesce(excerpt, '') FROM moderation_events WHERE episode_id = '{episodeId}' ORDER BY created_at");

    private long ExcerptRowCount(Guid episodeId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM moderation_events WHERE episode_id = '{episodeId}'");

    private long AutoApproveAuditRows(GateRunSeed.GateWorld world) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{AutopilotApprovalService.StoryApprovedAction}' "
            + $"AND subject_user_id = '{world.UserId}'");

    /// <summary>
    /// Removes the series' style template row so the evaluator's <c>style is null</c> branch can be exercised.
    /// The row is copied to a plain table first (the foreign key is dropped for the duration), and the function
    /// returns the constraint's own definition so the caller can put the schema back exactly as it was.
    /// </summary>
    /// <param name="seriesId">The series whose template is removed.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The foreign key's <c>pg_get_constraintdef</c> text.</returns>
    private async Task<string> RemoveStyleTemplateAsync(Guid seriesId, CancellationToken ct)
    {
        const string constraint = "fk_series_style_templates_style_template_id";
        var definition = SqlProbe.ScalarText(
            Database,
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint "
            + $"WHERE conname = '{constraint}' AND conrelid = 'series'::regclass");
        definition.ShouldNotBeNullOrWhiteSpace();

        await Database.ExecuteAsync("DROP TABLE IF EXISTS qa_saved_style_template", ct);
        await Database.ExecuteAsync(
            "CREATE TABLE qa_saved_style_template AS SELECT * FROM style_templates "
            + $"WHERE id = (SELECT style_template_id FROM series WHERE id = '{seriesId}')",
            ct);
        SqlProbe.Count(Database, "SELECT count(*) FROM qa_saved_style_template")
            .ShouldBe(1, "the series must have a style template row to remove");

        await Database.ExecuteAsync($"ALTER TABLE series DROP CONSTRAINT {constraint}", ct);
        await Database.ExecuteAsync(
            "DELETE FROM style_templates WHERE id = (SELECT style_template_id FROM series WHERE id = '" + seriesId + "')",
            ct);

        return definition;
    }

    /// <summary>Puts the saved template row and the foreign key back.</summary>
    /// <param name="definition">The definition <see cref="RemoveStyleTemplateAsync"/> read.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>A task that completes when the schema is whole again.</returns>
    private async Task RestoreStyleTemplateAsync(string definition, CancellationToken ct)
    {
        await Database.ExecuteAsync("INSERT INTO style_templates SELECT * FROM qa_saved_style_template", ct);
        await Database.ExecuteAsync(
            "ALTER TABLE series ADD CONSTRAINT fk_series_style_templates_style_template_id " + definition,
            ct);
        await Database.ExecuteAsync("DROP TABLE qa_saved_style_template", ct);
    }
}
