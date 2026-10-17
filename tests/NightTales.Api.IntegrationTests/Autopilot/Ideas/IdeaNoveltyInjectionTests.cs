using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fixtures;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The novelty judge's two shipped prompt-injection fixtures (02 README D6, task Test 19) driven through the real
/// pipeline: the payload of <c>ideas[0].logline</c> and of <c>past_episodes[0].summary</c> is material of the data
/// object — never an instruction — the run still answers on the scripted reading, and nothing of it reaches the
/// run's own log lines (D19).
/// </summary>
/// <remarks>
/// <para>
/// <c>IdeaNoveltyPipelineTests</c> pins the same two fixtures at the model edge (the canary is in the data object
/// and absent from the instructions, and the payload's "instruction" changes no outcome). What is added here is
/// the half that needs a log sink: the payload is absent from every log line the run emits, and the one field the
/// judge writes back — its <c>reason</c> — is cleaned and capped before it becomes the row's server-side data.
/// </para>
/// <para>
/// Nothing reaches a provider: the text client is a <see cref="FakeTextClient"/>, so the fixture's hostile text is
/// consumed by the same code the production path runs, without a paid call.
/// </para>
/// </remarks>
public sealed class IdeaNoveltyInjectionTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// Each shipped novelty fixture, injected into the slot it claims: the payload travels as the data object's
    /// own material (the positive control), the instructions never carry its canary, the scripted reading still
    /// decides the run, and no log line carries the canary.
    /// </summary>
    /// <param name="fixtureId">The fixture id; also the file name under <c>prompts/fixtures/injection/</c>.</param>
    [Theory]
    [InlineData(InjectionFixture.NoveltyLoglineFixture)]
    [InlineData(InjectionFixture.NoveltyPastEpisodeFixture)]
    public async Task Novelty_InjectionPayloadNeverReachesALogLine(string fixtureId)
    {
        var fixture = InjectionFixture.Load(fixtureId);
        fixture.Step.ShouldBe(InjectionFixture.NoveltyStep, "both fixtures are the novelty step's");

        var world = await SeedAsync(
            Database,
            language: string.Equals(fixture.Language, "en", StringComparison.Ordinal) ? Language.En : Language.Vi);
        var inLogline = string.Equals(fixture.Field, "ideas[0].logline", StringComparison.Ordinal);
        if (!inLogline)
        {
            _ = await SeedApprovedEpisodeAsync(Database, world, "Tập cũ có phần tóm tắt bị bơm", fixture.Value);
        }

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(
                inLogline
                    ? IdeaWithLogline("Hải đăng bỏ hoang", fixture.Value)
                    : DistinctIdea("Một ý tưởng mới của series")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(Database, text, count: 1, logs: sink);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse("a payload cannot fail the run");
        var judge = NoveltyCalls(text).ShouldHaveSingleItem();
        judge.SystemPrompt.ShouldNotContain(fixture.Canary, Case.Sensitive, "the payload is data, never an instruction");

        // The corpus summary and the data object both cap a summary at 200 characters, so the payload travels as
        // the same prefix of itself; the canary is inside that prefix.
        var travelled = fixture.Value[..Math.Min(200, fixture.Value.Length)];
        DataObjectContains(judge, travelled).ShouldBeTrue("the hostile text really travelled inside the data object");

        sink.AllText().ShouldNotContain(
            fixture.Canary,
            Case.Sensitive,
            $"D19: the {fixture.Id} payload is model material and may never reach a log line");
        IdeaOutcome(Database, pick.Value.IdeaId).ShouldStartWith("picked|-", Case.Sensitive, "the scripted reading still decided the batch");
    }

    /// <summary>
    /// The one field the judge writes back into the row: a <c>reason</c> that is both oversized and hostile is
    /// stored capped at 300 characters and cleaned of its control character (task file: "cap <c>reason</c> at 300
    /// chars on write"), and — like every other model text — never appears in a log line.
    /// </summary>
    [Fact]
    public async Task Novelty_HostileOversizedJudgeReason_IsStoredCappedAndSanitised()
    {
        const string canary = "ZQX-REASON-CANARY";
        var reason = canary + " " + new string('r', 280) + "\u0007" + new string('s', 40);
        reason.Length.ShouldBeGreaterThan(300, "the fixture reason is over the cap on purpose");

        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng với lý do phản hồi dài")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m, reason: reason)), Usage);
        var sink = new TestLogSink();
        await using var worker = await StartWorkerAsync(Database, text, count: 1, logs: sink);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var stored = JudgeReason(Database, pick.Value.IdeaId);
        stored.ShouldStartWith(canary, Case.Sensitive, "the reason the row keeps is the judge's own text");
        stored.Length.ShouldBe(300, "the cap is the column-side contract the checker owns");
        stored.ShouldNotContain("\u0007", Case.Sensitive, "a control character is dropped before the row is written");
        sink.AllText().ShouldNotContain(canary, Case.Sensitive, "D19: the reason is server-side data, never a log line");
    }

    /// <summary>One stored <c>novelty</c> document's judge reason.</summary>
    /// <param name="database">The class database.</param>
    /// <param name="ideaId">The idea.</param>
    /// <returns>The stored reason, or the empty string when the document carries none.</returns>
    private static string JudgeReason(TestDatabase database, Guid ideaId) =>
        SqlProbe.ScalarText(
            database,
            $"SELECT coalesce(novelty->'judge'->>'reason', '') FROM episode_ideas WHERE id = '{Pg(ideaId)}'");
}
