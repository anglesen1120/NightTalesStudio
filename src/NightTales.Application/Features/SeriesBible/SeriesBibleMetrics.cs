using System.Diagnostics.Metrics;

namespace NightTales.Application.Features.SeriesBible;

/// <summary>
/// The series bible's instruments (02.6.3-T1, plan §4 U1 failure table): the one counter a missing row
/// raises, and (02.6.3-T2, plan §7.1 step 5) the one the proposal job raises per dropped model op. The
/// instrument names are frozen for the 11.6.2 dashboards, and no tag carries an id or any content (D19):
/// the drop tag names the op kind and the closed reason code, never the op's text.
/// </summary>
public sealed class SeriesBibleMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "NightTales.SeriesBible";

    /// <summary>A read or a write that found no <c>series_bibles</c> row for an existing series.</summary>
    public const string MissingTotalName = "bible_missing_total";

    /// <summary>One model op of a proposal that survived no rule of <c>BibleProposalValidator</c>.</summary>
    public const string OpsDroppedTotalName = "bible_proposal_ops_dropped_total";

    /// <summary>The tag naming the dropped candidate's kind (<c>add_character</c>, &#8230;, or <c>unknown</c>).</summary>
    public const string KindTagName = "kind";

    /// <summary>The tag naming the closed reason code the validator dropped it with.</summary>
    public const string ReasonTagName = "reason";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _missing;
    private readonly Counter<long> _opsDropped;

    /// <summary>Creates the instruments.</summary>
    public SeriesBibleMetrics()
    {
        _missing = _meter.CreateCounter<long>(
            MissingTotalName,
            "1",
            "An existing series whose series_bibles row is missing (data drift: the create hook did not land or a row was deleted).");

        _opsDropped = _meter.CreateCounter<long>(
            OpsDroppedTotalName,
            "1",
            "A bible-proposal model op that no rule accepted, tagged with its kind and its closed drop reason (plan §7.1 step 5).");
    }

    /// <summary>Counts one missing-row alert: a series exists but has no bible row.</summary>
    public void Missing() => _missing.Add(1);

    /// <summary>
    /// Counts the ops one post-processing pass dropped, one add per group, so a batch of ten drops of one
    /// reason is one instrument update rather than ten.
    /// </summary>
    /// <param name="kind">The dropped candidates' kind, snake_case, or <c>unknown</c>.</param>
    /// <param name="reason">The closed reason code the validator dropped them with.</param>
    /// <param name="count">How many candidates the pair covers; must be positive.</param>
    public void OpsDropped(string kind, string reason, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _opsDropped.Add(count, new KeyValuePair<string, object?>(KindTagName, kind), new KeyValuePair<string, object?>(ReasonTagName, reason));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
