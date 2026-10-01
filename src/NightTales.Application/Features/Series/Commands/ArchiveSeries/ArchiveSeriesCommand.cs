using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Series.Commands.ArchiveSeries;

/// <summary>
/// <c>DELETE /api/v1/series/{id}</c> (09.4.1 plan §4 U3): archive, never hard-delete. No
/// <c>If-Match</c>: the operation is idempotent, so a repeated call answers 204 again instead of 412
/// (the plan lists no 412 for this route).
/// </summary>
/// <param name="Id">The series to archive; another user's id answers 404, like a missing one.</param>
public sealed record ArchiveSeriesCommand(Guid Id) : ICommand<ErrorOr<Success>>;
