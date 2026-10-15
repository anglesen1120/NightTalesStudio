using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// <c>POST /api/v1/episodes/{id}/render</c> over the platform spend cap (02 D8): the 429 carries
/// <c>Retry-After</c> and the refusal writes no job, no outbox row and no reservation.
/// </summary>
/// <remarks>
/// The daily cap is set below any render's worst case, so the first budget check breaches. The monthly cap stays
/// far above it, so only the daily window is breached and the retry time is the next UTC midnight.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RenderEpisodeSpendCapTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The smallest positive daily cap the budget options accept: every render breaches it.</summary>
    private const string TinyDailyCap = "0.000001";

    /// <summary>The paragraph keyword the linter lets through.</summary>
    private const string CleanKeyword = "empty wooden pier at dusk";

    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RenderEpisode_SpendCapReached_Returns429_WithRetryAfter()
    {
        var world = await SeedAsync();
        var ledger = new RecordingLedger();
        using var factory = Factory(ledger);
        using var client = factory.ClientFor(world.UserId);
        using var response = await RenderAsync(client, world.EpisodeId, Etag(world.EpisodeId), "render-spend-cap");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, await response.Content.ReadAsStringAsync(Ct));

        // 09:00 to the next UTC midnight is 15 hours.
        ((long)response.Headers.RetryAfter!.Delta!.Value.TotalSeconds).ShouldBe(54000L);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        JsonSerializer.Deserialize<ProblemResponse>(raw, ProblemResponse.Options)!.Code.ShouldBe(ErrorCodes.SpendCapReached);
        raw.ShouldContain("\"retryAfterSeconds\":54000");

        // Refused before the music pick, the job, the outbox and the reservation: nothing is written.
        SqlProbe.Count(Database, $"SELECT count(*) FROM render_jobs WHERE user_id='{world.UserId}'").ShouldBe(0);
        SqlProbe.Count(Database, $"SELECT count(*) FROM outbox_messages WHERE user_id='{world.UserId}'").ShouldBe(0);
        ledger.ReserveCalls.ShouldBe(0);
        SqlProbe.ScalarText(Database, $"SELECT status FROM episodes WHERE id='{world.EpisodeId}'").ShouldBe("story_approved");
    }

    private ApiFactory Factory(RecordingLedger ledger) =>
        new ApiFactory(Database, new FakeClock(Start))
            .WithSetting("Budget:MaxPlatformCostUsdPerDay", TinyDailyCap)
            .WithSetting("Budget:MaxPlatformCostUsdPerMonth", "100000")
            .WithServices(services =>
            {
                services.RemoveAll<ICreditLedger>();
                services.AddSingleton<ICreditLedger>(ledger);
            });

    /// <summary>One owner with a series, an approved episode of short clean text, and the price version the render needs.</summary>
    /// <returns>The seeded ids.</returns>
    private async Task<World> SeedAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            lengthTier: LengthTier.Short,
            status: EpisodeStatus.StoryApproved,
            ct: Ct);
        await SeedPriceVersionAsync(user.Id);
        await SeedApprovedVersionAsync(user.Id, episode.Id);

        return new World(user.Id, episode.Id);
    }

    private async Task SeedApprovedVersionAsync(Guid userId, Guid episodeId)
    {
        var draft = Draft(CleanKeyword);
        var version = StoryVersion.Create(userId, episodeId, 1, StoryVersionSource.Generated, draft,
            StoryContentJson.Write(draft, new JsonArray()), "model-x", "rel=1", null, null, null, null, null, Start);
        await using (var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start)))
        {
            db.StoryVersions.Add(version);
            await db.SaveChangesAsync(Ct);
        }

        SqlProbe.Execute(Database,
            $"UPDATE episodes SET status='story_approved', approved_story_version_id='{version.Id}', approved_at='{Start:O}' WHERE id='{episodeId}'");
    }

    private async Task SeedPriceVersionAsync(Guid userId)
    {
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId }, new FakeClock(Start));
        var rates = new PriceVersionRates(
            new Dictionary<string, PriceRates> { ["gpt-6-luna"] = new(0.10m, 0.01m, 0.125m, 0.50m) },
            new Dictionary<string, ImagePriceRate>(),
            null,
            TextCostCalculator.DefaultSurchargeThresholdTokens).ToJson();
        db.PriceVersions.Add(PriceVersion.Create(
            1,
            Start.AddDays(-1),
            rates,
            PriceVersionJson.WriteLimits(PriceLimits.D4Defaults),
            "render-spend-cap-price-version",
            Start.AddDays(-1)));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The story the render reads: one keyword per segment, and no name for the corpus to match.</summary>
    /// <param name="paragraphKeyword">The paragraph's keyword.</param>
    /// <returns>The content document the version stores.</returns>
    private static StoryContentV1 Draft(string paragraphKeyword) => new(
        "The pier at dawn",
        new StorySegmentV1("The mist drifted over the riverside.", ["quiet riverside morning", "grey mist over water"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", [paragraphKeyword], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>The episode's current strong ETag, read from <c>xmin</c> like the client's own copy.</summary>
    /// <param name="episodeId">The episode to read.</param>
    /// <returns>The quoted tag.</returns>
    private string Etag(Guid episodeId) =>
        ETags.Episode(uint.Parse(
            SqlProbe.ScalarText(Database, $"SELECT xmin::text FROM episodes WHERE id='{episodeId}'"),
            CultureInfo.InvariantCulture));

    private static async Task<HttpResponseMessage> RenderAsync(HttpClient client, Guid episodeId, string? tag, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/episodes/{episodeId}/render");
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private sealed record World(Guid UserId, Guid EpisodeId);

    /// <summary>The credit ledger the spend-cap test runs against: it counts the reserves, so a refusal is proven to reserve nothing.</summary>
    private sealed class RecordingLedger : ICreditLedger
    {
        private readonly Guid _reservation = Guid.CreateVersion7();

        /// <summary>How often the command reserved credits.</summary>
        public int ReserveCalls { get; private set; }

        /// <inheritdoc />
        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(
            Guid userId,
            CreditOperation op,
            Guid referenceId,
            UsageBound bound,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReserveCalls++;

            return ValueTask.FromResult<ErrorOr<ReservationId>>(new ReservationId(_reservation));
        }

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);

        /// <inheritdoc />
        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct) =>
            ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
