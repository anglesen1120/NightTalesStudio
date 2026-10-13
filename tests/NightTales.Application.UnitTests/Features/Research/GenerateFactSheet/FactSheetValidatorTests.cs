using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.GenerateFactSheet;
using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.GenerateFactSheet;

/// <summary>
/// 02.5.3-T2: the ordered, pure validation of one <c>facts.v1</c> output — alias resolution, statement
/// shape, deduplication, the verbatim-copy block (F18), the fact cap and id assignment (F12), plus the
/// failure-handling edge cases (a null output, NUL/C0 controls, a lone surrogate, a hostile score and a
/// runaway output).
/// </summary>
public sealed class FactSheetValidatorTests
{
    private static readonly Guid Source1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Source2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    private static readonly Guid EpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");
    private static readonly Guid RunId = Guid.Parse("0197a000-0000-7000-8000-0000000000a3");
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The context helper: alias <c>sN</c> maps to <paramref name="sourceIds"/>[N-1], in order.</summary>
    private static FactSheetValidationContext Context(
        IReadOnlyList<Guid> sourceIds,
        int maxFactId = 0,
        decimal threshold = 0.35m,
        int maxFacts = 40)
    {
        var aliases = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var texts = new Dictionary<Guid, SourceText>();
        for (var index = 0; index < sourceIds.Count; index++)
        {
            var alias = $"s{index + 1}";
            aliases[alias] = sourceIds[index];
            texts[sourceIds[index]] = new SourceText(sourceIds[index], $"source text {index + 1}");
        }

        return new FactSheetValidationContext(aliases, maxFactId, threshold, maxFacts, texts);
    }

    private static FactDraftV1 Draft(
        string statement,
        IReadOnlyList<string> aliases,
        FactConfidence confidence = FactConfidence.High) => new(statement, aliases, confidence);

    private static FactsV1 Output(params FactDraftV1[] facts) => new(facts);

    private static Func<ScoreFactRequest, decimal> Score(decimal value) => _ => value;

