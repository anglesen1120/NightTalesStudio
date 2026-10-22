using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The exits of one upload run that are not verdicts (SEC-4, CODE-2): a raw exception mid-stream and a live window
/// owned by another run. Both used to end the job as a silent success with the row left in <c>uploading</c> and its
/// streaming lease still stamped, which nothing delivers or wakes again.
/// </summary>
/// <remarks>
/// The raw failure is injected through <see cref="IYouTubeClient"/> — the port the stream itself calls — so the
/// exception lands after the claim transactions have committed, which is the state the defect was about. The job's
/// own catch-all is what a deployment sees, so the assertions read the re-labelled
/// <see cref="PublishAttemptException.InfrastructureCode"/> and the committed row.
/// </remarks>
/// <param name="database">The class database, a migrated copy of the run's template.</param>
public sealed class PublishYouTubeJobRawFailureTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContextToken.Value;

    [Fact]
    public async Task RawExceptionMidStream_ReleasesTheRunClaims_AndTheNextAttemptCompletes()
    {
        // D-1: an exception that is neither a retry request nor a cancellation still ends this run's claims. Without
        // the release the lease outlives the run, the next delivery answers "nothing to do" without throwing, and the
        // job ends as a success — the row would stay `uploading` for good.
        var arm = new ArmSwitch();
        await using var harness = await StartAsync(arm);
        var world = await SeedReadyAsync(harness);

        arm.Armed = true;

        var thrown = await Should.ThrowAsync<PublishAttemptException>(
            () => harness.RunAttemptAsync(world.PublicationId, attempt: 1, maxAttempts: 6, Ct));

        thrown.Code.ShouldBe(
            PublishAttemptException.InfrastructureCode,
            "the job's catch-all re-labels a raw failure; the handler itself rethrows it as it is");

        (await harness.StatusAsync(world.PublicationId)).ShouldBe("uploading", "the session is still resumable");

        var payload = await harness.PlatformPayloadAsync(world.PublicationId);

        PublicationPayload.ReadInstant(payload, PublicationPayload.UploadStreamLeaseUntilKey)
            .ShouldBeNull("a run that threw gives its window back, so the next delivery may stream");
        PublicationPayload.ReadString(payload, PublicationPayload.UploadStreamLeaseOwnerKey).ShouldBeNull();
        PublicationPayload.ReadInstant(payload, PublicationPayload.UploadSessionLeaseKey)
            .ShouldBeNull("the session-create claim ends with the run too");

        arm.Armed = false;

        await harness.RunAttemptAsync(world.PublicationId, attempt: 2, maxAttempts: 6, Ct);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe(
            "processing",
            "the next attempt resumes the stored session instead of silently succeeding");
        harness.Google.SessionCreateCalls.ShouldBe(1, "the row's session is resumed, never replaced");
    }

    [Fact]
    public async Task RawExceptionOnTheLastAttempt_EndsTheRowFailedUploadFailed()
    {
        // CODE-2/SEC-2: the attempts were spent on a failure nobody classified, so the row still gets the verdict plan
        // §7 assigns to spent attempts — otherwise it sits in `uploading` with nothing left to run it.
        var arm = new ArmSwitch();
        await using var harness = await StartAsync(arm);
        var world = await SeedReadyAsync(harness);
        var day = harness.QuotaDayToday();

        arm.Armed = true;

        var thrown = await Should.ThrowAsync<PublishAttemptException>(
            () => harness.RunAttemptAsync(world.PublicationId, attempt: 6, maxAttempts: 6, Ct));

        thrown.Code.ShouldBe(PublishAttemptException.InfrastructureCode);

        (await harness.StatusAsync(world.PublicationId)).ShouldBe(
            "failed",
            "the last attempt owes the row a terminal answer even for a raw failure");
        (await harness.ErrorCodeAsync(world.PublicationId)).ShouldBe(PublicationErrorCodes.UploadFailed);
        PublicationPayload.ReadInstant(
                await harness.PlatformPayloadAsync(world.PublicationId),
                PublicationPayload.UploadStreamLeaseUntilKey)
            .ShouldBeNull("the terminal transition clears the window");
        (await harness.QuotaUnitsAsync(day)).ShouldBe(
            1,
            "the insert reached Google, so the row-based release rule keeps the unit (YR19)");
    }

    [Fact]
    public async Task LiveForeignLease_OnANonLastAttempt_AsksForARetry()
    {
        // SEC-4: a lease with more time left than the wait threshold belongs to a run that is streaming the row. This
        // attempt writes nothing, but it must ask for another one: a decided `Rescheduled` ends the job as a success
        // and nothing would ever deliver the row again.
        await using var harness = await StartAsync(null);
        var owner = Guid.CreateVersion7().ToString();
        var publicationId = await SeedLeasedRowAsync(harness, owner, harness.Clock.UtcNow.AddMinutes(7));

        var thrown = await Should.ThrowAsync<PublishAttemptException>(
            () => harness.RunAttemptAsync(publicationId, attempt: 1, maxAttempts: 6, Ct));

        thrown.Code.ShouldBe(PublishAttemptException.RetryCode, "a healthy sibling's window is a retry, not a success");
        (await harness.StatusAsync(publicationId)).ShouldBe("uploading", "the attempt writes no row");
        harness.Google.SessionCreateCalls.ShouldBe(0);
        PublicationPayload.ReadString(
                await harness.PlatformPayloadAsync(publicationId),
                PublicationPayload.UploadStreamLeaseOwnerKey)
            .ShouldBe(owner, "the sibling's window is untouched");
    }

    [Fact]
    public async Task LiveForeignLease_OnTheLastAttempt_LeavesTheHealthyRowAlone()
    {
        // The other side of the same check: on the last attempt the lease is genuinely live — every shorter one was
        // waited out — so failing the row would orphan the video that streaming run owns. The row is left to it.
        await using var harness = await StartAsync(null);
        var owner = Guid.CreateVersion7().ToString();
        var publicationId = await SeedLeasedRowAsync(harness, owner, harness.Clock.UtcNow.AddMinutes(7));

        await harness.RunAttemptAsync(publicationId, attempt: 6, maxAttempts: 6, Ct);

        (await harness.StatusAsync(publicationId)).ShouldBe("uploading", "a healthy stream is never failed by a duplicate");
        (await harness.ErrorCodeAsync(publicationId)).ShouldBeEmpty();
        harness.Google.SessionCreateCalls.ShouldBe(0);
        PublicationPayload.ReadString(
                await harness.PlatformPayloadAsync(publicationId),
                PublicationPayload.UploadStreamLeaseOwnerKey)
            .ShouldBe(owner);
    }

    private Task<PublishYouTubeJobHarness> StartAsync(ArmSwitch? arm) =>
        PublishYouTubeJobHarness.StartAsync(
            Database,
            Ct,
            services: arm is null
                ? null
                : collection =>
                {
                    collection.AddSingleton(arm);
                    collection.RemoveAll<IYouTubeClient>();
                    collection.AddScoped<IYouTubeClient>(provider => new RawThrowingYouTubeClient(
                        ActivatorUtilities.CreateInstance<NightTales.Infrastructure.Publishing.GoogleYouTubeClient>(provider),
                        provider.GetRequiredService<ArmSwitch>()));
                });

    /// <summary>
    /// Seeds one row the way a sibling run leaves it while it streams: <c>uploading</c>, one unit reserved, and a
    /// streaming lease owned by <paramref name="owner"/> that is live until <paramref name="leaseUntil"/>.
    /// </summary>
    /// <param name="harness">The world.</param>
    /// <param name="owner">The id the lease names.</param>
    /// <param name="leaseUntil">When the lease expires.</param>
    private static async Task<Guid> SeedLeasedRowAsync(
        PublishYouTubeJobHarness harness,
        string owner,
        DateTimeOffset leaseUntil)
    {
        var day = harness.QuotaDayToday();
        await harness.SeedQuotaAsync(day, unitsUsed: 1, unitsLimit: 100, ct: Ct);

        var payload = PublicationPayload.WithKey(
            PublishYouTubeJobHarness.DefaultPlatformPayload,
            PublicationPayload.QuotaReservedDayKey,
            PublicationPayload.FormatQuotaDay(day));
        payload = PublicationPayload.WithInstant(
            PublicationPayload.WithKey(payload, PublicationPayload.UploadStreamLeaseOwnerKey, owner),
            PublicationPayload.UploadStreamLeaseUntilKey,
            leaseUntil);

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
    /// <param name="Episode">The episode with its final video.</param>
    /// <param name="PublicationId">The row the test drives.</param>
    private sealed record World(
        Guid UserId,
        PublishYouTubeJobHarness.SeededChannel Channel,
        PublishYouTubeJobHarness.SeededEpisode Episode,
        Guid PublicationId);

    /// <summary>Whether the decorator answers with a raw failure instead of streaming.</summary>
    private sealed class ArmSwitch
    {
        /// <summary>Set by the test before the attempt that must fail.</summary>
        public bool Armed { get; set; }
    }

    /// <summary>
    /// Forwards every port member to the real client, except the stream call: when armed it throws a plain
    /// <see cref="InvalidOperationException"/>, the shape of a provider or invariant failure nobody classified.
    /// </summary>
    private sealed class RawThrowingYouTubeClient(IYouTubeClient inner, ArmSwitch arm) : IYouTubeClient
    {
        /// <inheritdoc />
        public string BuildAuthorizationUrl(
            OAuthClient client,
            OAuthAuthorizationRequest request,
            string state,
            string? codeChallenge) => inner.BuildAuthorizationUrl(client, request, state, codeChallenge);

        /// <inheritdoc />
        public Task<ErrorOr<OAuthTokens>> ExchangeCodeAsync(
            OAuthClient client,
            string code,
            string? codeVerifier,
            string redirectUri,
            CancellationToken ct) => inner.ExchangeCodeAsync(client, code, codeVerifier, redirectUri, ct);

        /// <inheritdoc />
        public Task<ErrorOr<OAuthTokens>> RefreshAsync(OAuthClient client, string refreshToken, CancellationToken ct) =>
            inner.RefreshAsync(client, refreshToken, ct);

        /// <inheritdoc />
        public Task<ErrorOr<RevokeOutcome>> RevokeAsync(string token, CancellationToken ct) =>
            inner.RevokeAsync(token, ct);

        /// <inheritdoc />
        public Task<ErrorOr<GoogleChannel>> GetMyChannelAsync(string accessToken, CancellationToken ct) =>
            inner.GetMyChannelAsync(accessToken, ct);

        /// <inheritdoc />
        public Task<ErrorOr<Uri>> StartUploadAsync(
            string accessToken,
            VideoInsertRequest request,
            long length,
            CancellationToken ct) => inner.StartUploadAsync(accessToken, request, length, ct);

        /// <inheritdoc />
        public Task<ErrorOr<UploadResult>> ResumeUploadAsync(
            string accessToken,
            Uri sessionUri,
            Stream content,
            long length,
            IProgress<long>? progress,
            CancellationToken ct) => arm.Armed
            ? throw new InvalidOperationException("canary: the stream failed outside the platform's error vocabulary")
            : inner.ResumeUploadAsync(accessToken, sessionUri, content, length, progress, ct);

        /// <inheritdoc />
        public Task<ErrorOr<UploadProbe>> ProbeUploadAsync(
            string accessToken,
            Uri sessionUri,
            long length,
            CancellationToken ct) => inner.ProbeUploadAsync(accessToken, sessionUri, length, ct);

        /// <inheritdoc />
        public Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
            string accessToken,
            IReadOnlyList<string> ids,
            CancellationToken ct) => inner.GetVideosAsync(accessToken, ids, ct);
    }
}
