using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Tests.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.RenderNode.Tests;

/// <summary>
/// The D14/D18 fixture replay: every artefact of the frozen contract is read with
/// <see cref="RenderNodeJson.Strict"/> and written back value-equal, and the typed fixtures keep the semantics
/// the contract gives them. The fixtures are read-only inputs (05.4.1-T2); nothing here writes them.
/// </summary>
public sealed class FixtureReplayTests
{
    private const string LostStatus = "render-status.v1.lost.json";
    private const string FailedMaterialsStatus = "render-status.v1.failed-materials.json";
    private const string CancelledStatus = "render-status.v1.cancelled.json";

    public static TheoryData<string> Submits => new()
    {
        SubmitBodies.LandscapeVi,
        SubmitBodies.PortraitEn,
        SubmitBodies.NoMusic,
    };

    public static TheoryData<string, string, string, int, string?> Statuses => new()
    {
        { "render-status.v1.queued.json", RenderStates.Queued, RenderStages.Queued, 0, null },
        { "render-status.v1.running-voice.json", RenderStates.Running, RenderStages.Voice, 20, null },
        { "render-status.v1.running-images.json", RenderStates.Running, RenderStages.Images, 40, null },
        { "render-status.v1.checking.json", RenderStates.Running, RenderStages.Checking, 100, null },
        { "render-status.v1.succeeded.json", RenderStates.Succeeded, RenderStages.Done, 100, null },
        { "render-status.v1.failed-tts.json", RenderStates.Failed, RenderStages.Voice, 20, RenderOutcomeCodes.TtsFailed },
        { FailedMaterialsStatus, RenderStates.Failed, RenderStages.Images, 40, RenderOutcomeCodes.ImageGenerationFailed },
        { "render-status.v1.failed-assembly.json", RenderStates.Failed, RenderStages.Assembling, 75, RenderOutcomeCodes.RenderAssemblyFailed },
        { LostStatus, RenderStates.Failed, RenderStages.Images, 40, RenderOutcomeCodes.RenderLost },
        { CancelledStatus, RenderStates.Cancelled, RenderStages.Done, 100, null },
    };

    public static TheoryData<string, string> Healths => new()
    {
        { "render-node-health.v1.ok.json", RenderNodeHealthStatuses.Ok },
        { "render-node-health.v1.proxy-down.json", NodeHealthReasons.TtsProxyNotOk },
        { "render-node-health.v1.disk-low.json", NodeHealthReasons.DiskLow },
    };

    public static TheoryData<string> Checks => new()
    {
        "render-check.v1.ok.json",
        "render-check.v1.loud.json",
        "render-check.v1.silent.json",
        "render-check.v1.no-subtitles.json",
        "render-check.v1.zero-cues.json",
        "render-check.v1.truncated.json",
    };

