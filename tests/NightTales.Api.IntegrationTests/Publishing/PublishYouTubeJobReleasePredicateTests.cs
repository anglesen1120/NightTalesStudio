using System.Net;
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

    [Fact]
    public async Task PreSendReauthOnTheSecondAttempt_ReleasesTheUnitTheRowHolds()
    {
        // F4/SEC-1: `run.Reservation` is the *attempt's* property, so a release decided from it released nothing on any
        // later attempt and the day key stayed on the row forever. The decision now reads the row under the lock: the
        // unit attempt 1 reserved (then gave back no create for) is the one attempt 2 gives back.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        var day = harness.QuotaDayToday();

        harness.Google.Options.TokenStatusCode = HttpStatusCode.InternalServerError;
        harness.Google.Options.TokenError = "server_error";
        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("uploading", "a token 5xx is retryable");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBe(PublicationPayload.FormatQuotaDay(day));

        harness.Google.Options.TokenStatusCode = HttpStatusCode.BadRequest;
        harness.Google.Options.TokenError = "invalid_grant";
        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 2);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        (await harness.QuotaUnitsAsync(day)).ShouldBe(
            0,
            "no create was ever issued for this reservation, so the unit the row still names goes back");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBeEmpty("the key leaves in the same transaction as the release");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.UploadCreateIssuedAtKey)).ShouldBeEmpty();
        harness.Google.SessionCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task PreSendReauthAfterACreateWasIssued_KeepsTheUnit()
    {
        // The mirror: attempt 1 did reach the create, so its unit is spent whatever attempt 2's token answer says. The
        // row keeps both the unit and the key that names it.
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness, tokenExpiresIn: TimeSpan.Zero);
        var day = harness.QuotaDayToday();
        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 1);

        harness.Google.SessionCreateCalls.ShouldBe(1);
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.UploadCreateIssuedAtKey))
            .ShouldNotBeNullOrWhiteSpace();

        // Attempt 1 refreshed the token successfully, so the next attempt only reaches the token endpoint again once
        // that token is near its expiry (`TokenService.RefreshWindow`).
        harness.Clock.Advance(TimeSpan.FromHours(6));
        harness.Google.Scenario = UploadScenario.None;
        harness.Google.Options.TokenStatusCode = HttpStatusCode.BadRequest;
        harness.Google.Options.TokenError = "invalid_grant";
        await RunTolerantlyAsync(harness, world.PublicationId, attempt: 2);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("failed");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.ChannelReauthRequired);
        (await harness.QuotaUnitsAsync(day)).ShouldBe(1, "the create was issued, so YouTube may have counted it");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.QuotaReservedDayKey))
            .ShouldBe(PublicationPayload.FormatQuotaDay(day));
    }

    [Fact]
    public async Task QuotaExceededOnTheSecondAttempt_ReleasesTheRowUnit_AndTheNextDayReservesAgain()
    {
        // A decision that *is* a quota answer releases the unit even though a create went out (YR19: Google counted
        // nothing), and it still reschedules the row — so the next Pacific day must reserve a fresh unit rather than
        // skip on a stale key (SEC-1's second half).
        await using var harness = await StartAsync();
        var world = await SeedReadyAsync(harness);
        var day = harness.QuotaDayToday();
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 100, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(world.UserId, harness.Blobs.DefaultLength, ct: Ct);
        var payload = PublicationPayload.WithInstant(
            PublishYouTubeJobHarness.DefaultPlatformPayload,
            PublicationPayload.UploadCreateIssuedAtKey,
            harness.Clock.UtcNow.AddSeconds(-30));
        payload = PublicationPayload.WithKey(
            payload,
            PublicationPayload.QuotaReservedDayKey,
            PublicationPayload.FormatQuotaDay(day));
        var claimed = await harness.SeedPublicationAsync(
            world.UserId,
            episode.EpisodeId,
            world.Channel.Id,
            status: PublicationStatus.Uploading,
            platformPayloadJson: payload,
            ct: Ct);

        harness.Google.Scenario = UploadScenario.QuotaExceeded();
        await RunTolerantlyAsync(harness, claimed, attempt: 2);

        (await harness.StatusAsync(claimed)).ShouldBe("scheduled", "a quota refusal reschedules the row");
        (await harness.PayloadKeyAsync(claimed, PublicationPayload.QuotaReservedDayKey)).ShouldBeEmpty();
        (await harness.PayloadKeyAsync(claimed, PublicationPayload.UploadCreateIssuedAtKey)).ShouldBeEmpty();
        (await harness.QuotaUnitsAsync(day)).ShouldBe(0, "the quota-class decision gives the row's unit back");
        var wake = await harness.NextAttemptAtAsync(claimed);
        wake.ShouldNotBeNull();

        harness.Google.Scenario = UploadScenario.None;
        harness.Clock.Advance(wake!.Value - harness.Clock.UtcNow + TimeSpan.FromMinutes(1));
        await harness.RunAsync(claimed, Ct);

        (await harness.StatusAsync(claimed)).ShouldBe("processing");
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday())).ShouldBe(
            1,
            "the next day's run reserves its own unit; a stale key would have made it skip the reservation");
    }

    private Task<PublishYouTubeJobHarness> StartAsync() => PublishYouTubeJobHarness.StartAsync(Database, Ct);

    private static async Task RunTolerantlyAsync(PublishYouTubeJobHarness harness, Guid publicationId, int attempt)
    {
        try
        {
            await harness.RunAttemptAsync(publicationId, attempt, maxAttempts: 6, Ct);
        }
        catch (Exception) when (attempt < 6)
        {
            // A retryable failure surfaces to the job; the row is what these tests assert.
        }
    }

    private static async Task<World> SeedReadyAsync(
        PublishYouTubeJobHarness harness,
        TimeSpan? tokenExpiresIn = null)
    {
        var userId = await harness.CreateUserAsync(Ct);
        var channel = tokenExpiresIn is { } expires
            ? await harness.SeedChannelAsync(userId, expiresIn: expires, ct: Ct)
            : await harness.SeedChannelAsync(userId, ct: Ct);
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
