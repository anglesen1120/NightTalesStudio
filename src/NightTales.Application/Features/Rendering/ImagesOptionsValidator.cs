namespace NightTales.Application.Features.Rendering;

/// <summary>Checks <see cref="ImagesOptions"/> so a bad <c>Images</c> section stops the start-up, not a render.</summary>
public static class ImagesOptionsValidator
{
    /// <summary>
    /// <c>ClipSeconds</c> 5..20, <c>SpareShare</c> 0..0.5, <c>SparseShare</c> 0.3..1, <c>MaxSkippedShare</c> 0..1,
    /// a non-empty <c>Model</c>, <c>Quality</c> unset or one of <c>low|medium|high|auto</c>, and a placeholder of at
    /// least 6,000 tokens at rates no lower than the O8 plan placeholder (30 USD/1M image out, 5 USD/1M text in).
    /// </summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(ImagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.ClipSeconds is < 5 or > 20)
        {
            problems.Add($"{nameof(options.ClipSeconds)} must be between 5 and 20.");
        }

        if (!(options.SpareShare is >= 0 and <= 0.5))
        {
            problems.Add($"{nameof(options.SpareShare)} must be between 0 and 0.5.");
        }

        if (!(options.SparseShare is >= 0.3 and <= 1))
        {
            problems.Add($"{nameof(options.SparseShare)} must be between 0.3 and 1.");
        }

        if (!(options.MaxSkippedShare is >= 0 and <= 1))
        {
            problems.Add($"{nameof(options.MaxSkippedShare)} must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            problems.Add($"{nameof(options.Model)} must not be empty.");
        }

        if (!string.IsNullOrWhiteSpace(options.Quality)
            && !ImagesOptions.AllowedQualities.Contains(options.Quality, StringComparer.Ordinal))
        {
            problems.Add($"{nameof(options.Quality)} must be unset or one of {string.Join(", ", ImagesOptions.AllowedQualities)}.");
        }

        if (options.PlaceholderTokensPerImage < 6_000)
        {
            problems.Add($"{nameof(options.PlaceholderTokensPerImage)} must be at least 6000.");
        }

        if (options.PlaceholderImageOutPer1MUsd < 30m)
        {
            problems.Add($"{nameof(options.PlaceholderImageOutPer1MUsd)} must be at least 30.");
        }

        if (options.PlaceholderTextInPer1MUsd < 5m)
        {
            problems.Add($"{nameof(options.PlaceholderTextInPer1MUsd)} must be at least 5.");
        }

        return problems;
    }
}
