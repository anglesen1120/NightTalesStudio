using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// A task at <c>checking</c>/100 with its MPT dir on disk, wired to a <see cref="PostProcessor"/> whose check
/// runner is a scripted fake (hand-written, no mocking library).
/// </summary>
internal sealed class PostRig : IAsyncDisposable
{
    internal const string NormSha = "a84ae8a10fc22f6222e8e85ce2d0b2b5675ede40ee4a824df338b170fdbac371";
    internal static readonly byte[] NormBytes = "normalised-video-bytes"u8.ToArray();
    internal static readonly byte[] SrtBytes = "1\n00:00:00,000 --> 00:00:01,000\nhello\n"u8.ToArray();

    internal AdapterHarness H { get; private init; } = null!;

    internal ScriptedRunner Runner { get; } = new();

    internal PostProcessor Processor { get; private set; } = null!;

    internal static async Task<PostRig> StartAsync(Action<AdapterOptions>? configure = null, bool withHost = false)
    {
        var harness = await AdapterHarness.StartAsync(configure, withHost).ConfigureAwait(false);
        var rig = new PostRig { H = harness };
        var options = Options.Create(harness.Config);
        rig.Runner.Rig = rig;
        rig.Processor = new PostProcessor(
            harness.Feed,
            harness.Store,
            rig.Runner,
            new CheckReportReader(options),
            new OutputPublisher(options),
            new DiskRuleEnforcer(harness.Cleaner, harness.LogFactory.CreateLogger<DiskRuleEnforcer>(), static (_, _) => Task.CompletedTask),
            options,
            harness.LogFactory.CreateLogger<PostProcessor>());
        return rig;
    }

    internal string MptDir(string mptId) => Path.Combine(H.Config.MptTasksDir, mptId);

    /// <summary>Seeds a <c>checking</c>/100 row, its MPT dir (norm video, SRT) and the submit marker.</summary>
    internal async Task<(Guid TaskId, string MptId)> SeedCheckingAsync(
        bool subtitles = true,
        bool writeSrt = true,
        string aspect = "portrait")
    {
        var taskId = Guid.CreateVersion7();
        var mptId = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(MptDir(mptId));
        File.WriteAllBytes(Path.Combine(MptDir(mptId), "final-1.norm.mp4"), NormBytes);
        if (writeSrt)
        {
            File.WriteAllBytes(Path.Combine(MptDir(mptId), "subtitle.srt"), SrtBytes);
        }

        var adapterDir = Path.Combine(H.Config.AdapterTasksDir, taskId.ToString("D"));
        Directory.CreateDirectory(adapterDir);
        File.WriteAllText(
            Path.Combine(adapterDir, SubmitMarker.FileName),
            JsonSerializer.Serialize(new SubmitMarker(aspect, subtitles)));

        await H.SeedAsync(AdapterHarness.NewRecord(
            taskId,
            Guid.CreateVersion7(),
            backendTaskId: mptId,
            state: RenderStates.Running,
            stage: RenderStages.Checking,
            percent: 100)).ConfigureAwait(false);
        return (taskId, mptId);
    }

    /// <summary>The frozen <c>render-check.v1.ok.json</c> with this rig's real file size and a chosen cue count.</summary>
    internal static string ReportJson(int cues = 212, bool normalized = true, bool dropOutput = false, string[]? errors = null)
    {
        var node = JsonNode.Parse(FixtureFiles.Read("render-check.v1.ok.json"))!.AsObject();
        node["output"]!["bytes"] = NormBytes.Length;
        node["output"]!["normalized"] = normalized;
        node["subtitles"]!["cues"] = cues;
        node["errors"] = new JsonArray((errors ?? []).Select(static e => (JsonNode?)JsonValue.Create(e)).ToArray());
        if (dropOutput)
        {
            node["output"] = null;
        }

        return node.ToJsonString();
    }

