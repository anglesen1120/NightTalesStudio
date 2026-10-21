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
    /// <summary>
    /// Projects the ids and the compact summary the audit row carries. A <see langword="null"/> return
    /// writes no row: a successful command that changed nothing (09.3.4-T1's no-op PATCH) has nothing to
    /// record.
    /// </summary>
    /// <returns>The projection, or <see langword="null"/> when the command changed nothing.</returns>
    AuditData? ToAuditData();
}

/// <summary>
/// An <see cref="IAuditData"/> command whose audit row also needs a value only the handler knows — the id and
/// status of the row it created (02.5.1-T1 Design step 8, 02 QA-2). <c>AuditBehavior</c> calls
/// <see cref="ToAuditData(TResponse)"/> instead of <see cref="IAuditData.ToAuditData"/> after a successful handler;
/// the parameterless projection stays as the shape that holds without a response.
/// </summary>
/// <typeparam name="TResponse">The command's response type (an <c>ErrorOr&lt;T&gt;</c>), a success here.</typeparam>
public interface IAuditDataFromResponse<in TResponse> : IAuditData
{
    /// <summary>
    /// Projects the audit row from the command and its successful response. The same rules as
    /// <see cref="IAuditData.ToAuditData"/> apply: ids and codes only, never user text (BD16).
    /// </summary>
    /// <param name="response">The handler's successful response.</param>
    /// <returns>The projection, or <see langword="null"/> when the command changed nothing.</returns>
    AuditData? ToAuditData(TResponse response);
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
/// The changed fields as short, safe values: a string, a string list or a nested string dictionary when the
/// action's documented shape is structured (09.3.4: <c>{"fields":["uiLocale"],"uiLocale":{"from":"en","to":"vi"}}</c>).
/// Never story text, an e-mail address, a token or a secret (09 README BD16); an empty dictionary is a valid
/// summary.
/// </param>
public sealed record AuditData(
    Guid? SubjectUserId,
    string? EntityType,
    Guid? EntityId,
    IReadOnlyDictionary<string, object> Summary)
{
    /// <summary>
    /// The summary of a command whose action name already says everything that changed (a delete, an
    /// archive): an empty object, shared by every such row so the projection allocates nothing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, object> NoSummary =
        new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(StringComparer.Ordinal));
}
