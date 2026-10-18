using System.Diagnostics.Metrics;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// Independent QA of 02.8.1-T2's pipeline through the real production graph: the shipped thresholds at their exact
/// boundaries, a judge answer with duplicated and out-of-range indices, an out-of-contract score, the empty corpus,
/// the corpus' tenant isolation and the two calibration histograms.
/// </summary>
/// <remarks>
/// Every assertion reads the stored <c>episode_ideas</c> rows or the instruments the run published, never a mock of
/// the checkers: what these tests pin is what a run leaves behind. The class runs alone ("Process-global host
/// state"): the idea meter is published once per host, so a parallel pipeline class would add readings to the
/// histogram counts.
/// </remarks>
[Collection("Process-global host state")]
public sealed class IdeaPipelineQA(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The tokens <c>t{from} … t{to}</c>, joined by single spaces.</summary>
    /// <param name="from">The first token's number.</param>
    /// <param name="to">The last token's number.</param>
    /// <returns>The text.</returns>
    private static string Tokens(int from, int to) =>
        string.Join(' ', Enumerable.Range(from, to - from + 1).Select(index => $"t{index}"));

    /// <summary>
    /// An <c>ideas.v1</c> candidate whose three lexical texts are token runs the test controls: the title carries
    /// the first run, the logline and the setting the rest, so the shingle text is exactly the tokens the caller
    /// passed (task Design "Lexical": <c>title + " " + logline + " " + setting</c>).
    /// </summary>
    /// <param name="titleTokens">The title's tokens.</param>
    /// <param name="loglineTokens">The logline's tokens.</param>
    /// <param name="settingTokens">The setting's tokens.</param>
    /// <returns>The wire candidate.</returns>
    private static IdeaV1 TokenIdea(string titleTokens, string loglineTokens, string settingTokens) =>
        new(
            titleTokens,
            loglineTokens,
            "Đêm ấy có tiếng gõ cửa lúc nửa đêm.",
            settingTokens,
            new IdeaStructureV1("truyện ma", "ngôi nhà cũ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân", "first_person"),
            ["setup", "clue", "twist"]);

    /// <summary>
    /// The lexical ceiling exactly, end to end: the corpus is a 29-token episode (20 windows) and the first
    /// candidate is its first 16 tokens (7 windows, all of them the corpus'), so the pass scores 7/20 = 0.35 — the
    /// shipped <c>LexicalMax</c>, which the planner rejects <b>above</b> only. One token more is 8/20 = 0.40 and is
    /// rejected.
    /// </summary>
    /// <remarks>
    /// The candidate's title is the token run itself: the title is both the row's title and the first of the three
    /// lexical texts, so no readable title can be added without changing the window arithmetic the boundary is
    /// about. The corpus episode's own title is the first ten tokens of the same sequence and its summary the rest.
    /// </remarks>
    [Fact]
    public async Task QA_LexicalAtTheCeiling_IsPicked_AndOneWindowMoreIsRejected()
    {
        var world = await SeedAsync(Database);
        var episode = await SeedApprovedEpisodeAsync(Database, world, Tokens(1, 10), Tokens(11, 29));
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                TokenIdea(Tokens(1, 5), Tokens(6, 11), Tokens(12, 16)),
                TokenIdea(Tokens(1, 6), Tokens(7, 12), Tokens(13, 17))),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        var atCeiling = IdeaIdOf(Database, world.SeriesId, Tokens(1, 5));
        var above = IdeaIdOf(Database, world.SeriesId, Tokens(1, 6));

        NoveltyScores(Database, atCeiling).ShouldBe(
            $"0.35|0.1|null|{IdeaNoveltyKind.Episode}|{Pg(episode)}",

            "7 of 20 windows is 0.35 exactly, which is not above the 0.35 ceiling");
        IdeaOutcome(Database, atCeiling).ShouldStartWith("picked|-", Case.Sensitive);
        pick.Value.IdeaId.ShouldBe(atCeiling);

        NoveltyScores(Database, above).ShouldStartWith("0.4|0.1|", Case.Sensitive, "8 of 20 windows is 0.40");
        IdeaOutcome(Database, above).ShouldStartWith($"rejected|{IdeaRejectionReason.NoveltyLexical}", Case.Sensitive);
    }

    /// <summary>
    /// The judge's own arithmetic may not cost a candidate its verdict: a duplicated index keeps the first row, a
    /// row the batch has no position for is ignored, and every candidate the answer did mention keeps its judge
    /// signals. The 0.9 duplicate would have lost the pick had it been the row consumed, so the assertions are
    /// about behaviour and not only about a stored value.
    /// </summary>
    [Fact]
    public async Task QA_JudgeRowsWithDuplicateAndOutOfRangeIndices_KeepTheFirstAndDropNone()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ứng viên thứ nhất"), DistinctIdea("Ứng viên thứ hai")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(
                JudgeRow(1, 0.2m, fit: 0.5m),
                JudgeRow(0, 0.1m),
                JudgeRow(0, 0.9m),
                JudgeRow(7, 0.1m)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        text.Calls(TextStep.Novelty).ShouldBe(1, "the answer is a successful outcome, so it is consumed once");

        var first = IdeaIdOf(Database, world.SeriesId, "Ứng viên thứ nhất");
        var second = IdeaIdOf(Database, world.SeriesId, "Ứng viên thứ hai");

        NoveltyScores(Database, first).ShouldBe(
            "0|0.1|null|null|null",

            "the first row for index 0 is the one consumed; the 0.9 duplicate is ignored");
        NoveltyScores(Database, second).ShouldBe("0|0.2|null|null|null");
        NoveltyJudgePresence(Database, first).ShouldBe("judge");
        NoveltyJudgePresence(Database, second).ShouldBe("judge", "a row outside the batch never costs a candidate its own");
        pick.Value.IdeaId.ShouldBe(
            first,
            "0.45×0.9 + 0.30×1.0 + 0.25×0.5 = 0.83 beats the second candidate's 0.635; the 0.9 duplicate would have given 0.47 and lost");
    }

    /// <summary>
    /// A judge that ignores its own schema cannot fail the run: a score outside 0…1 is clamped to the column's
    /// domain and the candidate is then rejected on the threshold it really exceeds (the defensive half of task
    /// Design "Novelty"'s answer mapping). The batch's only candidate is rejected, so the run answers
    /// <c>ideas_no_idea</c>.
    /// </summary>
    [Fact]
    public async Task QA_JudgeScoreOutsideTheContract_IsClampedAndTheRunSurvives()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ứng viên điểm lạ")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 9.9m, fit: 4m, hookStrength: -2m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        var idea = IdeaIdOf(Database, world.SeriesId, "Ứng viên điểm lạ");

        NoveltyScores(Database, idea).ShouldBe(
            "0|1|null|null|null",

            "a score the schema forbids is clamped to the numeric(5,4) domain instead of failing the save");
        IdeaOutcome(Database, idea).ShouldStartWith($"rejected|{IdeaRejectionReason.NoveltyJudge}", Case.Sensitive);
        pick.IsError.ShouldBeTrue("the batch's only candidate is rejected");
        pick.FirstError.Code.ShouldBe(ErrorCodes.IdeasNoIdea);
        IdeaCount(Database, world.SeriesId).ShouldBe(1, "the rejected candidate is still stored with its reason");
    }

    /// <summary>
    /// The series' first episode: no corpus at all, so every candidate's lexical score is 0 and the judge's
    /// <c>fit</c>/<c>hook_strength</c> drive the pick (Failure table row "Corpus is empty"). The judge still runs,
    /// and it is sent no past episode.
    /// </summary>
    [Fact]
    public async Task QA_EmptyCorpus_StillAsksTheJudge_AndFitDecidesThePick()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng ít hợp"), DistinctIdea("Ý tưởng rất hợp")),
            Usage);
        text.EnqueueOk(
            TextStep.Novelty,
            NoveltyBatch(JudgeRow(0, 0.1m, fit: 0.2m, hookStrength: 0.1m), JudgeRow(1, 0.1m, fit: 1m, hookStrength: 0.9m)),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        DataObjectArrayLength(judge, "past_episodes").ShouldBe(0, "the series has nothing behind it yet");

        var fitted = IdeaIdOf(Database, world.SeriesId, "Ý tưởng rất hợp");
        pick.Value.IdeaId.ShouldBe(fitted);
        NoveltyScores(Database, fitted).ShouldBe("0|0.1|null|null|null");
        NoveltyCorpusCounts(Database, fitted).ShouldBe("0|0");
        pick.Value.Score.ShouldBe(0.93m, "0.45 × (1 − 0.1) + 0.30 × 1.0 + 0.25 × 0.9");
    }

    /// <summary>
    /// The corpus is the series' own: another owner's approved episode and pending idea whose texts are the
    /// candidate's exact copy contribute nothing, so the candidate is neither rejected nor credited with an overlap
    /// that belongs to someone else.
    /// </summary>
    [Fact]
    public async Task QA_CorpusNeverCarriesAnotherOwnersEpisodesOrPendingIdeas()
    {
        var world = await SeedAsync(Database);
        var rival = await SeedAsync(Database);
        const string Shared = "Bản sao của người khác";
        var rivalEpisode = await SeedCorpusCopyAsync(Database, rival, Shared);
        var rivalRun = await CreateRunAsync(Database, rival);
        var rivalIdea = await SeedPendingCopyAsync(Database, rival, rivalRun.GenerationRunId, Shared);

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea(Shared)), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        var idea = IdeaIdOf(Database, world.SeriesId, Shared);

        // Positive controls: the rival's rows exist and carry the candidate's own text, so a zero below means the
        // scope hid them rather than that they were never seeded.
        IdeaState(Database, rivalIdea).ShouldBe("proposed|-");
        SqlProbe.Count(Database, $"SELECT count(*) FROM episodes WHERE id = '{Pg(rivalEpisode)}'").ShouldBe(1);

        NoveltyScores(Database, idea).ShouldBe(
            "0|0.1|null|null|null",

            "another owner's rows are invisible, so nothing overlapped");
        NoveltyCorpusCounts(Database, idea).ShouldBe("0|0", "the corpus carried neither the rival's episode nor its idea");
        pick.Value.IdeaId.ShouldBe(idea, "a copy of another owner's idea is not the candidate's own copy");
    }

    /// <summary>
    /// The two calibration histograms report the readings the run stored — one per candidate for the lexical pass,
    /// one per judged candidate for the judge — and they carry no tag at all (D19: a score is a number, never a text
    /// or an id).
    /// </summary>
    [Fact]
    public async Task QA_NoveltyHistograms_ReportTheStoredScores_AndCarryNoTags()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        using var histograms = new ScoreHistograms();
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng đo được một"), DistinctIdea("Ý tưởng đo được hai")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.25m), JudgeRow(1, 0.5m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);

        var lexical = histograms.For(IdeaMetrics.NoveltyLexicalScoreName);
        lexical.Length.ShouldBe(2, "one lexical reading per stored candidate");
        lexical.ShouldAllBe(reading => reading.Value == 0d && reading.Tags.Length == 0);

        var judge = histograms.For(IdeaMetrics.NoveltyJudgeScoreName);
        var judgeValues = judge.Select(reading => reading.Value).OrderBy(value => value).ToArray();
        judgeValues.Length.ShouldBe(2, "one judge reading per judged candidate");
        judgeValues[0].ShouldBe(0.25);
        judgeValues[1].ShouldBe(0.5);
        judge.ShouldAllBe(reading => reading.Tags.Length == 0, "the histograms are untagged numbers (D19)");
    }

    /// <summary>
    /// A <see cref="MeterListener"/> over the idea feature's meter that keeps the values as well as the tags, which
    /// is how a histogram is read without an exporter.
    /// </summary>
    private sealed class ScoreHistograms : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> _readings = [];

        /// <summary>Starts the listener before the host exists, so no instrument is published unobserved.</summary>
        internal ScoreHistograms()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, IdeaMetrics.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                lock (_readings)
                {
                    _readings.Add((instrument.Name, value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        /// <summary>Every reading of one instrument so far, in order.</summary>
        /// <param name="instrument">The instrument's name.</param>
        /// <returns>The readings of that instrument, in order.</returns>
        internal (string Instrument, double Value, KeyValuePair<string, object?>[] Tags)[] For(string instrument)
        {
            lock (_readings)
            {
                return [.. _readings.Where(reading => string.Equals(reading.Instrument, instrument, StringComparison.Ordinal))];
            }
        }

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();
    }
}
