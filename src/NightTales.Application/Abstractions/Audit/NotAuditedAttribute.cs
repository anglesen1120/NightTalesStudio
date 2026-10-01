namespace NightTales.Application.Abstractions.Audit;

/// <summary>
/// Declares that this command writes <em>no</em> row through the pipeline's <c>AuditBehavior</c>, and
/// gives the reviewable reason (09 README BD15, D4).
/// </summary>
/// <remarks>
/// <para>
/// Every command implements either this attribute or <see cref="AuditedAttribute"/>; the architecture
/// test fails the build when one carries neither, or both. The reason is not decoration: it is what
/// makes "exempt because a handler writes its own row" reviewable, so it must be a sentence a reviewer
/// can check, not "n/a".
/// </para>
/// <para>
/// Legitimate reasons: the command is a job-only follow-up whose row the originating command already
/// wrote; the handler writes its own security row through <c>IAuditWriter</c> (the login family, where
/// the caller's IP and <c>User-Agent</c> are part of the row); the command changes nothing durable.
/// </para>
/// </remarks>
/// <param name="reason">Why the command writes no audit row; non-empty and at most <see cref="ReasonMaxLength"/>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotAuditedAttribute(string reason) : Attribute
{
    /// <summary>The longest accepted <see cref="Reason"/>; the architecture test enforces it.</summary>
    public const int ReasonMaxLength = 200;

    /// <summary>Why the command writes no audit row — one sentence, written for the reviewer.</summary>
    public string Reason { get; } = reason;
}
