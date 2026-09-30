namespace NightTales.Infrastructure.Persistence.Migrations;

/// <summary>
/// Marks a migration as the contract step of NFR-09-6 (expand → migrate → contract). Such a migration
/// drops a table or a column, or narrows one, and it may do so only one release after the expand
/// migration that stopped writing the old shape.
/// </summary>
/// <remarks>
/// The migration check of 09.2.2-T2 fails any migration whose <c>Up</c> carries a destructive
/// operation without this attribute, so a drop can never reach <c>main</c> unnoticed; the attribute is
/// the deliberate, reviewed exception, and <see cref="Reason"/> is the sentence the reviewer checks it
/// against.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ContractMigrationAttribute(string reason) : Attribute
{
    /// <summary>Why the previous release of this removal is over — one sentence, written for the reviewer.</summary>
    public string Reason { get; } = reason;
}
