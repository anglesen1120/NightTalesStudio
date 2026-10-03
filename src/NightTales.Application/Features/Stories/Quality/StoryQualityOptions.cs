using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Quality;

/// <summary>
/// The <c>Story:Quality</c> section (02.3.4-T1). Every key is required and has no code default: a missing key is
/// a start-up failure, never a silently different check. Application takes plain settings, so the options are bound
/// and validated in Infrastructure (<c>AddStoryQuality</c>) and handed on as <see cref="StoryQualityThresholds"/>.
/// </summary>
public sealed class StoryQualityOptions
{
    /// <summary>The configuration section path, <c>Story:Quality</c>.</summary>
    public const string SectionName = "Story:Quality";

    /// <summary>Q-LEN-01/02: the share outside the D4 token band that blocks (0 to 0.5).</summary>
    public double? LengthTolerance { get; set; }

    /// <summary>Q-HOOK-01: the fewest hook tokens.</summary>
    public int? MinHookTokens { get; set; }

    /// <summary>Q-HOOK-02: the most hook sentences.</summary>
    public int? MaxHookSentences { get; set; }

    /// <summary>Q-KW-01: <c>[min, max]</c> keywords per segment, both in 0 to 5.</summary>
    public int[]? KeywordsPerSegment { get; set; }

    /// <summary>Q-KW-02: <c>K</c>, keywords per minute (1 to 20).</summary>
    public int? KeywordsPerMinute { get; set; }

    /// <summary>Q-REP-01: the fewest tokens of a repeated sentence (at least 4).</summary>
    public int? RepeatedSentenceMinTokens { get; set; }

    /// <summary>Q-REP-02: the largest repeated-shingle share, exclusive 0 to 1.</summary>
    public double? MaxInternalShingleShare { get; set; }

    /// <summary>Q-NAME-01: whether the true-crime name heuristic runs.</summary>
    public bool? NameHeuristic { get; set; }

    /// <summary>The start-up problems of <paramref name="options"/>; empty when it is valid.</summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Validate(StoryQualityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        void Missing(string key) => problems.Add($"'{SectionName}:{key}' is required.");

        if (options.LengthTolerance is not { } tolerance)
        {
            Missing(nameof(LengthTolerance));
        }
        else if (!(tolerance is >= 0 and <= 0.5))
        {
            problems.Add($"'{SectionName}:{nameof(LengthTolerance)}' must be between 0 and 0.5.");
        }

        if (options.MinHookTokens is null)
        {
            Missing(nameof(MinHookTokens));
        }
        else if (options.MinHookTokens < 0)
        {
            problems.Add($"'{SectionName}:{nameof(MinHookTokens)}' must not be negative.");
        }

        if (options.MaxHookSentences is null)
        {
            Missing(nameof(MaxHookSentences));
        }
        else if (options.MaxHookSentences < 1)
        {
            problems.Add($"'{SectionName}:{nameof(MaxHookSentences)}' must be at least 1.");
        }

        if (options.KeywordsPerSegment is not { } range)
        {
            Missing(nameof(KeywordsPerSegment));
        }
        else if (range.Length != 2 || range[0] > range[1] || range[0] < 0 || range[1] > 5)
        {
            problems.Add($"'{SectionName}:{nameof(KeywordsPerSegment)}' must be [min, max] with 0 <= min <= max <= 5.");
        }

        if (options.KeywordsPerMinute is not { } perMinute)
        {
            Missing(nameof(KeywordsPerMinute));
        }
        else if (perMinute is < 1 or > 20)
        {
            problems.Add($"'{SectionName}:{nameof(KeywordsPerMinute)}' must be between 1 and 20.");
        }

        if (options.RepeatedSentenceMinTokens is not { } minTokens)
        {
            Missing(nameof(RepeatedSentenceMinTokens));
        }
        else if (minTokens < 4)
        {
            problems.Add($"'{SectionName}:{nameof(RepeatedSentenceMinTokens)}' must be at least 4.");
        }

        if (options.MaxInternalShingleShare is not { } share)
        {
            Missing(nameof(MaxInternalShingleShare));
        }
        else if (!(share > 0 && share < 1))
        {
            problems.Add($"'{SectionName}:{nameof(MaxInternalShingleShare)}' must be greater than 0 and less than 1.");
        }

        if (options.NameHeuristic is null)
        {
            Missing(nameof(NameHeuristic));
        }

        return problems;
    }

    /// <summary>The validated options as the Domain thresholds; call only after <see cref="Validate"/> returned no problem.</summary>
    /// <returns>The thresholds the checker reads.</returns>
    public StoryQualityThresholds ToThresholds()
    {
        if (Validate(this).Count > 0)
        {
            throw new InvalidOperationException($"'{SectionName}' is invalid.");
        }

        return new StoryQualityThresholds(
            LengthTolerance!.Value,
            MinHookTokens!.Value,
            MaxHookSentences!.Value,
            KeywordsPerSegment![0],
            KeywordsPerSegment[1],
            KeywordsPerMinute!.Value,
            RepeatedSentenceMinTokens!.Value,
            MaxInternalShingleShare!.Value,
            NameHeuristic!.Value);
    }
}
