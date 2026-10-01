using System.Collections.ObjectModel;

namespace NightTales.Application.Abstractions.Audit;

/// <summary>
/// A command that projects the <c>data</c> payload of its own audit row (09 README BD15): the
/// <c>AuditBehavior</c> calls it only after the handler succeeded, and the projection — never the
/// request body — is the sole source of the row's <c>data</c>.
/// </summary>
/// <remarks>
/// A command carrying <see cref="AuditedAttribute"/> but no <see cref="IAuditData"/> is a bug the
/// behaviour cannot report (it simply writes nothing), so the projection belongs to the same change as
/// the attribute.
/// </remarks>
public interface IAuditData
{
    /// <summary>Projects the ids and the compact summary the audit row carries.</summary>
    /// <returns>The projection; its summary must be safe text only (09 README BD16).</returns>
    AuditData ToAuditData();
}

/// <summary>
/// The <c>data</c> of one audit row (09 README BD15, plan §3): the affected account, the entity the
/// change concerns, and a compact summary of what changed.
/// </summary>
/// <param name="SubjectUserId">
/// The account the row is about, or <c>null</c> for a self-service command — the behaviour then records
/// the authenticated caller as the subject, which is what keeps a <c>user</c>-visible row subject-bound —
/// and for a system event.
/// </param>
/// <param name="EntityType">The changed entity's kind (<c>series</c>), or <c>null</c>.</param>
/// <param name="EntityId">The changed entity's id, or <c>null</c>.</param>
/// <param name="Summary">
/// The changed fields as short, safe key/value pairs (a field name to its new code or value). Never story
/// text, an e-mail address, a token or a secret (09 README BD16); an empty dictionary is a valid summary.
/// </param>
public sealed record AuditData(
    Guid? SubjectUserId,
    string? EntityType,
    Guid? EntityId,
    IReadOnlyDictionary<string, string> Summary)
{
    /// <summary>
    /// The summary of a command whose action name already says everything that changed (a delete, an
    /// archive): an empty object, shared by every such row so the projection allocates nothing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NoSummary =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
}
