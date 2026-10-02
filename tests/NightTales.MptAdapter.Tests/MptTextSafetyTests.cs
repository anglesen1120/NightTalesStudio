using System.Text.Json;
using NightTales.MptAdapter.Tests.Fakes;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>
/// The two text-boundary fixes of the T3 review: MPT's <c>warnings</c> are a closed code set or dropped
/// (CODE-3 / SEC-11.3.5-T2-03), and MPT's free-form <c>error</c>, <c>failed_stage</c> and parser reason reach
/// the journal as one printable line (SEC-11.3.5-T2-02).
/// </summary>
public sealed class MptTextSafetyTests
{
    private const string MptTaskId = "67b5ecfe-c283-47d1-a265-382224c1c207";

    [Theory]
    [InlineData("a\r\nb", "a??b")]
    [InlineData("tab\there", "tab?here")]
    [InlineData("esc\u001b[31mred", "esc?[31mred")]
    [InlineData("nul\u0000end", "nul?end")]
    [InlineData("plain text", "plain text")]
    [InlineData("a\u2028b\u2029c\u200Ed", "a?b?c?d")]
    [InlineData("tag\U000E0001x", "tag?x")]
    [InlineData("mus\U0001D173ic", "mus?ic")]
    [InlineData("emoji \U0001F600 stays", "emoji \U0001F600 stays")]
    [InlineData("", "")]
    public void Sanitize_ReplacesEveryControlCharacter(string input, string expected) =>
        AdapterText.Sanitize(input).ShouldBe(expected);

    [Fact]
    public void Sanitize_BoundsTheLength() =>
        AdapterText.Sanitize(new string('x', AdapterText.MaxSanitizedLength + 100)).Length
            .ShouldBe(AdapterText.MaxSanitizedLength);

    [Fact]
    public void MapFailure_AnUnknownStageWithControlCharacters_IsOneCleanDetail()
    {
        var outcome = OutcomeMapper.MapFailure("mystery\r\nstage");

        outcome.Code.ShouldBe(RenderOutcomeCodes.RenderNodeError);
        outcome.Detail.ShouldBe("mystery??stage");
        outcome.Detail!.ShouldNotContain("\n");
        outcome.Detail!.Length.ShouldBeLessThanOrEqualTo(OutcomeMapper.MaxDetailLength);
    }

    [Fact]
    public async Task CrlfInTheMptError_ReachesTheJournalAsOneLine()
    {
        const string rawError = "boom\r\n2026-10-02 00:00:00 ERROR forged line\u001b[31m";
        await using var harness = await AdapterHarness.StartAsync();
        await harness.SeedAsync(NewRecord());
        harness.Mpt.Script(MptTaskId, -1, 20, failedStage: "audio", error: rawError);

        await harness.PollOnceAsync();

        var reported = harness.Logs.Records
            .FirstOrDefault(entry => entry.Contains("MPT reported", StringComparison.Ordinal));
        reported.ShouldNotBeNull("the raw error text still reaches the journal (S4)");
        reported.ShouldContain("boom");
        reported.ShouldNotContain("\n");
        reported.ShouldNotContain("\r");
        reported.ShouldNotContain("\u001b");

        var failed = harness.Logs.Records
            .FirstOrDefault(entry => entry.Contains("failed:", StringComparison.Ordinal));
        failed.ShouldNotBeNull();
        failed!.ShouldNotContain("\n");
    }

    [Fact]
    public async Task CrlfInTheFailedStage_IsNeutralisedInTheDetailAndTheJournal()
    {
        await using var harness = await AdapterHarness.StartAsync();
        var record = NewRecord();
        await harness.SeedAsync(record);
        harness.Mpt.Script(MptTaskId, -1, 20, failedStage: "mystery\r\nstage");

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.Outcome!.Detail.ShouldBe("mystery??stage");
        row.Outcome.Detail!.ShouldNotContain("\n");

        var failed = harness.Logs.Records
            .FirstOrDefault(entry => entry.Contains("failed:", StringComparison.Ordinal));
        failed.ShouldNotBeNull();
        failed!.ShouldNotContain("\n");
    }

    [Fact]
    public async Task MptWarnings_NeverReachTheStoredSnapshotOrTheStatus()
    {
        var pathWarning = "/opt/nts/mpt/storage/tasks/abc/audio.mp3";
        await using var harness = await AdapterHarness.StartAsync(withHost: true);
        var record = NewRecord();
        await harness.SeedAsync(record);
        harness.Mpt.Script(
            MptTaskId,
            4,
            40,
            warnings: ["loudnorm_dynamic", "free text warning", pathWarning]);

        await harness.PollOnceAsync();

        var row = await harness.RowAsync(record.TaskId);
        row.MptLastSnapshot!.Warnings.ShouldBeEmpty(
            "render-node.v1 §4: warnings are a closed code set or dropped by the adapter");

        var status = await harness.StatusJsonAsync(record.TaskId);
        status.ShouldNotContain(pathWarning);
        status.ShouldNotContain("free text warning");
        using var document = JsonDocument.Parse(status);
        document.RootElement.GetProperty("mpt").GetProperty("warnings").GetArrayLength().ShouldBe(0);
        harness.Logs.Contains(pathWarning).ShouldBeFalse();

        MptWarnings.Project(["anything MPT says"]).ShouldBeEmpty();
    }

    private static RenderTaskRecord NewRecord() =>
        AdapterHarness.NewRecord(
            AdapterHarness.FixtureTaskId,
            AdapterHarness.FixtureRenderJobId,
            backendTaskId: MptTaskId);
}
