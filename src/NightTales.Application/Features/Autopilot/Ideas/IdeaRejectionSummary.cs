namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The summary of a batch's rejection reasons (02.8.1-T2 plan §4 U2, task Design "rejection reasons"): the one
/// reason a run that picked nothing reports.
/// </summary>
/// <remarks>
/// The answer must be deterministic, because it reaches an alert and a run log: the most common reason wins, and
/// a tie is broken alphabetically. Nothing here sees an idea or its text, only the closed reason codes.
/// </remarks>
public static class IdeaRejectionSummary
{
    /// <summary>
    /// The most common reason of a batch: the highest count, ties broken by the ordinal order of the codes.
    /// </summary>
    /// <param name="reasons">The rejected candidates' reason codes; never empty on the path that calls this.</param>
    /// <returns>
    /// The most common reason, or <see langword="null"/> when <paramref name="reasons"/> is empty (a batch with
    /// no candidate at all — the caller treats that as no idea without a reason).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="reasons"/> is <see langword="null"/>.</exception>
    public static string? MostCommon(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        var common = reasons
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .Select(group => (Reason: group.Key, Count: group.Count()))
            // Highest count first, then the alphabetically first code: the ordering is total, so two runs of the
            // same batch answer the same reason whatever the enumeration order is.
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Reason, StringComparer.Ordinal)
            .FirstOrDefault();

        return common.Reason;
    }
}
