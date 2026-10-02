using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.MptAdapter;

/// <summary>One check to run: the task's MPT dir name (from the store), its facts from the submit body.</summary>
internal sealed record CheckRequest(Guid TaskId, string DirName, string Aspect, bool Subtitles);

/// <summary>The outcome of one process run; <see cref="ExitCode"/> is -1 when it timed out.</summary>
internal sealed record CheckRunResult(
    bool TimedOut,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    string ReportPath);

/// <summary>The seam <see cref="PostProcessor"/> runs checks through; tests substitute a recording fake.</summary>
internal interface ICheckRunner
{
    Task<CheckRunResult> RunAsync(CheckRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// D6a's <c>render_check.py run --normalize</c>: one argument per list element, no shell, no stdin, a minimal
/// environment (no MPT key, no adapter key - S6), and a wall-clock timeout measured from process start.
/// </summary>
internal sealed class CheckRunner : ICheckRunner
{
    /// <summary>The journal cap for each captured stream (T3 design).</summary>
    internal const int MaxCapturedBytes = 8 * 1024;

    internal const string ReportFileName = "render-check.json";

    internal const string ChildPath = "/usr/bin:/bin";

    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(5);

    private readonly AdapterOptions _options;
    private readonly ILogger<CheckRunner> _log;

    public CheckRunner(IOptions<AdapterOptions> options, ILogger<CheckRunner> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _options = options.Value;
        _log = log;
    }

    /// <summary>D6a's exact argument list (the interpreter is the process file name, not an element).</summary>
    internal static IReadOnlyList<string> BuildArguments(AdapterOptions options, string taskDir, string aspect, bool subtitles)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            options.CheckScriptPath,
            "run",
            "--task-dir",
            taskDir,
            "--aspect",
            aspect,
            "--subtitles",
            subtitles ? "on" : "off",
            "--normalize",
            "--out",
            Path.Combine(taskDir, ReportFileName),
        ];
    }

