using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Rendering;
using NightTales.Infrastructure.Rendering;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// A hand-written node transport for the 09.6.2-T1 download tests (no mocking library, 09 README D13): every
/// request is recorded and answered by the caller's own delegate, which builds a
/// <see cref="SyntheticDownloadContent"/> — a body generated as it is read, so a 200 MiB transfer costs 200 MiB
/// of hashing and never 200 MiB of memory.
/// </summary>
/// <remarks>
/// A fake handler never follows a redirect on its own (that is <c>SocketsHttpHandler</c>'s job, and production
/// turns it off for a node client, 11 README D13), so a 302 the delegate returns is handed straight back to the
/// client under test.
/// </remarks>
/// <param name="answer">The answer for one request; it owns any body it puts on the response.</param>
public sealed class SyntheticDownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
    : HttpMessageHandler
{
    private readonly List<RecordedNodeRequest> _requests = [];

    /// <summary>Every request this node received, in arrival order.</summary>
    public IReadOnlyList<RecordedNodeRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_requests)
        {
            _requests.Add(new RecordedNodeRequest(
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues(RenderNodeConnectionFactory.ApiKeyHeaderName, out var key)
                    ? key.FirstOrDefault()
                    : null));
        }

        try
        {
            return Task.FromResult(answer(request));
        }
        catch (Exception exception)
        {
            // A delegate that throws stands in for a node that cannot be reached at all; the handler contract
            // is a faulted task, never a synchronous throw out of SendAsync.
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
}

/// <summary>
/// A node body generated as it is read, deterministically: byte <c>i</c> is a function of its offset only, so
/// <see cref="Fill"/> and <see cref="ExpectedSha256"/> let a test hash exactly the bytes the client was served
/// without ever keeping them.
/// </summary>
/// <remarks>
/// <see cref="CreateContentReadStreamAsync()"/> is overridden because the base implementation buffers the whole
/// body into memory — precisely what this fixture exists to detect.
/// </remarks>
public sealed class SyntheticDownloadContent : HttpContent
{
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the node announces a matching <c>Content-Length</c>; <c>false</c> is a chunked answer.</summary>
    private readonly bool _declareLength;

    private long _served;

    /// <summary>Creates a body of <paramref name="length"/> bytes of the given media type.</summary>
    /// <param name="length">How many bytes the node will serve.</param>
    /// <param name="contentType">The <c>Content-Type</c> the node announces.</param>
    /// <param name="declareLength">
    /// Whether the node sends a matching <c>Content-Length</c>; <c>false</c> stands in for a chunked answer, so
    /// the client's own running count is the only bound.
    /// </param>
    /// <param name="stallAfterBytes">
    /// How many bytes are served before the body stalls; <see cref="long.MaxValue"/> never stalls.
    /// </param>
    /// <param name="failAfterBytes">
    /// How many bytes are served before the body dies like a node whose connection dropped mid-transfer
    /// (<see cref="IOException"/>); <see cref="long.MaxValue"/> never fails.
    /// </param>
    public SyntheticDownloadContent(
        long length,
        string contentType,
        bool declareLength = true,
        long stallAfterBytes = long.MaxValue,
        long failAfterBytes = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType);

        Length = length;
        StallAfterBytes = stallAfterBytes;
        FailAfterBytes = failAfterBytes;
        _declareLength = declareLength;
        Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        if (declareLength)
        {
            Headers.ContentLength = length;
        }
    }

    /// <summary>How many bytes the body serves.</summary>
    public long Length { get; }

    /// <summary>How many bytes are served before the body stalls.</summary>
    public long StallAfterBytes { get; }

    /// <summary>How many bytes are served before the body throws.</summary>
    public long FailAfterBytes { get; }

    /// <summary>How many bytes the client has taken, so a test can prove a body was never read.</summary>
    public long BytesServed => Interlocked.Read(ref _served);

    /// <summary>Completed once the body has stalled, i.e. once the client is waiting on it.</summary>
    public Task Stalled => _stalled.Task;

    /// <summary>Completed once the body has been served to its end.</summary>
    public Task Completed => _completed.Task;

    /// <summary>The bytes this fixture generates, block by block.</summary>
    /// <param name="destination">The block to fill.</param>
    /// <param name="offset">The absolute offset of <paramref name="destination"/>[0] within the body.</param>
    public static void Fill(Span<byte> destination, long offset)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (byte)(((offset + index) * 31 + 7) % 251);
        }
    }

    /// <summary>The lowercase hex SHA-256 of the body's <paramref name="length"/> bytes.</summary>
    /// <param name="length">The number of bytes to hash.</param>
    /// <returns>The digest of exactly the bytes the node serves.</returns>
    public static string ExpectedSha256(long length)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            for (var offset = 0L; offset < length; offset += block.Length)
            {
                var count = (int)Math.Min(block.Length, length - offset);
                Fill(block.AsSpan(0, count), offset);
                hasher.AppendData(block, 0, count);
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Body(this));

    /// <inheritdoc />
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        CreateContentReadStreamAsync();

    /// <summary>
    /// The length is computable only when the test asked the node to announce one: a chunked answer must not
    /// let the framework derive a <c>Content-Length</c> the node never sent, because the client's own running
    /// count is what has to bound that body.
    /// </summary>
    /// <param name="length">The length the node would announce.</param>
    /// <returns>Whether the node announced a length at all.</returns>
    protected override bool TryComputeLength(out long length)
    {
        length = Length;

        return _declareLength;
    }

    /// <summary>
    /// Serves the whole body, without the stall hook: the download client reads the content stream
    /// (<see cref="CreateContentReadStreamAsync()"/>), so only that path models a stalled node.
    /// </summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);

        try
        {
            for (var offset = 0L; offset < Length; offset += buffer.Length)
            {
                var count = (int)Math.Min(buffer.Length, Length - offset);
                Fill(buffer.AsSpan(0, count), offset);
                await stream.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The read side of <see cref="SyntheticDownloadContent"/>: generated bytes, a stall that only the caller's
    /// cancellation ends, and no buffering of its own.
    /// </summary>
    private sealed class Body(SyntheticDownloadContent owner) : Stream
    {
        private long _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Serve(buffer.AsSpan(offset, count));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ServeAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask<int> ServeAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_offset >= owner.Length)
            {
                owner._completed.TrySetResult();

                return 0;
            }

            if (_offset >= owner.FailAfterBytes)
            {
                // The node's connection dropped mid-body: what a reset looks like to the reader.
                throw new IOException("The response ended prematurely.");
            }

            if (_offset >= owner.StallAfterBytes)
            {
                owner._stalled.TrySetResult();

                // Nothing but the client's idle timer or its total budget cancels this: a stalled node is
                // exactly what those two timers exist to end (WF08).
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return Serve(buffer.Span);
        }

        private int Serve(Span<byte> buffer)
        {
            var remaining = Math.Min(owner.Length, Math.Min(owner.StallAfterBytes, owner.FailAfterBytes)) - _offset;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            SyntheticDownloadContent.Fill(buffer[..count], _offset);

            _offset += count;
            Interlocked.Exchange(ref owner._served, _offset);

            return count;
        }
    }
}

