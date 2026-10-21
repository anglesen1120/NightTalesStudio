using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The P implementation of <see cref="ISecurityAlerter"/> (09.10.4-T1 Design, [RULED — README §B10 WT25]):
/// one <c>account_security</c> row with <c>visibility = internal</c> plus one redacted structured log
/// line.
/// </summary>
/// <remarks>
/// <para>
/// The row joins the caller's transaction through <c>IAuditWriter</c> (09 README D5), so an alert is
/// never raised for a change that rolled back. The owner e-mail fan-out and the 11.6.2 pipeline are S:
/// they attach here and key on <see cref="SecurityEvent.Kind"/>.
/// </para>
/// <para>
/// The log carries the kind and the subject's id only — no detail, no address, no secret (09 README
/// BD16). The detail is not lost: it is in the audit row's <c>data</c>, which staff can read.
/// </para>
/// </remarks>
/// <param name="audit">The writer that adds the row to the ambient transaction.</param>
/// <param name="clock">The scope's clock reading, which stamps the row.</param>
/// <param name="logger">The redacted structured log of the event.</param>
public sealed partial class AuditAndLogSecurityAlerter(
    IAuditWriter audit,
    IClock clock,
    ILogger<AuditAndLogSecurityAlerter> logger) : ISecurityAlerter
{
    /// <summary>The web-serializer defaults, so the payload's keys are camelCase like every other <c>data</c>.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task AlertAsync(SecurityEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await audit
            .WriteAsync(
                new AuditEntry(
                    ActorUserId: null,
                    ActorType: ActorType.System,
                    SubjectUserId: evt.SubjectUserId,
                    Action: evt.Kind,
                    Category: Category.AccountSecurity,
                    Visibility: Visibility.Internal,
                    EntityType: null,
                    EntityId: null,
                    DataJson: Payload(evt.Detail),
                    Ip: null,
                    UserAgent: null,
                    ActorLabel: null,
                    OccurredAt: clock.UtcNow),
                ct)
            .ConfigureAwait(false);

        LogSecurityEvent(evt.Kind, evt.SubjectUserId);
    }

    /// <summary>The <c>data</c> payload: the short detail, or an empty object when there is none.</summary>
    private static string Payload(string? detail) =>
        detail is null ? "{}" : JsonSerializer.Serialize(new SecurityEventData(detail), SerializerOptions);

    [LoggerMessage(EventId = 2400, Level = LogLevel.Warning, Message = "security.alert: kind={Kind} subject={SubjectUserId}")]
    private partial void LogSecurityEvent(string kind, Guid? subjectUserId);

    /// <summary>The row's payload; safe text only (09 README BD16).</summary>
    /// <param name="Detail">The short, non-identifying description of the event.</param>
    private sealed record SecurityEventData(string Detail);
}
