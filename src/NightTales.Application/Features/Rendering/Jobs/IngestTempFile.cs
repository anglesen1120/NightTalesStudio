namespace NightTales.Application.Features.Rendering.Jobs;

/// <summary>
/// One private temp file of the render ingest (09.6.3-T2 step 3, BD7): a downloaded output lands here, is
/// verified, and is uploaded from here — the job never holds the media in memory and never writes it under the
/// repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>Naming.</b> <c>{renderJobId:N}-{random:N}.{ext}</c>: the render job id is there so an operator who finds a
/// leftover file (a killed process, a full disk) can name the row it belongs to, and the random part keeps two
/// attempts of the same row — or one attempt's video and subtitles — from colliding. The mode is owner-only
/// (<c>0600</c> on POSIX, created that way rather than fixed afterwards), because the bytes are a paid render.
/// </para>
/// <para>
/// <b>Cleanup.</b> The handle is opened with <see cref="FileOptions.DeleteOnClose"/>, which removes the file when
/// the OS releases the last handle, and <see cref="Dispose"/> deletes it explicitly as well: a filesystem that
/// ignored the hint (or a handle the runtime kept) still leaves nothing behind. Either way the file is gone when
/// the job's <c>finally</c> runs, on success and on every failure path (AC10).
/// </para>
/// <para>
/// <b>Not a port.</b> It is a plain type with a static factory, like <c>SpooledUpload</c> in Infrastructure: there
/// is nothing to substitute in a test — the real filesystem is the thing under test (the temp directory must be
/// empty afterwards) — so a second implementation would only hide the behaviour the task asserts.
/// </para>
/// </remarks>
public sealed class IngestTempFile : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private IngestTempFile(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>The absolute path of the file; <see cref="Stream"/> writes into it.</summary>
    public string Path { get; }

    /// <summary>The seekable destination of the download, positioned at its start.</summary>
    public Stream Stream => _stream;

    /// <summary>
    /// Creates the file under <paramref name="directory"/> (creating that directory owner-only when it is
    /// missing).
    /// </summary>
    /// <param name="directory">The configured spool directory.</param>
    /// <param name="renderJobId">The row the output belongs to; part of the file name.</param>
    /// <param name="extension">The file extension of the output, without the dot (<c>mp4</c>, <c>srt</c>).</param>
    /// <returns>The open file, owned by the caller.</returns>
    /// <exception cref="IOException">The file or the directory could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory is not writable by this account.</exception>
    public static IngestTempFile Create(string directory, Guid renderJobId, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = System.IO.Path.Combine(directory, $"{renderJobId:N}-{Guid.CreateVersion7():N}.{extension}");

        return new IngestTempFile(path, Open(path));
    }

    /// <summary>Rewinds the file so the bytes just written can be read again — the blob upload's source.</summary>
    /// <returns>The same stream, at position 0.</returns>
    public Stream Rewind()
    {
        _stream.Position = 0;

        return _stream;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        TryDelete(Path);
    }

    /// <summary>
    /// Opens the file for the download: create-new, read/write, asynchronous and sequential, owner-only on POSIX,
    /// and deleted when this handle is released.
    /// </summary>
    /// <param name="path">The path to create.</param>
    /// <returns>The stream the download writes into.</returns>
    private static FileStream Open(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>
    /// Deletes the file, ignoring the two ways a delete may legitimately be refused: the OS already removed it
    /// (<see cref="FileOptions.DeleteOnClose"/>) or the file is momentarily locked.
    /// </summary>
    /// <param name="path">The path to remove.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // DeleteOnClose remains the primary cleanup when an explicit delete is refused.
        }
    }
}
