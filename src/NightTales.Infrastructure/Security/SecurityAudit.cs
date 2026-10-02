using System.Net;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence.Entities;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The audit-row builder the security handlers share (09 README BD15, plan §3): an
/// <c>account_security</c> row with the caller's address and <c>User-Agent</c>, and nothing else.
/// </summary>
/// <remarks>
/// The two request fields are filled only here, on security events, which is why the ordinary commands'
/// rows leave them null: only an auth handler owns a security row (plan §3). The <c>data</c> payload is
/// safe text only (BD16) — the short detail this codebase passes, never an address, a token or a secret.
/// </remarks>
internal static class SecurityAudit
{
    /// <summary>
    /// The web-serializer defaults of a hand-written <c>data</c> payload: camelCase keys, like the
    /// pipeline's projections. Built once, because the CA1869 rule is right — a fresh options instance per
    /// row would allocate a new serializer cache every time.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions PayloadOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>One security row of <paramref name="userId"/>.</summary>
    /// <param name="action">A <c>SecurityAuditActions</c> value.</param>
    /// <param name="userId">The account the row is about.</param>
    /// <param name="info">The caller metadata the row records.</param>
    /// <param name="visibility">Who may read the row.</param>
    /// <param name="detail">The short non-identifying description, or null.</param>
    /// <param name="now">The scope's clock reading.</param>
    internal static AuditEntry Entry(
        string action,
        Guid userId,
        RequestInfo info,
        Visibility visibility,
        string? detail,
        DateTimeOffset now) =>
        Row(action, userId, info, visibility, Payload(detail), now);

    /// <summary>
    /// One security row whose <c>data</c> carries the named values the Design freezes
    /// (<c>{ attempts }</c> on an MFA failure, <c>{ method }</c> on a completed login, the two address
    /// hashes of an IP change).
    /// </summary>
    /// <remarks>
    /// The keys are serialized camelCase, like every other hand-written payload. A value is a bounded
    /// scalar or a short label from this codebase's own vocabulary — the same rule the plain
    /// <see cref="Entry(string, Guid, RequestInfo, Visibility, string?, DateTimeOffset)"/> overload
    /// enforces by construction (BD16: no token, no address, no secret).
    /// </remarks>
    /// <param name="action">A <c>SecurityAuditActions</c> value.</param>
    /// <param name="userId">The account the row is about.</param>
    /// <param name="info">The caller metadata the row records.</param>
    /// <param name="visibility">Who may read the row.</param>
    /// <param name="payload">The <c>data</c> members; empty writes <c>{}</c>.</param>
    /// <param name="now">The scope's clock reading.</param>
    internal static AuditEntry EntryWithData(
        string action,
        Guid userId,
        RequestInfo info,
        Visibility visibility,
        IReadOnlyDictionary<string, object?> payload,
        DateTimeOffset now) =>
        Row(action, userId, info, visibility, Payload(payload), now);

    /// <summary>The row both overloads build; only the <c>data</c> text differs.</summary>
    private static AuditEntry Row(
        string action,
        Guid userId,
        RequestInfo info,
        Visibility visibility,
        string dataJson,
        DateTimeOffset now) =>
        new(
            ActorUserId: userId,
            ActorType: ActorType.User,
            SubjectUserId: userId,
            Action: action,
            Category: Category.AccountSecurity,
            Visibility: visibility,
            EntityType: null,
            EntityId: null,
            DataJson: dataJson,
            Ip: ParseIp(info.Ip),
            UserAgent: Truncate(info.UserAgent),
            ActorLabel: null,
            OccurredAt: now);

    /// <summary>The caller's address, or null when it is absent or unparsable.</summary>
    private static IPAddress? ParseIp(string? ip) =>
        !string.IsNullOrWhiteSpace(ip) && IPAddress.TryParse(ip, out var parsed) ? parsed : null;

    /// <summary>
    /// The caller's <c>User-Agent</c>, bounded like the refresh rows' copy: the column is unbounded
    /// <c>text</c>, but a client header must not be able to grow a row without limit.
    /// </summary>
    private static string? Truncate(string? userAgent) =>
        string.IsNullOrEmpty(userAgent) ? null
        : userAgent.Length <= RefreshToken.UserAgentMaxLength ? userAgent
        : userAgent[..RefreshToken.UserAgentMaxLength];

    /// <summary>The <c>data</c> payload of a hand-written security row.</summary>
    private static string Payload(string? detail) =>
        detail is null
            ? "{}"
            : System.Text.Json.JsonSerializer.Serialize(new SecurityAuditData(detail), PayloadOptions);

    /// <summary>The <c>data</c> payload of the named values the Design fixes; camelCase like the rest.</summary>
    private static string Payload(IReadOnlyDictionary<string, object?> payload) =>
        payload.Count == 0
            ? "{}"
            : System.Text.Json.JsonSerializer.Serialize(payload, PayloadOptions);

    /// <summary>The row's payload; safe text only (09 README BD16).</summary>
    /// <param name="Detail">The short, non-identifying description of the event.</param>
    private sealed record SecurityAuditData(string Detail);
}