    internal async Task<RenderTaskRecord> WaitForTerminalAsync(Guid taskId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var row = await H.RowAsync(taskId).ConfigureAwait(false);
            if (row.IsTerminal)
            {
                return row;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException("The task never reached a terminal state.");
    }

    public async ValueTask DisposeAsync()
    {
        await H.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>An <see cref="ICheckRunner"/> that writes the scripted report and returns the scripted exit.</summary>
internal sealed class ScriptedRunner : ICheckRunner
{
    private readonly object _gate = new();

    internal PostRig Rig { get; set; } = null!;

    internal string? Report { get; set; } = PostRig.ReportJson();

    internal int ExitCode { get; set; }

    internal bool TimedOut { get; set; }

    internal TimeSpan Duration { get; set; } = TimeSpan.Zero;

    internal Func<CheckRequest, Task>? OnRun { get; set; }

    internal List<CheckRequest> Requests { get; } = [];

    internal List<(DateTime Start, DateTime End)> Intervals { get; } = [];

    public async Task<CheckRunResult> RunAsync(CheckRequest request, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        lock (_gate)
        {
            Requests.Add(request);
        }

        if (OnRun is not null)
        {
            await OnRun(request).ConfigureAwait(false);
        }

        if (Duration > TimeSpan.Zero)
        {
            await Task.Delay(Duration, cancellationToken).ConfigureAwait(false);
        }

        var path = Path.Combine(Rig.MptDir(request.DirName), CheckRunner.ReportFileName);
        if (Report is not null)
        {
            await File.WriteAllTextAsync(path, Report, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            Intervals.Add((start, DateTime.UtcNow));
        }

        return new CheckRunResult(TimedOut, TimedOut ? -1 : ExitCode, string.Empty, string.Empty, Duration, path);
    }
}

public sealed class PostProcessorTests
{
    [Fact]
    public async Task SucceededPublishesOutputFromReport()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        var reportJson = PostRig.ReportJson(cues: 7);
        rig.Runner.Report = reportJson;

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Succeeded);
        row.Stage.ShouldBe(RenderStages.Done);
        row.Percent.ShouldBe(100);
        row.Outcome.ShouldBeNull();
        row.Output!.Video.Bytes.ShouldBe(PostRig.NormBytes.Length);
        row.Output.Video.Sha256.ShouldBe(PostRig.NormSha);
        row.Output.Video.Cues.ShouldBeNull();
        row.Output.Subtitles!.Cues.ShouldBe(7);
        row.Output.Subtitles.Bytes.ShouldBe(PostRig.SrtBytes.Length);
        row.Output.Subtitles.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PostRig.SrtBytes)));
        row.Output.NarrationSeconds.ShouldBe(301.37);
        JsonNode.DeepEquals(JsonNode.Parse(row.Check!.Value.GetRawText()), JsonNode.Parse(reportJson)).ShouldBeTrue();
        File.Exists(Path.Combine(rig.H.Config.AdapterTasksDir, taskId.ToString("D"), "render-check.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunsExactlyOneCheck_AspectFromSubmitBody()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync(subtitles: false, writeSrt: false, aspect: "portrait");

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);
        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var request = rig.Runner.Requests.ShouldHaveSingleItem();
        request.Aspect.ShouldBe("portrait");
        request.Subtitles.ShouldBeFalse();
        request.DirName.ShouldBe(mptId);
        (await rig.H.RowAsync(taskId)).Output!.Subtitles.ShouldBeNull();
    }

