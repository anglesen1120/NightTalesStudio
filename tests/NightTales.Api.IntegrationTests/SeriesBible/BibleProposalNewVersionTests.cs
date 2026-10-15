using System.Globalization;
using System.Text.Json.Nodes;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The idempotency key of a bible proposal names the episode's approved story version (plan §7.1 step 2), so a
/// revised version of an already-proposed episode is a new approval with its own run, while a redelivery of that
/// same version is still a replay (02.6.3-T2 failure table, C7).
/// </summary>
public sealed class BibleProposalNewVersionTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// A story is revised after its bible proposal ran: the second approval carries the new version id, so it queues
    /// a second run and a second paid call; delivering that same approval again adds nothing.
    /// </summary>
    [Fact]
    public async Task StoryApproved_NewVersionOfTheSameEpisode_QueuesASecondRun_AndItsReplayAddsNothing()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient()
            .EnqueueOk(TextStep.Bible, Draft(Character("Bà Hoa", "Người gác bến giữ ngọn đèn dầu.")), Usage)
            .EnqueueOk(TextStep.Bible, Draft(Character("Ông Tư", "Thợ sửa đồng hồ ở đầu ngõ.")), Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.DeliverAndRunAsync(worker);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(1);

        // The author revised the story: the same episode is approved again under a new version id.
        var revised = await SeedRevisedVersionAsync(Database, world, episode);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, revised, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(
            2,
            "a new story version is a new approval, so its idempotency key does not match the first run's");
        text.Requests.Count.ShouldBe(2, "the second run makes its own paid call");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM generation_runs WHERE idempotency_key = 'bible:{BibleProposalJobHarness.Pg(episode.Id)}:{BibleProposalJobHarness.Pg(revised.VersionId)}'")
            .ShouldBe(1);

        // The revised approval is redelivered by the at-least-once outbox: it is the same approval, so nothing is
        // queued and nothing is paid for.
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, revised, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(2);
        text.Requests.Count.ShouldBe(2);
    }

    /// <summary>
    /// Seeds the next version of an approved episode: a story version row of number 2 that becomes the episode's
    /// approved version, the way an approval writes it.
    /// </summary>
    /// <returns>The episode with the new version id.</returns>
    private static async Task<BibleProposalJobHarness.ApprovedEpisode> SeedRevisedVersionAsync(
        TestDatabase database,
        World world,
        BibleProposalJobHarness.ApprovedEpisode episode)
    {
        var actor = new TestCurrentUser { Id = world.UserId };
        var draft = StoryDraft();
        var sources = new JsonArray(new JsonObject
        {
            ["source_id"] = "11111111-1111-7111-8111-111111111111",
            ["title"] = "Stored source",
            ["url"] = "https://example.com/stored-source",
        });
        var at = SeriesBibleHarness.Start.AddMinutes(5);
        var version = StoryVersion.Create(
            world.UserId,
            episode.Id,
            2,
            StoryVersionSource.Generated,
            draft,
            StoryContentJson.Write(draft, sources),
            "model-x",
            "rel=1",
            null,
            null,
            null,
            AuditJson(),
            0.021m,
            at);

        await using (var context = database.CreateAppDbContext(actor, new FakeClock(at)))
        {
            context.StoryVersions.Add(version);
            await context.SaveChangesAsync(CancellationToken.None);
        }

        SqlProbe.Execute(
            database,
            $"UPDATE episodes SET current_story_version_id = '{BibleProposalJobHarness.Pg(version.Id)}', approved_story_version_id = '{BibleProposalJobHarness.Pg(version.Id)}', approved_at = '{at:O}' WHERE id = '{BibleProposalJobHarness.Pg(episode.Id)}'");

        return new BibleProposalJobHarness.ApprovedEpisode(episode.Id, version.Id);
    }

    /// <summary>The valid Short/vi draft of the story routes, as the harness seeds it.</summary>
    private static StoryContentV1 StoryDraft()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Căn nhà thuê rẻ bất thường, chủ nhà chỉ dặn đừng mở cánh cửa cuối hành lang.", ["empty chair in the hallway"], [], "setup"),
            new("Tiếng gõ vang lên từ trong đó, và khi anh mở ra thì thấy chiếc ghế cũ vẫn còn ấm.", ["rain on the window"], [], "twist"),
            new("Anh kể lại chuyện ấy cho người hàng xóm, và người ấy chỉ im lặng nhìn ra sân.", ["figure at the door"], [], "reveal"),
        ];
        var hook = new StorySegmentV1(
            "Đêm ấy mưa rất to, người kể ngồi một mình trong căn nhà cũ ven sông.",
            ["old house by the river", "stormy night over the river"],
            []);
        var ending = new StorySegmentV1(
            "Anh trả nhà ngay hôm sau, không dám kể cho ai nghe chuyện mình đã thấy.",
            ["letter on the table"],
            []);

        return new StoryContentV1(
            "Căn nhà cuối hành lang",
            hook,
            paragraphs,
            ending,
            "Một chuyện ma nhẹ nhàng về căn nhà bên sông.",
            ["truyện ma", "tâm linh"],
            ["supernatural"],
            new StoryStructureV1("truyện ma tâm linh", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"));
    }

    /// <summary>The audit document a stored version carries (the story routes' own fixture).</summary>
    private static string AuditJson() =>
        """
        {"schema_version":"audit.v1","local":{"issues":[
          {"rule":"Q-REP-02","severity":"advisory","kind":"taste","segment":"body","evidence":"repeated phrase","repair_direction":"vary phrasing"},
          {"rule":"Q-KW-01","severity":"blocking","kind":"objective","segment":"p:1","message_key":"quality.keyword_count_per_segment","evidence":"0 keywords","repair_direction":"add 1-2 keywords","measured":0,"threshold":1}]},
         "moderation":{"flagged":false,"categories":[],"stale":false},
         "model_review":{"scores":{"hook":4,"coherence":3},"issues":[
           {"id":"M1","severity":"blocking","type":"ear_rule","segment":"p:1","evidence":"test evidence","repair_direction":"simplify"},
           {"id":"M2","severity":"advisory","type":"ear_rule","segment":"p:2","evidence":"test evidence","repair_direction":"shorten"}],"stale":true},
         "revision":{"rounds":1},"similarity":{"score":0.021}}
        """;

    /// <summary>The bible draft the scripted paid call answers with.</summary>
    private static BibleProposalV1 Draft(params BibleProposalOpV1[] ops) => new(ops);

    /// <summary>An <c>add_character</c> candidate.</summary>
    private static BibleProposalOpV1 Character(string name, string description) =>
        new("add_character", null, name, description, null, "vì sao");

    /// <summary>The usage one scripted call reports, so the metered row prices real tokens.</summary>
    private static TokenUsage Usage { get; } = new(1_200, 0, 0, 300, 0);
}
