namespace NightTales.Application.Features.Stories.Review;

/// <summary>
/// The single-revision switch (02.6.2-T1, 02 README D21), bound from <c>Story:Revision</c>. The pipeline reads it
/// once per run: <see cref="Enabled"/> off or <see cref="MaxRevisions"/> zero skips the revision (and its review #2),
/// and the worst-case cost reservation follows the same switch so a disabled stage is never reserved for.
/// </summary>
public sealed class StoryRevisionOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Story:Revision";

    /// <summary>The default of <see cref="Enabled"/>: the revision stage runs unless switched off.</summary>
    public const bool DefaultEnabled = true;

    /// <summary>The default and only accepted count of a run: D21 allows a single revision pass.</summary>
    public const int DefaultMaxRevisions = 1;

    /// <summary>The largest accepted <see cref="MaxRevisions"/>; D21 caps a run at exactly one revision.</summary>
    public const int MaxRevisionsCeiling = 1;

    /// <summary>The default of <see cref="MinTextSimilarity"/> (D21).</summary>
    public const double DefaultMinTextSimilarity = 0.6;

    /// <summary><see langword="false"/> skips the revision stage (and review #2); existing rows stay readable.</summary>
    public bool Enabled { get; set; } = DefaultEnabled;

    /// <summary>How many revisions one run may apply: 0 or 1 only (D21, never a loop).</summary>
    public int MaxRevisions { get; set; } = DefaultMaxRevisions;

    /// <summary>The token-set similarity below which an untouched segment counts as drifted (D21); in (0, 1].</summary>
    public double MinTextSimilarity { get; set; } = DefaultMinTextSimilarity;

    /// <summary>Checks the section; the Worker's options validation calls it, so a bad value fails the boot.</summary>
    /// <param name="options">The bound section.</param>
    /// <returns>Every problem found; empty when the options are valid.</returns>
    public static IReadOnlyList<string> Validate(StoryRevisionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (options.MaxRevisions is < 0 or > MaxRevisionsCeiling)
        {
            problems.Add($"'{SectionName}:{nameof(MaxRevisions)}' must be 0 or {MaxRevisionsCeiling}.");
        }

        if (double.IsNaN(options.MinTextSimilarity) || options.MinTextSimilarity <= 0 || options.MinTextSimilarity > 1)
        {
            problems.Add($"'{SectionName}:{nameof(MinTextSimilarity)}' must be greater than 0 and at most 1.");
        }

        return problems;
    }
}
