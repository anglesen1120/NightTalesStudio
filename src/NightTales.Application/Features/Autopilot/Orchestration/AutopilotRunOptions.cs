using System.Globalization;
using NightTales.Domain.Series.Autopilot;

namespace NightTales.Application.Features.Autopilot.Orchestration;

/// <summary>
/// The <c>Autopilot:Run</c> section: the planner's horizon and batch, the orchestrator's step budget, the
/// one-producing-run rule and the two placeholder estimates the timing chain needs (02.8.3 plan §3.4, §7.1,
/// task file Steps 8/9).
/// </summary>
/// <remarks>
/// <para>
/// <b>A plain value, bound and validated by the host.</b> Application may not reference
/// <c>Microsoft.Extensions.Configuration</c> or <c>Microsoft.Extensions.Options</c> (assembly-reference rule), so the
/// Worker binds this section, runs <see cref="ApplyPlatformValues"/> through its own configuration indexer, runs
/// <see cref="Validate"/> at start-up and registers the bound value as a singleton — exactly as it does for
/// <c>StoryJobOptions</c> and <c>AutopilotGateOptions</c>. Application services take this type directly.
/// </para>
/// <para>
/// <b>One key per value, no drift.</b> The task file names some of these keys with a dotted subsection
/// (<c>Autopilot:PlanJob:MaxRunsPerTick</c>, <c>Autopilot:RenderEstimate:Minutes</c>,
/// <c>Autopilot:StoryEstimate:Minutes</c>, <c>Autopilot:WaitingOwner:ExpiryHours</c>); both are shipped in
/// <c>appsettings.json</c> under the section as comments. <see cref="ApplyPlatformValues"/> still reads those
/// task-file keys as fall-backs, so a deployment that already set them is not silently ignored — but the
/// canonical <c>Autopilot:Run</c> key always wins, and the two YouTube values are <em>never</em> duplicated
/// here: the lead time and the minimum veto window are read from <c>YouTube:PublishLeadMinutes</c> and
/// <c>YouTube:MinVetoWindowHours</c> (06 CR-3, "one source, no duplicate <c>Autopilot:</c> keys").
/// </para>
/// <para>
/// <b>The two estimates are placeholders.</b> <see cref="RenderEstimateMinutes"/> (15) must be replaced by the
/// 05 render measurement and <see cref="StoryEstimateMinutes"/> (10) by the M1 story p95 before the
/// M1-autopilot gate (F16); they are the fall-backs a series uses when its
/// <c>autopilot_settings.render_lead_minutes</c>/<c>story_lead_minutes</c> is absent.
/// </para>
/// </remarks>
public sealed class AutopilotRunOptions
{
    /// <summary>The configuration section this class binds to.</summary>
    public const string SectionName = "Autopilot:Run";

    /// <summary>The section the two platform timing values come from; 06 owns both keys.</summary>
    public const string YouTubeSectionName = "YouTube";

    /// <summary>The key of <see cref="PublishLeadMinutes"/> inside <see cref="YouTubeSectionName"/>.</summary>
    public const string PublishLeadMinutesKey = "PublishLeadMinutes";

    /// <summary>The key of <see cref="MinVetoWindowHours"/> inside <see cref="YouTubeSectionName"/>.</summary>
    public const string MinVetoWindowHoursKey = "MinVetoWindowHours";

    /// <summary>The shipped <see cref="MaxProducingRunsPerSeries"/> (plan §3.4: one producing run per series).</summary>
    public const int DefaultMaxProducingRunsPerSeries = 1;

    /// <summary>The shipped <see cref="WaitingOwnerExpiryHours"/> (task file §True-crime path: 48 h).</summary>
    public const int DefaultWaitingOwnerExpiryHours = 48;

    /// <summary>The shipped <see cref="StuckRunMinutes"/> (task file Steps 8: 60 min).</summary>
    public const int DefaultStuckRunMinutes = 60;

    /// <summary>The shipped <see cref="MaxStepsPerTick"/>: one run may walk this many steps in one tick.</summary>
    public const int DefaultMaxStepsPerTick = 6;

