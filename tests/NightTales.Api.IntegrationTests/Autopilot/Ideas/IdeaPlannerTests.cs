using System.Text.Json;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The planner half of 02.8.1-T1 (plan §4 U1 steps 1–10): the context read from real rows, the one paid call
/// scripted on <see cref="TextStep.Ideas"/>, the three selection rules of the batch, the persistence of every
/// survivor as <c>proposed</c>, and the D7/D14/D19 contracts around them.
/// </summary>
/// <remarks>
/// <para>
/// The provider edge is the <see cref="FakeTextClient"/> only; everything else is the production graph (see
/// <see cref="IdeaPlannerHarness"/>). The caller's <c>generation_runs</c> row is created by the test, because the
/// planner never writes it.
/// </para>
/// <para>
/// Isolation: the class database is shared by the class, so every count is scoped to the ids a test seeded (its
/// own user, its own series) and every probe reads the rows of the seeded series only.
/// </para>
/// </remarks>
public sealed class IdeaPlannerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// The acceptance check's manual smoke: one run for a seeded series persists every candidate the model
    /// proposed as <c>proposed</c>, each naming the caller's run and carrying the stored outline signature, and
    /// the pick is the first candidate.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_PersistsAllCandidates()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Căn nhà cuối hành lang"), Idea("Tiếng gõ trong đêm"), Idea("Bức ảnh cũ")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 3);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse(pick.Errors.Count > 0 ? pick.FirstError.Description : null);
        pick.Value.Title.ShouldBe("Căn nhà cuối hành lang", "T1's pick is the first valid candidate in the model's order");
        pick.Value.RequiresResearch.ShouldBeFalse("a ghost series' idea is a story, not an angle (F33)");
        pick.Value.Score.ShouldBe(0m, "nothing has scored the batch before 02.8.1-T2");

        IdeaCount(Database, world.SeriesId).ShouldBe(3);
        IdeasOfRun(Database, world.SeriesId, run.GenerationRunId)
            .ShouldBe(3, "every persisted candidate names the run the caller created");
        IdeaState(Database, pick.Value.IdeaId).ShouldBe("proposed|-", "the pick is a persisted row, still proposed");

        // The acceptance check's manual-smoke query and the run log projection (plan §5), as the set of rows: every
        // row of one batch carries the same FakeClock stamp, so the persistence order is not a contract.
        Render(ManualSmokeRows(Database, world.SeriesId)).ShouldBe(
            "Bức ảnh cũ|proposed|setup>clue>twist"
            + " || Căn nhà cuối hành lang|proposed|setup>clue>twist"
            + " || Tiếng gõ trong đêm|proposed|setup>clue>twist",
            "the acceptance check's manual-smoke query: title, status, stored beat signature");
        Render(IdeaRows(Database, world.SeriesId)).ShouldBe(
            "Bức ảnh cũ|proposed|true|true|setup>clue>twist"
            + " || Căn nhà cuối hành lang|proposed|true|true|setup>clue>twist"
            + " || Tiếng gõ trong đêm|proposed|true|true|setup>clue>twist");

        // One paid call, and the caller's run row is untouched by the planner.
        text.Requests.Count.ShouldBe(1);
        text.Requests[0].Step.ShouldBe(TextStep.Ideas);
        text.Requests[0].Run.GenerationRunId.ShouldBe(run.GenerationRunId);
        text.Requests[0].Run.Attempt.ShouldBe(1);
        RunCount(Database, world.SeriesId).ShouldBe(1);
        RunRow(Database, world.SeriesId).ShouldStartWith($"{Pg(run.GenerationRunId)}|ideas|queued|queued|-");
    }

    /// <summary>
    /// The selection rule of plan §4 U1 step 4: a candidate <c>IdeaValidator</c> refuses is dropped, and the
    /// valid ones around it are all persisted <c>proposed</c>, in the model's order.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_PersistsAllValidCandidatesAsProposed()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                Idea("Ý tưởng hợp lệ thứ nhất"),
                Idea("Ý tưởng thiếu twist", beats: ["setup", "clue"]),
                Idea("Ý tưởng hợp lệ thứ hai")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 3);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "the invalid candidate is dropped, not repaired");
        Render(IdeaRows(Database, world.SeriesId)).ShouldBe(
            "Ý tưởng hợp lệ thứ hai|proposed|true|true|setup>clue>twist"
            + " || Ý tưởng hợp lệ thứ nhất|proposed|true|true|setup>clue>twist");
        pick.Value.Title.ShouldBe("Ý tưởng hợp lệ thứ nhất");
    }

    /// <summary>A field over its configured width is the same <c>invalid</c> drop: only that candidate is lost.</summary>
    [Fact]
    public async Task IdeaPlanner_OverLongTitleField_DropsOnlyThatCandidate()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var tooLong = new string('a', new IdeaLimits().TitleChars + 1);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Tiêu đề đầu"), Idea(tooLong), Idea("Tiêu đề cuối")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 3);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaCount(Database, world.SeriesId).ShouldBe(2);
        Render(IdeaRows(Database, world.SeriesId)).ShouldBe(
            "Tiêu đề cuối|proposed|true|true|setup>clue>twist"
            + " || Tiêu đề đầu|proposed|true|true|setup>clue>twist");
    }

    /// <summary>
    /// The dedupe rule: two titles of one batch that read the same are one idea, and the later duplicate is
    /// dropped — the first candidate of the group is the one persisted.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_DuplicateTitlesWithinBatch_DropsLaterDuplicate()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Chuyện cũ"), Idea("chuyện  cũ"), Idea("Một chuyện khác")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 3);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "the later duplicate is dropped");
        Render(IdeaRows(Database, world.SeriesId)).ShouldBe(
            "Chuyện cũ|proposed|true|true|setup>clue>twist"
            + " || Một chuyện khác|proposed|true|true|setup>clue>twist");
    }

    /// <summary>
    /// The failure table's "zero valid candidates": a schema-valid answer whose every candidate the validator
    /// refuses fails the run <c>ideas_invalid_output</c> without a second paid call, and persists nothing.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_AllCandidatesInvalid_ReturnsInvalidOutput()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Thiếu twist", beats: ["setup", "clue"]), Idea("Cũng thiếu twist", beats: ["build_up", "clue"])),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasInvalidOutput);
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        text.Requests.Count.ShouldBe(1, "the D7 retry is for an output the schema refused, not for a validator drop");
        UsageCount(Database, world.UserId).ShouldBe(1, "the call was made and is metered");
    }

    /// <summary>
    /// The D7 retry: an output the schema refused earns exactly one more call, sent under <c>attempt = 2</c>; a
    /// second refusal is final, the run fails <c>ideas_invalid_output</c> and no idea row exists.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_InvalidOutput_RetriesOnceThenFails()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient()
            .Enqueue(TextStep.Ideas, TextCallResult<IdeasV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<IdeasV1>.SchemaInvalid]))
            .Enqueue(TextStep.Ideas, TextCallResult<IdeasV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<IdeasV1>.MaxOutputTokensExceeded]));
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasInvalidOutput);
        text.Requests.Count.ShouldBe(2, "at most two paid calls per run (D7)");
        text.Requests[0].Run.Attempt.ShouldBe(1);
        text.Requests[1].Run.Attempt.ShouldBe(2, "the retry is identified by attempt = 2");
        text.Requests[1].Run.GenerationRunId.ShouldBe(run.GenerationRunId);
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        UsageCount(Database, world.UserId).ShouldBe(2, "both refused calls were dispatched and are metered");
    }

    /// <summary>
    /// A provider refusal on the run's own paid call writes one <c>moderation_events</c> row with
    /// <c>stage = ideas</c> (TL ruling #3) before the planner answers <c>ideas_moderated</c>; no idea is stored.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_Moderated_WritesModerationEventWithStageIdeas()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().Enqueue(
            TextStep.Ideas,
            TextCallResult<IdeasV1>.Moderated(Model, Usage, "req-fake", ["sexual"]));
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasModerated);
        ModerationRows(Database, world.UserId).ShouldBe(
            "openai_text|ideas|true|sexual|true",
            "the stage is the run kind, the run is series-level, and no excerpt is stored (D19)");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        text.Requests.Count.ShouldBe(1, "a refusal is final: the D7 retry exists for a schema-refused output only");
        UsageCount(Database, world.UserId).ShouldBe(1);
    }

    /// <summary>
    /// D14: the one paid call of a run is metered by the generator itself, once, under <c>operation = idea</c>,
    /// with a NULL episode (the run is series-level) and a positive cost.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_RecordsUsageWithOperationIdea()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng được ghi nhận")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        UsageCount(Database, world.UserId).ShouldBe(1, "one row per call, written inside the generator");
        UsageRows(Database, world.UserId).ShouldBe(
            $"idea|true|true|{ActivePriceVersion(Database)}|false",
            "operation = idea, episode_id IS NULL, cost_usd > 0, priced (not estimated)");
        IdeaCount(Database, world.SeriesId).ShouldBe(1);
    }

    /// <summary>
    /// The one-writer rule (task file [DECISION]): the caller owns the <c>generation_runs</c> row, so a whole
    /// idea step leaves every column of the run exactly as the caller wrote it.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_DoesNotWriteGenerationRuns()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var before = RunRow(Database, world.SeriesId);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng không chạm vào run")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaCount(Database, world.SeriesId).ShouldBe(1, "the step really ran");
        RunCount(Database, world.SeriesId).ShouldBe(1);
        RunRow(Database, world.SeriesId).ShouldBe(before, "the planner never writes generation_runs");
    }

    /// <summary>
    /// Tenant isolation (09 README D10): another user's series answers the same <c>not_found</c> an unknown one
    /// does, no paid call is made and nothing is persisted.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_OtherUsersSeries_ReturnsNotFound()
    {
        var world = await SeedAsync(Database);
        var other = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Không được dùng")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var foreign = await ProposeAsync(worker, run, seriesId: other.SeriesId, asUser: world.UserId);
        var unknown = await ProposeAsync(worker, run, seriesId: Guid.CreateVersion7(), asUser: world.UserId);

        foreign.IsError.ShouldBeTrue();
        foreign.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        unknown.FirstError.Code.ShouldBe(ErrorCodes.NotFound, "the foreign and the unknown answer the same code");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        IdeaCount(Database, other.SeriesId).ShouldBe(0);
        text.Requests.ShouldBeEmpty("the series is resolved before the paid call");
        UsageCount(Database, world.UserId).ShouldBe(0);
    }

    /// <summary>
    /// The clamp of the batch size: a context that reports a count above <c>Autopilot:Ideas:MaxCount</c> (a
    /// mis-bound provider) is cut to the ceiling — the paid call asks for the ceiling and the batch keeps at most
    /// that many candidates, the rest counted as dropped-invalid.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_ContextCountAboveMaxCount_AsksForAndKeepsMaxCount()
    {
        var ceiling = new IdeaPlannerOptions().MaxCount;
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);

        // The real provider clamps to the same ceiling, so only a mis-bound context can report one above it.
        var context = new IdeaContext("ghost", "vi", null, [], null, [], [], ceiling + 40);
        var over = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch([.. Enumerable.Range(1, ceiling + 2).Select(index => Idea($"Ý tưởng số {index}")).ToArray()]),
            Usage);
        await using var worker = await StartWorkerAsync(Database, over, contexts: new FixedContextProvider(context));

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        over.Requests.Count.ShouldBe(1);
        DataObjectCount(over.Requests[0]).ShouldBe(ceiling, "the request asks for the ceiling, never for the mis-bound count");
        IdeaCount(Database, world.SeriesId).ShouldBe(ceiling, "candidates beyond the ceiling are dropped before validation");
    }
}
