using NightTales.Application.Abstractions.Prompts;
using NightTales.Infrastructure.Prompts;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Prompts;

/// <summary>
/// The placeholder scan and substitution of one template (02.2.3-T1, 02 README D5 rule 6 / D6): the
/// closed list is the whole contract, so a token outside it is a violation and never reaches a model.
/// </summary>
public sealed class PromptTemplateTests
{
    [Fact]
    public void PromptTemplate_SubstitutesAllClosedPlaceholders()
    {
        PromptPlaceholders.All.Count.ShouldBe(8);

        var template =
            "L={{language_name}} T={{tier_minutes}} K={{token_range}} S={{segment_range}} "
            + "R={{keyword_rules}} E={{ear_rules}} O={{overused_phrases}} V={{structure_vocab}}";

        var values = PromptPlaceholders.All.ToDictionary(
            name => name,
            name => $"[{name}]",
            StringComparer.Ordinal);

        var substituted = PromptTemplate.Substitute(template, values);

        substituted.ShouldBe(
            "L=[language_name] T=[tier_minutes] K=[token_range] S=[segment_range] "
            + "R=[keyword_rules] E=[ear_rules] O=[overused_phrases] V=[structure_vocab]");
        substituted.ShouldNotContain("{{");
        PromptTemplate.UnknownPlaceholders(substituted).ShouldBeEmpty();
    }

    [Fact]
    public void PromptTemplate_UnknownPlaceholder_Throws()
    {
        var failure = Should.Throw<PromptCatalogException>(
            () => PromptTemplate.Substitute(
                "Write it in {{tier}} minutes.\n{{language_name}}\n",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PromptPlaceholders.LanguageName] = "Vietnamese",
                }));

        failure.Message.ShouldContain("unknown placeholder '{{tier}}'");
    }

    /// <summary>
    /// Every malformed token of one substitution is reported together, as at start-up, instead of one per
    /// call.
    /// </summary>
    [Fact]
    public void PromptTemplate_MultipleUnknownPlaceholders_AllReported()
    {
        var failure = Should.Throw<PromptCatalogException>(
            () => PromptTemplate.Substitute(
                "{{bad_one}} {{bad_two}}",
                new Dictionary<string, string>(StringComparer.Ordinal)));

        failure.Violations.ShouldBe(
            new List<string> { "unknown placeholder '{{bad_one}}'", "unknown placeholder '{{bad_two}}'" });
    }

    /// <summary>
    /// A malformed token is named before a known token the caller supplied no value for: the template's
    /// own contract is reported first, and the missing value never hides it.
    /// </summary>
    [Fact]
    public void PromptTemplate_UnknownToken_IsReportedBeforeMissingValue()
    {
        var failure = Should.Throw<PromptCatalogException>(
            () => PromptTemplate.Substitute(
                "{{ear_rules}} {{bad_one}}",
                new Dictionary<string, string>(StringComparer.Ordinal)));

        failure.Violations.ShouldBe(new List<string> { "unknown placeholder '{{bad_one}}'" });
    }

    /// <summary>
    /// A closed placeholder the caller supplied no value for is a violation too: leaving
    /// <c>{{ear_rules}}</c> in the text would send it to the model as literal content.
    /// </summary>
    [Fact]
    public void PromptTemplate_KnownPlaceholderWithoutValue_Throws()
    {
        var failure = Should.Throw<PromptCatalogException>(
            () => PromptTemplate.Substitute(
                "{{ear_rules}}\n",
                new Dictionary<string, string>(StringComparer.Ordinal)));

        failure.Message.ShouldContain("no value for placeholder '{{ear_rules}}'");
    }

    /// <summary>
    /// An unterminated token is rejected whichever name it carries, even one from the allow-list: the
    /// literal <c>{{ear_rules</c> would otherwise reach the model unchanged.
    /// </summary>
    [Fact]
    public void PromptTemplate_UnterminatedPlaceholder_Throws()
    {
        var failure = Should.Throw<PromptCatalogException>(
            () => PromptTemplate.Substitute(
                "{{ear_rules",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [PromptPlaceholders.EarRules] = "[ear rules]",
                }));

        failure.Violations.ShouldHaveSingleItem().ShouldBe("unterminated placeholder '{{ear_rules'");
    }

    /// <summary>An unterminated token is not also read as an unknown name: the scan stops at it.</summary>
    [Fact]
    public void PromptTemplate_UnterminatedToken_IsNotAnUnknownName()
    {
        PromptTemplate.UnknownPlaceholders("{{tier}} {{tier_minutes").ShouldBe(new List<string> { "tier" });
        PromptTemplate.UnterminatedPlaceholders("{{tier}} {{tier_minutes")
            .ShouldBe(new List<string> { "tier_minutes" });
    }

    [Fact]
    public void PromptTemplate_UnknownPlaceholders_ReportsEachNameOnce()
    {
        var unknown = PromptTemplate.UnknownPlaceholders("{{tier}} {{tier}} {{language_name}} {{loudness}}");

        unknown.ShouldBe(new List<string> { "tier", "loudness" });
    }

    [Fact]
    public void PromptTemplate_NoPlaceholders_IsReturnedUnchanged()
    {
        var text = "A body with no placeholder at all.\n";

        PromptTemplate.UnknownPlaceholders(text).ShouldBeEmpty();
        PromptTemplate.Substitute(text, new Dictionary<string, string>(StringComparer.Ordinal)).ShouldBe(text);
    }
}
