using NightTales.Domain.Series.Bible;
using NightTales.Infrastructure.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Bible;

/// <summary>
/// The <c>Bible</c> section of 02.6.3-T1: the numbers a host configures are the numbers the Domain enforces, and
/// a zero limit fails the boot instead of answering every save with 400.
/// </summary>
/// <remarks>
/// The section is bound from configuration; the defaults are the plan's values (plan §3.2 table), so a host whose
/// section is missing or partial still enforces the plan. The proposal and prompt numbers are 02.6.3-T2's, and
/// they are validated here because they are the same section and the same boot.
/// </remarks>
public sealed class BibleOptionsTests
{
    private static readonly BibleOptionsValidator Validator = new();

    [Fact]
    public void BibleOptions_SectionName_IsBible() => BibleOptions.SectionName.ShouldBe("Bible");

    [Fact]
    public void BibleOptions_Defaults_AreThePlanNumbers()
    {
        var options = new BibleOptions();
        var limits = options.Limits;

        limits.NarratorNameChars.ShouldBe(80);
        limits.NarratorPersonaChars.ShouldBe(1000);
        limits.VoiceNotesChars.ShouldBe(1000);
        limits.MaxCharacters.ShouldBe(30);
        limits.MaxPlaces.ShouldBe(30);
        limits.MaxRules.ShouldBe(30);
        limits.NameChars.ShouldBe(60);
        limits.DescriptionChars.ShouldBe(300);
        limits.RuleChars.ShouldBe(300);
        limits.SummaryTitleChars.ShouldBe(100);
        limits.SummaryChars.ShouldBe(200);
        limits.MaxContentBytes.ShouldBe(65_536);

        options.Proposal.Enabled.ShouldBeTrue();
        options.Proposal.MaxOps.ShouldBe(10);
        options.Proposal.MaxCharacterOps.ShouldBe(3);
        options.Proposal.MaxPlaceOps.ShouldBe(3);
        options.Proposal.MaxRuleOps.ShouldBe(2);
        options.Proposal.MaxStoryChars.ShouldBe(24_000);

        options.Prompt.MaxSummaries.ShouldBe(50);
        options.Prompt.MaxChars.ShouldBe(24_000);
    }

    [Fact]
    public void BibleOptions_Defaults_PassValidation() =>
        Validator.Validate(null, new BibleOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void BibleOptions_ZeroLimit_Fails()
    {
        var result = Validator.Validate(null, new BibleOptions { Limits = new BibleLimits { MaxContentBytes = 0 } });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Bible:Limits:MaxContentBytes", Case.Sensitive);
    }

    [Fact]
    public void BibleOptions_EveryLimitZero_NamesEachKey()
    {
        var result = Validator.Validate(
            null,
            new BibleOptions
            {
                Limits = new BibleLimits
                {
                    NarratorNameChars = 0,
                    NarratorPersonaChars = 0,
                    VoiceNotesChars = 0,
                    MaxCharacters = 0,
                    MaxPlaces = 0,
                    MaxRules = 0,
                    NameChars = 0,
                    DescriptionChars = 0,
                    RuleChars = 0,
                    SummaryTitleChars = 0,
                    SummaryChars = 0,
                    MaxContentBytes = 0,
                },
            });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Bible:Limits:NarratorNameChars", Case.Sensitive);
        result.FailureMessage.ShouldContain("Bible:Limits:MaxContentBytes", Case.Sensitive);
        result.Failures!.Count().ShouldBe(12);
    }

    [Fact]
    public void BibleOptions_ZeroJobCaps_Fail()
    {
        var result = Validator.Validate(
            null,
            new BibleOptions
            {
                Proposal = new BibleProposalOptions { MaxOps = 0, MaxStoryChars = 0 },
                Prompt = new BiblePromptOptions { MaxSummaries = 0, MaxChars = 0 },
            });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Bible:Proposal:MaxStoryChars", Case.Sensitive);
        result.FailureMessage.ShouldContain("Bible:Prompt:MaxSummaries", Case.Sensitive);
    }

    [Fact]
    public void BibleOptions_PerKindOpCapZero_Succeeds()
    {
        // Zero disables one op kind without switching the job off, so it is a legal configuration.
        var result = Validator.Validate(
            null,
            new BibleOptions { Proposal = new BibleProposalOptions { MaxCharacterOps = 0, MaxPlaceOps = 0, MaxRuleOps = 0 } });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void BibleOptions_NegativePerKindOpCap_Fails()
    {
        var result = Validator.Validate(null, new BibleOptions { Proposal = new BibleProposalOptions { MaxRuleOps = -1 } });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Bible:Proposal:MaxRuleOps", Case.Sensitive);
    }
}
