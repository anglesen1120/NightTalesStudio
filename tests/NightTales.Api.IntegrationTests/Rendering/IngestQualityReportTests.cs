using System.Text.Json.Nodes;
using NightTales.Application.Features.Rendering.Jobs;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// The 03.4.2-T1 seam of <see cref="IngestRenderOutputJob"/>: the <c>images</c> block the terminal hook merges
/// must land inside the same <c>quality_report</c> row, in the same transaction as the assets and the episode
/// move - and a failure after the verdict was evaluated must leave nothing of it behind.
/// </summary>
/// <remarks>
/// The hook is driven through the recorded <c>IRenderTerminalHooks</c> of <see cref="IngestHarness"/>, which is
/// the registration the Worker resolves, so the job's own call order is what the assertions read.
/// </remarks>
public sealed class IngestQualityReportTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The token xunit cancels when this test's deadline passes.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The outbox type of a successful ingest.</summary>
    private const string RenderSucceeded = "render_succeeded.v1";

    /// <summary>The stored schema of the verdict this job writes.</summary>
    private const string QualitySchema = "render-quality.v1";

    /// <summary>The canary the hook's own block carries (low entropy on purpose: it reaches the commit hooks).</summary>
    private const string ImagesCanary = "nts-images-canary-aaaa";

    /// <summary>The hook's block is merged into the stored verdict, in the transaction that moves the episode.</summary>
    [Fact]
    public async Task QualityReport_ImagesMergedInSameTransaction()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));
        harness.Hooks.Images = new JsonObject
        {
            ["schema"] = "images.v1",
            ["canary"] = ImagesCanary,
        };

        // What the database shows while the hook is merging: the report and the assets are still invisible, so
        // they can only become visible together with this transaction's commit.
        string? reportDuringMerge = null;
        long? assetsDuringMerge = null;
        harness.Hooks.OnMerge = () =>
        {
            reportDuringMerge = IngestDb.Report(Database, world, "->>'schema'");
            assetsDuringMerge = IngestDb.AssetCount(Database, world);
            return ValueTask.CompletedTask;
        };

        await harness.RunAsync(world.RenderJobId);

        reportDuringMerge.ShouldBe("-", "the verdict is not readable before the transaction commits");
        assetsDuringMerge.ShouldBe(0, "neither are the assets");
        harness.Hooks.Calls.ShouldContain("images");
        harness.Hooks.Calls.ShouldContain("kept");

        // After the commit, one report carries both halves of the verdict and the episode points at the video.
        IngestDb.Row(Database, world).ShouldBe("succeeded|-|true");
        IngestDb.Report(Database, world, "->>'schema'").ShouldBe(QualitySchema);
        IngestDb.Report(Database, world, "->'images'->>'schema'").ShouldBe("images.v1");
        IngestDb.Report(Database, world, "->'images'->>'canary'").ShouldBe(ImagesCanary);
        IngestDb.AssetCount(Database, world).ShouldBe(2);
        IngestDb.Episode(Database, world).ShouldStartWith("video_review|");
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(1);
    }

    /// <summary>A failure after the verdict was evaluated leaves no report, no asset and no episode change.</summary>
    [Fact]
    public async Task MergedReport_PartialWriteImpossible()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        var subtitles = harness.Downloads.ServeSubtitles();
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, subtitles), IngestBodies.Check()));
        harness.Hooks.Images = new JsonObject { ["schema"] = "images.v1", ["canary"] = ImagesCanary };

        // The hook throws after the report and the assets are staged: the transaction must not commit any of it.
        harness.Hooks.KeptVideoFailure =
            new InvalidOperationException("scripted: the terminal hook refused after the verdict was evaluated");

        var thrown = await harness.ThrowsAsync(world.RenderJobId, 1, IngestHarness.MaxAttempts);

        thrown.Message.ShouldContain("terminal hook refused");
        harness.Hooks.Calls.ShouldContain("images");
        harness.Hooks.Calls.ShouldContain("kept", "the failure happened after the report and the assets were staged");

        IngestDb.Report(Database, world, "->>'schema'").ShouldBe("-");
        IngestDb.Report(Database, world, "->'images'->>'canary'").ShouldBe("-");
        IngestDb.AssetCount(Database, world).ShouldBe(0);
        IngestDb.Episode(Database, world).ShouldBe("story_approved|-");
        IngestDb.Outbox(Database, world, RenderSucceeded).ShouldBe(0);
        IngestDb.AuditCount(Database, world).ShouldBe(0);
        harness.TempFiles().ShouldBeEmpty();
    }

    /// <summary>The same seam on the happy path keeps the job's own checks and the hook's block apart.</summary>
    [Fact]
    public async Task MergedReport_HookBlockDoesNotReplaceThePoliciesChecks()
    {
        await using var harness = await IngestHarness.StartAsync(Database, Ct);
        var world = await IngestWorlds.SeedAsync(Database, Ct);
        var video = harness.Downloads.ServeVideo(4096);
        harness.Render.EnqueueStatus(
            IngestBodies.Succeeded(world, IngestBodies.Output(video, null), IngestBodies.Check()));
        harness.Hooks.Images = new JsonObject { ["schema"] = "images.v1", ["canary"] = ImagesCanary };

        await harness.RunAsync(world.RenderJobId);

        IngestDb.ReportChecks(Database, world).ShouldBeGreaterThanOrEqualTo(3);
        IngestDb.Report(Database, world, "->'images'->>'canary'").ShouldBe(ImagesCanary);
        harness.Hooks.Calls.ShouldContain("usage");
        harness.Hooks.Calls.ShouldContain("images");
        harness.Hooks.Calls.ShouldContain("kept");
    }
}
