using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// 05 D5's <c>failed_stage</c> → outcome map (11 README D6): <c>audio</c>, <c>materials</c> and <c>video</c>
/// keep their own codes, everything else is a node error whose detail is the stage, and a missing stage is
/// <c>unknown_stage</c>. The mapping reads the closed stage and never MPT's free-form <c>error</c> text (S4).
/// </summary>
public sealed class FailureMappingTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    private static readonly string[] Timestamps = ["submitted_at", "started_at", "finished_at"];

    [Fact]
    public async Task Audio_TtsFailed()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, -1, 20, failedStage: "audio");

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Stage.ShouldBe(RenderStages.Voice);
        row.Percent.ShouldBe(20);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);
        row.Outcome.Detail.ShouldBe("audio");
        row.Outcome.Message.ShouldBe(OutcomeMessages.For(RenderOutcomeCodes.TtsFailed));

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.failed-tts.json",
            await harness.StatusJsonAsync(record.TaskId),
            Timestamps);
    }

    [Fact]
    public async Task Materials_ImageGenerationFailed()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, -1, 40, failedStage: "materials");

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.Outcome!.Code.ShouldBe(
            RenderOutcomeCodes.ImageGenerationFailed,
            "03.4.2-T1 reclassifies a moderation block from images.v1 later");
        row.Outcome.Detail.ShouldBe("materials");
        row.Stage.ShouldBe(RenderStages.Images);
        row.Percent.ShouldBe(40);

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.failed-materials.json",
            await harness.StatusJsonAsync(record.TaskId),
            [.. Timestamps, "images"]);
    }

    [Fact]
    public async Task Video_RenderAssemblyFailed()
    {
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, -1, 75, failedStage: "video");

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderAssemblyFailed);
        row.Outcome.Detail.ShouldBe("video");
        row.Stage.ShouldBe(RenderStages.Assembling);
        row.Percent.ShouldBe(75);

        StatusComparison.ShouldMatchFixture(
            "render-status.v1.failed-assembly.json",
            await harness.StatusJsonAsync(record.TaskId),
            [.. Timestamps, "images"]);
    }

    [Theory]
    [InlineData("preflight")]
    [InlineData("script")]
    [InlineData("terms")]
    [InlineData("pipeline")]
    public async Task PreflightAndPipeline_RenderNodeErrorWithStageDetail(string stage)
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, -1, 10, failedStage: stage);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        row.Outcome.Detail.ShouldBe(stage);
        row.Outcome.Message.ShouldBe(OutcomeMessages.For(RenderOutcomeCodes.RenderNodeError));
    }

    [Fact]
    public async Task UnknownStage_RenderNodeError_UnknownStage()
    {
        await using var harness = await AdapterHarness.StartAsync();

        var noStage = AdapterHarness.NewRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), backendTaskId: MptTaskId);
        await harness.SeedAsync(noStage);
        harness.Mpt.Script(MptTaskId, -1, 10);
        await harness.PollOnceAsync();

        var missing = await harness.RowAsync(noStage.TaskId);
        missing.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        missing.Outcome.Detail.ShouldBe(OutcomeMapper.UnknownStage);

        // An unknown stage is reported verbatim, truncated to 05 D5's 64-character detail.
        var unknownId = "816973fc-1cd3-40b1-a1e1-0acd00781c30";
        var unknown = AdapterHarness.NewRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), backendTaskId: unknownId);
        await harness.SeedAsync(unknown);
        harness.Mpt.Script(unknownId, -1, 10, failedStage: "mystery-stage");
        await harness.PollOnceAsync();
        (await harness.RowAsync(unknown.TaskId)).Outcome!.Detail.ShouldBe("mystery-stage");

        var longId = "02c90883-e314-4e30-b754-d5c8441b8523";
        var longStage = new string('x', 80);
        var truncated = AdapterHarness.NewRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), backendTaskId: longId);
        await harness.SeedAsync(truncated);
        harness.Mpt.Script(longId, -1, 10, failedStage: longStage);
        await harness.PollOnceAsync();
        (await harness.RowAsync(truncated.TaskId)).Outcome!.Detail!.Length
            .ShouldBe(OutcomeMapper.MaxDetailLength);
    }

    [Fact]
    public async Task UnknownState_RenderNodeError_UnknownState()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, 7, 20);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.State.ShouldBe(RenderStates.Failed);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        row.Outcome.Detail.ShouldBe(OutcomeMapper.UnknownState);
    }

    [Fact]
    public async Task RawMptError_NotInOutcome()
    {
        const string rawError = "ValueError: Invalid font /opt/nts/mpt/resource/fonts/missing.ttf";
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = await SeedSubmittedAsync(harness);
        harness.Mpt.Script(MptTaskId, -1, 20, failedStage: "audio", error: rawError);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.Outcome!.Code.ShouldBe(RenderOutcomeCodes.TtsFailed);
        row.Outcome.Message.ShouldBe(OutcomeMessages.For(RenderOutcomeCodes.TtsFailed));
        row.Outcome.Message.ShouldNotContain("ValueError");

        // S4: the raw text is in the journal and nowhere near a status body.
        harness.Logs.Contains(rawError).ShouldBeTrue();
        (await harness.StatusJsonAsync(record.TaskId)).ShouldNotContain(rawError);

        // And MPT's content (script, terms) never reaches either.
        (await harness.StatusJsonAsync(record.TaskId)).ShouldNotContain(FakeMptServer.SecretScript);
        (await harness.StatusJsonAsync(record.TaskId)).ShouldNotContain(FakeMptServer.SecretTerm);
        harness.Logs.Contains(FakeMptServer.SecretScript).ShouldBeFalse();
    }

    private static async Task<RenderTaskRecord> SeedSubmittedAsync(AdapterHarness harness)
    {
        var record = AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId);
        await harness.SeedAsync(record);
        return record;
    }
}
