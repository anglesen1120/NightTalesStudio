using NightTales.Domain.Episodes;

namespace NightTales.Application.Features.Stories.Jobs;

/// <summary>
/// The per-run time budget of <c>GenerateStoryJob</c> (02.2.2-T3, TL T21), bound from <c>Story:Job</c>. Every key has a
/// default, so none is a required start-up key; a value outside the bounds fails the Worker's start-up.
/// </summary>
public sealed class StoryJobOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Story:Job";

    /// <summary>The smallest accepted timeout; below it the three paid steps cannot fit.</summary>
    public const int MinTimeoutSeconds = 60;

    /// <summary>The largest accepted timeout.</summary>
    public const int MaxTimeoutSeconds = 3_600;

    /// <summary>The run deadline for the Short and Standard tiers: the worst case of 300 s fits inside 360 s.</summary>
    public int TimeoutSeconds { get; set; } = 360;

    /// <summary>The run deadline for the Long tier.</summary>
    public int TimeoutSecondsLong { get; set; } = 480;

    /// <summary>The deadline length of a run of <paramref name="tier"/>.</summary>
    /// <param name="tier">The episode's length tier.</param>
    /// <returns>The timeout, counted from <c>started_at</c>.</returns>
    public TimeSpan TimeoutFor(LengthTier tier) =>
        TimeSpan.FromSeconds(tier == LengthTier.Long ? TimeoutSecondsLong : TimeoutSeconds);

    /// <summary>Checks both timeouts; the Worker's options validation calls it.</summary>
    /// <param name="options">The bound options.</param>
    /// <returns>Every problem found; empty when the options are valid.</returns>
    public static IReadOnlyList<string> Validate(StoryJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            problems.Add($"'{SectionName}:{nameof(TimeoutSeconds)}' must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds}.");
        }

        if (options.TimeoutSecondsLong < options.TimeoutSeconds || options.TimeoutSecondsLong > MaxTimeoutSeconds)
        {
            problems.Add($"'{SectionName}:{nameof(TimeoutSecondsLong)}' must be between '{nameof(TimeoutSeconds)}' and {MaxTimeoutSeconds}.");
        }

        return problems;
    }
}

/// <summary>The batch size of <c>StaleGenerationSweeperJob</c> (02.2.2-T3), bound from <c>Story:Sweeper</c>.</summary>
public sealed class StorySweeperOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Story:Sweeper";

    /// <summary>The largest accepted batch.</summary>
    public const int MaxBatchSize = 5_000;

    /// <summary>How many stale runs one sweep pass handles per query.</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>
/// The model review switch (02.6.1-T1, 02 §10 T113), bound from <c>Story:Review</c>. The job reads it once per run and
/// records the value it used in <c>generation_runs.models</c>.
/// </summary>
public sealed class StoryReviewOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Story:Review";

    /// <summary><see langword="false"/> skips the review stage (and, once it exists, the revision).</summary>
    public bool Enabled { get; set; } = true;
}
