using System.Diagnostics.Metrics;
using System.Text.Json;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The idea steps' instruments (02.8.1-T1 plan §7.1 step 7, §11 monitoring): the one counter of what happened to
/// each candidate a model batch carried. Tags carry bounded values only — the candidate's outcome, never an id,
/// a title or any text (D19).
/// </summary>
/// <remarks>
/// <para>
/// The meter is the autopilot feature's (<c>NightTales.Autopilot</c>), shared with the generator's own instrument
/// <c>idea_generation_duration_seconds</c> — the split <c>SeriesBibleMetrics</c> (Application) and
/// <c>OpenAiBibleProposalMetrics</c> (Infrastructure) already use, so one dashboard selects the meter and finds
/// both.
/// </para>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one counter
/// (the <c>StorageMetrics</c> rule).
/// </para>
/// </remarks>
public sealed class IdeaMetrics : IDisposable
{
    /// <summary>The meter the idea steps publish on.</summary>
    public const string MeterName = "NightTales.Autopilot";

    /// <summary>One candidate of a model batch, by outcome.</summary>
    public const string CandidatesTotalName = "idea_candidates_total";

    /// <summary>The tag naming the candidate's outcome: a status wire value or a rejection reason.</summary>
    public const string OutcomeTagName = "outcome";

    /// <summary>
    /// The outcome of a candidate the run persisted, in the <c>proposed</c> status (02.8.1-T1: every survivor of
    /// the validity and duplicate rules).
    /// </summary>
    public static readonly string ProposedOutcome = JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(IdeaStatus.Proposed));

    /// <summary>The outcome of the candidate the run answered as its pick.</summary>
    public static readonly string PickedOutcome = JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(IdeaStatus.Picked));

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _candidates;

    /// <summary>Creates the instruments.</summary>
    public IdeaMetrics() =>
        _candidates = _meter.CreateCounter<long>(
            CandidatesTotalName,
            "1",
            "A candidate of one idea batch, tagged with its outcome: the status it was stored with, or the reason it was dropped.");

    /// <summary>
    /// Counts the candidates of one outcome in one add, so a batch that drops five candidates for the same reason
    /// is one instrument update rather than five.
    /// </summary>
    /// <param name="outcome">
    /// The closed outcome vocabulary: <see cref="ProposedOutcome"/>, <see cref="PickedOutcome"/>,
    /// <c>IdeaRejectionReason.Invalid</c> (which also covers the candidates beyond <c>MaxCount</c> the failure
    /// table drops before validation) or <c>IdeaRejectionReason.Duplicate</c>. 02.8.1-T2 adds the remaining
    /// <see cref="IdeaRejectionReason"/> values as it rejects candidates.
    /// </param>
    /// <param name="count">How many candidates the outcome covers; must be positive.</param>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public void Candidates(string outcome, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _candidates.Add(count, new KeyValuePair<string, object?>(OutcomeTagName, outcome));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