/// <summary>
/// The destination of a download test: write-only — no <c>Length</c>, no <c>Position</c>, no seek — so a client
/// that buffers the file in memory instead of streaming it fails loudly. It also hashes what it was handed,
/// which is how a test proves the reported digest covers exactly the bytes that were written.
/// </summary>
public sealed class CountingWriteStream : Stream
{
    private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private long _bytes;

    /// <summary>How many bytes were written to this stream.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>The lowercase hex SHA-256 of exactly the bytes written to this stream.</summary>
    /// <returns>The digest; reading it twice is not meaningful, so a test reads it once, at the end.</returns>
    public string Sha256() => Convert.ToHexStringLower(_hasher.GetHashAndReset());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hasher.AppendData(buffer);
        Interlocked.Add(ref _bytes, buffer.Length);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hasher.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// The destination of a download test whose write is scripted (D5, D6, D8): the callback decides whether a write
/// returns at once, records the chunk size, waits on the manual clock or throws, so a test can prove what the
/// client does while the caller's own stream is writing. Nothing of the chunk is kept — the byte count is the
/// only thing this stream remembers.
/// </summary>
/// <remarks>
/// A write that waits is the case the client's idle timer must not count (D6); a write that throws is the case
/// the client must rethrow instead of mapping to a node fault (D8).
/// </remarks>
/// <param name="write">The write itself, handed the chunk and the caller's token.</param>
public sealed class ScriptedWriteStream(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write) : Stream
{
    private readonly TaskCompletionSource _writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytes;

    /// <summary>How many bytes the client handed this stream.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Completed once the client is inside a write, i.e. while it is blocked on the destination.</summary>
    public Task Writing => _writing.Task;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _writing.TrySetResult();

        await write(buffer, cancellationToken).ConfigureAwait(false);

        Interlocked.Add(ref _bytes, buffer.Length);
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// A hand-written <see cref="IRenderNodeConnectionFactory"/> for the download tests: it hands the client an
/// <see cref="HttpClient"/> over the scripted <see cref="SyntheticDownloadHandler"/> and sets the node's base
/// URL and <c>x-api-key</c> exactly as <c>RenderNodeConnectionFactory</c> does, so the client under test is
/// exercised through its real seam and never opens a socket.
/// </summary>
/// <remarks>
/// Production's client has <c>AllowAutoRedirect = false</c> and the caller replaces its timeout with
/// <see cref="Timeout.InfiniteTimeSpan"/> (WF08); a fake handler never follows a redirect, so only the base
/// address, the key and the infinite timeout are repeated here.
/// </remarks>
/// <param name="handler">The node's scripted transport.</param>
/// <param name="apiKey">The key the client must carry; <c>null</c> leaves the header off.</param>
public sealed class SyntheticDownloadNodeFactory(SyntheticDownloadHandler handler, string? apiKey = null)
    : IRenderNodeConnectionFactory, IDisposable
{
    private readonly List<HttpClient> _clients = [];

    /// <summary>Every client this factory handed out, so the test can dispose them with the factory.</summary>
    public IReadOnlyList<HttpClient> Clients => _clients;

    /// <inheritdoc />
    public HttpClient Create(WorkerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = node.BaseUrl,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(RenderNodeConnectionFactory.ApiKeyHeaderName, apiKey);
        }

        _clients.Add(client);

        return client;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
