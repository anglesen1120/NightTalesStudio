using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Stories;

/// <summary>
/// The <c>currentStory.attestation</c> member of <c>GET /api/v1/episodes/{id}</c> against the real host
/// and PostgreSQL (02.3.3-T1, CR-7): the wording and version the approval dialog renders, the tick of the
/// current version, and the null member of an episode that is not true-crime-governed.
/// </summary>
public sealed class AttestationReadModelTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AcceptedAt = Start.AddMinutes(5);
    private static CancellationToken Ct => TestContextToken.Value;

    /// <summary>
    /// Acceptance check 2: a true-crime episode reports <c>required</c>, the configured
    /// <c>textVersion</c> and the clause text of its language with <c>acceptedAt</c> null, and a
    /// non-null instant once the version carries an attested audit row.
    /// </summary>
    [Theory]
    [InlineData(Language.Vi)]
    [InlineData(Language.En)]
    public async Task GetCurrentStory_IncludesAttestationTextAndAcceptedState(Language language)
    {
        var world = await SeedAsync(Topic.TrueCrime, language, withFactSheet: true);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        var shipped = factory.Services.GetRequiredService<IPromptCatalog>().GetAttestation(language);

        using var before = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var attestation = (await ReadOkAsync(before))
            .GetProperty("currentStory").GetProperty("attestation");

        attestation.GetProperty("required").GetBoolean().ShouldBeTrue();
        attestation.GetProperty("textVersion").GetString().ShouldBe("ta.v1");
        attestation.GetProperty("text").GetString().ShouldBe(shipped.Text);
        attestation.GetProperty("text").GetString().ShouldNotBeNullOrWhiteSpace();
        attestation.GetProperty("acceptedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        await AttestAsync(world);

        using var after = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var accepted = (await ReadOkAsync(after)).GetProperty("currentStory").GetProperty("attestation");

        accepted.GetProperty("required").GetBoolean().ShouldBeTrue();
        accepted.GetProperty("textVersion").GetString().ShouldBe("ta.v1");
        accepted.GetProperty("text").GetString().ShouldBe(shipped.Text);
        accepted.GetProperty("acceptedAt").GetDateTimeOffset().ShouldBe(AcceptedAt);
    }

    /// <summary>
    /// The gate's pinned-data rule (SEC-1): the topic governs, and a version that carries a fact sheet
    /// stays governed whatever the series topic says now — a fiction → true-crime patch must not make an
    /// approved story look unattested. An episode that is neither reads as <c>null</c>, never as a false
    /// flag.
    /// </summary>
    [Theory]
    [InlineData(Topic.TrueCrime, true, true)]
    [InlineData(Topic.TrueCrime, false, true)]
    [InlineData(Topic.Ghost, true, true)]
    [InlineData(Topic.Ghost, false, false)]
    public async Task GetCurrentStory_AttestationGovernance_FollowsTopicAndPinnedFactSheet(
        Topic topic, bool withFactSheet, bool governed)
    {
        var world = await SeedAsync(topic, Language.Vi, withFactSheet);
        using var factory = Factory();
        using var client = factory.ClientFor(world.UserId);
        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var attestation = (await ReadOkAsync(response))
            .GetProperty("currentStory").GetProperty("attestation");

        if (!governed)
        {
            attestation.ValueKind.ShouldBe(JsonValueKind.Null);

            return;
        }

        attestation.ValueKind.ShouldBe(JsonValueKind.Object);
        attestation.GetProperty("required").GetBoolean().ShouldBeTrue();
        attestation.GetProperty("textVersion").GetString().ShouldBe("ta.v1");
    }

    /// <summary>
    /// A release whose configured version is not the one the catalog ships (a <c>ta.v2</c> config before
    /// the text lands) still reads as governed and names the configured version, but shows no wording and
    /// never fails the read: the gate is what refuses the tick, the dialog just cannot offer one.
    /// </summary>
    [Fact]
    public async Task GetCurrentStory_AttestationTextOfAnotherVersion_ReadsAsRequiredWithoutText()
    {
        var world = await SeedAsync(Topic.TrueCrime, Language.Vi, withFactSheet: true);
        using var factory = Factory().WithSetting("Story:TrueCrime:AttestationVersion", "ta.v2");
        using var client = factory.ClientFor(world.UserId);

        using var response = await client.GetAsync(EpisodeRoute(world.EpisodeId), Ct);
        var attestation = (await ReadOkAsync(response))
            .GetProperty("currentStory").GetProperty("attestation");

        attestation.GetProperty("required").GetBoolean().ShouldBeTrue();
        attestation.GetProperty("textVersion").GetString().ShouldBe("ta.v2");
        attestation.GetProperty("text").ValueKind.ShouldBe(JsonValueKind.Null);
        attestation.GetProperty("acceptedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private ApiFactory Factory() =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithServices(services =>
            {
                services.RemoveAll<ITextClient>();
                services.AddSingleton<ITextClient>(new FakeTextClient());
            });

    private async Task<World> SeedAsync(Topic topic, Language language, bool withFactSheet)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor)
            .CreateAsync(user.Id, topic: topic, language: language, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, lengthTier: LengthTier.Short, status: EpisodeStatus.StoryReview, ct: Ct);
        var sheet = withFactSheet
            ? await new FactSheetBuilder(Database, actor).CreateAsync(user.Id, episode.Id, Ct)
            : null;

        var draft = Draft();
        var version = StoryVersion.Create(
            user.Id, episode.Id, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=2026.10.1", null, null, sheet?.Id, null, 0.021m, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database,
            $"UPDATE episodes SET current_story_version_id = '{version.Id}' WHERE id = '{episode.Id}'");

        return new World(user.Id, episode.Id, version.Id);
    }

    /// <summary>
    /// The audit row the approval handler writes (D13): the action, the entity the tick belongs to
    /// (<c>episode</c> + the episode id, which is what the read model filters on) and the version, with
    /// the owner as actor and subject. Written in SQL because the row is append-only production data.
    /// </summary>
    private async Task AttestAsync(World world)
    {
        var data = $$"""{"story_version_id":"{{world.VersionId}}","text_version":"ta.v1"}""";

        await Database.ExecuteAsync(
            "INSERT INTO audit_logs (id, actor_user_id, actor_type, subject_user_id, action, category, "
            + "visibility, entity_type, entity_id, data, created_at) "
            + $"VALUES (gen_random_uuid(), '{world.UserId}', 'system', '{world.UserId}', 'episode.true_crime_attested', "
            + $"'episodes', 'user', 'episode', '{world.EpisodeId}', '{data}'::jsonb, '{AcceptedAt:O}'::timestamptz)",
            Ct);
    }

    private static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string EpisodeRoute(Guid episodeId) => $"/api/v1/episodes/{episodeId}";

    /// <summary>A valid Short/vi draft; the attestation member never reads the story body itself.</summary>
    private static StoryContentV1 Draft()
    {
        StorySegmentV1[] paragraphs =
        [
            new("Cảnh sát tìm thấy chiếc xe bỏ lại bên đường vào sáng hôm sau.", ["abandoned car"], [], "setup"),
            new("Hồ sơ vụ án được mở lại sau hai mươi năm.", ["case file"], [], "twist"),
        ];
        var hook = new StorySegmentV1("Vụ án khép lại nhưng câu hỏi vẫn còn nguyên.", ["closed case"], []);
        var ending = new StorySegmentV1("Gia đình nạn nhân chỉ mong một lời giải thích.", ["family portrait"], []);

        return new StoryContentV1(
            "Vụ án bỏ ngỏ", hook, paragraphs, ending,
            "Một vụ án có thật được kể lại từ hồ sơ.", ["true crime"], ["violence"],
            new StoryStructureV1("true_crime", "con đường ven sông", "điều tra lại hồ sơ", "nhân chứng cuối cùng", "third_person"));
    }

    private sealed record World(Guid UserId, Guid EpisodeId, Guid VersionId);
}
