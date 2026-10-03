using ErrorOr;

namespace NightTales.Application.Abstractions.Usage;

/// <summary>
/// The platform spend cap (02 README D1, 02.2.2-T2): refuses a paid unit of work when what was already spent
/// today and this month, plus the open worst-case bounds, plus this unit's worst case would pass the cap.
/// </summary>
public interface IBudgetGuard
{
    /// <summary>
    /// Checks the daily and the monthly cap. Called from a command, it runs inside the command's transaction
    /// after a platform-wide advisory lock, so two different episodes cannot both pass the check and together
    /// exceed the cap; the lock is held until the run row commits.
    /// </summary>
    /// <param name="worstCaseUsd">The worst case of the unit of work about to be queued.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <returns>Success, or <c>spend_cap_reached</c>.</returns>
    ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct);
}
