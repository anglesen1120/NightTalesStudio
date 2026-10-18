using NightTales.Domain.Stories.Gates;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The <c>Autopilot:Gates</c> section (02.8.2 plan §3.3): whether the gate may approve at all, how long the
/// calibration dry run lasts, and the threshold profiles <see cref="StoryGateRules"/> and
/// <see cref="VideoGateRules"/> evaluate with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dry run is the shipped default.</b> <see cref="DryRun"/> is <see langword="true"/> until the owner flips
/// it, so a deployment that says nothing about this feature never auto-approves anything; that is also the
/// rollback recipe of the plan's failure table (set the key back and nothing else changes).
/// </para>
/// <para>
/// <b>A plain settings object, bound by the host.</b> Application may not reference
/// <c>Microsoft.Extensions.Options</c> (the assembly-reference rule), so every host binds this section, runs
/// <see cref="AutopilotGateOptionsValidator.Validate"/> at start-up and exposes the bound value as itself —
/// exactly as it does for <c>AssetRetentionOptions</c>. The value it hands over is read from the live monitor,
/// so a threshold change needs no restart; <see cref="Mode"/> is read from that same fresh value.
/// </para>
/// <para>
/// <b>An unknown profile is never a disabled gate.</b> <see cref="Resolve"/> answers an absent, blank or unknown
/// <c>gate_thresholds_ref</c> with the shipped <c>default</c> profile; the caller logs the unknown ref. A profile
/// can therefore only make the gate stricter or looser within its own declared numbers, never turn it off.
/// </para>
/// </remarks>
public sealed class AutopilotGateOptions
{
    /// <summary>The configuration section this class binds to.</summary>
    public const string SectionName = "Autopilot:Gates";

    /// <summary>The profile every series resolves to unless its own reference names one (plan §3.3).</summary>
    public const string DefaultProfileName = "default";

    /// <summary>The shipped value of <see cref="DryRun"/>: record the verdict, never approve.</summary>
    public const bool DefaultDryRun = true;

    /// <summary>The shipped value of <see cref="DryRunMaxEpisodes"/>; after this many runs the owner decides.</summary>
    public const int DefaultDryRunMaxEpisodes = 10;

    /// <summary>
    /// Whether a pass only records the verdict (<c>Autopilot:Gates:DryRun</c>). The shipped default is
    /// <see langword="true"/>: nothing is auto-approved until the owner has read a calibration report and
    /// turned this off.
    /// </summary>
    public bool DryRun { get; set; } = DefaultDryRun;

    /// <summary>
    /// How many evaluated runs the dry run reports before the owner is expected to decide; read by 02.8.3-T1's
    /// tick and by the calibration report, never by a check.
    /// </summary>
    public int DryRunMaxEpisodes { get; set; } = DefaultDryRunMaxEpisodes;

    /// <summary>
    /// The configured profiles, keyed by the name a series' <c>autopilot_settings.gate_thresholds_ref</c> holds.
    /// Every entry is a partial overlay on the shipped defaults, so a profile that names two keys changes two
    /// keys (plan §3.3: <c>strict</c> is <c>default</c> overlaid).
    /// </summary>
    public Dictionary<string, AutopilotGateProfileSection> Profiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The mode this configuration runs in: <see cref="GateMode.DryRun"/> while <see cref="DryRun"/>.</summary>
    public GateMode Mode => DryRun ? GateMode.DryRun : GateMode.Live;

    /// <summary>
    /// The profile one series evaluates with: the shipped defaults, then the configured
    /// <see cref="DefaultProfileName"/> entry, then the entry <paramref name="profileRef"/> names.
    /// </summary>
    /// <param name="profileRef">
    /// The series' <c>gate_thresholds_ref</c>, or <see langword="null"/> when it has none.
    /// </param>
    /// <returns>A complete profile; never <see langword="null"/> and never a disabled gate.</returns>
    public AutopilotGateProfile Resolve(string? profileRef)
    {
        var resolved = AutopilotGateProfile.Defaults;

        if (Profiles.TryGetValue(DefaultProfileName, out var baseline))
        {
            resolved = resolved.Overlay(baseline.ToPatch());
        }

        return profileRef is not null && !IsDefault(profileRef) && Profiles.TryGetValue(profileRef, out var named)
            ? resolved.Overlay(named.ToPatch())
            : resolved;
    }

