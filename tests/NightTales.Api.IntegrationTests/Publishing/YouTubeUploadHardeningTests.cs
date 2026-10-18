using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using NightTales.TestKit.Google;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The hardening findings of 06.2.3-T1's review (C1, C6, SEC-4, SEC-5, SEC-6, SEC-7, QA-D1) pinned against
/// <see cref="FakeGoogleServer"/> and the real HTTP stack: one case per finding, each asserting both the typed
/// answer and the request the fake did or did not receive.
/// </summary>
/// <remarks>
/// The composition is <see cref="YouTubeUploadHarness"/>, the same bare host the protocol tests use, so every
/// assertion is about the deployment's own client, transport and options validation. A refusal is asserted through
/// <see cref="YouTubeFailure.From"/> rather than by string: the code alone would not tell "refused before the call"
/// from "the endpoint answered".
/// </remarks>
[Trait("Category", "YouTube")]
public sealed class YouTubeUploadHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The hostile body reason C1 is about: long, and carrying a newline the log must never show.</summary>
    private const string HostileReason = "CANARYREASONMARKER\nsecond line";

    [Fact]
    public async Task ResumeUpload_WithTheStreamAlreadyAtTheEnd_IsRefusedWithoutARequest()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse();

        await using var content = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);
        content.Seek(0, SeekOrigin.End);

        var result = await harness.ResumeUploadAsync(
            session.Value, content, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        // A caller that hands a stream already at its end is a caller bug: no offset is derivable, so nothing is
        // asked of Google and the answer is the non-transport refusal (C6).
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        YouTubeFailure.From(result.FirstError)!.IsTransport.ShouldBeFalse();

        harness.Google.ChunkRequests.ShouldBe(0);
        harness.Google.ProbeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ResumeUpload_WithASessionUriOutsideTheUploadPath_SendsNothing()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse();

        // Same scheme, host and port as the upload root, but another API's path on the shared host: the bearer must
        // never travel there (SEC-4).
        var foreign = new Uri(harness.Google.BaseUrl, "youtube/v3/videos?part=status");

        await using var content = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        var result = await harness.ResumeUploadAsync(
            foreign, content, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        YouTubeFailure.From(result.FirstError)!.IsTransport.ShouldBeFalse();

        harness.Google.ChunkRequests.ShouldBe(0);
        harness.Google.ProbeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ResumeUpload_AgainstAPeerThatAdvancesOneBytePerRequest_StopsAtItsPerChunkBudget()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        // The hostile peer: every chunk PUT is answered with a 308 whose Range moved one byte further along, so
        // forward progress is real but useless — a client without a per-chunk pass budget would ask once per byte.
        harness.Google.Scenario = UploadScenario.None.WithChunkProgressBytes(1);

        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse();

        await using var content = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        var result = await harness.ResumeUploadAsync(
            session.Value, content, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.Transport);

        // One MiB chunk = four 256 KiB steps, the caller's three retries, and the pass that completes it (SEC-5).
        const int expected = (YouTubeUploadHarness.ChunkSizeBytes / 262144) + 3 + 1;

        harness.Google.ChunkRequests.ShouldBe(expected);
    }

    [Fact]
    public async Task RefreshAsync_WithAnAbsurdExpiresIn_AnswersTypedTokensInsteadOfThrowing()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(
            options => options.ExpiresInSeconds = long.MaxValue,
            cancellationToken: Ct);

        var tokens = await harness.Client.RefreshAsync(
            new OAuthClient(FakeGoogleOptions.DefaultClientId, FakeGoogleOptions.DefaultClientSecret),
            "canary-refresh-token",
            Ct);

        // An untrusted lifetime must not reach AddSeconds unbounded: the answer is typed, and the expiry stays
        // inside the ceiling the client enforces (SEC-6).
        tokens.IsError.ShouldBeFalse();
        tokens.Value.ExpiresAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(86400 + 60));
        tokens.Value.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task StartUpload_WithAnUnpairedSurrogateTitle_IsRefusedWithoutASessionCreate()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var request = YouTubeUploadHarness.Request(YouTubeUploadHarness.PayloadLength) with { Title = "\ud800" };

        var result = await harness.Client.StartUploadAsync(
            YouTubeUploadHarness.AccessToken, request, YouTubeUploadHarness.PayloadLength, Ct);

        // The JSON writer cannot encode the surrogate, so the request is refused as a caller bug (SEC-7).
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        YouTubeFailure.From(result.FirstError)!.IsTransport.ShouldBeFalse();

        harness.Google.SessionCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task StartUpload_WithAMimeTypeThatIsNotAMediaType_IsRefusedWithoutASessionCreate()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var request = YouTubeUploadHarness.Request(YouTubeUploadHarness.PayloadLength);
        request = request with { MimeType = "video/mp4\r\nX-Canary: 1" };

        var result = await harness.Client.StartUploadAsync(
            YouTubeUploadHarness.AccessToken, request, YouTubeUploadHarness.PayloadLength, Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        YouTubeFailure.From(result.FirstError)!.IsTransport.ShouldBeFalse();

        harness.Google.SessionCreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ARefusalReasonFromTheBody_NeverReachesALogLine()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        harness.Google.Scenario = UploadScenario.Refusal(HostileReason);

        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse();

        await using var content = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);

        var result = await harness.ResumeUploadAsync(
            session.Value, content, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();

        // The reason is sanitised before it is logged: a body controls its content and its length, so a line may
        // carry neither the marker nor the newline fragment it was built around (C1/SEC-3).
        harness.Logs.ShouldNotContain(line => line.Message.Contains("CANARYREASONMARKER", StringComparison.Ordinal));
        harness.Logs.ShouldNotContain(line => line.Message.Contains("second line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeUpload_WithAStreamShorterThanTheDeclaredLength_LogsTheNumbersAndFails()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse();

        const int shortLength = 4096;
        await using var content = new MemoryStream(new byte[shortLength], writable: false);

        var result = await harness.ResumeUploadAsync(
            session.Value, content, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.Transport);

        // The row and the object disagree: the line names the two numbers — never the bytes — so an operator can
        // see the disagreement without a body ever being logged (QA-D1).
        harness.Logs.ShouldContain(line =>
            line.Message.Contains("youtube.upload.short-stream", StringComparison.Ordinal)
            && line.Message.Contains($"bytesSent={shortLength}", StringComparison.Ordinal)
            && line.Message.Contains($"length={YouTubeUploadHarness.PayloadLength}", StringComparison.Ordinal));
    }
}
