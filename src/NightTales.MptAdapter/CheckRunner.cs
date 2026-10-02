using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

        var info = new ProcessStartInfo(_options.CheckPythonPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _options.CheckWorkingDirectory,
        };

        foreach (var argument in BuildArguments(_options, taskDir, request.Aspect, request.Subtitles))
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        info.Environment["PATH"] = ChildPath;

        using var process = new Process { StartInfo = info };
        var clock = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            // An interpreter or working directory that cannot start is an environment problem (exit 2 class).
            return new CheckRunResult(false, 2, string.Empty, "the check process could not start", clock.Elapsed, reportPath);
        }

        process.StandardInput.Close();
        var stdout = CaptureAsync(process.StandardOutput.BaseStream);
        var stderr = CaptureAsync(process.StandardError.BaseStream);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.CheckTimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(DrainWait, CancellationToken.None)).ConfigureAwait(false);
        var outText = stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty;
        var errText = stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty;

        var exit = timedOut ? -1 : process.ExitCode;
        var seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        _log.CheckFinished(request.TaskId, exit, seconds, reportPath);

        return new CheckRunResult(timedOut, exit, outText, errText, clock.Elapsed, reportPath);
    }

    /// <summary>SIGTERM, then SIGKILL of the whole tree after the grace period (T3 design).</summary>
    private async Task TerminateAsync(Process process)
    {
        try
        {
            using var term = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList = { "-TERM", process.Id.ToString(CultureInfo.InvariantCulture) },
            });
            term?.WaitForExit(2000);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            // No kill binary: the SIGKILL below is the fallback.
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0, _options.CheckKillGraceSeconds)));
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Still running after the grace period.
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DrainWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A process that survives SIGKILL is the OS's problem; the run is reported as timed out regardless.
        }
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
