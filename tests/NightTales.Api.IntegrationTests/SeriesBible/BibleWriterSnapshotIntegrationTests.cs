using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

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

        // The bible travels in the user message, never in the shared system text.
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

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        await PutOkAsync(client, world.SeriesId, SeriesBibleHarness.Etag(1), MaximalContent(episodeIds));

        var view = await AssembleAsync(factory, world.UserId, world.SeriesId);

        // Precondition: the fixture is over the writer's 36,000-byte bible budget, so truncation is exercised.
        var bible = view.Writer.Bible.ShouldNotBeNull();
        bible.Truncated.ShouldBeTrue();
        bible.Narrator.ShouldNotBeNull();

        Encoding.UTF8.GetByteCount(view.Prompt.UserDataJson)
            .ShouldBeLessThanOrEqualTo(PromptDataBudget.DefaultMaxDataObjectBytes);
    }

    /// <summary>Every list at its cap (30/30/30 and 50 summaries), each text at its cap, ASCII so bytes equal chars.</summary>
    private static JsonObject MaximalContent(IReadOnlyList<Guid> episodeIds)
    {
        var characters = new JsonArray();
        for (var i = 1; i <= 30; i++)
        {
            characters.Add(SeriesBibleHarness.Character(i, Sized($"Character {i:D2} ", 60), Sized($"Description {i:D2} ", 300)));
        }

        var places = new JsonArray();
        for (var i = 1; i <= 30; i++)
        {
            places.Add(new JsonObject
            {
                ["id"] = $"pl{i}",
                ["name"] = Sized($"Place {i:D2} ", 60),
                ["description"] = Sized($"Place description {i:D2} ", 300),
            });
        }

        var rules = new JsonArray();
        for (var i = 1; i <= 30; i++)
        {
            rules.Add(new JsonObject
            {
                ["id"] = $"rl{i}",
                ["text"] = Sized($"Rule {i:D2} ", 300),
            });
        }

        var summaries = new JsonArray();
        for (var i = 0; i < episodeIds.Count; i++)
        {
            summaries.Add(SeriesBibleHarness.Summary(
                episodeIds[i],
                Sized($"Episode {i:D2} ", 100),
                Sized($"Summary {i:D2} ", 200)));
        }

        return SeriesBibleHarness.Content(
            characters: characters,
            places: places,
            rules: rules,
            summaries: summaries);
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
