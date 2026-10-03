using ErrorOr;
using NightTales.Domain.Series;

namespace NightTales.Application.Abstractions.Research;

/// <summary>
/// The true-crime guard (02 README D21, CR-24): a true-crime story needs an approved fact sheet. Declared by
/// 02.2.2-T2, implemented and registered by 02.5.5-T1.
/// </summary>
public interface IFactSheetGuard
{
    /// <summary>Checks the episode's approved fact sheet for the series topic.</summary>
    /// <param name="episodeId">The episode the story is for.</param>
    /// <param name="topic">The series topic; fiction topics need no sheet.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>
    /// The approved sheet (<see cref="ApprovedFactSheet.None"/> for a fiction episode without one), or
    /// <c>fact_sheet_required</c> when a true-crime episode has no approved sheet.
    /// </returns>
    ValueTask<ErrorOr<ApprovedFactSheet>> EnsureApprovedAsync(Guid episodeId, Topic topic, CancellationToken ct);
}
