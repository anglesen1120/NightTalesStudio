using NightTales.Application.Features.Research.Facts;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research.Facts;

/// <summary>
/// 02.5.3-T1 QA: invariants of the fact input budget that the implementer tests do not pin directly — the
/// estimate equals the bytes actually sent, a within-budget plan never exceeds the token ceiling, the exact
/// per-source cap boundary, the one-source ceiling, and sanitisation of the serialised title.
/// </summary>
public sealed class FactInputBudgetQaTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Marker = "\n[…truncated…]\n";

    private static FactInputLimits Limits(int maxSources = 10, int perSourceCharCap = 20_000, int maxInputTokens = 60_000) =>
        new(maxSources, perSourceCharCap, maxInputTokens);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    private static FactSourceCandidate Url(int n, string text, int ageMinutes = 0) =>
        new(Id(n), $"Title {n}", FactSourceKind.Url, text, BaseTime.AddMinutes(-ageMinutes));

    private static FactSourceCandidate Note(int n, string text, int ageMinutes = 0) =>
        new(Id(n), $"Note {n}", FactSourceKind.UserNote, text, BaseTime.AddMinutes(-ageMinutes));

    [Fact]
    public void QA_EstimatedTokens_EqualsTheEstimateOfTheSerialisedPlan()
    {
        // A truncated URL, a user note, a short URL and a multi-byte URL: the estimate must be taken from the
        // exact bytes the plan will send, not from the raw candidate texts.
        var candidates = new[]
        {
            Url(1, new string('a', 25_000), 5),
            Note(2, "my note " + new string('b', 2_000), 4),
            Url(3, "short", 3),
            Url(4, "\u00e9t\u00e9 \U0001F600 text", 2),
        };

        var plan = FactInputBudget.Plan(Language.Vi, candidates, Limits());

        var json = FactDataObjectJson.Serialize(Language.Vi, plan.Sources);
        plan.EstimatedTokens.ShouldBe(FactInputBudget.EstimateTokens(json));
        plan.TruncatedAliases.ShouldBe(["s1"]);
    }

    [Theory]
    [InlineData(60_000)]
    [InlineData(5_000)]
    [InlineData(1_500)]
    public void QA_WhenWithinBudget_TheSerialisedEstimateNeverExceedsMaxInputTokens(int maxInputTokens)
    {
        var candidates = Enumerable.Range(1, 10)
            .Select(n => Url(n, new string((char)('a' + n), 20_000 + (n * 500)), n))
            .ToArray();

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(maxInputTokens: maxInputTokens));

        var json = FactDataObjectJson.Serialize(Language.En, plan.Sources);
        if (plan.WithinBudget)
        {
            FactInputBudget.EstimateTokens(json).ShouldBeLessThanOrEqualTo(maxInputTokens);
        }
        else
        {
            plan.EstimatedTokens.ShouldBeGreaterThan(maxInputTokens);
        }
    }

    [Fact]
    public void QA_TenFullSizeSources_AtDefaultLimits_AreWithinBudgetAndUntruncated()
    {
        // Plan §10 re-check: 10 × 20,000 characters of ASCII is about 50K estimated tokens, under the 60K default.
        var candidates = Enumerable.Range(1, 10)
            .Select(n => Url(n, new string('x', 20_000), n))
            .ToArray();

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits());

        plan.WithinBudget.ShouldBeTrue();
        plan.TruncatedAliases.ShouldBeEmpty();
        plan.Sources.Count.ShouldBe(10);
    }

    [Fact]
    public void QA_SourceExactlyAtTheCharCap_IsNotTruncated()
    {
        var plan = FactInputBudget.Plan(Language.En, [Url(1, new string('x', 20_000))], Limits());

        plan.TruncatedAliases.ShouldBeEmpty();
        plan.Sources[0].Text.Length.ShouldBe(20_000);
    }

    [Fact]
    public void QA_SourceOneCharOverTheCharCap_IsTruncatedHeadAndTail()
    {
        var text = new string('h', 10_000) + new string('t', 10_001); // 20,001 characters

        var plan = FactInputBudget.Plan(Language.En, [Url(1, text)], Limits());

        plan.TruncatedAliases.ShouldBe(["s1"]);
        var cut = plan.Sources[0].Text;
        cut.ShouldContain(Marker);
        cut.StartsWith('h').ShouldBeTrue();
        cut.EndsWith('t').ShouldBeTrue();
        cut.Length.ShouldBeLessThanOrEqualTo(20_000 + Marker.Length);
    }

    [Fact]
    public void QA_MaxSourcesOne_KeepsTheOldestAsS1_AndExcludesTheRest()
    {
        // Sources are ordered by created_at ascending, so the oldest (candidate 3) takes s1.
        var candidates = new[] { Url(1, "a", 1), Url(2, "b", 2), Url(3, "c", 3) };

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(maxSources: 1));

        plan.Sources.Select(source => source.Alias).ShouldBe(["s1"]);
        plan.Sources[0].SourceId.ShouldBe(Id(3));
        plan.ExcludedSourceIds.ShouldBe([Id(2), Id(1)], ignoreOrder: true);
    }

    [Fact]
    public void QA_LiteralTruncationMarkerInsideShortText_IsNotReportedAsTruncated()
    {
        const string text = "[…truncated…] keep this exact text";

        var plan = FactInputBudget.Plan(Language.En, [Url(1, text)], Limits());

        plan.TruncatedAliases.ShouldBeEmpty();
        plan.Sources[0].Text.ShouldBe(text);
    }

    [Fact]
    public void QA_ControlCharactersInTheTitle_AreSanitisedInTheSerialisedObject()
    {
        var source = new FactSourceInput("s1", Id(1), "bad\u0000title\u0007", "text", false);

        var json = FactDataObjectJson.Serialize(Language.En, [source]);

        json.ShouldNotContain("\\u0000");
        json.ShouldNotContain("\\u0007");
        json.ShouldContain("bad title");
    }

    [Fact]
    public void QA_TinyBudgetWithOnlyUrls_ReportsOverBudget_AndDoesNotThrow()
    {
        // A one-token budget can never hold ten sources even at the 1,000-character floor.
        var candidates = Enumerable.Range(1, 3).Select(n => Url(n, new string('z', 20_000), n)).ToArray();

        var plan = FactInputBudget.Plan(Language.En, candidates, Limits(maxInputTokens: 1));

        plan.WithinBudget.ShouldBeFalse();
        plan.Status.ShouldBe(FactInputBudgetStatus.OverBudget);
    }
}