    /// <summary>
    /// The profile name a verdict is stored with (plan §3.4's <c>profile</c> key): the requested one when it is
    /// configured, otherwise <see cref="DefaultProfileName"/> — so the stored name is always one
    /// <see cref="Resolve"/> would accept, and an unknown ref is visible as the default it fell back to.
    /// </summary>
    /// <param name="profileRef">The series' <c>gate_thresholds_ref</c>, or <see langword="null"/>.</param>
    /// <returns>The effective profile name.</returns>
    public string ResolveName(string? profileRef) =>
        profileRef is not null && Profiles.ContainsKey(profileRef) ? profileRef : DefaultProfileName;

    private static bool IsDefault(string profileRef) => string.Equals(profileRef, DefaultProfileName, StringComparison.Ordinal);
}

/// <summary>
/// One bindable entry of <c>Autopilot:Gates:Profiles</c> (plan §3.3). The Domain's
/// <see cref="AutopilotGateProfilePatch"/> is the immutable record the rules read, but the options system
/// cannot construct it member by member, so the section binds here and converts — the same split
/// <c>IdeaWeightsOptions</c> uses for <c>IdeaWeights</c>.
/// </summary>
/// <remarks>
/// The moderation thresholds of plan §3.3's example (<c>Moderation</c>, <c>RealWorld</c>) are deliberately not
/// bound here: they belong to 02.8.2-T2, and an operator key this feature does not implement must not look
/// implemented. A profile that carries only such keys binds to an empty section, which
/// <see cref="AutopilotGateOptionsValidator"/> refuses.
/// </remarks>
public sealed class AutopilotGateProfileSection
{
    /// <summary>The story thresholds of this profile (checks 1–13), or <see langword="null"/> to inherit all.</summary>
    public StoryGateThresholdsSection? Story { get; set; }

    /// <summary>The video thresholds of this profile (checks 1–10), or <see langword="null"/> to inherit all.</summary>
    public VideoGateThresholdsSection? Video { get; set; }

    /// <summary>Whether this section names no threshold at all; the validator refuses such a profile.</summary>
    public bool IsEmpty => Story is null && Video is null;

    /// <summary>The Domain patch this section configures.</summary>
    /// <returns>The partial patch <see cref="AutopilotGateProfile.Overlay"/> reads.</returns>
    public AutopilotGateProfilePatch ToPatch() => new(Story?.ToPatch(), Video?.ToPatch());
}

/// <summary>The bindable <c>Profiles:*:Story</c> section (plan §3.3); every member optional, so an absent key inherits.</summary>
public sealed class StoryGateThresholdsSection
{
    /// <summary>Check 2's floor: the lowest accepted mean of the non-null D16 dimensions, or <see langword="null"/> to inherit.</summary>
    public decimal? MinMeanReviewScore { get; set; }

    /// <summary>Check 3's floor: the lowest accepted single dimension, or <see langword="null"/> to inherit.</summary>
    public decimal? MinDimensionScore { get; set; }

    /// <summary>Check 5's cap: how many revisions are still acceptable, or <see langword="null"/> to inherit.</summary>
    public int? MaxRevisions { get; set; }

    /// <summary>Check 9's ceiling: the highest accepted source similarity, or <see langword="null"/> to inherit.</summary>
    public decimal? MaxSourceSimilarity { get; set; }

    /// <summary>Check 10's ceiling: the highest accepted past-episode similarity, or <see langword="null"/> to inherit.</summary>
    public decimal? MaxPastEpisodeSimilarity { get; set; }