    [Theory]
    [MemberData(nameof(Submits))]
    public void Submit_ReplaysFixture(string fileName)
    {
        var text = FixtureFiles.Read(fileName);

        var submit = JsonSerializer.Deserialize<AdapterSubmitRequestV1>(text, RenderNodeJson.Strict);

        submit.ShouldNotBeNull();
        submit.Schema.ShouldBe(RenderNodeSchemas.RenderSubmit);
        submit.ImagePlan.PromptSha256.Count.ShouldBeGreaterThan(0);
        RoundTrips(text, JsonSerializer.Serialize(submit, RenderNodeJson.Strict), fileName);
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Status_ReplaysFixture(string fileName, string state, string stage, int percent, string? outcomeCode)
    {
        var text = FixtureFiles.Read(fileName);

        var status = JsonSerializer.Deserialize<RenderStatusV1>(text, RenderNodeJson.Strict);

        status.ShouldNotBeNull();
        status.Schema.ShouldBe(RenderNodeSchemas.RenderStatus);
        status.State.ShouldBe(state);
        status.Stage.ShouldBe(stage);
        status.Percent.ShouldBe(percent);
        // §4: the outcome block is present exactly on `failed`, cancel_requested exactly on `cancelled`, and the
        // detail of a failed task is the closed failed_stage/pipeline set — never free text.
        status.Outcome?.Code.ShouldBe(outcomeCode);
        status.CancelRequested.ShouldBe(state == RenderStates.Cancelled);
        status.Mpt.ShouldNotBeNull();

        if (outcomeCode is not null)
        {
            OutcomeMessages.For(outcomeCode).ShouldNotBeEmpty();
        }

        // §10 T63: a lost task reports the last observed values with no MPT state.
        if (string.Equals(outcomeCode, RenderOutcomeCodes.RenderLost, StringComparison.Ordinal))
        {
            status.Mpt!.State.ShouldBeNull("a lost task carries no MPT state (§10 T63)");
        }

        RoundTrips(text, JsonSerializer.Serialize(status, RenderNodeJson.Strict), fileName);
    }

    [Theory]
    [MemberData(nameof(Healths))]
    public void Health_ReplaysFixture(string fileName, string reason)
    {
        var text = FixtureFiles.Read(fileName);

        var health = JsonSerializer.Deserialize<RenderNodeHealthV1>(text, RenderNodeJson.Strict);

        health.ShouldNotBeNull();
        health.Schema.ShouldBe(RenderNodeSchemas.RenderNodeHealth);
        if (string.Equals(reason, RenderNodeHealthStatuses.Ok, StringComparison.Ordinal))
        {
            health.Status.ShouldBe(RenderNodeHealthStatuses.Ok);
            health.Reasons.ShouldBeEmpty();
        }
        else
        {
            health.Status.ShouldBe(RenderNodeHealthStatuses.Error);
            health.Reasons.ShouldContain(reason);
        }

        health.Capacity.Accepting.ShouldBe(string.Equals(health.Status, RenderNodeHealthStatuses.Ok, StringComparison.Ordinal));
        // The unchanged proxy-health.v1 body of 04 D8 is read through the view the adapter's health source uses.
        var proxy = ProxyHealthView.From(health.TtsProxy);
        proxy.Ok.ShouldBe(!string.Equals(reason, NodeHealthReasons.TtsProxyNotOk, StringComparison.Ordinal));
        RoundTrips(text, JsonSerializer.Serialize(health, RenderNodeJson.Strict), fileName);
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public void Check_ReplaysFixture(string fileName)
    {
        var text = FixtureFiles.Read(fileName);

        var check = JsonSerializer.Deserialize<RenderCheckV1>(text, RenderNodeJson.Strict);

        check.ShouldNotBeNull();
        check.Schema.ShouldBe(RenderNodeSchemas.RenderCheck);
        check.Warnings.ShouldNotBeNull();
        check.Errors.ShouldNotBeNull();
        RoundTrips(text, JsonSerializer.Serialize(check, RenderNodeJson.Strict), fileName);
    }

    [Theory]
    [InlineData(LostStatus)]
    [InlineData(FailedMaterialsStatus)]
    [InlineData(CancelledStatus)]
    public void EmbeddedImageReport_ReplaysFixture(string fileName)
    {
        var status = JsonSerializer.Deserialize<RenderStatusV1>(FixtureFiles.Read(fileName), RenderNodeJson.Strict);
        status.ShouldNotBeNull();
        var images = status.Images ?? throw new InvalidOperationException($"{fileName} has no images block.");

        var report = images.Deserialize<ImageReportV1>(RenderNodeJson.Strict);

        report.ShouldNotBeNull();
        report.Schema.ShouldBe(RenderNodeSchemas.Images);
        // 03 D6: the relay cannot know the audio length, so `needed` is filled by the backend.
        report.Needed.ShouldNotBeNull(fileName);
        RoundTrips(images.GetRawText(), JsonSerializer.Serialize(report, RenderNodeJson.Strict), fileName);
    }

    [Fact]
    public void ImageReport_ModeratedEntry_RoundTrips()
    {
        // No D14 fixture carries a moderated slot yet (the mock's MOCK_IMAGE_REPORT=moderated_all fixture is
        // 03.4.3-T2's); the code value is opaque to this type, so the shape is proved by substituting it.
        var status = JsonSerializer.Deserialize<RenderStatusV1>(
            FixtureFiles.Read(FailedMaterialsStatus),
            RenderNodeJson.Strict);
        status.ShouldNotBeNull();
        var images = JsonNode.Parse(
            (status.Images ?? throw new InvalidOperationException("the fixture has no images block.")).GetRawText());
        images.ShouldNotBeNull();
        var failed = images["failed"] ?? throw new InvalidOperationException("the fixture has no failed slots.");
        failed.AsArray()[0]!.AsObject()["error_code"] = "moderation_blocked";

        var report = JsonSerializer.Deserialize<ImageReportV1>(images.ToJsonString(), RenderNodeJson.Strict);

        report.ShouldNotBeNull();
        report.Failed[0].ErrorCode.ShouldBe("moderation_blocked");
        RoundTrips(images.ToJsonString(), JsonSerializer.Serialize(report, RenderNodeJson.Strict), "moderated");
    }

    [Fact]
    public void EveryFixture_RoundTripsThroughStrict()
    {
        var fileNames = FixtureFiles.All();

        fileNames.ShouldContain(SubmitBodies.LandscapeVi);
        fileNames.Count.ShouldBeGreaterThanOrEqualTo(24);

        foreach (var fileName in fileNames)
        {
            var text = FixtureFiles.Read(fileName);

            // Strict is the reader that rejects a duplicate property (E18/T1) and the mode every fixture test
            // uses, so parsing every file with it proves the whole fixture set stays readable — including the
            // render-quality fixtures, whose DTO is the Worker's (05.5.1-T2).
            var node = JsonSerializer.Deserialize<JsonNode>(text, RenderNodeJson.Strict);

            node.ShouldNotBeNull(fileName);
            RoundTrips(text, node.ToJsonString(), fileName);
        }
    }

    private static void RoundTrips(string expected, string actual, string context)
    {
        var difference = JsonComparison.Difference(expected, actual);
        difference.ShouldBeNull($"{context}: {difference}");
    }
}
