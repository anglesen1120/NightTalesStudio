namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// The resolved story thresholds of one gate evaluation (02.8.2 plan §3.3, <c>Autopilot:Gates:Profiles</c>):
/// the numbers <see cref="StoryGateRules"/> compares against and writes into every <see cref="GateCheck"/>.
/// </summary>
/// <param name="MinMeanReviewScore">Check 2: the lowest accepted mean of the non-null D16 dimensions.</param>
/// <param name="MinDimensionScore">Check 3: the lowest accepted single dimension.</param>
/// <param name="MaxRevisions">Check 5: how many revisions are still acceptable (D21: one).</param>
/// <param name="MaxSourceSimilarity">Check 9: the highest accepted <c>story_versions.similarity_score</c>.</param>
/// <param name="MaxPastEpisodeSimilarity">Check 10: the highest accepted similarity against the series' own history.</param>
/// <param name="AllowedContentWarnings">Check 13: the closed allow-list <c>content_warnings</c> must be a subset of.</param>
public sealed record StoryGateThresholds(
    decimal MinMeanReviewScore,
    decimal MinDimensionScore,
    int MaxRevisions,
    decimal MaxSourceSimilarity,
    decimal MaxPastEpisodeSimilarity,
    IReadOnlyList<string> AllowedContentWarnings)
{
    /// <summary>The plan §3.3 <c>default</c> profile, the base every other profile is overlaid on.</summary>
    public static StoryGateThresholds Defaults { get; } = new(
        MinMeanReviewScore: 3.5m,
        MinDimensionScore: 3.0m,
        MaxRevisions: 1,
        MaxSourceSimilarity: 0.05m,
        MaxPastEpisodeSimilarity: 0.10m,
        AllowedContentWarnings: ["violence", "death", "crime", "supernatural", "disturbing"]);

    /// <summary>
    /// This profile with <paramref name="patch"/> overlaid: a member the patch leaves unset keeps this value,
    /// which is what makes <c>strict</c> a partial overlay on <c>default</c> (plan §3.3). Pure and total.
    /// </summary>
    /// <param name="patch">The configured profile, or <see langword="null"/> to keep every member.</param>
    /// <returns>The merged thresholds.</returns>
    public StoryGateThresholds Overlay(StoryGateThresholdsPatch? patch) => patch is null
        ? this
        : new StoryGateThresholds(
            patch.MinMeanReviewScore ?? MinMeanReviewScore,
            patch.MinDimensionScore ?? MinDimensionScore,
            patch.MaxRevisions ?? MaxRevisions,
            patch.MaxSourceSimilarity ?? MaxSourceSimilarity,
            patch.MaxPastEpisodeSimilarity ?? MaxPastEpisodeSimilarity,
            patch.AllowedContentWarnings ?? AllowedContentWarnings);
}

/// <summary>
/// The configured <c>Autopilot:Gates:Profiles:*:Story</c> section (plan §3.3): every member optional, so an
/// absent key inherits from the profile it is overlaid on. This is the shape the configuration binder fills.
/// </summary>
/// <param name="MinMeanReviewScore">The configured mean-score floor, or <see langword="null"/> to inherit.</param>
/// <param name="MinDimensionScore">The configured dimension floor, or <see langword="null"/> to inherit.</param>
/// <param name="MaxRevisions">The configured revision cap, or <see langword="null"/> to inherit.</param>
/// <param name="MaxSourceSimilarity">The configured source-similarity cap, or <see langword="null"/> to inherit.</param>
/// <param name="MaxPastEpisodeSimilarity">The configured past-episode cap, or <see langword="null"/> to inherit.</param>
/// <param name="AllowedContentWarnings">
/// The configured allow-list, or <see langword="null"/> to inherit. An empty list is a value, not an absent key:
/// it allows no warning at all.
/// </param>
public sealed record StoryGateThresholdsPatch(
    decimal? MinMeanReviewScore = null,
    decimal? MinDimensionScore = null,
    int? MaxRevisions = null,
    decimal? MaxSourceSimilarity = null,
    decimal? MaxPastEpisodeSimilarity = null,
    IReadOnlyList<string>? AllowedContentWarnings = null);

/// <summary>
/// The resolved video thresholds of one gate evaluation (02.8.2 plan §3.2/§3.3): the numbers
/// <see cref="VideoGateRules"/> compares against and writes into every <see cref="GateCheck"/>.
/// </summary>
/// <param name="DurationTolerance">Check 3: the largest accepted relative duration error (0.05, 05.5.1).</param>
/// <param name="LoudnessMinLufs">Check 4: the lower end of the accepted loudness band (−16, inclusive).</param>
/// <param name="LoudnessMaxLufs">Check 4: the upper end of the accepted loudness band (−14, inclusive).</param>
public sealed record VideoGateThresholds(
    double DurationTolerance,
    double LoudnessMinLufs,
    double LoudnessMaxLufs)
{
    /// <summary>The plan §3.3 video values, the base every profile is overlaid on.</summary>
    public static VideoGateThresholds Defaults { get; } = new(
        DurationTolerance: 0.05,
        LoudnessMinLufs: -16,
        LoudnessMaxLufs: -14);

    /// <summary>This profile with <paramref name="patch"/> overlaid; an unset member keeps this value.</summary>
    /// <param name="patch">The configured profile, or <see langword="null"/> to keep every member.</param>
    /// <returns>The merged thresholds.</returns>
    public VideoGateThresholds Overlay(VideoGateThresholdsPatch? patch) => patch is null
        ? this
        : new VideoGateThresholds(
            patch.DurationTolerance ?? DurationTolerance,
            patch.LoudnessMinLufs ?? LoudnessMinLufs,
            patch.LoudnessMaxLufs ?? LoudnessMaxLufs);
}

