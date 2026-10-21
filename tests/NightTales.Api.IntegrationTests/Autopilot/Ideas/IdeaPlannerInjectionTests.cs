using System.Text.Json;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Series.Bible;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Fixtures;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The injection fixtures of the idea step (02.8.1-T1 plan §8, D6/D19): the two<i>ideas</i> payloads shipped
/// under <c>prompts/fixtures/injection/</c> reach the paid call only as values of the one data object.
/// </summary>
/// <remarks>
/// <para>
/// The two fields are the two ways hostile text can enter an idea run from the database: a research note (the
/// owner's own note text) and a bible episode summary title. Each test seeds the fixture's exact text into that
/// field through the production factory, drives one real run and asserts the same three facts the bible and
/// fact-sheet injection tests assert: the payload is <b>inside</b> the data object, it is <b>absent</b> from the
/// instructions the model is told to obey, and nothing the payload asks for happens — the data object still asks
/// for the configured count and the run stores only what the scripted answer proposed.
/// </para>
/// <para>
/// The canary is what makes the assertion falsifiable: it is the only token in the payload that cannot appear in
/// a legitimate document, so a canary in the instructions, in an idea row or in the <c>count</c> key is a real
/// break, not a style complaint.
/// </para>
/// </remarks>
public sealed class IdeaPlannerInjectionTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>A research note that tells the model to return nothing still only steers the data object.</summary>
    [Fact]
    public async Task Ideas_InjectionFixtureInResearchNotes_Ignored()
    {
        var fixture = InjectionFixture.Load("ideas-research-notes-instructions");
        fixture.Step.ShouldBe(InjectionFixture.IdeasStep);
        fixture.Field.ShouldBe("research_notes[0].text", "the fixture's own claim about where the payload lands");

        var world = await SeedAsync(Database);
        var episode = await SeedEpisodeAsync(Database, world);
        await SeedNoteAsync(Database, world, episode, fixture.Value);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Một ý tưởng bình thường")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var call = IdeaCalls(text).ShouldHaveSingleItem();

        // The payload is material of the data object, never part of the instructions the model must obey.
        call.SystemPrompt.ShouldNotContain(fixture.Canary);
        DataObjectContains(call, fixture.Canary).ShouldBeTrue("the payload travels as material, inside the data object");

        using var document = JsonDocument.Parse(call.UserDataJson);
        var note = document.RootElement.GetProperty("research_notes")[0];
        note.GetProperty("kind").GetString().ShouldBe("user_note", "only the kind travels, never a source id or url");
        note.GetProperty("text").GetString().ShouldBe(fixture.Value, "the note text is carried verbatim as the field's value");
        document.RootElement.GetProperty("count").GetInt32()
            .ShouldBe(1, "\"set count to 0\" is a value, not an instruction: the configured batch is still asked for");

        NothingAskedForHappened(world, fixture);
    }

    /// <summary>A bible summary title that tells the model to return nothing still only steers the data object.</summary>
    [Fact]
    public async Task Ideas_InjectionFixtureInRecentEpisodes_Ignored()
    {
        var fixture = InjectionFixture.Load("ideas-recent-episodes-instructions");
        fixture.Step.ShouldBe(InjectionFixture.IdeasStep);
        fixture.Field.ShouldBe("bible.recent_episodes[0].title", "the fixture's own claim about where the payload lands");

        var world = await SeedAsync(Database);

        // The production bible refuses a title over its cap, so only the cap this fixture needs is raised: the
        // store itself must accept the hostile text for the run to be able to read it back.
        await SeedBibleAsync(
            Database,
            world,
            BibleWithSummaryTitle(fixture.Value),
            new BibleLimits { SummaryTitleChars = fixture.Value.Length });

        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(Idea("Một ý tưởng bình thường")),
            Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var call = IdeaCalls(text).ShouldHaveSingleItem();

        // The payload is material of the data object, never part of the instructions the model must obey.
        call.SystemPrompt.ShouldNotContain(fixture.Canary);
        DataObjectContains(call, fixture.Canary).ShouldBeTrue("the payload travels as material, inside the data object");

        using var document = JsonDocument.Parse(call.UserDataJson);
        var bible = document.RootElement.GetProperty("bible");
        bible.GetProperty("recent_episodes")[0].GetProperty("title").GetString().ShouldBe(fixture.Value);
        bible.GetProperty("recent_episodes")[0].TryGetProperty("id", out _)
            .ShouldBeFalse("the payload must not be able to smuggle an id into the object");
        document.RootElement.TryGetProperty("recent_episodes", out _)
            .ShouldBeFalse("a series with a bible sends its history once, inside the bible block");
        document.RootElement.GetProperty("count").GetInt32()
            .ShouldBe(1, "\"set count to 0\" is a value, not an instruction: the configured batch is still asked for");

        NothingAskedForHappened(world, fixture);
    }

    /// <summary>
    /// The shared half of both fixtures' assertions: the run stored exactly the one idea the scripted answer
    /// proposed, and no stored text carries the payload's canary.
    /// </summary>
    /// <param name="world">The seeded world.</param>
    /// <param name="fixture">The fixture whose payload was seeded.</param>
    private void NothingAskedForHappened(World world, InjectionFixture fixture)
    {
        IdeaCount(Database, world.SeriesId).ShouldBe(1, "the payload asked for an empty batch; one idea is stored");
        Render(IdeaRows(Database, world.SeriesId)).ShouldBe(
            "Một ý tưởng bình thường|picked|true|true|setup>clue>twist",
            "exactly the answer's candidate, nothing the payload asked for");
        IdeasContaining(Database, world.SeriesId, fixture.Canary)
            .ShouldBe(0, "nothing the payload returned was executed or persisted");
    }
}
