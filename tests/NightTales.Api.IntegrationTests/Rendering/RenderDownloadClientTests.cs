using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;
using NightTales.RenderNode.Contract;
using NightTales.TestKit.Rendering;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The 200 MiB streaming case reads the process-wide allocated-bytes counter to prove the file is never
/// buffered, so no other test may run beside it and allocate into that measurement.
/// </summary>
[CollectionDefinition("Download allocation", DisableParallelization = true)]
public sealed class DownloadAllocationIsolation;

/// <summary>
/// The streamed download of 09.6.2-T1 (09 README BD6, WF08; acceptance checks 5 and 6): the transport is the
/// node client of 11.3.3-T1, so every case runs against a hand-written node (<see cref="SyntheticDownloadHandler"/>)
/// with no socket, no certificate and no GPU (D18). The SHA-256 assertions are the point of the fixture: the
/// 200 MiB case proves the client hashes and writes as it reads instead of buffering the file.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Download allocation")]
public sealed class RenderDownloadClientTests
{
    private const string ApiKeyValue = "alpha-node-key-value";
    private const string TaskId = "0192f0aa-0000-7000-8000-000000000000";
    private const string VideoType = "video/mp4";
    private const string SubRipType = "application/x-subrip; charset=utf-8";
    private const string Sha256Header = "X-Content-SHA256";

    /// <summary>The video ceiling of every test unless it says otherwise (150 KiB).</summary>
    private const long VideoCap = 153_600L;

    /// <summary>The subtitle ceiling of every test unless it says otherwise (1 KiB).</summary>
    private const long SubtitleCap = 1_024L;

    private static readonly Uri NodeUrl = new("https://alpha.example/");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StreamsWithoutBuffering_AndVerifiesSha256()
    {
        const long Size = 200L * 1024 * 1024;
        var expected = SyntheticDownloadContent.ExpectedSha256(Size);
        using var rig = CreateRig(
            _ => Accepted(new SyntheticDownloadContent(Size, VideoType), expected),
            Settings(videoBytes: 512L * 1024 * 1024));

        using var destination = new CountingWriteStream();
        var before = GC.GetTotalAllocatedBytes();

        var result = await rig.Client.DownloadAsync(
            Node(), TaskId, RenderOutputKind.Video, destination, 512L * 1024 * 1024, Ct);

        var allocated = GC.GetTotalAllocatedBytes() - before;

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        result.Value.Bytes.ShouldBe(Size);
        result.Value.Sha256.ShouldBe(expected);
        result.Value.ContentType.ShouldBe(VideoType);
        destination.Bytes.ShouldBe(Size);
        destination.Sha256().ShouldBe(expected, "the reported digest covers exactly the bytes that were written");
        allocated.ShouldBeLessThan(
            Size / 8,
            "a 200 MiB transfer is streamed and hashed incrementally: the file is never held in memory");

        rig.Handler.Requests.ShouldHaveSingleItem().Path.ShouldBe($"/v1/renders/{TaskId}/video");
        rig.Handler.Requests[0].ApiKey.ShouldBe(
            ApiKeyValue,
            "the request travels on the node client the connection factory built (11 README F13)");
    }

    [Theory]
    [InlineData(RenderOutputKind.Video, 4_096L, true, 1_024L)]
    [InlineData(RenderOutputKind.Video, 307_200L, false, VideoCap)]
    [InlineData(RenderOutputKind.Video, 307_200L, true, 1_000_000L)]
    [InlineData(RenderOutputKind.Subtitles, 4_096L, true, 2_048L)]
    [InlineData(RenderOutputKind.Subtitles, 4_096L, false, 1_000_000L)]
    public async Task Download_SizeCapAborts(
        RenderOutputKind kind,
        long bodyBytes,
        bool declaredLength,
        long maxBytes)
    {
        var content = new SyntheticDownloadContent(
            bodyBytes,
            kind == RenderOutputKind.Video ? VideoType : SubRipType,
            declareLength: declaredLength);

        using var rig = CreateRig(_ => Accepted(content, SyntheticDownloadContent.ExpectedSha256(bodyBytes)));
        using var destination = new CountingWriteStream();

        var result = await rig.Client.DownloadAsync(Node(), TaskId, kind, destination, maxBytes, Ct);

        var cap = Math.Min(maxBytes, kind == RenderOutputKind.Video ? VideoCap : SubtitleCap);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_output_too_large");
        failure.Alert.ShouldBeFalse("a size cap breach is the node's own report to act on, not an alert (BD6)");
        destination.Bytes.ShouldBeLessThanOrEqualTo(cap, "nothing past the cap is ever written to the caller's stream");

        if (declaredLength)
        {
            content.BytesServed.ShouldBe(0L, "a declared Content-Length over the cap is refused before any read");
        }
        else
        {
            content.BytesServed.ShouldBeGreaterThan(0L);
            destination.Bytes.ShouldBeLessThan(
                bodyBytes,
                "a body without a Content-Length is aborted by the running count, mid-stream");
        }
    }

