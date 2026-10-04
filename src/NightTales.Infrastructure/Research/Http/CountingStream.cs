using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research.Http;

/// <summary>
/// A read-only stream that counts the bytes read from <paramref name="inner"/> and throws
/// <see cref="SourceFetchException"/> (<c>source_too_large</c>) as soon as the count exceeds
/// <paramref name="limit"/> (02 D11: the cap is on <em>decompressed</em> bytes, so this wraps the decompression stream).
/// Reading stops at the first read that crosses the limit, so a decompression bomb is not expanded further.
/// </summary>
/// <param name="inner">The stream to count.</param>
/// <param name="limit">The most bytes that may be read.</param>
public sealed class CountingStream(Stream inner, long limit) : Stream
{
    private long _count;

    /// <summary>The bytes read so far.</summary>
    public long BytesRead => _count;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Count(int read)
    {
        _count += read;
        if (_count > limit)
        {
            throw new SourceFetchException(SourceErrors.SourceTooLarge);
        }

        return read;
    }
}
