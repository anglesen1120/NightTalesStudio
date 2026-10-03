using Microsoft.Extensions.DependencyInjection;
using NightTales.MockMpt.Backend;
using NightTales.MockMpt.Images;
using NightTales.MockMpt.Tests.TestHost;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MockMpt.Tests;

/// <summary>
/// Review fixes of 11.1.2-T2: <c>skipped:&lt;share&gt;</c> rounds against 03.4.3-T2's
/// <c>needed_guess = min(planned, ceil(MOCK_VIDEO_SECONDS ÷ video_clip_duration))</c> and not against the cap
/// (CR-1), and the backend's own cancel releases the plan sink entry (PERF-1).
/// </summary>
public sealed class ImageReportReviewTests
{
    private static ImagePlanV1 Plan(int maxImages) =>
        new(maxImages, Enumerable.Range(0, maxImages).Select(i => new string((char)('a' + i), 64)).ToList());

    private static ImageReportV1 SkippedReport(double share, int maxImages, int clipBound)
    {
        var sink = new MockImageReport();
        var taskId = Guid.CreateVersion7();
        sink.Register(taskId, Plan(maxImages));
        sink.UseReport(taskId, new ImageReportMode(ImageReportKind.Skipped, Share: share), clipBound);
        return sink.BuildReport(taskId).ShouldNotBeNull();
    }

    [Fact]
    public async Task Report_SkippedShare_AppliesToNeededNotToTheCap()
    {
        // The landscape fixture plans max_images = 4 with a 10 s clip; the default MOCK_VIDEO_SECONDS is 10, so
        // needed_guess = min(4, ceil(10 / 10)) = 1 and skipped:0.5 rounds to one of that one slot.
        ImageReportMode.TryParse("skipped:0.5", out var mode, out var error).ShouldBeTrue(error);
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { ImageReport = mode, VideoSeconds = 10 });

        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        var status = (await host.PollUntilTerminalAsync(taskId))[^1];

        status.State.ShouldBe(RenderStates.Succeeded);
        var images = status.Images!.Value;
        images.GetProperty("planned").GetInt32().ShouldBe(4);
        images.GetProperty("needed").GetInt32().ShouldBe(1);
        images.GetProperty("called").GetInt32().ShouldBe(1);
        images.GetProperty("succeeded").GetInt32().ShouldBe(0);
        images.GetProperty("unattempted").GetInt32().ShouldBe(3);
        var failed = images.GetProperty("failed").EnumerateArray().ToList();
        failed.Count.ShouldBe(1);
        failed[0].GetProperty("slot").GetInt32().ShouldBe(0);
        failed[0].GetProperty("error_code").GetString().ShouldBe("upstream_error");
    }

    [Theory]
    [InlineData(0.5, 4, 1, 1, new[] { 0 })]          // the review example: 4 planned, 1 needed
    [InlineData(0.5, 4, 4, 4, new[] { 2, 3 })]       // needed == planned: the old behaviour
    [InlineData(0.5, 4, 10, 4, new[] { 2, 3 })]      // the clip bound is capped by the plan
    [InlineData(0.5, 4, 3, 3, new[] { 1, 2 })]       // 1.5 rounds away from zero to 2
    [InlineData(0.25, 4, 2, 2, new[] { 1 })]         // 0.5 rounds away from zero
    [InlineData(0.1, 4, 2, 2, new int[0])]           // 0.2 rounds to nothing
    [InlineData(1.0, 4, 2, 2, new[] { 0, 1 })]       // everything needed is skipped, the spare plan slots are not
    public void Report_SkippedShare_RoundsAgainstNeededGuess(
        double share,
        int maxImages,
        int clipBound,
        int expectedNeeded,
        int[] expectedSlots)
    {
        var report = SkippedReport(share, maxImages, clipBound);

        report.Planned.ShouldBe(maxImages);
        report.Needed.ShouldBe(expectedNeeded);
        report.Called.ShouldBe(expectedNeeded);
        report.Succeeded.ShouldBe(expectedNeeded - expectedSlots.Length);
        report.Unattempted.ShouldBe(maxImages - expectedNeeded);
        report.Failed.Select(failure => failure.Slot).ToArray().ShouldBe(expectedSlots);
        report.Usage.Count.ShouldBe(report.Succeeded);
    }

    [Fact]
    public async Task Cancel_BackendCancel_ReleasesTheSelectorEntry()
    {
        // The core releases the plan before it calls the backend, so the HTTP route cannot tell whether the
        // backend itself leaks; call the backend's cancel directly.
        await using var host = await MockMptTestHost.StartAsync(
            MockMptTestHost.DefaultOptions with { RenderSeconds = 60 });
        var taskId = await host.SubmitAsync(SubmitBodies.LandscapeFresh());
        var id = Guid.Parse(taskId);
        host.PlanSink.BuildReport(id).ShouldNotBeNull("the submit registered the plan and the report selection");

        var store = host.Services.GetRequiredService<ITaskStore>();
        var record = (await store.TryGetAsync(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var backend = host.Services.GetRequiredService<FakeRenderBackend>();

        await backend.CancelAsync(record, TestContext.Current.CancellationToken);

        host.PlanSink.BuildReport(id).ShouldBeNull("the backend's cancel releases the plan sink entry");
    }
}
