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
    /// <param name="worstCaseUsd">The worst case of the unit of work about to be queued or dispatched.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <param name="excludingRunId">
    /// A generation run whose own open bound must not count: the job checks each paid dispatch of a run that was already
    /// admitted, and that run's worst case is reserved in the open bounds since admission. The check is then
    /// <c>spent + open bounds of the other runs + this step's bound</c>. Null (the command's admission check) counts every open run.
    /// </param>
    /// <returns>Success, or <c>spend_cap_reached</c>.</returns>
    ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct, Guid? excludingRunId = null);
}
