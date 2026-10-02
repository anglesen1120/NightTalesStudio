using System.Net;
using ErrorOr;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using NightTales.RenderNode.Tests.Fakes;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The two download routes of 05.4.1-T2 §2: served only for a succeeded, not yet cleaned task; framed from the
/// seam's measured numbers (<c>Content-Length</c>) and <c>check.output.sha256</c>
/// (<c>X-Content-SHA256</c>) without ever re-hashing; and 409/404 for the not-ready and absent cases.
/// </summary>
public sealed class OutputTests
{
    [Fact]
    public async Task Video_XContentSha256FromCheck()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        var output = Records.Output();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7(), output, check: Records.CheckBlock()));

        var response = await host.GetAsync($"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
        response.Content.Headers.ContentLength.ShouldBe(output.Video.Bytes);
        response.Headers.TryGetValues(RenderNodeHeaders.ContentSha256, out var sha256).ShouldBeTrue();
        sha256!.Single().ShouldBe(output.Video.Sha256);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).LongLength.ShouldBe(output.Video.Bytes);
        host.Backend.OpenOutputCount.ShouldBe(1);
        host.Backend.LastOutputKind.ShouldBe(OutputKind.Video);
    }

    [Fact]
    public async Task Subtitles_AreServedAsSubRip()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        var output = Records.Output();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7(), output));

        var response = await host.GetAsync($"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-subrip");
        response.Headers.TryGetValues(RenderNodeHeaders.ContentSha256, out var sha256).ShouldBeTrue();
        sha256!.Single().ShouldBe(output.Subtitles!.Sha256);
        host.Backend.OpenOutputCount.ShouldBe(1);
        host.Backend.LastOutputKind.ShouldBe(OutputKind.Subtitles);
    }

    [Fact]
    public async Task Subtitles_Null_404_AdapterOutputAbsent()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7(), Records.Output(withSubtitles: false)));

        var response = await host.GetAsync($"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
        host.Backend.OpenOutputCount.ShouldBe(0);
    }

    [Fact]
    public async Task FreshTask_409_AdapterOutputNotReady()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Running(taskId, Guid.CreateVersion7()));

        var video = await host.GetAsync($"/v1/renders/{taskId}/video");
        var subtitles = await host.GetAsync($"/v1/renders/{taskId}/subtitles");

        video.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RenderNodeTestHost.ReadErrorAsync(video)).Error.Code.ShouldBe(AdapterErrorCodes.OutputNotReady);
        subtitles.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RenderNodeTestHost.ReadErrorAsync(subtitles)).Error.Code.ShouldBe(AdapterErrorCodes.OutputNotReady);
        host.Backend.OpenOutputCount.ShouldBe(0);
    }

    [Fact]
    public async Task CleanedTask_404_AdapterOutputAbsent()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        var cleanedAt = new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7(), cleanedAt: cleanedAt));

        var response = await host.GetAsync($"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
        host.Backend.OpenOutputCount.ShouldBe(0);
    }

    [Fact]
    public async Task BackendReportsOutputAbsent_404()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7()));
        host.Backend.OpenOutputError = Error.NotFound(AdapterErrorCodes.OutputAbsent);

        var response = await host.GetAsync($"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RenderNodeTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);
        host.Backend.OpenOutputCount.ShouldBe(1);
    }

    [Fact]
    public async Task ServedDownload_DisposesTheOutputStream()
    {
        // The core owns the seam's stream: a download must close it, or a FileStream-backed backend leaks a
        // file handle per request (CR-1 / PERF-02 / SEC-11.3.5-T1-01).
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7()));

        var response = await host.GetAsync($"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Backend.LastOutputStream.ShouldNotBeNull();
        host.Backend.LastOutputStream!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task FailedCopy_StillDisposesTheOutputStream()
    {
        await using var host = await RenderNodeTestHost.StartAsync();
        var taskId = Records.NewTaskId();
        await host.SeedAsync(Records.Succeeded(taskId, Guid.CreateVersion7()));
        host.Backend.OutputStreamReadError = new IOException("artefact read failed");

        try
        {
            (await host.GetAsync($"/v1/renders/{taskId}/video")).Dispose();
        }
        catch (HttpRequestException)
        {
            // A copy fault may surface as a reset connection; the assertion is about the stream, not the answer.
        }

        host.Backend.LastOutputStream.ShouldNotBeNull();
        host.Backend.LastOutputStream!.Disposed.ShouldBeTrue();
    }
}
