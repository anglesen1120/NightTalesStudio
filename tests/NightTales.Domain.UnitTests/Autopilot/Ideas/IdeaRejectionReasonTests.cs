using System.Reflection;
using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The <c>rejected_reason</c> vocabulary (02.8.1 plan §3.1): exactly ten snake_case constants, each inside
/// the <c>varchar(40)</c> column and each unique, so the run log can group rejections by reason and the
/// EF configuration can take the column width from <see cref="IdeaRejectionReason.MaxLength"/>.
/// </summary>
public sealed class IdeaRejectionReasonTests
{
    [Fact]
    public void IdeaRejectionReason_AllReasons_FitTheColumn()
    {
        var reasons = Reasons();

        reasons.Count.ShouldBe(10, "the plan §3.1 list");
        IdeaRejectionReason.MaxLength.ShouldBe(40, "the rejected_reason varchar(40)");
        reasons.ShouldAllBe(reason => reason.Length <= IdeaRejectionReason.MaxLength);
    }

    [Fact]
    public void IdeaRejectionReason_AllReasons_AreUnique() =>
        Reasons().ShouldBeUnique();

    [Fact]
    public void IdeaRejectionReason_AllReasons_AreSnakeCase()
    {
        // The stored vocabulary is lowercase ASCII with underscores only — no uppercase, digits or spaces.
        Reasons().ShouldAllBe(reason => reason.All(character => (character >= 'a' && character <= 'z') || character == '_'));
    }

    [Theory]
    [InlineData("not_best", IdeaRejectionReason.NotBest)]
    [InlineData("novelty_lexical", IdeaRejectionReason.NoveltyLexical)]
    [InlineData("novelty_judge", IdeaRejectionReason.NoveltyJudge)]
    [InlineData("policy_banned_theme", IdeaRejectionReason.PolicyBannedTheme)]
    [InlineData("policy_moderated", IdeaRejectionReason.PolicyModerated)]
    [InlineData("variety_triple_repeat", IdeaRejectionReason.VarietyTripleRepeat)]
    [InlineData("variety_template_repeat", IdeaRejectionReason.VarietyTemplateRepeat)]
    [InlineData("variety_tag_cap", IdeaRejectionReason.VarietyTagCap)]
    [InlineData("invalid", IdeaRejectionReason.Invalid)]
    [InlineData("duplicate", IdeaRejectionReason.Duplicate)]
    public void IdeaRejectionReason_StoredValue_IsThePlanValue(string expected, string actual) =>
        actual.ShouldBe(expected);

    /// <summary>Every public <c>string</c> constant of the class, which is the stored vocabulary.</summary>
    private static IReadOnlyList<string> Reasons() =>
        [.. typeof(IdeaRejectionReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];
}