    /// <inheritdoc />
    public async Task<CheckRunResult> RunAsync(CheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var directory = SafePath.ResolveDirectory(_options.MptTasksDir, request.DirName, out var taskDir);
        var reportPath = Path.Combine(taskDir, ReportFileName);
        if (directory != SafePathResult.Ok)
        {
            return new CheckRunResult(false, 2, string.Empty, "the task dir is missing or not contained", TimeSpan.Zero, reportPath);
        }

        var clock = Stopwatch.StartNew();
        ISpawned child;
        try
        {
            child = Spawn(
                _options.CheckPythonPath,
                BuildArguments(_options, taskDir, request.Aspect, request.Subtitles),
                _options.CheckWorkingDirectory);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            // An interpreter or working directory that cannot start is an environment problem (exit 2 class).
            return new CheckRunResult(false, 2, string.Empty, "the check process could not start", clock.Elapsed, reportPath);
        }

        using (child)
        {
            var stdout = CaptureAsync(child.StandardOutput);
            var stderr = CaptureAsync(child.StandardError);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.CheckTimeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var timedOut = false;
            var exit = -1;
            try
            {
                exit = await child.WaitAsync().WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await child.TerminateAsync(
                        TimeSpan.FromSeconds(Math.Max(0, _options.CheckKillGraceSeconds)))
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                timedOut = true;
            }

            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(DrainWait, CancellationToken.None)).ConfigureAwait(false);
            var outText = stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty;
            var errText = stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty;

            var seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
            _log.CheckFinished(request.TaskId, exit, seconds, reportPath);

            return new CheckRunResult(timedOut, timedOut ? -1 : exit, outText, errText, clock.Elapsed, reportPath);
        }
    }

    /// <summary>The spawned check process, whichever platform mechanism created it.</summary>
    private interface ISpawned : IDisposable
    {
        Stream StandardOutput { get; }

        Stream StandardError { get; }

        /// <summary>Resolves with the exit code; not cancellable - <see cref="TerminateAsync"/> is what unblocks it.</summary>
        Task<int> WaitAsync();

        /// <summary>SIGTERM the whole group, wait the grace for the GROUP (not only the root), then SIGKILL it.</summary>
        Task TerminateAsync(TimeSpan grace);
    }

    private static ISpawned Spawn(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // The node is Linux and the developer Mac is Darwin; neither ships a setsid binary, so the child is
        // spawned by posix_spawn with POSIX_SPAWN_SETPGROUP: libc puts it in its own process group (pgid = its
        // pid) before it execs, so kill(-pid, sig) reaches the whole tree - including grandchildren the parent
        // orphans when it dies on SIGTERM - without a per-PID snapshot that could hit a recycled PID (DELTA-2).
        if (!OperatingSystem.IsWindows())
        {
            return SpawnedChild.Start(executable, arguments, workingDirectory);
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        info.Environment["PATH"] = ChildPath;
        return new ProcessChild(info);
    }

    /// <summary>The Windows fallback: job-object-style <see cref="Process.Kill(bool)"/> of the whole tree.</summary>
    private sealed class ProcessChild : ISpawned
    {
        private readonly Process _process;

        internal ProcessChild(ProcessStartInfo info)
        {
            _process = new Process { StartInfo = info };
            _process.Start();
            _process.StandardInput.Close();
        }

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public async Task<int> WaitAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            return _process.ExitCode;
        }

        public async Task TerminateAsync(TimeSpan grace)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            try
            {
                await _process.WaitForExitAsync().WaitAsync(grace + DrainWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        public void Dispose() => _process.Dispose();
    }

    /// <summary>
    /// A <c>posix_spawn</c> child in its own process group. Pipes are opened with <c>pipe</c>, wired to the
    /// child's fds 0/1/2 by file actions, and read through <see cref="FileStream"/>s.
    /// </summary>
    private sealed class SpawnedChild : ISpawned
    {
        private const int SIGTERM = 15;
        private const int SIGKILL = 9;
        private const int ESRCH = 3;
        private const int EINTR = 4;
        private const int F_SETFD = 2;
        private const int FD_CLOEXEC = 1;
        private const short POSIX_SPAWN_SETPGROUP = 0x02;

        private readonly int _pid;
        private readonly FileStream _stdout;
        private readonly FileStream _stderr;
        private readonly Task<int> _wait;

        private SpawnedChild(int pid, int stdoutFd, int stderrFd)
        {
            _pid = pid;
            _stdout = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stdoutFd, ownsHandle: true), FileAccess.Read);
            _stderr = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(stderrFd, ownsHandle: true), FileAccess.Read);
            _wait = Task.Run(WaitForExit);
        }

        public Stream StandardOutput => _stdout;

        public Stream StandardError => _stderr;

        public Task<int> WaitAsync() => _wait;

        internal static SpawnedChild Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            var allocated = new List<IntPtr>();
            var fileActions = Marshal.AllocHGlobal(1024);
            var attributes = Marshal.AllocHGlobal(1024);
            var stdinFd = -1;
            int stdoutRead = -1, stdoutWrite = -1, stderrRead = -1, stderrWrite = -1;
            try
            {
                CheckSpawnResult(posix_spawn_file_actions_init(fileActions));
                CheckSpawnResult(posix_spawnattr_init(attributes));
                CheckSpawnResult(posix_spawnattr_setpgroup(attributes, 0));
                CheckSpawnResult(posix_spawnattr_setflags(attributes, POSIX_SPAWN_SETPGROUP));

                stdinFd = NativeOpenDevNull();
                PipeCloexec(ref stdoutRead, ref stdoutWrite);
                PipeCloexec(ref stderrRead, ref stderrWrite);

                CheckSpawnResult(posix_spawn_file_actions_adddup2(fileActions, stdinFd, 0));
                CheckSpawnResult(posix_spawn_file_actions_adddup2(fileActions, stdoutWrite, 1));
                CheckSpawnResult(posix_spawn_file_actions_adddup2(fileActions, stderrWrite, 2));
                CheckSpawnResult(posix_spawn_file_actions_addchdir_np(fileActions, Terminated(workingDirectory)));

                var argv = AllocStringArray(allocated, [executable, .. arguments]);
                var envp = AllocStringArray(allocated, ["PATH=" + ChildPath]);
                var result = posix_spawn(
                    out var pid,
                    Terminated(executable),
                    fileActions,
                    attributes,
                    argv,
                    envp);
                if (result != 0)
                {
                    throw new Win32Exception(result, "posix_spawn failed");
                }

                _ = close(stdinFd);
                stdinFd = -1;
                _ = close(stdoutWrite);
                stdoutWrite = -1;
                _ = close(stderrWrite);
                stderrWrite = -1;
                var child = new SpawnedChild(pid, stdoutRead, stderrRead);
                stdoutRead = stderrRead = -1;
                return child;
            }
            finally
            {
                _ = posix_spawn_file_actions_destroy(fileActions);
                _ = posix_spawnattr_destroy(attributes);
                Marshal.FreeHGlobal(fileActions);
                Marshal.FreeHGlobal(attributes);
                foreach (var pointer in allocated)
                {
                    Marshal.FreeHGlobal(pointer);
                }

                foreach (var fd in new[] { stdinFd, stdoutRead, stdoutWrite, stderrRead, stderrWrite })
                {
                    if (fd >= 0)
                    {
                        _ = close(fd);
                    }
                }
            }
        }

        public async Task TerminateAsync(TimeSpan grace)
        {
            // TERM the group: a descendant re-parented by a dying root still carries the pgid, so it dies too.
            SignalGroup(SIGTERM);

            // DELTA-1: the grace covers the whole group, not only the root.
            var clock = Stopwatch.StartNew();
            while (GroupAlive() && clock.Elapsed < grace)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (GroupAlive())
            {
                SignalGroup(SIGKILL);
            }

            // Killed descendants are reaped by init after a short delay; give them a bounded moment so an
            // immediate kill -0 does not see a zombie.
            var drained = Stopwatch.StartNew();
            while (GroupAlive() && drained.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(25).ConfigureAwait(false);
            }

            try
            {
                await _wait.WaitAsync(DrainWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A process that survives SIGKILL is the OS's problem; the run is reported regardless.
            }
        }

        private void SignalGroup(int signal)
        {
            // pgid == _pid by POSIX_SPAWN_SETPGROUP. The group call NEVER touches our own group: a dead
            // group answers ESRCH and nothing is signalled.
            if (kill(-_pid, signal) != 0 && Marshal.GetLastPInvokeError() != ESRCH)
            {
                // Only reachable before the pgid settles or on EPERM; signal the root alone, never our group.
                _ = kill(_pid, signal);
            }
        }

        private bool GroupAlive() => kill(-_pid, 0) == 0 || Marshal.GetLastPInvokeError() != ESRCH;

        private int WaitForExit()
        {
            int status;
            while (waitpid(_pid, out status, 0) < 0)
            {
                if (Marshal.GetLastPInvokeError() != EINTR)
                {
                    return -1;
                }
            }

            if ((status & 0x7f) == 0)
            {
                return (status >> 8) & 0xff;
            }

            var signal = status & 0x7f;
            return signal != 0x7f ? 128 + signal : -1;
        }

        public void Dispose()
        {
            _stdout.Dispose();
            _stderr.Dispose();
        }

        private static void CheckSpawnResult(int result)
        {
            if (result != 0)
            {
                throw new Win32Exception(result, "posix_spawn setup failed");
            }
        }

        private static int NativeOpenDevNull()
        {
            var fd = open(Terminated("/dev/null"), 0);
            if (fd < 0)
            {
                throw new IOException($"open(/dev/null) failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            return fd;
        }

        private static void PipeCloexec(ref int read, ref int write)
        {
            var fds = new int[2];
            if (pipe(fds) != 0)
            {
                throw new IOException($"pipe failed with errno {Marshal.GetLastPInvokeError()}.");
            }

            read = fds[0];
            write = fds[1];
            _ = fcntl(read, F_SETFD, FD_CLOEXEC);
            _ = fcntl(write, F_SETFD, FD_CLOEXEC);
        }

        private static byte[] Terminated(string value)
        {
            var bytes = Encoding.UTF8.GetByteCount(value) + 1;
            var buffer = new byte[bytes];
            Encoding.UTF8.GetBytes(value, buffer);
            return buffer;
        }

        private static IntPtr AllocStringArray(List<IntPtr> allocated, IReadOnlyList<string> values)
        {
            var block = Marshal.AllocHGlobal((values.Count + 1) * IntPtr.Size);
            allocated.Add(block);
            for (var index = 0; index < values.Count; index++)
            {
                var bytes = Terminated(values[index]);
                var entry = Marshal.AllocHGlobal(bytes.Length);
                allocated.Add(entry);
                Marshal.Copy(bytes, 0, entry, bytes.Length);
                Marshal.WriteIntPtr(block, index * IntPtr.Size, entry);
            }

            Marshal.WriteIntPtr(block, values.Count * IntPtr.Size, IntPtr.Zero);
            return block;
        }

#pragma warning disable SYSLIB1054 // a blittable-free DllImport avoids AllowUnsafeBlocks for the generator
        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn(
            out int pid,
            byte[] path,
            IntPtr fileActions,
            IntPtr attributes,
            IntPtr argv,
            IntPtr environment);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn_file_actions_init(IntPtr fileActions);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn_file_actions_adddup2(IntPtr fileActions, int fd, int newFd);

        [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np", SetLastError = true)]
        private static extern int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, byte[] path);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawnattr_init(IntPtr attributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawnattr_destroy(IntPtr attributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawnattr_setpgroup(IntPtr attributes, int processGroup);

        [DllImport("libc", SetLastError = true)]
        private static extern int pipe([In, Out] int[] fds);

        [DllImport("libc")]
        private static extern int fcntl(int fd, int command, int argument);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, out int status, int options);

        [DllImport("libc")]
        private static extern int close(int fd);

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int open(byte[] path, int flags);
#pragma warning restore SYSLIB1054
    }

    /// <summary>Keeps the first <see cref="MaxCapturedBytes"/> bytes and drains the rest so the child never blocks.</summary>
    private static async Task<string> CaptureAsync(Stream stream)
    {
        var buffer = new byte[4096];
        using var kept = new MemoryStream();
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var room = MaxCapturedBytes - (int)kept.Length;
            if (room > 0)
            {
                kept.Write(buffer, 0, Math.Min(room, read));
            }

            if (read > room)
            {
                truncated = true;
            }
        }

        var text = new UTF8Encoding(false, false).GetString(kept.GetBuffer(), 0, (int)kept.Length);
        return truncated ? text.TrimEnd('\uFFFD') : text;
    }
}