    [Fact]
    public async Task Download_RedirectNotFollowed()
    {
        var calls = 0;
        using var rig = CreateRig(_ =>
        {
            Interlocked.Increment(ref calls);

            var response = new HttpResponseMessage(HttpStatusCode.Found)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            };
            response.Headers.Location = new Uri("https://elsewhere.example/v1/renders/x/video");

            return response;
        });

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeTrue("a redirect is a node misconfiguration and is never followed (05 D4)");
        calls.ShouldBe(1, "the Location was never requested: the answer is returned, not followed (11 README D13)");
        destination.Bytes.ShouldBe(0L);
    }

    [Fact]
    public async Task Download_IdleTimeoutAborts()
    {
        // The node answers the headers and then never sends a byte: the idle timer, not the (infinite) client
        // timeout, is what ends it (WF08).
        var content = new SyntheticDownloadContent(1_024L, VideoType, declareLength: false, stallAfterBytes: 0);
        using var rig = CreateRig(_ => Accepted(content), Settings(idleSeconds: 5));

        using var destination = new CountingWriteStream();
        var download = rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        await content.Stalled;
        rig.Time.Advance(TimeSpan.FromSeconds(5));

        var failure = FailureOf(await download);

        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        failure.Alert.ShouldBeFalse();
        destination.Bytes.ShouldBe(0L);
    }

    [Fact]
    public async Task Download_TotalBudgetAborts()
    {
        var content = new SyntheticDownloadContent(1_024L, VideoType, declareLength: false, stallAfterBytes: 0);
        using var rig = CreateRig(
            _ => Accepted(content),
            Settings(idleSeconds: 600, maxMinutes: 1));

        using var destination = new CountingWriteStream();
        var download = rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        await content.Stalled;
        rig.Time.Advance(TimeSpan.FromMinutes(1));

        var failure = FailureOf(await download);

        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
    }

    [Fact]
    public async Task Download_ContentTypeMismatch_IsOutputInvalid()
    {
        var content = new SyntheticDownloadContent(1_024L, "text/html");
        using var rig = CreateRig(_ => Accepted(content));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_output_content_type_mismatch");
        content.BytesServed.ShouldBe(0L, "the media type is checked before the body is read");
        destination.Bytes.ShouldBe(0L);
    }

    [Fact]
    public async Task Download_Subtitles_AcceptsSubRipWithCharset()
    {
        const long Size = 512L;
        var body = new SyntheticDownloadContent(Size, SubRipType);
        using var rig = CreateRig(_ => Accepted(body, SyntheticDownloadContent.ExpectedSha256(Size)));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Subtitles, destination, SubtitleCap, Ct);

        result.IsError.ShouldBeFalse(result.FirstError.Description);
        result.Value.ContentType.ShouldBe(
            "application/x-subrip",
            "the media type is reported without the charset parameter (05 D4)");
        destination.Bytes.ShouldBe(Size);
        rig.Handler.Requests.ShouldHaveSingleItem().Path.ShouldBe($"/v1/renders/{TaskId}/subtitles");
    }

    [Fact]
    public async Task Download_OutputNotReady_409_IsOutputNotReady()
    {
        using var rig = CreateRig(_ => Failed(HttpStatusCode.Conflict, AdapterErrorCodes.OutputNotReady));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        FailureOf(result).Kind.ShouldBe(RenderClientFailureKind.OutputNotReady);
    }

    [Fact]
    public async Task Download_SubtitlesAbsent_404_IsOutputAbsent()
    {
        using var rig = CreateRig(_ => Failed(HttpStatusCode.NotFound, AdapterErrorCodes.OutputAbsent));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Subtitles, destination, SubtitleCap, Ct);

        FailureOf(result).Kind.ShouldBe(RenderClientFailureKind.OutputAbsent);
        destination.Bytes.ShouldBe(0L);
    }

    [Fact]
    public async Task Download_TaskUnknown_404_OnASubmittedTask_IsRenderLost()
    {
        using var rig = CreateRig(_ => Failed(HttpStatusCode.NotFound, AdapterErrorCodes.TaskUnknown));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TerminalFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderLost);
        failure.Alert.ShouldBeFalse("the download only ever runs for a task the node accepted (05 D5)");
    }

    [Fact]
    public async Task Download_CapacityFull_429_IsDeferredWithTheHeader()
    {
        using var rig = CreateRig(_ => Failed(HttpStatusCode.TooManyRequests, AdapterErrorCodes.CapacityFull, retryAfterSeconds: 30));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.Deferred);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30), "the adapter's Retry-After travels into the failure");
    }

    [Fact]
    public async Task Download_Sha256HeaderMismatch_IsOutputInvalid()
    {
        const long Size = 4_096L;
        var content = new SyntheticDownloadContent(Size, VideoType);
        using var rig = CreateRig(_ => Accepted(content, new string('0', 64)));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_output_sha256_mismatch");
        destination.Bytes.ShouldBe(Size, "the hash of a complete body is what was promised and not delivered");
    }

    [Fact]
    public async Task Download_TransportFailureMidStream_IsTransient_AndLeavesPartialBytes()
    {
        const long Served = 8_192L;
        var content = new SyntheticDownloadContent(100_000L, VideoType, failAfterBytes: Served);
        using var rig = CreateRig(_ => Accepted(content));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        destination.Bytes.ShouldBe(
            Served,
            "a transport that breaks mid-body leaves the partial file to the caller, which discards it (05 D4)");
    }

    [Fact]
    public async Task Download_CallerCancellation_Propagates()
    {
        var content = new SyntheticDownloadContent(1_024L, VideoType, declareLength: false, stallAfterBytes: 0);
        using var rig = CreateRig(_ => Accepted(content));

        using var cancellation = new CancellationTokenSource();
        using var destination = new CountingWriteStream();

        var download = rig.Client.DownloadAsync(
            Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, cancellation.Token);

        await content.Stalled;
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await download);
    }

    [Fact]
    public async Task Download_ErrorBody_IsReadBoundedAndMappedByStatus()
    {
        // A 500 whose body is neither small nor a valid envelope: the client reads at most 64 KiB, finds no
        // code, and maps the status (a 5xx is transient, 05 D5).
        var content = new SyntheticDownloadContent(300L * 1024L, "application/json");
        using var rig = CreateRig(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = content });

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        content.BytesServed.ShouldBeLessThanOrEqualTo(64L * 1024L, "an error body is read bounded, never streamed");
    }

    [Fact]
    public async Task Download_UnsafeTaskId_IsRefusedWithoutARequest()
    {
        using var rig = CreateRig(_ => throw new InvalidOperationException("no request may be sent"));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), "../etc/passwd", RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_task_id_invalid");
        rig.Handler.Requests.ShouldBeEmpty("the id is validated before any URL is built");
    }

    [Fact]
    public async Task Download_NonPositiveCap_IsRefusedWithoutARequest()
    {
        using var rig = CreateRig(_ => throw new InvalidOperationException("no request may be sent"));

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, 0L, Ct);

        var failure = FailureOf(result);
        failure.OutcomeCode.ShouldBe(RenderOutcomeCodes.RenderOutputInvalid);
        failure.Detail.ShouldBe("render_download_cap_invalid");
        rig.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Download_LogsCarryNoKeyAndNoBaseUrl()
    {
        using var rig = CreateRig(
            _ => throw new HttpRequestException(
                $"connect to {NodeUrl}v1/renders/{TaskId}/video failed (x-api-key {ApiKeyValue})"),
            captureLogs: true);

        using var destination = new CountingWriteStream();
        var result = await rig.Client.DownloadAsync(Node(), TaskId, RenderOutputKind.Video, destination, VideoCap, Ct);

        var failure = FailureOf(result);
        failure.Kind.ShouldBe(RenderClientFailureKind.TransientFailure);

        rig.Logs.ShouldNotBeEmpty("a failed download is logged so an operator sees the node");
        rig.LogText.ShouldContain("render-node.download.failed");
        rig.LogText.ShouldContain(TaskId);
        rig.LogText.ShouldNotContain(ApiKeyValue, Case.Sensitive);
        rig.LogText.ShouldNotContain("alpha.example", Case.Sensitive);
        rig.LogText.ShouldNotContain("https://", Case.Sensitive);

        result.FirstError.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        result.FirstError.Description.ShouldNotContain(ApiKeyValue, Case.Sensitive);
        result.FirstError.Description.ShouldNotContain("alpha.example", Case.Sensitive);
    }

    private static WorkerNode Node(string name = "alpha") =>
        WorkerNode.Create(name, NodeUrl, capacity: 1, tlsCertSha256: new string('A', 64), gpuModel: null).Value;

    private static RenderClientOptions Settings(
        long videoBytes = VideoCap,
        long subtitleBytes = SubtitleCap,
        int idleSeconds = 60,
        int maxMinutes = 20) => new()
        {
            MaxVideoBytes = videoBytes,
            MaxSubtitleBytes = subtitleBytes,
            Download = new RenderClientOptions.DownloadOptions { IdleSeconds = idleSeconds },
            DownloadMaxMinutes = maxMinutes,
        };

    private static HttpResponseMessage Accepted(HttpContent content, string? sha256 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        if (sha256 is not null)
        {
            response.Headers.TryAddWithoutValidation(Sha256Header, sha256);
        }

        return response;
    }

    private static HttpResponseMessage Failed(HttpStatusCode status, string code, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(
                $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"The adapter refused the download (05 D4 S4).\"}}}}",
                Encoding.UTF8,
                "application/json"),
        };

        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static RenderClientFailure FailureOf(ErrorOr<DownloadResult> result)
    {
        result.IsError.ShouldBeTrue("the call was expected to fail");
        RenderClientErrors.TryGetFailure(result.FirstError, out var failure)
            .ShouldBeTrue($"a download client error always carries its typed failure ({result.FirstError.Code})");

        return failure!;
    }

    private static Rig CreateRig(
        Func<HttpRequestMessage, HttpResponseMessage> answer,
        RenderClientOptions? options = null,
        bool captureLogs = false)
    {
        var handler = new SyntheticDownloadHandler(answer);
        var factory = new SyntheticDownloadNodeFactory(handler, ApiKeyValue);
        var time = new ManualTimeProvider();

        CapturingLoggerProvider? sink = null;
        ILoggerFactory? loggers = null;
        ILogger<RenderDownloadClient> logger = NullLogger<RenderDownloadClient>.Instance;

        if (captureLogs)
        {
            sink = new CapturingLoggerProvider();
            loggers = LoggerFactory.Create(builder => builder.AddProvider(sink));
            logger = loggers.CreateLogger<RenderDownloadClient>();
        }

        var client = new RenderDownloadClient(factory, Options.Create(options ?? Settings()), time, logger);

        return new Rig(client, handler, time, sink, loggers, factory);
    }

    /// <summary>One test's node, clock, logger capture and the client under test; disposing releases them all.</summary>
    private sealed record Rig(
        RenderDownloadClient Client,
        SyntheticDownloadHandler Handler,
        ManualTimeProvider Time,
        CapturingLoggerProvider? Sink,
        ILoggerFactory? Loggers,
        SyntheticDownloadNodeFactory Factory) : IDisposable
    {
        /// <summary>Every captured line, or an empty list when the test does not capture logs.</summary>
        public IReadOnlyList<string> Logs => Sink?.Messages ?? [];

        /// <summary>Every captured line as one text.</summary>
        public string LogText => Sink?.Text ?? string.Empty;

        public void Dispose()
        {
            Loggers?.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>Keeps every rendered log line, so a test can scan it for a leaked key or URL (09 README BD16).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (owner._gate)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
