using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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

        var result = OperatingSystem.IsWindows() ? CheckDirectoryPortable(combined) : CheckDirectoryNative(rootFull, dirName!, combined);
        if (result == SafePathResult.Ok)
        {
            path = combined;
        }

        return result;
    }

    /// <summary>
    /// Opens <c>root/dirName/fileName</c> for a sequential read. On Unix the walk is descriptor-relative
    /// (<c>openat</c> from the root's descriptor, <c>O_NOFOLLOW</c> on every component below the root), so a link
    /// swapped in for the task dir or the file after any earlier look can never lead outside the root: the
    /// kernel refuses it at the moment of the open (S8, QA-2). The size cap is checked on the opened handle.
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

        FileStream opened;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var file = new FileInfo(Path.Combine(directory, fileName));
                if (!file.Exists)
                {
                    return SafePathResult.NotFound;
                }

                if (IsLink(file) || IsLink(new DirectoryInfo(directory)))
                {
                    return SafePathResult.Escapes;
                }

                opened = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);
            }
            else
            {
                var handle = OpenFileNative(Path.GetFullPath(root), dirName!, fileName, directory, out var failure);
                if (handle is null)
                {
                    return failure;
                }

                opened = new FileStream(handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
            }
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

    private static SafePathResult CheckDirectoryPortable(string combined)
    {
        var directory = new DirectoryInfo(combined);
        if (!directory.Exists)
        {
            return SafePathResult.NotFound;
        }

        return IsLink(directory) ? SafePathResult.Escapes : SafePathResult.Ok;
    }

    /// <summary>The root may sit behind links (an operator's choice); the task dir below it may not.</summary>
    private static SafePathResult CheckDirectoryNative(string rootFull, string dirName, string combined)
    {
        using var rootHandle = OpenNative(null, rootFull, DirectoryFlag | CloseOnExecFlag(), rootFull, out var failure);
        if (rootHandle is null)
        {
            return failure;
        }

        using var dirHandle = OpenNative(rootHandle, dirName, DirectoryFlag | NoFollowFlag() | CloseOnExecFlag(), combined, out failure);
        return dirHandle is null ? failure : SafePathResult.Ok;
    }

    /// <summary>
    /// Pins the task dir: resolves it through the descriptor walk, keeps that directory open (so its inode
    /// cannot be recycled) and records its (device, inode). <see cref="DirectoryPin.StillSame"/> re-walks the
    /// path and compares, so a dir replaced while a path-based tool ran is noticed. The tool itself still opens
    /// the dir by path - that residual cannot be closed without passing descriptors to it.
    /// </summary>
    internal static SafePathResult PinDirectory(string root, string? dirName, out DirectoryPin? pin)
    {
        pin = null;
        var result = ResolveDirectory(root, dirName, out var path);
        if (result != SafePathResult.Ok)
        {
            return result;
        }

        if (OperatingSystem.IsWindows())
        {
            pin = new DirectoryPin(path, root, dirName!, null, default);
            return SafePathResult.Ok;
        }

        var held = WalkToDirectory(Path.GetFullPath(root), dirName!, path, out var failure);
        if (held is null)
        {
            return failure;
        }

        pin = new DirectoryPin(path, root, dirName!, held, IdentityOf(held));
        return SafePathResult.Ok;
    }

    private static SafeFileHandle? WalkToDirectory(string rootFull, string dirName, string combined, out SafePathResult failure)
    {
        using var rootHandle = OpenNative(null, rootFull, DirectoryFlag | CloseOnExecFlag(), rootFull, out failure);
        return rootHandle is null
            ? null
            : OpenNative(rootHandle, dirName, DirectoryFlag | NoFollowFlag() | CloseOnExecFlag(), combined, out failure);
    }

    /// <summary>
    /// (device, inode) from <c>fstat</c>. Only equality is used, so only the two fields' offsets matter: inode is a
    /// 64-bit value at offset 8 on Linux (x64, arm64) and Darwin; device is 64-bit at 0 on Linux and 32-bit at 0 on Darwin.
    /// </summary>
    private static (long Device, ulong Inode) IdentityOf(SafeFileHandle handle)
    {
        var buffer = new byte[512];
        if (NativeFstat((int)handle.DangerousGetHandle(), buffer) != 0)
        {
            throw new IOException($"fstat failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        var device = OperatingSystem.IsMacOS() ? BitConverter.ToInt32(buffer, 0) : BitConverter.ToInt64(buffer, 0);
        return (device, BitConverter.ToUInt64(buffer, 8));
    }

    /// <summary>A task dir held open since <see cref="PinDirectory"/>.</summary>
    internal sealed class DirectoryPin : IDisposable
    {
        private readonly string _root;
        private readonly string _dirName;
        private readonly SafeFileHandle? _held;
        private readonly (long Device, ulong Inode) _identity;

        internal DirectoryPin(string path, string root, string dirName, SafeFileHandle? held, (long Device, ulong Inode) identity)
        {
            Path = path;
            _root = root;
            _dirName = dirName;
            _held = held;
            _identity = identity;
        }

        internal string Path { get; }

        /// <summary>True while the name still leads, without a link, to the very directory that was pinned.</summary>
        internal bool StillSame()
        {
            if (_held is null)
            {
                return CheckDirectoryPortable(Path) == SafePathResult.Ok;
            }

            using var current = WalkToDirectory(System.IO.Path.GetFullPath(_root), _dirName, Path, out _);
            return current is not null && IdentityOf(current) == _identity;
        }

        public void Dispose() => _held?.Dispose();
    }

    private static SafeFileHandle? OpenFileNative(string rootFull, string dirName, string fileName, string directory, out SafePathResult failure)
    {
        using var rootHandle = OpenNative(null, rootFull, DirectoryFlag | CloseOnExecFlag(), rootFull, out failure);
        if (rootHandle is null)
        {
            return null;
        }

        using var dirHandle = OpenNative(rootHandle, dirName, DirectoryFlag | NoFollowFlag() | CloseOnExecFlag(), directory, out failure);
        if (dirHandle is null)
        {
            return null;
        }

        var file = OpenNative(
            dirHandle,
            fileName,
            NoFollowFlag() | CloseOnExecFlag() | NonBlockFlag(),
            Path.Combine(directory, fileName),
            out failure);
        if (file is null)
        {
            return null;
        }

        // RD-3: only a regular file is served. O_RDONLY opens a directory happily and a FIFO would otherwise
        // block the open (hence O_NONBLOCK); a one-byte pread at offset 0 is refused by both (EISDIR, ESPIPE)
        // and is a plain read for a regular file, empty ones included (0 bytes).
        var probe = new byte[1];
        if (NativePread((int)file.DangerousGetHandle(), probe, 1, 0) < 0)
        {
            file.Dispose();
            failure = SafePathResult.NotFound;
            return null;
        }

        return file;
    }

    // The flag values are not portable: the node is Linux (x64 or arm64), the developer Mac is Darwin.
    private static int NoFollowFlag() =>
        OperatingSystem.IsMacOS() ? 0x0100 : IsLinuxArm() ? 0x8000 : 0x20000;

    private static int CloseOnExecFlag() => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    private static int NonBlockFlag() => OperatingSystem.IsMacOS() ? 0x0004 : 0x800;

    private static int DirectoryFlag =>
        OperatingSystem.IsMacOS() ? 0x100000 : IsLinuxArm() ? 0x4000 : 0x10000;

    private static bool IsLinuxArm() =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    /// <summary>
    /// <c>open</c>/<c>openat</c> (<c>O_RDONLY</c>). A link met under <c>O_NOFOLLOW</c> is <c>Escapes</c>; a missing
    /// or over-long name is <c>NotFound</c>; anything else (permissions, descriptor exhaustion) throws.
    /// </summary>
    private static SafeFileHandle? OpenNative(SafeFileHandle? parent, string name, int flags, string fullName, out SafePathResult failure)
    {
        failure = SafePathResult.Ok;
        var bytes = new byte[Encoding.UTF8.GetByteCount(name) + 1];
        Encoding.UTF8.GetBytes(name, bytes);
        var descriptor = parent is null
            ? NativeOpen(bytes, flags)
            : NativeOpenAt((int)parent.DangerousGetHandle(), bytes, flags);
        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        var errno = Marshal.GetLastPInvokeError();
        // ELOOP: Linux 40, Darwin 62. ENOENT 2 and ENOTDIR 20 are the same on both; ENAMETOOLONG Linux 36, Darwin 63.
        if (errno is 40 or 62)
        {
            failure = SafePathResult.Escapes;
            return null;
        }

        if (errno == 20)
        {
            // O_NOFOLLOW|O_DIRECTORY on a link answers ENOTDIR on some kernels: a link is Escapes, a file is NotFound.
            failure = IsLink(new FileInfo(fullName)) ? SafePathResult.Escapes : SafePathResult.NotFound;
            return null;
        }

        if (errno is 2 or 36 or 63)
        {
            failure = SafePathResult.NotFound;
            return null;
        }

        if (errno == 13)
        {
            throw new UnauthorizedAccessException($"open was denied (errno {errno}).");
        }

        throw new IOException($"open failed with errno {errno}.");
    }

#pragma warning disable SYSLIB1054 // a blittable-free DllImport avoids AllowUnsafeBlocks for the generator
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(byte[] path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int NativeOpenAt(int directoryDescriptor, byte[] path, int flags);

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static extern nint NativePread(int descriptor, byte[] buffer, nint count, long offset);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int NativeFstat(int descriptor, byte[] statBuffer);
#pragma warning restore SYSLIB1054

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
