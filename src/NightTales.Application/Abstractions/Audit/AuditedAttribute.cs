using NightTales.Domain.Audit;

namespace NightTales.Application.Abstractions.Audit;

/// <summary>
/// Declares that this command writes exactly one <c>audit_logs</c> row when it succeeds, through the
/// pipeline's <c>AuditBehavior</c> in slot 6 — inside the UnitOfWork transaction (09 README BD15, D4).
/// </summary>
/// <remarks>
/// <para>
/// Every command implements either this attribute or <see cref="NotAuditedAttribute"/>; the architecture
/// test fails the build when one carries neither, or both. All three arguments are required: the
/// pre-BD15 attribute had no visibility, so dropping it is a silent behaviour change and is a compile
/// error instead.
/// </para>
/// <para>
/// <see cref="Action"/> is the frozen wire <c>messageKey</c> of the change and the key of 09.3.6-T2's
/// per-<c>action</c> projection allow-list: once released, renaming it breaks the activity feed and the
/// <c>activity.&lt;action&gt;</c> localisations, so it must never change.
/// </para>
/// <para>
/// The behaviour writes the row only when the command also projects an <see cref="IAuditData"/>: the
/// projection, not the request, is the sole source of the <c>data</c> payload (09 README BD16).
/// </para>
/// </remarks>
/// <param name="action">The frozen wire <c>messageKey</c>, e.g. <c>episode.story_approved</c>.</param>
/// <param name="category">The area of the change.</param>
/// <param name="visibility">Who may read the row: the owner's feed or staff only.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuditedAttribute(string action, Category category, Visibility visibility) : Attribute
{
    /// <summary>
    /// The pattern <see cref="Action"/> must match: a lowercase domain word, a dot, and one or more
    /// lowercase words joined by underscores (<c>episode.story_approved</c>). The architecture test
    /// enforces it, so a bare <c>story_approved</c> or a camelCase action fails the build.
    /// </summary>
    public const string ActionPattern = @"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$";

    /// <summary>The frozen wire <c>messageKey</c> of the change (09 README BD15/BD16).</summary>
    public string Action { get; } = action;

    /// <summary>The area of the change, stored snake_case in <c>audit_logs.category</c>.</summary>
    public Category Category { get; } = category;

    /// <summary>Who may read the row, stored snake_case in <c>audit_logs.visibility</c>.</summary>
    public Visibility Visibility { get; } = visibility;
}
