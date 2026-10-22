using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Google;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The session-create claim of 06.2.3-T2 (plan §Performance, SEC-8, QA D3): the Google call runs with no transaction and
/// no publication lock open, one run at a time holds the row's claim, and a claim left by a run that died only blocks
/// the next attempt until it expires.
/// </summary>
/// <remarks>
/// The claim is <c>platform_payload.uploadCreateIssuedAt</c> plus <c>uploadSessionCreateLeaseUntil</c>, both written by
/// the short transaction that precedes the create. The first test proves the transaction is closed at the moment the
/// create is issued (a probe over <see cref="IUnitOfWork"/> and <see cref="IYouTubeClient"/>); the rest drive the lease's
/// two states and its last-attempt behaviour.
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobSessionClaimTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task SessionCreate_IsIssuedWithNoTransactionOpen()
    {
        // QA D3 / PERF-4: the row lock and the pooled connection used to be held across the create, so a cancel on that
        // row waited for Google and a crash rolled the cipher back while Google kept the session. The claim transaction
        // commits first, and the create runs outside any transaction.
        var probe = new TransactionProbe();
        await using var harness = await StartAsync(probe);
        var world = await SeedReadyAsync(harness);

        await harness.RunAsync(world.PublicationId, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("processing");
        harness.Google.SessionCreateCalls.ShouldBe(1);
        probe.Violations.ShouldBe(0, "no session create may run while a transaction — and so a row lock — is open");
        probe.Open.ShouldBe(0, "the run leaves no transaction behind");

        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.UploadCreateIssuedAtKey))
            .ShouldBeEmpty(
                "the marker belongs to the reservation, and `processing` settles the reservation: it leaves with the "
                + "day key (F4's release rule reads it while the row is still uploading)");
        (await harness.PayloadKeyAsync(world.PublicationId, PublicationPayload.UploadSessionLeaseKey))
            .ShouldBeEmpty("the transaction that stored the cipher also ended the claim");
    }

    [Fact]
    public async Task SessionCreateLease_FreshFromAnotherRun_CreatesNothingAndAsksForARetry()
    {
        // A duplicate delivery while a sibling run is calling the create. Opening a second session would spend a second
        // unit and could produce a second video, so this run creates nothing and asks for another attempt.
        await using var harness = await StartAsync();
        var claimed = await SeedClaimedRowAsync(harness, harness.Clock.UtcNow.AddSeconds(30));

        var thrown = await Should.ThrowAsync<PublishAttemptException>(
            () => harness.RunAttemptAsync(claimed, attempt: 1, maxAttempts: 6, Ct));

        thrown.Code.ShouldBe(PublishAttemptException.RetryCode);
        harness.Google.SessionCreateCalls.ShouldBe(0, "the sibling's claim is what stops the second session");
        (await harness.StatusAsync(claimed)).ShouldBe("uploading", "a retry writes no row");
        (await harness.PayloadKeyAsync(claimed, PublicationPayload.UploadSessionLeaseKey))
            .ShouldNotBeNullOrWhiteSpace();
        (await harness.QuotaUnitsAsync(harness.QuotaDayToday())).ShouldBe(1, "the row's one unit is untouched");
    }

    [Fact]
    public async Task SessionCreateLease_StaleFromACrashedRun_OpensTheSession()
    {
        // The run that stamped the claim never observed its create: the lease expires by time, and the next attempt then
        // may open the session itself. Nothing has to be cleaned up by hand.
        await using var harness = await StartAsync();
        var claimed = await SeedClaimedRowAsync(harness, harness.Clock.UtcNow.AddSeconds(-5));

        await harness.RunAsync(claimed, Ct);

        (await harness.StatusAsync(claimed)).ShouldBe("processing");
        harness.Google.SessionCreateCalls.ShouldBe(1, "an expired claim no longer blocks the create");
        (await harness.PayloadKeyAsync(claimed, PublicationPayload.UploadSessionLeaseKey)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionCreateLease_OnTheLastAttempt_DefersTheRowInsteadOfFailingIt()
    {
        // F2's exception to "the last attempt always fails the row": the claim being held means a sibling is uploading
        // exactly this row, so the row is healthy. It is rescheduled at the claim's own expiry — the existing wake, so the
        // dispatcher hands the job back — instead of being failed by a verdict about an upload nothing is wrong with.
        await using var harness = await StartAsync();
        var leaseUntil = harness.Clock.UtcNow.AddSeconds(30);
        var claimed = await SeedClaimedRowAsync(harness, leaseUntil);

        await harness.RunAttemptAsync(claimed, attempt: 6, maxAttempts: 6, Ct);

        (await harness.StatusAsync(claimed)).ShouldBe(
            "scheduled",
            "a held claim defers the row; ending a healthy upload would strand the sibling's video");
        (await harness.ErrorCodeAsync(claimed)).ShouldBe(PublicationErrorCodes.UploadFailed);
        (await harness.NextAttemptAtAsync(claimed)).ShouldBe(leaseUntil, "the wake is the claim's own expiry");
        harness.Google.SessionCreateCalls.ShouldBe(0);
        (await harness.OutboxCountAsync("publication_updated.v1", claimed))
            .ShouldBe(1, "the deferral is a row transition, so the dispatcher schedules it again");
    }

    private Task<PublishYouTubeJobHarness> StartAsync(TransactionProbe? probe = null) =>
        PublishYouTubeJobHarness.StartAsync(
            Database,
            Ct,
            services: probe is null
                ? null
                : collection =>
                {
                    collection.AddSingleton(probe);
                    collection.RemoveAll<IUnitOfWork>();
                    collection.AddScoped<IUnitOfWork>(provider => new ProbingUnitOfWork(
                        ActivatorUtilities.CreateInstance<NightTales.Infrastructure.Persistence.UnitOfWork.EfUnitOfWork>(provider),
                        probe));
                    collection.RemoveAll<IYouTubeClient>();
                    collection.AddScoped<IYouTubeClient>(provider => new ProbingYouTubeClient(
                        ActivatorUtilities.CreateInstance<NightTales.Infrastructure.Publishing.GoogleYouTubeClient>(provider),
                        probe));
                });

    /// <summary>
    /// Seeds one row the way a run that stamped its claim and never came back leaves it: <c>uploading</c>, one unit
    /// reserved, the create marker written and the lease still naming <paramref name="leaseUntil"/>.
    /// </summary>
    /// <param name="harness">The world.</param>
    /// <param name="leaseUntil">When the claim expires.</param>
    private static async Task<Guid> SeedClaimedRowAsync(PublishYouTubeJobHarness harness, DateTimeOffset leaseUntil)
    {
        var day = harness.QuotaDayToday();
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 100, ct: Ct);

        var payload = PublicationPayload.WithInstant(
            PublishYouTubeJobHarness.DefaultPlatformPayload,
            PublicationPayload.UploadCreateIssuedAtKey,
            harness.Clock.UtcNow.AddSeconds(-20));
        payload = PublicationPayload.WithInstant(payload, PublicationPayload.UploadSessionLeaseKey, leaseUntil);
        payload = PublicationPayload.WithKey(
            payload,
            PublicationPayload.QuotaReservedDayKey,
            PublicationPayload.FormatQuotaDay(day));

        var userId = await harness.CreateUserAsync(Ct);
        var channel = await harness.SeedChannelAsync(userId, ct: Ct);
        var episode = await harness.SeedEpisodeAsync(userId, harness.Blobs.DefaultLength, ct: Ct);

        return await harness.SeedPublicationAsync(
            userId,
            episode.EpisodeId,
            channel.Id,
            status: PublicationStatus.Uploading,
            platformPayloadJson: payload,
            ct: Ct);
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
