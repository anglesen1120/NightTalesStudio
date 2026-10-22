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
    /// The authorization row the handler revoked with <c>disabled</c> while archiving, or <see langword="null"/>
    /// when the series had no live one (02.8.3-T2 plan §3.7: archiving disables the autopilot, so its consent goes
    /// with it).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? RevokedAuthorizationId { get; set; }

    /// <summary>
    /// The projection of this command's audit row (09 README BD15): the archived series, the caller as
    /// the subject, and no summary — <c>series.archived</c> says everything that changed.
    /// </summary>
    /// <remarks>
    /// One row, never two: <c>autopilot.disabled</c> has no row of its own here, because the archive's row is the
    /// record of the same transaction and BD15 is one row per command. A revoked consent is the one fact the action
    /// name does not carry, so it travels as the summary's id when it happened.
    /// </remarks>
    /// <returns>The projection of this command's audit row.</returns>
    public AuditData ToAuditData() => RevokedAuthorizationId is { } authorizationId
        ? new(
            null,
            "series",
            Id,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["authorizationId"] = authorizationId.ToString(),
                ["reason"] = "disabled",
            })
        : new(null, "series", Id, AuditData.NoSummary);
}