    /// <summary>The shipped <see cref="PlanJobMaxRunsPerTick"/> (task file Steps 9: 50).</summary>
    public const int DefaultPlanJobMaxRunsPerTick = 50;

    /// <summary>The shipped <see cref="PublicationCreateMarginMinutes"/> (plan §3.4: 10).</summary>
    public const int DefaultPublicationCreateMarginMinutes = 10;

    /// <summary>The shipped <see cref="RenderEstimateMinutes"/> — a placeholder (plan §3.4 ⚠️, F16).</summary>
    public const int DefaultRenderEstimateMinutes = 15;

    /// <summary>The shipped <see cref="StoryEstimateMinutes"/> — a placeholder (plan §3.4 ⚠️, F16).</summary>
    public const int DefaultStoryEstimateMinutes = 10;

    /// <summary>The shipped <see cref="MaxBufferDays"/> (plan §3.4: 7; the settings validator's ceiling).</summary>
    public const int DefaultMaxBufferDays = 7;

    /// <summary>The shipped <see cref="PublishLeadMinutes"/> (<c>YouTubeOptions.PublishLeadMinutes</c>).</summary>
    public const int DefaultPublishLeadMinutes = 120;

    /// <summary>The shipped <see cref="MinVetoWindowHours"/> (<c>YouTubeOptions.MinVetoWindowHours</c>).</summary>
    public const int DefaultMinVetoWindowHours = 2;

    /// <summary>
    /// How many runs of one series may stand in a producing state at once
    /// (<c>Autopilot:MaxProducingRunsPerSeries</c>, 1). The ready buffer is a start-time offset, not
    /// parallelism: a second producing run is a bug, so the orchestrator logs
    /// <c>autopilot_concurrency_violation_total</c> and skips the later run this tick.
    /// </summary>
    public int MaxProducingRunsPerSeries { get; set; } = DefaultMaxProducingRunsPerSeries;

    /// <summary>
    /// How long a run may wait for its owner before it fails with <c>owner_approval_expired</c>
    /// (<c>Autopilot:WaitingOwner:ExpiryHours</c>, 48). Expiry never pauses the series.
    /// </summary>
    public int WaitingOwnerExpiryHours { get; set; } = DefaultWaitingOwnerExpiryHours;

    /// <summary>
    /// How long a producing run may hold one state before the tick logs
    /// <c>autopilot_run_stuck</c> (60). Observability only: the run is re-driven, its state never changes.
    /// </summary>
    public int StuckRunMinutes { get; set; } = DefaultStuckRunMinutes;

    /// <summary>
    /// How many states one run may walk inside one tick, so a catch-up run does not starve the others (6).
    /// </summary>
    public int MaxStepsPerTick { get; set; } = DefaultMaxStepsPerTick;

    /// <summary>
    /// How many due runs one tick advances at all (<c>Autopilot:PlanJob:MaxRunsPerTick</c>, 50). The planner
    /// creates rows regardless; a run past the budget waits for the next tick.
    /// </summary>
    public int PlanJobMaxRunsPerTick { get; set; } = DefaultPlanJobMaxRunsPerTick;

    /// <summary>
    /// The notice-delivery margin before the publication is created (10). Read by the publication step; the
    /// planning side passes it through <see cref="ToTimingOptions"/>.
    /// </summary>
    public int PublicationCreateMarginMinutes { get; set; } = DefaultPublicationCreateMarginMinutes;

    /// <summary>
    /// The render estimate a series without <c>render_lead_minutes</c> plans with (15, placeholder ⚠️).
    /// </summary>
    public int RenderEstimateMinutes { get; set; } = DefaultRenderEstimateMinutes;

    /// <summary>
    /// The story estimate a series without <c>story_lead_minutes</c> plans with (10, placeholder ⚠️).
    /// </summary>
    public int StoryEstimateMinutes { get; set; } = DefaultStoryEstimateMinutes;

    /// <summary>
    /// The largest <c>buffer_days</c> a settings document may state (7): the settings validator's ceiling and
    /// the planner's maximum horizon contribution.
    /// </summary>
    public int MaxBufferDays { get; set; } = DefaultMaxBufferDays;

