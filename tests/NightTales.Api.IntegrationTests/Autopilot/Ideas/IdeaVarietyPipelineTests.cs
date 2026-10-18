using System.Diagnostics.Metrics;
using System.Globalization;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fixtures;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;
using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaVarietyVocabulary;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The variety guard's half of the idea pipeline (02.8.4-T1): the block of a repeated template and of a repeated
/// tag triple, the rotation and the cap the run steers the writer with, the soft penalty that lowers a candidate's
/// score without rejecting it, the <c>Autopilot:Variety:Enabled</c> switch, the cold-series floor and the
/// vocabulary mapping of the stored tags (IDEA-S52-1).
/// </summary>
/// <remarks>
/// <para>
/// Every test drives the production graph with only the provider edge scripted, exactly as
/// <see cref="IdeaNoveltyPipelineTests"/> does, and the history is seeded through
/// <see cref="SeedStructuredHistoryAsync"/>: real approved episodes whose <c>story.v1</c> content the guard's own
/// reader derives the tags and the beat signature from. Nothing here re-implements the guard — its rules are
/// pinned by the Domain unit tests — these tests pin what a run <i>does</i> with the guard's answer: which rows
/// are stored, what the prompt is steered with, and how many paid calls happened.
/// </para>
/// <para>
/// The expectations of the steering blocks are hand-computed from the ten-episode windows below (counts, the
/// <c>(count + 1) / (window + 1)</c> cap and the least-used rotation value), so a run that steers with something
/// else fails here rather than only in the Domain tests.
/// </para>
/// </remarks>
public sealed class IdeaVarietyPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// The steering block the ten-episode windows below have to produce: the rotation's least-used value per
    /// dimension, with the two dimensions that have none absent (the data object omits a null member).
    /// </summary>
    private const string ExpectedRequired = $"setting={CanHoThue}|twist_type={LoiNguyen}";

    /// <summary>
    /// The forbidden block of the same window: every value the <c>(count + 1) / 11</c> cap is over, members in
    /// ordinal order and the values of a dimension in ordinal order as well.
    /// </summary>
    private const string ExpectedForbidden =
        $"opening={MotLoiKe},{TimLaiDoVat}|setting={BenhVienBoHoang}|sub_genre={MaHocDuong}|twist_type={TrungHop}";

    /// <summary>
    /// The ten approved episodes the steering tests read, newest first: one <c>sub_genre</c> (10 of 10), one
    /// <c>setting</c> at 8 and one at 2, one <c>opening</c> at 4 and one at 6, one <c>twist_type</c> at 8 and one
    /// at 2. Every value is a vocabulary term, so nothing is <c>other:</c> and nothing is skipped by the rotation.
    /// </summary>
    private static readonly (string SubGenre, string Setting, string Opening, string Twist)[] TenEpisodeWindow =
    [
        (MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop),
        (MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop),
        (MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop),
        (MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop),
        (MaHocDuong, BenhVienBoHoang, TimLaiDoVat, TrungHop),
        (MaHocDuong, BenhVienBoHoang, TimLaiDoVat, TrungHop),
        (MaHocDuong, BenhVienBoHoang, TimLaiDoVat, TrungHop),
        (MaHocDuong, BenhVienBoHoang, TimLaiDoVat, TrungHop),
        (MaHocDuong, CanHoThue, TimLaiDoVat, LoiNguyen),
        (MaHocDuong, CanHoThue, TimLaiDoVat, LoiNguyen),
    ];

    /// <summary>
    /// A run whose whole batch repeats the template of a recent episode is answered with
    /// <c>NoIdea(variety_template_repeat)</c>: every candidate is stored <c>rejected</c> with that reason, nothing
    /// is picked, and no second paid call is made — the block is decided from the history the run has already read.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_VarietyBlocksAllCandidates_ReturnsNoIdea()
    {
        var world = await SeedAsync(Database);

        // Two episodes, both carrying the template the candidates repeat. Two is the guard's floor (R5), so the
        // window is warm; neither value is over the cap at 2 of 10, so the block is a block and not a cap.
        await SeedWindowAsync(
            world,
            [(MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop), (MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop)],
            DefaultBeats);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                IdeaWithStructure("Ý tưởng trùng một", MaHocDuong, MotLoiKe, TrungHop),
                IdeaWithStructure("Ý tưởng trùng hai", MaHocDuong, MotLoiKe, TrungHop)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasNoIdea);
        result.FirstError.Description.ShouldContain(
            IdeaRejectionReason.VarietyTemplateRepeat,
            Case.Sensitive,
            "both candidates repeat one recent template, so the template rule is the batch's reason");
        text.Calls(TextStep.Ideas).ShouldBe(1, "the block costs no second paid call: the window was already read");
        text.Calls(TextStep.Novelty).ShouldBe(1, "the judge pass is one call per batch, made before the guard's verdict");
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "the whole batch is stored, rejected or not");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng trùng một"))
            .ShouldStartWith($"rejected|{IdeaRejectionReason.VarietyTemplateRepeat}");
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng trùng hai"))
            .ShouldStartWith($"rejected|{IdeaRejectionReason.VarietyTemplateRepeat}");
        IdeaRows(Database, world.SeriesId)
            .ShouldAllBe(row => !row.Contains("picked", StringComparison.Ordinal), "no candidate was eligible, so nothing was picked");
    }

    /// <summary>
    /// The steering the guard hands the writer's prompt: the rotation's least-used value per dimension in
    /// <c>required_tags</c>, the values over the cap in <c>forbidden_tags</c>, and an all-empty block omitted
    /// instead of sent empty.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_RequiredAndForbiddenTagsAppearInTheIdeaPrompt()
    {
        var world = await SeedAsync(Database);
        await SeedWindowAsync(world, TenEpisodeWindow, DefaultBeats);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                // A candidate that follows the rotation (its setting is the least-used one) and repeats nothing:
                // it is clean, so a failure here is about the steering and not about the guard's verdicts.
                IdeaWithStructure("Ý tưởng theo gợi ý", CleanSubGenre, CleanOpening, CleanTwist, CanHoThue),
                IdeaWithStructure("Ý tưởng thứ hai", CleanSubGenre, CleanOpening, CleanTwist, CanHoThue)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.2m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        var call = IdeaCalls(text).ShouldHaveSingleItem();
        DataObjectSteering(call, "required_tags").ShouldBe(ExpectedRequired, "the rotation's least-used value of each dimension");
        DataObjectSteering(call, "forbidden_tags").ShouldBe(ExpectedForbidden, "every value the tag-share cap is over");
        DataObjectContains(call, IdeaRejectionReason.VarietyTagCap).ShouldBeFalse("the counter is an outcome, never an instruction");
        // The judge value is a similarity: the 0.1 candidate is the fresher one and wins, so the 0.2 candidate
        // (the one the steering was written for) is the one that lost the ranking.
        IdeaOutcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng theo gợi ý"))
            .ShouldStartWith(
                $"rejected|{IdeaRejectionReason.NotBest}",
                Case.Sensitive,
                "a candidate that broke no rule lost the ranking, nothing more");
    }

    /// <summary>
    /// The soft cap: a candidate whose value is over the tag-share cap is <b>penalised</b>, never rejected — its
    /// score is lower than an otherwise equal candidate's by exactly <c>Variety weight × 0.5</c> and the other one
    /// is picked.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_VarietyPenalty_ReplacesTheSeamAndLowersTheScore()
    {
        var world = await SeedAsync(Database);

        // Four episodes share the opening the penalised candidate uses (4 of 10 is over the cap); the beat
        // signature of every episode differs from every candidate's, so only the cap can fire here.
        var window = new List<(string SubGenre, string Setting, string Opening, string Twist)>();
        for (var index = 0; index < 10; index++)
        {
            window.Add((MaHocDuong, BenhVienBoHoang, index < 4 ? HotOpening : MotLoiKe, DeadTwist));
        }

        await SeedWindowAsync(world, window, ["setup", "build_up", "twist"]);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                IdeaWithStructure("Ý tưởng quá tải", CleanSubGenre, HotOpening, LoiNguyen, CanHoThue),
                IdeaWithStructure("Ý tưởng sạch sẽ", CleanSubGenre, CleanOpening, LoiNguyen, CanHoThue)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.3m, fit: 0.8m, hookStrength: 0.6m), JudgeRow(1, 0.3m, fit: 0.8m, hookStrength: 0.6m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        var capped = Outcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng quá tải"));
        var clean = Outcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng sạch sẽ"));

        clean.Status.ShouldBe("picked", "the candidate under the cap wins the ranking");
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, "Ý tưởng sạch sẽ"));
        capped.Status.ShouldBe("rejected");
        capped.Reason.ShouldBe(IdeaRejectionReason.NotBest, "the tag-share cap is a penalty, never a rejection reason");
        capped.Score.ShouldBeLessThan(clean.Score, "the penalty is subtracted from the score");

        // Both candidates carry the same fit and hook and the same judge score, so the difference is the penalty's
        // 0.35 × 0.5 = 0.175 plus the two candidates' freshness term (their own similarity readings).
        (clean.Score - capped.Score).ShouldBe(
            0.175m + (0.45m * (capped.Similarity - clean.Similarity)),
            0.001m,
            "the seam's 0 became the guard's 0.5 through VarietyPenalty");
    }

    /// <summary>
    /// <c>Autopilot:Variety:Enabled = false</c> turns the guard's rejections and its penalty off — a batch that
    /// would repeat a template runs through — while the window, the cap and the steering stay the real ones.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_VarietyDisabled_NoRejectionNoPenalty_StillSteers()
    {
        var world = await SeedAsync(Database);
        await SeedWindowAsync(world, TenEpisodeWindow, DefaultBeats);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                // Exactly the template rows 0-3 of the window carry: enabled, this batch is blocked whole.
                IdeaWithStructure("Ý tưởng lặp mẫu", MaHocDuong, MotLoiKe, TrungHop),
                IdeaWithStructure("Ý tưởng thứ hai", CleanSubGenre, CleanOpening, CleanTwist, CanHoThue)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.3m, fit: 0.8m, hookStrength: 0.6m), JudgeRow(1, 0.3m, fit: 0.8m, hookStrength: 0.6m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2, varietyEnabled: false);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        var call = IdeaCalls(text).ShouldHaveSingleItem();
        DataObjectSteering(call, "required_tags").ShouldBe(ExpectedRequired, "the switch stops the verdicts, not the steering");
        DataObjectSteering(call, "forbidden_tags").ShouldBe(ExpectedForbidden);

        foreach (var title in new[] { "Ý tưởng lặp mẫu", "Ý tưởng thứ hai" })
        {
            var outcome = Outcome(Database, IdeaIdOf(Database, world.SeriesId, title));
            outcome.Reason.ShouldNotBe(
                IdeaRejectionReason.VarietyTemplateRepeat,
                $"{title} repeats a template the switched-off guard must not block on");
            outcome.Reason.ShouldNotBe(
                IdeaRejectionReason.VarietyTripleRepeat,
                $"{title} repeats a triple the switched-off guard must not block on");
        }
    }

    /// <summary>
    /// A cold series is blocked by nothing (R5): a single recent episode is below the guard's floor, so the batch
    /// runs through and its prompt is steered by nothing at all.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_ColdSeries_NotBlocked()
    {
        var world = await SeedAsync(Database);

        // One episode whose template both candidates repeat: the guard needs two structured episodes before it
        // reads a window, so this one rows nothing.
        await SeedWindowAsync(world, [(MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop)], DefaultBeats);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                IdeaWithStructure("Ý tưởng trùng mẫu", MaHocDuong, MotLoiKe, TrungHop),
                IdeaWithStructure("Ý tưởng khác mẫu", CleanSubGenre, CleanOpening, CleanTwist)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.2m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        Outcome(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng trùng mẫu")).Reason
            .ShouldNotBe(IdeaRejectionReason.VarietyTemplateRepeat, "one episode is not a window");
        var call = IdeaCalls(text).ShouldHaveSingleItem();
        DataObjectSteering(call, "required_tags").ShouldBe("-", "a cold series requires nothing");
        DataObjectSteering(call, "forbidden_tags").ShouldBe("-", "and forbids nothing");
    }

    /// <summary>
    /// The stored tags are vocabulary values (IDEA-S52-1): a value the topic's vocabulary does not carry is stored
    /// as <c>other:&lt;text&gt;</c> by the shared mapper, a value the model already wrote as <c>other:x</c> is
    /// normalised once, a vocabulary term is stored as it stands, and <c>setting</c> stays the model's free text.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_UnknownTagValues_StoredAsOtherAndGuardDoesNotCrash()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                // "truyện ma" is no vocabulary term of the ghost topic; the other three values are.
                IdeaWithStructure("Ý tưởng ngoài từ vựng", "truyện ma", CleanOpening, LoiNguyen),
                // The model wrote a value that is already `other:`-prefixed: the mapper strips it and maps the term
                // it names, so the prefix is never doubled (02 T71).
                IdeaWithStructure("Ý tưởng tự đánh dấu", "other:ma học đường", HotOpening, CleanTwist)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.2m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        IdeaTags(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng ngoài từ vựng"))
            .ShouldBe($"other:truyện ma|ngôi nhà cũ|{CleanOpening}|{LoiNguyen}|first_person");
        IdeaTags(Database, IdeaIdOf(Database, world.SeriesId, "Ý tưởng tự đánh dấu"))
            .ShouldBe($"{MaHocDuong}|ngôi nhà cũ|{HotOpening}|{CleanTwist}|first_person");
    }

    /// <summary>Seeds the series' structure history: one approved episode per entry, newest first.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entries">The episodes' four structure values; the first is the newest.</param>
    /// <param name="beats">The beats of every episode's paragraphs.</param>
    /// <returns>A task that completes when the history is stored.</returns>
    private async Task SeedWindowAsync(
        World world,
        IReadOnlyList<(string SubGenre, string Setting, string Opening, string Twist)> entries,
        IReadOnlyList<string> beats)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            await SeedStructuredHistoryAsync(
                Database,
                world,
                $"Tập đã kể {index}",
                Start.AddMinutes(-index),
                new StoryStructureTags(entry.SubGenre, entry.Setting, entry.Opening, entry.Twist, "first_person", []),
                beats);
        }
    }

    /// <summary>One idea row's disposition, parsed out of the raw probe for arithmetic.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The row's status, reason, score and similarity.</returns>
    private static (string Status, string Reason, decimal Score, decimal Similarity) Outcome(TestDatabase database, Guid ideaId)
    {
        var fields = IdeaOutcome(database, ideaId).Split('|');
        fields.Length.ShouldBe(4, $"unexpected outcome row: {IdeaOutcome(database, ideaId)}");

        return (fields[0], fields[1], decimal.Parse(fields[2], CultureInfo.InvariantCulture), decimal.Parse(fields[3], CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The vocabulary values of the ghost/vi topic the windows and the candidates of the variety suite are built
/// from, spelled as the shared vocabulary file spells them
/// (<c>prompts/shared/structure-vocab.v1.json</c>). Shared by <see cref="IdeaVarietyPipelineTests"/> and
/// <see cref="IdeaVarietyMetricsTests"/>, so both seed and script the same terms.
/// </summary>
internal static class IdeaVarietyVocabulary
{
    /// <summary>A <c>sub_genre</c> value; ten episodes of the window carry it, so it is over the cap.</summary>
    internal const string MaHocDuong = "ma học đường";

    /// <summary>A <c>sub_genre</c> value no seeded episode carries.</summary>
    internal const string CleanSubGenre = "truyện ma tâm linh";

    /// <summary>A <c>setting</c> value eight of the window's ten episodes carry, so it is over the cap.</summary>
    internal const string BenhVienBoHoang = "bệnh viện bỏ hoang";

    /// <summary>A <c>setting</c> value two of the window's ten episodes carry: the rotation's least-used one.</summary>
    internal const string CanHoThue = "căn hộ thuê";

    /// <summary>An <c>opening</c> value four of the window's ten episodes carry, so it is over the cap.</summary>
    internal const string MotLoiKe = "một lời kể truyền miệng";

    /// <summary>An <c>opening</c> value six of the window's ten episodes carry, so it is over the cap too.</summary>
    internal const string TimLaiDoVat = "tìm lại đồ vật cũ";

    /// <summary>An <c>opening</c> value no seeded episode carries.</summary>
    internal const string CleanOpening = "một cuộc điện thoại lúc nửa đêm";

    /// <summary>An <c>opening</c> value four episodes of the penalty window carry.</summary>
    internal const string HotOpening = "chuyển đến nơi ở mới";

    /// <summary>A <c>twist_type</c> value eight of the window's ten episodes carry, so it is over the cap.</summary>
    internal const string TrungHop = "trùng hợp bị bỏ lỡ";

    /// <summary>A <c>twist_type</c> value two of the window's ten episodes carry: the rotation's least-used one.</summary>
    internal const string LoiNguyen = "lời nguyền có thật";

    /// <summary>A <c>twist_type</c> value no seeded episode carries.</summary>
    internal const string CleanTwist = "người kể là nguyên nhân";

    /// <summary>A <c>twist_type</c> value every episode of the penalty window carries.</summary>
    internal const string DeadTwist = "người đã chết vẫn còn sống";
}

/// <summary>
/// The variety counters (02.8.4-T1): <c>variety_rejections_total{rule}</c> with the closed rule vocabulary and
/// <c>variety_penalty_total</c>, published on the idea meter by a run.
/// </summary>
/// <remarks>
/// The class runs alone ("Process-global host state"): the idea meter is one instrument per host, so a parallel
/// class would add its own readings to these counters.
/// </remarks>
[Collection("Process-global host state")]
public sealed class IdeaVarietyMetricsTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// One run, both counters: the rule that blocked a candidate (the closed rejection code, never free text) and
    /// the count of candidates the cap penalised without blocking, both visible in the run's own log as well.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_VarietyCounters_AreClosedAndRuleTagged()
    {
        var world = await SeedAsync(Database);

        // The window: one episode carries the template the blocked candidate repeats (its opening and twist are at
        // 1 of 10, under the cap, so that candidate is blocked and not penalised), four carry the opening the
        // other candidate uses (4 of 10 is over the cap, so that one is penalised and not blocked).
        var window = new List<(string SubGenre, string Setting, string Opening, string Twist)>
        {
            (MaHocDuong, BenhVienBoHoang, TimLaiDoVat, LoiNguyen),
        };
        for (var index = 0; index < 4; index++)
        {
            window.Add((MaHocDuong, BenhVienBoHoang, HotOpening, TrungHop));
        }

        for (var index = 0; index < 5; index++)
        {
            window.Add((MaHocDuong, BenhVienBoHoang, MotLoiKe, TrungHop));
        }

        for (var index = 0; index < window.Count; index++)
        {
            var entry = window[index];
            await SeedStructuredHistoryAsync(
                Database,
                world,
                $"Tập đã kể {index}",
                Start.AddMinutes(-index),
                new StoryStructureTags(entry.SubGenre, entry.Setting, entry.Opening, entry.Twist, "first_person", []),
                DefaultBeats);
        }

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                IdeaWithStructure("Ý tưởng lặp mẫu", CleanSubGenre, TimLaiDoVat, LoiNguyen, CanHoThue),
                IdeaWithStructure("Ý tưởng quá tải", CleanSubGenre, HotOpening, CleanTwist, CanHoThue)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.3m, fit: 0.8m, hookStrength: 0.6m), JudgeRow(1, 0.3m, fit: 0.8m, hookStrength: 0.6m)), Usage);
        var sink = new TestLogSink();
        using var counters = new LongCounters();
        await using var worker = await StartWorkerAsync(Database, text, count: 2, logs: sink);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, "Ý tưởng quá tải"), "the blocked candidate can never win, the capped one can");
        counters.Tagged(IdeaMetrics.VarietyRejectionsTotalName, IdeaMetrics.RuleTagName, IdeaRejectionReason.VarietyTemplateRepeat)
            .ShouldBe(1, "one candidate was blocked by the template rule");
        counters.Sum(IdeaMetrics.VarietyRejectionsTotalName).ShouldBe(1, "and nothing was counted under any other rule");
        counters.Sum(IdeaMetrics.VarietyPenaltiesTotalName).ShouldBe(1, "only the capped candidate carried a penalty");
        sink.AllText().ShouldContain("idea_planner_variety", Case.Sensitive, "the run's own log carries the counts and the closed rule code");
        sink.AllText().ShouldContain(IdeaRejectionReason.VarietyTemplateRepeat, Case.Sensitive);
        sink.AllText().ShouldContain("penalties=1", Case.Sensitive);

        // The tag's vocabulary is closed: the cap is a penalty, never a rule a candidate was blocked by.
        Should.Throw<ArgumentException>(() => new IdeaMetrics().VarietyRejections(IdeaRejectionReason.VarietyTagCap, 1));
    }

    /// <summary>
    /// A <see cref="MeterListener"/> over the idea meter that keeps each <see cref="long"/> reading with its value
    /// and its tags: how a test reads a counter without an exporter.
    /// </summary>
    private sealed class LongCounters : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];

        /// <summary>Starts the listener before the host exists, so no instrument is published unobserved.</summary>
        internal LongCounters()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, IdeaMetrics.MeterName, StringComparison.Ordinal)
                    && instrument is Counter<long>)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
            _listener.Start();
        }

        /// <summary>Every reading received so far, in order.</summary>
        internal IReadOnlyList<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        /// <summary>The summed value of one counter.</summary>
        /// <param name="instrument">The instrument's name.</param>
        /// <returns>The sum of its readings.</returns>
        internal long Sum(string instrument) =>
            Measurements.Where(reading => string.Equals(reading.Instrument, instrument, StringComparison.Ordinal))
                .Sum(reading => reading.Value);

        /// <summary>How many readings of one counter carry one tag value.</summary>
        /// <param name="instrument">The instrument's name.</param>
        /// <param name="tag">The tag's name.</param>
        /// <param name="value">The tag's value.</param>
        /// <returns>The number of matching readings.</returns>
        internal long Tagged(string instrument, string tag, string value) =>
            Measurements.Count(reading =>
                string.Equals(reading.Instrument, instrument, StringComparison.Ordinal)
                && reading.Tags.Any(pair => string.Equals(pair.Key, tag, StringComparison.Ordinal)
                    && string.Equals(pair.Value as string, value, StringComparison.Ordinal)));

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();

        private void Record(string instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_measurements)
            {
                _measurements.Add((instrument, value, tags.ToArray()));
            }
        }
    }
}
