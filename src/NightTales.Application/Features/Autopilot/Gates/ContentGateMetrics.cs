using System.Diagnostics.Metrics;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Gates;

/// <summary>
/// The content gate's instrument (02.8.2-T2 plan §"Failure handling"): one count per flagged category per failing
/// chunk, which is how T3's calibration sees which category actually stops stories.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own counter, on the shared meter.</b> The idea pre-check already publishes an instrument named
/// <c>moderation_flags_total</c> on the autopilot meter with a <c>stage</c> tag and no category vocabulary
/// (<see cref="IdeaMetrics.ModerationFlagsTotalName"/>). Two instruments of one name on one meter would fork the
/// series an exporter sees, so the gate publishes <c>autopilot_content_gate_flags_total</c> beside it, on the same
/// meter, with the <c>stage</c> and <c>category</c> tags the run-log dashboard needs.
/// </para>
/// <para>
/// <b>Bounded tags only.</b> The stage is the closed vocabulary's <c>autopilot_gate</c> wire name and a category
/// outside the omni table (plus the port's <c>unclassified</c> sentinel) is counted as
/// <see cref="OtherTagValue"/> — a provider that renamed a category must not create an unbounded series (D19).
/// </para>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one counter
/// (the <c>StorageMetrics</c> rule).
/// </para>
/// </remarks>
public sealed class ContentGateMetrics : IDisposable
{
    /// <summary>The meter the gate instruments publish on, shared with the rest of the autopilot feature.</summary>
    public const string MeterName = IdeaMetrics.MeterName;

    /// <summary>One flagged category of one failing chunk.</summary>
    public const string FlagsTotalName = "autopilot_content_gate_flags_total";

    /// <summary>The tag naming the moderation stage, always <c>autopilot_gate</c> for this counter.</summary>
    public const string StageTagName = "stage";

    /// <summary>The tag naming the flagged category.</summary>
    public const string CategoryTagName = "category";

    /// <summary>The tag value every category outside the closed vocabulary is counted under.</summary>
    public const string OtherTagValue = "other";

    /// <summary>The <see cref="StageTagName"/> value of this gate's checks.</summary>
    public static readonly string AutopilotGateStage = ModerationStageNames.ToWireName(ModerationStage.AutopilotGate);

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _flags;

    /// <summary>Creates the counter.</summary>
    public ContentGateMetrics()
    {
        _flags = _meter.CreateCounter<long>(
            FlagsTotalName,
            "1",
            "A flagged moderation category of one failing content-gate chunk, by stage and category.");
    }

    /// <summary>Counts one flagged category of one failing chunk.</summary>
    /// <param name="category">
    /// The flagged category; a value outside the omni table is counted as <see cref="OtherTagValue"/>, and so is a
    /// null or blank one — the input is provider vocabulary, and the counter never throws on external data.
    /// </param>
    public void Flagged(string? category)
    {
        _flags.Add(
            1,
            new KeyValuePair<string, object?>(StageTagName, AutopilotGateStage),
            new KeyValuePair<string, object?>(CategoryTagName, Known(category)));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static string Known(string? category) =>
        category is not null
        && (ModerationOptions.KnownCategories.Contains(category) || string.Equals(category, ModerationOptions.UnclassifiedCategory, StringComparison.Ordinal))
            ? category
            : OtherTagValue;
}
