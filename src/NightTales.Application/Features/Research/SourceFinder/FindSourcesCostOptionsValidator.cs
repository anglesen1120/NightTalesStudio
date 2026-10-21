namespace NightTales.Application.Features.Research.SourceFinder;

/// <summary>
/// Checks <see cref="FindSourcesCostOptions"/> so a bad or missing <c>SourceFinder</c> block stops the start-up and
/// never a request. Application cannot reference <c>IValidateOptions</c> (Architecture test), so Infrastructure
/// adapts this to <c>IValidateOptions</c> exactly as it does for <c>StoryQualityOptionsValidator</c>.
/// </summary>
/// <remarks>
/// The bounds exist because the estimator turns them into money: an unbounded tool-call cap multiplies the
/// per-call fee, and an unbounded input ceiling reserves far above any real call. The model must be named, or the
/// estimator silently prices the default model.
/// </remarks>
public static class FindSourcesCostOptionsValidator
{
    /// <summary>Every problem of the section; empty when valid.</summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Validate(FindSourcesCostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            problems.Add($"'{FindSourcesCostOptions.SectionName}:Model' must name the finder model to price.");
        }

        if (options.MaxSearchCalls < 1 || options.MaxSearchCalls > FindSourcesCostOptions.MaxMaxSearchCalls)
        {
            problems.Add(
                $"'{FindSourcesCostOptions.SectionName}:{nameof(FindSourcesCostOptions.MaxSearchCalls)}' must be 1..{FindSourcesCostOptions.MaxMaxSearchCalls}.");
        }

        if (options.MaxInputBytes < 1 || options.MaxInputBytes > FindSourcesCostOptions.MaxMaxInputBytes)
        {
            problems.Add(
                $"'{FindSourcesCostOptions.SectionName}:{nameof(FindSourcesCostOptions.MaxInputBytes)}' must be 1..{FindSourcesCostOptions.MaxMaxInputBytes}.");
        }

        return problems;
    }
}
