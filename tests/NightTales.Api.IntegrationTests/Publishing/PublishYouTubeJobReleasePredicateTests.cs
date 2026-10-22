using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The corrected YR19 release predicate at the handler level (06.2.3-T2 §Design, QA D2): a refusal that answers a
/// session create the run already issued must not give the unit back, because Google may have counted the insert.
/// </summary>
/// <remarks>
/// The shipped reauth and incident tests both fail at the token step, so they only ever exercise the "nothing was
/// sent" half of the predicate. This test drives the other half with
/// <see cref="UploadScenario.SessionCreateAuthFailure"/>: a <c>401</c> + <c>authError</c> at <c>videos.insert</c>, the
/// answer that maps to <c>youtube_reauth_required</c> after <c>run.SessionCreateSent</c> was set.
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobReleasePredicateTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task AuthFailureAtTheSessionCreate_KeepsTheUnitThatBoughtTheAttempt()
    {
        // `videos.insert` was issued, so this run's unit is spent whether or not Google opened a session for it: the row
        // fails `channel_reauth_required` (the owner has to reconnect), and the ledger keeps the unit.
        await using var harness = await PublishYouTubeJobHarness.StartAsync(Database, Ct);
        var world = await SeedReadyAsync(harness);
        harness.Google.Scenario = UploadScenario.SessionCreateAuthFailure();

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        (await harness.ChannelStatusAsync(world.Channel.Id)).ShouldBe("expired");
        harness.Google.SessionCreateCalls.ShouldBe(1, "the insert was issued, which is what the predicate reads");

        var day = harness.QuotaDayToday();
        (await harness.QuotaUnitsAsync(day)).ShouldBe(
            1,
            "a 401 after the create keeps this run's unit: YouTube may have counted the insert");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey)).ShouldBe(
            PublicationPayload.FormatQuotaDay(day),
            "the reserved day stays with the unit that is still spent");
    }

    private static async Task<World> SeedReadyAsync(PublishYouTubeJobHarness harness)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);
        var publicationId = await harness.SeedPublicationAsync(userId, episode.EpisodeId, channel.Id, ct: Ct);

        return new World(userId, channel, episode, publicationId);
    }

    /// <summary>One seeded world: the owner, its connection, the ready episode and the publication under test.</summary>
    /// <param name="UserId">The owner.</param>
    /// <param name="Channel">The seeded connection.</param>
    /// <param name="Episode">The seeded episode with its final video.</param>
    /// <param name="PublicationId">The row the test drives.</param>
    private sealed record World(
        Guid UserId,
        PublishYouTubeJobHarness.SeededChannel Channel,
        PublishYouTubeJobHarness.SeededEpisode Episode,
        Guid PublicationId);
}
