using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Abstractions;
using NightTales.Application.Features.SeriesBible.Jobs;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Events;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Telemetry;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.SeriesBible;

/// <summary>
/// Independent QA of the 02.6.3-T2 surface the feature's own suites leave open: the accept and dismiss routes
/// against an unknown series, a series without a bible row, a stored proposal whose source episode column was
/// lost, an empty proposal, a content array already at its §3.2 limit, control and bidi characters inside a stored
/// proposal, a stale <c>If-Match</c> that must be ignored, and anonymous callers — plus the job's own edges: an
/// unknown series, a redelivery after the run finished, an answer with no ops, a summary-only answer, an episode
/// deleted or reopened between the queueing and the run, a bible row removed in the same window, and the kill
/// switch being flipped without a restart.
/// </summary>
/// <remarks>
/// The tests drive the real host (HTTP) and the Worker's own dispatcher and inline scheduler where the job is the
/// subject, exactly as the feature's suites do, so what is asserted is shipped behaviour and not a harness copy.
/// Where a case only records shipped behaviour it says so in the assertion message.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SeriesBibleProposalQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string AcceptAction = "series.bible_proposal_accepted";
    private const string DismissAction = "series.bible_proposal_dismissed";

    /// <summary>The plan's <c>Bible:Limits:MaxCharacters</c> default, the array limit this suite fills.</summary>
    private const int CharacterLimit = 30;

    // ---------------------------------------------------------------- accept / dismiss negatives

    /// <summary>An unknown series id answers 404 on both proposal routes, never 403 and never 500.</summary>
    [Fact]
    public async Task PostAcceptAndDismiss_UnknownSeries_Return404()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        var unknown = Guid.CreateVersion7();

        using var accept = await BibleProposalCommandHarness.AcceptAsync(client, unknown);
        await SeriesBibleHarness.AssertProblemAsync(accept, 404, "not_found");

        using var dismiss = await BibleProposalCommandHarness.DismissAsync(client, unknown);
        await SeriesBibleHarness.AssertProblemAsync(dismiss, 404, "not_found");
    }

    /// <summary>
    /// Data drift on the caller's own series — a series whose bible row is gone — is the PUT's 500
    /// <c>series_bible_missing</c> on both routes, and neither route creates the row.
    /// </summary>
    [Fact]
    public async Task PostAcceptAndDismiss_OwnSeriesWithoutABibleRow_Return500SeriesBibleMissing()
    {
        var world = await SeriesBibleHarness.SeedSeriesWithoutBibleAsync(Database);
        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using var accept = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        await SeriesBibleHarness.AssertProblemAsync(accept, 500, "series_bible_missing");

        using var dismiss = await BibleProposalCommandHarness.DismissAsync(client, world.SeriesId);
        await SeriesBibleHarness.AssertProblemAsync(dismiss, 500, "series_bible_missing");

        SeriesBibleHarness.RowCount(Database, world.SeriesId).ShouldBe(0, "a write never creates the missing row");
    }

    /// <summary>
    /// A stored proposal whose <c>proposed_from_episode_id</c> was lost (the column is nullable, and the FK nulls
    /// it when the episode goes) is not applicable: the accept answers 409 and leaves the row alone rather than
    /// applying ops it cannot bind a summary to.
    /// </summary>
    [Fact]
    public async Task PostAccept_ProposalWithoutItsSourceEpisodeColumn_Returns409()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [BibleProposalCommandHarness.CharacterOp("Bà Tư")]);

        SqlProbe.Execute(
            Database,
            $"UPDATE series_bibles SET proposed_from_episode_id = NULL WHERE series_id = '{Pg(world.SeriesId)}'");

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);

        await SeriesBibleHarness.AssertProblemAsync(response, 409, "bible_proposal_not_pending");
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1, "a refused accept writes nothing");

        // The stored proposal survives: the owner can still dismiss it, which is the only exit for a row that
        // cannot be applied.
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldNotBeEmpty();
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, AcceptAction).ShouldBe(0);

        using var dismiss = await BibleProposalCommandHarness.DismissAsync(client, world.SeriesId);
        dismiss.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            await dismiss.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldBeEmpty();
    }

    /// <summary>
    /// An empty op list is a proposal with nothing to add: the accept succeeds, bumps the version exactly once
    /// (the Domain's own rule: an accept always produces a version) and leaves the content byte-identical.
    /// </summary>
    [Fact]
    public async Task PostAccept_EmptyProposal_BumpsTheVersionOnceAndTouchesNoContent()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(Database, world, approved, ops: []);

        var before = SeriesBibleHarness.Row(Database, world.SeriesId).Content!.ToJsonString();

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);
        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.ResponseEtag(response).ShouldBe(SeriesBibleHarness.Etag(2));

        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(2, "an accept is a content version even when the ops added nothing");
        row.Content!.ToJsonString().ShouldBe(before, "nothing was applied, so nothing moved");
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldBeEmpty();

        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, AcceptAction, "applied").ShouldBe("0");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, AcceptAction, "skipped").ShouldBe("0");

        using var second = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        await SeriesBibleHarness.AssertProblemAsync(second, 409, "bible_proposal_not_pending");
    }

    // ---------------------------------------------------------------- the accept's own op limits

    /// <summary>
    /// A character op whose array is already at <c>Bible:Limits:MaxCharacters</c> (30) is skipped, not stored and
    /// not refused; the ops of the same proposal that do fit are applied, and the two counts reconcile with the
    /// ops the proposal carried.
    /// </summary>
    [Fact]
    public async Task PostAccept_ContentAtTheCharacterLimit_SkipsOnlyTheOverLimitOp()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        var characters = new JsonArray();
        for (var index = 1; index <= CharacterLimit; index++)
        {
            characters.Add(SeriesBibleHarness.Character(index));
        }

        using (var full = await SeriesBibleHarness.PutAsync(
                   client,
                   world.SeriesId,
                   SeriesBibleHarness.Etag(1),
                   SeriesBibleHarness.Content(characters: characters)))
        {
            full.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"{CharacterLimit} characters is exactly the limit, so the save must be accepted — {await full.Content.ReadAsStringAsync(SeriesBibleHarness.Ct)}");
        }

        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [
                BibleProposalCommandHarness.CharacterOp("Bà Mới"),
                BibleProposalCommandHarness.RuleOp("Không nhắc tên người thật."),
            ]);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"an accept never fails for a full array that skips the op — {await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct)}");

        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(3);
        row.Content!["recurring_characters"]!.AsArray().Count.ShouldBe(CharacterLimit, "the full array is left as it was");
        row.Content!["rules"]!.AsArray().Count.ShouldBe(1);
        row.Content!["rules"]![0]!["text"]!.GetValue<string>().ShouldBe("Không nhắc tên người thật.");

        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, AcceptAction, "applied").ShouldBe("1");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, AcceptAction, "skipped").ShouldBe("1");
    }

    /// <summary>
    /// A stored proposal may carry characters the manual PUT refuses (<c>BibleContentValidator</c> rejects
    /// control, format, bidi and tag characters). The accept drops those ops — counted as skipped — and stores
    /// only text the very next save accepts, which is what keeps the client's own copy savable.
    /// </summary>
    [Fact]
    public async Task PostAccept_ControlAndBidiCharacters_AreSkippedAndTheStoredDocumentStaysSavable()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        // The create-path row has an empty narrator persona by design (02.6.3-T1 open item), so a valid document is
        // saved first: the last assertion of this test is about the accept's own output, not about that row.
        using (var initial = await SeriesBibleHarness.PutAsync(
                   client,
                   world.SeriesId,
                   SeriesBibleHarness.Etag(1),
                   SeriesBibleHarness.Content()))
        {
            initial.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                await initial.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        }

        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [
                BibleProposalCommandHarness.CharacterOp("Bà Kiểm", "Mô tả\u0001có ký tự điều khiển."),
                BibleProposalCommandHarness.RuleOp("Luật\u202Ecó ký tự đảo chiều."),
                BibleProposalCommandHarness.RuleOp("Không nhắc tên người thật."),
            ]);

        using var response = await BibleProposalCommandHarness.AcceptAsync(client, world.SeriesId);
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));

        var row = SeriesBibleHarness.Row(Database, world.SeriesId);
        row.Version.ShouldBe(3, "the first save is version 2 and the accepted proposal is one more");
        row.Content!["recurring_characters"]!.AsArray().ShouldBeEmpty("the op holding the control character is dropped");
        row.Content!["rules"]!.AsArray().Count.ShouldBe(1);
        row.Content!["rules"]![0]!["text"]!.GetValue<string>().ShouldBe("Không nhắc tên người thật.");
        var stored = row.Content!.ToJsonString();
        stored.ShouldNotContain("\\u0001", Case.Insensitive);
        stored.ShouldNotContain("\\u202e", Case.Insensitive);

        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, AcceptAction, "applied").ShouldBe("1");
        BibleProposalCommandHarness.AuditDataField(Database, world.UserId, AcceptAction, "skipped").ShouldBe("2");

        // The document the accept stored is accepted by the very next save.
        using var read = await SeriesBibleHarness.GetAsync(client, world.SeriesId);
        var content = (await SeriesBibleHarness.JsonAsync(read)).GetProperty("content").Deserialize<JsonObject>()!;
        using var save = await SeriesBibleHarness.PutAsync(client, world.SeriesId, SeriesBibleHarness.Etag(3), content);
        save.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await save.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
    }

    // ---------------------------------------------------------------- the routes take no precondition

    /// <summary>
    /// Neither proposal route takes an <c>If-Match</c> (plan §4 U3/U4): a stale or bogus value is ignored, never
    /// the PUT's 412/428, because the ops are append-only and a dismissal cannot damage anything.
    /// </summary>
    [Fact]
    public async Task PostAcceptAndDismiss_IgnoreAStaleIfMatchHeader()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [BibleProposalCommandHarness.CharacterOp("Bà Tư")]);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var client = factory.ClientFor(world.UserId);

        using (var accepted = await PostWithIfMatchAsync(
                   client,
                   BibleProposalCommandHarness.AcceptUrl(world.SeriesId),
                   SeriesBibleHarness.Etag(999)))
        {
            accepted.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"the accept has no precondition to fail — {await accepted.Content.ReadAsStringAsync(SeriesBibleHarness.Ct)}");
            SeriesBibleHarness.ResponseEtag(accepted).ShouldBe(SeriesBibleHarness.Etag(2));
        }

        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [BibleProposalCommandHarness.CharacterOp("Bà Tư Hai")]);

        using var dismissed = await PostWithIfMatchAsync(
            client,
            BibleProposalCommandHarness.DismissUrl(world.SeriesId),
            SeriesBibleHarness.Etag(1));

        dismissed.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            await dismissed.Content.ReadAsStringAsync(SeriesBibleHarness.Ct));
        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(2, "a dismissal writes no content version");
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldBeEmpty();
    }

    /// <summary>
    /// Both proposal routes are user routes on the caller's own series: an anonymous caller answers 401 and the
    /// stored proposal is still there for its owner (the group's <c>RequireAuthorization</c> before any handler).
    /// </summary>
    [Fact]
    public async Task ProposalRoutes_Anonymous_Return401WithoutActing()
    {
        var world = await SeriesBibleHarness.SeedAsync(Database);
        var approved = await SeriesBibleHarness.SeedApprovedEpisodeAsync(Database, world);
        await BibleProposalCommandHarness.SeedProposalAsync(
            Database,
            world,
            approved,
            [BibleProposalCommandHarness.CharacterOp("Bà Tư")]);

        using var factory = SeriesBibleHarness.Factory(Database);
        using var anonymous = factory.AnonymousClient();

        using (var accept = await BibleProposalCommandHarness.AcceptAsync(anonymous, world.SeriesId))
        {
            accept.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var dismiss = await BibleProposalCommandHarness.DismissAsync(anonymous, world.SeriesId))
        {
            dismiss.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        SeriesBibleHarness.Row(Database, world.SeriesId).Version.ShouldBe(1);
        BibleProposalCommandHarness.ProposalJson(Database, world.SeriesId).ShouldNotBeEmpty("an anonymous call changes nothing");
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, AcceptAction).ShouldBe(0);
        BibleProposalCommandHarness.AuditCount(Database, world.UserId, DismissAction).ShouldBe(0);
    }

    // ---------------------------------------------------------------- the outbox handler's edges

    /// <summary>
    /// A delivery naming a series the tenant-filtered scope cannot see (the row is gone, or it was never this
    /// user's) queues nothing and stores nothing — the handler's first guard.
    /// </summary>
    [Fact]
    public async Task StoryApproved_UnknownSeries_QueuesNothing()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var approval = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        var unknownSeries = Guid.CreateVersion7();
        // The class shares one database and other tests leave a failed or deferred delivery pending on purpose;
        // the count below must see only this test's message.
        SqlProbe.Execute(Database, "DELETE FROM outbox_messages WHERE processed_at IS NULL");
        await InsertApprovalAsync(world.UserId, unknownSeries, approval.Id, approval.VersionId);

        var text = new FakeTextClient();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);
        (await worker.RunOutboxOnceAsync(SeriesBibleHarness.Ct)).ShouldBe(1);

        BibleProposalJobHarness.RunCount(Database, unknownSeries).ShouldBe(0);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(0, "the real series is untouched");
        worker.Jobs.Enqueued.ShouldBeEmpty();
        worker.Jobs.Failures.ShouldBeEmpty();
        ProcessedApprovals(world.UserId).ShouldBe(1, "the refusal is an outcome, so the message is not redelivered");
    }

    /// <summary>
    /// A redelivery of one approval after its run already finished is a replay: the run is found terminal, no
    /// second paid call is bought and the staged proposal is left exactly as the first run wrote it (the handler's
    /// re-enqueue branch is only for a run still <c>queued</c>).
    /// </summary>
    [Fact]
    public async Task StoryApproved_RedeliveredAfterTheRunFinished_QueuesNoSecondCall()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient().EnqueueOk(
            TextStep.Bible,
            Draft(Summary("Tóm tắt tập một.", BibleProposalJobHarness.StoryTitle)),
            Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        // The same approval arrives again once the run reached its terminal state.
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);
        (await worker.RunOutboxOnceAsync(SeriesBibleHarness.Ct)).ShouldBe(1);

        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(1);
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "ProposeBibleUpdateJob")
            .ShouldBe(1, "a terminal run is never re-enqueued");
        text.Requests.Count.ShouldBe(1, "the replay buys no second call");
        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId)
            .ShouldBe($"1|false|{BibleProposalJobHarness.Pg(episode.Id)}", "the staged proposal is untouched");
    }

    // ---------------------------------------------------------------- the job's own answer edges

    /// <summary>
    /// An answer with no ops at all is a success with exactly the deterministic summary: the run stages one op,
    /// the story's own description, and reports zero model ops.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_ModelReturnsNoOps_StagesExactlyTheDeterministicSummary()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient().EnqueueOk(TextStep.Bible, new BibleProposalV1([]), Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        BibleProposalJobHarness.RunRows(Database, world.SeriesId).ShouldBe(
            "true|bible_proposal|system|succeeded|done|-|-|0|false|true|true");

        var bible = await BibleProposalJobHarness.BibleAsync(Database, world.UserId, world.SeriesId);
        BibleProposalJobHarness.ProposalKinds(bible).ShouldBe(["add_summary"], "the fallback is the only op");
        BibleProposalJobHarness.ProposalSummaries(bible).ShouldBe([BibleProposalJobHarness.StoryDescription]);
        BibleProposalJobHarness.Provenance(Database, world.SeriesId).ShouldBe(
            $"0|1|0|10|0|0|0|0|{BibleProposalJobHarness.Model}",
            "no model op, one kept (the fallback), nothing dropped");
    }

    /// <summary>
    /// A summary-only answer keeps its own summary and gets no second one: the fallback is only for an answer that
    /// carried none.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_SummaryOnlyAnswer_StagesOneOpWithoutASecondSummary()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient().EnqueueOk(
            TextStep.Bible,
            Draft(Summary("Tóm tắt do mô hình viết.", BibleProposalJobHarness.StoryTitle)),
            Usage);
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text);

        await BibleProposalJobHarness.DeliverAndRunAsync(worker);

        var bible = await BibleProposalJobHarness.BibleAsync(Database, world.UserId, world.SeriesId);
        BibleProposalJobHarness.ProposalKinds(bible).ShouldBe(["add_summary"]);
        BibleProposalJobHarness.ProposalSummaries(bible).ShouldBe(["Tóm tắt do mô hình viết."]);
        BibleProposalJobHarness.Provenance(Database, world.SeriesId).ShouldBe(
            $"1|1|0|10|0|0|0|0|{BibleProposalJobHarness.Model}");
    }

    /// <summary>
    /// The episode is deleted between the queueing and the run: the job closes the run terminally (<c>failed</c>,
    /// <c>not_found</c>) and refunds the reservation, instead of leaving it <c>queued</c> for 12 hours where it would
    /// block every later approval of the series (C4). No provider call, no proposal and no settlement.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_EpisodeDeletedAfterQueueing_FailsTheRunAndRefunds()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.DeliverAsync(worker);
        SqlProbe.Execute(
            Database,
            $"DELETE FROM episodes WHERE id = '{BibleProposalJobHarness.Pg(episode.Id)}'");

        await BibleProposalJobHarness.RunJobsUntilQuietAsync(worker);

        text.Requests.ShouldBeEmpty("an episode that is gone buys no call");
        RunStatus(world.SeriesId).ShouldBe("failed", "a terminal guard frees the series' single active-run slot");
        SqlProbe.ScalarText(
            Database,
            $"SELECT error_code FROM generation_runs WHERE series_id = '{BibleProposalJobHarness.Pg(world.SeriesId)}'")
            .ShouldBe("not_found");
        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe("1|true|-");
        ledger.Settled.ShouldBeEmpty();
        ledger.Refunds.Count.ShouldBe(1, "the reservation is released exactly once");
        ledger.Refunds[0].Item1.ShouldBe("bible_proposal_generation_failed");
    }

    /// <summary>
    /// The commit of the queued run fails with a non-conflict error after the reservation was made. The reservation
    /// is released as <c>bible_proposal_generation_failed</c> before the failure propagates (C8), so no budget is left
    /// reserved without a run row to settle it.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_CommitFailsAfterReservation_RefundsTheReservation()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        InstallRunInsertFailure();
        try
        {
            // The dispatcher may surface the failure or record it; either way the reservation must be released.
            await Record.ExceptionAsync(() => worker.RunOutboxOnceAsync(SeriesBibleHarness.Ct));
        }
        finally
        {
            RemoveRunInsertFailure();
        }

        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(0, "the failed commit left no run row");
        ledger.Reserved.Count.ShouldBe(1, "the reservation was made before the commit failed");
        ledger.Refunds.ShouldBe([("bible_proposal_generation_failed", ledger.Reserved[0])]);
        ledger.Settled.ShouldBeEmpty();
        text.Requests.ShouldBeEmpty();

        // The class shares one database: the failed delivery stays pending for a later redelivery, which would be
        // counted by the next test's RunOutboxOnceAsync. Remove it.
        SqlProbe.Execute(
            Database,
            $"DELETE FROM outbox_messages WHERE user_id = '{BibleProposalJobHarness.Pg(world.UserId)}'");
    }

    /// <summary>
    /// A second approval arrives while the first run of the series is still queued. The series-level index refuses
    /// it, so its reservation is released as <c>bible_proposal_deferred</c> and the delivery is re-scheduled rather
    /// than failed (G4). Only the first run exists, and nothing is settled or called.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_SecondApprovalWhileFirstQueued_RefundsTheSecondAsDeferred()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var first = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, first, ApprovalSource.User);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.DeliverAsync(worker);

        var second = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, second, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAsync(worker);

        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(1, "only the first approval queued a run");
        ledger.Reserved.Count.ShouldBe(2, "both deliveries reserved budget before the index was consulted");
        ledger.Refunds.ShouldBe([("bible_proposal_deferred", ledger.Reserved[1])]);
        ledger.Settled.ShouldBeEmpty();
        text.Requests.ShouldBeEmpty("nothing runs while the first run is queued");
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "ProposeBibleUpdateJob")
            .ShouldBe(1, "only the first run is enqueued");
        worker.Jobs.Enqueued.Single(enqueue => enqueue.JobType == "BibleProposalHandoffJob")
            .Delay.ShouldBe(TimeSpan.FromMinutes(2), "the deferred delivery waits one run out");
    }

    /// <summary>
    /// A handoff delivery throws after it reserved budget. The job has no retries of its own, so it must not lose the
    /// work: it re-schedules one try later through the bounded path (P6), refunds the reservation as
    /// <c>bible_proposal_generation_failed</c>, logs only the exception type and does not rethrow.
    /// </summary>
    [Fact]
    public async Task BibleProposalHandoffJob_DeliveryThrows_ReschedulesOneTryLaterWithoutRethrowing()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        var sink = new TestLogSink();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(
            Database,
            text,
            ledger: ledger,
            sink: sink);

        using var scope = worker.CreateScope();
        await RunHandoffWithInjectedCommitFailureAsync(scope, world.SeriesId, episode.Id, episode.VersionId, tries: 0);

        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(0, "the failed commit left no run row");
        ledger.Refunds.ShouldBe([("bible_proposal_generation_failed", ledger.Reserved[0])]);
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "BibleProposalHandoffJob").ShouldBe(1);
        worker.Jobs.Enqueued.Single(enqueue => enqueue.JobType == "BibleProposalHandoffJob")
            .Delay.ShouldBe(TimeSpan.FromMinutes(2), "the failed delivery is re-scheduled one try later");
        sink.AllText().ShouldContain("bible_proposal_handoff_job_failed");
        text.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// A handoff that keeps failing is re-scheduled while it has deliveries left and dropped at the limit (G2). The
    /// limit is <c>MaxHandoffTries</c> (10) in the handler, which is private and mirrored here: with 9 spent the
    /// job re-schedules once more, and with 10 spent the drop is logged and nothing is enqueued.
    /// </summary>
    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public async Task BibleProposalHandoffJob_RetriesStopAtTheDeliveryLimit(int tries, bool rescheduled)
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        var sink = new TestLogSink();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(
            Database,
            text,
            ledger: ledger,
            sink: sink);

        using var scope = worker.CreateScope();
        await RunHandoffWithInjectedCommitFailureAsync(scope, world.SeriesId, episode.Id, episode.VersionId, tries);

        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "BibleProposalHandoffJob")
            .ShouldBe(rescheduled ? 1 : 0, $"a delivery that already spent {tries} tries is re-scheduled only below the limit");
        if (rescheduled)
        {
            sink.AllText().ShouldNotContain("bible_proposal_dropped");
        }
        else
        {
            sink.AllText().ShouldContain("bible_proposal_dropped");
        }

        ledger.Refunds.ShouldBe([("bible_proposal_generation_failed", ledger.Reserved[0])]);
        text.Requests.ShouldBeEmpty();
    }

    /// <summary>
    /// The episode is reopened between the queueing and the run: there is no approved version left to build the
    /// prompt from, so the run fails typed (<c>episode_not_approved</c>) and its reservation is released.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_EpisodeReopenedAfterQueueing_FailsTheRunNotApproved()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.DeliverAsync(worker);
        SqlProbe.Execute(
            Database,
            $"UPDATE episodes SET approved_story_version_id = NULL, approved_at = NULL WHERE id = '{BibleProposalJobHarness.Pg(episode.Id)}'");

        await BibleProposalJobHarness.RunJobsUntilQuietAsync(worker);

        BibleProposalJobHarness.RunRows(Database, world.SeriesId).ShouldBe(
            $"true|bible_proposal|system|failed|writing|{ErrorCodes.BibleProposalGenerationFailed}|episode_not_approved|0|false|false|true");
        text.Requests.ShouldBeEmpty();
        ledger.Refunds.ShouldBe(
            [("bible_proposal_generation_failed", BibleProposalJobHarness.Reservation(Database, world.SeriesId))]);
        ledger.Settled.ShouldBeEmpty();
        BibleProposalJobHarness.BibleColumns(Database, world.SeriesId).ShouldBe("1|true|-");
    }

    /// <summary>
    /// The bible row is removed between the queueing and the run: there is nothing to propose onto, so the job
    /// fails the run typed (<c>bible_missing</c>) and releases its reservation — data drift on the caller's own
    /// series is never silently retried.
    /// </summary>
    [Fact]
    public async Task ProposeBibleUpdate_BibleRowDeletedAfterQueueing_FailsTheRunBibleMissing()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var episode = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, episode, ApprovalSource.User);

        var text = new FakeTextClient();
        var ledger = new BibleProposalJobHarness.RecordingLedger();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(Database, text, ledger: ledger);

        await BibleProposalJobHarness.DeliverAsync(worker);
        SqlProbe.Execute(
            Database,
            $"DELETE FROM series_bibles WHERE series_id = '{BibleProposalJobHarness.Pg(world.SeriesId)}'");

        await BibleProposalJobHarness.RunJobsUntilQuietAsync(worker);

        var reservation = BibleProposalJobHarness.Reservation(Database, world.SeriesId);
        SqlProbe.ScalarText(
                Database,
                $"""
                 SELECT status || '|' || coalesce(error_code, '-') || '|' || coalesce(error_detail, '-')
                 FROM generation_runs WHERE series_id = '{BibleProposalJobHarness.Pg(world.SeriesId)}'
                 """)
            .ShouldBe($"failed|{ErrorCodes.BibleProposalGenerationFailed}|bible_missing");
        text.Requests.ShouldBeEmpty();
        ledger.Refunds.ShouldBe([("bible_proposal_generation_failed", reservation)]);
        ledger.Settled.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- the kill switch

    /// <summary>
    /// The kill switch is read per delivery through the production Worker adapter (<c>BibleProposalSwitch</c> over
    /// the monitored <c>Bible:Proposal</c> section): flipping it off and back on in a running host changes what the
    /// next delivery does, with no restart and no run row while it is off.
    /// </summary>
    [Fact]
    public async Task StoryApproved_KillSwitchFlippedWithoutARestart_IsReadPerDelivery()
    {
        var world = await BibleProposalJobHarness.SeedAsync(Database);
        var first = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        var second = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);
        var third = await BibleProposalJobHarness.ApproveEpisodeAsync(Database, world);

        var text = new FakeTextClient()
            .EnqueueOk(
                TextStep.Bible,
                Draft(Summary("Tóm tắt tập một.", BibleProposalJobHarness.StoryTitle)),
                Usage)
            .EnqueueOk(
                TextStep.Bible,
                Draft(Summary("Tóm tắt tập ba.", "Căn nhà thứ ba")),
                Usage);

        var options = new BibleProposalJobOptions();
        await using var worker = await BibleProposalJobHarness.StartWorkerAsync(
            Database,
            text,
            configure: services =>
            {
                services.RemoveAll<IOptionsMonitor<BibleProposalJobOptions>>();
                services.AddSingleton<IOptionsMonitor<BibleProposalJobOptions>>(new MutableOptionsMonitor(options));
            });

        worker.Services.GetRequiredService<IBibleProposalSwitch>().ShouldBeOfType<NightTales.Worker.BibleProposalSwitch>(
            "the test must exercise the production switch adapter, not a stub");

        // On: the first approval queues its run, which the job completes so the series-level index is free again.
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, first, ApprovalSource.User);
        await BibleProposalJobHarness.DeliverAndRunAsync(worker);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(1);

        // Off: the next delivery spends nothing and stores nothing, without the host being restarted.
        options.Enabled = false;
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, second, ApprovalSource.User);
        (await worker.RunOutboxOnceAsync(SeriesBibleHarness.Ct)).ShouldBe(1);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId)
            .ShouldBe(1, "the switch is off, so the delivery queues no row at all");
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "ProposeBibleUpdateJob")
            .ShouldBe(1, "nothing was enqueued while the feature was off");

        // On again: the very next delivery obeys the flipped value.
        options.Enabled = true;
        await BibleProposalJobHarness.InsertStoryApprovedAsync(Database, world, third, ApprovalSource.User);
        (await worker.RunOutboxOnceAsync(SeriesBibleHarness.Ct)).ShouldBe(1);
        BibleProposalJobHarness.RunCount(Database, world.SeriesId).ShouldBe(2);
        worker.Jobs.Enqueued.Count(enqueue => enqueue.JobType == "ProposeBibleUpdateJob").ShouldBe(2);

        await BibleProposalJobHarness.RunJobsUntilQuietAsync(worker);
        var bible = await BibleProposalJobHarness.BibleAsync(Database, world.UserId, world.SeriesId);
        BibleProposalJobHarness.ProposalSummaries(bible)
            .ShouldBe(["Tóm tắt tập một.", "Tóm tắt tập ba."], "only the two approvals delivered while on were bought");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A POST of one proposal route with an explicit <c>If-Match</c>, which the routes must ignore.</summary>
    private static async Task<HttpResponseMessage> PostWithIfMatchAsync(HttpClient client, string url, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        return await client.SendAsync(request, SeriesBibleHarness.Ct);
    }

    /// <summary>Writes one <c>story_approved.v1</c> row with the production serializer for explicit ids.</summary>
    private Task InsertApprovalAsync(Guid userId, Guid seriesId, Guid episodeId, Guid storyVersionId)
    {
        var id = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(
            new StoryApproved(episodeId, seriesId, userId, storyVersionId, ApprovalSource.User, SeriesBibleHarness.Start),
            OutboxJson.Options);

        return Database.ExecuteAsync(
            $"""
             INSERT INTO outbox_messages (id, type, payload, user_id, occurred_at, next_attempt_at, attempts)
             VALUES ('{id}', 'story_approved.v1', '{payload}', '{Pg(userId)}', '{SeriesBibleHarness.Start:O}', '{SeriesBibleHarness.Start:O}', 0)
             """,
            SeriesBibleHarness.Ct);
    }

    /// <summary>How many of the owner's <c>story_approved.v1</c> rows the dispatcher processed.</summary>
    private long ProcessedApprovals(Guid userId) =>
        SqlProbe.Count(
            Database,
            $"""
             SELECT count(*) FROM outbox_messages
             WHERE type = 'story_approved.v1' AND user_id = '{Pg(userId)}' AND processed_at IS NOT NULL
             """);

    /// <summary>The <c>status</c> of the series' single run row.</summary>
    private string RunStatus(Guid seriesId) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT status FROM generation_runs WHERE series_id = '{Pg(seriesId)}'");

    /// <summary>The D6 answer of the scripted call.</summary>
    private static BibleProposalV1 Draft(params BibleProposalOpV1[] ops) => new(ops);

    /// <summary>An <c>add_summary</c> candidate.</summary>
    private static BibleProposalOpV1 Summary(string summary, string name) =>
        new("add_summary", summary, name, null, null, "vì sao");

    /// <summary>The usage one scripted call reports, so the metered row prices real tokens.</summary>
    private static TokenUsage Usage { get; } = new(1_200, 0, 0, 300, 0);

    /// <summary>A Postgres uuid literal for the raw-SQL probes.</summary>
    private static string Pg(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Makes every insert into <c>generation_runs</c> fail with a non-conflict error, so a commit throws after the
    /// reservation was made. The error is not the series-level index, so the handler takes its generic path.
    /// </summary>
    private void InstallRunInsertFailure() =>
        SqlProbe.Execute(
            Database,
            """
            DROP TRIGGER IF EXISTS nt_test_fail_run_insert ON generation_runs;
            DROP FUNCTION IF EXISTS nt_test_fail_run_insert();
            CREATE FUNCTION nt_test_fail_run_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'injected commit failure' USING ERRCODE = 'XX000';
            END;
            $$;
            CREATE TRIGGER nt_test_fail_run_insert BEFORE INSERT ON generation_runs
                FOR EACH ROW EXECUTE FUNCTION nt_test_fail_run_insert();
            """);

    /// <summary>Removes the failure injected by <see cref="InstallRunInsertFailure"/>.</summary>
    private void RemoveRunInsertFailure() =>
        SqlProbe.Execute(
            Database,
            """
            DROP TRIGGER IF EXISTS nt_test_fail_run_insert ON generation_runs;
            DROP FUNCTION IF EXISTS nt_test_fail_run_insert();
            """);

    /// <summary>
    /// Runs one handoff job in its own scope, the way Hangfire resolves it, with the commit made to fail.
    /// </summary>
    private async Task RunHandoffWithInjectedCommitFailureAsync(
        IServiceScope scope,
        Guid seriesId,
        Guid episodeId,
        Guid storyVersionId,
        int tries)
    {
        var job = ActivatorUtilities.CreateInstance<BibleProposalHandoffJob>(scope.ServiceProvider);

        InstallRunInsertFailure();
        try
        {
            await job.RunAsync(seriesId, episodeId, storyVersionId, ApprovalSource.User, tries, SeriesBibleHarness.Ct);
        }
        finally
        {
            RemoveRunInsertFailure();
        }
    }

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
