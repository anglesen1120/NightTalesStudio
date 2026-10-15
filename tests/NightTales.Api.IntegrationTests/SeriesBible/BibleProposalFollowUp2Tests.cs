using System.Globalization;
using System.Net;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Stories;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Stories;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// The 02.6.3-T2 follow-up review checks on the proposal job (C1, C5, C6) and on its dispatch-side budget guard (P1):
/// a merge keeps each summary on its own episode through accept, a merge keeps the pending proposal's
/// <c>based_on_version</c> and <c>proposed_at</c>, an autopilot approval with no surviving ops writes no audit row,
/// and the dispatch check receives the reserved bound and excludes its own run.
/// </summary>
/// <remarks>
/// Isolation: the class database is shared by the class, so every probe reads the seeded series only.
/// </remarks>
public sealed class BibleProposalFollowUp2Tests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>C1: two approvals merge into one pending proposal, and accept stores one summary per episode.</summary>
    [Fact]
    public async Task StoryApproved_WhileProposalPending_KeepsEachSummaryOnItsOwnEpisode_ThroughAccept()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var first = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        var second = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient()
            .EnqueueOk(
                TextStep.Bible,
                Draft(Summary("Tóm tắt tập một.", "Căn nhà cuối hành lang")),
                Usage)
            .EnqueueOk(
                TextStep.Bible,
                Draft(Summary("Tóm tắt tập hai.", "Căn nhà thứ hai")),
                Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, first, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, second, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe(
            $"1|false|{BibleProposalJobHarness.Pg(second.Id)}");

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        var summaries = row.Content["episode_summaries"]!.AsArray();
        var episodeIds = summaries.Select(s => s!["episode_id"]!.GetValue<string>()).ToArray();

        episodeIds.Order(StringComparer.Ordinal).ToArray().ShouldBe(
            new[] { BibleProposalJobHarness.Pg(first.Id), BibleProposalJobHarness.Pg(second.Id) }
                .Order(StringComparer.Ordinal)
                .ToArray(),
            "each merged summary keeps its own episode: the first run's summary is not rebound to the second episode");
    }

    /// <summary>
    /// C5: a merge after a version bump keeps the pending proposal's <c>based_on_version</c> and <c>proposed_at</c>.
    /// The bump and the sentinel timestamp are written directly, because the test's point is the merge, not the
    /// series' own edit path.
    /// </summary>
    [Fact]
    public async Task StoryApproved_WhileProposalPending_KeepsBasedOnVersionAndProposedAt_AcrossAVersionBump()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var first = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        var second = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient()
            .EnqueueOk(
                TextStep.Bible,
                Draft(Summary("Tóm tắt tập một.", "Căn nhà cuối hành lang"), Character("Bà Hoa", "Người gác bến.")),
                Usage)
            .EnqueueOk(
                TextStep.Bible,
                Draft(Character("Ông Tư", "Thợ sửa đồng hồ ở đầu ngõ.")),
                Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, first, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        // The series moves on between the two approvals: the version is bumped, and the pending proposal's stamp
        // is set to a sentinel the merge must carry over unchanged.
        SqlProbe.Execute(
            Database,
            $$"""
             UPDATE series_bibles
             SET version = version + 1,
                 proposed_content = jsonb_set(proposed_content::jsonb, '{proposed_at}', '"2001-01-01T00:00:00Z"'::jsonb)
             WHERE series_id = '{{BibleProposalJobHarness.Pg(world.SeriesId)}}'
             """);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, second, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe(
            $"2|false|{BibleProposalJobHarness.Pg(second.Id)}",
            "the series is at version 2 and the pending proposal still names the newest episode");
        ProposedField(world.SeriesId, "based_on_version").ShouldBe(
            "1",
            "the merge keeps the version the ops were computed against, not the bumped version");
        DateTimeOffset.Parse(ProposedField(world.SeriesId, "proposed_at"), CultureInfo.InvariantCulture)
            .ShouldBe(
                DateTimeOffset.Parse("2001-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                "the merge keeps the pending proposal's proposed_at, it is not re-stamped with the run's clock");
        text.Requests.Count.ShouldBe(2);
    }

    /// <summary>
    /// C6: an autopilot approval whose draft carries no ops applies nothing, writes no bible change and writes no
    /// system-actor audit row. The run still settles as a success (one paid call).
    /// </summary>
    [Fact]
    public async Task StoryApproved_AutopilotWithNoOps_AppliesNothingAndWritesNoAuditRow()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft(), Usage);
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.Autopilot);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.RunRows(Database, world.SeriesId).ShouldContain("|bible_proposal|system|succeeded|");
        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe(
            "1|true|-",
            "no ops survive, so no proposal is staged, applied or versioned");
        BibleProposalJobHarness.AutoAcceptAuditCount(Database, world.UserId).ShouldBe(0);
        text.Requests.Count.ShouldBe(1);
    }

    /// <summary>
    /// P1: the dispatch-side budget check receives the reserved bound (the admission reservation, not one call's
    /// price) and excludes its own run, while admission checks with no excluded run.
    /// </summary>
    [Fact]
    public async Task StoryApproved_DispatchGuard_ChecksTheReservedBound_ExcludingItsOwnRun()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient().EnqueueOk(TextStep.Bible, Draft(Summary("Tóm tắt tập một.", "Căn nhà")), Usage);
        var guard = new FakeBudgetGuard();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, guard: guard);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        var runId = BibleProposalJobHarness.RunId(Database, world.SeriesId);
        var stored = decimal.Parse(
            SqlProbe.ScalarText(
                Database,
                $"SELECT worst_case_usd::text FROM generation_runs WHERE series_id = '{BibleProposalJobHarness.Pg(world.SeriesId)}'"),
            CultureInfo.InvariantCulture);

        stored.ShouldBeGreaterThan(0m);
        guard.Checked.ShouldBe(
            new[] { stored, stored },
            "admission and dispatch both check the reserved bound stored on the run");
        guard.ExcludedRunIds.ShouldBe(
            new Guid?[] { null, runId },
            "admission excludes no run; dispatch excludes the run's own reservation");
    }

    /// <summary>Reads one field of the pending proposal's stored JSON, as text.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="field">The snake_case key inside <c>proposed_content</c>.</param>
    /// <returns>The field's text.</returns>
    private string ProposedField(Guid seriesId, string field) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT (proposed_content::jsonb ->> '{field}') FROM series_bibles WHERE series_id = '{BibleProposalJobHarness.Pg(seriesId)}'");

    private static BibleProposalV1 Draft(params BibleProposalOpV1[] ops) => new(ops);

    /// <summary>An <c>add_summary</c> candidate: the summary and the episode's title.</summary>
    private static BibleProposalOpV1 Summary(string summary, string name) =>
        new("add_summary", summary, name, null, null, "vì sao");

    /// <summary>An <c>add_character</c> candidate.</summary>
    private static BibleProposalOpV1 Character(string name, string description) =>
        new("add_character", null, name, description, null, "vì sao");

    /// <summary>The usage one scripted call reports, so the metered row prices real tokens.</summary>
    private static TokenUsage Usage { get; } = new(1_200, 0, 0, 300, 0);
}