    [Fact]
    public void FactSheetValidator_DropsFactWithUnknownAlias()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("A fact.", ["s9"])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.UnknownAlias);
        result.Drops[0].OutputIndex.ShouldBe(0);
        result.ModelFactCount.ShouldBe(1);
        result.NewMaxFactId.ShouldBe(0);
    }

    [Fact]
    public void FactSheetValidator_DropsFactWithNoAlias()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("A fact.", [])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.UnknownAlias);
    }

    [Fact]
    public void FactSheetValidator_KeepsKnownAliasesOnly_WhenPartiallyUnknown()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("A fact.", ["s1", "s9", "s2"])),
            Context(sourceIds: [Source1, Source2]),
            Score(0m));

        var fact = result.Facts.ShouldHaveSingleItem();
        fact.Id.ShouldBe("f1");
        fact.SourceAliases.ShouldBe(["s1", "s2"]);
        fact.SourceIds.ShouldBe([Source1, Source2]);
    }

    [Fact]
    public void FactSheetValidator_DropsStatementEmptyOrOver600Chars()
    {
        var result = FactSheetValidator.Validate(
            Output(
                Draft("   ", ["s1"]),
                Draft(new string('x', FactSheetValidator.StatementMaxChars + 1), ["s1"]),
                Draft(new string('y', FactSheetValidator.StatementMaxChars), ["s1"])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldHaveSingleItem().Statement.Length.ShouldBe(FactSheetValidator.StatementMaxChars);
        result.Drops.Select(drop => drop.Reason)
            .ShouldBe([FactDropReasons.StatementInvalid, FactDropReasons.StatementInvalid]);
    }

    [Fact]
    public void FactSheetValidator_DedupesIdenticalStatements_FirstWins()
    {
        // The two statements differ only in inner whitespace, so they normalise to the same text.
        var result = FactSheetValidator.Validate(
            Output(Draft("Same  fact.", ["s1"]), Draft("Same fact.", ["s1"])),
            Context(sourceIds: [Source1, Source2]),
            Score(0m));

        var fact = result.Facts.ShouldHaveSingleItem();
        fact.Id.ShouldBe("f1");
        fact.Statement.ShouldBe("Same fact.");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.Duplicate);
    }

    [Fact]
    public void FactSheetValidator_DropsVerbatimCopyAboveOverlapThreshold()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("Copied from the source.", ["s1"]), Draft("Paraphrased in our own words.", ["s1"])),
            Context(threshold: 0.35m, sourceIds: [Source1]),
            request => request.Statement == "Copied from the source." ? 0.9m : 0.1m);

        var fact = result.Facts.ShouldHaveSingleItem();
        fact.Statement.ShouldBe("Paraphrased in our own words.");
        fact.SourceOverlap.ShouldBe(0.1m);
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.VerbatimCopy);
    }

    [Fact]
    public void FactSheetValidator_AssignsSequentialIds_FromMaxFactIdPlusOne()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("One.", ["s1"]), Draft("Two.", ["s1"])),
            Context(maxFactId: 3, sourceIds: [Source1]),
            Score(0m));

        result.Facts.Select(fact => fact.Id).ShouldBe(["f4", "f5"]);
        result.NewMaxFactId.ShouldBe(5);
    }

    [Fact]
    public void FactSheetValidator_NeverReusesId_AcrossVersions()
    {
        var first = FactSheetValidator.Validate(
            Output(Draft("One.", ["s1"]), Draft("Two.", ["s1"])),
            Context(maxFactId: 0, sourceIds: [Source1]),
            Score(0m));
        first.Facts.Select(fact => fact.Id).ShouldBe(["f1", "f2"]);

        var second = FactSheetValidator.Validate(
            Output(Draft("Three.", ["s1"])),
            Context(maxFactId: first.NewMaxFactId, sourceIds: [Source1]),
            Score(0m));

        second.NewMaxFactId.ShouldBe(3);
        second.Facts.ShouldHaveSingleItem().Id.ShouldBe("f3");
    }

    [Fact]
    public void FactSheetValidator_CapsFacts_AtMaxFacts()
    {
        var facts = Enumerable.Range(0, 5).Select(index => Draft($"Fact number {index}.", ["s1"])).ToArray();

        var result = FactSheetValidator.Validate(
            Output(facts),
            Context(maxFacts: 2, sourceIds: [Source1]),
            Score(0m));

        result.Facts.Select(fact => fact.Id).ShouldBe(["f1", "f2"]);
        result.Drops.Count.ShouldBe(3);
        result.Drops.ShouldAllBe(drop => drop.Reason == FactDropReasons.OverCap);
    }

    [Fact]
    public void FactSheetValidator_ContentSha256_IsStableForSameFacts()
    {
        var context = Context(sourceIds: [Source1]);
        var first = FactSheetValidator.Validate(Output(Draft("A fact.", ["s1"])), context, Score(0m));
        var second = FactSheetValidator.Validate(Output(Draft("A fact.", ["s1"])), context, Score(0m));

        var sheetOne = FactSheet.CreateDraft(UserId, EpisodeId, 1, first.Facts, context.AliasToSourceId, false, first.NewMaxFactId, RunId, "facts.v1", Now);
        var sheetTwo = FactSheet.CreateDraft(UserId, EpisodeId, 1, second.Facts, context.AliasToSourceId, false, second.NewMaxFactId, RunId, "facts.v1", Now);

        sheetOne.ContentSha256.Length.ShouldBe(64);
        sheetOne.ContentSha256.ShouldBe(sheetTwo.ContentSha256);
    }

    [Fact]
    public void FactSheetValidator_KeepsLowConfidenceFact()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("A thinly sourced fact.", ["s1"], FactConfidence.Low)),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldHaveSingleItem().Confidence.ShouldBe(FactConfidence.Low);
        result.Drops.ShouldBeEmpty();
    }

    [Fact]
    public void FactSheetValidator_DropsStatementWithNulOrC0Control()
    {
        var result = FactSheetValidator.Validate(
            Output(
                Draft("Nul\0fact.", ["s1"]),
                Draft("Bell\u0007fact.", ["s1"]),
                Draft("Del\u007Ffact.", ["s1"]),
                Draft("Tabs\tand\r\nnewlines.", ["s1"])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldHaveSingleItem().Statement.ShouldBe("Tabs and newlines.");
        result.Drops.Select(drop => drop.Reason).ShouldBe(
        [
            FactDropReasons.ControlCharactersInvalid,
            FactDropReasons.ControlCharactersInvalid,
            FactDropReasons.ControlCharactersInvalid,
        ]);
    }

    [Fact]
    public void FactSheetValidator_DropsLoneSurrogate()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("Bad \uD800 fact.", ["s1"])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void FactSheetValidator_ScoreAboveThresholdIncludingNonFinite_Drops()
    {
        // decimal has no NaN/Infinity, so "non-finite" is exercised as an out-of-range value; the validator
        // must fail closed on a negative or >1 score rather than trusting it as "not a copy".
        var result = FactSheetValidator.Validate(
            Output(
                Draft("Above.", ["s1"]),
                Draft("Negative.", ["s1"]),
                Draft("Huge.", ["s1"]),
                Draft("Fine.", ["s1"])),
            Context(threshold: 0.5m, sourceIds: [Source1]),
            request => request.Statement switch
            {
                "Above." => 0.6m,
                "Negative." => -0.1m,
                "Huge." => decimal.MaxValue,
                _ => 0m,
            });

        result.Facts.ShouldHaveSingleItem().Statement.ShouldBe("Fine.");
        result.Drops.Select(drop => drop.Reason).ShouldBe(
        [
            FactDropReasons.VerbatimCopy,
            FactDropReasons.VerbatimCopy,
            FactDropReasons.VerbatimCopy,
        ]);
    }

    [Fact]
    public void FactSheetValidator_CapsWorkOnHugeOutput()
    {
        var scoreCalls = 0;
        var facts = Enumerable.Range(0, 10_000).Select(index => Draft($"Fact number {index}.", ["s1"])).ToArray();

        var result = FactSheetValidator.Validate(
            Output(facts),
            Context(maxFacts: 10, sourceIds: [Source1]),
            _ =>
            {
                scoreCalls++;
                return 0m;
            });

        result.ModelFactCount.ShouldBe(10_000);
        result.Facts.Count.ShouldBe(10);
        result.Drops.Count.ShouldBe(10_000 - 10);
        scoreCalls.ShouldBe(40, "only the MaxFacts x 4 scan window is scored");
    }

    [Fact]
    public void FactSheetValidator_NullFacts_ReturnsEmpty()
    {
        var nullList = FactSheetValidator.Validate(new FactsV1(null!), Context(sourceIds: [Source1]), Score(0m));
        nullList.Facts.ShouldBeEmpty();
        nullList.Drops.ShouldBeEmpty();
        nullList.ModelFactCount.ShouldBe(0);
        nullList.NewMaxFactId.ShouldBe(0);

        var nullElement = FactSheetValidator.Validate(new FactsV1([null!]), Context(sourceIds: [Source1]), Score(0m));
        nullElement.Facts.ShouldBeEmpty();
        nullElement.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.UnknownAlias);

        var nullOutput = FactSheetValidator.Validate(null!, Context(sourceIds: [Source1]), Score(0m));
        nullOutput.Facts.ShouldBeEmpty();
        nullOutput.ModelFactCount.ShouldBe(0);
    }

    [Fact]
    public void FactSheetValidator_KeepsExactlyMaxFacts_AndDropsTheFortyFirstAsOverCap()
    {
        var facts = Enumerable.Range(0, 41).Select(index => Draft($"Distinct fact {index}.", ["s1"])).ToArray();

        var result = FactSheetValidator.Validate(Output(facts), Context(sourceIds: [Source1]), Score(0m));

        result.Facts.Count.ShouldBe(40);
        result.Facts[39].Statement.ShouldBe("Distinct fact 39.");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.OverCap);
        result.Drops[0].OutputIndex.ShouldBe(40);
        result.NewMaxFactId.ShouldBe(40);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    public void FactSheetValidator_KeepsAnyCountBelowTheCap_WithoutDropping(int count)
    {
        var facts = Enumerable.Range(0, count).Select(index => Draft($"Distinct fact {index}.", ["s1"])).ToArray();

        var result = FactSheetValidator.Validate(Output(facts), Context(sourceIds: [Source1]), Score(0m));

        result.Facts.Count.ShouldBe(count);
        result.Drops.ShouldBeEmpty();
        result.ModelFactCount.ShouldBe(count);
    }

    [Fact]
    public void FactSheetValidator_KeepsScoreExactlyAtThreshold_AndDropsJustAbove()
    {
        var result = FactSheetValidator.Validate(
            Output(Draft("At threshold.", ["s1"]), Draft("Just above.", ["s1"])),
            Context(threshold: 0.15m, sourceIds: [Source1]),
            request => request.Statement == "At threshold." ? 0.15m : 0.1501m);

        result.Facts.ShouldHaveSingleItem().Statement.ShouldBe("At threshold.");
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.VerbatimCopy);
    }

    [Fact]
    public void FactSheetValidator_KeepsZeroScore_AndDropsOneAndValuesOutsideTheUnitRange()
    {
        var result = FactSheetValidator.Validate(
            Output(
                Draft("Zero.", ["s1"]),
                Draft("One.", ["s1"]),
                Draft("Below.", ["s1"]),
                Draft("Above one.", ["s1"])),
            Context(sourceIds: [Source1]),
            request => request.Statement switch
            {
                "Zero." => 0m,
                "One." => 1m,
                "Below." => -0.0001m,
                _ => 1.0001m,
            });

        result.Facts.ShouldHaveSingleItem().Statement.ShouldBe("Zero.");
        result.Drops.Select(drop => drop.Reason).ShouldBe(
        [
            FactDropReasons.VerbatimCopy,
            FactDropReasons.VerbatimCopy,
            FactDropReasons.VerbatimCopy,
        ]);
    }

    [Fact]
    public void FactSheetValidator_KeepsStatementAt600Chars_AndDropsAt601()
    {
        var atLimit = new string('a', FactSheetValidator.StatementMaxChars);
        var overLimit = new string('a', FactSheetValidator.StatementMaxChars + 1);

        var result = FactSheetValidator.Validate(
            Output(Draft(atLimit, ["s1"]), Draft(overLimit, ["s1"])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldHaveSingleItem().Statement.Length.ShouldBe(FactSheetValidator.StatementMaxChars);
        result.Drops.ShouldHaveSingleItem().Reason.ShouldBe(FactDropReasons.StatementInvalid);
    }

    [Fact]
    public void FactSheetValidator_DropsUnitSeparatorAndLoneLowSurrogate_KeepsValidSurrogatePair()
    {
        var result = FactSheetValidator.Validate(
            Output(
                Draft("Unit\u001Fsep.", ["s1"]),
                Draft("Low \uDC00 fact.", ["s1"]),
                Draft("Emoji \uD83D\uDE00 fact.", ["s1"])),
            Context(sourceIds: [Source1]),
            Score(0m));

        result.Facts.ShouldHaveSingleItem().Statement.ShouldBe("Emoji \uD83D\uDE00 fact.");
        result.Drops.Select(drop => drop.Reason).ShouldBe(
        [
            FactDropReasons.ControlCharactersInvalid,
            FactDropReasons.StatementInvalid,
        ]);
    }
}
