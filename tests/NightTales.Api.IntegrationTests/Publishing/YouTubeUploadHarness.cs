using System.Collections.Concurrent;
using ErrorOr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Publishing;
using NightTales.Infrastructure.Time;
using NightTales.TestKit.Google;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// The composition the upload tests run against (06.2.3-T1): the real <see cref="GoogleYouTubeClient"/> resolved
/// from the product's own registration (<c>AddYouTubePublishing</c>, the same call the Api and the Worker make),
/// the real named <c>HttpClient</c> chain, and <see cref="FakeGoogleServer"/> answering every Google call.
/// </summary>
/// <remarks>
/// <para>
/// A bare host rather than <c>ApiFactory</c>: these tests never touch the database, and the Api host would demand
/// a migrated PostgreSQL fixture for a client that speaks HTTP only. The <c>YouTube</c> configuration is
/// <see cref="FakeGoogleHosting.SettingsFor"/>, the same dictionary the connect harness points an
/// <c>ApiFactory</c> at, and the host runs in Development so the options validator accepts the fake's plain-http
/// loopback endpoints. Start-up validation is the host's own: <c>ValidateOnStart</c> runs on
/// <see cref="StartAsync"/>, which is why a test can assert what refuses to boot.
/// </para>
/// <para>
/// Every log line of the host is captured, <c>System.Net.Http.*</c> categories included (the minimum level is
/// Trace), so <c>AnyMember_NeverLogsTheTokenTheSessionUriOrTheTitle</c> scans what the whole HTTP stack wrote
/// rather than what one component claims to have written. That check has teeth for the framework categories too:
/// the product filters the upload client's own request logging to <c>LogLevel.None</c>, so no line may carry the
/// session URI even at Trace.
/// </para>
/// </remarks>
internal sealed class YouTubeUploadHarness : IAsyncDisposable
{
    /// <summary>The chunk size the tests run with (1 MiB, a multiple of 256 KiB as G7 requires).</summary>
    public const int ChunkSizeBytes = 1024 * 1024;

    /// <summary>The payload length most tests use: three full chunks and a short last one.</summary>
    public const long PayloadLength = (3 * ChunkSizeBytes) + (ChunkSizeBytes / 2);

    /// <summary>The token the tests hand the client; a canary that appears nowhere a log may show (D17).</summary>
    public const string AccessToken = "canary-upload-access-token";

    /// <summary>The title the tests upload; it must never reach a log line.</summary>
    public const string Title = "Canary Upload Episode";

    /// <summary>The description the tests upload; it must never reach a log line either.</summary>
    public const string Description = "canary upload description";

    private readonly IHost _host;
    private readonly CapturingUploadLogs _logs;

    private YouTubeUploadHarness(IHost host, FakeGoogleServer google, CapturingUploadLogs logs)
    {
        _host = host;
        _logs = logs;
        Google = google;
    }

    /// <summary>The fake every Google call of this harness lands on.</summary>
    public FakeGoogleServer Google { get; }

    /// <summary>Every log line the host rendered, from every category.</summary>
    public IReadOnlyCollection<CapturedUploadLog> Logs => _logs.Lines;

    /// <summary>The real client, resolved exactly as a deployment resolves it.</summary>
    public IYouTubeClient Client => _host.Services.GetRequiredService<IYouTubeClient>();

