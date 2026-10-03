using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// Independent QA probes for 11.3.5-T3 (boundary, hostile-name, race and process-tree cases the implementer's
/// tests do not cover). They assert the CORRECT behaviour; a failure is a defect, not a test to relax.
/// </summary>
public sealed class QaT3ProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nts-qa-t3-" + Guid.NewGuid().ToString("N"));

    public QaT3ProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AdapterOptions ShellOptions(string script, int timeoutSeconds = 30, int graceSeconds = 1)
    {
        var path = Path.Combine(_root, "fake-check.sh");
        File.WriteAllText(path, script);
        return new AdapterOptions
        {
            MptRoot = Path.Combine(_root, "mpt"),
            DataDir = Path.Combine(_root, "data"),
            CheckPythonPath = "/bin/sh",
            CheckScriptPath = path,
            CheckWorkingDirectory = _root,
            CheckTimeoutSeconds = timeoutSeconds,
            CheckKillGraceSeconds = graceSeconds,
        };
    }

    private static CheckRunner Runner(AdapterOptions options) =>
        new(Options.Create(options), NullLogger<CheckRunner>.Instance);

    private static string MakeTaskDir(AdapterOptions options, string name)
    {
        var dir = Path.Combine(options.MptTasksDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsAlive(int pid)
    {
        using var probe = Process.Start(new ProcessStartInfo("/bin/kill")
        {
            ArgumentList = { "-0", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        probe.WaitForExit();
        return probe.ExitCode == 0;
    }

    // ---- process tree -------------------------------------------------------------------------------------

    [Fact]
    public async Task Timeout_KillsGrandchildToo_WhenParentDiesOnSigterm()
    {
        var pidFile = Path.Combine(_root, "grandchild.pid");
        var options = ShellOptions($"sleep 300 &\necho $! > '{pidFile}'\nwait\n", timeoutSeconds: 1, graceSeconds: 2);
        MakeTaskDir(options, "t1");

        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            IsAlive(pid).ShouldBeFalse("a timed-out check must not leave its child processes running (D6a: process tree killed)");
        }
        finally
        {
            if (IsAlive(pid))
            {
                using var k = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-9", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })!;
                k.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task Timeout_KillsChildAndGreatGrandchild_AndSignalsNothingOutsideTheGroup()
    {
        var childFile = Path.Combine(_root, "child.pid");
        var deepFile = Path.Combine(_root, "deep.pid");
        var script = $"sh -c 'sleep 300 & echo $! > \"{deepFile}\"; wait' &\necho $! > '{childFile}'\nwait\n";
        var options = ShellOptions(script, timeoutSeconds: 1, graceSeconds: 2);
        MakeTaskDir(options, "t1");

        // A bystander in the test host's own process group: the group kill must never reach it (DELTA-2).
        using var bystander = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "300" }, UseShellExecute = false })!;
        try
        {
            var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

            result.TimedOut.ShouldBeTrue();
            var child = int.Parse(File.ReadAllText(childFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var deep = int.Parse(File.ReadAllText(deepFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            try
            {
                IsAlive(child).ShouldBeFalse("the intermediate child must die with the group");
                IsAlive(deep).ShouldBeFalse("the great-grandchild must die with the group");
            }
            finally
            {
                foreach (var pid in new[] { child, deep }.Where(IsAlive))
                {
                    using var k = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-9", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })!;
                    k.WaitForExit();
                }
            }

            bystander.HasExited.ShouldBeFalse("a process outside the check's group must not be signalled");
        }
        finally
        {
            if (!bystander.HasExited)
            {
                bystander.Kill();
            }
        }
    }

    [Fact]
    public async Task Timeout_KillsTree_WhenParentIgnoresSigterm()
    {
        var pidFile = Path.Combine(_root, "gc2.pid");
        var options = ShellOptions($"trap '' TERM\nsleep 300 &\necho $! > '{pidFile}'\nwait\nsleep 300\n", timeoutSeconds: 1, graceSeconds: 1);
        MakeTaskDir(options, "t1");

        var sw = Stopwatch.StartNew();
        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var alive = IsAlive(pid);
        if (alive)
        {
            using var k = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-9", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })!;
            k.WaitForExit();
        }

        alive.ShouldBeFalse();
    }

    [Fact]
    public async Task Timeout_JustInside_DoesNotTimeOut()
    {
        var options = ShellOptions("sleep 1\n", timeoutSeconds: 4);
        MakeTaskDir(options, "t1");

        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.TimedOut.ShouldBeFalse();
        result.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Timeout_JustOutside_TimesOutPromptly()
    {
        var options = ShellOptions("sleep 20\n", timeoutSeconds: 1, graceSeconds: 1);
        MakeTaskDir(options, "t1");

        var sw = Stopwatch.StartNew();
        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        result.ExitCode.ShouldBe(-1);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveTimeout_IsNotAnInstantFailure(int seconds)
    {
        // A misconfigured 0/negative timeout must not turn every check into check_timeout instantly,
        // and must not hang forever either: the runner clamps to 1 s, so a 3 s script times out in about 1 s.
        var options = ShellOptions("sleep 3\n", timeoutSeconds: seconds, graceSeconds: 1);
        MakeTaskDir(options, "t1");

        var sw = Stopwatch.StartNew();
        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        sw.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(800));
    }

    // ---- hostile names -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a\0b")]
    [InlineData("x/")]
    [InlineData("/etc")]
    [InlineData("..\\x")]
    [InlineData("")]
    public void HostileDirNames_AreRefused_ByEveryEntryPoint(string name)
    {
        var options = ShellOptions("true\n");
        MakeTaskDir(options, "legit");
        File.WriteAllText(Path.Combine(options.MptTasksDir, "legit", "render-check.json"), "{}");

        SafePath.ResolveDirectory(options.MptTasksDir, name, out _).ShouldBe(SafePathResult.Escapes);
        SafePath.OpenRead(options.MptTasksDir, name, "render-check.json", 1024, out var stream).ShouldBe(SafePathResult.Escapes);
        stream.ShouldBeNull();
    }

    [Fact]
    public void UnicodeAndVeryLongNames_DoNotThrow_AndStayContained()
    {
        var options = ShellOptions("true\n");
        var unicode = "tâche-é-日本-😀";
        var dir = MakeTaskDir(options, unicode);
        File.WriteAllText(Path.Combine(dir, "f.json"), "{}");

        SafePath.OpenRead(options.MptTasksDir, unicode, "f.json", 1024, out var ok).ShouldBe(SafePathResult.Ok);
        ok!.Dispose();

        // Visually-similar separators are plain file-name characters on Unix, not path separators.
        var lookalike = "a\u2215b";
        Should.NotThrow(() => SafePath.OpenRead(options.MptTasksDir, lookalike, "f.json", 1024, out _));

        var longName = new string('x', 5000);
        Should.NotThrow(() => SafePath.OpenRead(options.MptTasksDir, longName, "f.json", 1024, out _));
        Should.NotThrow(() => SafePath.ResolveDirectory(options.MptTasksDir, longName, out _));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a\0b")]
    public async Task PostProcessor_HostileBackendTaskId_FailsCheckFailed_WithoutRunningTheTool(string hostile)
    {
        await using var rig = await PostRig.StartAsync();
        var taskId = Guid.CreateVersion7();
        var adapterDir = Path.Combine(rig.H.Config.AdapterTasksDir, taskId.ToString("D"));
        Directory.CreateDirectory(adapterDir);
        File.WriteAllText(Path.Combine(adapterDir, SubmitMarker.FileName), "{\"aspect\":\"landscape\",\"subtitles\":true}");
        await rig.H.SeedAsync(AdapterHarness.NewRecord(
            taskId,
            Guid.CreateVersion7(),
            backendTaskId: hostile,
            state: RenderStates.Running,
            stage: RenderStages.Checking,
            percent: 100));

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        rig.Runner.Requests.ShouldBeEmpty();
        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        row.Outcome.Detail.ShouldBe("check_failed");
    }

    // ---- TOCTOU --------------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenRead_SymlinkSwappedBetweenCheckAndOpen_NeverYieldsTheOutsideFile()
    {
        var options = ShellOptions("true\n");
        var dir = MakeTaskDir(options, "victim");
        var outside = Path.Combine(_root, "outside-secret.bin");
        const string secret = "OUTSIDE-SECRET-OUTSIDE-SECRET";
        const string honest = "honest-content-honest-con"; // same length (29 vs 25 is irrelevant: cap is generous)
        File.WriteAllText(outside, secret);
        var target = Path.Combine(dir, "final-1.norm.mp4");
        File.WriteAllText(target, honest);

        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    File.Delete(target);
                    File.CreateSymbolicLink(target, outside);
                    File.Delete(target);
                    File.WriteAllText(target, honest);
                }
                catch (IOException)
                {
                    // racing with the reader; fine
                }
            }
        }, TestContext.Current.CancellationToken);

        var leaked = 0;
        var attempts = 0;
        var deadline = Stopwatch.StartNew();
        try
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(4))
            {
                attempts++;
                SafePathResult result;
                FileStream? stream;
                try
                {
                    result = SafePath.OpenRead(options.MptTasksDir, "victim", "final-1.norm.mp4", 1 << 20, out stream);
                }
                catch (IOException)
                {
                    continue;
                }

                if (result == SafePathResult.Ok && stream is not null)
                {
                    using (stream)
                    using (var reader = new StreamReader(stream))
                    {
                        if (reader.ReadToEnd().Contains("OUTSIDE-SECRET", StringComparison.Ordinal))
                        {
                            leaked++;
                        }
                    }
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await swapper;
        }

        attempts.ShouldBeGreaterThan(100);
        leaked.ShouldBe(0, $"{leaked} of {attempts} opens followed a symlink swapped in after the IsLink check (S8/P3 containment)");
    }

    [Fact]
    public async Task OpenRead_ParentDirectorySwappedForASymlink_NeverYieldsTheOutsideFile()
    {
        var options = ShellOptions("true\n");
        var victim = MakeTaskDir(options, "victim");
        var parked = Path.Combine(options.MptTasksDir, "victim.parked");
        var outsideDir = Path.Combine(_root, "outside-dir");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "final-1.norm.mp4"), "OUTSIDE-SECRET-OUTSIDE-SECRET");
        File.WriteAllText(Path.Combine(victim, "final-1.norm.mp4"), "honest-content-honest-con");

        using var stop = new CancellationTokenSource();
        var swaps = 0;
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Directory.Move(victim, parked);
                    Directory.CreateSymbolicLink(victim, outsideDir);
                    swaps++;
                    Directory.Delete(victim);
                    Directory.Move(parked, victim);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // racing with the reader; put the honest dir back if it is still parked
                    if (Directory.Exists(parked) && !Directory.Exists(victim))
                    {
                        Directory.Move(parked, victim);
                    }
                }
            }
        }, TestContext.Current.CancellationToken);

        var leaked = 0;
        var attempts = 0;
        var deadline = Stopwatch.StartNew();
        try
        {
            while (deadline.Elapsed < TimeSpan.FromSeconds(4))
            {
                attempts++;
                SafePathResult result;
                FileStream? stream;
                try
                {
                    result = SafePath.OpenRead(options.MptTasksDir, "victim", "final-1.norm.mp4", 1 << 20, out stream);
                }
                catch (IOException)
                {
                    continue;
                }

                if (result == SafePathResult.Ok && stream is not null)
                {
                    using (stream)
                    using (var reader = new StreamReader(stream))
                    {
                        if (reader.ReadToEnd().Contains("OUTSIDE-SECRET", StringComparison.Ordinal))
                        {
                            leaked++;
                        }
                    }
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await swapper;
        }

        swaps.ShouldBeGreaterThan(10, "the swapper must really have raced the reader");
        leaked.ShouldBe(0, $"{leaked} of {attempts} opens followed a swapped parent-directory link (S8)");
    }

    [Fact]
    public void OpenRead_ADirectoryOrFifoNamedLikeTheFile_IsRefused()
    {
        var options = ShellOptions("true\n");
        var dir = MakeTaskDir(options, "victim");
        Directory.CreateDirectory(Path.Combine(dir, "as-dir"));

        SafePath.OpenRead(options.MptTasksDir, "victim", "as-dir", 1 << 20, out var stream).ShouldBe(SafePathResult.NotFound);
        stream.ShouldBeNull();

        using var mkfifo = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo") { ArgumentList = { Path.Combine(dir, "as-fifo") }, UseShellExecute = false })!;
        mkfifo.WaitForExit();
        mkfifo.ExitCode.ShouldBe(0);
        SafePath.OpenRead(options.MptTasksDir, "victim", "as-fifo", 1 << 20, out stream).ShouldBe(SafePathResult.NotFound);
        stream.ShouldBeNull();

        File.WriteAllBytes(Path.Combine(dir, "empty.bin"), []);
        SafePath.OpenRead(options.MptTasksDir, "victim", "empty.bin", 1 << 20, out stream).ShouldBe(SafePathResult.Ok);
        stream!.Dispose();
    }

    [Fact]
    public async Task ConcurrentChecks_DoNotInheritEachOthersDescriptors()
    {
        // Each child waits until the other has certainly been spawned, then lists its own open descriptors.
        var baselineFile = Path.Combine(_root, "fds-baseline.txt");
        var options = ShellOptions($"ls /dev/fd | tr '\\n' ' ' > '{baselineFile}'\n");
        MakeTaskDir(options, "t0");
        await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t0", "landscape", true), TestContext.Current.CancellationToken);
        var baseline = File.ReadAllText(baselineFile);

        var script = $"sleep 1\nls /dev/fd | tr '\\n' ' ' > \"$(dirname \"$0\")/fds-$(basename \"$(pwd)\")-$$.txt\"\n";
        var shared = ShellOptions(script, timeoutSeconds: 20);
        MakeTaskDir(shared, "t1");
        MakeTaskDir(shared, "t2");

        var runs = new List<Task<CheckRunResult>>();
        foreach (var name in (string[])["t1", "t2"])
        {
            runs.Add(Runner(shared).RunAsync(new CheckRequest(Guid.NewGuid(), name, "landscape", true), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(runs);

        var listings = Directory.GetFiles(_root, "fds-nts-*.txt");
        listings.Length.ShouldBe(2);
        foreach (var listing in listings)
        {
            File.ReadAllText(listing).ShouldBe(baseline, "a child must hold only the descriptors a lone child holds");
        }
    }

    [Fact]
    public async Task TaskDirReplacedDuringTheCheck_IsCheckFailed()
    {
        var options = ShellOptions("true\n");
        var dir = MakeTaskDir(options, "t1");
        File.WriteAllText(Path.Combine(_root, "fake-check.sh"), $"mv '{dir}' '{dir}.old' && mkdir '{dir}'\n");

        var result = await Runner(options).RunAsync(new CheckRequest(Guid.NewGuid(), "t1", "landscape", true), TestContext.Current.CancellationToken);

        result.TimedOut.ShouldBeFalse();
        result.ExitCode.ShouldBe(2);
        result.Stderr.ShouldContain("changed during the check");
    }

    // ---- caps ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Report_ExactlyAtCap_IsAccepted_CapPlusOne_IsRejected()
    {
        var baseJson = PostRig.ReportJson();
        var cap = baseJson.Length + 64;
        var options = ShellOptions("true\n");
        options.MaxReportBytes = cap;
        var dir = MakeTaskDir(options, "t1");
        var reader = new CheckReportReader(Options.Create(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<CheckReportReader>.Instance);
        var taskId = Guid.NewGuid();

        File.WriteAllText(Path.Combine(dir, "render-check.json"), baseJson + new string(' ', cap - baseJson.Length));
        new FileInfo(Path.Combine(dir, "render-check.json")).Length.ShouldBe(cap);
        (await reader.ReadAsync(taskId, "t1", CancellationToken.None)).ShouldNotBeNull();

        File.WriteAllText(Path.Combine(dir, "render-check.json"), baseJson + new string(' ', cap - baseJson.Length + 1));
        (await reader.ReadAsync(taskId, "t1", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Video_ExactlyAtCap_Publishes_CapMinusOne_FailsCheckFailed()
    {
        await using (var rig = await PostRig.StartAsync(o => o.MaxVideoBytes = PostRig.NormBytes.Length))
        {
            var (taskId, _) = await rig.SeedCheckingAsync();
            await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);
            (await rig.H.RowAsync(taskId)).State.ShouldBe(RenderStates.Succeeded);
        }

        await using (var rig = await PostRig.StartAsync(o => o.MaxVideoBytes = PostRig.NormBytes.Length - 1))
        {
            var (taskId, _) = await rig.SeedCheckingAsync();
            await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);
            var row = await rig.H.RowAsync(taskId);
            row.State.ShouldBe(RenderStates.Failed);
            row.Outcome!.Detail.ShouldBe("check_failed");
        }
    }

    [Fact]
    public async Task Subtitles_ExactlyAtCap_Publishes_CapMinusOne_FailsCheckFailed()
    {
        await using (var rig = await PostRig.StartAsync(o => o.MaxSubtitleBytes = PostRig.SrtBytes.Length))
        {
            var (taskId, _) = await rig.SeedCheckingAsync();
            await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);
            var row = await rig.H.RowAsync(taskId);
            row.State.ShouldBe(RenderStates.Succeeded);
            row.Output!.Subtitles!.Bytes.ShouldBe(PostRig.SrtBytes.Length);
        }

        await using (var rig = await PostRig.StartAsync(o => o.MaxSubtitleBytes = PostRig.SrtBytes.Length - 1))
        {
            var (taskId, _) = await rig.SeedCheckingAsync();
            await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);
            var row = await rig.H.RowAsync(taskId);
            row.State.ShouldBe(RenderStates.Failed);
            row.Outcome!.Detail.ShouldBe("check_failed");
        }
    }

    // ---- HTTP contract -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000000000000000000000000000")]
    public async Task Output_NonUuidId_Is404_AdapterTaskUnknown(string id)
    {
        await using var rig = await PostRig.StartAsync(withHost: true);

        foreach (var route in new[] { "video", "subtitles" })
        {
            using var response = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/{id}/{route}");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("adapter_task_unknown");
        }
    }

    [Fact]
    public async Task Output_UnknownUuid_Is404_AdapterTaskUnknown_NotOutputAbsent()
    {
        await using var rig = await PostRig.StartAsync(withHost: true);

        using var response = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/{Guid.NewGuid()}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("adapter_task_unknown");
    }

    // ---- concurrency ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public async Task Concurrency_NeverExceedsConfiguredCap(int configured, int expectedMax)
    {
        await using var rig = await PostRig.StartAsync(o => o.CheckConcurrency = configured);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await rig.SeedCheckingAsync()).TaskId);
        }

        rig.Runner.Duration = TimeSpan.FromMilliseconds(150);
        await rig.Processor.StartAsync(CancellationToken.None);
        try
        {
            foreach (var id in ids)
            {
                rig.H.Feed.Publish(await rig.H.RowAsync(id));
            }

            foreach (var id in ids)
            {
                (await rig.WaitForTerminalAsync(id)).State.ShouldBe(RenderStates.Succeeded);
            }
        }
        finally
        {
            await rig.Processor.StopAsync(CancellationToken.None);
        }

        rig.Runner.Intervals.Count.ShouldBe(5);
        rig.Runner.MaxInFlight.ShouldBe(expectedMax);
    }

    [Fact]
    public async Task DuplicateFeedEntries_RunTheCheckOnce()
    {
        await using var rig = await PostRig.StartAsync(o => o.CheckConcurrency = 2);
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.Duration = TimeSpan.FromMilliseconds(200);
        await rig.Processor.StartAsync(CancellationToken.None);
        try
        {
            var row = await rig.H.RowAsync(taskId);
            rig.H.Feed.Publish(row);
            rig.H.Feed.Publish(row);
            await rig.WaitForTerminalAsync(taskId);
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }
        finally
        {
            await rig.Processor.StopAsync(CancellationToken.None);
        }

        rig.Runner.Requests.Count.ShouldBe(1);
    }
}
