namespace NightTales.Application.Features.Research.Sources;

/// <summary>
/// The <c>Sources</c> section (02.5.1 plan §3.3): the volume caps of the add/retry commands. A plain object so the
/// Application layer needs no options package; Infrastructure binds, validates and registers it.
/// </summary>
public sealed class SourcesOptions
{
    /// <summary>The configuration section, <c>Sources</c>.</summary>
    public const string SectionName = "Sources";

    /// <summary>Hard cap of sources (both kinds) on one episode.</summary>
    public const int DefaultMaxPerEpisode = 10;

    /// <summary>Url sources one user may create in a rolling window (FR-02-8).</summary>
    public const int DefaultMaxUrlSourcesPerUserPerDay = 30;

    /// <summary>The longest user note.</summary>
    public const int DefaultMaxNoteChars = 3_000;

    /// <summary>The largest accepted <c>MaxNoteChars</c>.</summary>
    public const int MaxNoteCharsCeiling = 10_000;

    /// <summary>The rolling window of the url-source cap.</summary>
    public static readonly TimeSpan UrlWindow = TimeSpan.FromHours(24);

    /// <summary>Sources allowed on one episode; the next one is 409 <c>source_limit_reached</c>.</summary>
    public int MaxPerEpisode { get; set; } = DefaultMaxPerEpisode;

    /// <summary>Url sources per user in <see cref="UrlWindow"/>; the next one is 429 <c>source_fetch_limit_reached</c>.</summary>
    public int MaxUrlSourcesPerUserPerDay { get; set; } = DefaultMaxUrlSourcesPerUserPerDay;

    /// <summary>The longest note, in characters (raw length, before the Domain's trim).</summary>
    public int MaxNoteChars { get; set; } = DefaultMaxNoteChars;

    /// <summary>The start-up problems of <paramref name="options"/>; empty when it is valid.</summary>
    /// <param name="options">The bound section.</param>
    public static IReadOnlyList<string> Validate(SourcesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (options.MaxPerEpisode < 1)
        {
            problems.Add($"'{SectionName}:{nameof(MaxPerEpisode)}' must be greater than 0.");
        }

        if (options.MaxUrlSourcesPerUserPerDay < 1)
        {
            problems.Add($"'{SectionName}:{nameof(MaxUrlSourcesPerUserPerDay)}' must be greater than 0.");
        }

        if (options.MaxNoteChars is < 1 or > MaxNoteCharsCeiling)
        {
            problems.Add($"'{SectionName}:{nameof(MaxNoteChars)}' must be between 1 and {MaxNoteCharsCeiling}.");
        }

        return problems;
    }
}