    /// <summary>Starts the fake and then the host that talks to it.</summary>
    /// <param name="configure">Lets a test shape the fake's behaviour before the first request.</param>
    /// <param name="chunkSizeBytes">The host's <c>YouTube:Upload:ChunkSizeBytes</c>.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<YouTubeUploadHarness> StartAsync(
        Action<FakeGoogleOptions>? configure = null,
        int chunkSizeBytes = ChunkSizeBytes,
        CancellationToken cancellationToken = default)
    {
        var options = new FakeGoogleOptions();
        configure?.Invoke(options);

        var google = await FakeGoogleServer.StartAsync(options, cancellationToken).ConfigureAwait(false);
        var logs = new CapturingUploadLogs();

        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(FakeGoogleHosting.SettingsFor(google, chunkSizeBytes));

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        // A real host registers the factory through AddPersistence; AddYouTubePublishing documents it as a
        // prerequisite, so the harness supplies exactly that.
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddYouTubePublishing(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        return new YouTubeUploadHarness(host, google, logs);
    }

    /// <summary>Creates a resumable session for <paramref name="length"/> bytes.</summary>
    /// <param name="length">The object's total size in bytes.</param>
    /// <param name="accessToken">The token to send; the harness's canary token when null.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public Task<ErrorOr<Uri>> StartUploadAsync(
        long length,
        string? accessToken = null,
        CancellationToken cancellationToken = default) =>
        Client.StartUploadAsync(
            accessToken ?? AccessToken,
            Request(length),
            length,
            cancellationToken);

    /// <summary>Resumes <paramref name="sessionUri"/> from the stream's current position.</summary>
    /// <param name="sessionUri">The session the create answered with.</param>
    /// <param name="content">The object stream.</param>
    /// <param name="length">The object's total size in bytes.</param>
    /// <param name="progress">Receives the confirmed byte counts.</param>
    /// <param name="accessToken">The token to send; the harness's canary token when null.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public Task<ErrorOr<UploadResult>> ResumeUploadAsync(
        Uri sessionUri,
        Stream content,
        long length,
        IProgress<long>? progress = null,
        string? accessToken = null,
        CancellationToken cancellationToken = default) =>
        Client.ResumeUploadAsync(
            accessToken ?? AccessToken,
            sessionUri,
            content,
            length,
            progress,
            cancellationToken);

    /// <summary>Asks a session how much it holds.</summary>
    /// <param name="sessionUri">The session the create answered with.</param>
    /// <param name="length">The object's total size in bytes.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public Task<ErrorOr<UploadProbe>> ProbeUploadAsync(
        Uri sessionUri,
        long length,
        CancellationToken cancellationToken = default) =>
        Client.ProbeUploadAsync(AccessToken, sessionUri, length, cancellationToken);

    /// <summary>Reads the processing state of up to fifty videos.</summary>
    /// <param name="ids">The video ids to read.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public Task<ErrorOr<IReadOnlyList<YouTubeVideoStatus>>> GetVideosAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        Client.GetVideosAsync(AccessToken, ids, cancellationToken);

    /// <summary>The <c>videos.insert</c> metadata the tests upload, with the D6 disclosure set.</summary>
    /// <param name="length">The object's total size in bytes.</param>
    /// <param name="publishAt">The scheduled publication instant, when the test wants one.</param>
    /// <param name="privacyStatus">The wire privacy value.</param>
    public static VideoInsertRequest Request(
        long length,
        DateTimeOffset? publishAt = null,
        string privacyStatus = "private") =>
        new(
            Title,
            Description,
            ["nighttales", "audiobook"],
            privacyStatus,
            publishAt,
            ContainsSyntheticMedia: true,
            SelfDeclaredMadeForKids: false,
            length,
            "video/mp4");

    /// <summary>A deterministic payload of <paramref name="length"/> bytes: no zero-filled body, so a truncated
    /// or misaligned chunk would still be a body a store could not have produced.</summary>
    /// <param name="length">The number of bytes.</param>
    public static MemoryStream Payload(long length)
    {
        var bytes = new byte[checked((int)length)];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return new MemoryStream(bytes, writable: false);
    }

    /// <summary>Runs one raw chunk request against a session, the way a test seeds a partially uploaded session.</summary>
    /// <param name="sessionUri">The session URI.</param>
    /// <param name="contentRange">The <c>Content-Range</c> to send.</param>
    /// <param name="bodyLength">How many bytes the body carries.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static async Task<HttpResponseMessage> SendRawChunkAsync(
        Uri sessionUri,
        string contentRange,
        long bodyLength,
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Put, sessionUri);

        request.Content = new ByteArrayContent(new byte[checked((int)bodyLength)]);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AccessToken}");

        // Content-Range is a content header: HttpRequestHeaders refuses it, so adding it there would drop it
        // silently and the fake would answer 400 for a header it never saw.
        if (!request.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange))
        {
            throw new InvalidOperationException("the content headers refused the test's Content-Range");
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
        await Google.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>One captured log line: level, category and the fully rendered text, exception included.</summary>
/// <param name="Level">The level the line was written at.</param>
/// <param name="Category">The logger category.</param>
/// <param name="Message">The rendered message, with the exception text the formatter appended.</param>
internal sealed record CapturedUploadLog(LogLevel Level, string Category, string Message);

/// <summary>Keeps every line the host rendered, whichever category wrote it (Trace and up).</summary>
internal sealed class CapturingUploadLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedUploadLog> _lines = new();

    /// <summary>Everything captured so far, oldest first.</summary>
    public IReadOnlyCollection<CapturedUploadLog> Lines => _lines;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: the lines are the sink.
    }

    /// <summary>One category's sink.</summary>
    /// <param name="category">The category the host asked for.</param>
    /// <param name="lines">The shared queue.</param>
    private sealed class Sink(string category, ConcurrentQueue<CapturedUploadLog> lines) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new CapturedUploadLog(logLevel, category, formatter(state, exception)));
    }
}

