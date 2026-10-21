using System.IO;
using NightTales.Application.Features.Publishing.Upload;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// QA of 06.2.3-T1: every refusal that must happen before a Google call. Each case asserts the specific
/// <c>youtube_upload_failed</c> code, not merely <c>IsError</c>. A refusal is the only producer of that code without
/// an HTTP status, so a request that reached the network and failed would surface as a transport error and fail here.
/// </summary>
[Trait("Category", "YouTube")]
public sealed class YouTubeUploadQaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task StartUpload_NonPositiveLength_IsRefusedBeforeAnyGoogleCall(long length)
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var result = await harness.StartUploadAsync(length, cancellationToken: Ct);

        result.IsError.ShouldBeTrue($"a session for {length} bytes must not be created");
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        harness.Google.SessionCreateCalls.ShouldBe(0);
        harness.Google.Calls.ShouldBeEmpty("no Google request of any kind was made");
    }

    [Fact]
    public async Task ResumeUpload_ZeroLength_IsRefusedBeforeAnyChunkIsSent()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await StartSessionAsync(harness);
        var callsBefore = harness.Google.Calls.Count;

        using var empty = new MemoryStream();
        var result = await harness.ResumeUploadAsync(session, empty, 0, cancellationToken: Ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        harness.Google.ChunkRequests.ShouldBe(0);
        harness.Google.ProbeCalls.ShouldBe(0);
        harness.Google.Calls.Count.ShouldBe(callsBefore, "a refused resume sends no request");
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("scheme")]
    [InlineData("userinfo")]
    public async Task ResumeUpload_SessionUriOutsideTheUploadOrigin_IsRefusedWithoutSendingTheBearer(string variant)
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await StartSessionAsync(harness);
        var foreign = ForeignSessionUri(session, variant);
        var callsBefore = harness.Google.Calls.Count;

        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);
        var result = await harness.ResumeUploadAsync(foreign, payload, YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);

        // A transport error here would mean the request left the process; only the refusal yields this code.
        result.IsError.ShouldBeTrue($"a {variant}-divergent session URI must be refused");
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed, $"the {variant} divergence is refused, not attempted");
        harness.Google.ChunkRequests.ShouldBe(0);
        harness.Google.Calls.Count.ShouldBe(callsBefore, "the bearer is never sent to a session URI off the upload origin");
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("scheme")]
    [InlineData("userinfo")]
    public async Task ProbeUpload_SessionUriOutsideTheUploadOrigin_IsRefusedWithoutSendingTheBearer(string variant)
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await StartSessionAsync(harness);
        var foreign = ForeignSessionUri(session, variant);
        var callsBefore = harness.Google.Calls.Count;

        var result = await harness.ProbeUploadAsync(foreign, YouTubeUploadHarness.PayloadLength, Ct);

        result.IsError.ShouldBeTrue($"a {variant}-divergent session URI must be refused");
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed, $"the {variant} divergence is refused, not attempted");
        harness.Google.ProbeCalls.ShouldBe(0);
        harness.Google.Calls.Count.ShouldBe(callsBefore, "the bearer is never sent to a session URI off the upload origin");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a&part=status")]
    [InlineData("a/b")]
    public async Task GetVideos_MalformedId_IsRefusedWithoutAGoogleCall(string badId)
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var result = await harness.GetVideosAsync([badId], Ct);

        result.IsError.ShouldBeTrue($"the id '{badId}' is not a YouTube id");
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        harness.Google.VideoListCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GetVideos_OneMalformedIdInABatch_RefusesTheWholeBatch()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        var result = await harness.GetVideosAsync(["canary-video-0", "bad id"], Ct);

        result.IsError.ShouldBeTrue("a batch with one malformed id is refused, not silently trimmed");
        result.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        harness.Google.VideoListCalls.ShouldBe(0);
    }

    [Fact]
    public async Task GetVideos_IdAtTheSixtyFourCharacterBoundary_IsAcceptedAndOneOverIsRefused()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var atLimit = new string('a', 64);
        var overLimit = new string('a', 65);

        var accepted = await harness.GetVideosAsync([atLimit], Ct);
        accepted.IsError.ShouldBeFalse($"a 64-character id is the upper bound: {accepted.FirstError}");
        harness.Google.VideoListCalls.ShouldBe(1);

        var refused = await harness.GetVideosAsync([overLimit], Ct);
        refused.IsError.ShouldBeTrue("a 65-character id is over the bound");
        refused.FirstError.Code.ShouldBe(YouTubeErrorCodes.UploadFailed);
        harness.Google.VideoListCalls.ShouldBe(1, "the over-length id is refused before any Google call");
    }

    private static async Task<Uri> StartSessionAsync(YouTubeUploadHarness harness)
    {
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse($"the session must be creatable: {session.FirstError}");
        return session.Value;
    }

    /// <summary>A copy of the real session URI with exactly one component moved off the upload origin.</summary>
    private static Uri ForeignSessionUri(Uri valid, string variant) => variant switch
    {
        "host" => new UriBuilder(valid) { Host = "evil.example" }.Uri,
        "port" => new UriBuilder(valid) { Port = valid.Port + 1 }.Uri,
        "scheme" => new UriBuilder(valid) { Scheme = "https" }.Uri,
        "userinfo" => new UriBuilder(valid) { UserName = "attacker", Password = "canary" }.Uri,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "unknown divergence"),
    };
}
