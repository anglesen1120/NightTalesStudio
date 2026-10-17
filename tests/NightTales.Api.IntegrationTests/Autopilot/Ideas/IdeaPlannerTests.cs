using System.Text.Json;
using ErrorOr;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Application.Features.Autopilot.Ideas.Abstractions;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
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

        // The retry re-checks the platform cap (C1) and the real guard sums the whole platform's spend, so its
        // answer inside a shared class database is not a test fact: the allowed answer is pinned with a stub.
        await using var worker = await StartWorkerAsync(
            Database,
            text,
            count: 1,
            guard: new StubBudgetGuard(allowed: true),
            estimator: new StubIdeaCostEstimator(new UsageBound(0.25m, 1_000, 1_500)));

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

    /// <summary>
    /// The two keys of <c>series.autopilot_settings</c> 02.8.3-T1 owns (TL ruling #3): the owner's tone and banned
    /// themes are read defensively and travel in the one data object exactly as written.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_AutopilotSettings_ToneAndBannedThemes_ReachTheDataObject()
    {
        var world = await SeedAsync(
            Database,
            autopilotSettings: """{"tone":"điềm tĩnh, lạnh sống lưng","banned_themes":["tự tử","trẻ em bị hại"]}""");
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng có tone")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        using var document = JsonDocument.Parse(text.Requests.ShouldHaveSingleItem().UserDataJson);
        document.RootElement.GetProperty("tone").GetString().ShouldBe("điềm tĩnh, lạnh sống lưng");
        document.RootElement.GetProperty("banned_themes").EnumerateArray().Select(theme => theme.GetString())
            .ShouldBe(["tự tử", "trẻ em bị hại"]);
    }

    /// <summary>
    /// The failure table's defensive read (TL ruling #3): a settings document the current schema has not written
    /// yet — absent, a non-object, a malformed entry — is <c>null</c>/empty, never an error, so the prompt omits the
    /// two keys and the run still proposes.
    /// </summary>
    /// <param name="settings">The stored <c>autopilot_settings</c> document (the column is <c>jsonb</c>, so it is
    /// always valid JSON; a shape the reader does not know is what it must survive).</param>
    /// <param name="expectedTheme">The one theme the document really carries, or <c>null</c>.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("\"tone\"", null)]
    [InlineData("[]", null)]
    [InlineData("{}", null)]
    [InlineData("""{"tone":"   ","banned_themes":[]}""", null)]
    [InlineData("""{"tone":42,"banned_themes":[7,"ok"]}""", "ok")]
    public async Task IdeaPlanner_UnusableAutopilotSettings_OmitTheKeysWithoutFailing(
        string? settings,
        string? expectedTheme)
    {
        var world = await SeedAsync(Database, autopilotSettings: settings);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng không cần settings")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse("a settings document the schema does not own yet is never an error");
        using var document = JsonDocument.Parse(text.Requests.ShouldHaveSingleItem().UserDataJson);
        document.RootElement.TryGetProperty("tone", out _).ShouldBeFalse();
        if (expectedTheme is null)
        {
            document.RootElement.TryGetProperty("banned_themes", out _).ShouldBeFalse();
        }
        else
        {
            document.RootElement.GetProperty("banned_themes").EnumerateArray().Select(theme => theme.GetString())
                .ShouldBe([expectedTheme]);
        }
    }

    /// <summary>
    /// The settings contract's own bounds (02.8.3-T1, TL ruling #3) are applied when the document is read: a
    /// hand-written row cannot grow the data object past a 120-character tone or 20 banned themes of 60 characters.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_OversizedAutopilotSettings_AreClampedToTheSettingsContract()
    {
        var longTone = new string('t', 130);
        var longThemes = Enumerable.Range(1, 25).Select(index => $"{index}-{new string('x', 80)}");
        var world = await SeedAsync(
            Database,
            autopilotSettings: JsonSerializer.Serialize(new { tone = longTone, banned_themes = longThemes }));
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng có settings dài")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        using var document = JsonDocument.Parse(text.Requests.ShouldHaveSingleItem().UserDataJson);
        document.RootElement.GetProperty("tone").GetString()!.Length.ShouldBe(120);
        var themes = document.RootElement.GetProperty("banned_themes").EnumerateArray()
            .Select(theme => theme.GetString()!).ToArray();
        themes.Length.ShouldBe(20);
        themes.ShouldAllBe(theme => theme.Length <= 60);
    }

    /// <summary>
    /// F33's angle path: a <c>true_crime</c> series is the one topic whose pick is flagged
    /// <c>RequiresResearch</c> (the facts arrive later from sources), and the control key travels snake_case.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_TrueCrimeTopic_FlagsThePickAsRequiringResearch()
    {
        var world = await SeedAsync(Database, Topic.TrueCrime);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Một góc nhìn chưa kể")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        pick.Value.RequiresResearch.ShouldBeTrue("a true_crime idea is an angle plus research questions (F33)");
        DataObjectText(text.Requests[0], "topic").ShouldBe("true_crime");
    }

    /// <summary>
    /// The dedupe key is deliberately smaller than T2's novelty normaliser (plan §4 U1 step 4): two titles are the
    /// same idea when they read the same, so their case and spacing may not differ, but their letters must — two
    /// Vietnamese titles differing only by diacritics are two ideas.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_TitlesDifferingOnlyByDiacritics_AreTwoIdeas()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Chuyện cũ ở làng"), Idea("Chuyen cu o lang")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "diacritics are letters: neither title is a duplicate");
    }

    /// <summary>
    /// The same title written in NFD (a decomposed Vietnamese string, what a paste or another tool can produce) and
    /// in NFC is one idea: the key normalises to NFC first, so the composition of the input never splits a batch.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_NfdAndNfcTitlesOfTheSameText_AreOneIdea()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var composed = "Chuyện cũ ở làng";
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        decomposed.ShouldNotBe(composed, "the fixture really is a decomposed spelling");

        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea(composed), Idea(decomposed)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        IdeaCount(Database, world.SeriesId).ShouldBe(1, "the later spelling of the same title is the duplicate");
    }

    /// <summary>
    /// D19: a <c>url</c> source of the series contributes nothing to the data object — its extracted text is never
    /// read and neither its title nor its URL may travel — while the owner's own notes still do.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_UrlSource_ContributesNoNoteNoTitleAndNoUrl()
    {
        const string fetchedTitle = "Canary: the 1974 archive page title";
        var world = await SeedAsync(Database);
        var episode = await SeedEpisodeAsync(Database, world);
        await SeedNoteAsync(Database, world, episode, "Ghi chú của chủ series.");
        await SeedUrlSourceAsync(Database, world, episode, fetchedTitle);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng khi có nguồn url")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var call = text.Requests.ShouldHaveSingleItem();
        call.UserDataJson.Contains(fetchedTitle, StringComparison.Ordinal)
            .ShouldBeFalse("a fetched title may not reach the provider (D19)");
        call.UserDataJson.Contains("bbc.co.uk", StringComparison.Ordinal)
            .ShouldBeFalse("no URL may reach the provider (D6)");
        call.UserDataJson.Contains("text.txt", StringComparison.Ordinal)
            .ShouldBeFalse("the extracted-text key is never read, only the note row is");

        using var document = JsonDocument.Parse(call.UserDataJson);
        var notes = document.RootElement.GetProperty("research_notes");
        notes.GetArrayLength().ShouldBe(1, "only the user's own note is a note");
        notes[0].GetProperty("kind").GetString().ShouldBe("user_note");
        notes[0].GetProperty("text").GetString().ShouldBe("Ghi chú của chủ series.");
    }

    /// <summary>
    /// The failure table's empty-bible row end to end: a series with no bible row sends no <c>bible</c> key and
    /// its approved-episode history as the top-level <c>recent_episodes</c>, so a series whose bible is somehow
    /// empty still avoids repeating itself.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_SeriesWithoutABible_SendsTheHistoryInstead()
    {
        var world = await SeedAsync(Database);
        var historyWorld = new NightTales.Api.IntegrationTests.SeriesBible.World(world.UserId, world.SeriesId);
        await NightTales.Api.IntegrationTests.SeriesBible.SeriesBibleHarness
            .SeedApprovedEpisodeAsync(Database, historyWorld);

        var expectedTitle = NightTales.Api.IntegrationTests.Persistence.SqlProbe.ScalarText(
            Database,
            $"""
             SELECT sv.title FROM episodes e JOIN story_versions sv ON sv.id = e.approved_story_version_id
             WHERE e.series_id = '{Pg(world.SeriesId)}'
             """);
        expectedTitle.ShouldNotBeNullOrWhiteSpace("the seeded history really carries a title");

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng không cần bible")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        using var document = JsonDocument.Parse(text.Requests.ShouldHaveSingleItem().UserDataJson);
        document.RootElement.TryGetProperty("bible", out _).ShouldBeFalse("a series without a bible sends no bible key");
        document.RootElement.GetProperty("recent_episodes")[0].GetProperty("title").GetString().ShouldBe(expectedTitle);
    }

    /// <summary>
    /// The D7 outcome table's remaining rows: a provider account out of funds is the caller's pause code and is
    /// never metered, and an unavailable provider or a provider error is a generation failure — every one of them
    /// an answer, never a crash of the caller's job, and none of them earns the one retry (that is for a refused
    /// output only).
    /// </summary>
    /// <param name="outcome">The scripted provider outcome.</param>
    /// <param name="expectedCode">The error code the planner must answer.</param>
    [Theory]
    [InlineData("funds", ErrorCodes.ProviderFundsExhausted)]
    [InlineData("unavailable", ErrorCodes.IdeasGenerationFailed)]
    [InlineData("provider-error", ErrorCodes.IdeasGenerationFailed)]
    public async Task IdeaPlanner_ProviderOutcome_MapsToTheD7Error(string outcome, string expectedCode)
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var scripted = outcome switch
        {
            "funds" => TextCallResult<IdeasV1>.ProviderFundsExhausted(Model, "req-fake"),
            "unavailable" => TextCallResult<IdeasV1>.ProviderUnavailable(Model, "req-fake"),
            _ => TextCallResult<IdeasV1>.ProviderError(Model, 500, "req-fake"),
        };
        var text = new FakeTextClient().Enqueue(TextStep.Ideas, scripted);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeTrue();
        pick.FirstError.Code.ShouldBe(expectedCode);
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
        text.Requests.Count.ShouldBe(1, "only a refused output may be retried (D7)");
        if (outcome == "funds")
        {
            UsageCount(Database, world.UserId).ShouldBe(0, "no request was sent, so nothing can have been billed (SEC-1)");
        }
    }

    /// <summary>
    /// C2 (plan §7.1, the <c>ProposeBibleUpdateHandler</c> rule the reviewer cited): a first answer the client
    /// stopped at the ruled <c>max_output_tokens</c> is final — the same cap would cut the same text — so the run
    /// fails on the first call with <c>ideas_invalid_output</c> and only one paid call is ever made.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_MaxOutputTokensExceeded_IsNotRetried()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient()
            .Enqueue(TextStep.Ideas, TextCallResult<IdeasV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<IdeasV1>.MaxOutputTokensExceeded]))
            .EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Không bao giờ được gọi")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.IdeasInvalidOutput);
        text.Requests.Count.ShouldBe(1, "a truncated answer is final on the first call (C2)");
        text.Requests[0].Run.Attempt.ShouldBe(1);
        UsageCount(Database, world.UserId).ShouldBe(1, "the one dispatched call is metered");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
    }

    /// <summary>
    /// C1 (D1: "before every paid dispatch"): the retry is the run's second paid dispatch, which the caller's
    /// admission reserved nothing for, so the platform cap is checked again with this call's own worst case before
    /// the second call is bought. A refusal answers <c>spend_cap_reached</c> and no second call is made.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_SpendCapRefusedBeforeTheRetry_ReturnsSpendCapReached()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient()
            .Enqueue(TextStep.Ideas, TextCallResult<IdeasV1>.InvalidOutput(Model, Usage, "req-fake", [TextCallInvalidOutput<IdeasV1>.SchemaInvalid]))
            .EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Không bao giờ được gọi")), Usage);
        var guard = new StubBudgetGuard(allowed: false);
        var estimator = new StubIdeaCostEstimator(new UsageBound(0.25m, 1_000, 1_500));
        await using var worker = await StartWorkerAsync(Database, text, count: 1, guard: guard, estimator: estimator);

        var result = await ProposeAsync(worker, run);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SpendCapReached);
        text.Requests.Count.ShouldBe(1, "the refused retry was never dispatched");
        var check = guard.Checks.ShouldHaveSingleItem();
        check.Bound.WorstCaseUsd.ShouldBe(0.25m, "the estimator's bound is what the guard is asked about");
        check.ExcludingRunId.ShouldBe(run.GenerationRunId, "the run's own already-reserved bound is left out of the sum");
        UsageCount(Database, world.UserId).ShouldBe(1, "the refused retry is not metered");
        IdeaCount(Database, world.SeriesId).ShouldBe(0);
    }

    /// <summary>
    /// C5: a bible that carries an entity but no episode summary yet — a young series' normal state — no longer
    /// silences the continuity memory: the approved episodes' titles still travel as the top-level
    /// <c>recent_episodes</c> next to the bible's own keys.
    /// </summary>
    [Fact]
    public async Task IdeaPlanner_BibleWithoutSummaries_StillSendsTheHistory()
    {
        var world = await SeedAsync(Database);
        var historyWorld = new NightTales.Api.IntegrationTests.SeriesBible.World(world.UserId, world.SeriesId);
        await NightTales.Api.IntegrationTests.SeriesBible.SeriesBibleHarness
            .SeedApprovedEpisodeAsync(Database, historyWorld);

        var expectedTitle = NightTales.Api.IntegrationTests.Persistence.SqlProbe.ScalarText(
            Database,
            $"""
             SELECT sv.title FROM episodes e JOIN story_versions sv ON sv.id = e.approved_story_version_id
             WHERE e.series_id = '{Pg(world.SeriesId)}'
             """);
        expectedTitle.ShouldNotBeNullOrWhiteSpace("the seeded history really carries a title");

        await SeedBibleAsync(Database, world, BibleWithEntitiesOnly());

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(Idea("Ý tưởng nhớ các tập trước")), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        using var document = JsonDocument.Parse(text.Requests.ShouldHaveSingleItem().UserDataJson);
        document.RootElement.TryGetProperty("bible", out var bible).ShouldBeTrue("the bible carries entities");
        bible.GetProperty("rules").GetArrayLength().ShouldBe(1);
        document.RootElement.GetProperty("recent_episodes")[0].GetProperty("title").GetString().ShouldBe(expectedTitle);
    }

    /// <summary>
    /// A bible with entities but no episode summary yet (C5): a narrator and one rule, and no
    /// <c>episode_summaries</c> — the state a series is in long before its first episode is approved.
    /// </summary>
    private static SeriesBibleContent BibleWithEntitiesOnly() =>
        new(
            SeriesBibleContent.V1,
            new BibleNarrator("Trần Văn Kể", "kể chuyện đêm", "giọng chậm, ấm"),
            [],
            [],
            [new BibleRule("rl1", "Không bao giờ nói tên thật của người thật.")],
            []);

    /// <summary>
    /// A hand-written spend cap (no mocking library): it records every check and answers what the test set, so a
    /// refusal is exact instead of depending on the platform's real spend and open bounds.
    /// </summary>
    private sealed class StubBudgetGuard(bool allowed) : IBudgetGuard
    {
        /// <summary>What the planner asked, in order: the bound and the run it excluded.</summary>
        internal List<(CostBound Bound, Guid? ExcludingRunId)> Checks { get; } = [];

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null)
        {
            Checks.Add((worstCaseUsd, excludingRunId));

            return allowed
                ? ValueTask.FromResult<ErrorOr<Success>>(Result.Success)
                : ValueTask.FromResult<ErrorOr<Success>>(
                    Error.Failure(ErrorCodes.SpendCapReached, "The stub guard refuses every dispatch."));
        }
    }

    /// <summary>A hand-written estimator, so the retry's re-check does not depend on the price catalogue.</summary>
    /// <param name="bound">The bound the ideas step's guard sees.</param>
    /// <param name="noveltyBound">The bound the judge's guard sees; the ideas bound unless a test sets its own.</param>
    private sealed class StubIdeaCostEstimator(UsageBound bound, UsageBound? noveltyBound = null)
        : IIdeaCostEstimator
    {
        /// <summary>
        /// The bound the novelty judge's guard is checked with (02.8.1-T2): the same as the ideas bound by
        /// default, because most tests care that a refusal propagates, not which step's number it carries.
        /// </summary>
        internal UsageBound NoveltyBound { get; } = noveltyBound ?? bound;

        /// <inheritdoc />
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(bound);

        /// <inheritdoc />
        public Task<ErrorOr<UsageBound>> NoveltyWorstCaseAsync(string priceVersion, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(NoveltyBound);
    }
}
