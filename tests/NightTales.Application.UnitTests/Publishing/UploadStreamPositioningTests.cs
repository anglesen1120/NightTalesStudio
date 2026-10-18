using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Features.Publishing.Upload;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <c>UploadStreamPositioning.EnsureAtAsync</c> (06.2.3-T1, G7): a seekable stream is positioned without a single
/// read, a non-seekable one (an S3 response body, CR-17) is advanced through one rented 256 KiB buffer whatever the
/// offset is, a negative offset is refused before anything is touched, and a stream that ends before the offset
/// answers <c>youtube_session_expired</c> instead of resuming from a byte the session does not expect.
/// </summary>
/// <remarks>
/// No mocking library (09 D13): the two doubles below are hand-written streams that record how the helper read them
/// — the read count proves the seek path reads nothing, and the largest requested read proves the discard path never
/// sizes a buffer from the offset.
/// </remarks>
public sealed class UploadStreamPositioningTests
{
    /// <summary>One 256 KiB-multiple offset plus a remainder, so the discard loop also reads a thin tail.</summary>
    private const long Offset = 700_000;

    private static readonly byte[] Payload = CreatePayload(1024 * 1024);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UploadStreamPositioning_SeekableStream_DoesNotReadForward()
    {
        using var stream = new RecordingSeekableStream(Payload);

        var result = await UploadStreamPositioning.EnsureAtAsync(stream, Offset, Ct);

        result.IsError.ShouldBeFalse();
        stream.Position.ShouldBe(Offset);
        stream.ReadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task UploadStreamPositioning_NonSeekableStream_DiscardsThroughOneSmallBuffer()
    {
        using var stream = new RecordingNonSeekableStream(Payload);

        var result = await UploadStreamPositioning.EnsureAtAsync(stream, Offset, Ct);

        result.IsError.ShouldBeFalse();
        stream.Consumed.ShouldBe(Offset);

        // Three reads for 700 000 bytes: two full buffers and the remainder. The largest request is the 256 KiB
        // constant — never the offset — which is what keeps a 2 GB resume at constant memory.
        stream.ReadCalls.ShouldBe(3);
        stream.LargestRequestBytes.ShouldBe(UploadStreamPositioning.DiscardBufferBytes);
    }

    [Fact]
    public async Task UploadStreamPositioning_EndsBeforeOffset_IsSessionExpired()
    {
        // The asset row says 700 000 bytes but the object holds 100: it shrank after the session was created, so the
        // session cannot continue and the caller creates a new one.
        using var stream = new RecordingNonSeekableStream(CreatePayload(100));

        var result = await UploadStreamPositioning.EnsureAtAsync(stream, Offset, Ct);

        AssertFailure(result, YouTubeErrorCodes.SessionExpired);
    }

    [Fact]
    public async Task UploadStreamPositioning_StreamEndsExactlyAtTheOffset_Succeeds()
    {
        // The boundary the previous test's failure must not swallow: the object is exactly as long as the row says,
        // so the loop ends with nothing left to discard and no read past the end.
        using var stream = new RecordingNonSeekableStream(CreatePayload((int)Offset));

        var result = await UploadStreamPositioning.EnsureAtAsync(stream, Offset, Ct);

        result.IsError.ShouldBeFalse();
        stream.Consumed.ShouldBe(Offset);
    }

    [Fact]
    public async Task UploadStreamPositioning_NegativeOffset_IsUploadFailedAndTouchesNothing()
    {
        using var stream = new RecordingNonSeekableStream(Payload);

        var result = await UploadStreamPositioning.EnsureAtAsync(stream, -1, Ct);

        AssertFailure(result, YouTubeErrorCodes.UploadFailed);
        stream.ReadCalls.ShouldBe(0);
        stream.Consumed.ShouldBe(0);
    }

    [Fact]
    public async Task UploadStreamPositioning_ZeroOffset_ReadsNothing()
    {
        // A probe that answers `Partial(0)` (or a completion check on a fresh object) resumes from the first byte:
        // the stream stands where it already is, and not one read is spent proving it.
        using var nonSeekable = new RecordingNonSeekableStream(Payload);
        using var seekable = new RecordingSeekableStream(Payload);

        (await UploadStreamPositioning.EnsureAtAsync(nonSeekable, 0, Ct)).IsError.ShouldBeFalse();
        (await UploadStreamPositioning.EnsureAtAsync(seekable, 0, Ct)).IsError.ShouldBeFalse();

        nonSeekable.ReadCalls.ShouldBe(0);
        nonSeekable.Consumed.ShouldBe(0);
        seekable.ReadCalls.ShouldBe(0);
        seekable.Position.ShouldBe(0);
    }

    [Fact]
    public async Task UploadStreamPositioning_CancelledToken_IsRethrown()
    {
        // The job's token is honoured before anything is read: a cancelled call rethrows and never becomes an
        // error the caller would classify as a Google failure.
        using var stream = new RecordingNonSeekableStream(Payload);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => UploadStreamPositioning.EnsureAtAsync(stream, Offset, cancellation.Token));

        stream.ReadCalls.ShouldBe(0);
    }

    private static void AssertFailure(ErrorOr<Success> result, string expectedCode)
    {
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(expectedCode);

        // Every upload failure carries the same empty classifier input: no Google reason, no HTTP status, not a
        // transport failure (the caller retries on that flag alone).
        var failure = YouTubeFailure.From(result.FirstError);

        failure.ShouldNotBeNull();
        failure!.GoogleReason.ShouldBeNull();
        failure.HttpStatus.ShouldBeNull();
        failure.IsTransport.ShouldBeFalse();
    }

    private static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];

        for (var index = 0; index < size; index++)
        {
            payload[index] = (byte)index;
        }

        return payload;
    }

    /// <summary>A seekable stream over a payload that counts every read, so the seek path's zero reads are provable.</summary>
    private sealed class RecordingSeekableStream(byte[] payload) : MemoryStream(payload, writable: false)
    {
        internal int ReadCalls { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>
    /// A read-only stream that cannot seek (the S3 response body, CR-17) and records each read: how many arrived and
    /// the largest buffer the caller asked for.
    /// </summary>
    private sealed class RecordingNonSeekableStream(byte[] payload) : Stream
    {
        private long _consumed;

        internal int ReadCalls { get; private set; }

        internal int LargestRequestBytes { get; private set; }

        internal long Consumed => _consumed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = (int)Math.Min(buffer.Length, payload.Length - _consumed);
            payload.AsSpan((int)_consumed, read).CopyTo(buffer);
            _consumed += read;

            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            LargestRequestBytes = Math.Max(LargestRequestBytes, buffer.Length);

            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
