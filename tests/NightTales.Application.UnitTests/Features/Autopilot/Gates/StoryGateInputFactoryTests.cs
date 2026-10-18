using System.Text.Json.Nodes;
using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Stories;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Gates;
using NightTales.Domain.Usage;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="StoryGateInputFactory"/> (02.8.2-T1 step 1, plan §3.1): the <c>audit_report</c> (<c>audit.v1</c>) read
/// that checks 1–6 and 8 consume, the D4 band of check 11, the keyword cap of check 12 and the reason the keyword
/// violations of check 12 never hide.
/// </summary>
/// <remarks>
/// <para>
/// The suite is about one property: <b>nothing the factory cannot read can become a pass</b>. A missing, garbage or
/// half-written document, an unreadable issue entry, a foreign score and a missing moderation verdict all end in a
/// value the Domain rule fails closed on (<see cref="GateDetailCodes.FieldAbsent"/>, an objective issue, a null
/// score) — never in an empty scorecard that reads as clean.
/// </para>
/// <para>
/// The one thing that is not a value but an error is the D4 band: check 11 has no closed value, so a missing
/// price-version cell stops the evaluation instead of comparing the draft against a band nobody calibrated.
/// </para>
/// <para>
/// Pure: the price catalog is the only port, and every case below answers it with
/// <see cref="PriceLimits.D4Defaults"/> or with nothing at all.
/// </para>
/// </remarks>
public sealed class StoryGateInputFactoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The acceptance row: a version whose <c>audit_report</c> is missing has no <c>model_review</c>, and check 1
    /// fails — the gate never reads "no review" as "nothing to complain about".
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_MissingModelReview_FailsClosed()
    {
        var reading = await ReadAsync(row: new Row { AuditReport = null });

        reading.IsError.ShouldBeFalse();
        reading.Value.Audit.ModelReviewPresent.ShouldBeFalse();
        reading.Value.Audit.ModelReviewStale.ShouldBeTrue("a missing flag is stale");
        reading.Value.Audit.Scores.ShouldBeNull("no scorecard at all");
        reading.Value.Audit.RevisionApplied.ShouldBeNull("an unreadable document leaves the revision count unknown");

        var result = StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Failures.ShouldContain(StoryGateCheckIds.SelfReviewPresent);
        result.Failures.ShouldContain(StoryGateCheckIds.SelfReviewMeanScore);
    }

    /// <summary>
    /// A document that is not a JSON object at all fails the same checks closed, and its revision count stays unknown
    /// (null) rather than 0: only a document that <em>was</em> read and carries no revision is a count of 0 (D21 §3.1
    /// check 5 fails closed on null).
    /// </summary>
    [Theory]
    [InlineData("{ not json", null)]
    [InlineData("[]", null)]
    [InlineData("\"audit.v1\"", null)]
    [InlineData("{\"local\": \"not an object\"}", false)]
    public async Task StoryGateInputFactory_UnreadableAudit_FailsClosed(string auditReport, bool? revisionApplied)
    {
        var reading = await ReadAsync(row: new Row { AuditReport = auditReport });

        reading.IsError.ShouldBeFalse();
        reading.Value.Audit.ModelReviewPresent.ShouldBeFalse();
        reading.Value.Audit.Scores.ShouldBeNull();
        reading.Value.Audit.Issues.ShouldBeNull("the issues could not be read, so check 4 fails closed");
        reading.Value.Audit.ModerationFlagged.ShouldBeNull();
        reading.Value.Audit.RevisionApplied.ShouldBe(revisionApplied);
        reading.Value.RealWorld.ShouldBeNull("unknown, which check 8 fails closed on");

        var result = StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults);

        result.Pass.ShouldBeFalse();
        result.Failures.ShouldContain(StoryGateCheckIds.BlockingObjectiveIssues);
        result.Failures.ShouldContain(StoryGateCheckIds.RealWorldSignal);
    }

    /// <summary>
    /// 02.6.2-T1: with an applied revision the current state is review #2, so the scores come from
    /// <c>revision.review_after</c> and model_review's already-fixed issues are not counted — while
    /// <c>local.issues</c> still is (they belong to neither review).
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_AppliedRevision_ReadsReviewAfter()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":3,"pacing":3,"atmosphere":3,"natural_language":3,"originality":3,"factual_care":3,"length_fit":3},"issues":[{"severity":"blocking","kind":"objective","message":"fixed in revision #1"}]},"local":{"issues":[]},"revision":{"applied":true,"reason":"scores","review_after":{"scores":{"hook":5,"pacing":5,"atmosphere":4,"natural_language":5,"originality":4,"factual_care":5,"length_fit":5},"issues":[]}}}""",
        });

        reading.Value.Audit.Scores.ShouldNotBeNull();
        reading.Value.Audit.Scores!.Hook.ShouldBe(5);
        reading.Value.Audit.Issues.ShouldBeEmpty("review #2 has no issues and local.issues is empty");
        reading.Value.Audit.RevisionApplied.ShouldBe(true);

        var result = StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults);

        result.Failures.ShouldNotContain(StoryGateCheckIds.BlockingObjectiveIssues);
        result.Failures.ShouldNotContain(StoryGateCheckIds.SelfReviewMeanScore);
    }

    /// <summary>A revision that claims to be applied but has no complete <c>review_after</c> falls back to the stored review.</summary>
    [Fact]
    public async Task StoryGateInputFactory_AppliedRevisionWithoutReviewAfter_FallsBackToModelReview()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":3,"pacing":3,"atmosphere":3,"natural_language":3,"originality":3,"factual_care":3,"length_fit":3},"issues":[]},"local":{"issues":[]},"revision":{"applied":true,"reason":"scores","review_after":{"scores":{"hook":5}}}}""",
        });

        reading.Value.Audit.RevisionApplied.ShouldBe(true);
        reading.Value.Audit.Scores.ShouldNotBeNull().Hook.ShouldBe(3, "the half-written review_after is not the current state");
    }

    /// <summary>
    /// Check 4 counts <c>local.issues</c> and the selected review's issues, and an issue whose kind is not a string
    /// is carried as an empty kind, which the Domain rule reads as objective ([OK] fail closed).
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_Issues_UnionLocalAndReview_MalformedKindCountsAsObjective()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[{"severity":"advisory","kind":"taste","message":"preference"}]},"local":{"issues":[{"severity":"blocking","rule":"Q-KW-03","message":"keyword"}]},"revision":{"applied":false}}""",
        });

        reading.Value.Audit.Issues!.Count.ShouldBe(2);
        reading.Value.Audit.Issues.ShouldContain(issue => issue.Severity == "advisory" && issue.Kind == "taste");

        var malformed = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[{"severity":"blocking","kind":42,"rule":"other"}]},"revision":{"applied":false}}""",
        });

        malformed.Value.Audit.Issues.ShouldHaveSingleItem().Kind.ShouldBeEmpty();
        StoryGateRules.Evaluate(Compose(malformed.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldContain(StoryGateCheckIds.BlockingObjectiveIssues);
    }

    /// <summary>
    /// Check 12 reads the stored blocked-keyword rules only; the keyword count of check 12 is capped at
    /// <c>ceil(D × K)</c> with K = 6 (P5/F09).
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_KeywordViolationsAndCap()
    {
        var reading = await ReadAsync(row: new Row
        {
            Keywords = ["one", "two", "three"],
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[{"severity":"advisory","rule":"Q-KW-03"},{"severity":"blocking","rule":"keyword_invalid"},{"severity":"advisory","rule":"Q-REP-01"}]},"revision":{"applied":false}}""",
        });

        reading.Value.Version.KeywordViolationCount.ShouldBe(2, "Q-KW-03 and keyword_invalid are the KeywordRules violations");
        reading.Value.Version.Keywords.Count.ShouldBe(3);
        reading.Value.Version.KeywordLimit.ShouldBe(6, "a short episode is one minute at six images per minute");

        StoryGateInputFactory.KeywordLimit(LengthTier.Standard).ShouldBe(48);
        StoryGateInputFactory.KeywordLimit(LengthTier.Long).ShouldBe(90);
    }

    /// <summary>
    /// Check 8 (02.8.2-T1 decision 1): the persisted model claims become one hit per claim with their kind, and the
    /// claim's own wording never leaves <c>audit_report</c>.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_RealWorldClaims_BecomeKindOnlyHits()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[],"real_world_claims":[{"segment":"p:2","text":"a real named event"},{"segment":"title","text":"a dated incident"}]},"local":{"issues":[]},"revision":{"applied":false}}""",
        });

        var signal = reading.Value.RealWorld.ShouldNotBeNull();
        signal.Hit.ShouldBeTrue();
        signal.Hits.Count.ShouldBe(2);
        signal.Hits.ShouldAllBe(hit => hit.Kind == RealWorldSignalKind.ModelClaim);
        signal.Hits.Select(hit => hit.Segment).ShouldBe(["p:2", "title"]);
        signal.Hits.ShouldAllBe(hit => hit.MatchedPhrase == string.Empty && hit.Evidence == string.Empty);

        var result = StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults);

        result.Failures.ShouldContain(StoryGateCheckIds.RealWorldSignal);
    }

    /// <summary>A review that stored no claims array asserted none: no hit, and check 8 passes.</summary>
    [Fact]
    public async Task StoryGateInputFactory_NoClaimsArray_NoHit()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[]},"revision":{"applied":false}}""",
        });

        reading.Value.RealWorld.ShouldNotBeNull();
        reading.Value.RealWorld!.Hit.ShouldBeFalse();
        reading.Value.RealWorld.Hits.ShouldBeEmpty();

        StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldNotContain(StoryGateCheckIds.RealWorldSignal);
    }

    /// <summary>
    /// Check 11's band is the D4 cell of the version's tier × language (02 README D4), and its advisory tolerance is
    /// the Domain's own 15 %.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_LengthBand_FromTheD4Cell()
    {
        var reading = await ReadAsync(row: new Row { Tier = LengthTier.Short, Language = Language.Vi });

        reading.Value.Length.Min.ShouldBe(PriceLimits.D4Defaults.Story[LengthTier.Short][Language.Vi].MinTokens);
        reading.Value.Length.Max.ShouldBe(PriceLimits.D4Defaults.Story[LengthTier.Short][Language.Vi].MaxTokens);
        reading.Value.Length.AdvisoryTolerance.ShouldBe(0.15m);

        var standard = await ReadAsync(row: new Row { Tier = LengthTier.Standard, Language = Language.En });

        standard.Value.Length.Min.ShouldBe(PriceLimits.D4Defaults.Story[LengthTier.Standard][Language.En].MinTokens);
        standard.Value.Length.Min.ShouldNotBe(reading.Value.Length.Min);
    }

    /// <summary>
    /// Check 6 reads the draft-time moderation block and check 7's fallback reads the same block's verdict: a
    /// complete, current verdict is carried through with its categories. The real writers store
    /// <c>{flagged,categories,blocking,stale,unavailable}</c> and no <c>checked_at</c>, so a stored verdict has none.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_StoredModerationVerdict_IsCarried()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[]},"revision":{"applied":false},"moderation":{"flagged":true,"categories":["sexual/minors"],"blocking":["sexual/minors"],"stale":false,"unavailable":false}}""",
        });

        reading.Value.Audit.ModerationFlagged.ShouldBe(true);
        reading.Value.Moderation.Usable.ShouldBeTrue();
        reading.Value.Moderation.Flagged.ShouldBeTrue();
        reading.Value.Moderation.Categories.ShouldBe(["sexual/minors"]);
        reading.Value.Moderation.CheckedAt.ShouldBeNull("the stored verdict carries no checked_at");

        StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldContain(StoryGateCheckIds.ModerationDraft);
    }

    /// <summary>
    /// A block written before a story edit (<c>stale: true</c>) and one the moderator could not answer
    /// (<c>unavailable: true</c>) are not verdicts: check 6 fails closed instead of reading a stale
    /// <c>flagged: false</c> as "the draft is clean" (StoryDraftModeration.NeedsRecheck, D12).
    /// </summary>
    [Theory]
    [InlineData("""{"flagged":false,"categories":[],"blocking":[],"stale":true,"unavailable":false}""")]
    [InlineData("""{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":true}""")]
    [InlineData("""{"flagged":false,"categories":[]}""")]
    [InlineData("""{"flagged":false}""")]
    public async Task StoryGateInputFactory_StaleOrIncompleteModerationBlock_FailsCheck6Closed(string moderation)
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[]},"revision":{"applied":false},"moderation":"""
                + moderation
                + "}",
        });

        reading.Value.Audit.ModerationFlagged.ShouldBeNull();
        reading.Value.Moderation.Usable.ShouldBeFalse("check 7 then checks the current narration and metadata itself");

        StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldContain(StoryGateCheckIds.ModerationDraft);
    }

    /// <summary>
    /// Check 5 (D21): a clean review writes no <c>revision</c> block at all, which is a revision count of 0 — never an
    /// unknown that would fail the check and keep every un-revised story from auto-approval.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_NoRevisionBlock_IsZeroRevisions()
    {
        var reading = await ReadAsync(row: new Row
        {
            AuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[]}}""",
        });

        reading.Value.Audit.RevisionApplied.ShouldBe(false);

        StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldNotContain(StoryGateCheckIds.RevisionCount);
    }

    /// <summary>
    /// SEC-1 (pinned data, <c>StoryApprovalGate</c> 02.3.3-T1): a version written from a fact sheet stays true crime
    /// after the series topic is patched to fiction, so checks 14–15 run and the story is held for the owner.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_FactSheetVersionUnderFictionSeries_IsHeldForTheOwner()
    {
        var sheetId = Guid.CreateVersion7();
        var reading = await ReadAsync(
            row: new Row { Topic = Topic.Ghost, FactSheetId = sheetId },
            approvedFactSheetId: sheetId);

        reading.Value.Series.Topic.ShouldBe(Topic.TrueCrime);

        var result = StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults);

        result.Hold.ShouldBeTrue();
        result.Pass.ShouldBeFalse();
        result.Failures.ShouldContain(StoryGateCheckIds.TrueCrimeHold);
        result.Failures.ShouldNotContain(StoryGateCheckIds.FactSheetCurrent, "the version was written from the current sheet");
    }

    /// <summary>The other half of SEC-1: without an approved sheet the same version fails check 14 too.</summary>
    [Fact]
    public async Task StoryGateInputFactory_FactSheetVersionWithoutApprovedSheet_FailsCheck14()
    {
        var reading = await ReadAsync(row: new Row { Topic = Topic.Ghost, FactSheetId = Guid.CreateVersion7() });

        StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldContain(StoryGateCheckIds.FactSheetCurrent);
    }

    /// <summary>A missing moderation block is not a verdict: check 6 fails on null and the gate checks the draft itself.</summary>
    [Fact]
    public async Task StoryGateInputFactory_MissingModerationBlock_NeedsAGateCheck()
    {
        var reading = await ReadAsync(row: new Row());

        reading.Value.Audit.ModerationFlagged.ShouldBeNull();
        reading.Value.Moderation.Usable.ShouldBeFalse();
        reading.Value.RealWorld.ShouldNotBeNull();
    }

    /// <summary>
    /// The text the gate moderates itself and the similarity projection both come from the stored content document:
    /// narration, title, description and tags, and never a keyword (plan §3.1 checks 6–8).
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_ModerationTextsAndStoryText_ComeFromTheContentDocument()
    {
        var reading = await ReadAsync(row: new Row());

        reading.Value.Version.ContentWarnings.ShouldBe(["atmosphere"]);
        reading.Value.ModerationTexts.ShouldContain("The pier at dawn");
        reading.Value.ModerationTexts.ShouldContain("A quiet story about an empty pier.");
        reading.Value.ModerationTexts.ShouldContain("mist");
        reading.Value.ModerationTexts.ShouldNotContain("quiet riverside morning", "a keyword is not part of the stage's text");
        reading.Value.StoryText.ShouldNotBeNull();
    }

    /// <summary>
    /// An unreadable content document yields no text at all: the past-episode similarity has nothing to score and
    /// its check fails closed, while the length check (11) still answers from the stored <c>word_count</c>, which
    /// the document never carried in the first place.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_UnreadableContent_FailsClosed()
    {
        var reading = await ReadAsync(row: new Row { Content = "not a story.v1 document" });

        reading.Value.Version.ContentWarnings.ShouldBeEmpty();
        reading.Value.ModerationTexts.ShouldBeEmpty();
        reading.Value.StoryText.ShouldBeNull();

        StoryGateRules.Evaluate(Compose(reading.Value), AutopilotGateProfile.Defaults)
            .Failures.ShouldContain(StoryGateCheckIds.SimilarityPastEpisodes);
    }

    /// <summary>
    /// The D4 band cannot be absent: a price version without the episode's cell (and an empty catalog) stops the
    /// evaluation instead of passing check 11 against an invented band.
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_MissingPriceCell_IsAnError()
    {
        var limits = new PriceLimits(new Dictionary<LengthTier, IReadOnlyDictionary<Language, LengthLimit>>
        {
            [LengthTier.Standard] = new Dictionary<Language, LengthLimit>
            {
                [Language.En] = new(400, 520, 8, 12, 2_500, 1_200, 1_500),
            },
        });

        var error = await Factory(limits).ReadAsync(ToRow(new Row { Tier = LengthTier.Short, Language = Language.Vi }), null, Ct);

        error.IsError.ShouldBeTrue();
        error.FirstError.Code.ShouldBe(ErrorCodes.InternalError);
    }

    /// <summary>The catalog's own refusal travels through unchanged: the caller maps it, this factory does not invent one.</summary>
    [Fact]
    public async Task StoryGateInputFactory_EmptyCatalog_ReturnsTheCatalogError()
    {
        var error = await Factory(PriceLimits.D4Defaults, fails: true)
            .ReadAsync(ToRow(new Row()), null, Ct);

        error.IsError.ShouldBeTrue();
        error.FirstError.Code.ShouldBe(ErrorCodes.PriceCatalogEmpty);
    }

    /// <summary>
    /// The series' <c>autopilot_settings</c> names the profile (plan §3.3); an absent, foreign or blank ref is null,
    /// which resolves to the <c>default</c> profile rather than disabling the gate.
    /// </summary>
    [Theory]
    [InlineData("""{"gate_thresholds_ref":"strict"}""", "strict")]
    [InlineData("""{"gate_thresholds_ref":"  "}""", null)]
    [InlineData("""{"gate_thresholds_ref":42}""", null)]
    [InlineData("""{"other":"strict"}""", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public async Task StoryGateInputFactory_ProfileRef_IsReadDefensively(string? settings, string? expected)
    {
        var reading = await ReadAsync(row: new Row { AutopilotSettings = settings });

        reading.Value.ProfileRef.ShouldBe(expected);
    }

    /// <summary>
    /// <see cref="StoryGateInputFactory.Compose"/> is the only place that knows the corpus bound: the Domain input
    /// records the plan's 50 and the attempt the caller passed (D17).
    /// </summary>
    [Fact]
    public async Task StoryGateInputFactory_Compose_RecordsCorpusMaxAndAttempt()
    {
        var reading = await ReadAsync(row: new Row { SimilarityScore = 0.031m });

        var input = StoryGateInputFactory.Compose(reading.Value, 0.044m, null, attempt: 2);

        input.Similarity.CorpusMax.ShouldBe(50);
        input.Similarity.Sources.ShouldBe(0.031m);
        input.Similarity.PastEpisodes.ShouldBe(0.044m);
        input.Attempt.ShouldBe(2);
        input.Series.Topic.ShouldBe(Topic.Ghost);
    }

    private static StoryGateInput Compose(StoryGateInputFactory.StoryGateReading reading) =>
        StoryGateInputFactory.Compose(reading, pastEpisodeSimilarity: null, moderation: null, attempt: 1);

    private static StoryGateInputFactory Factory(PriceLimits limits, bool fails = false) =>
        new(new FixedCatalog(limits, fails));

    private static StoryGateInputFactory.StoryGateRow ToRow(Row row) => new(
        row.VersionId,
        row.WordCount,
        row.Keywords,
        row.Content,
        row.AuditReport,
        row.SimilarityScore,
        row.FactSheetId,
        row.Topic,
        row.Language,
        row.Tier,
        row.AutopilotSettings,
        Start);

    private static ValueTask<ErrorOr<StoryGateInputFactory.StoryGateReading>> ReadAsync(
        Row row,
        Guid? approvedFactSheetId = null) =>
        Factory(PriceLimits.D4Defaults).ReadAsync(ToRow(row), approvedFactSheetId, Ct);

    /// <summary>A <c>story_versions</c> row's stored values, with the clean fixture the tests vary one field of.</summary>
    private sealed record Row
    {
        /// <summary>The audit of a complete, current review: a clean scorecard, no issues, no revision, no claims.</summary>
        public const string CleanAuditReport = """{"model_review":{"stale":false,"scores":{"hook":4,"pacing":4,"atmosphere":4,"natural_language":4,"originality":4,"factual_care":4,"length_fit":4},"issues":[]},"local":{"issues":[]},"revision":{"applied":false}}""";

        public Guid VersionId { get; init; } = Guid.CreateVersion7();

        public int WordCount { get; init; } = 200;

        public IReadOnlyList<string> Keywords { get; init; } = ["mist", "pier"];

        public string Content { get; init; } = StoryContentJson.Write(Draft(), new JsonArray());

        public string? AuditReport { get; init; } = CleanAuditReport;

        public decimal? SimilarityScore { get; init; }

        public Guid? FactSheetId { get; init; }

        public Topic Topic { get; init; } = Topic.Ghost;

        public Language Language { get; init; } = Language.Vi;

        public LengthTier Tier { get; init; } = LengthTier.Short;

        public string? AutopilotSettings { get; init; }
    }

    /// <summary>A complete <c>story.v1</c> draft (the shape the pipeline writes).</summary>
    private static StoryContentV1 Draft() => new(
        "The pier at dawn",
        new StorySegmentV1("Grey mist drifted over the riverside.", ["quiet riverside morning"], []),
        [new StorySegmentV1("A wooden pier stood empty at dusk.", ["empty wooden pier at dusk"], [], "setup")],
        new StorySegmentV1("Cold morning light returned.", ["cold morning light"], []),
        "A quiet story about an empty pier.",
        ["mist", "pier"],
        ["atmosphere"],
        new StoryStructureV1("ghost", "riverside", "a misty dawn", "the narrator", "first_person"));

    /// <summary>The one port of this factory: the D4 limits of the active price version, or its refusal.</summary>
    private sealed class FixedCatalog(PriceLimits limits, bool fails) : IPriceCatalog
    {
        public Task<ErrorOr<PriceVersion>> GetActiveAsync(DateTimeOffset queuedAt, CancellationToken ct) =>
            Task.FromResult<ErrorOr<PriceVersion>>(fails
                ? Error.Failure(ErrorCodes.PriceCatalogEmpty, "no active price version in this test")
                : PriceVersion.Create(
                    1,
                    Start,
                    "{}",
                    PriceVersionJson.WriteLimits(limits),
                    "test",
                    Start));

        public Task<PriceVersion> GetAsync(int version, CancellationToken ct) =>
            throw new NotSupportedException("the gate reads the active version alone");
    }
}
