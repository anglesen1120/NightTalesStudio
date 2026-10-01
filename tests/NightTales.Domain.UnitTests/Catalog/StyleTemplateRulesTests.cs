using NightTales.Domain.Catalog;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Catalog;

/// <summary>
/// <c>StyleTemplateRules</c> (03.1.1-T2 Tests table, 03 README D1): each rule reports its own code, and
/// the rule order keeps a template that breaks two rules reporting the first one.
/// </summary>
public sealed class StyleTemplateRulesTests
{
    private const string ValidName = "dark-folklore-ink";

    private const string ValidTemplate = "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted grey tones.";

    [Fact]
    public void Rules_ValidTemplate_Ok()
    {
        var result = StyleTemplateRules.Validate(ValidName, ValidTemplate);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Rules_NoTerm_StyleTermCount()
    {
        var result = StyleTemplateRules.Validate(ValidName, new string('x', 80));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.TermCount);
    }

    [Fact]
    public void Rules_TwoTerms_StyleTermCount()
    {
        var result = StyleTemplateRules.Validate(ValidName, "{term} and {term} " + new string('x', 70));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.TermCount);
    }

    [Fact]
    public void Rules_59Chars_StyleLength()
    {
        var result = StyleTemplateRules.Validate(ValidName, TemplateOfLength(59));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.Length);
    }

    [Fact]
    public void Rules_601Chars_StyleLength()
    {
        var result = StyleTemplateRules.Validate(ValidName, TemplateOfLength(601));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.Length);
    }

    [Fact]
    public void Rules_LengthBoundaries_60And600_Ok()
    {
        var shortest = StyleTemplateRules.Validate(ValidName, TemplateOfLength(60));
        var longest = StyleTemplateRules.Validate(ValidName, TemplateOfLength(600));

        shortest.IsError.ShouldBeFalse();
        longest.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Rules_Newline_StyleLineBreak()
    {
        var result = StyleTemplateRules.Validate(ValidName, "{term}\n" + new string('x', 53));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.LineBreak);
    }

    [Fact]
    public void Rules_Quote_StyleQuote()
    {
        var result = StyleTemplateRules.Validate(ValidName, "{term}\"" + new string('x', 53));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.Quote);
    }

    [Theory]
    [InlineData("no gore")]
    [InlineData("NO GORE")]
    [InlineData("no text")]
    [InlineData("Watermark")]
    [InlineData(NegativeClause.V1)]
    public void Rules_ContainsNoGore_StyleHasNegativeClause(string clause)
    {
        var result = StyleTemplateRules.Validate(ValidName, Padding("{term} " + clause));

        result.FirstError.Code.ShouldBe(StyleTemplateRules.HasNegativeClause);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Dark-Folklore")]
    [InlineData("dark_folklore")]
    [InlineData("-dark")]
    [InlineData("dark-")]
    [InlineData("dark--folklore")]
    [InlineData("dark folklore")]
    [InlineData("dark.folklore")]
    [InlineData("dấrk-folklore")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Rules_BadSlug_StyleNameInvalid(string name)
    {
        var result = StyleTemplateRules.Validate(name, ValidTemplate);

        result.FirstError.Code.ShouldBe(StyleTemplateRules.NameInvalid);
    }

    [Fact]
    public void Rules_NameLengthBoundaries_3And40_Ok()
    {
        var shortest = StyleTemplateRules.Validate("abc", ValidTemplate);
        var longest = StyleTemplateRules.Validate(new string('a', 40), ValidTemplate);

        shortest.IsError.ShouldBeFalse();
        longest.IsError.ShouldBeFalse();
    }

    /// <summary>A template of exactly <paramref name="length"/> characters with one placeholder.</summary>
    private static string TemplateOfLength(int length) => "{term}" + new string('x', length - "{term}".Length);

    /// <summary>Grows a template that already holds one placeholder and no line break to at least 60 characters.</summary>
    private static string Padding(string template) =>
        template + new string('x', Math.Max(0, 60 - template.Length));
}