    /// <summary>
    /// Check 13's allow-list, or <see langword="null"/> to inherit. An empty list is a value, not an absent key:
    /// it allows no content warning at all.
    /// </summary>
    public List<string>? AllowedContentWarnings { get; set; }

    /// <summary>The Domain patch this section configures.</summary>
    /// <returns>The partial story thresholds.</returns>
    public StoryGateThresholdsPatch ToPatch() => new(
        MinMeanReviewScore,
        MinDimensionScore,
        MaxRevisions,
        MaxSourceSimilarity,
        MaxPastEpisodeSimilarity,
        AllowedContentWarnings);
}

/// <summary>The bindable <c>Profiles:*:Video</c> section (plan §3.3); every member optional, so an absent key inherits.</summary>
public sealed class VideoGateThresholdsSection
{
    /// <summary>Check 3's largest accepted relative duration error, or <see langword="null"/> to inherit.</summary>
    public double? DurationTolerance { get; set; }

    /// <summary>Check 4's lower loudness bound in LUFS, or <see langword="null"/> to inherit.</summary>
    public double? LoudnessMinLufs { get; set; }

    /// <summary>Check 4's upper loudness bound in LUFS, or <see langword="null"/> to inherit.</summary>
    public double? LoudnessMaxLufs { get; set; }

    /// <summary>The Domain patch this section configures.</summary>
    /// <returns>The partial video thresholds.</returns>
    public VideoGateThresholdsPatch ToPatch() => new(DurationTolerance, LoudnessMinLufs, LoudnessMaxLufs);
}

/// <summary>
/// Checks <c>Autopilot:Gates</c> so a threshold that cannot decide a verdict stops the start-up and never a
/// gate evaluation (02.8.2 plan §3.3).
/// </summary>
/// <remarks>
/// <para>
/// Application cannot reference <c>IValidateOptions</c> (the assembly-reference rule), so each host adapts this
/// to it exactly as it does for <c>AssetRetentionOptionsValidator</c>.
/// </para>
/// <para>
/// Every key has an in-code default, so a host that ships no <c>Autopilot:Gates</c> block still runs — in dry
/// run, which is the safe direction. What this refuses is a value that silently changes every verdict: a score
/// outside the D16 1–5 scale, a similarity ratio outside 0–1, a negative dry-run length, an inverted loudness
/// band, a profile that names no threshold at all (the shape a typo or a 02.8.2-T2-only block produces), and a
/// configured <c>Profiles</c> map without the <c>default</c> entry every other profile is overlaid on.
/// </para>
/// <para>
/// Every message names the configuration key and never echoes the rejected value.
/// </para>
/// </remarks>
public static class AutopilotGateOptionsValidator
{
    /// <summary>The lowest D16 dimension score; the rubric's own floor.</summary>
    public const decimal MinDimensionScore = 1.0m;

    /// <summary>The highest D16 dimension score; the rubric's own ceiling.</summary>
    public const decimal MaxDimensionScore = 5.0m;

    /// <summary>The lowest accepted loudness bound; below it no master of a spoken track is measured.</summary>
    public const double MinLufsBound = -70.0;

    /// <summary>The highest accepted loudness bound; above 0 LUFS a track clips.</summary>
    public const double MaxLufsBound = 0.0;

    /// <summary>Every problem of the section; empty when valid.</summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Validate(AutopilotGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        var section = $"'{AutopilotGateOptions.SectionName}'";

        if (options.DryRunMaxEpisodes < 0)
        {
            problems.Add($"{section}:{nameof(AutopilotGateOptions.DryRunMaxEpisodes)} must not be negative.");
        }

        if (options.Profiles.Count > 0 && !options.Profiles.ContainsKey(AutopilotGateOptions.DefaultProfileName))
        {
            problems.Add(
                $"{section}:{nameof(AutopilotGateOptions.Profiles)} must carry a "
                + $"'{AutopilotGateOptions.DefaultProfileName}' entry: every other profile is an overlay on it.");
        }

