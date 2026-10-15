using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Jobs;
using NightTales.Domain.Stories;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// Follow-up cases of the bible-proposal job (02.6.3-T2) that the feature's suites leave open: the autopilot audit's
/// data keys, a max-output-tokens stop that must fail on its first call, the kill switch read when the job runs
/// (not when the delivery queued it), and a series archived between the queueing and the run.
/// </summary>
/// <remarks>
/// Each case drives the real Worker (outbox dispatcher and inline job scheduler) and the production handler; the
/// only fakes are the text provider and the credit ledger. Each assertion names the production branch it covers.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ProposeBibleUpdateFollowUpTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// C3: the autopilot audit row stores its data under camelCase keys (<c>seriesId</c>, <c>fromVersion</c>, ...),
    /// the same keys the reader and the audit consumers use; no snake_case form is written.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_AutopilotAudit_DataKeysAreCamelCase()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.Autopilot);

        var text = new FakeTextClient().EnqueueOk(
            TextStep.Bible,
            Draft(
                Character("Bà Hoa", "Người gác bến giữ ngọn đèn dầu."),
                Rule("Đèn dầu phải tắt trước nửa đêm."),
                Summary("Tóm tắt tập một.", BibleProposalJobHarness.StoryTitle)),
            Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.AutoAcceptAuditCount(Database, world.UserId).ShouldBe(1);
        AuditDataKeys(world.UserId).ShouldBe(
            "applied,fromVersion,proposedFromEpisodeId,seriesId,skipped,toVersion",
            "the audit data is written under camelCase keys only");
    }

    /// <summary>
    /// P2: a stop at <c>max_output_tokens</c> repeats the same truncation on a retry, so the run fails on the first
    /// call with the max-output detail and exactly one paid call is made (no retry).
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_MaxOutputTokensStop_FailsOnFirstCallWithoutRetry()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var truncated = TextCallResult<BibleProposalV1>.InvalidOutput(
            BibleProposalJobHarness.Model,
            Usage,
            "req-fake",
            [TextCallInvalidOutput<BibleProposalV1>.MaxOutputTokensExceeded]);
        var text = new FakeTextClient().Enqueue(TextStep.Bible, truncated);
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        text.Requests.Count.ShouldBe(1, "a max-output stop is never retried: the retry would truncate the same way");
        RunOutcome(world.SeriesId).ShouldBe(
            $"failed|{ErrorCodes.BibleProposalInvalidOutput}|invalid_output_max_tokens");
        ledger.Refunds.ShouldBe([("bible_proposal_invalid_output", BibleProposalJobHarness.Reservation(Database, world.SeriesId))]);
        ledger.Settled.ShouldBeEmpty();
        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe("1|true|-", "a failed run stages nothing");
    }

    /// <summary>
    /// S7: the kill switch is read when the job runs. A run queued while the feature was on, whose switch is turned
    /// off before the job runs, closes terminally with <c>feature_disabled</c>, is refunded, and makes no paid call.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_FeatureDisabledAtJobTime_ClosesRunWithoutPaidCall()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient().EnqueueOk(
            TextStep.Bible,
            Draft(Summary("Tóm tắt tập một.", BibleProposalJobHarness.StoryTitle)),
            Usage);
        var options = new BibleProposalJobOptions { Enabled = true };
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(
            Database,
            text,
            ledger: ledger,
            configure: services =>
            {
                services.RemoveAll<IOptionsMonitor<BibleProposalJobOptions>>();
                services.AddSingleton<IOptionsMonitor<BibleProposalJobOptions>>(new MutableOptionsMonitor(options));
            });

        // The delivery queues the run while the feature is on; the job is enqueued but not yet run.
        await BibleProposalJobHarness.DeliverAsync(worker);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(1);
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "ProposeBibleUpdateJob").ShouldBe(1);

        // The switch is flipped off before the job runs: the job itself must observe it.
        options.Enabled = false;
        await BibleProposalJobHarness.RunJobsUntilQuietAsync(worker);

        text.Requests.ShouldBeEmpty("the switch was off when the job ran, so no paid call is made");
        RunOutcome(world.SeriesId).ShouldBe($"failed|{ErrorCodes.FeatureDisabled}|feature_disabled");
        ledger.Refunds.ShouldBe([("feature_disabled", BibleProposalJobHarness.Reservation(Database, world.SeriesId))]);
        ledger.Settled.ShouldBeEmpty();
        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe("1|true|-", "a closed run stages nothing");
    }

    /// <summary>
    /// The series is archived between the queueing and the run: the job closes the run terminally with
    /// <c>series_archived</c>, refunds it under the generation-failed reason, and makes no paid call.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_SeriesArchivedAfterQueueing_FailsRunAndRefunds()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient().EnqueueOk(
            TextStep.Bible,
            Draft(Summary("Tóm tắt tập một.", BibleProposalJobHarness.StoryTitle)),
            Usage);
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.DeliverAsync(worker);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(1);

        SqlProbe.Execute(
            Database,
            $"UPDATE series SET archived_at = now() WHERE id = '{BibleProposalJobHarness.Pg(world.SeriesId)}'");

        await BibleProposalJobHarness.RunJobsUntilQuietAsync(worker);

        text.Requests.ShouldBeEmpty("an archived series is refused before any paid call");
        RunOutcome(world.SeriesId).ShouldBe($"failed|{ErrorCodes.SeriesArchived}|series_archived");
        ledger.Refunds.ShouldBe([("bible_proposal_generation_failed", BibleProposalJobHarness.Reservation(Database, world.SeriesId))]);
        ledger.Settled.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The run's status, error code and error detail as one string.</summary>
    /// <param name="seriesId">The series whose single run is probed.</param>
    /// <returns><c>status|error_code|error_detail</c>.</returns>
    private string RunOutcome(Guid seriesId) =>
        SqlProbe.ScalarText(
            Database,
            $"""
             SELECT status || '|' || coalesce(error_code, '-') || '|' || coalesce(error_detail, '-')
             FROM generation_runs WHERE series_id = '{BibleProposalJobHarness.Pg(seriesId)}'
             """);

    /// <summary>The sorted keys of the owner's autopilot audit row, comma-joined.</summary>
    /// <param name="userId">The owner.</param>
    /// <returns>The keys.</returns>
    private string AuditDataKeys(Guid userId) =>
        SqlProbe.ScalarText(
            Database,
            $"""
             SELECT coalesce(string_agg(key, ',' ORDER BY key), '')
             FROM audit_logs, jsonb_object_keys(data) AS key
             WHERE action = 'series.bible_proposal_accepted' AND subject_user_id = '{BibleProposalJobHarness.Pg(userId)}'
             """);

    /// <summary>The D6 answer of the scripted call: the ops the model proposed, in its own order.</summary>
    /// <param name="ops">The ops.</param>
    /// <returns>The answer.</returns>
    private static BibleProposalV1 Draft(params BibleProposalOpV1[] ops) => new(ops);

    /// <summary>An <c>add_summary</c> candidate: the summary and the episode's title.</summary>
    /// <param name="summary">The summary text.</param>
    /// <param name="name">The summary's title.</param>
    /// <returns>The candidate.</returns>
    private static BibleProposalOpV1 Summary(string summary, string name) =>
        new("add_summary", summary, name, null, null, "vì sao");

    /// <summary>An <c>add_character</c> candidate.</summary>
    /// <param name="name">The character.</param>
    /// <param name="description">The description.</param>
    /// <returns>The candidate.</returns>
    private static BibleProposalOpV1 Character(string name, string description) =>
        new("add_character", null, name, description, null, "vì sao");

    /// <summary>An <c>add_rule</c> candidate.</summary>
    /// <param name="text">The rule.</param>
    /// <returns>The candidate.</returns>
    private static BibleProposalOpV1 Rule(string text) => new("add_rule", null, null, null, text, "vì sao");

    /// <summary>The usage one scripted call reports, so the metered row prices real tokens.</summary>
    private static TokenUsage Usage { get; } = new(1_200, 0, 0, 300, 0);

    /// <summary>
    /// A hand-driven <see cref="IOptionsMonitor{T}"/> over one mutable options instance, so a test can flip
    /// <c>Bible:Proposal:Enabled</c> in a running host.
    /// </summary>
    private sealed class MutableOptionsMonitor(BibleProposalJobOptions options) : IOptionsMonitor<BibleProposalJobOptions>
    {
        /// <inheritdoc />
        public BibleProposalJobOptions CurrentValue => options;

        /// <inheritdoc />
        public BibleProposalJobOptions Get(string? name) => options;

        /// <inheritdoc />
        public IDisposable? OnChange(Action<BibleProposalJobOptions, string?> listener) => null;
    }
}
