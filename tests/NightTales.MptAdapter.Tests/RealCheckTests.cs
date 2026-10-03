using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The real <c>worker-node/checks/render_check.py</c> (11.3.5-T3). Needs python3.12 and ffmpeg, so it is a
/// node/Mac run and never part of CI (<c>Category=Node</c>, the CI unit step filters it out). No binary media is
/// committed (05 D14): the task dir is generated with lavfi.
/// </summary>
[Trait("Category", "Node")]
public sealed class RealCheckTests
{
    private static readonly string[] PythonCandidates =
    [
        "/usr/bin/python3.12",
        "/opt/homebrew/bin/python3.12",
        "/usr/local/bin/python3.12",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/python3.12"),
    ];

    private static readonly string[] FfmpegDirectories = ["/usr/bin", "/opt/homebrew/bin", "/usr/local/bin"];

    [Fact]
    public async Task SelfTest_Passes()
    {
        var tools = Tools.Resolve();

        var run = await RunAsync(tools, ["--self-test"], tools.RepoRoot);

        run.ExitCode.ShouldBe(0, run.Stderr);
    }

    [Fact]
    public async Task LavfiTaskDir_ExitsZero_AndNormalizesTheOutput()
    {
        var tools = Tools.Resolve();
        var taskDir = Path.Combine(Path.GetTempPath(), "nts-real-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskDir);
        try
        {
            var media = await RunProcessAsync(
                tools.FfmpegPath,
                [
                    "-v", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30:duration=3",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
                    Path.Combine(taskDir, "final-1.mp4"),
                ],
                taskDir,
                tools.ChildPath);
            media.ExitCode.ShouldBe(0, media.Stderr);
            var audio = await RunProcessAsync(
                tools.FfmpegPath,
                ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", Path.Combine(taskDir, "audio.mp3")],
                taskDir,
                tools.ChildPath);
            audio.ExitCode.ShouldBe(0, audio.Stderr);
            await File.WriteAllTextAsync(Path.Combine(taskDir, "subtitle.srt"), "1\n00:00:00,000 --> 00:00:01,500\nhello night\n\n2\n00:00:01,500 --> 00:00:02,900\nsecond line\n", TestContext.Current.CancellationToken);

            var options = new AdapterOptions { CheckScriptPath = tools.ScriptPath };
            var run = await RunAsync(tools, CheckRunner.BuildArguments(options, taskDir, "portrait", true).Skip(1).ToArray(), taskDir);

            run.ExitCode.ShouldBe(0, run.Stderr);
            using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(taskDir, CheckRunner.ReportFileName), TestContext.Current.CancellationToken));
            report.RootElement.GetProperty("output").GetProperty("normalized").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(taskDir, recursive: true);
        }
    }

    private static Task<(int ExitCode, string Stderr)> RunAsync(Tools tools, IReadOnlyList<string> arguments, string workingDirectory) =>
        RunProcessAsync(tools.PythonPath, [tools.ScriptPath, .. arguments], workingDirectory, tools.ChildPath);

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(
        string file,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string path)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["PATH"] = path;
        using var process = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await stdout;
        return (process.ExitCode, await stderr);
    }

    private sealed record Tools(string PythonPath, string FfmpegPath, string ScriptPath, string RepoRoot, string ChildPath)
    {
        internal static Tools Resolve()
        {
            var python = PythonCandidates.FirstOrDefault(File.Exists);
            var ffmpegDirectory = FfmpegDirectories.FirstOrDefault(directory => File.Exists(Path.Combine(directory, "ffmpeg")));
            Assert.SkipWhen(python is null || ffmpegDirectory is null, "Category=Node needs python3.12 and ffmpeg on the machine.");

            var root = AppContext.BaseDirectory;
            while (root is not null && !File.Exists(Path.Combine(root, "worker-node", "checks", "render_check.py")))
            {
                root = Path.GetDirectoryName(root);
            }

            root.ShouldNotBeNull("worker-node/checks/render_check.py was not found above the test binary");
            return new Tools(
                python!,
                Path.Combine(ffmpegDirectory!, "ffmpeg"),
                Path.Combine(root, "worker-node", "checks", "render_check.py"),
                root,
                string.Join(':', FfmpegDirectories.Append("/bin")));
        }
    }
}
