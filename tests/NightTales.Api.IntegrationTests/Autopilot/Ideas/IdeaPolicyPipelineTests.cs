using System.Diagnostics.Metrics;
using System.Text.Json;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The content-policy half of 02.8.1-T2 (task Design "Content policy", D12, D19, F31/F33): the real
/// <c>IdeaPolicyChecker</c> inside a real run — the banned-theme backstop, the per-candidate moderator
/// pre-check with its <c>brief</c> evidence row, the true-crime framing flag, the two states of an unavailable
/// moderator, the switch that skips the endpoint altogether, and the D19 promise that none of it is logged.
/// </summary>
/// <remarks>
/// <para>
/// The novelty thresholds, the degradation of the judge and the pick ranking are pinned by
/// <c>IdeaNoveltyPipelineTests</c>; everything asserted here is the pre-check's own contract, read off the
/// stored rows (<c>episode_ideas</c>, <c>moderation_events</c>, <c>usage_records</c>), the judge data object,
/// the host's own log events (<see cref="TestLogSink"/>) and the feature's metric tags
/// (<see cref="MetricTags"/>). Nothing reaches a provider: the text client is a
/// <see cref="FakeTextClient"/> and the moderator a <see cref="FakeContentModerator"/>.
/// </para>
/// <para>
/// The batches are built by <see cref="BatchIdea"/> rather than the harness's shared <see cref="Idea"/> shape:
/// a policy test drives one specific text through one field, and two candidates that share their hook and
/// setting would make the lexical shingle pass read them as the same story before the policy ever runs.
/// </para>
/// </remarks>
public sealed class IdeaPolicyPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// The banned-theme backstop (Design "Content policy" 1): a theme of the series in a candidate's text
    /// rejects that candidate for free — the moderator is never asked about it — and the match is whole-word, so
    /// the same letters inside another word do not reject a candidate.
    /// </summary>
    /// <remarks>
    /// The list carries the acceptance check's Vietnamese theme and the failure table's false-positive probe
    /// (<c>ma</c> inside <c>mama</c>): the second candidate contains both traps and must survive them.
    /// </remarks>
    [Fact]
    public async Task ProposeAndPick_BannedThemeMatch_RejectsCandidate()
    {
        var world = await SeedAsync(Database, autopilotSettings: """{"banned_themes":["tự tử","ma"]}""");
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator();
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                BatchIdea(
                    "Ý tưởng chạm chủ đề cấm",
                    "Một chuyện về chuyện tự tử của người hàng xóm.",
                    "một căn hộ tầng bảy bên sông Hồng"),
                BatchIdea(
                    "Ý tưởng chỉ có hai tiếng rời",
                    "Cô ấy tự nhủ rằng tử tế là điều nên giữ, giữa mama của ngôi nhà cũ.",
                    "một xóm chài ven biển miền Trung")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var banned = IdeaIdOf(Database, world.SeriesId, "Ý tưởng chạm chủ đề cấm");
        var clean = IdeaIdOf(Database, world.SeriesId, "Ý tưởng chỉ có hai tiếng rời");

        IdeaOutcome(Database, banned).ShouldStartWith(
            $"rejected|{IdeaRejectionReason.PolicyBannedTheme}",
            "a theme of the series in the candidate's own text is the free, deterministic rejection");
        pick.Value.IdeaId.ShouldBe(
            clean,
            "the near miss carries the theme's letters inside other words and both token sequences apart");
        IdeaState(Database, clean).ShouldBe("picked|-");
        moderator.Calls.ShouldBe(
            1,
            "the banned-theme check runs before the endpoint, so a candidate it stopped is never sent (task Design \"Content policy\")");
        moderator.Checks[0].Stage.ShouldBe(ModerationStage.Brief);
        DataObjectArrayLength(
                NoveltyCalls(text).ShouldHaveSingleItem(),
                "ideas")
            .ShouldBe(2, "the one judge call of the batch measures the whole batch; only the moderator skips a stopped candidate");
        ModerationRows(Database, world.UserId).ShouldBeEmpty("the banned-theme check buys no endpoint call and writes no evidence row");
    }

    /// <summary>
    /// D12's per-candidate pre-check: a flagged logline/hook rejects the candidate <c>policy_moderated</c> and
    /// writes one <c>moderation_events</c> row with <c>stage = brief</c>, the categories and the flagged text as
    /// its excerpt — cut to the column's 300 characters — while the cleared candidate is picked.
    /// </summary>
    /// <remarks>
    /// The flagged candidate's logline is exactly at its own limit (300 characters), so the excerpt the row keeps
    /// is the logline alone: the first 300 characters of <c>logline + " " + hook</c>, which is the cap the task's
    /// failure table names. The moderated usage row itself (<c>operation = moderation</c>, cost 0) belongs to the
    /// real moderator's own meter, which a harness fake deliberately does not have; what this test pins is the
    /// pipeline's half.
    /// </remarks>
    [Fact]
    public async Task ProposeAndPick_ModeratorFlags_RejectsAndWritesModerationEvent()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Brief, "sexual");
        var longLogline = "Logline dài " + new string('a', 288);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                BatchIdea("Ý tưởng bị gắn cờ", longLogline, "một căn hộ tầng bảy bên sông Hồng"),
                BatchIdea("Ý tưởng được duyệt", "Một chuyện ma nhẹ nhàng về căn nhà bên sông.", "một xóm chài ven biển miền Trung")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var flagged = IdeaIdOf(Database, world.SeriesId, "Ý tưởng bị gắn cờ");
        var cleared = IdeaIdOf(Database, world.SeriesId, "Ý tưởng được duyệt");

        IdeaOutcome(Database, flagged).ShouldStartWith($"rejected|{IdeaRejectionReason.PolicyModerated}");
        pick.Value.IdeaId.ShouldBe(cleared);
        moderator.Calls.ShouldBe(2, "the check is asked once per candidate, logline and hook together");
        moderator.Checks.ShouldAllBe(check => check.Stage == ModerationStage.Brief);

        ModerationRows(Database, world.UserId).ShouldBe(
            "openai_text|brief|true|sexual|false",
            "one row per flagged candidate, at the pre-check's own stage, with the flagged text as its excerpt");
        var excerpt = Excerpt(Database, world.UserId);
        excerpt.ShouldBe(longLogline, "the excerpt is the flagged text, cut to the column's 300 characters");
        excerpt.Length.ShouldBe(300);
        UsageCount(Database, world.UserId).ShouldBe(2, "the run's own rows are its two text calls; the pre-check buys no call");
    }

    /// <summary>
    /// F31/F33's framing flag on the real checker: a dated incident next to a crime noun arms the flag on a
    /// <c>true_crime</c> series and on no other topic, a plain angle of the same series does not arm it, and the
    /// pipeline's pick carries it for a true-crime series whatever the candidate says.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_TrueCrime_SetsRequiresResearch()
    {
        var world = await SeedAsync(Database, Topic.TrueCrime);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(IdeaWithLogline("Góc nhìn chưa kể", "Chuyện về một vụ án chưa có lời giải từ năm 1998.")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1, crimeNouns: ["vụ án"]);
        using var scope = OpenScope(worker, world.UserId);

        var dated = Guid.CreateVersion7();
        var plain = Guid.CreateVersion7();
        var batch = new List<(Guid IdeaId, IdeaCandidate Candidate)>
        {
            (dated, CandidateWithLogline("Góc nhìn có thật", "Chuyện về một vụ án chưa có lời giải từ năm 1998.")),
            (plain, CandidateWithLogline("Góc nhìn thường", "Một chuyện ma trong ngôi nhà bên sông.")),
        };

        var trueCrime = await scope.Policy.CheckAsync(
            batch,
            new IdeaPolicyContext(IdeaPolicyContext.TrueCrimeTopic, []),
            run,
            Ct);

        trueCrime.ByIdeaId[dated].RequiresResearch.ShouldBeTrue("a year within 40 characters of a crime noun arms the flag (F31)");
        trueCrime.ByIdeaId[plain].RequiresResearch.ShouldBeFalse("a plain angle of a true-crime series needs no research");
        trueCrime.ByIdeaId[dated].RejectReason.ShouldBeNull("research is a signal for the story gate, never a rejection");

        var ghost = await scope.Policy.CheckAsync(batch, new IdeaPolicyContext("ghost", []), run, Ct);
        ghost.ByIdeaId.Values.ShouldAllBe(verdict => !verdict.RequiresResearch, "only true_crime arms the framing check");

        var pick = await ProposeAsync(worker, run);
        pick.IsError.ShouldBeFalse();
        pick.Value.RequiresResearch.ShouldBeTrue("the true-crime topic rule stands and this candidate's dated incident trips the framing rule too");
    }

    /// <summary>
    /// The corpus' bound and its source: the judge sees exactly the newest 100 approved episodes of the series,
    /// newest first and numbered 1-based, an episode whose approval was cleared (reopened) is unreachable through
    /// the <c>approved_story_version_id</c> join, and a draft episode never appears at all.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_CorpusIsNewest100ApprovedOnly()
    {
        var world = await SeedAsync(Database);
        var titles = new List<string>();
        for (var number = 1; number <= 102; number++)
        {
            var title = $"Tập cũ số {number:D3}";
            titles.Add(title);
            _ = await SeedApprovedEpisodeAsync(Database, world, title, $"Tóm tắt của {title}.");
        }

        // 102 approved episodes: the newest (Tập cũ số 102) is reopened, so the corpus is the 100 newest of the
        // remaining 101 — the oldest one (số 001) falls outside the bound.
        SqlProbe.Execute(
            Database,
            $"""
             UPDATE story_versions sv
             SET created_at = timestamptz '2026-10-17 09:00:00+00' + (ordered.position * interval '1 minute')
             FROM (
                 SELECT version.id, version.title, row_number() OVER (ORDER BY version.title) AS position
                 FROM story_versions version
                 JOIN episodes e ON e.id = version.episode_id
                 WHERE e.series_id = '{Pg(world.SeriesId)}'
             ) AS ordered
             WHERE sv.id = ordered.id
             """);
        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET approved_story_version_id = NULL WHERE id = (SELECT e.id FROM episodes e JOIN story_versions v ON v.episode_id = e.id WHERE e.series_id = '{Pg(world.SeriesId)}' AND v.title = '{Quote("Tập cũ số 102")}')");
        _ = await SeedEpisodeAsync(Database, world);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng mới")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        var corpus = PastEpisodes(judge);
        corpus.Count.ShouldBe(100, "the corpus is bounded by Autopilot:Ideas:Novelty:CorpusEpisodes");
        corpus.Select(entry => entry.Index).ShouldBe(Enumerable.Range(1, 100), "the judge's indices are 1-based and complete");
        corpus[0].Title.ShouldBe("Tập cũ số 101", "the newest approved episode is the judge's index 1");
        corpus[^1].Title.ShouldBe("Tập cũ số 002", "the bound cuts the oldest approved episode, not the newest");
        corpus.ShouldNotContain(entry => entry.Title == "Tập cũ số 001", "an episode outside the bound is not sent");
        corpus.ShouldNotContain(entry => entry.Title == "Tập cũ số 102", "a reopened episode has left the approved set");
        corpus[0].Summary.ShouldBe("Tóm tắt của Tập cũ số 101.");
        NoveltyCorpusCounts(Database, pick.Value.IdeaId).ShouldBe("100|0", "the stored document reports the corpus it measured");
        IdeaCount(Database, world.SeriesId).ShouldBe(1, "the run itself stored exactly its one candidate");
    }

    /// <summary>
    /// The failure table's shipped mode (<c>FailClosed = false</c>): an unavailable moderator clears the batch
    /// instead of stopping it, the outage is visible on <c>moderation_unavailable_total</c> and in the run's log,
    /// and no evidence row is written — nothing was flagged.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_ModeratorUnavailable_FailOpenClearsTheBatchAndCounts()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator().EnqueueUnavailable(ModerationStage.Brief);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(BatchIdea("Ý tưởng thứ nhất", "Một chuyện về ngọn hải đăng bỏ hoang.", "một hòn đảo nhỏ ngoài khơi"), BatchIdea("Ý tưởng thứ hai", "Một chuyện về con tàu mất tích.", "một cảng cá phía bắc")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.2m)), Usage);
        var sink = new TestLogSink();
        using var tags = new MetricTags();
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator, logs: sink);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse("the shipped mode buys the ideas: an outage is not a refusal");
        IdeaState(Database, pick.Value.IdeaId).ShouldBe("picked|-");
        moderator.Calls.ShouldBe(1, "the endpoint is asked once per batch at most: it just failed");
        ModerationRows(Database, world.UserId).ShouldBeEmpty("nothing was flagged, so there is no evidence to keep");
        tags.Count(IdeaMetrics.ModerationUnavailableTotalName).ShouldBe(1, "the outage is counted whatever the fail mode decides");
        sink.AllText().ShouldContain("idea_policy_moderation_unavailable", "and it is visible in the run's own log");
        sink.AllText().ShouldContain("failClosed=False");
    }

    /// <summary>
    /// The failure table's configured mode (<c>FailClosed = true</c>): a deployment that must not buy ideas
    /// nothing checked rejects every candidate <c>policy_moderated</c>, the run answers
    /// <c>NoIdea(policy_moderated)</c>, and still no evidence row claims a flag that never happened.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_ModeratorUnavailable_FailClosedRejectsTheBatch()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator().EnqueueUnavailable(ModerationStage.Brief);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(BatchIdea("Ý tưởng thứ nhất", "Một chuyện về ngọn hải đăng bỏ hoang.", "một hòn đảo nhỏ ngoài khơi"), BatchIdea("Ý tưởng thứ hai", "Một chuyện về con tàu mất tích.", "một cảng cá phía bắc")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        using var tags = new MetricTags();
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator, failClosed: true);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasNoIdea);
        result.FirstError.Description.ShouldContain(IdeaRejectionReason.PolicyModerated);
        text.Calls(TextStep.Novelty).ShouldBe(1, "the batch's judge call runs before the pre-check, so its measurement is bought either way");
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "the whole batch is stored, rejected or not");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng thứ nhất"))
            .ShouldStartWith($"rejected|{IdeaRejectionReason.PolicyModerated}");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng thứ hai"))
            .ShouldStartWith($"rejected|{IdeaRejectionReason.PolicyModerated}");
        moderator.Calls.ShouldBe(1, "one failed call decides the whole batch, not one per candidate");
        ModerationRows(Database, world.UserId).ShouldBeEmpty("the outage is not a flag: the counter and the log carry it");
        tags.Count(IdeaMetrics.ModerationUnavailableTotalName).ShouldBe(1);
    }

    /// <summary>
    /// <c>ModerationEnabled = false</c> skips the moderator entirely — no call, no usage row of its kind — and
    /// leaves the free half of the pre-check running: a banned theme still rejects its candidate.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_ModerationDisabled_SkipsTheModeratorButKeepsTheBannedThemes()
    {
        var world = await SeedAsync(Database, autopilotSettings: """{"banned_themes":["tự tử"]}""");
        var run = await CreateRunAsync(Database, world);
        var moderator = new FakeContentModerator();
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                BatchIdea("Ý tưởng chạm chủ đề cấm", "Một chuyện về chuyện tự tử của người hàng xóm.", "một căn hộ tầng bảy bên sông Hồng"),
                BatchIdea("Ý tưởng sạch sẽ", "Một chuyện ma nhẹ nhàng về căn nhà bên sông.", "một xóm chài ven biển miền Trung")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, moderator: moderator, moderationEnabled: false);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        moderator.Calls.ShouldBe(0, "the switch is the endpoint's own off-ramp");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng chạm chủ đề cấm"))
            .ShouldStartWith($"rejected|{IdeaRejectionReason.PolicyBannedTheme}", "the free check is not switched off with it");
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, "Ý tưởng sạch sẽ"));
        UsageCount(Database, world.UserId).ShouldBe(2, "the ideas call and the judge call, nothing else");
    }

    /// <summary>
    /// D19: the run's own log events and the feature's metric tags carry ids, counts, scores and closed codes —
    /// never a candidate's title, logline, hook or setting, never an episode's title or summary and never the
    /// judge's reason, all of which the run really handled (the positive controls below prove the sweep is not
    /// vacuous).
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_ContentNeverReachesTheLogsOrTheMetricTags()
    {
        var world = await SeedAsync(Database, autopilotSettings: """{"banned_themes":["ZQX-THEME"]}""");
        _ = await SeedApprovedEpisodeAsync(Database, world, "ZQX-EPISODE-TITLE", "ZQX-EPISODE-SUMMARY");
        var run = await CreateRunAsync(Database, world);
        var sink = new TestLogSink();
        using var tags = new MetricTags();
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                BatchIdea("ZQX-TITLE-KEPT", "ZQX-LOGLINE-KEPT của ý tưởng được chọn.", "ZQX-SETTING-KEPT bên sông", hook: "ZQX-HOOK-KEPT"),
                BatchIdea("ZQX-TITLE-BANNED", "Một chuyện nhắc ZQX-THEME trong đêm.", "ZQX-SETTING-BANNED bên sông", hook: "ZQX-HOOK-BANNED")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(
                JudgeRow(0, 0.1m, reason: "ZQX-JUDGE-REASON gần với một tập đã kể."),
                JudgeRow(1, 0.1m, reason: "ZQX-JUDGE-REASON của ứng viên bị chặn.")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, logs: sink);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var kept = IdeaIdOf(Database, world.SeriesId, "ZQX-TITLE-KEPT");

        // Positive controls: the content was really in flight and really stored, so its absence below means
        // something.
        IdeaOutcome(Database, kept).ShouldStartWith("picked|-");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "ZQX-TITLE-BANNED"))
            .ShouldStartWith($"rejected|{IdeaRejectionReason.PolicyBannedTheme}");
        DataObjectContains(NoveltyCalls(text).ShouldHaveSingleItem(), "ZQX-TITLE-KEPT").ShouldBeTrue("the judge's data object carries the candidate");
        JudgeReason(Database, kept).ShouldContain("ZQX-JUDGE-REASON", "the reason is stored as server-side data of the novelty document");
        var logged = sink.AllText();
        logged.ShouldContain("idea_planner_picked", "the sink observed the run's own log events");

        string[] content =
        [
            "ZQX-TITLE-KEPT", "ZQX-LOGLINE-KEPT", "ZQX-HOOK-KEPT", "ZQX-SETTING-KEPT",
            "ZQX-TITLE-BANNED", "ZQX-LOGLINE-BANNED", "ZQX-HOOK-BANNED", "ZQX-THEME",
            "ZQX-JUDGE-REASON", "ZQX-EPISODE-TITLE", "ZQX-EPISODE-SUMMARY",
        ];
        foreach (var sentinel in content)
        {
            logged.ShouldNotContain(sentinel, Case.Sensitive, $"D19: '{sentinel}' is owner or model content and may never reach a log line");
        }

        tags.Measurements.ShouldNotBeEmpty("the run's own instruments reported, so the tag sweep below is not vacuous");
        tags.Measurements.ShouldContain(measurement =>
            measurement.Instrument == IdeaMetrics.CandidatesTotalName && Tag(measurement, IdeaMetrics.OutcomeTagName) == IdeaMetrics.PickedOutcome);
        tags.Measurements.ShouldContain(measurement =>
            measurement.Instrument == IdeaMetrics.CandidatesTotalName && Tag(measurement, IdeaMetrics.OutcomeTagName) == IdeaRejectionReason.PolicyBannedTheme);
        foreach (var measurement in tags.Measurements)
        {
            foreach (var (key, value) in measurement.Tags)
            {
                if (value is not string tag)
                {
                    continue;
                }

                foreach (var sentinel in content)
                {
                    tag.ShouldNotContain(sentinel, Case.Sensitive, $"D19: the tag {key}={tag} carries content");
                }
            }

            if (measurement.Instrument == IdeaMetrics.CandidatesTotalName)
            {
                ClosedOutcomes.ShouldContain(Tag(measurement, IdeaMetrics.OutcomeTagName)!, "the outcome tag is a closed vocabulary value");
            }

            if (measurement.Instrument == IdeaMetrics.ModerationFlagsTotalName)
            {
                Tag(measurement, IdeaMetrics.StageTagName).ShouldBe(IdeaMetrics.BriefStage);
            }
        }
    }

    /// <summary>
    /// Tenant isolation (09 README D10): a run of another owner's series — or of a series that is not the run's
    /// own — answers <c>not_found</c> before any step is reached, so the other tenant's approved episode is never
    /// read, no judge call is made and nothing is stored for either series.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_AnotherOwnersSeries_StoresNothingAndCallsNothing()
    {
        var world = await SeedAsync(Database);
        var other = await SeedAsync(Database);
        _ = await SeedApprovedEpisodeAsync(Database, other, "Tập riêng của người khác", "Tóm tắt riêng của người khác.");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Không được dùng")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var foreign = await ProposeAsync(worker, run, seriesId: other.SeriesId, asUser: world.UserId);
        var wrongRun = await ProposeAsync(worker, run, seriesId: other.SeriesId, asUser: other.UserId);

        foreign.IsError.ShouldBeTrue();
        foreign.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        wrongRun.FirstError.Code.ShouldBe(ErrorCodes.NotFound, "a run of another series buys nothing");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        IdeaCount(Database, other.SeriesId).ShouldBe(0);
        text.Requests.ShouldBeEmpty("no step of the pipeline is reached before the series is resolved");
        UsageCount(Database, world.UserId).ShouldBe(0);
        ModerationRows(Database, world.UserId).ShouldBeEmpty();
    }

    /// <summary>The rejection reasons a candidate's outcome tag may carry (the closed vocabulary of <c>idea_candidates_total</c>).</summary>
    private static readonly string[] ClosedOutcomes =
    [
        IdeaMetrics.PickedOutcome,
        IdeaRejectionReason.NotBest,
        IdeaRejectionReason.NoveltyLexical,
        IdeaRejectionReason.NoveltyJudge,
        IdeaRejectionReason.PolicyBannedTheme,
        IdeaRejectionReason.PolicyModerated,
        IdeaRejectionReason.VarietyTripleRepeat,
        IdeaRejectionReason.VarietyTemplateRepeat,
        IdeaRejectionReason.VarietyTagCap,
        IdeaRejectionReason.Invalid,
        IdeaRejectionReason.Duplicate,
    ];

    /// <summary>
    /// One <c>ideas.v1</c> candidate whose four texts and setting the caller supplies, so a policy test can put
    /// one exact phrase in one field without the shared shape's repeated hook and setting making the batch look
    /// like one story to the lexical pass.
    /// </summary>
    /// <param name="title">The episode title.</param>
    /// <param name="logline">The one-line premise.</param>
    /// <param name="setting">The setting in words.</param>
    /// <param name="hook">The opening hook; the shared brief's hook by default.</param>
    /// <returns>The wire candidate.</returns>
    private static IdeaV1 BatchIdea(string title, string logline, string setting, string hook = "Đêm ấy có tiếng gõ cửa lúc nửa đêm.") =>
        new(
            title,
            logline,
            hook,
            setting,
            new IdeaStructureV1("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"),
            ["setup", "clue", "twist"]);

    /// <summary>The <c>past_episodes</c> array of the captured judge request, in the judge's own order.</summary>
    /// <param name="request">The captured judge call.</param>
    /// <returns>One entry per element, its 1-based index and its texts.</returns>
    private static List<(int Index, string Title, string Summary)> PastEpisodes(TextCallRequest request)
    {
        using var document = JsonDocument.Parse(request.UserDataJson);

        return
        [
            .. document.RootElement.GetProperty("past_episodes").EnumerateArray().Select(entry => (
                entry.GetProperty("index").GetInt32(),
                entry.GetProperty("title").GetString() ?? string.Empty,
                entry.GetProperty("summary").GetString() ?? string.Empty)),
        ];
    }

    /// <summary>One string tag of a captured measurement, or <see langword="null"/> when the tag is absent.</summary>
    /// <param name="measurement">The captured measurement.</param>
    /// <param name="key">The tag's name.</param>
    /// <returns>The tag's value as text.</returns>
    private static string? Tag((string Instrument, KeyValuePair<string, object?>[] Tags) measurement, string key) =>
        measurement.Tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.Ordinal)).Value as string;

    /// <summary>The excerpt of the owner's single <c>moderation_events</c> row.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="userId">The owner.</param>
    /// <returns>The stored excerpt.</returns>
    private static string Excerpt(TestDatabase database, Guid userId) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT coalesce(excerpt, '') FROM moderation_events WHERE user_id = '{Pg(userId)}' ORDER BY created_at, id LIMIT 1");

    /// <summary>One stored <c>novelty</c> document's judge reason.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The stored reason, or the empty string when the document carries none.</returns>
    private static string JudgeReason(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT coalesce(novelty->'judge'->>'reason', '') FROM episode_ideas WHERE id = '{Pg(ideaId)}'");

    /// <summary>
    /// A <see cref="MeterListener"/> over the idea feature's meter: every measurement's instrument name and tags,
    /// which is how a test reads what the run published without an exporter (09.9.3-T1's pattern).
    /// </summary>
    private sealed class MetricTags : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, KeyValuePair<string, object?>[] Tags)> _measurements = [];

        /// <summary>Starts the listener before the host exists, so no instrument is published unobserved.</summary>
        internal MetricTags()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, IdeaMetrics.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument.Name, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument.Name, tags));
            _listener.Start();
        }

        /// <summary>Every measurement received so far, in order.</summary>
        internal IReadOnlyList<(string Instrument, KeyValuePair<string, object?>[] Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        /// <summary>How many measurements one instrument reported.</summary>
        /// <param name="instrument">The instrument's name.</param>
        /// <returns>The measurement count.</returns>
        internal int Count(string instrument) =>
            Measurements.Count(measurement => string.Equals(measurement.Instrument, instrument, StringComparison.Ordinal));

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();

        private void Record(string instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_measurements)
            {
                _measurements.Add((instrument, tags.ToArray()));
            }
        }
    }
}
