namespace NightTales.MptAdapter;

/// <summary>Why <see cref="SafePath"/> refused a file.</summary>
internal enum SafePathResult
{
    Ok,
    NotFound,

    /// <summary>The name is not a plain file name, or a directory/file on the way is a link (path containment, S8).</summary>
    Escapes,

    TooLarge,
}

/// <summary>
/// The adapter's one path-containment gate for task files (S8, 05 D15 P3). A task file is always
/// <c>&lt;root&gt;/&lt;dirName&gt;/&lt;fileName&gt;</c> where <c>dirName</c> comes from the stored row and
/// <c>fileName</c> is a constant; both must be plain names, and neither the task dir nor the file may be a
/// link, so a hostile or confused task dir cannot make the adapter read or serve a file outside it.
/// </summary>
internal static class SafePath
{
    /// <summary>True for a single path segment: not empty, no separators, no <c>.</c>/<c>..</c>, no NUL.</summary>
    internal static bool IsPlainName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', '\0']) < 0
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    /// <summary>The canonical <c>root/dirName</c> when it is an existing, non-link directory under root.</summary>
    internal static SafePathResult ResolveDirectory(string root, string? dirName, out string path)
    {
        path = string.Empty;
        if (!IsPlainName(dirName))
        {
            return SafePathResult.Escapes;
        }

        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, dirName!));
        if (!combined.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return SafePathResult.Escapes;
        }

        var directory = new DirectoryInfo(combined);
        if (!directory.Exists)
        {
            return SafePathResult.NotFound;
        }

        if (IsLink(directory))
        {
            return SafePathResult.Escapes;
        }

        path = combined;
        return SafePathResult.Ok;
    }

    /// <summary>
    /// Opens <c>root/dirName/fileName</c> for a sequential read after the containment checks. The size cap is
    /// checked on the opened handle too, so a file that grew between the check and the open is still refused.
    /// </summary>
    internal static SafePathResult OpenRead(
        string root,
        string? dirName,
        string fileName,
        long maxBytes,
        out FileStream? stream)
    {
        stream = null;
        if (!IsPlainName(fileName))
        {
            return SafePathResult.Escapes;
        }

        var directoryResult = ResolveDirectory(root, dirName, out var directory);
        if (directoryResult != SafePathResult.Ok)
        {
            return directoryResult;
        }

        var file = new FileInfo(Path.Combine(directory, fileName));
        if (!file.Exists)
        {
            return SafePathResult.NotFound;
        }

        if (IsLink(file))
        {
            return SafePathResult.Escapes;
        }

        if (file.Length > maxBytes)
        {
            return SafePathResult.TooLarge;
        }

        FileStream opened;
        try
        {
            opened = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return SafePathResult.NotFound;
        }

        if (opened.Length > maxBytes)
        {
            opened.Dispose();
            return SafePathResult.TooLarge;
        }

        stream = opened;
        return SafePathResult.Ok;
    }

    /// <summary>Writes <paramref name="bytes"/> to a temp file beside <paramref name="path"/> and renames it into place.</summary>
    internal static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("The path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // The temp file is a leftover at worst; the original destination was never touched.
            }

            throw;
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || info.ResolveLinkTarget(returnFinalTarget: true) is not null;
}
