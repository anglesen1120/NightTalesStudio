using System.Net;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Audit;

/// <summary>
/// One audit row as the writers build it (09 README BD15, plan §3): the pipeline's <c>AuditBehavior</c>
/// builds it from the command's <see cref="AuditedAttribute"/> and <see cref="IAuditData"/> projection,
/// and the auth handlers, jobs and CLI verbs build it by hand.
/// </summary>
/// <param name="ActorUserId">The actor, or <c>null</c> for a pure system row.</param>
/// <param name="ActorType">How to read the actor (09 README D10).</param>
/// <param name="SubjectUserId">The affected account, or <c>null</c> when there is none.</param>
/// <param name="Action">The frozen wire <c>messageKey</c>; for a command, its attribute's action.</param>
/// <param name="Category">The area of the change.</param>
/// <param name="Visibility">Who may read the row.</param>
/// <param name="EntityType">The changed entity's kind, or <c>null</c>.</param>
/// <param name="EntityId">The changed entity's id, or <c>null</c>.</param>
/// <param name="DataJson">
/// The <c>data</c> payload as JSON text. The pipeline writes the command's projection through the
/// compact serializer; a hand-written writer passes its own. Safe text only (09 README BD16).
/// </param>
/// <param name="Ip">
/// The caller's address for a security event. Ordinary commands leave it <c>null</c>: only the auth
/// handlers fill it, because only they own a security row (plan §3).
/// </param>
/// <param name="UserAgent">The caller's <c>User-Agent</c> for a security event; <c>null</c> otherwise.</param>
/// <param name="ActorLabel">
/// The operator label persisted into <c>data.actor</c> (09 README BD13), e.g. <c>system:operator</c> for
/// the CLI verbs; <c>null</c> for everything else, which leaves <c>data</c> exactly as projected.
/// </param>
/// <param name="OccurredAt">The instant of the change, from the scope's clock; also the row's <c>created_at</c>.</param>
public sealed record AuditEntry(
    Guid? ActorUserId,
    ActorType ActorType,
    Guid? SubjectUserId,
    string Action,
    Category Category,
    Visibility Visibility,
    string? EntityType,
    Guid? EntityId,
    string DataJson,
    IPAddress? Ip,
    string? UserAgent,
    string? ActorLabel,
    DateTimeOffset OccurredAt);
