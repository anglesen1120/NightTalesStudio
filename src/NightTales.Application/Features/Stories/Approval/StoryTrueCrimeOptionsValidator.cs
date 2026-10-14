namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// Checks <c>Story:TrueCrime</c> (02.3.3-T1) so an invalid attestation version or cited-source floor
/// stops the start-up and never an approval.
/// </summary>
/// <remarks>
/// Application cannot reference <c>IValidateOptions</c> (Architecture test), so Infrastructure adapts
/// this to <c>IValidateOptions</c> exactly as it does for <c>StoryQualityOptionsValidator</c>. Both keys
/// carry a default, so the block itself is optional and only a present-but-invalid value fails the boot.
/// </remarks>
public static class StoryTrueCrimeOptionsValidator
{
    /// <summary>The longest accepted attestation version marker; the shipped one is <c>ta.v1</c>.</summary>
    public const int MaxAttestationVersionLength = 32;

    /// <summary>The smallest accepted <see cref="StoryTrueCrimeOptions.MinCitedSources"/>: one source can never qualify under F13.</summary>
    public const int MinCitedSourcesFloor = 1;

    /// <summary>
    /// The largest accepted <see cref="StoryTrueCrimeOptions.MinCitedSources"/>: the per-episode url
    /// source cap of 02.5.1, above which no sheet could ever cite enough sources.
    /// </summary>
    public const int MinCitedSourcesCeiling = 10;

    /// <summary>Every problem of the section; empty when valid.</summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem, naming the key and never echoing a rejected value.</returns>
    public static IReadOnlyList<string> Validate(StoryTrueCrimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var version = options.AttestationVersion;
        var versionKey = $"'{StoryTrueCrimeOptions.SectionName}:{nameof(StoryTrueCrimeOptions.AttestationVersion)}'";

        if (string.IsNullOrWhiteSpace(version))
        {
            problems.Add($"{versionKey} must not be empty; it names the text version an approval tick is compared to.");
        }
        else
        {
            if (!string.Equals(version, version.Trim(), StringComparison.Ordinal))
            {
                problems.Add($"{versionKey} must not carry surrounding whitespace.");
            }

            if (version.Length > MaxAttestationVersionLength)
            {
                problems.Add($"{versionKey} must be at most {MaxAttestationVersionLength} characters.");
            }

            // A control character would make the stored version unreadable and is never part of a marker.
            if (version.Any(char.IsControl))
            {
                problems.Add($"{versionKey} must not contain control characters.");
            }
        }

        if (options.MinCitedSources is < MinCitedSourcesFloor or > MinCitedSourcesCeiling)
        {
            problems.Add(
                $"'{StoryTrueCrimeOptions.SectionName}:{nameof(StoryTrueCrimeOptions.MinCitedSources)}' must be between "
                + $"{MinCitedSourcesFloor} and {MinCitedSourcesCeiling}.");
        }

        return problems;
    }
}