    /// <summary>
    /// The upload lead time before the slot (<c>YouTube:PublishLeadMinutes</c>, 120). Derived, never a second
    /// <c>Autopilot:</c> key (06 CR-3): <see cref="ApplyPlatformValues"/> reads it from the YouTube section, and
    /// the private setter keeps a stray <c>Autopilot:Run:PublishLeadMinutes</c> from ever binding.
    /// </summary>
    public int PublishLeadMinutes { get; private set; } = DefaultPublishLeadMinutes;

    /// <summary>
    /// The shortest veto window a late publication may get (<c>YouTube:MinVetoWindowHours</c>, 2). Derived like
    /// <see cref="PublishLeadMinutes"/>.
    /// </summary>
    public int MinVetoWindowHours { get; private set; } = DefaultMinVetoWindowHours;

    /// <summary>The Domain's view of these values, which <see cref="AutopilotTiming"/> computes with.</summary>
    /// <returns>The six timing values, all read from this instance.</returns>
    public AutopilotTimingOptions ToTimingOptions() => new(
        PublishLeadMinutes: PublishLeadMinutes,
        MinVetoWindowHours: MinVetoWindowHours,
        PublicationCreateMarginMinutes: PublicationCreateMarginMinutes,
        RenderEstimateMinutes: RenderEstimateMinutes,
        StoryEstimateMinutes: StoryEstimateMinutes,
        MaxBufferDays: MaxBufferDays);