        foreach (var (name, profile) in options.Profiles)
        {
            var key = $"{section}:{nameof(AutopilotGateOptions.Profiles)}:{name}";

            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"{section}:{nameof(AutopilotGateOptions.Profiles)} must not carry a blank profile name.");
                continue;
            }

            if (profile is null || profile.IsEmpty)
            {
                problems.Add($"{key} names no threshold; an unknown key would otherwise look configured.");
                continue;
            }

            ValidateStory(problems, key, profile.Story);
            ValidateVideo(problems, key, profile.Video);

            // The band is checked on the resolved profile too: a section that only sets one end inherits the
            // other, so the inversion can appear in the merge even when neither half is wrong on its own.
            var resolved = options.Resolve(name);
            if (resolved.Video.LoudnessMinLufs > resolved.Video.LoudnessMaxLufs)
            {
                problems.Add($"{key} resolves to a loudness band whose lower bound is above its upper bound.");
            }
        }

        return problems;
    }

    private static void ValidateStory(List<string> problems, string key, StoryGateThresholdsSection? story)
    {
        if (story is null)
        {
            return;
        }

        ValidateScore(problems, key, nameof(StoryGateThresholdsSection.MinMeanReviewScore), story.MinMeanReviewScore);
        ValidateScore(problems, key, nameof(StoryGateThresholdsSection.MinDimensionScore), story.MinDimensionScore);
        ValidateRatio(problems, key, nameof(StoryGateThresholdsSection.MaxSourceSimilarity), story.MaxSourceSimilarity);
        ValidateRatio(problems, key, nameof(StoryGateThresholdsSection.MaxPastEpisodeSimilarity), story.MaxPastEpisodeSimilarity);

        if (story.MaxRevisions is { } revisions && revisions < 0)
        {
            problems.Add($"{key}:{nameof(StoryGateThresholdsSection.MaxRevisions)} must not be negative.");
        }

        if (story.AllowedContentWarnings is not { } warnings)
        {
            return;
        }

        if (warnings.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add($"{key}:{nameof(StoryGateThresholdsSection.AllowedContentWarnings)} must not carry a blank entry.");
        }
    }

    private static void ValidateVideo(List<string> problems, string key, VideoGateThresholdsSection? video)
    {
        if (video is null)
        {
            return;
        }

        if (video.DurationTolerance is { } tolerance && tolerance is < 0 or > 1)
        {
            problems.Add($"{key}:{nameof(VideoGateThresholdsSection.DurationTolerance)} must be a ratio between 0 and 1.");
        }

        ValidateLufs(problems, key, nameof(VideoGateThresholdsSection.LoudnessMinLufs), video.LoudnessMinLufs);
        ValidateLufs(problems, key, nameof(VideoGateThresholdsSection.LoudnessMaxLufs), video.LoudnessMaxLufs);
    }

    private static void ValidateScore(List<string> problems, string key, string name, decimal? value)
    {
        if (value is { } score && (score < MinDimensionScore || score > MaxDimensionScore))
        {
            problems.Add(
                $"{key}:{name} must be between {MinDimensionScore} and {MaxDimensionScore} "
                + "(the D16 rubric's own scale).");
        }
    }

    private static void ValidateRatio(List<string> problems, string key, string name, decimal? value)
    {
        if (value is { } ratio && (ratio < 0m || ratio > 1m))
        {
            problems.Add($"{key}:{name} must be a ratio between 0 and 1.");
        }
    }

    private static void ValidateLufs(List<string> problems, string key, string name, double? value)
    {
        if (value is { } lufs && (lufs < MinLufsBound || lufs > MaxLufsBound))
        {
            problems.Add($"{key}:{name} must be between {MinLufsBound} and {MaxLufsBound} LUFS.");
        }
    }
}
