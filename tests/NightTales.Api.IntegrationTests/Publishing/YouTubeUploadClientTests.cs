using System.Globalization;
using System.Net;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit.Google;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// 06.2.3-T1's upload half of <see cref="IYouTubeClient"/> against <see cref="FakeGoogleServer"/>: the real
/// <c>GoogleYouTubeClient</c> from the product's own registration, the real HTTP stack, and no database.
/// </summary>
/// <remarks>
/// <para>
/// The composition is <see cref="YouTubeUploadHarness"/>: a bare host that calls the product's
/// <c>AddYouTubePublishing</c> with the same <c>YouTube</c> dictionary the connect harness uses, so the client,
/// its named <c>HttpClient</c> chain and the options validation are the deployment's, while the payloads stay in
/// memory. Payloads span more than one chunk (1 MiB chunks, a 3.5 MiB payload), because the resumable protocol is
/// what these tests are about.
/// </para>
/// <para>
/// The never-log test captures every line of the host — <c>System.Net.Http.*</c> categories at Trace included — and
/// asserts the canaries against all of them, with no category skipped. That is a real check because the product
/// turns the upload client's own request logging off (<c>AddYouTubePublishing</c> filters
/// <c>System.Net.Http.HttpClient.&lt;upload client&gt;</c> to <c>LogLevel.None</c>): a session URI is a capability,
/// and no line of any category may carry it.
/// </para>
/// </remarks>
[Trait("Category", "YouTube")]
public sealed class YouTubeUploadClientTests
{
    /// <summary>The refresh token the refresh tests present; a canary that must never reach a log line.</summary>
    private const string RefreshToken = "canary-upload-refresh-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartUpload_ReturnsSessionUri_AndSendsSyntheticMediaTrue()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var result = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeFalse($"a session create must succeed: {result.FirstError}");
        harness.Google.SessionCreateCalls.ShouldBe(1);

        var create = harness.Google.LastSessionCreate.ShouldNotBeNull();
        create.ContainsSyntheticMedia.ShouldBe(true, "the D8 disclosure is part of every upload");
        create.SelfDeclaredMadeForKids.ShouldBe(false);
        create.UploadType.ShouldBe("resumable");
        create.Part.ShouldBe("snippet,status");
        create.DeclaredLength.ShouldBe(YouTubeUploadHarness.PayloadLength, "the total travels on the create");
        create.UploadContentType.ShouldBe("video/mp4");
        create.PrivacyStatus.ShouldBe("private");
        create.PublishAt.ShouldBeNull();
        create.TagCount.ShouldBe(2);

