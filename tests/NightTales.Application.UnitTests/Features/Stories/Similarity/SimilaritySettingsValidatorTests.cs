using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Stories.Similarity;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Similarity;

public sealed class SimilaritySettingsValidatorTests
{
    private static readonly SimilaritySettingsValidator Validator = new(NullLogger<SimilaritySettingsValidator>.Instance);

    [Fact]
    public void SimilaritySettingsValidator_Defaults_AreValid() => Validator.Validate(null, new()).Succeeded.ShouldBeTrue();

    [Fact]
    public void SimilaritySettingsValidator_BlockNotAboveWarn_Fails()
    {
        var result = Validator.Validate(null, new() { BlockAbove = 0.05 });
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("Similarity:BlockAbove must be greater than Similarity:WarnAbove.");
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    [InlineData("other")]
    public void SimilaritySettingsValidator_NBelowTwo_Fails(string language)
    {
        var settings = new SimilaritySettings();
        settings.N[language] = 1;
        var result = Validator.Validate(null, settings);
        result.Failures.ShouldNotBeNull();
        result.Failures!.ShouldContain($"Similarity:N:{language} must be greater than or equal to 2.");
    }

    [Theory]
    [InlineData("WarnAbove", -0.01, "Similarity:WarnAbove")]
    [InlineData("WarnAbove", double.NaN, "Similarity:WarnAbove")]
    [InlineData("WarnAbove", double.PositiveInfinity, "Similarity:WarnAbove")]
    [InlineData("WarnAbove", double.NegativeInfinity, "Similarity:WarnAbove")]
    [InlineData("BlockAbove", 1.01, "Similarity:BlockAbove")]
    [InlineData("BlockAbove", double.NaN, "Similarity:BlockAbove")]
    [InlineData("BlockAbove", double.PositiveInfinity, "Similarity:BlockAbove")]
    [InlineData("BlockAbove", double.NegativeInfinity, "Similarity:BlockAbove")]
    public void SimilaritySettingsValidator_InvalidThreshold_Fails(string property, double value, string path)
    {
        var settings = new SimilaritySettings();
        if (property == "WarnAbove")
        {
            settings.WarnAbove = value;
        }
        else
        {
            settings.BlockAbove = value;
        }

        var result = Validator.Validate(null, settings);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.StartsWith(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Similarity:MinTokens")]
    [InlineData("Similarity:Corpus:MaxSourceTexts")]
    [InlineData("Similarity:Corpus:MaxSourceTextChars")]
    [InlineData("Similarity:Corpus:MaxPastEpisodes")]
    [InlineData("Similarity:Perf:BudgetMs")]
    public void SimilaritySettingsValidator_InvalidCount_Fails(string path)
    {
        var settings = new SimilaritySettings();
        switch (path)
        {
            case "Similarity:MinTokens": settings.MinTokens = 0; break;
            case "Similarity:Corpus:MaxSourceTexts": settings.Corpus.MaxSourceTexts = 0; break;
            case "Similarity:Corpus:MaxSourceTextChars": settings.Corpus.MaxSourceTextChars = 0; break;
            case "Similarity:Corpus:MaxPastEpisodes": settings.Corpus.MaxPastEpisodes = -1; break;
            case "Similarity:Perf:BudgetMs": settings.Perf.BudgetMs = 0; break;
        }

        var result = Validator.Validate(null, settings);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.StartsWith(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Similarity:Corpus:MaxSourceTexts", 50, false)]
    [InlineData("Similarity:Corpus:MaxSourceTexts", 50, true)]
    [InlineData("Similarity:Corpus:MaxSourceTextChars", 300000, false)]
    [InlineData("Similarity:Corpus:MaxSourceTextChars", 300000, true)]
    [InlineData("Similarity:Corpus:MaxPastEpisodes", 200, false)]
    [InlineData("Similarity:Corpus:MaxPastEpisodes", 200, true)]
    [InlineData("Similarity:Perf:BudgetMs", 60000, false)]
    [InlineData("Similarity:Perf:BudgetMs", 60000, true)]
    public void SimilaritySettingsValidator_UpperBound_RejectsOnlyAboveBound(string path, int bound, bool exceedsBound)
    {
        var settings = new SimilaritySettings();
        var value = bound + (exceedsBound ? 1 : 0);
        switch (path)
        {
            case "Similarity:Corpus:MaxSourceTexts": settings.Corpus.MaxSourceTexts = value; break;
            case "Similarity:Corpus:MaxSourceTextChars": settings.Corpus.MaxSourceTextChars = value; break;
            case "Similarity:Corpus:MaxPastEpisodes": settings.Corpus.MaxPastEpisodes = value; break;
            case "Similarity:Perf:BudgetMs": settings.Perf.BudgetMs = value; break;
        }

        var result = Validator.Validate(null, settings);
        result.Failed.ShouldBe(exceedsBound);
        result.Succeeded.ShouldBe(!exceedsBound);
        if (exceedsBound)
        {
            result.Failures.ShouldHaveSingleItem().ShouldBe($"{path} must be less than or equal to {bound}.");
        }
    }

    [Fact]
    public void SimilaritySettingsValidator_LowerBoundCountsAndWarnOnlyRollback_AreValid()
    {
        var settings = new SimilaritySettings { WarnAbove = 0, BlockAbove = 1, MinTokens = 1 };
        settings.N["vi"] = 2;
        settings.N["en"] = 2;
        settings.Corpus.MaxSourceTexts = 1;
        settings.Corpus.MaxSourceTextChars = 1;
        settings.Corpus.MaxPastEpisodes = 0;
        settings.Perf.BudgetMs = 1;
        Validator.Validate(null, settings).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void SimilarityOptions_For_MissingLanguageFallsBackToEnN()
    {
        var settings = new SimilaritySettings();
        settings.N.Remove("vi");
        settings.N["en"] = 12;
        SimilarityOptions.For(Language.Vi, settings).N.ShouldBe(12);
        settings.N.Clear();
        SimilarityOptions.For(Language.Vi, settings).N.ShouldBe(8);
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public void SimilaritySettingsValidator_MissingLanguage_WarnsWithoutFailure(string language)
    {
        var settings = new SimilaritySettings();
        settings.N.Remove(language);
        var logger = new RecordingLogger();
        new SimilaritySettingsValidator(logger).Validate(null, settings).Succeeded.ShouldBeTrue();
        logger.Warnings.ShouldHaveSingleItem().ShouldContain($"Similarity:N:{language}");
    }

    private sealed class RecordingLogger : ILogger<SimilaritySettingsValidator>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
