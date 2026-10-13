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

    /// <summary>The default of <see cref="RevisionRequestTimeoutSeconds"/>: the revise step's own timeout (D7).</summary>
    public const int DefaultRevisionRequestTimeoutSeconds = 90;

    /// <summary>The default of <see cref="ReviewRequestTimeoutSeconds"/>: the review step's own timeout (D7).</summary>
    public const int DefaultReviewRequestTimeoutSeconds = 45;

    /// <summary>The default of <see cref="SaveMarginSeconds"/>: room for the terminal save and the settlement.</summary>
    public const int DefaultSaveMarginSeconds = 30;

    /// <summary>The smallest accepted value of the three revision-headroom keys.</summary>
    public const int MinHeadroomSeconds = 1;

    /// <summary>
    /// The run deadline for the Short and Standard tiers. One pass of the paid steps without a revision needs 300 s
    /// of it; the optional revision is only started while the deadline still fits what it costs on top (D7, TL T21,
    /// 02.6.2-T1): the pipeline compares the remaining deadline against <see cref="RevisionHeadroom"/> first, and
    /// against <see cref="ReviewAfterHeadroom"/> before review #2.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 360;

    /// <summary>The run deadline for the Long tier.</summary>
    public int TimeoutSecondsLong { get; set; } = 480;

    /// <summary>
    /// The revise call's own timeout the revision deadline check reserves (D7: the <c>Revision</c> step's
    /// <c>OpenAi:RequestTimeout</c>, 90 s). The pipeline runs in the Application layer and cannot read the adapter's
    /// options, so the number it plans with is a run-deadline key of its own.
    /// </summary>
    public int RevisionRequestTimeoutSeconds { get; set; } = DefaultRevisionRequestTimeoutSeconds;

    /// <summary>The review call's own timeout the revision deadline check reserves for review #2 (D7: the <c>Review</c> step's <c>OpenAi:RequestTimeout</c>, 45 s).</summary>
    public int ReviewRequestTimeoutSeconds { get; set; } = DefaultReviewRequestTimeoutSeconds;

    /// <summary>The room the run keeps after its last paid call for the terminal save and the settlement (02.6.2-T1).</summary>
    public int SaveMarginSeconds { get; set; } = DefaultSaveMarginSeconds;

    /// <summary>The room kept after the last paid call, as a duration.</summary>
    public TimeSpan SaveMargin => TimeSpan.FromSeconds(SaveMarginSeconds);

    /// <summary>
    /// What the optional revision stage still needs before it starts: the revise call, review #2 and the save margin.
    /// The pipeline enters the stage only while the run deadline has at least this much left; otherwise the revision
    /// degrades to <c>revision_unavailable</c> and the generated row is saved (02.6.2-T1).
    /// </summary>
    public TimeSpan RevisionHeadroom =>
        TimeSpan.FromSeconds(RevisionRequestTimeoutSeconds + ReviewRequestTimeoutSeconds + SaveMarginSeconds);

    /// <summary>What review #2 still needs once the revise call has returned: its own timeout and the save margin.</summary>
    public TimeSpan ReviewAfterHeadroom => TimeSpan.FromSeconds(ReviewRequestTimeoutSeconds + SaveMarginSeconds);

    /// <summary>The deadline length of a run of <paramref name="tier"/>.</summary>
    /// <param name="tier">The episode's length tier.</param>
    /// <returns>The timeout, counted from <c>started_at</c>.</returns>
    public TimeSpan TimeoutFor(LengthTier tier) =>
        TimeSpan.FromSeconds(tier == LengthTier.Long ? TimeoutSecondsLong : TimeoutSeconds);

    /// <summary>Checks both timeouts and the three revision-headroom keys; the Worker's options validation calls it.</summary>
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

        Check(nameof(RevisionRequestTimeoutSeconds), options.RevisionRequestTimeoutSeconds);
        Check(nameof(ReviewRequestTimeoutSeconds), options.ReviewRequestTimeoutSeconds);
        Check(nameof(SaveMarginSeconds), options.SaveMarginSeconds);

        return problems;

        void Check(string key, int value)
        {
            if (value is < MinHeadroomSeconds or > MaxTimeoutSeconds)
            {
                problems.Add($"'{SectionName}:{key}' must be between {MinHeadroomSeconds} and {MaxTimeoutSeconds}.");
            }
        }
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
