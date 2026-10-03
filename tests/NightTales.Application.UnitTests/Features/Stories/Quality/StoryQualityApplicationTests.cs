using System.Text.Json;
using NightTales.Application.Features.Stories.Quality;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Quality;

public sealed class StoryQualityApplicationTests
{
    private static StoryQualityOptions Valid() => new()
    {
        LengthTolerance = 0.15,
        MinHookTokens = 3,
        MaxHookSentences = 3,
        KeywordsPerSegment = [1, 2],
        KeywordsPerMinute = 6,
        RepeatedSentenceMinTokens = 12,
        MaxInternalShingleShare = 0.25,
        NameHeuristic = true,
    };

    [Fact]
    public void Options_Valid_HasNoProblems_AndMapsToDefaults()
    {
        StoryQualityOptionsValidator.Validate(Valid()).ShouldBeEmpty();
        Valid().ToThresholds().ShouldBe(StoryQualityThresholds.Defaults);
    }

    [Fact]
    public void StoryQualityOptions_InvalidKeywordRange_FailsStartupValidation()
    {
        var options = Valid();
        options.KeywordsPerSegment = [3, 1];

        StoryQualityOptionsValidator.Validate(options).ShouldHaveSingleItem().ShouldContain("KeywordsPerSegment");
        Should.Throw<InvalidOperationException>(() => options.ToThresholds());
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0.51)]
    public void Options_LengthToleranceOutOfRange_Fails(double value)
    {
        var options = Valid();
        options.LengthTolerance = value;

        StoryQualityOptionsValidator.Validate(options).ShouldHaveSingleItem().ShouldContain("LengthTolerance");
    }

    [Fact]
    public void Options_OtherBoundaries_Fail()
    {
        var options = Valid();
        options.KeywordsPerMinute = 0;
        options.RepeatedSentenceMinTokens = 3;
        options.MaxInternalShingleShare = 1;
        options.KeywordsPerSegment = [0, 6];

        StoryQualityOptionsValidator.Validate(options).Count.ShouldBe(4);
    }

    [Fact]
    public void StoryQualityOptions_MissingBlock_FailsStartupValidation()
    {
        var problems = StoryQualityOptionsValidator.Validate(new StoryQualityOptions());

        problems.Count.ShouldBe(8);
        problems.ShouldAllBe(problem => problem.Contains("is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_Projections_KeepOrderAndPutObjectiveHintsFirst()
    {
        var taste = new QualityIssue("Q-NAME-01", QualitySeverity.Advisory, QualityKind.Taste, "p:0", "quality.possible_real_name", "x", null, null, null);
        var objective = new QualityIssue("Q-REP-01", QualitySeverity.Blocking, QualityKind.Objective, "p:1", "quality.repeated_sentence", "y", "fix", 3, 2);
        var report = new QualityReport([taste, objective], new QualityCounters(0, 0, 0, 0, null, null));

        report.ToAuditIssues().Select(issue => issue.Rule).ShouldBe(["Q-NAME-01", "Q-REP-01"]);
        report.ToAuditIssues()[1].Severity.ShouldBe("blocking");
        report.ToAuditIssues()[0].Kind.ShouldBe("taste");
        var hints = report.ToHints();
        hints.Select(hint => hint.RuleId).ShouldBe(["Q-REP-01", "Q-NAME-01"]);
        hints[0].MessageKey.ShouldBe("quality.repeated_sentence");
        hints[0].Measured.ShouldBe(3);
        hints[0].Threshold.ShouldBe(2);
    }

    [Fact]
    public void QualityHints_AllMessageKeysExistInBothLanguages()
    {
        var directory = FindI18nDirectory();
        foreach (var language in new[] { "vi", "en" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, language + ".json")));
            var keys = document.RootElement.EnumerateObject().Select(property => "quality." + property.Name).ToHashSet(StringComparer.Ordinal);

            StoryQualityChecker.MessageKeys.Values.ShouldAllBe(key => keys.Contains(key), language);
            keys.Count.ShouldBe(StoryQualityChecker.MessageKeys.Count, language);
            document.RootElement.EnumerateObject().ShouldAllBe(property => !string.IsNullOrWhiteSpace(property.Value.GetString()));
        }
    }

    private static string FindI18nDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "web", "src", "assets", "i18n", "quality");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("web/src/assets/i18n/quality not found above " + AppContext.BaseDirectory);
    }
}
