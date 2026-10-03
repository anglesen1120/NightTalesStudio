using Microsoft.Extensions.Logging;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

public sealed class DiskRulesTests
{
    private static DiskRuleEnforcer Enforcer(AdapterHarness h, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(h.Cleaner, h.LogFactory.CreateLogger<DiskRuleEnforcer>(), delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task Cancelled_RemovesTaskDirAtOnce()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();
        File.Exists(Path.Combine(rig.MptDir(mptId), "final-1.norm.mp4")).ShouldBeTrue();

        await Enforcer(rig.H).OnCancelledAsync(taskId, mptId, CancellationToken.None);

        Directory.Exists(rig.MptDir(mptId)).ShouldBeFalse();
        File.Exists(Path.Combine(rig.MptDir(mptId), "final-1.norm.mp4")).ShouldBeFalse();
        Directory.Exists(Path.Combine(rig.H.Config.AdapterTasksDir, taskId.ToString("D"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Succeeded_KeepsUntilDelete()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        (await rig.H.RowAsync(taskId)).State.ShouldBe(RenderStates.Succeeded);
        File.Exists(Path.Combine(rig.MptDir(mptId), "final-1.norm.mp4")).ShouldBeTrue();
        File.Exists(Path.Combine(rig.MptDir(mptId), "subtitle.srt")).ShouldBeTrue();

        await Enforcer(rig.H).OnDeletedAsync(taskId, mptId, CancellationToken.None);

        Directory.Exists(rig.MptDir(mptId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_Keeps_ForeignCleanupAfter24h()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? waited = null;
        var enforcer = Enforcer(rig.H, async (span, token) =>
        {
            waited = span;
            await release.Task.WaitAsync(token);
        });

        var cleanup = enforcer.OnFailedAsync(taskId, mptId, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        waited.ShouldBe(TimeSpan.FromHours(24));
        cleanup.IsCompleted.ShouldBeFalse();
        Directory.Exists(rig.MptDir(mptId)).ShouldBeTrue();

        release.SetResult();
        await cleanup;

        Directory.Exists(rig.MptDir(mptId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_ThroughPostProcessor_KeepsFilesImmediately()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();
        rig.Runner.ExitCode = 1;

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        (await rig.H.RowAsync(taskId)).State.ShouldBe(RenderStates.Failed);
        File.Exists(Path.Combine(rig.MptDir(mptId), "final-1.norm.mp4")).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteThenDelete_Idempotent()
    {
        await using var rig = await PostRig.StartAsync();
        var (taskId, mptId) = await rig.SeedCheckingAsync();
        var enforcer = Enforcer(rig.H);

        await enforcer.OnDeletedAsync(taskId, mptId, CancellationToken.None);
        await Should.NotThrowAsync(() => enforcer.OnDeletedAsync(taskId, mptId, CancellationToken.None));
        await Should.NotThrowAsync(() => enforcer.OnCancelledAsync(taskId, mptId, CancellationToken.None));
    }
}
