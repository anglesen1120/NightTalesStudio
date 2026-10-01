using NightTales.Domain.Catalog;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Catalog;

/// <summary>
/// <c>ScenePromptBuilder</c> (03.1.1-T2 Acceptance checks, 03 README D1): the clause is appended
/// exactly once after the styled text, the moderation text never carries it, and the SHA-256 is the
/// stable attribution key.
/// </summary>
public sealed class ScenePromptBuilderTests
{
    /// <summary>A rule-valid template (03.1.1-T2), so <see cref="StyleTemplate.Create"/> accepts it.</summary>
    private const string Template = "Dark folklore ink illustration of {term}. Black ink wash on aged rice paper, muted grey tones.";

    private const string Keyword = "empty rocking chair by a window at dusk";

    /// <summary>Computed once with <c>printf '%s' "$text" | shasum -a 256</c>; the relay's key (D5).</summary>
    private const string KnownVector = "1ad025bcf76f249e59803f85d27b45d977526cae85e7f1dcfc77f5ecc00a0c1c";

    [Fact]
    public void Build_AppendsNegativeClauseAfterStyledText()
    {
        var style = CreateStyle();

        var prompt = ScenePromptBuilder.Build(style, Keyword);

        var styled = Template.Replace("{term}", Keyword, StringComparison.Ordinal);
        prompt.Text.ShouldBe(styled + " " + NegativeClause.V1);
        prompt.Text.ShouldEndWith(" " + NegativeClause.Current);
        prompt.ModerationText.ShouldBe(styled);
        prompt.ModerationText.ShouldNotContain(NegativeClause.Current);
        prompt.ModerationText.ShouldNotContain("no gore");
        prompt.Sha256.ShouldBe(ScenePromptBuilder.Sha256Hex(prompt.Text));
    }

    [Fact]
    public void Build_TrimsKeyword()
    {
        var style = CreateStyle();

        var prompt = ScenePromptBuilder.Build(style, "  ghost  ");

        prompt.ModerationText.ShouldBe(Template.Replace("{term}", "ghost", StringComparison.Ordinal));
        prompt.Text.ShouldStartWith("Dark folklore ink illustration of ghost. Black ink wash");
    }

    [Fact]
    public void Build_KeywordWithCommas_KeptVerbatim()
    {
        var style = CreateStyle();

        var prompt = ScenePromptBuilder.Build(style, "a chair, a window, a lamp");

        prompt.ModerationText.ShouldContain("a chair, a window, a lamp");
    }

    [Fact]
    public void Build_KeywordContainingPlaceholder_KeepsLiteral()
    {
        // Edge case of 03.1.1-T2 §Failure handling: a single Replace pass means a keyword that
        // carries the literal placeholder keeps it in the styled text (harmless — the keyword
        // charset of 03.2.2 rejects braces before this builder ever sees one).
        var prompt = ScenePromptBuilder.Build(CreateStyle(), "a {term} b");

        prompt.ModerationText.ShouldBe(Template.Replace("{term}", "a {term} b", StringComparison.Ordinal));
        prompt.ModerationText.ShouldContain("{term}");
        prompt.Text.ShouldEndWith(" " + NegativeClause.Current);
    }

    [Fact]
    public void Build_Sha256MatchesKnownVector()
    {
        var style = CreateStyle();

        var prompt = ScenePromptBuilder.Build(style, Keyword);

        prompt.Sha256.ShouldBe(KnownVector);
        prompt.Sha256.ShouldBe(prompt.Sha256.ToLowerInvariant(), "the relay's attribution key is lower-case hex");
    }

    [Fact]
    public void Build_SameInputs_SameHash()
    {
        // Two independently created rows: the hash is a function of the template text, not of the row id.
        var first = ScenePromptBuilder.Build(CreateStyle(), Keyword);
        var second = ScenePromptBuilder.Build(CreateStyle(), Keyword);

        second.Text.ShouldBe(first.Text);
        second.Sha256.ShouldBe(first.Sha256);
    }

    private static StyleTemplate CreateStyle() => StyleTemplate.Create("dark-folklore-ink", Template);
}
