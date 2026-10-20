namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The resolved per-category moderation thresholds of one gate evaluation (02.8.2 plan §3.3,
/// <c>Autopilot:Gates:Profiles:*:Moderation</c>): the categories a flag on which always fails check 7, and the
/// highest accepted score of each remaining omni category.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configured, not shipped code.</b> The plan lists the <c>default</c> block as data (02.8.2-T3 calibrates it
/// on the first ten episodes), so this record only carries the numbers the content gate compares against and
/// <see cref="Defaults"/> repeats the plan's shipped values; <c>strict</c> or a future profile overlays them.
/// </para>
/// <para>
/// <b>A category without a ceiling fails when it is flagged.</b> That is deliberate and is not a validator
/// problem: a provider that reports a category the operator never decided a threshold for must route the
/// episode to the owner, never pass silently (the content gate's fail-closed rule).
/// </para>
/// </remarks>
/// <param name="BlockedCategories">
/// Categories a flag on which fails the gate whatever the score (plan §3.3 <c>BlockedCategories</c>).
/// </param>
/// <param name="MaxScore">
/// The highest accepted score per category, 0…1. A category absent from the map has no ceiling: a flag on it
/// fails, but a score below the provider's own flag threshold does not.
/// </param>
public sealed record ModerationThresholds(
    IReadOnlyList<string> BlockedCategories,
    IReadOnlyDictionary<string, double> MaxScore)
{
    /// <summary>The plan §3.3 <c>default</c> moderation block, the base every other profile is overlaid on.</summary>
    public static ModerationThresholds Defaults { get; } = new(
        BlockedCategories: ["sexual/minors", "self-harm/instructions", "illicit/violent", "hate/threatening"],
        MaxScore: new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["harassment"] = 0.5,
            ["hate"] = 0.3,
            ["illicit"] = 0.5,
            ["self-harm"] = 0.3,
            ["sexual"] = 0.5,
            ["violence"] = 0.6,
            ["violence/graphic"] = 0.3,
        });

    /// <summary>
    /// This block with <paramref name="patch"/> overlaid: a member the patch leaves unset keeps this value, and a
    /// score the patch names replaces that one entry while the other ceilings inherit, so a profile can tighten a
    /// single category without restating the block (plan §3.3). An empty score map adds no entry. Pure and total.
    /// </summary>
    /// <param name="patch">The configured block, or <see langword="null"/> to keep every member.</param>
    /// <returns>The merged thresholds.</returns>
    public ModerationThresholds Overlay(ModerationThresholdsPatch? patch)
    {
        if (patch is null)
        {
            return this;
        }

        var blocked = patch.BlockedCategories ?? BlockedCategories;
        if (patch.MaxScore is not { Count: > 0 } additions)
        {
            return new ModerationThresholds(blocked, MaxScore);
        }

        var scores = MaxScore.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var (category, score) in additions)
        {
            scores[category] = score;
        }

        return new ModerationThresholds(blocked, scores);
    }
}

/// <summary>
/// The configured <c>Autopilot:Gates:Profiles:*:Moderation</c> section (plan §3.3): every member optional, so an
/// absent key inherits from the profile it is overlaid on. A configured empty <c>BlockedCategories</c> list is a
/// value, not an absent key, and the options validator refuses it when the resolved profile then blocks nothing.
/// </summary>
/// <param name="BlockedCategories">The configured blocked categories, or <see langword="null"/> to inherit.</param>
/// <param name="MaxScore">The configured score ceilings to add or replace, or <see langword="null"/> to inherit.</param>
public sealed record ModerationThresholdsPatch(
    IReadOnlyList<string>? BlockedCategories = null,
    IReadOnlyDictionary<string, double>? MaxScore = null);
