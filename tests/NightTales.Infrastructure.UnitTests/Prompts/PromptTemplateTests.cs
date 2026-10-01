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

    /// <summary>An unterminated <c>{{</c> is reported rather than silently ignored.</summary>
    [Fact]
    public void PromptTemplate_UnterminatedPlaceholder_Throws()
    {
        var failure = Should.Throw<PromptCatalogException>(
            () => PromptTemplate.Substitute(
                "{{tier_minutes\n",
                new Dictionary<string, string>(StringComparer.Ordinal)));

        // The scan takes the name to the end of the text (the documented truncated case) and the message
        // wraps it in the usual delimiters, so the violation names the token the file actually carries.
        failure.Violations.ShouldHaveSingleItem()
            .ShouldBe("unknown placeholder '{{tier_minutes\n}}'");
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
