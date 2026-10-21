using System.IO;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// A payload-backed stream that records whether it was disposed, so the output route's cleanup can be asserted:
/// the real seam hands out a <see cref="FileStream"/>, which leaks a file handle when it is never closed. A
/// settable read fault stands in for a copy failure mid-download.
/// </summary>
internal sealed class TrackingStream(byte[] payload) : Stream
{
    private readonly MemoryStream _inner = new(payload);

    /// <summary>When set, every read throws it (a copy failure).</summary>
    internal Exception? ReadError { get; init; }

    /// <summary>True once the stream was disposed, however the download ended.</summary>
    internal bool Disposed { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (ReadError is not null)
        {
            throw ReadError;
        }

        return _inner.Read(buffer, offset, count);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (ReadError is not null)
        {
            throw ReadError;
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

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
        Disposed = true;
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
