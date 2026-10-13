using System.Diagnostics;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Prompts;
using NightTales.Domain.Stories.Similarity;
using NightTales.Domain.Usage;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Research;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Research;

/// <summary>
/// <c>GenerateFactSheetPipeline</c> (02.5.3-T2 plan §7, D21) against a real <c>AppDbContext</c> on a real
/// PostgreSQL 17, with hand-written fakes for the extractor, the moderator, the ledger, the usage meter and the
/// two catalogs. Every case drives the pipeline once and asserts the run row, the draft row, the refund/settle
/// calls and the outbox: the D7 failure mapping, the single invalid-output retry, the budget gate, the source
/// re-check, the validator integration through the real <c>SimilarityScorer</c>, the id watermark and the
/// control-character guarantees. No sleeps: the clock is a <see cref="FakeClock"/> and the run deadline is set
/// far in the future per case.
/// </summary>
public sealed class GenerateFactSheetPipelineTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Long enough to clear the score window of either language, so a verbatim copy scores 1.</summary>
    private const string CopiedSentence =
        "Đêm ấy người giữ bến mang chiếc đèn dầu ra trước cổng gỗ rồi đứng chờ những con thuyền trở về.";

    private const string SourceProse =
        CopiedSentence
        + " Mưa phủ kín mặt sông và tiếng gió khiến ông không nghe rõ lời gọi từ xa."
        + " Khi một cậu bé xuất hiện dưới mái hiên ông đưa cho cậu chiếc áo khô rồi hỏi tên gia đình."
        + " Cậu chỉ về phía ngôi nhà nhỏ cuối con đường nơi mẹ vẫn đang thức.";

    /// <summary>A statement written from another source: no shingle of it appears in <see cref="SourceProse"/>.</summary>
    private const string Paraphrase =
        "Sáng hôm sau cô thợ làm bánh mở cửa tiệm sớm hơn mọi ngày để chuẩn bị bữa ăn cho các gia đình mất nhà.";

    /// <summary>The user note's text; a user note is never part of the similarity corpus (F18).</summary>
    private const string NoteProse =
        "Người dân trong xóm kể rằng đêm đó trời mưa rất to và không ai ra ngoài.";

    [Fact]
    public async Task Pipeline_Succeeds_WritesDraftV1_SettlesReservation_RecordsUsage_EmitsFactSheetGenerated()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.Stage.ShouldBe(GenerationStage.Done);
        run.ErrorCode.ShouldBeNull();
        run.CountsTowardLimit.ShouldBeTrue("a succeeded run counts (Succeed forces it)");
        run.CostUsd.ShouldBe(0.25m, "the sum of the reservation's usage records");
        run.ResultFactSheetId.ShouldNotBeNull();
        run.PromptVersion.ShouldBe("pv-v1");

        var sheet = await world.FactSheetAsync();
        sheet.Version.ShouldBe(1);
        sheet.Status.ShouldBe(FactSheetStatus.Draft);
        sheet.GenerationRunId.ShouldBe(world.RunId);
        sheet.CreatedBy.ShouldBe(world.UserId);
        sheet.Facts.ShouldHaveSingleItem().Id.ShouldBe("f1");
        sheet.MaxFactId.ShouldBe(1);
        sheet.InputTruncated.ShouldBeFalse();
        sheet.ContentSha256.Length.ShouldBe(64);

        world.Ledger.Settled.ShouldBe(new[] { world.Reservation });
        world.Ledger.Refunds.ShouldBeEmpty();
        world.Meter.Items.ShouldHaveSingleItem().Operation.ShouldBe(GenerateFactSheetPipeline.Operation);
        (await world.OutboxTypesAsync()).ShouldContain("fact_sheet_generated.v1");
    }

    [Fact]
    public async Task Pipeline_RedeliveredAfterSuccess_IsANoOp_NoSecondSheet_NoSecondCharge_NoSecondEvent()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));
        await world.RunAsync(Ct);
        var generatedEventsAfterFirst = (await world.OutboxTypesAsync()).Count(type => type == "fact_sheet_generated.v1");

        await world.RunAsync(Ct);

        world.Extractor.Requests.Count.ShouldBe(1, "the paid extraction is not repeated");
        (await world.FactSheetCountAsync()).ShouldBe(1);
        world.Ledger.Settled.Count.ShouldBe(1);
        world.Ledger.Refunds.ShouldBeEmpty();
        world.Meter.Items.Count.ShouldBe(1);
        (await world.OutboxTypesAsync()).Count(type => type == "fact_sheet_generated.v1").ShouldBe(generatedEventsAfterFirst);
    }

    [Fact]
    public async Task Pipeline_RedeliveredAfterFailure_StaysFailed_WithoutACallOrASecondRefund()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueFundsExhausted();
        await world.RunAsync(Ct);
        var callsAfterFirst = world.Extractor.Requests.Count;
        var refundsAfterFirst = world.Ledger.Refunds.Count;

        await world.RunAsync(Ct);

        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Failed);
        world.Extractor.Requests.Count.ShouldBe(callsAfterFirst);
        world.Ledger.Refunds.Count.ShouldBe(refundsAfterFirst);
        (await world.FactSheetCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task GenerateFactSheetJob_InvalidOutputAfterRetry_FailsRun_FactSheetInvalidOutput_AndRefunds()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueInvalidOutput();
        world.Extractor.EnqueueInvalidOutput();

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetInvalidOutput);
        run.ErrorDetail.ShouldBe("invalid_output_twice");
        run.CountsTowardLimit.ShouldBeFalse();
        world.Extractor.Calls.ShouldBe(2, "the single in-job D7 retry, then the failure");
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_invalid_output", world.Reservation) });
        world.Ledger.Settled.ShouldBeEmpty();
        (await world.FactSheetCountAsync()).ShouldBe(0, "a failed run writes no draft");
        (await world.OutboxTypesAsync()).ShouldContain("fact_sheet_generation_failed.v1");
    }

    [Fact]
    public async Task Pipeline_InvalidOutputOnceThenSuccess_SucceedsOnAttemptTwo()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueInvalidOutput();
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.Medium)));

        await world.RunAsync(Ct);

        world.Extractor.Calls.ShouldBe(2);
        world.Extractor.Requests[0].Run.Attempt.ShouldBe(1);
        world.Extractor.Requests[1].Run.Attempt.ShouldBe(2, "the retry rewrites the attempt on its own context");
        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        run.Attempt.ShouldBe(1, "the run records one retry");
        (await world.FactSheetCountAsync()).ShouldBe(1);
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_FlaggedSources_FailsModerated_WritesFactSheetStage_Refunds()
    {
        var world = await NewWorldAsync();
        world.Moderator.EnqueueFlagged(ModerationStage.FactSheet, "violence");

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetModerated);
        run.ErrorDetail.ShouldBe("sources_flagged");
        world.Moderator.Checks.ShouldContain(check => check.Stage == ModerationStage.FactSheet);
        world.Events.Stages.ShouldBe(new[] { ModerationStage.FactSheet });
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_moderated", world.Reservation) });
        (await world.FactSheetCountAsync()).ShouldBe(0, "a moderated run writes no draft");
    }

    [Fact]
    public async Task Pipeline_ProviderModerated_FailsModerated_WritesFactSheetStage_Refunds()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueModerated();

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetModerated);
        run.ErrorDetail.ShouldBe("provider_refused");
        world.Events.Stages.ShouldBe([ModerationStage.FactSheet], ignoreOrder: false, customMessage: "a provider refusal writes the same moderation stage");
        world.Extractor.Calls.ShouldBe(1, "a provider refusal is the only call");
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_moderated", world.Reservation) });
    }

    [Fact]
    public async Task Pipeline_SourcesRemovedBeforeTheWrite_FailsSourcesChanged_NoExtraction_Refunds()
    {
        // Every source is gone by the time the plan reads the fetched set: the re-check after the request fails the
        // run with `sources_changed`, before any paid call.
        var world = await NewWorldAsync(removeSourcesBeforePlan: true);

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("sources_changed");
        world.Extractor.Calls.ShouldBe(0);
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_generation_failed", world.Reservation) });
        (await world.FactSheetCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Pipeline_OverBudget_FailsInputOverBudget_NoExtraction_Refunds()
    {
        // A 3,000-character user note alone exceeds the 50-token input ceiling; the note is never truncated, so the
        // plan is OverBudget and the run fails before the paid call.
        var world = await NewWorldAsync(
            noteText: new string('n', 3_000),
            extraction: new FactSheetExtractionOptions { MaxInputTokens = 50 });

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("input_over_budget");
        world.Extractor.Calls.ShouldBe(0, "the budget gate is before any paid call");
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_generation_failed", world.Reservation) });
        (await world.FactSheetCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Pipeline_TruncatedUrlSource_RecordsSafePlanDetail_AndInputTruncatedTrue()
    {
        var world = await NewWorldAsync(
            urlText: new string('a', 4_000),
            sourceTitle: "Bài báo gốc",
            extraction: new FactSheetExtractionOptions { PerSourceCharCap = 1_000 });

        world.Extractor.EnqueueOk(Facts(("s2", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        var models = run.Models.ShouldNotBeNull();
        models.ShouldContain("\"input_truncated\": true");
        models.ShouldContain("\"excluded_sources\": 0");
        models.ShouldContain("\"truncated_aliases\": 1");
        models.ShouldNotContain(new string('a', 200), Case.Sensitive, "the plan detail carries counts, never content");

        var sheet = await world.FactSheetAsync();
        sheet.InputTruncated.ShouldBeTrue("the url source's text was cut, so the CR-4 flag is set");
    }

    [Fact]
    public async Task FactSheetJob_PersistsSourceAliasMap_AndInputTruncatedFlag()
    {
        var world = await NewWorldAsync(
            urlText: new string('a', 4_000),
            sourceTitle: "Bài báo gốc",
            extraction: new FactSheetExtractionOptions { PerSourceCharCap = 1_000 });

        world.Extractor.EnqueueOk(Facts(("s2", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));

        await world.RunAsync(Ct);

        var sheet = await world.FactSheetAsync();
        sheet.SourceAliases.Count.ShouldBe(2, "the note and the url source both took an alias (CR-4/D21)");
        sheet.SourceAliases["s1"].ShouldBe(world.NoteSourceId, "a user note takes its slot first");
        ((Guid?)sheet.SourceAliases["s2"]).ShouldBe(world.UrlSourceId);
        sheet.InputTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Pipeline_SecondRun_WritesVersionTwo_AndIdsContinueFromMaxFactId()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));
        await world.RunAsync(Ct);

        var first = await world.FactSheetAsync();
        first.Version.ShouldBe(1);
        first.Facts[0].Id.ShouldBe("f1");
        first.MaxFactId.ShouldBe(1);

        var second = await world.RunSecondRunAsync();
        second.Extractor.EnqueueOk(Facts(("s1", "Nhân chứng nhớ lại buổi sáng hôm ấy rất rõ.", FactConfidence.Low)));

        await second.RunAsync(Ct);

        var versionTwo = await second.FactSheetAsync(2);
        versionTwo.Version.ShouldBe(2);
        versionTwo.MaxFactId.ShouldBe(2);
        versionTwo.Facts.ShouldHaveSingleItem().Id.ShouldBe("f2", "ids continue from the episode watermark, never reused");
        (await world.FactSheetCountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Pipeline_AllAliasesUnknown_SavesEmptyDraft_AndSucceeds()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueOk(Facts(("s9", "Không nguồn nào khớp.", FactConfidence.High)));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded, "zero surviving facts is a valid empty draft (F13)");
        var sheet = await world.FactSheetAsync();
        sheet.Facts.ShouldBeEmpty();
        sheet.MaxFactId.ShouldBe(0);
        sheet.SourceAliases.ShouldContainKey("s1");
        world.Ledger.Settled.ShouldBe(new[] { world.Reservation });
        (await world.OutboxTypesAsync()).ShouldContain("fact_sheet_generated.v1");
    }

    [Fact]
    public async Task Pipeline_VerbatimCopyDropped_ParaphraseKept_NoteTextNotScored()
    {
        // Three url sources: one statement copies the first verbatim, another paraphrases the others. The user note's
        // text is never part of the similarity corpus (F18), so a statement that repeats the note verbatim is kept.
        var world = await NewWorldAsync(urlSources:
        [
            SourceProse,
            "Cảnh sát mở lại hồ sơ cũ sau khi một nhân chứng bất ngờ liên lạc với họ.",
            "Tang vật được niêm phong trong kho lạnh chờ giám định chính thức.",
        ]);

        world.Extractor.EnqueueOk(new FactsV1(
        [
            new FactDraftV1(SourceProse, ["s2"], FactConfidence.High),
            new FactDraftV1(Paraphrase, ["s3"], FactConfidence.Medium),
            new FactDraftV1(NoteProse, ["s2"], FactConfidence.Low),
        ]));

        await world.RunAsync(Ct);

        var sheet = await world.FactSheetAsync();
        sheet.Facts.Count.ShouldBe(2, "the verbatim copy is dropped as verbatim_copy; the paraphrase and the note copy survive");
        sheet.Facts[0].Statement.ShouldBe(Paraphrase);
        sheet.Facts[0].Id.ShouldBe("f1");
        sheet.Facts[1].Statement.ShouldBe(NoteProse, "the note's text is not a similarity corpus");
        sheet.Facts[1].Id.ShouldBe("f2");
        sheet.Facts[1].Confidence.ShouldBe(FactConfidence.Low, "a low-confidence fact is kept, never dropped (T53)");

        var models = (await world.ReadRunAsync()).Models.ShouldNotBeNull();
        models.ShouldContain("\"model_facts\": 3");
        models.ShouldContain("\"kept_facts\": 2");
        models.ShouldContain("\"verbatim_copy\": 1");
    }

    [Fact]
    public async Task Pipeline_StatementWithControlCharacters_IsDropped_AndNeverReachesTheDatabase()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueOk(new FactsV1(
        [
            new FactDraftV1("Người gác bến\0 giữ đèn.", ["s1"], FactConfidence.High),
            new FactDraftV1("Nhân chứng kể lại buổi sáng hôm ấy.", ["s1"], FactConfidence.Medium),
        ]));

        await world.RunAsync(Ct);

        var sheet = await world.FactSheetAsync();
        sheet.Facts.ShouldHaveSingleItem().Statement.ShouldBe("Nhân chứng kể lại buổi sáng hôm ấy.");
        SqlProbe.ScalarText(Database, $"SELECT facts::text FROM fact_sheets WHERE episode_id = '{world.EpisodeId}'")
            .ShouldNotContain("\\u0000");
    }

    [Fact]
    public async Task Pipeline_ControlCharacterInSourceTitle_IsStrippedBeforeThePrompt_AndTheRunSucceeds()
    {
        // Postgres rejects NUL in text at insert, so the title carries U+001F (a C0 control it does store).
        var world = await NewWorldAsync(urlText: "Bài báo về vụ án năm ấy.", sourceTitle: "Nhân chứng\u001Fbiên bản");
        world.Extractor.EnqueueOk(new FactsV1(
        [
            new FactDraftV1("Nhân chứng kể lại buổi sáng hôm ấy.", ["s2"], FactConfidence.High),
        ]));

        await world.RunAsync(Ct);

        var request = world.Extractor.Requests.Single();
        request.Sources.Any(source => source.Title.Any(char.IsControl)).ShouldBeFalse("the control character never reaches the prompt");
        request.Sources.Any(source => source.Title.Contains("Nhân chứng")).ShouldBeTrue("the title text survives");

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.FactSheetCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Pipeline_UsageRecordedWhenCarried_AndNotWhenTheCallCannotBeBilled()
    {
        var billable = await NewWorldAsync();
        billable.Extractor.EnqueueInvalidOutput();
        billable.Extractor.EnqueueInvalidOutput(new TokenUsage(10, 0, 0, 5, 0));

        await billable.RunAsync(Ct);

        billable.Meter.Items.Count.ShouldBe(2, "each attempt is metered when it reports usage, even on a failing run");
        billable.Meter.Items.ShouldContain(item => item.InputTokens == 10 && item.OutputTokens == 5);
        billable.Meter.Items.ShouldAllBe(item => item.Operation == GenerateFactSheetPipeline.Operation);

        var unbillable = await NewWorldAsync();
        unbillable.Extractor.EnqueueFundsExhausted();

        await unbillable.RunAsync(Ct);

        unbillable.Meter.Items.ShouldBeEmpty("a call that cannot have been billed is never metered (MayBeBilled = false)");
    }

    [Fact]
    public async Task Pipeline_FundsExhausted_FailsProviderFundsExhausted_AndRefunds()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueFundsExhausted();

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.ProviderFundsExhausted);
        run.ErrorDetail.ShouldBe("funds_exhausted");
        world.Ledger.Refunds.ShouldBe(new[] { ("provider_funds_exhausted", world.Reservation) });
        world.Meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_ProviderError_FailsFactSheetGenerationFailed_AndRefunds()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueProviderError(status: 422);

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("provider_error");
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_generation_failed", world.Reservation) });
    }

    [Fact]
    public async Task Pipeline_ProviderUnavailable_FailsFactSheetGenerationFailed_AndRefunds()
    {
        var world = await NewWorldAsync();
        world.Extractor.EnqueueUnavailable(usage: new TokenUsage(20, 0, 0, 7, 0));

        await world.RunAsync(Ct);

        var run = await world.ReadRunAsync();
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("provider_unavailable");
        world.Meter.Items.ShouldHaveSingleItem("the failed call still reported usage");
        world.Ledger.Refunds.ShouldBe(new[] { ("provider_unavailable", world.Reservation) });
    }

    [Fact]
    public async Task Pipeline_IndependentCancellation_FailsTheRun_NotAnEscapingCancellation()
    {
        // The extractor's call is cancelled by a token that is NOT the job's: the pipeline's
        // `when (jobToken.IsCancellationRequested)` filter does not match, so the OCE must be caught and mapped to a
        // failed run (never escape to Hangfire). The pipeline has no distinct code for an externally-cancelled call,
        // so it lands on the generic failure code with the `unexpected_error` detail; the guarantee under test is
        // that the run fails and the reservation is refunded (failure class 3 is only the job's own token).
        var world = await NewWorldAsync();
        world.Extractor.EnqueueCancel();
        using var jobToken = new CancellationTokenSource();

        await Should.NotThrowAsync(() => world.RunAsync(jobToken.Token));

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("unexpected_error");
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_generation_failed", world.Reservation) });
    }

    [Fact]
    public async Task Pipeline_CancelledJobTokenAfterDispatch_LeavesTheRunForTheSweeper_AndRecordsTheBound()
    {
        // Hangfire's shutdown token cancels the call after the request was dispatched: the paid call is recorded at
        // the D1 bound (the provider can still bill it), and the run is left `running` for StaleGenerationSweeperJob,
        // which owns the outcome and the refund (SEC-2).
        using var jobToken = new CancellationTokenSource();
        var world = await NewWorldAsync(textClient: new HangingTextClient(dispatch: true, onCall: jobToken.Cancel));

        await Should.NotThrowAsync(() => world.RunAsync(jobToken.Token));

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Running, "a shutdown leaves the run for StaleGenerationSweeperJob");
        world.Ledger.Refunds.ShouldBeEmpty("the sweeper owns the refund of a cancelled run");
        (await world.FactSheetCountAsync()).ShouldBe(0);

        var item = world.Meter.Items.ShouldHaveSingleItem("a dispatched call is metered even when its host shuts down");
        item.Estimated.ShouldBeTrue();
        item.CostUsd.ShouldBe(FixedFactSheetEstimator.Bound);
        item.Operation.ShouldBe(GenerateFactSheetPipeline.Operation);
    }

    [Fact]
    public async Task Pipeline_CancelledBeforeDispatch_RecordsNoUsage()
    {
        // The same shutdown, but the request never left this process: nothing can have been billed, so nothing is
        // metered, and the run is still left to the sweeper (SEC-2).
        using var jobToken = new CancellationTokenSource();
        var world = await NewWorldAsync(textClient: new HangingTextClient(dispatch: false, onCall: jobToken.Cancel));

        await Should.NotThrowAsync(() => world.RunAsync(jobToken.Token));

        world.Meter.Items.ShouldBeEmpty();
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Running);
    }

    [Fact]
    public async Task Pipeline_DeadlineCancelledAfterDispatch_MetersTheBound_AndFailsDeadlineExceeded()
    {
        // This run's own deadline fires while the request is in flight, so the failure is this run's (a typed
        // `deadline_exceeded` with its reservation released), and the dispatched call is still metered (SEC-2).
        var world = await NewWorldAsync(
            textClient: new HangingTextClient(dispatch: true),
            job: new FactSheetJobOptions { TimeoutSeconds = 1 });

        await Should.NotThrowAsync(() => world.RunAsync(Ct));

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("deadline_exceeded");
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_generation_failed", world.Reservation) });
        world.Ledger.Settled.ShouldBeEmpty();

        var item = world.Meter.Items.ShouldHaveSingleItem("the deadline cancelled a request that was already sent");
        item.Estimated.ShouldBeTrue();
        item.CostUsd.ShouldBe(FixedFactSheetEstimator.Bound);
        (await world.FactSheetCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Pipeline_TwoDeliveriesOfOneQueuedRun_OneExtraction_OneSheet_OneCharge()
    {
        // At-least-once delivery: a second job starts for the same queued run while the first is working it. The
        // claim is one conditional UPDATE, so exactly one delivery works the run — the loser stops quietly, with no
        // failure write, no refund and no second paid call (CODE-1/SEC-1).
        var world = await NewWorldAsync();
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));
        var second = world.SecondPipeline();

        await Task.WhenAll(world.RunAsync(Ct), second.RunAsync(world.RunId, Ct));

        world.Extractor.Calls.ShouldBe(1, "only the delivery that won the claim may reach the paid extractor");
        world.Extractor.Requests.ShouldHaveSingleItem();
        (await world.FactSheetCountAsync()).ShouldBe(1);
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.ReadRunAsync()).ErrorCode.ShouldBeNull("the delivery that lost the claim must not fail the run");
        world.Meter.Items.Count.ShouldBe(1);
        world.Ledger.Settled.ShouldBe(new[] { world.Reservation });
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_StaleRunningRun_IsTakenOverByExactlyOneDelivery()
    {
        // A `running` run its worker stopped reporting on — older than the run deadline plus the sweep grace — is
        // claimable again (the claim's stale branch), and the claim stamps the takeover instant so a second
        // delivery of the same run loses it: at most one paid call, one draft and one charge (CODE-1/SEC-1, N3).
        var job = new FactSheetJobOptions { TimeoutSeconds = FactSheetJobOptions.MinTimeoutSeconds };
        var staleBy = job.Timeout + StoryGenerationPipeline.SweepGrace + TimeSpan.FromSeconds(1);
        var world = await NewWorldAsync(job: job, runningSince: staleBy);
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));
        var second = world.SecondPipeline();

        await Task.WhenAll(world.RunAsync(Ct), second.RunAsync(world.RunId, Ct));

        world.Extractor.Calls.ShouldBe(1, "only the delivery that won the stale claim may reach the paid extractor");
        world.Extractor.Requests.ShouldHaveSingleItem();
        (await world.FactSheetCountAsync()).ShouldBe(1);
        (await world.ReadRunAsync()).Status.ShouldBe(GenerationRunStatus.Succeeded);
        (await world.ReadRunAsync()).ErrorCode.ShouldBeNull("the delivery that lost the stale claim must not fail the run");
        world.Meter.Items.Count.ShouldBe(1);
        world.Ledger.Settled.ShouldBe(new[] { world.Reservation });
        world.Ledger.Refunds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_NonStaleRunningRun_StaysHeld_NoExtraction_NoFailure_NoRefund()
    {
        // A `running` run still inside its deadline plus the sweep grace is another worker's business: this
        // delivery neither fails it nor refunds it nor buys a call. It is the fast-path hold over a non-stale run,
        // which the claim would reject anyway (N3).
        var job = new FactSheetJobOptions { TimeoutSeconds = FactSheetJobOptions.MinTimeoutSeconds };
        var world = await NewWorldAsync(job: job, runningSince: TimeSpan.FromSeconds(1));
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));

        await world.RunAsync(Ct);

        world.Extractor.Calls.ShouldBe(0, "a held run buys no extraction");
        (await world.FactSheetCountAsync()).ShouldBe(0);
        world.Meter.Items.ShouldBeEmpty();
        world.Ledger.Settled.ShouldBeEmpty();
        world.Ledger.Refunds.ShouldBeEmpty("the sweeper, not this delivery, owns a stuck run");

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Running, "a held run is left exactly as it was");
        run.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Pipeline_UnusableVersionWatermark_FailsTheRunTyped_WithoutLimbo()
    {
        // An internal error of the saving stage (here a version counter a corrupt row pushed past int.MaxValue)
        // must end the run typed and release the reservation, never leave it `running` until the sweeper.
        var world = await NewWorldAsync(corruptVersion: true);
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));

        await Should.NotThrowAsync(() => world.RunAsync(Ct));

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Failed);
        run.ErrorCode.ShouldBe(ErrorCodes.FactSheetGenerationFailed);
        run.ErrorDetail.ShouldBe("unexpected_error");
        run.ResultFactSheetId.ShouldBeNull();
        world.Ledger.Refunds.ShouldBe(new[] { ("fact_sheet_generation_failed", world.Reservation) });
        world.Ledger.Settled.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pipeline_MeterUnavailable_LogsAnError_AndStillFinishesTheRun()
    {
        // A lost usage row is an Error, not a Warning (SEC-4): the platform spend cap is read from usage_records,
        // so an operator must see it. Metering is evidence, not control flow, so the run still succeeds and
        // settles — the outcome the invoice, not the meter, will settle.
        var logger = new RecordingLogger();
        var world = await NewWorldAsync(logger: logger);
        world.Extractor.EnqueueOk(Facts(("s1", "Người gác bến giữ ngọn đèn dầu bên cổng.", FactConfidence.High)));
        world.Meter.ThrowOnRecord = true;

        await Should.NotThrowAsync(() => world.RunAsync(Ct));

        var run = await world.ReadRunAsync();
        run.Status.ShouldBe(GenerationRunStatus.Succeeded);
        world.Ledger.Settled.ShouldBe(new[] { world.Reservation });
        world.Meter.Items.ShouldBeEmpty("the meter failed before it stored anything");

        var entry = logger.Entries.Where(logged => logged.Level == LogLevel.Error).ShouldHaveSingleItem(
            "a lost usage row is the only error of a successful run");
        entry.Event.ShouldBe(new EventId(6179));
    }

    private async Task<World> NewWorldAsync(
        string noteText = NoteProse,
        string? urlText = null,
        string? sourceTitle = null,
        IReadOnlyList<string>? urlSources = null,
        FactSheetExtractionOptions? extraction = null,
        bool removeSourcesBeforePlan = false,
        FactSheetJobOptions? job = null,
        ITextClient? textClient = null,
        bool corruptVersion = false,
        ILogger<GenerateFactSheetPipeline>? logger = null,
        TimeSpan? runningSince = null)
    {
        var setup = new TestCurrentUser();
        var user = await new UserBuilder(Database, setup).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, setup).CreateAsync(user.Id, topic: Topic.TrueCrime, language: Language.Vi, ct: Ct);
        var episode = await new EpisodeBuilder(Database, setup).CreateAsync(user.Id, series.Id, status: EpisodeStatus.Draft, ct: Ct);

        var reservation = Guid.CreateVersion7();
        var noteId = Guid.CreateVersion7();
        var urlIds = new List<Guid>();
        var texts = new FakeExtractedTextStore();

        await using (var seed = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, new FakeClock(Now)))
        {
            seed.StorySources.Add(StorySource.CreateNote(noteId, user.Id, episode.Id, noteText, Now).Value);

            // Each url source is stamped one second after the previous one (and after the note at `Now`), so the
            // plan's `created_at then id` order — and with it the s1…sN alias assignment — is deterministic.
            // Relying on the random tail of two Guid.CreateVersion7 ids created in the same millisecond would
            // make the alias a coin flip.
            if (urlSources is not null)
            {
                foreach (var text in urlSources)
                {
                    await SeedUrlAsync(seed, texts, urlIds, user.Id, episode.Id, text, "Bài báo", Now.AddSeconds(urlIds.Count + 1));
                }
            }
            else if (urlText is not null)
            {
                await SeedUrlAsync(seed, texts, urlIds, user.Id, episode.Id, urlText, sourceTitle ?? "Bài báo gốc", Now.AddSeconds(1));
            }

            var run = GenerationRun.Queue(
                    user.Id,
                    series.Id,
                    episode.Id,
                    GenerationKind.FactSheet,
                    GenerationTrigger.User,
                    null,
                    EpisodeStatus.Draft,
                    null,
                    reservation,
                    0.5m,
                    Now,
                    countsTowardLimit: false)
                .Value;

            if (runningSince is { } since)
            {
                // A run its worker started and then stopped reporting on: `running` with a `StartedAt` in the
                // past. Older than the run deadline plus the sweep grace it is stale enough for a takeover; a
                // recent one is still another worker's business.
                run.Start(Now - since);
            }

            seed.GenerationRuns.Add(run);
            seed.UsageRecords.Add(UsageRecord.Record(
                user.Id,
                UsageOperation.FactSheet,
                "openai",
                0.25m,
                Now,
                episodeId: episode.Id,
                seriesId: series.Id,
                generationRunId: run.Id,
                reservationId: reservation));

            await seed.SaveChangesAsync(Ct);

            if (corruptVersion)
            {
                // A version counter one above int.MaxValue, so the saving stage's `max(version) + 1` cannot be a
                // valid version: the reachable internal error of the save path.
                seed.FactSheets.Add(FactSheet.CreateDraft(
                    user.Id,
                    episode.Id,
                    int.MaxValue,
                    [],
                    new Dictionary<string, Guid>(StringComparer.Ordinal),
                    inputTruncated: false,
                    maxFactId: 0,
                    generationRunId: run.Id,
                    promptVersion: "pv-corrupt",
                    createdAt: Now));
                await seed.SaveChangesAsync(Ct);
            }

            if (removeSourcesBeforePlan)
            {
                SqlProbe.Execute(Database, $"DELETE FROM story_sources WHERE episode_id = '{episode.Id}'");
            }

            return new World(
                Database,
                user.Id,
                series.Id,
                episode.Id,
                run.Id,
                new ReservationId(reservation),
                texts,
                extraction,
                noteId,
                urlIds.Count > 0 ? urlIds[0] : null,
                job,
                textClient,
                logger);
        }
    }

    private static async Task SeedUrlAsync(
        AppDbContext seed,
        FakeExtractedTextStore texts,
        List<Guid> urlIds,
        Guid userId,
        Guid episodeId,
        string text,
        string title,
        DateTimeOffset createdAt)
    {
        var parsed = SourceUrl.Parse($"https://example.com/article-{Guid.NewGuid():N}", new FakePublicSuffixList()).Value;
        var url = StorySource.CreateUrl(Guid.CreateVersion7(), userId, episodeId, parsed, createdAt).Value;
        url.MarkFetched(
            parsed.Value,
            title,
            "text/html",
            new string('a', 64),
            $"sources/{url.Id}.txt",
            text.Length,
            createdAt);
        texts.Add($"sources/{url.Id}.txt", text);
        urlIds.Add(url.Id);
        seed.StorySources.Add(url);

        await Task.CompletedTask;
    }

    private static FactsV1 Facts(params (string Alias, string Statement, FactConfidence Confidence)[] drafts) =>
        new([.. drafts.Select(draft => new FactDraftV1(draft.Statement, [draft.Alias], draft.Confidence))]);

    private sealed class World
    {
        private readonly TestDatabase _database;
        private readonly FactSheetExtractionOptions _extraction;
        private readonly FactSheetJobOptions _job;
        private readonly FakeExtractedTextStore _texts;
        private readonly ILogger<GenerateFactSheetPipeline> _logger;

        public World(
            TestDatabase database,
            Guid userId,
            Guid seriesId,
            Guid episodeId,
            Guid runId,
            ReservationId reservation,
            FakeExtractedTextStore texts,
            FactSheetExtractionOptions? extraction,
            Guid noteSourceId,
            Guid? urlSourceId,
            FactSheetJobOptions? job = null,
            ITextClient? textClient = null,
            ILogger<GenerateFactSheetPipeline>? logger = null)
        {
            _database = database;
            UserId = userId;
            SeriesId = seriesId;
            EpisodeId = episodeId;
            RunId = runId;
            Reservation = reservation;
            NoteSourceId = noteSourceId;
            UrlSourceId = urlSourceId;
            _extraction = extraction ?? new FactSheetExtractionOptions();
            _texts = texts;
            _job = job ?? new FactSheetJobOptions { TimeoutSeconds = 360 };
            _logger = logger ?? NullLogger<GenerateFactSheetPipeline>.Instance;

            var clock = new FakeClock(Now);
            var db = database.CreateAppDbContext(new TestCurrentUser { Id = userId }, clock);

            // The cancellation cases drive the production extractor over a scripted client, so the SEC-2 row it
            // writes for a dispatched call is asserted on this world's meter; every other case scripts the fake.
            IFactExtractor extractor = textClient is null
                ? Extractor
                : new OpenAiFactExtractor(textClient, new FixedPromptCatalog(), Options.Create(new FactsOptions()), Meter, Estimator);

            Pipeline = Build(db, clock, extractor);
        }

        private GenerateFactSheetPipeline Build(AppDbContext db, IClock clock, IFactExtractor extractor) =>
            new(
                db,
                clock,
                Ledger,
                Meter,
                extractor,
                Moderator,
                Events,
                _texts,
                new FixedPriceCatalog(),
                new FixedPromptCatalog(),
                _job,
                _extraction,
                new SimilaritySettings(),
                _logger);

        /// <summary>
        /// A second pipeline over the same run, the same collaborators and its own write context: the delivery
        /// seam of the claim race, where two jobs work one run at once.
        /// </summary>
        /// <returns>The second pipeline.</returns>
        public GenerateFactSheetPipeline SecondPipeline() =>
            Build(_database.CreateAppDbContext(new TestCurrentUser { Id = UserId }, new FakeClock(Now)), new FakeClock(Now), Extractor);

        public Guid UserId { get; }

        public Guid SeriesId { get; }

        public Guid EpisodeId { get; }

        public Guid RunId { get; }

        public ReservationId Reservation { get; }

        public Guid NoteSourceId { get; }

        public Guid? UrlSourceId { get; }

        public GenerateFactSheetPipeline Pipeline { get; }

        public FakeFactExtractor Extractor { get; } = new();

        public FakeContentModerator Moderator { get; } = new();

        public RecordingEvents Events { get; } = new();

        public RecordingLedger Ledger { get; } = new();

        public RecordingMeter Meter { get; } = new();

        /// <summary>The D1 bound the production extractor writes for a dispatched call its caller cancelled.</summary>
        public IFactSheetCostEstimator Estimator { get; } = new FixedFactSheetEstimator();

        public Task RunAsync(CancellationToken ct = default) => Pipeline.RunAsync(RunId, ct == default ? Ct : ct);

        public async Task<World> RunSecondRunAsync()
        {
            var reservation = Guid.CreateVersion7();
            Guid runId;
            await using (var seed = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId }, new FakeClock(Now)))
            {
                var run = GenerationRun.Queue(
                        UserId,
                        SeriesId,
                        EpisodeId,
                        GenerationKind.FactSheet,
                        GenerationTrigger.User,
                        null,
                        EpisodeStatus.Draft,
                        null,
                        reservation,
                        0.5m,
                        Now,
                        countsTowardLimit: false)
                    .Value;
                seed.GenerationRuns.Add(run);
                await seed.SaveChangesAsync(Ct);
                runId = run.Id;
            }

            return new World(
                _database,
                UserId,
                SeriesId,
                EpisodeId,
                runId,
                new ReservationId(reservation),
                new FakeExtractedTextStore(),
                _extraction,
                NoteSourceId,
                UrlSourceId,
                _job);
        }

        public async Task<GenerationRun> ReadRunAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId });

            return await db.GenerationRuns.AsNoTracking().SingleAsync(run => run.Id == RunId, Ct);
        }

        public async Task<FactSheet> FactSheetAsync(int version = 1)
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId });

            return await db.FactSheets.AsNoTracking().SingleAsync(sheet => sheet.EpisodeId == EpisodeId && sheet.Version == version, Ct);
        }

        public async Task<int> FactSheetCountAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId });

            return await db.FactSheets.CountAsync(sheet => sheet.EpisodeId == EpisodeId, Ct);
        }

        public async Task<IReadOnlyList<string>> OutboxTypesAsync()
        {
            await using var db = _database.CreateAppDbContext(new TestCurrentUser { Id = UserId });

            return await db.OutboxMessages.Where(message => message.UserId == UserId).Select(message => message.Type).ToListAsync(Ct);
        }
    }

    /// <summary>A scriptable extractor with an optional pre-call hook and an explicit cancellation outcome.</summary>
    private sealed class FakeFactExtractor : IFactExtractor
    {
        private readonly Queue<Func<CancellationToken, ValueTask<TextCallResult<FactsV1>>>> _script = new();

        public Action? BeforeCall { get; set; }

        public List<FactExtractionRequest> Requests { get; } = [];

        public int Calls => Requests.Count;

        public void Enqueue(TextCallResult<FactsV1> result) =>
            _script.Enqueue(_ => ValueTask.FromResult(result));

        public void EnqueueOk(FactsV1 facts, TokenUsage? usage = null) =>
            Enqueue(TextCallResult<FactsV1>.Ok(facts, "gpt-6-luna", usage, "req-fake"));

        public void EnqueueInvalidOutput(TokenUsage? usage = null) =>
            Enqueue(TextCallResult<FactsV1>.InvalidOutput("gpt-6-luna", usage, null, [TextCallInvalidOutput<FactsV1>.SchemaInvalid]));

        public void EnqueueModerated() =>
            Enqueue(TextCallResult<FactsV1>.Moderated("gpt-6-luna", null, null, ["content_filter"]));

        public void EnqueueFundsExhausted() =>
            Enqueue(TextCallResult<FactsV1>.ProviderFundsExhausted("gpt-6-luna", null));

        public void EnqueueProviderError(int status) =>
            Enqueue(TextCallResult<FactsV1>.ProviderError("gpt-6-luna", status, null));

        public void EnqueueUnavailable(TokenUsage? usage = null) =>
            Enqueue(TextCallResult<FactsV1>.ProviderUnavailable("gpt-6-luna", null, usage));

        // An OperationCanceledException carrying a pre-cancelled token that is NOT the caller's: the pipeline's
        // `when (jobToken.IsCancellationRequested)` filter does not match it, so it must be mapped to a failure
        // instead of escaping the job.
        public void EnqueueCancel()
        {
            using var external = new CancellationTokenSource();
            external.Cancel();
            var token = external.Token;

            _script.Enqueue(_ => throw new OperationCanceledException(token));
        }

        public ValueTask<TextCallResult<FactsV1>> ExtractAsync(FactExtractionRequest req, CancellationToken ct)
        {
            BeforeCall?.Invoke();
            Requests.Add(req);

            return _script.Count == 0
                ? throw new InvalidOperationException("The extractor script is empty.")
                : _script.Dequeue()(ct);
        }
    }

    private sealed class RecordingEvents : IModerationEventWriter
    {
        public List<ModerationStage> Stages { get; } = [];

        public Task WriteAsync(
            Guid userId,
            Guid? episodeId,
            ModerationSource source,
            ModerationStage stage,
            IReadOnlyCollection<string> categories,
            string? ownText,
            CancellationToken ct)
        {
            Stages.Add(stage);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLedger : ICreditLedger
    {
        public List<(string Reason, ReservationId Reservation)> Refunds { get; } = [];

        public List<ReservationId> Settled { get; } = [];

        public ValueTask<ErrorOr<ReservationId>> ReserveAsync(Guid userId, CreditOperation op, Guid referenceId, UsageBound bound, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<ErrorOr<Success>> SettleAsync(ReservationId reservationId, CancellationToken ct)
        {
            Settled.Add(reservationId);

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }

        public ValueTask<ErrorOr<Success>> RefundAsync(ReservationId reservationId, string reason, CancellationToken ct)
        {
            Refunds.Add((reason, reservationId));

            return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }

    private sealed class RecordingMeter : IUsageMeter
    {
        public List<UsageItem> Items { get; } = [];

        /// <summary>Makes every write fail, the way an unavailable meter does.</summary>
        public bool ThrowOnRecord { get; set; }

        public Task RecordAsync(UsageItem item, CancellationToken ct)
        {
            if (ThrowOnRecord)
            {
                throw new InvalidOperationException("The usage meter is unavailable.");
            }

            Items.Add(item);

            return Task.CompletedTask;
        }
    }

    /// <summary>Records the level and the event id of every entry the pipeline writes, and nothing else.</summary>
    private sealed class RecordingLogger : ILogger<GenerateFactSheetPipeline>
    {
        public List<(LogLevel Level, EventId Event)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId));
    }

    private sealed class FixedPriceCatalog : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(Version(1));

        public Task<PriceVersion> GetAsync(int requested, CancellationToken ct) =>
            Task.FromResult(Version(requested));

        private static PriceVersion Version(int number) =>
            PriceVersion.Create(number, Now.AddDays(-1), "{}", "{}", "fake-price-version", Now.AddDays(-1));
    }

    private sealed class FixedPromptCatalog : IPromptCatalog
    {
        public string Release => "2026.10.1";

        public IReadOnlyDictionary<string, PromptFileInfo> Files { get; } =
            new Dictionary<string, PromptFileInfo>(StringComparer.Ordinal);

        public string BuildPromptVersion(PromptVersionParts parts) => "pv-v1";

        public string GetSystemPrompt() => string.Empty;

        public string GetPreset(Topic topic, Language language) => string.Empty;

        public string GetShared(PromptFile file, Language language) => string.Empty;

        public string GetStepPrompt(PromptStep promptStep) => string.Empty;

        public ReadOnlyMemory<byte> GetSchema(string name) => ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// A text client whose call never comes back: it optionally marks the request dispatched (the transport's own
    /// act, <see cref="TextCallScope"/>), runs the hook, then hangs until the caller's token cancels it. The
    /// before-dispatch case models a request cancelled before it ever left this process (SEC-2).
    /// </summary>
    private sealed class HangingTextClient(bool dispatch, Action? onCall = null) : ITextClient
    {
        public async ValueTask<TextCallResult<T>> CallAsync<T>(TextCallRequest req, CancellationToken ct)
            where T : class
        {
            if (dispatch)
            {
                req.MarkDispatched("gpt-6-luna");
            }

            onCall?.Invoke();

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);

            throw new UnreachableException("The hanging client only leaves through its token.");
        }
    }

    /// <summary>The D1 bound the SEC-2 row is written at, the same value the world's run reserved.</summary>
    private sealed class FixedFactSheetEstimator : IFactSheetCostEstimator
    {
        /// <summary>The worst-case cost every test run reserves and the estimator reports.</summary>
        public const decimal Bound = 0.25m;

        public Task<ErrorOr<UsageBound>> WorstCaseAsync(CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(Bound, 60_000, 4_000));
    }
}