/// <summary>The configured <c>Autopilot:Gates:Profiles:*:Video</c> section (plan §3.3); every member optional.</summary>
/// <param name="DurationTolerance">The configured duration tolerance, or <see langword="null"/> to inherit.</param>
/// <param name="LoudnessMinLufs">The configured lower loudness bound, or <see langword="null"/> to inherit.</param>
/// <param name="LoudnessMaxLufs">The configured upper loudness bound, or <see langword="null"/> to inherit.</param>
public sealed record VideoGateThresholdsPatch(
    double? DurationTolerance = null,
    double? LoudnessMinLufs = null,
    double? LoudnessMaxLufs = null);

/// <summary>
/// One resolved gate profile: everything <see cref="StoryGateRules"/>, <see cref="VideoGateRules"/> and the
/// Application's content gate need, with no member left unset.
/// </summary>
/// <param name="Story">The story thresholds (checks 1–13).</param>
/// <param name="Video">The video thresholds (checks 1–10).</param>
/// <param name="Moderation">
/// The moderation thresholds of check 7 (<c>BlockedCategories</c>, <c>MaxScore</c>, 02.8.2-T2).
/// </param>
/// <param name="RealWorld">
/// The real-world detector's behaviour of check 8 (02.8.2-T2): whether a date needs an incident word in the same
/// segment, and the incident words themselves.
/// </param>
public sealed record AutopilotGateProfile(
    StoryGateThresholds Story,
    VideoGateThresholds Video,
    ModerationThresholds Moderation,
    RealWorldOptions RealWorld)
{
    /// <summary>The plan §3.3 <c>default</c> profile; a series with no <c>gate_thresholds_ref</c> uses it.</summary>
    public static AutopilotGateProfile Defaults { get; } = new(
        StoryGateThresholds.Defaults,
        VideoGateThresholds.Defaults,
        ModerationThresholds.Defaults,
        RealWorldOptions.Defaults);

    /// <summary>
    /// This profile with <paramref name="patch"/> overlaid: <c>strict</c> raises only the keys it names
    /// (4.0/3.5/0.03/0.05), every other key inherits (plan §3.3). Pure and total.
    /// </summary>
    /// <param name="patch">The configured profile, or <see langword="null"/> to keep every member.</param>
    /// <returns>The merged profile.</returns>
    public AutopilotGateProfile Overlay(AutopilotGateProfilePatch? patch) => patch is null
        ? this
        : new AutopilotGateProfile(
            Story.Overlay(patch.Story),
            Video.Overlay(patch.Video),
            Moderation.Overlay(patch.Moderation),
            MergeRealWorld(RealWorld, patch.RealWorld));

    /// <summary>
    /// The profile a series resolves to: the plan §3.3 defaults with its configured profile overlaid. This is
    /// what the Application's <c>Resolve(gate_thresholds_ref)</c> calls, with <see langword="null"/> for an
    /// unknown or absent ref.
    /// </summary>
    /// <param name="patch">The configured profile entry, or <see langword="null"/> for the defaults.</param>
    /// <returns>A complete profile, safe to evaluate with.</returns>
    public static AutopilotGateProfile Resolve(AutopilotGateProfilePatch? patch) => Defaults.Overlay(patch);

    private static RealWorldOptions MergeRealWorld(RealWorldOptions options, RealWorldOptionsPatch? patch) => patch is null
        ? options
        : new RealWorldOptions(
            patch.RequireIncidentCoOccurrence ?? options.RequireIncidentCoOccurrence,
            patch.IncidentWords ?? options.IncidentWords);
}

/// <summary>
/// One configured entry of <c>Autopilot:Gates:Profiles</c> (plan §3.3): <c>default</c>, <c>strict</c> or a
/// future name, with every member optional so absent keys inherit from <see cref="AutopilotGateProfile.Defaults"/>.
/// </summary>
/// <param name="Story">The configured story section, or <see langword="null"/> to inherit all of it.</param>
/// <param name="Video">The configured video section, or <see langword="null"/> to inherit all of it.</param>
/// <param name="Moderation">The configured moderation block, or <see langword="null"/> to inherit all of it.</param>
/// <param name="RealWorld">The configured real-world section, or <see langword="null"/> to inherit all of it.</param>
public sealed record AutopilotGateProfilePatch(
    StoryGateThresholdsPatch? Story = null,
    VideoGateThresholdsPatch? Video = null,
    ModerationThresholdsPatch? Moderation = null,
    RealWorldOptionsPatch? RealWorld = null);

/// <summary>
/// The configured <c>Autopilot:Gates:Profiles:*:RealWorld</c> section (plan §3.3): every member optional, so an
/// absent key inherits. A configured empty <c>IncidentWords</c> list is a value, not an absent key.
/// </summary>
/// <param name="RequireIncidentCoOccurrence">
/// The configured co-occurrence switch, or <see langword="null"/> to inherit. With <see langword="false"/> a
/// dated-incident pattern alone is a hit; the shipped default requires the incident word in the same segment.
/// </param>
/// <param name="IncidentWords">The configured incident words, or <see langword="null"/> to inherit.</param>
public sealed record RealWorldOptionsPatch(
    bool? RequireIncidentCoOccurrence = null,
    IReadOnlyList<string>? IncidentWords = null);
