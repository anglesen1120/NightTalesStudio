using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Series.Commands.ArchiveSeries;

/// <summary>
/// <c>DELETE /api/v1/series/{id}</c> (09.4.1 plan §4 U3): archive, never hard-delete. No
/// <c>If-Match</c>: the operation is idempotent, so a repeated call answers 204 again instead of 412
/// (the plan lists no 412 for this route).
/// </summary>
/// <param name="Id">The series to archive; another user's id answers 404, like a missing one.</param>
[Audited("series.archived", Category.Episodes, Visibility.User)]
public sealed record ArchiveSeriesCommand(Guid Id) : ICommand<ErrorOr<Success>>, IAuditData
{
    /// <summary>
    /// The projection of this command's audit row (09 README BD15): the archived series, the caller as
    /// the subject, and no summary — <c>series.archived</c> says everything that changed.
    /// </summary>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => new(null, "series", Id, AuditData.NoSummary);
}
