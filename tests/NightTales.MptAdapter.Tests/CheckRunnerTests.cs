using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The real <see cref="CheckRunner"/> against a throwaway <c>/bin/sh</c> script standing in for
/// <c>render_check.py</c>: what is asserted is the process contract (argv, environment, timeout, capture), not
/// the tool.
/// </summary>
public sealed class CheckRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nts-check-" + Guid.NewGuid().ToString("N"));

    public CheckRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AdapterOptions Options(string script) => new()
    {
        MptRoot = Path.Combine(_root, "mpt"),
        CheckPythonPath = "/bin/sh",
        CheckScriptPath = WriteScript(script),
        CheckWorkingDirectory = _root,
        CheckTimeoutSeconds = 30,
        CheckKillGraceSeconds = 1,
    };

    private string WriteScript(string body)
    {
        var path = Path.Combine(_root, "fake-check.sh");
        File.WriteAllText(path, body);
        return path;
    }

    private static string TaskDir(AdapterOptions options, string name)
    {
        var dir = Path.Combine(options.MptTasksDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CheckRunner Runner(AdapterOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), NullLogger<CheckRunner>.Instance);

    [Fact]
    public async Task Arguments_MatchD6a_ListNoShell()
    {
        var args = Path.Combine(_root, "args.txt");
        var options = Options($"printf '%s\\n' \"$@\" > '{args}'\nprintf 'PATH=%s\\n' \"$PATH\" >> '{args}'\nenv | grep -c NTS_MPT_API_KEY >> '{args}'\n");
        const string dirName = "t $(touch pwned);x y";
        var dir = TaskDir(options, dirName);
        // The assembly's TestEnvironment already exports NTS_MPT_API_KEY to this process (never mutate it here: the
        // other suites run in parallel and read it); the child must still not see it.
        Environment.GetEnvironmentVariable("NTS_MPT_API_KEY").ShouldNotBeNullOrEmpty();
        await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), dirName, "portrait", Subtitles: false), CancellationToken.None);

        File.ReadAllLines(args).ShouldBe(
        [
            "run", "--task-dir", dir, "--aspect", "portrait", "--subtitles", "off", "--normalize",
            "--out", Path.Combine(dir, "render-check.json"),
            "PATH=/usr/bin:/bin",
            "0",
        ]);
        File.Exists(Path.Combine(_root, "pwned")).ShouldBeFalse();
        Directory.GetFiles(_root, "pwned*", SearchOption.AllDirectories).ShouldBeEmpty();
        CheckRunner.BuildArguments(options, dir, "landscape", subtitles: true)[0].ShouldBe(options.CheckScriptPath);
    }

    [Theory]
    [InlineData("16:9", "landscape")]
    [InlineData("9:16", "portrait")]
    public void Aspect_FromSubmitBody(string videoAspect, string expected) =>
        SubmitMarker.AspectFor(videoAspect).ShouldBe(expected);

    [Fact]
    public async Task Timeout_KillsProcess_FailedCheckTimeout()
    {
        var pidFile = Path.Combine(_root, "pid.txt");
        var options = Options($"echo $$ > '{pidFile}'\nexec sleep 60\n");
        options.CheckTimeoutSeconds = 1;
        TaskDir(options, "t1");

        var watch = Stopwatch.StartNew();
        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var alive = true;
        try
        {
            using var process = Process.GetProcessById(pid);
            alive = !process.HasExited;
        }
        catch (ArgumentException)
        {
            alive = false;
        }

        alive.ShouldBeFalse();
    }

    [Fact]
    public async Task Exit_CodesAreReturnedVerbatim()
    {
        foreach (var code in new[] { 0, 1, 2 })
        {
            var options = Options($"exit {code}\n");
            TaskDir(options, "t1");
            var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);
            result.ExitCode.ShouldBe(code);
            result.TimedOut.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Stdout_BoundedTo8KiB()
    {
        var options = Options("head -c 200000 /dev/zero | tr '\\0' 'a'\nhead -c 200000 /dev/zero | tr '\\0' 'b' >&2\n");
        TaskDir(options, "t1");

        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.Stdout.Length.ShouldBe(8 * 1024);
        result.Stderr.Length.ShouldBe(8 * 1024);
        result.Stdout.ShouldAllBe(c => c == 'a');
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task DirNameOutsideRoot_IsRefusedWithoutStartingTheTool(string dirName)
    {
        var marker = Path.Combine(_root, "ran.txt");
        var options = Options($"echo ran > '{marker}'\n");
        Directory.CreateDirectory(options.MptTasksDir);

        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), dirName, "landscape", true), CancellationToken.None);

        result.ExitCode.ShouldBe(2);
        File.Exists(marker).ShouldBeFalse();
    }

    [Fact]
    public async Task SymlinkedTaskDir_IsRefused()
    {
        var marker = Path.Combine(_root, "ran.txt");
        var options = Options($"echo ran > '{marker}'\n");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        Directory.CreateDirectory(options.MptTasksDir);
        Directory.CreateSymbolicLink(Path.Combine(options.MptTasksDir, "linked"), outside);

        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "linked", "landscape", true), CancellationToken.None);

        result.ExitCode.ShouldBe(2);
        File.Exists(marker).ShouldBeFalse();
    }
}
