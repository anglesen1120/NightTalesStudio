using System.Net;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

public sealed class OutputServingTests
{
    private static async Task<(PostRig Rig, Guid TaskId, string MptId)> SucceededAsync(
        bool subtitles = true,
        Action<AdapterOptions>? configure = null,
        bool withHost = true)
    {
        var rig = await PostRig.StartAsync(configure, withHost);
        var (taskId, mptId) = await rig.SeedCheckingAsync(subtitles, writeSrt: subtitles);
        rig.Runner.Report = PostRig.ReportJson(cues: 212);
        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);
        (await rig.H.RowAsync(taskId)).State.ShouldBe(RenderStates.Succeeded);
        return (rig, taskId, mptId);
    }

    private static FileOutputSource Source(PostRig rig) =>
        new(rig.H.Store, Options.Create(rig.H.Config), rig.H.LogFactory.CreateLogger<FileOutputSource>());

    [Fact]
    public async Task Video_XContentSha256FromCheck_LengthFromCheck()
    {
        var (rig, taskId, _) = await SucceededAsync();
        await using var _ = rig;

        using var response = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
        response.Headers.GetValues(RenderNodeHeaders.ContentSha256).ShouldHaveSingleItem().ShouldBe(PostRig.NormSha);
        response.Content.Headers.ContentLength.ShouldBe(PostRig.NormBytes.Length);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(PostRig.NormBytes);
    }

    [Fact]
    public async Task Video_Streamed_NotBuffered()
    {
        var (rig, taskId, _) = await SucceededAsync();
        await using var _ = rig;

        var opened = await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None);

        opened.IsError.ShouldBeFalse();
        await using var content = opened.Value.Content;
        var file = content.ShouldBeOfType<FileStream>();
        file.Position.ShouldBe(0);
        file.Length.ShouldBe(opened.Value.Length);
        opened.Value.Sha256.ShouldBe(PostRig.NormSha);

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NightTales.MptAdapter", "FileOutputSource.cs"));
        source.ShouldNotContain("ReadAllBytes");
        source.ShouldNotContain("ReadAllText");
        source.ShouldNotContain("SHA256.Hash");
    }

    [Fact]
    public async Task Subtitles_ServedWithCues()
    {
        var (rig, taskId, _) = await SucceededAsync();
        await using var _ = rig;

        using var response = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/x-subrip; charset=utf-8");
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(PostRig.SrtBytes);
        (await rig.H.RowAsync(taskId)).Output!.Subtitles!.Cues.ShouldBe(212);
    }

    [Fact]
    public async Task SubtitlesOff_404_AdapterOutputAbsent()
    {
        var (rig, taskId, _) = await SucceededAsync(subtitles: false);
        await using var _ = rig;

        using var response = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AdapterHarness.ReadErrorCodeAsync(response)).ShouldBe(AdapterErrorCodes.OutputAbsent);
    }

    [Fact]
    public async Task Running_409_AdapterOutputNotReady()
    {
        await using var rig = await PostRig.StartAsync(withHost: true);
        var (taskId, _) = await rig.SeedCheckingAsync();

        using var response = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await AdapterHarness.ReadErrorCodeAsync(response)).ShouldBe(AdapterErrorCodes.OutputNotReady);
        var direct = await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None);
        direct.FirstError.Code.ShouldBe(AdapterErrorCodes.OutputNotReady);
    }

    [Fact]
    public async Task Cleaned_404_AdapterOutputAbsent()
    {
        var (rig, taskId, _) = await SucceededAsync();
        await using var _ = rig;
        var row = await rig.H.RowAsync(taskId);
        await rig.H.Store.UpdateAsync(row with { CleanedAt = DateTimeOffset.UtcNow }, CancellationToken.None);

        var opened = await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None);

        opened.FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
    }

    [Fact]
    public async Task FileRemoved_404_AdapterOutputAbsent()
    {
        var (rig, taskId, mptId) = await SucceededAsync();
        await using var _ = rig;
        File.Delete(Path.Combine(rig.MptDir(mptId), "final-1.norm.mp4"));

        (await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None))
            .FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
    }

    [Fact]
    public async Task PathFromStore_NotFromRequest()
    {
        var (rig, taskId, _) = await SucceededAsync();
        await using var _ = rig;
        var secret = Path.Combine(rig.H.Root, "secret.mp4");
        File.WriteAllText(secret, "outside");

        // A traversal in the URL never reaches the file system: the id is a Guid or the route is a 404.
        using var traversal = await rig.H.SendAsync(HttpMethod.Get, $"/v1/renders/..%2F..%2Fsecret/video");
        traversal.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        // A poisoned stored backend_task_id is contained too.
        var row = await rig.H.RowAsync(taskId);
        foreach (var poisoned in new[] { "../secret.mp4", "..", "a/../../x", "/etc" })
        {
            var forged = row with { BackendTaskId = poisoned };
            (await rig.H.Store.UpdateAsync(forged, CancellationToken.None)).ShouldBeTrue();
            var stored = await rig.H.RowAsync(taskId);
            if (stored.BackendTaskId != poisoned)
            {
                continue; // Merge keeps a terminal row's id: the stored path is fixed once published.
            }

            (await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None))
                .FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
        }
    }

    [Fact]
    public async Task SymlinkedOutput_IsNotServed()
    {
        var (rig, taskId, mptId) = await SucceededAsync();
        await using var _ = rig;
        var outside = Path.Combine(rig.H.Root, "outside.mp4");
        File.WriteAllBytes(outside, PostRig.NormBytes);
        var path = Path.Combine(rig.MptDir(mptId), "final-1.norm.mp4");
        File.Delete(path);
        File.CreateSymbolicLink(path, outside);

        (await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None))
            .FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
    }

    [Fact]
    public async Task SymlinkedTaskDir_IsNotServed()
    {
        var (rig, taskId, mptId) = await SucceededAsync();
        await using var _ = rig;
        var moved = Path.Combine(rig.H.Root, "moved");
        Directory.Move(rig.MptDir(mptId), moved);
        Directory.CreateSymbolicLink(rig.MptDir(mptId), moved);

        (await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None))
            .FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
    }

    [Fact]
    public async Task OversizeVideo_IsNotServed_AndNotPublished()
    {
        var (rig, taskId, _) = await SucceededAsync(configure: o => o.MaxVideoBytes = 1024);
        await using var _ = rig;
        // The 22-byte fixture is under the cap, so shrink the cap below it afterwards.
        rig.H.Config.MaxVideoBytes = 4;
        (await Source(rig).OpenAsync(taskId, OutputKind.Video, CancellationToken.None))
            .FirstError.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);

        var (taskId2, _) = await rig.SeedCheckingAsync();
        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId2), CancellationToken.None);
        (await rig.H.RowAsync(taskId2)).Outcome!.Detail.ShouldBe("check_failed");
    }

    [Fact]
    public async Task OversizeReport_IsRejected()
    {
        await using var rig = await PostRig.StartAsync(o => o.MaxReportBytes = 100);
        var (taskId, _) = await rig.SeedCheckingAsync();

        await rig.Processor.ProcessAsync(await rig.H.RowAsync(taskId), CancellationToken.None);

        var row = await rig.H.RowAsync(taskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Detail.ShouldBe("check_failed");
    }

    [Fact]
    public async Task AtomicWrite_FailureLeavesNoTempAndKeepsOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "nts-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "render-check.json");
            await SafePath.WriteAtomicAsync(target, "one"u8.ToArray(), CancellationToken.None);
            File.ReadAllText(target).ShouldBe("one");
            await SafePath.WriteAtomicAsync(target, "two"u8.ToArray(), CancellationToken.None);
            File.ReadAllText(target).ShouldBe("two");

            var blocked = Path.Combine(root, "blocked");
            Directory.CreateDirectory(blocked);
            await Should.ThrowAsync<Exception>(() => SafePath.WriteAtomicAsync(blocked, "x"u8.ToArray(), CancellationToken.None));
            Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => SafePath.WriteAtomicAsync(target, "three"u8.ToArray(), cancelled.Token));
            File.ReadAllText(target).ShouldBe("two");
            Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\0b", false)]
    [InlineData("67b5ecfe-c283-47d1-a265-382224c1c207", true)]
    public void IsPlainName_OnlyAcceptsOneSegment(string name, bool expected) =>
        SafePath.IsPlainName(name).ShouldBe(expected);
}
