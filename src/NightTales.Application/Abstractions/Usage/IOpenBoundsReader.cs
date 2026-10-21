namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The sum of the worst-case bounds of work that was accepted but has not settled yet (02.2.2-T2, §10 item 9).
/// It is the one extension point of the budget guard: 05 and 12 add the render reservations by changing the
/// reader, not the guard.
/// </summary>
public interface IOpenBoundsReader
{
    /// <summary>The platform-wide total of the open worst-case bounds, in USD.</summary>
    /// <param name="ct">Cancels the query.</param>
    /// <param name="excludingRunId">A generation run left out of the sum (its own reservation), or null to count them all.</param>
    ValueTask<decimal> SumOpenWorstCaseUsdAsync(CancellationToken ct, Guid? excludingRunId = null);
}
