using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

// Aliased: the enclosing namespace is `…Api.IntegrationTests.SeriesBible`, so `SeriesBible` resolves to that
// namespace; the Domain bible lives under this alias.
using DomainBible = NightTales.Domain.Series.Bible;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The bible as the story writer reads it (02.6.3-T3): the provider's snapshot, the serialised writer data
/// object and the prompt version, through the real DI graph and the embedded prompt catalog.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BibleWriterSnapshotIntegrationTests(TestDatabase database) : IntegrationTest(database)
{
    private const string FirstPersona = "kể chuyện đêm";
    private const string SecondPersona = "giọng kể khác";

    [Fact]
    public async Task BibleSnapshotProvider_EmptyBible_ReturnsNullSnapshot()
    {
        // Both "no row" and "row with empty content" must read as no bible: the writer then gets no bible block.
        var missingRow = await SeriesBibleHarness.SeedSeriesWithoutBibleAsync(Database);
        var emptyRow = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(missingRow.UserId);

        var missing = await ReadWriterBibleAsync(factory, missingRow.UserId, missingRow.SeriesId);
        var empty = await ReadWriterBibleAsync(factory, emptyRow.UserId, emptyRow.SeriesId);

        foreach (var writer in new[] { missing, empty })
        {
            writer.Bible.ShouldBeNull();
            writer.AvoidPlots.ShouldBeEmpty();
            writer.Version.ShouldBe(0);
        }
    }

    [Fact]
    public async Task BibleSnapshotProvider_TenantFilter_OtherUsersSeriesNotFound()
    {
        var owner = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(owner.UserId);
        await PutOkAsync(client, owner.SeriesId, SeriesBibleHarness.Etag(1), SeriesBibleHarness.Content(persona: FirstPersona));

        // Precondition: the owner sees a non-empty bible, so the other user's empty read below is meaningful.
        var ownerView = await ReadWriterBibleAsync(factory, owner.UserId, owner.SeriesId);
        ownerView.Bible.ShouldNotBeNull();

        var other = await SeriesBibleHarness.SeedAsync(Database);
        var otherView = await ReadWriterBibleAsync(factory, other.UserId, owner.SeriesId);

        otherView.Bible.ShouldBeNull();
        otherView.AvoidPlots.ShouldBeEmpty();
        otherView.Version.ShouldBe(0);
    }

    [Fact]
    public async Task StoryWriteRequest_BibleSerialisedUnderBibleKeyAndLast()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        await PutOkAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), SeriesBibleHarness.Content(persona: FirstPersona));

        var view = await AssembleAsync(factory, world.UserId, world.SeriesId);

        using var document = JsonDocument.Parse(view.Prompt.UserDataJson);
        document.RootElement.TryGetProperty("bible", out var bible).ShouldBeTrue();
        bible.GetRawText().ShouldContain(FirstPersona);
        document.RootElement.TryGetProperty("avoid_plots", out _).ShouldBeTrue();

        // In-object order: bible precedes avoid_plots (StoryDataObject.cs:22-30). The row name says "Last"; the code does not put it last.
        view.Prompt.UserDataJson.IndexOf("\"bible\"", StringComparison.Ordinal)
            .ShouldBeLessThan(view.Prompt.UserDataJson.IndexOf("\"avoid_plots\"", StringComparison.Ordinal));

        // The bible travels in the user message, never in the shared system text; the system text is the preset.
        view.Prompt.SystemPrompt.ShouldNotBeNullOrWhiteSpace();
        view.Prompt.SystemPrompt.ShouldNotContain(FirstPersona);
        view.Prompt.PromptCacheKey.ShouldBe("writer:ghost.vi.v1");
    }

    [Fact]
    public async Task BibleVersionChange_DoesNotChangePromptVersion()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        var first = await PutOkAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), SeriesBibleHarness.Content(persona: FirstPersona));
        var before = await AssembleAsync(factory, world.UserId, world.SeriesId);

        var firstTag = SeriesBibleHarness.ResponseEtag(first);
        _ = await PutOkAsync(client, world.SeriesId, firstTag, SeriesBibleHarness.Content(persona: SecondPersona));
        var after = await AssembleAsync(factory, world.UserId, world.SeriesId);

        // Non-vacuity: the bible really changed between the two reads.
        after.Writer.Bible!.Version.ShouldNotBe(before.Writer.Bible!.Version);
        after.Prompt.UserDataJson.ShouldNotBe(before.Prompt.UserDataJson);
        after.Prompt.UserDataJson.ShouldContain(SecondPersona);

        after.Prompt.PromptVersion.ShouldBe(before.Prompt.PromptVersion);
    }

    [Fact]
    public async Task StoryWriteRequest_MaximalBible_StaysWithinDataObjectBudget()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var episodeIds = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            episodeIds.Add(await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world));
        }

        // The maximal bible is written straight to the row: a PUT cannot store it, because the server derives
        // the structure of each summary, which pushes the document past the 65,536-byte cap.
        await SeedMaximalBibleAsync(Database, world.UserId, world.SeriesId, episodeIds);

        using var factory = SeriesBibleHarness.Factory(Database);

        var view = await AssembleAsync(factory, world.UserId, world.SeriesId);

        // Precondition: the fixture is over the writer's 36,000-byte bible budget, so truncation is exercised.
        var bible = view.Writer.Bible.ShouldNotBeNull();
        bible.Truncated.ShouldBeTrue();
        bible.Narrator.ShouldNotBeNull();

        Encoding.UTF8.GetByteCount(view.Prompt.UserDataJson)
            .ShouldBeLessThanOrEqualTo(PromptDataBudget.DefaultMaxDataObjectBytes);
    }

    /// <summary>
    /// Every list at its cap (30/30/30 and 50 summaries), each text at its cap, ASCII so bytes equal chars. It is
    /// built as domain objects and written straight to the row: a PUT cannot store it, because the server derives
    /// the structure of each summary, which pushes the document past the 65,536-byte cap, so the row is seeded directly.
    /// </summary>
    private static DomainBible.SeriesBibleContent MaximalContent(List<Guid> episodeIds)
    {
        var characters = new List<DomainBible.BibleCharacter>();
        for (var i = 1; i <= 30; i++)
        {
            characters.Add(new DomainBible.BibleCharacter(
                $"ch{i}",
                Sized($"Character {i:D2} ", 60),
                Sized($"Description {i:D2} ", 300),
                null));
        }

        var places = new List<DomainBible.BiblePlace>();
        for (var i = 1; i <= 30; i++)
        {
            places.Add(new DomainBible.BiblePlace(
                $"pl{i}",
                Sized($"Place {i:D2} ", 60),
                Sized($"Place description {i:D2} ", 300)));
        }

        var rules = new List<DomainBible.BibleRule>();
        for (var i = 1; i <= 30; i++)
        {
            rules.Add(new DomainBible.BibleRule($"rl{i}", Sized($"Rule {i:D2} ", 300)));
        }

        var summaries = new List<DomainBible.BibleEpisodeSummary>();
        for (var i = 0; i < episodeIds.Count; i++)
        {
            summaries.Add(new DomainBible.BibleEpisodeSummary(
                episodeIds[i],
                Sized($"Episode {i:D2} ", 100),
                Sized($"Summary {i:D2} ", 200),
                StoryStructureTags.Empty,
                SeriesBibleHarness.Start));
        }

        var narrator = new DomainBible.BibleNarrator(
            Sized("Narrator ", 80),
            Sized("Persona ", 1000),
            Sized("Voice notes ", 1000));

        return new DomainBible.SeriesBibleContent(
            DomainBible.SeriesBibleContent.V1,
            narrator,
            characters,
            places,
            rules,
            summaries);
    }

    /// <summary>Writes the maximal content straight to the series' bible row, the way the writer reads it.</summary>
    private static async Task SeedMaximalBibleAsync(
        TestDatabase database,
        Guid userId,
        Guid seriesId,
        List<Guid> episodeIds)
    {
        await using var context = database.CreateAppDbContext(
            new TestCurrentUser { Id = userId },
            new FakeClock(SeriesBibleHarness.Start));
        var bible = await context.SeriesBibles
            .SingleAsync(candidate => candidate.SeriesId == seriesId, SeriesBibleHarness.Ct);
        context.Entry(bible).Property(candidate => candidate.Content).CurrentValue = MaximalContent(episodeIds);
        await context.SaveChangesAsync(SeriesBibleHarness.Ct);
    }

    private static string Sized(string prefix, int length) => prefix.PadRight(length, 'x');

    private static async Task<HttpResponseMessage> PutOkAsync(
        HttpClient client,
        Guid seriesId,
        string ifMatch,
        JsonObject content)
    {
        var response = await SeriesBibleHarness.PutAsync(client, seriesId, ifMatch, content);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        return response;
    }

    private static async Task<BibleForWriter> ReadWriterBibleAsync(ApiFactory factory, Guid userId, Guid seriesId)
    {
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
        return await scope.ServiceProvider.GetRequiredService<IBibleSnapshotProvider>()
            .GetForWriterAsync(seriesId, SeriesBibleHarness.Ct);
    }

    /// <summary>Reads the writer bible of the series and assembles the writer prompt the way the pipeline does.</summary>
    private static async Task<WriterView> AssembleAsync(ApiFactory factory, Guid userId, Guid seriesId)
    {
        using var scope = factory.Services.CreateScope();
        using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(userId);
        var writer = await scope.ServiceProvider.GetRequiredService<IBibleSnapshotProvider>()
            .GetForWriterAsync(seriesId, SeriesBibleHarness.Ct);

        var episodeId = Guid.NewGuid();
        var request = new StoryWriteRequest(
            new RunContext(
                Guid.NewGuid(),
                userId,
                seriesId,
                episodeId,
                new ReservationId(Guid.NewGuid()),
                GenerationTrigger.User,
                1,
                "price-v1"),
            new SeriesSnapshot(seriesId, Topic.Ghost, Language.Vi),
            new EpisodeSnapshot(episodeId, "Một đêm mưa ở làng cũ.", LengthTier.Short, null),
            new LengthTarget(1300, 2080, 20, 32, 6000, 2500, 6000),
            writer.Bible,
            null,
            writer.AvoidPlots,
            null);

        var context = new AssemblerContext(
            Topic.Ghost,
            Language.Vi,
            StoryPresetKeys.For(Topic.Ghost, Language.Vi),
            6000);

        var prompt = scope.ServiceProvider.GetRequiredService<IPromptAssembler>().Assemble(request, context);
        return new WriterView(writer, prompt);
    }

    private sealed record WriterView(BibleForWriter Writer, AssembledPrompt Prompt);
}
