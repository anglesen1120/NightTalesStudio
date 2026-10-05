using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Stories.Similarity;

namespace NightTales.Application.Features.Stories.Similarity;

/// <summary>Validates similarity bounds at host startup and warns about language fallback.</summary>
public sealed partial class SimilaritySettingsValidator(ILogger<SimilaritySettingsValidator> logger)
    : IValidateOptions<SimilaritySettings>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SimilaritySettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (!double.IsFinite(options.WarnAbove) || options.WarnAbove < 0)
        {
            failures.Add("Similarity:WarnAbove must be a finite number greater than or equal to 0.");
        }

        if (!double.IsFinite(options.BlockAbove) || options.BlockAbove > 1)
        {
            failures.Add("Similarity:BlockAbove must be a finite number less than or equal to 1.");
        }

        if (options.BlockAbove <= options.WarnAbove)
        {
            failures.Add("Similarity:BlockAbove must be greater than Similarity:WarnAbove.");
        }

        foreach (var (language, n) in options.N)
        {
            if (n < 2)
            {
                failures.Add($"Similarity:N:{language} must be greater than or equal to 2.");
            }
        }

        if (options.MinTokens < 1)
        {
            failures.Add("Similarity:MinTokens must be greater than or equal to 1.");
        }

        if (options.Corpus.MaxSourceTexts < 1)
        {
            failures.Add("Similarity:Corpus:MaxSourceTexts must be greater than or equal to 1.");
        }

        if (options.Corpus.MaxPastEpisodes < 0)
        {
            failures.Add("Similarity:Corpus:MaxPastEpisodes must be greater than or equal to 0.");
        }

        if (options.Corpus.MaxSourceTextChars < 1)
        {
            failures.Add("Similarity:Corpus:MaxSourceTextChars must be greater than or equal to 1.");
        }

        if (options.Perf.BudgetMs < 1)
        {
            failures.Add("Similarity:Perf:BudgetMs must be greater than or equal to 1.");
        }

        if (!options.N.ContainsKey("vi"))
        {
            MissingLanguage("Similarity:N:vi");
        }

        if (!options.N.ContainsKey("en"))
        {
            MissingLanguage("Similarity:N:en");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [LoggerMessage(EventId = 2600, Level = LogLevel.Warning,
        Message = "Missing {ConfigurationPath}; similarity uses the en value or the default English N.")]
    private partial void MissingLanguage(string configurationPath);
}
