using System.Diagnostics.Metrics;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The one instrument the bible-proposal step itself raises (02.6.3-T2 plan §7.1 step 3): a proposal prompt
/// sent with a body the step had to cut. The other <c>bible_proposal_*</c> counters belong to the job
/// (<c>SeriesBibleMetrics</c>, 02.6.3-T2's C2 slice), which never sees this one.
/// </summary>
/// <remarks>
/// <para>
/// The instrument name is frozen for the 11.6.2 dashboards and carries no tag, no id and no content (D19).
/// Exported by 09.9.3-T1 like every other meter of the platform (<c>AddMeter("NightTales*")</c>): it publishes
/// on the <c>SeriesBible</c> feature meter beside <c>SeriesBibleMetrics</c>, so a dashboard selects the meter
/// and finds both instruments.
/// </para>
/// <para>
/// Registered as a singleton: one instrument per process, so a scrape never sees two series for one counter
/// (the <c>StorageMetrics</c> rule).
/// </para>
/// </remarks>
public sealed class OpenAiBibleProposalMetrics : IDisposable
{
    /// <summary>The meter the bible feature publishes on.</summary>
    public const string MeterName = "NightTales.SeriesBible";

    /// <summary>An approved story body longer than the prompt ceiling, sent truncated.</summary>
    public const string BodyTruncatedTotalName = "bible_proposal_body_truncated_total";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _bodyTruncated;

    /// <summary>Creates the instruments.</summary>
    public OpenAiBibleProposalMetrics() =>
        _bodyTruncated = _meter.CreateCounter<long>(
            BodyTruncatedTotalName,
            "1",
            "An approved story body longer than Bible:Proposal:MaxStoryChars, sent truncated to its opening (plan §7.1 step 3).");

    /// <summary>Counts one proposal prompt whose body was truncated.</summary>
    public void BodyTruncated() => _bodyTruncated.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