    /// <summary>
    /// Applies the two values this class does not own: the task-file key names of the sections it replaced, and
    /// the two <c>YouTube</c> timing values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run after <c>BindConfiguration(</c><see cref="SectionName"/><c>)</c>: a canonical
    /// <c>Autopilot:Run:*</c> key always wins, and each task-file key is read only when its canonical key is
    /// absent, so a deployment migrating from the task file's names keeps working without two live sources.
    /// </para>
    /// <para>
    /// <c>YouTube:PublishLeadMinutes</c> and <c>YouTube:MinVetoWindowHours</c> are read
    /// <em>unconditionally</em>, because they are 06's keys and have no canonical twin here (06 CR-3). An absent
    /// or unparsable value keeps the shipped default, which is why a Worker that does not bind the YouTube
    /// section still plans with 120/2.
    /// </para>
    /// </remarks>
    /// <param name="options">The instance the binder already filled.</param>
    /// <param name="readValue">
    /// The host's configuration indexer (<c>key =&gt; configuration[key]</c>). Application may not reference
    /// <c>Microsoft.Extensions.Configuration</c>, so the Worker passes its own configuration's indexer in.
    /// </param>
    public static void ApplyPlatformValues(AutopilotRunOptions options, Func<string, string?> readValue)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readValue);

        options.PlanJobMaxRunsPerTick = Alias(readValue, "Autopilot:PlanJob:MaxRunsPerTick",
            $"{SectionName}:{nameof(PlanJobMaxRunsPerTick)}", options.PlanJobMaxRunsPerTick);
        options.MaxProducingRunsPerSeries = Alias(readValue, "Autopilot:MaxProducingRunsPerSeries",
            $"{SectionName}:{nameof(MaxProducingRunsPerSeries)}", options.MaxProducingRunsPerSeries);
        options.PublicationCreateMarginMinutes = Alias(readValue, "Autopilot:PublicationCreateMarginMinutes",
            $"{SectionName}:{nameof(PublicationCreateMarginMinutes)}", options.PublicationCreateMarginMinutes);
        options.MaxBufferDays = Alias(readValue, "Autopilot:MaxBufferDays",
            $"{SectionName}:{nameof(MaxBufferDays)}", options.MaxBufferDays);
        options.WaitingOwnerExpiryHours = Alias(readValue, "Autopilot:WaitingOwner:ExpiryHours",
            $"{SectionName}:{nameof(WaitingOwnerExpiryHours)}", options.WaitingOwnerExpiryHours);
        options.RenderEstimateMinutes = Alias(readValue, "Autopilot:RenderEstimate:Minutes",
            $"{SectionName}:{nameof(RenderEstimateMinutes)}", options.RenderEstimateMinutes);
        options.StoryEstimateMinutes = Alias(readValue, "Autopilot:StoryEstimate:Minutes",
            $"{SectionName}:{nameof(StoryEstimateMinutes)}", options.StoryEstimateMinutes);

        options.PublishLeadMinutes = ReadInt(readValue, $"{YouTubeSectionName}:{PublishLeadMinutesKey}")
            ?? options.PublishLeadMinutes;
        options.MinVetoWindowHours = ReadInt(readValue, $"{YouTubeSectionName}:{MinVetoWindowHoursKey}")
            ?? options.MinVetoWindowHours;
    }

    /// <summary>
    /// Every problem of these values; empty when valid (bound at start-up, so an impossible run policy fails
    /// the boot instead of the first tick). Every message names the configuration key and never echoes the
    /// rejected value.
    /// </summary>
    /// <param name="options">The bound section.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Validate(AutopilotRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        Check(options.MaxProducingRunsPerSeries is >= 1 and <= 10, Key(nameof(MaxProducingRunsPerSeries)), "must be between 1 and 10", problems);
        Check(options.WaitingOwnerExpiryHours is >= 1 and <= 720, Key(nameof(WaitingOwnerExpiryHours)), "must be between 1 and 720 hours", problems);
        Check(options.StuckRunMinutes is >= 1 and <= 1440, Key(nameof(StuckRunMinutes)), "must be between 1 and 1440 minutes", problems);
        Check(options.MaxStepsPerTick is >= 1 and <= 50, Key(nameof(MaxStepsPerTick)), "must be between 1 and 50", problems);
        Check(options.PlanJobMaxRunsPerTick is >= 1 and <= 1000, Key(nameof(PlanJobMaxRunsPerTick)), "must be between 1 and 1000", problems);
        Check(options.PublicationCreateMarginMinutes is >= 0 and <= 1440, Key(nameof(PublicationCreateMarginMinutes)), "must be between 0 and 1440 minutes", problems);
        Check(options.RenderEstimateMinutes is >= 1 and <= 240, Key(nameof(RenderEstimateMinutes)), "must be between 1 and 240 minutes", problems);
        Check(options.StoryEstimateMinutes is >= 1 and <= 240, Key(nameof(StoryEstimateMinutes)), "must be between 1 and 240 minutes", problems);
        Check(options.MaxBufferDays is >= 1 and <= 7, Key(nameof(MaxBufferDays)), "must be between 1 and 7 days", problems);
        // 06's keys, validated here because the timing chain reads them as hours and minutes: a zero lead would
        // make every slot's upload instant equal its publish instant and a zero veto window would remove the
        // owner's.
        Check(options.PublishLeadMinutes is >= 1 and <= 1440, $"{YouTubeSectionName}:{PublishLeadMinutesKey}", "must be between 1 and 1440 minutes", problems);
        Check(options.MinVetoWindowHours is >= 1 and <= 48, $"{YouTubeSectionName}:{MinVetoWindowHoursKey}", "must be between 1 and 48 hours", problems);

        return problems;
    }

    private static string Key(string property) => $"{SectionName}:{property}";

    private static void Check(bool condition, string key, string requirement, List<string> problems)
    {
        if (!condition)
        {
            problems.Add($"'{key}' {requirement}.");
        }
    }

    /// <summary>
    /// The task-file key's value, or <paramref name="canonicalValue"/> when either the alias or the canonical
    /// key is absent — the canonical key wins whenever it is present.
    /// </summary>
    private static int Alias(Func<string, string?> readValue, string aliasKey, string canonicalKey, int canonicalValue)
    {
        if (readValue(canonicalKey) is not null)
        {
            return canonicalValue;
        }

        return ReadInt(readValue, aliasKey) ?? canonicalValue;
    }

    private static int? ReadInt(Func<string, string?> readValue, string key) =>
        int.TryParse(readValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
