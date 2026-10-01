using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Domain.Audit;

namespace NightTales.Api.IntegrationTests.Audit;

/// <summary>What the probe handler does inside the command's transaction.</summary>
internal enum AuditProbeMode
{
    /// <summary>Succeeds, so the audit row commits with the change.</summary>
    Success,

    /// <summary>Returns a conflict result, so the UnitOfWork rolls the transaction back.</summary>
    Conflict,

    /// <summary>
    /// Inserts a row the table's own <c>CHECK</c> refuses (<c>visibility = 'user'</c> with no subject), so
    /// the commit's <c>SaveChangesAsync</c> throws and everything added in the transaction is rolled back.
    /// </summary>
    InvalidInsert,
}

/// <summary>
/// The command the audit integration probes send (09.3.6-T1 Tests table): a real command shape that is
/// <c>[Audited]</c>, projects its <c>data</c>, and carries an <c>Idempotency-Key</c> so a replay can be
/// proven not to duplicate the row.
/// </summary>
/// <param name="Body">
/// Distinguishes the rows of the tests that share the class database; the projection stores it as the
/// <c>data.probe</c> value.
/// </param>
/// <param name="IdempotencyKey">The client key; null runs the command once and stores nothing.</param>
/// <param name="Mode">What the handler does before the transaction commits.</param>
[Audited("probe.audited", Category.Episodes, Visibility.User)]
internal sealed record AuditProbeCommand(
    string Body = "body",
    string? IdempotencyKey = null,
    AuditProbeMode Mode = AuditProbeMode.Success)
    : ICommand<ErrorOr<Guid>>, IIdempotentCommand, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(
        null,
        "probe",
        null,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["probe"] = Body });
}
