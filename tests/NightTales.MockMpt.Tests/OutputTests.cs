using System.Net;
using System.Security.Cryptography;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// The served artefacts (11.1.2-T1 Tests, 05 D4/§2): the canned MP4 per aspect with the headers the core takes
/// from the seam, and the SRT the mock derives from the submitted <c>video_script</c>.
/// </summary>
public sealed class OutputTests
{
    [Fact]
    public async Task Video_CannedLandscape_ContentLengthAndShaMatch()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });
        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        status.State.ShouldBe(RenderStates.Succeeded);
        var video = status.Output!.Video;
        video.Bytes.ShouldBe(host.Media.Landscape.Bytes);
        video.Sha256.ShouldBe(host.Media.Landscape.Sha256);

        using var response = await host.GetAsync($"/v1/renders/{taskId}/video");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
        response.Content.Headers.ContentLength.ShouldBe(video.Bytes);
        response.Headers.GetValues(RenderNodeHeaders.ContentSha256).Single().ShouldBe(video.Sha256);

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        bytes.Length.ShouldBe((int)video.Bytes);
        Convert.ToHexStringLower(SHA256.HashData(bytes)).ShouldBe(video.Sha256);
    }

    [Fact]
    public async Task Video_Portrait_ServesThePortraitFile()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });
        var taskId = await host.SubmitAsync(SubmitBodies.PortraitFresh());
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        var video = status.Output!.Video;
        video.Sha256.ShouldBe(host.Media.Portrait.Sha256);

        using var response = await host.GetAsync($"/v1/renders/{taskId}/video");
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Convert.ToHexStringLower(SHA256.HashData(bytes)).ShouldBe(host.Media.Portrait.Sha256);
        response.Content.Headers.ContentLength.ShouldBe(host.Media.Portrait.Bytes);
    }

    [Fact]
    public async Task Subtitles_OneCuePerSentence()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });
        const string script = "Câu một. Câu hai! Câu ba?";
        var taskId = await host.SubmitAsync(SubmitBodies.Build(SubmitBodies.Landscape, script: script));
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        var subtitles = status.Output!.Subtitles;
        subtitles.ShouldNotBeNull();
        subtitles.Cues.ShouldBe(3);

        using var response = await host.GetAsync($"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-subrip");
        response.Content.Headers.ContentLength.ShouldBe(subtitles.Bytes);

        var srt = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var cues = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        cues.Length.ShouldBe(3);
        cues[0].Split('\n')[2].ShouldBe("Câu một.");
        cues[1].Split('\n')[2].ShouldBe("Câu hai!");
        cues[2].Split('\n')[2].ShouldBe("Câu ba?");
    }

    [Fact]
    public async Task Subtitles_CueTiming_UsesTheCannedDurationNotTheReportedNarration()
    {
        // MOCK_VIDEO_SECONDS is deliberately far from the 10 s canned file the mock serves.
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1, VideoSeconds = 40 });
        const string script = "Câu một. Câu hai.";
        var taskId = await host.SubmitAsync(SubmitBodies.Build(SubmitBodies.Landscape, script: script));
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        // The status still reports the requested narration length ...
        status.Output!.NarrationSeconds.ShouldBe(40);

        using var response = await host.GetAsync($"/v1/renders/{taskId}/subtitles");
        var srt = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // ... but the cues span the canned 10 s file, not the reported 40 s.
        srt.ShouldContain("00:00:00,000 --> 00:00:04,999");
        srt.ShouldContain("00:00:05,000 --> 00:00:10,000");
        srt.ShouldNotContain("00:00:40,000");
    }

    [Fact]
    public async Task Subtitles_Disabled_404OutputAbsent()
    {
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 1 });
        var taskId = await host.SubmitAsync(
            SubmitBodies.Build(SubmitBodies.Landscape, subtitleEnabled: false));
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        status.Output!.Subtitles.ShouldBeNull();

        using var response = await host.GetAsync($"/v1/renders/{taskId}/subtitles");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MockMptTestHost.ReadErrorAsync(response)).Error.Code.ShouldBe(AdapterErrorCodes.OutputAbsent);

        // The video is still served.
        using var video = await host.GetAsync($"/v1/renders/{taskId}/video");
        video.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void Subtitles_SplitSentences_HandlesTerminatorRunsAndNewlines()
    {
        var sentences = Media.SubtitleScript.SplitSentences("Một... Hai!!\nBa? Bốn");

        sentences.ShouldBe(["Một...", "Hai!!", "Ba?", "Bốn"]);
        Media.SubtitleScript.Build("Một.", 10).Content.Length.ShouldBeGreaterThan(0);
    }
}
