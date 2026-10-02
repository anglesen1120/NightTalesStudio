using System.Security.Cryptography;
using NightTales.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Storage;

/// <summary>
/// <see cref="SpooledUpload"/> (09.7.2-T1): the digest and byte count, the source stream ownership and
/// the private temp file's lifecycle (created once, deleted on dispose, cancellation or failure).
/// </summary>
public sealed class SpooledUploadTests
{
    private static readonly byte[] Content = "nighttales-spool-payload"u8.ToArray();

    [Fact]
    public async Task CreateAsync_HashesTheBody_SpoolsIt_AndKeepsTheSourceOpen()
    {
        using var home = new TempHome();
        var ct = TestContext.Current.CancellationToken;
        await using var source = new NonSeekableStream(Content);

        await using var spool = await SpooledUpload.CreateAsync(source, ct);

        spool.Bytes.ShouldBe(Content.LongLength);
        spool.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant());

        // The spooled body holds exactly the source bytes...
        spool.Stream.CanSeek.ShouldBeTrue("the SDK needs a seekable body of known length");
        spool.Stream.Length.ShouldBe(Content.LongLength);
        using var copy = new MemoryStream();
        await spool.Stream.CopyToAsync(copy, ct);
        copy.ToArray().ShouldBe(Content);

        // ...it is the only file in the private directory while the spool is alive...
        Files(home.Path).Length.ShouldBe(1);

        // ...and the caller's stream was never disposed: another read is legal and finds the end.
        (await source.ReadAsync(new byte[1], ct)).ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_DeletesTheTempFile()
    {
        using var home = new TempHome();

        await using (var source = new NonSeekableStream(Content))
        {
            var spool = await SpooledUpload.CreateAsync(source, TestContext.Current.CancellationToken);
            Files(home.Path).Length.ShouldBe(1);

            await spool.DisposeAsync();
        }

        Files(home.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenTheSourceFails_LeavesNoTempFile()
    {
        using var home = new TempHome();
        await using var source = new FailingStream(Content);

        await Should.ThrowAsync<IOException>(
            () => SpooledUpload.CreateAsync(source, TestContext.Current.CancellationToken));

        Files(home.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenCancelled_LeavesNoTempFile()
    {
        using var home = new TempHome();
        await using var source = new NonSeekableStream(Content);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => SpooledUpload.CreateAsync(source, cancelled.Token));

        Files(home.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_CreatesAnOwnerOnlyDirectoryAndFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var home = new TempHome();
        await using var source = new NonSeekableStream(Content);

        await using var spool = await SpooledUpload.CreateAsync(source, TestContext.Current.CancellationToken);

        var directory = Path.Combine(home.Path, "tmp", "blob-store");
        File.GetUnixFileMode(directory).ShouldBe(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            "the spool directory must not be readable by other users");
        File.GetUnixFileMode(Files(home.Path).Single()).ShouldBe(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            "the spooled payload must not be readable by other users");
    }

    private static string[] Files(string home)
    {
        var directory = Path.Combine(home, "tmp", "blob-store");

        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    /// <summary>Points <c>NTS_HOME</c> at a fresh private directory for the test and restores it after.</summary>
    private sealed class TempHome : IDisposable
    {
        private readonly string? _previous;

        public TempHome()
        {
            Path = Directory.CreateTempSubdirectory("nts-spool-test-").FullName;
            _previous = Environment.GetEnvironmentVariable("NTS_HOME");
            Environment.SetEnvironmentVariable("NTS_HOME", Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NTS_HOME", _previous);

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A read-only stream that cannot seek, like the render pipeline's upload body.</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A non-seekable stream that yields one chunk and then fails, as a broken render upload would.</summary>
    private sealed class FailingStream(byte[] content) : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the source failed");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered)
            {
                throw new IOException("the source failed");
            }

            _delivered = true;
            content.AsSpan().CopyTo(buffer.Span);

            return ValueTask.FromResult(content.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
