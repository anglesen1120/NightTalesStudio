using System.Net;
using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// QA of 06.2.3-T1's resume origin (acceptance 161) and of the one refusal that must not be over-read (the failure
/// row): a session that already holds bytes resumes at the byte it needs whether the caller positioned a seekable
/// stream or a stream that cannot seek learned the offset from the probe — the two runs must send the same first
/// <c>Content-Range</c> — and a plain <c>401</c>, an answer whose body names no reason, stays
/// <c>youtube_transport</c> instead of marking the channel as needing re-authentication.
/// </summary>
/// <remarks>
/// Both runs go through the real <c>GoogleYouTubeClient</c> and the real HTTP stack of
/// <see cref="YouTubeUploadHarness"/>, so the offset, the chunk ladder and the read pattern are asserted on what the
/// fake recorded and what the stream was asked for — never on a value the client computed internally.
/// </remarks>
[Trait("Category", "YouTube")]
public sealed class YouTubeUploadResumeOriginTests
{
    /// <summary>The byte the seeded session needs — one chunk in, which is where both runs must resume.</summary>
    private const long ResumeOffset = YouTubeUploadHarness.ChunkSizeBytes;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resume_NonSeekableStreamAtTheProbedOffset_StartsWhereTheSeekableRunStarts()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);

        // (1) The seekable run: the caller positioned the stream, so the client reads the offset off the stream and
        // never probes. The first Content-Range proves where the resume landed.
        var seekableSession = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        seekableSession.IsError.ShouldBeFalse($"a session must be creatable: {seekableSession.FirstError}");
        await SeedFirstChunkAsync(seekableSession.Value);

        var seekableChunks = harness.Google.Chunks.Count;
        var seekableProbes = harness.Google.ProbeCalls;

        using (var seekable = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength))
        {
            var positioned = await UploadStreamPositioning.EnsureAtAsync(seekable, ResumeOffset, Ct);
            positioned.IsError.ShouldBeFalse($"positioning a seekable stream cannot fail: {positioned.FirstError}");

            var uploaded = await harness.ResumeUploadAsync(
                seekableSession.Value,
                seekable,
                YouTubeUploadHarness.PayloadLength,
                cancellationToken: Ct);
            uploaded.IsError.ShouldBeFalse($"the remainder must complete the session: {uploaded.FirstError}");
        }

        var seekableStart = FirstChunkStart(harness, seekableChunks);
        seekableStart.ShouldBe(ResumeOffset, "the first chunk of a positioned stream starts at its position");
        harness.Google.ProbeCalls.ShouldBe(seekableProbes, "a seekable stream is resumed without a probe");

        // (2) The same offset through a stream that cannot seek: the caller discards the offset and the client learns
        // where the session stands from the probe, but the first Content-Range must start at exactly the byte the
        // seekable run started at — an origin re-counted from zero would send a chunk the session already holds.
        var nonSeekableSession = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        nonSeekableSession.IsError.ShouldBeFalse($"a session must be creatable: {nonSeekableSession.FirstError}");
        await SeedFirstChunkAsync(nonSeekableSession.Value);

        var nonSeekableChunks = harness.Google.Chunks.Count;
        var probesBefore = harness.Google.ProbeCalls;

        using (var source = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength))
        {
            var nonSeekable = new NonSeekableStream(source);
            var positioned = await UploadStreamPositioning.EnsureAtAsync(nonSeekable, ResumeOffset, Ct);
            positioned.IsError.ShouldBeFalse($"positioning by discarding cannot fail: {positioned.FirstError}");

            // The discard pass reads through its own 256 KiB buffer; the peak is reset so the assertion after the
            // upload is about the upload itself.
            nonSeekable.MaxRequestedBytes.ShouldBeLessThanOrEqualTo(
                UploadStreamPositioning.DiscardBufferBytes,
                "the offset is discarded through one constant-size buffer");
            nonSeekable.ResetPeak();

            var uploaded = await harness.ResumeUploadAsync(
                nonSeekableSession.Value,
                nonSeekable,
                YouTubeUploadHarness.PayloadLength,
                cancellationToken: Ct);
            uploaded.IsError.ShouldBeFalse($"the remainder must complete the session: {uploaded.FirstError}");

            // One rented request buffer, sized to the chunk: the largest single read the client asked of the stream
            // is exactly one chunk — never the whole 3.5 MiB object, and never one buffer per chunk.
            nonSeekable.MaxRequestedBytes.ShouldBe(
                YouTubeUploadHarness.ChunkSizeBytes,
                "the body is streamed through one chunk-sized buffer, not buffered whole");
        }

        FirstChunkStart(harness, nonSeekableChunks).ShouldBe(
            seekableStart,
            "the resume origin must not depend on whether the stream could seek");
        harness.Google.ProbeCalls.ShouldBeGreaterThan(
            probesBefore,
            "a stream that cannot seek needs the probe to learn where the session stands");

        // The chunk ladder the offset implies: every chunk carries exactly the bytes its Content-Range declares, and
        // that is the chunk size for each chunk but the last.
        var chunks = harness.Google.Chunks
            .Skip(nonSeekableChunks)
            .Where(chunk => !chunk.IsProbe)
            .ToList();

        chunks.Count.ShouldBeGreaterThanOrEqualTo(2, "the remainder spans more than one chunk");
        foreach (var chunk in chunks)
        {
            chunk.Received.ShouldBe(chunk.Declared, "the body carries what the Content-Range declares");
            chunk.Received.ShouldBe(
                Math.Min(
                    YouTubeUploadHarness.ChunkSizeBytes,
                    YouTubeUploadHarness.PayloadLength - chunk.Start),
                "each chunk is the chunk size, and the last one the remainder of the object");
        }
    }

    [Fact]
    public async Task Resume_PlainUnauthorizedWithoutAReason_StaysTransport_AndIsNotReauthRequired()
    {
        await using var harness = await YouTubeUploadHarness.StartAsync(cancellationToken: Ct);
        var session = await harness.StartUploadAsync(YouTubeUploadHarness.PayloadLength, cancellationToken: Ct);
        session.IsError.ShouldBeFalse($"a session must be creatable: {session.FirstError}");

        // A token Google no longer knows: the next chunk PUT answers 401 with a body that names no reason
        // (`{"error":{"code":401,"message":"Invalid Credentials"}}`), which is the shape a token expiry produces and
        // the one the job retries instead of declaring the connection expired.
        var revoked = await harness.Client.RevokeAsync(YouTubeUploadHarness.AccessToken, Ct);
        revoked.IsError.ShouldBeFalse($"the fake's revoke endpoint must answer: {revoked.FirstError}");

        using var payload = YouTubeUploadHarness.Payload(YouTubeUploadHarness.PayloadLength);
        var uploaded = await harness.ResumeUploadAsync(
            session.Value,
            payload,
            YouTubeUploadHarness.PayloadLength,
            cancellationToken: Ct);

        uploaded.IsError.ShouldBeTrue("a chunk Google refuses cannot report success");
        uploaded.FirstError.Code.ShouldBe(
            YouTubeErrorCodes.Transport,
            "a plain 401 with no reason is retried, not treated as a dead grant");

        var failure = YouTubeFailure.From(uploaded.FirstError).ShouldNotBeNull();
        failure.HttpStatus.ShouldBe((int)HttpStatusCode.Unauthorized);
        failure.GoogleReason.ShouldBeNull("the body named no reason, which is exactly why reauth is not decided here");
        failure.IsTransport.ShouldBeTrue();

        // The refusal answered a chunk PUT of this session, so it came from the chunk path and not from a client-side
        // guard: the fake records the request before it decides what to answer, and a 401 is answered before a chunk
        // is counted.
        var sessionPath = session.Value.AbsolutePath;
        harness.Google.Calls.Count(call => call.Is("PUT", sessionPath))
            .ShouldBeGreaterThanOrEqualTo(1, "the refused chunk reached the fake");
    }

    /// <summary>
    /// Seeds the first chunk through a raw request, the shape a session resumed after a crash has: the client under
    /// test never sends this, but the fake holds the bytes because of it.
    /// </summary>
    /// <param name="sessionUri">The session the create answered with.</param>
    private static async Task SeedFirstChunkAsync(Uri sessionUri)
    {
        var end = YouTubeUploadHarness.ChunkSizeBytes - 1;
        using var response = await YouTubeUploadHarness.SendRawChunkAsync(
            sessionUri,
            $"bytes 0-{end}/{YouTubeUploadHarness.PayloadLength}",
            YouTubeUploadHarness.ChunkSizeBytes,
            Ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.PermanentRedirect,
            "the fake confirms a partial session with 308");
    }

    /// <summary>The first byte the first chunk request after <paramref name="chunksBefore"/> declared.</summary>
    /// <param name="harness">The harness whose fake recorded the requests.</param>
    /// <param name="chunksBefore">How many chunk records the run before it had left behind.</param>
    private static long FirstChunkStart(YouTubeUploadHarness harness, int chunksBefore) =>
        harness.Google.Chunks.Skip(chunksBefore).First(chunk => !chunk.IsProbe).Start;
}