        result.Value.AbsoluteUri.ShouldBe(create.Location, "the session URI is Google's Location, verbatim");
    }

    [Fact]
    public async Task StartUpload_SetsPrivacyStatusAndPublishAt_WhenProvided()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var publishAt = new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);

        var result = await harness.Client.StartUploadAsync(
            YouTubeUploadHarness.AccessToken,
            YouTubeUploadHarness.Request(YouTubeUploadHarness.PayloadLength, publishAt, "unlisted"),
            YouTubeUploadHarness.PayloadLength,
            Ct);

        result.IsError.ShouldBeFalse($"a session create must succeed: {result.FirstError}");

        var create = harness.Google.LastSessionCreate.ShouldNotBeNull();
        create.PrivacyStatus.ShouldBe("unlisted");
        create.PublishAt.ShouldNotBeNull();
        DateTimeOffset.Parse(create.PublishAt!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ShouldBe(publishAt);
    }

    [Fact]
    public async Task StartUpload_OmitsPublishAt_WhenNull()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var result = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeFalse($"a session create must succeed: {result.FirstError}");
        harness.Google.LastSessionCreate.ShouldNotBeNull().PublishAt.ShouldBeNull();
    }

    [Fact]
    public async Task ResumeUpload_SendsChunksOf256KbMultiple_ExceptLast()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeFalse($"the upload must complete: {result.FirstError}");

        var chunks = harness.Google.Chunks.Where(chunk => !chunk.IsProbe).ToList();
        chunks.Count.ShouldBeGreaterThanOrEqualTo(2, "a payload larger than the chunk size spans several requests");

        for (var index = 0; index < chunks.Count; index++)
        {
            var length = chunks[index].End - chunks[index].Start + 1;

            chunks[index].Start.ShouldBe((long)index * YouTubeUploadHarness.ChunkSizeBytes);
            length.ShouldBeLessThanOrEqualTo(YouTubeUploadHarness.ChunkSizeBytes);

            if (index < chunks.Count - 1)
            {
                length.ShouldBe(YouTubeUploadHarness.ChunkSizeBytes, "every chunk but the last is a full chunk");
                (length % 262144).ShouldBe(0, "G7: every chunk but the last is a multiple of 256 KiB");
            }
        }

        chunks[^1].End.ShouldBe(YouTubeUploadHarness.PayloadLength - 1);
        harness.Google.ProbeCalls.ShouldBe(0, "a seekable stream is resumed without a probe");
        harness.Google.LastVideoId.ShouldBe(result.Value.VideoId);
    }

    [Fact]
    public async Task ResumeUpload_StreamsTheStorageStream_WithoutBufferingItWhole()
    {
        // 25 MB, five times the chunk size: the payload cannot be held, because the store the client reads from
        // throws the moment a read asks for more than one chunk.
        const long Length = 25 * 1024 * 1024;
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(Length, cancellationToken: Ct);

        var store = new CountingBlobStore(Length, YouTubeUploadHarness.ChunkSizeBytes);
        var opened = await store.OpenReadAsync(CountingBlobStore.Bucket, CountingBlobStore.Key, Ct);
        opened.IsError.ShouldBeFalse($"the store hands the object over: {opened.FirstError}");

        var blob = opened.Value;
        var total = UploadTotal.Resolve(blob.Length!.Value, blob);
        total.IsError.ShouldBeFalse($"the row and the object agree: {total.FirstError}");

        var result = await harness.ResumeUploadAsync(session.Value, blob.Content, total.Value, cancellationToken: Ct);

        result.IsError.ShouldBeFalse($"the upload must complete: {result.FirstError}");
        store.OpenReadCalls.ShouldBe(1);
        harness.Google.HeldBytes(session.Value).ShouldBe(Length);
        harness.Google.Chunks.Count(chunk => !chunk.IsProbe).ShouldBe(25);

        blob.Content.Dispose();
        store.Disposals.ShouldBe(1, "the caller owns the stream; the client must never dispose it");
    }

    [Fact]
    public async Task ResumeUpload_ReportsProgressMonotonically()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        var progress = new RecordingProgress();
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        var result = await harness.ResumeUploadAsync(
            session.Value,
            payload,
            YouTubeUploadHarness.PayloadLength,
            progress,
            cancellationToken: Ct);

        result.IsError.ShouldBeFalse($"the upload must complete: {result.FirstError}");

        var values = progress.Values;
        values.Count.ShouldBeGreaterThanOrEqualTo(2, "every confirmed chunk is reported");
        for (var index = 1; index < values.Count; index++)
        {
            values[index].ShouldBeGreaterThanOrEqualTo(values[index - 1], "progress never goes backwards");
        }

        values[^1].ShouldBe(YouTubeUploadHarness.PayloadLength);
    }

    [Fact]
    public async Task ResumeUpload_RetriesAChunkThreeTimes_ThenTransportError()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        harness.Google.Scenario = UploadScenario.PersistentServerErrors;

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.Transport);
        YouTubeFailure.From(result.FirstError)!.IsTransport.ShouldBeTrue();
        harness.Google.ChunkRequests.ShouldBe(
            1 + 3,
            "MaxChunkRetries is 3, so the same chunk is sent four times before the call gives up");
        harness.Google.HeldBytes(session.Value).ShouldBe(0, "a 5xx never advances the session");
    }

    [Fact]
    public async Task ResumeUpload_FourHundredFour_RestartsANewSession()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var firstAttempt = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        harness.Google.Scenario = UploadScenario.ExpiredSession();

        var expired = await harness.ResumeUploadAsync(
            session.Value,
            firstAttempt,
            YouTubeUploadHarness.PayloadLength,
            cancellationToken: Ct);

        expired.IsError.ShouldBeTrue();
        expired.FirstError.Code.ShouldBe(YouTubeErrorCodes.SessionExpired);
        YouTubeFailure.From(expired.FirstError)!.HttpStatus.ShouldBe(404);

        // The 404 belongs to the session, not to the endpoint: a fresh session works against the same fake.
        harness.Google.Scenario = UploadScenario.None;

        var restarted = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        restarted.IsError.ShouldBeFalse($"a new session must be creatable: {restarted.FirstError}");
        harness.Google.SessionCreateCalls.ShouldBe(2);

        using var secondAttempt = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);
        var uploaded = await harness.ResumeUploadAsync(
            restarted.Value,
            secondAttempt,
            YouTubeUploadHarness.PayloadLength,
            cancellationToken: Ct);

        uploaded.IsError.ShouldBeFalse($"the new session must carry the upload: {uploaded.FirstError}");
        harness.Google.LastVideoId.ShouldBe(uploaded.Value.VideoId);
    }

    [Fact]
    public async Task Probe_ReturnsPartialWithNextByte_AndCompleteWithVideoId()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        var empty = await harness.ProbeUploadAsync(session.Value, YouTubeUploadHarness.PayloadLength, Ct);
        empty.Value.ShouldBeOfType<UploadProbe.Partial>().NextByte.ShouldBe(0, "an empty session resumes at zero");

        await SeedChunkAsync(harness, session.Value, 0);
        await SeedChunkAsync(harness, session.Value, YouTubeUploadHarness.ChunkSizeBytes);

        var partial = await harness.ProbeUploadAsync(session.Value, YouTubeUploadHarness.PayloadLength, Ct);
        partial.Value.ShouldBeOfType<UploadProbe.Partial>().NextByte.ShouldBe(2 * YouTubeUploadHarness.ChunkSizeBytes);

        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);
        payload.Seek(2 * YouTubeUploadHarness.ChunkSizeBytes, SeekOrigin.Begin);

        var uploaded = await harness.ResumeUploadAsync(
            session.Value,
            payload,
            YouTubeUploadHarness.PayloadLength,
            cancellationToken: Ct);
        uploaded.IsError.ShouldBeFalse($"the remainder must complete the session: {uploaded.FirstError}");

        var complete = await harness.ProbeUploadAsync(session.Value, YouTubeUploadHarness.PayloadLength, Ct);
        complete.Value.ShouldBeOfType<UploadProbe.Complete>().VideoId.ShouldBe(uploaded.Value.VideoId);
    }

    [Fact]
    public async Task Probe_WhenSessionExpiredAndResume_ReturnsExpired_ThenANewSessionIsCreated()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        harness.Google.Scenario = UploadScenario.ExpiredSession();

        var expired = await harness.ProbeUploadAsync(session.Value, YouTubeUploadHarness.PayloadLength, Ct);

        expired.IsError.ShouldBeFalse("G7: an expired session is an outcome, not a failure");
        expired.Value.ShouldBeOfType<UploadProbe.Expired>();

        harness.Google.Scenario = UploadScenario.None;

        var restarted = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        restarted.IsError.ShouldBeFalse($"a new session must be creatable: {restarted.FirstError}");
        restarted.Value.ShouldNotBe(session.Value);
    }

    [Fact]
    public async Task Resume_DropAfterLastChunk_EndsWithTransportError_AndNoVideoId()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        // F02: Google reads the last chunk and the connection dies before the answer, for every attempt.
        harness.Google.Scenario = UploadScenario.DropAfterLastChunk();

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue("a dropped connection is never a success");
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.Transport);
        YouTubeFailure.From(result.FirstError)!.IsTransport.ShouldBeTrue();
        harness.Google.LastVideoId.ShouldBeNull("the video id never arrived, so the caller must probe or resume");
        harness.Google.HeldBytes(session.Value).ShouldBe(
            3 * YouTubeUploadHarness.ChunkSizeBytes,
            "the three full chunks landed; the dropped final one did not");
        harness.Google.ChunkRequests.ShouldBe(
            3 + 4,
            "three accepted chunks plus the final chunk retried 1 + MaxChunkRetries times");
    }

    [Fact]
    public async Task Resume_QuotaExceeded_MapsToQuotaExceededCode()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        harness.Google.Scenario = UploadScenario.QuotaExceeded();

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.QuotaExceeded);

        var failure = YouTubeFailure.From(result.FirstError).ShouldNotBeNull();
        failure.GoogleReason.ShouldBe(UploadScenario.Reasons.QuotaExceeded);
        failure.HttpStatus.ShouldBe(403);
        failure.IsTransport.ShouldBeFalse("a refusal is not a transport failure: a retry cannot fix it");
    }

    [Fact]
    public async Task Resume_UploadLimitExceeded_MapsToQuotaExceededCode()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        harness.Google.Scenario = UploadScenario.UploadLimitExceeded();

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.QuotaExceeded);
        YouTubeFailure.From(result.FirstError).ShouldNotBeNull()
            .GoogleReason.ShouldBe(UploadScenario.Reasons.UploadLimitExceeded);
    }

    [Fact]
    public async Task Resume_InvalidPublishAt_MapsToUploadFailed_WithGoogleReason()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        harness.Google.Scenario = UploadScenario.InvalidPublishAt();

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);

        var failure = YouTubeFailure.From(result.FirstError).ShouldNotBeNull();
        failure.GoogleReason.ShouldBe(
            UploadScenario.Reasons.InvalidPublishAt,
            "the sanitised reason is preserved for the classifier of 06.2.3-T3");
        failure.HttpStatus.ShouldBe(400);
        failure.IsTransport.ShouldBeFalse();
    }

    [Fact]
    public async Task Resume_ServerError_MapsToTransport()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        // A bounded run of 503s that outlasts the retry budget: the chunk must never advance past it.
        harness.Google.Scenario = UploadScenario.ServerErrors(1 + 3);

        var result = await harness.ResumeUploadAsync(session.Value, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.Transport);
        YouTubeFailure.From(result.FirstError)!.HttpStatus.ShouldBe(503);
        harness.Google.ChunkRequests.ShouldBe(1 + 3);
        harness.Google.ContentRanges.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1,
            "the same chunk was retried; no later chunk was ever attempted");
        harness.Google.HeldBytes(session.Value).ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_InvalidGrant_MapsToReauthRequired()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        harness.Google.Options.TokenError = "invalid_grant";

        var result = await harness.Client.RefreshAsync(PinnedClient(harness), RefreshToken, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.ReauthRequired);
        YouTubeFailure.From(result.FirstError).ShouldNotBeNull()
            .GoogleReason.ShouldBe("invalid_grant");
        harness.Google.IssuedAccessTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refresh_InvalidClient_MapsToClientBroken()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        harness.Google.Options.TokenError = "invalid_client";
        harness.Google.Options.TokenStatusCode = HttpStatusCode.Unauthorized;

        var result = await harness.Client.RefreshAsync(PinnedClient(harness), RefreshToken, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.ClientBroken, "a broken client is not the user's grant");
    }

    [Fact]
    public async Task Refresh_UnauthorizedClient_MapsToClientBroken()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        harness.Google.Options.TokenError = "unauthorized_client";
        harness.Google.Options.TokenStatusCode = HttpStatusCode.BadRequest;

        var result = await harness.Client.RefreshAsync(PinnedClient(harness), RefreshToken, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.ClientBroken);
    }

    [Fact]
    public async Task Refresh_ServerError_MapsToTransport()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        // Google's bare server failure: a 5xx with no OAuth error value (FakeGoogleOptions.TokenStatusCode only
        // applies together with a TokenError), the shape the connect QA tests drive too.
        harness.Google.Options.TokenError = "server_error";
        harness.Google.Options.TokenStatusCode = HttpStatusCode.ServiceUnavailable;

        var result = await harness.Client.RefreshAsync(PinnedClient(harness), RefreshToken, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.Transport);
        YouTubeFailure.From(result.FirstError).ShouldNotBeNull().HttpStatus.ShouldBe(503);
    }

    [Fact]
    public async Task Refresh_RotatesTheAccessTokenFromTheFake()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var result = await harness.Client.RefreshAsync(PinnedClient(harness), RefreshToken, Ct);

        result.IsError.ShouldBeFalse($"the fake rotates a token: {result.FirstError}");
        result.Value.AccessToken.ShouldStartWith("ya29.");
        result.Value.AccessToken.ShouldNotBe(RefreshToken);
        result.Value.RefreshToken.ShouldNotBeNull().ShouldStartWith("ya29.");
        harness.Google.RefreshCalls.ShouldBe(1, "exactly one refresh reached the fake");
        harness.Google.IssuedAccessTokens.ShouldContain(result.Value.AccessToken);

        // The rotated token is a token the surface accepts, which is what makes the rotation real.
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, result.Value.AccessToken, Ct);
        session.IsError.ShouldBeFalse($"the rotated token must be usable: {session.FirstError}");
    }

    [Fact]
    public async Task GetVideos_MapsUploadStatusFailureAndRejectionReason()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        harness.Google.Options.VideoStatuses["canary-video-processed"] = new FakeVideoStatus("processed", PrivacyStatus: "public");
        harness.Google.Options.VideoStatuses["canary-video-failed"] = new FakeVideoStatus("failed", FailureReason: "codec");
        harness.Google.Options.VideoStatuses["canary-video-rejected"] = new FakeVideoStatus(
            "rejected",
            RejectionReason: "copyright");

        var ids = new[]
        {
            "canary-video-processed",
            "canary-video-failed",
            "canary-video-rejected",
            "canary-video-unknown",
        };

        var result = await harness.GetVideosAsync(ids, Ct);

        result.IsError.ShouldBeFalse($"videos.list must answer: {result.FirstError}");
        result.Value.Count.ShouldBe(3, "an id Google does not know is simply absent");
        result.Value.Select(status => status.VideoId).ShouldBe(ids.Take(3));

        var processed = result.Value.Single(status => status.VideoId == "canary-video-processed");
        processed.UploadStatus.ShouldBe("processed");
        processed.PrivacyStatus.ShouldBe("public");
        processed.FailureReason.ShouldBeNull();
        processed.RejectionReason.ShouldBeNull();

        var failed = result.Value.Single(status => status.VideoId == "canary-video-failed");
        failed.UploadStatus.ShouldBe("failed");
        failed.FailureReason.ShouldBe("codec");

        var rejected = result.Value.Single(status => status.VideoId == "canary-video-rejected");
        rejected.UploadStatus.ShouldBe("rejected");
        rejected.RejectionReason.ShouldBe("copyright");

        harness.Google.VideoListCalls.ShouldBe(1, "three ids are one read");
        harness.Google.VideoListIdBatches.Single().ShouldBe(ids);
    }

    [Fact]
    public async Task GetVideos_BatchesAtMostFiftyIds()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var fifty = Enumerable.Range(0, 50).Select(index => $"canary-video-{index}").ToList();

        var accepted = await harness.GetVideosAsync(fifty, Ct);

        accepted.IsError.ShouldBeFalse($"fifty ids are one call: {accepted.FirstError}");
        harness.Google.VideoListCalls.ShouldBe(1);
        harness.Google.VideoListIdBatches.Single().Count.ShouldBe(50);

        var seventy = Enumerable.Range(0, 70).Select(index => $"canary-video-{index}").ToList();

        var refused = await harness.GetVideosAsync(seventy, Ct);

        refused.IsError.ShouldBeTrue();
        refused.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        harness.Google.VideoListCalls.ShouldBe(1, "more than fifty ids is refused before any Google call");

        var empty = await harness.GetVideosAsync([], Ct);

        empty.IsError.ShouldBeTrue("an empty read is a caller bug, not a request Google should see");
        harness.Google.VideoListCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AnyMember_NeverLogsTheTokenTheSessionUriOrTheTitle()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        // A refresh gives the run the token shape a deployment really holds (ya29.*), so a leak would be a leak.
        var refreshed = await harness.Client.RefreshAsync(PinnedClient(harness), RefreshToken, Ct);
        refreshed.IsError.ShouldBeFalse($"the refresh must succeed for this run to be a real one: {refreshed.FirstError}");

        var accessToken = refreshed.Value.AccessToken;
        accessToken.ShouldStartWith("ya29.");

        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, accessToken, Ct);
        session.IsError.ShouldBeFalse($"the session must be created: {session.FirstError}");

        var progress = new RecordingProgress();
        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        var uploaded = await harness.ResumeUploadAsync(
            session.Value,
            payload,
            YouTubeUploadHarness.PayloadLength,
            progress,
            accessToken,
            Ct);
        uploaded.IsError.ShouldBeFalse($"the upload must complete: {uploaded.FirstError}");

        var probed = await harness.ProbeUploadAsync(session.Value, YouTubeUploadHarness.PayloadLength, Ct);
        probed.IsError.ShouldBeFalse($"the probe must answer: {probed.FirstError}");

        harness.Google.Options.VideoStatuses[uploaded.Value.VideoId] = new FakeVideoStatus("processed");
        var videos = await harness.GetVideosAsync([uploaded.Value.VideoId], Ct);
        videos.IsError.ShouldBeFalse($"videos.list must answer: {videos.FirstError}");

        // A failing path logs too: the expiry answers 404 on a session the caller still holds the URI of.
        harness.Google.Scenario = UploadScenario.ExpiredSession();
        using var secondAttempt = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);
        var expired = await harness.ResumeUploadAsync(
            session.Value,
            secondAttempt,
            YouTubeUploadHarness.PayloadLength,
            accessToken: accessToken,
            cancellationToken: Ct);
        expired.IsError.ShouldBeTrue();
        harness.Google.Scenario = UploadScenario.None;

        var lines = harness.Logs;
        lines.ShouldNotBeEmpty("the run must have logged something for this check to mean anything");

        var sessionId = FakeGoogleServer.SessionId(session.Value);

        foreach (var line in lines)
        {
            line.Message.ShouldNotContain("ya29.", Case.Sensitive, $"a token reached the log ({line.Category})");
            line.Message.ShouldNotContain("Bearer ", Case.Sensitive, $"a bearer header reached the log ({line.Category})");
            line.Message.ShouldNotContain(RefreshToken, Case.Sensitive, "the refresh token reached the log");
            line.Message.ShouldNotContain(refreshed.Value.RefreshToken!, Case.Sensitive, "a rotated token reached the log");
            line.Message.ShouldNotContain(harness.Google.Options.ActiveClientSecret, Case.Sensitive, "a client secret reached the log");
            line.Message.ShouldNotContain(YouTubeUploadHarness.Title, Case.Sensitive, "the title reached the log (D17)");
            line.Message.ShouldNotContain(YouTubeUploadHarness.Description, Case.Sensitive, "the description reached the log (D17)");

            // No category is skipped here: the product filters the upload client's own request logging to None, so a
            // session URI in any line — ours or the framework's — is a leak this test must fail on.
            line.Message.ShouldNotContain("upload_id=", Case.Sensitive, $"the session handle reached the log ({line.Category})");
            line.Message.ShouldNotContain(sessionId, Case.Sensitive, $"the session id reached the log ({line.Category})");
            line.Message.ShouldNotContain(
                session.Value.AbsoluteUri,
                Case.Sensitive,
                $"the session URI reached the log ({line.Category})");
        }
    }

    [Fact]
    public void Options_MissingUploadBaseUrl_FailsStartup()
    {
        var exception = Should.Throw<OptionsValidationException>(() => StartHost(string.Empty));

        exception.Message.ShouldContain(
            "YouTube:UploadBaseUrl",
            Case.Sensitive,
            "the failure must name the key, and only the key: an endpoint is configuration (D17)");

        // The control: with the key set the same host boots, so the failure above is the missing upload root.
        Should.NotThrow(() => StartHost("https://www.googleapis.com/upload/youtube/v3/"));
    }

    [Fact]
    public async Task ResumeUpload_FromAPositionedStream_StartsTheContentRangeAtThatOffset()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        // (1) A stream the caller positioned with Seek: the first Content-Range starts exactly there, and a
        // stream that can seek never needs the probe.
        var seekableSession = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        await SeedChunkAsync(harness, seekableSession.Value, 0);
        var probesBefore = harness.Google.ProbeCalls;
        var chunksBefore = harness.Google.Chunks.Count;

        using (var seekable = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength))
        {
            var positioned = await UploadStreamPositioning.EnsureAtAsync(
                seekable,
                YouTubeUploadHarness.ChunkSizeBytes,
                Ct);
            positioned.IsError.ShouldBeFalse($"positioning a seekable stream cannot fail: {positioned.FirstError}");
            seekable.Position.ShouldBe(YouTubeUploadHarness.ChunkSizeBytes);

            var uploaded = await harness.ResumeUploadAsync(
                seekableSession.Value,
                seekable,
                YouTubeUploadHarness.PayloadLength,
                cancellationToken: Ct);
            uploaded.IsError.ShouldBeFalse($"the reminder must complete the session: {uploaded.FirstError}");
        }

        harness.Google.Chunks.Skip(chunksBefore).First(chunk => !chunk.IsProbe).Start
            .ShouldBe(YouTubeUploadHarness.ChunkSizeBytes);
        harness.Google.ProbeCalls.ShouldBe(probesBefore, "a seekable stream is resumed without a probe");

        // (2) The same bytes through a stream that cannot seek: EnsureAtAsync discards the offset with one
        // constant-size buffer, and the client learns the offset from the probe, so the first Content-Range is the
        // same one.
        var nonSeekableSession = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        await SeedChunkAsync(harness, nonSeekableSession.Value, 0);
        probesBefore = harness.Google.ProbeCalls;
        chunksBefore = harness.Google.Chunks.Count;

        using (var source = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength))
        {
            var nonSeekable = new NonSeekableStream(source);
            var positioned = await UploadStreamPositioning.EnsureAtAsync(
                nonSeekable,
                YouTubeUploadHarness.ChunkSizeBytes,
                Ct);
            positioned.IsError.ShouldBeFalse($"positioning by discarding cannot fail: {positioned.FirstError}");

            var peak = nonSeekable.MaxRequestedBytes;
            nonSeekable.ResetPeak();

            peak.ShouldBeLessThanOrEqualTo(
                UploadStreamPositioning.DiscardBufferBytes,
                "the offset is discarded through one 256 KiB buffer, never a buffer sized from the offset");

            var uploaded = await harness.ResumeUploadAsync(
                nonSeekableSession.Value,
                nonSeekable,
                YouTubeUploadHarness.PayloadLength,
                cancellationToken: Ct);
            uploaded.IsError.ShouldBeFalse($"the remainder must complete the session: {uploaded.FirstError}");
        }

        harness.Google.Chunks.Skip(chunksBefore).First(chunk => !chunk.IsProbe).Start
            .ShouldBe(YouTubeUploadHarness.ChunkSizeBytes);
        harness.Google.ProbeCalls.ShouldBeGreaterThan(
            probesBefore,
            "a stream that cannot seek needs the probe to learn where the session stands");
    }

    [Fact]
    public async Task TotalThatDisagreesWithAssetsSizeBytes_FailsBeforeAnyGoogleCall()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var store = new CountingBlobStore(YouTubeUploadHarness.PayloadLength, YouTubeUploadHarness.ChunkSizeBytes);
        var opened = await store.OpenReadAsync(CountingBlobStore.Bucket, CountingBlobStore.Key, Ct);
        var blob = opened.Value;

        // CR-18: the asset row is the authority, and a store that reports another length means the object was
        // replaced or truncated under the row, so the upload fails instead of publishing half a video.
        var disagreeing = UploadTotal.Resolve(blob.Length!.Value + 1, blob);

        disagreeing.IsError.ShouldBeTrue();
        disagreeing.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        YouTubeFailure.From(disagreeing.FirstError)!.IsTransport.ShouldBeFalse();
        harness.Google.SessionCreateCalls.ShouldBe(0, "no session may be created for a total that disagrees");
        harness.Google.Calls.ShouldBeEmpty("no Google request of any kind was made");

        // The control: when the two agree, the caller does create the session.
        var agreeing = UploadTotal.Resolve(blob.Length!.Value, blob);

        agreeing.IsError.ShouldBeFalse($"the row and the object agree: {agreeing.FirstError}");
        var session = await harness.StartUploadAsync(agreeing.Value, cancellationToken: Ct);
        session.IsError.ShouldBeFalse($"the session must be creatable: {session.FirstError}");
        harness.Google.SessionCreateCalls.ShouldBe(1);

        blob.Content.Dispose();
    }

    /// <summary>The OAuth client the connect flow would have pinned to the connection.</summary>
    /// <param name="harness">The harness whose fake holds the client identities.</param>
    private static OAuthClient PinnedClient(YouTubeUploadHarness harness) =>
        new(harness.Google.Options.ActiveClientId, harness.Google.Options.ActiveClientSecret);

    /// <summary>
    /// Seeds one already-uploaded chunk through a raw request, the way a session resumed after a crash looks:
    /// the client under test never sends this, but the fake holds the bytes because of it.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="sessionUri">The session to seed.</param>
    /// <param name="start">The first byte of the chunk.</param>
    private static async Task SeedChunkAsync(YouTubeUploadHarness harness, Uri sessionUri, long start)
    {
        var end = start + YouTubeUploadHarness.ChunkSizeBytes - 1;
        using var response = await YouTubeUploadHarness.SendRawChunkAsync(
            sessionUri,
            $"bytes {start}-{end}/{YouTubeUploadHarness.PayloadLength}",
            YouTubeUploadHarness.ChunkSizeBytes,
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect, "the fake confirms a partial session with 308");
        response.Headers.TryGetValues("Range", out var ranges).ShouldBeTrue("a partial answer names what it holds");
        string.Join(",", ranges!).ShouldBe($"bytes=0-{end}");
    }

    /// <summary>
    /// Boots the product's <c>YouTube</c> composition the way the Api does — the same registration, the same
    /// start-up validation — with one key under test.
    /// </summary>
    /// <param name="uploadBaseUrl">The <c>YouTube:UploadBaseUrl</c> the host runs with.</param>
    private static void StartHost(string uploadBaseUrl)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["YouTube:ActiveClientId"] = "nts-primary",
            ["YouTube:Clients:nts-primary:SecretRef"] = FakeGoogleHosting.PrimarySecretRef,
            [FakeGoogleHosting.PrimarySecretKey] = "canary-client-secret",
            ["YouTube:AuthorizationEndpoint"] = "https://accounts.google.com/o/oauth2/v2/auth",
            ["YouTube:TokenEndpoint"] = "https://oauth2.googleapis.com/token",
            ["YouTube:RevokeEndpoint"] = "https://oauth2.googleapis.com/revoke",
            ["YouTube:ApiBaseUrl"] = "https://www.googleapis.com",
            ["YouTube:UploadBaseUrl"] = uploadBaseUrl,
            ["YouTube:RedirectUri"] = FakeGoogleHosting.RedirectUri,
            ["YouTube:Pkce"] = "true",
            ["YouTube:Scopes:0"] = FakeGoogleHosting.UploadScope,
            ["YouTube:Scopes:1"] = FakeGoogleHosting.ReadonlyScope,
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);

        // The factory the registration documents as a prerequisite, and nothing else: this host is about the section.
        builder.Services.AddHttpClient();
        builder.Services.AddYouTubePublishing(builder.Configuration);

        using var host = builder.Build();
        host.Start();
    }
}
