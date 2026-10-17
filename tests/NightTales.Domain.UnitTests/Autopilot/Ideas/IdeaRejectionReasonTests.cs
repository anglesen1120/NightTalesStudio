using System.Reflection;
using NightTales.Domain.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The reason vocabulary of <c>episode_ideas.rejected_reason</c> (02.8.1 plan §3.1): one source of truth for
/// the ten stored values, each inside the column's width.
/// </summary>
public sealed class IdeaRejectionReasonTests
{
    [Fact]
    public void IdeaRejectionReason_AllReasons_FitTheColumn()
    {
        IdeaRejectionReason.MaxLength.ShouldBe(40);

        var tooLong = Reasons()
            .Where(reason => reason.Length > IdeaRejectionReason.MaxLength)
            .ToList();

        tooLong.ShouldBeEmpty($"rejected_reason is varchar({IdeaRejectionReason.MaxLength})");
    }

    [Fact]
    public void IdeaRejectionReason_AllReasons_AreDistinct()
    {
        var reasons = Reasons();

        reasons.ShouldBeUnique();
        reasons.Count.ShouldBe(10, "the plan §3.1 list");
    }

    [Fact]
    public void IdeaRejectionReason_AllReasons_AreSnakeCase()
    {
        Reasons().ShouldAllBe(reason => reason == reason.ToLowerInvariant() && !reason.Contains(' '));
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