    [Fact]
    public async Task StageCheckingDuringRun()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        RenderTaskRecord? during = null;
        rig.Runner.OnRun = async _ => during = await rig.H.RowAsync(taskId);

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        during!.State.ShouldBe(RenderStates.Running);
        during.Stage.ShouldBe(RenderStages.Checking);
        during.Percent.ShouldBe(100);
    }

    [Fact]
    public async Task Concurrency1_SecondWaits()
    {
        await using var rig = await PostRig.StartAsync(o => o.CheckConcurrency = 1);
        var (first, _) = await rig.SeedCheckingAsync();
        var (second, _) = await rig.SeedCheckingAsync();
        rig.Runner.Duration = TimeSpan.FromMilliseconds(250);

        await rig.Processor.StartAsync(CancellationToken.None);
        try
        {
            rig.H.Feed.Publish(await rig.H.RowAsync(first));
            rig.H.Feed.Publish(await rig.H.RowAsync(second));
            await rig.WaitForTerminalAsync(first);
            await rig.WaitForTerminalAsync(second);
        }
        finally
        {
            await rig.Processor.StopAsync(CancellationToken.None);
        }

        var intervals = rig.Runner.Intervals.OrderBy(i => i.Start).ToList();
        intervals.Count.ShouldBe(2);
        intervals[1].Start.ShouldBeGreaterThanOrEqualTo(intervals[0].End);
    }

    [Fact]
    public async Task CheckOutputMissing_FailedCheckFailed()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.Report = PostRig.ReportJson(dropOutput: true);

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        row.Outcome.Detail.ShouldBe("check_failed");
        row.Check.ShouldNotBeNull();
    }

    [Fact]
    public async Task NotNormalized_FailedCheckFailed()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.Report = PostRig.ReportJson(normalized: false);

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        (await rig.H.RowAsync(taskId)).Outcome!.Detail.ShouldBe("check_failed");
    }

    [Fact]
    public async Task Timeout_FailedCheckTimeout()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.TimedOut = true;
        rig.Runner.Report = null;

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        row.Outcome.Detail.ShouldBe("check_timeout");
    }

    [Fact]
    public async Task Exit1_CheckFailed_ReportAttached()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.ExitCode = 1;

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Detail.ShouldBe("check_failed");
        row.Check.ShouldNotBeNull();
        row.Output.ShouldBeNull();
        rig.H.Logs.Contains("ops alert").ShouldBeFalse();
    }

    [Fact]
    public async Task Exit2_CheckFailed_PlusAlertLine()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.ExitCode = 2;
        rig.Runner.Report = null;

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        (await rig.H.RowAsync(taskId)).Outcome!.Detail.ShouldBe("check_failed");
        rig.H.Logs.Records.Count(r => r.Contains("ops alert", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ReportWithErrors_StaysSucceeded()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.Report = PostRig.ReportJson(errors: ["audio_silent"]);

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Succeeded);
        row.Check!.Value.GetProperty("errors")[0].GetString().ShouldBe("audio_silent");
    }

    [Fact]
    public async Task MissingReport_Exit0_CheckFailed()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        rig.Runner.Report = null;

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Detail.ShouldBe("check_failed");
        row.Check.ShouldBeNull();
    }

    [Fact]
    public async Task MissingSubmitMarker_FailsWithoutRunningTheTool()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, _) = await rig.SeedCheckingAsync();
        File.Delete(Path.Combine(rig.H.Config.AdapterTasksDir, taskId.ToString("D"), SubmitMarker.FileName));

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        rig.Runner.Requests.ShouldBeEmpty();
        (await rig.H.RowAsync(taskId)).Outcome!.Detail.ShouldBe("check_failed");
    }

    [Fact]
    public async Task CancelDuringChecking_EndsCancelled()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();
        rig.Runner.OnRun = async _ =>
        {
            var row = await rig.H.RowAsync(taskId);
            (await rig.H.Store.UpdateAsync(row with { CancelRequested = true }, CancellationToken.None)).ShouldBeTrue();
        };

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Cancelled);
        row.Output.ShouldBeNull();
        Directory.Exists(rig.MptDir(mptId)).ShouldBeFalse();
        Directory.Exists(Path.Combine(rig.H.Config.AdapterTasksDir, taskId.ToString("D"))).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelBeforeCheck_NeverRunsTheTool()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();
        var seeded = await rig.H.RowAsync(taskId);
        await rig.H.Store.UpdateAsync(seeded with { CancelRequested = true }, CancellationToken.None);

        await rig.Processor.ProcessAsync(seeded, CancellationToken.None);

        rig.Runner.Requests.ShouldBeEmpty();
        (await rig.H.RowAsync(taskId)).State.ShouldBe(RenderStates.Cancelled);
        Directory.Exists(rig.MptDir(mptId)).ShouldBeFalse();
    }
}