/// <summary>
/// A read stream that refuses to be asked for more than <paramref name="maxRequestBytes"/> in one read — the guard
/// that proves a caller streams an object instead of buffering it — records the largest read it was asked for, and
/// counts its own disposals.
/// </summary>
/// <param name="inner">The stream that really holds the bytes.</param>
/// <param name="maxRequestBytes">The largest read the caller may ask for.</param>
internal sealed class GuardedStream(Stream inner, int maxRequestBytes) : Stream
{
    /// <summary>The largest read the caller asked for.</summary>
    public long MaxRequestedBytes { get; private set; }

    /// <summary>How many times this stream was disposed.</summary>
    public int DisposeCount { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        Guard(buffer.Length);

        return inner.Read(buffer);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Guard(buffer.Length);

        return inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("the guard is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("the guard is read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Guard(int count)
    {
        if (count > MaxRequestedBytes)
        {
            MaxRequestedBytes = count;
        }

        if (count > maxRequestBytes)
        {
            throw new InvalidOperationException(
                $"a read of {count} bytes was asked for, larger than the {maxRequestBytes}-byte chunk: the payload was buffered, not streamed.");
        }
    }
}

/// <summary>
/// A stream over the same bytes whose <see cref="CanSeek"/> is false (an S3 response body), recording the largest
/// read it was asked for so a test can prove the positioning pass used one constant-size buffer.
/// </summary>
/// <param name="inner">The seekable stream the bytes come from.</param>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    /// <summary>The largest read the caller asked for since the last <see cref="ResetPeak"/>.</summary>
    public long MaxRequestedBytes { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("the stream cannot seek.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("the stream cannot seek.");
        set => throw new NotSupportedException("the stream cannot seek.");
    }

    /// <summary>Forgets the peak, so a later phase's reads are measured on their own.</summary>
    public void ResetPeak() => MaxRequestedBytes = 0;

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        Track(buffer.Length);

        return inner.Read(buffer);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Track(buffer.Length);

        return inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("the stream cannot seek.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("the stream cannot seek.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("the stream is read-only.");

    private void Track(int count)
    {
        if (count > MaxRequestedBytes)
        {
            MaxRequestedBytes = count;
        }
    }
}

/// <summary>
/// The <see cref="IBlobStore"/> the memory test runs against: one object, opened for reading, whose stream throws
/// when the caller asks for more than one chunk — so "streamed, not buffered" is enforced, not assumed — and which
/// counts how many times the object was opened and how many times its stream was disposed.
/// </summary>
/// <param name="length">The object's length in bytes.</param>
/// <param name="maxRequestBytes">The largest read the client may ask for.</param>
internal sealed class CountingBlobStore(long length, int maxRequestBytes) : IBlobStore
{
    /// <summary>The bucket every call names.</summary>
    public const string Bucket = "nt-media-test";

    /// <summary>The key every call names.</summary>
    public const string Key = "media/canary-user/canary-episode/canary-job/video.mp4";

    private int _openReads;
    private int _disposals;

    /// <summary>How many times <see cref="OpenReadAsync(string, string, CancellationToken)"/> was called.</summary>
    public int OpenReadCalls => Volatile.Read(ref _openReads);

    /// <summary>How many times the stream an open returned was disposed.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _openReads);

        var content = new DisposalCountingStream(
            new GuardedStream(YouTubeUploadHarness.Payload(length), maxRequestBytes),
            () => Interlocked.Increment(ref _disposals));

        return Task.FromResult<ErrorOr<BlobObject>>(new BlobObject(content, "video/mp4", length));
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        long fromByte,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload tests open the whole object.");

    /// <inheritdoc />
    public Task<ErrorOr<BlobInfo>> PutAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload client never writes the store.");

    /// <inheritdoc />
    public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload client never deletes from the store.");

    /// <inheritdoc />
    public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload client never probes the store.");

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the upload client never lists the store.");

    /// <inheritdoc />
    public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
        throw new NotSupportedException("the upload client never presigns.");

    /// <summary>Counts its own disposals while passing every read through.</summary>
    /// <param name="inner">The stream that holds the bytes.</param>
    /// <param name="onDispose">Runs once, when the stream is disposed.</param>
    private sealed class DisposalCountingStream(Stream inner, Action onDispose) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => inner.CanSeek;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException("the stream is read-only.");

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("the stream is read-only.");

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Records the byte counts a caller reports through <see cref="IProgress{T}"/>.</summary>
internal sealed class RecordingProgress : IProgress<long>
{
    private readonly List<long> _values = [];

    /// <summary>Every reported count, in order.</summary>
    public IReadOnlyList<long> Values
    {
        get
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }

    /// <inheritdoc />
    public void Report(long value)
    {
        lock (_values)
        {
            _values.Add(value);
        }
    }
}
