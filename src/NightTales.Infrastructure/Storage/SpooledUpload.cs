using System.Buffers;
using System.Security.Cryptography;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// A private temporary file that holds an upload body of unknown length, because AWSSDK.S3 4.x needs a
/// seekable body with a known length to frame a plain (non <c>aws-chunked</c>) PUT — a non-seekable
/// stream makes <c>AmazonS3PostMarshallHandler.GetStreamWithLength</c> throw
/// "Could not determine content length" (QA-ST-2) and forces chunked encoding whose
/// <c>Content-Encoding: aws-chunked</c> SeaweedFS then persists on the object (QA-ST-4).
/// </summary>
/// <remarks>
/// <para>
/// The source is copied and SHA-256 hashed in one pass, so <see cref="Bytes"/> and <see cref="Sha256"/>
/// describe exactly the bytes the store accepted without a second read. The source stream is never
/// disposed — it belongs to the caller — and the temp file is deleted when this object is disposed,
/// including on cancellation or a failed upload.
/// </para>
/// <para>
/// The file lives under a private directory: <c>$NTS_HOME/tmp/blob-store</c> when <c>NTS_HOME</c> is
/// set (01 CR-11 / DEC-5, mode 700), otherwise under <c>.nts/tmp/blob-store</c> in the current runtime
/// directory; the directory and the file are created owner-only on POSIX.
/// </para>
/// </remarks>
public sealed class SpooledUpload : IAsyncDisposable
{
    private const int BufferSize = 81_920;

    private readonly FileStream _stream;
    private readonly string _path;

    private SpooledUpload(FileStream stream, string path, long bytes, string sha256)
    {
        _stream = stream;
        _path = path;
        Bytes = bytes;
        Sha256 = sha256;
    }

    /// <summary>The spooled body, positioned at its start; the caller reads it and disposes this object.</summary>
    public Stream Stream => _stream;

    /// <summary>The number of bytes copied from the source.</summary>
    public long Bytes { get; }

    /// <summary>The lowercase hex SHA-256 of those bytes.</summary>
    public string Sha256 { get; }

    /// <summary>Copies <paramref name="source"/> to a new private temp file, hashing it in the same pass.</summary>
    /// <param name="source">The non-seekable body; it stays open and the caller owns it.</param>
    /// <param name="cancellationToken">Cancels the copy; the partial file is deleted.</param>
    /// <returns>The spooled body.</returns>
    public static async Task<SpooledUpload> CreateAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var path = Path.Combine(UploadDirectory(), $"upload-{Guid.CreateVersion7():N}.tmp");
        var stream = Open(path);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                total += read;
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Position = 0;

            return new SpooledUpload(
                stream,
                path,
                total,
                Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            // Cancellation or a read/write failure: drop the partial file before the exception escapes.
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                TryDelete(path);
            }

            throw;
        }
        finally
        {
            // Clear private media before returning the bounded buffer to other uploads.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);

        // DeleteOnClose removes it on most platforms; the explicit delete covers a filesystem that
        // ignored the hint (and is a no-op when the file is already gone).
        TryDelete(_path);
    }

    private static FileStream Open(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            BufferSize = BufferSize,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static string UploadDirectory()
    {
        var home = Environment.GetEnvironmentVariable("NTS_HOME");
        var directory = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Directory.GetCurrentDirectory(), ".nts", "tmp", "blob-store")
            : Path.Combine(home, "tmp", "blob-store");

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // DeleteOnClose remains the primary cleanup if an explicit delete is temporarily refused.
        }
    }
}
