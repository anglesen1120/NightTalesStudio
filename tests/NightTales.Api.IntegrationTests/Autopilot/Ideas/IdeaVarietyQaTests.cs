using System.Globalization;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fixtures;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;
using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaVarietyVocabulary;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// Independent QA characterisation of the ranking the variety suite's steering test relies on (02.8.4-T1): it pins
/// what a run <b>stores for both candidates</b> of that scenario, because the branch's own test
/// (<c>IdeaVarietyPipelineTests.ProposeAndPick_RequiredAndForbiddenTagsAppearInTheIdeaPrompt</c>, its last
/// assertion) reads only the losing candidate's row and leaves the winner's row unobserved.
/// </summary>
/// <remarks>
/// Same scenario, same window and same scripted judge rows as that test, with the assertions added on the winner:
/// the judge's value is a <i>similarity</i> inside the scorer's freshness term (novelty weight x
/// (1 - max(lexical, judge)), <c>IdeaScorer</c>), so the candidate whose row reads the lower similarity is the
/// fresher one and wins the ranking; the other is rejected with <c>not_best</c> and no rule code.
/// </remarks>
public sealed class IdeaVarietyQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Suggested = "Ý tưởng theo gợi ý";
    private const string Second = "Ý tưởng thứ hai";

    /// <summary>The ten-episode window of the production steering test, newest first (values: see the vocabulary).</summary>
    private static readonly (string SubGenre, string Setting, string Opening, string Twist)[] Window =
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
    /// Two candidates the guard lets through, judged 0.2 and 0.1: the row with the lower similarity is picked and
    /// the other one is only <c>not_best</c> — the observation the production steering test's expectation inverts.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_LowerSimilarityRowWins_AndTheOtherIsOnlyNotBest()
    {
        var world = await SeedAsync(Database);
        for (var index = 0; index < Window.Length; index++)
        {
            var entry = Window[index];
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
                IdeaWithStructure(Suggested, CleanSubGenre, CleanOpening, CleanTwist, CanHoThue),
                IdeaWithStructure(Second, CleanSubGenre, CleanOpening, CleanTwist, CanHoThue)),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.2m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);

        var winner = Outcome(IdeaIdOf(Database, world.SeriesId, Second));
        var loser = Outcome(IdeaIdOf(Database, world.SeriesId, Suggested));

        winner.Status.ShouldBe("picked");
        winner.Reason.ShouldBe("-", "the scorer, not a guard rule, decided this batch");
        winner.Similarity.ShouldBe(0.1m, "the stored similarity is the judge's own row for this candidate");
        winner.Score.ShouldBeGreaterThan(loser.Score, "the fresher (lower similarity) candidate carries the higher score");
        loser.Status.ShouldBe("rejected");
        loser.Reason.ShouldBe(IdeaRejectionReason.NotBest, "it broke no rule: it only lost the ranking");
        loser.Similarity.ShouldBe(0.2m);
        pick.Value.IdeaId.ShouldBe(IdeaIdOf(Database, world.SeriesId, Second));
    }

    /// <summary>One idea row's outcome, parsed: <c>status|reason|score|similarity</c>.</summary>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The four fields.</returns>
    private (string Status, string Reason, decimal Score, decimal Similarity) Outcome(Guid ideaId)
    {
        var row = IdeaOutcome(Database, ideaId);
        var fields = row.Split('|');
        fields.Length.ShouldBe(4, $"unexpected outcome row: {row}");

        return (
            fields[0],
            fields[1],
            decimal.Parse(fields[2], CultureInfo.InvariantCulture),
            decimal.Parse(fields[3], CultureInfo.InvariantCulture));
    }
}
