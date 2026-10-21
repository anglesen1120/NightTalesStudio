namespace NightTales.Application.Features.Stories.Quality;

/// <summary>
/// Checks <see cref="StoryQualityOptions"/> so a bad or missing <c>Story:Quality</c> block stops the start-up and
/// never a check. Application cannot reference <c>IValidateOptions</c> (Architecture test), so Infrastructure adapts
/// this to <c>IValidateOptions</c> exactly as it does for <c>ImagesOptionsValidator</c>.
/// </summary>
public static class StoryQualityOptionsValidator
{
    /// <summary>Every problem of the section; empty when valid.</summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Validate(StoryQualityOptions options) => StoryQualityOptions.Validate(options);
}
