namespace NightTales.Domain.Audit;

/// <summary>
/// The area an <c>audit_logs</c> row belongs to (09 README BD15, plan §3): the retention rules and the
/// activity-feed filter key on it, and the operator verbs write <see cref="Support"/>.
/// </summary>
/// <remarks>
/// Stored as snake_case text with a <c>CHECK</c> constraint listing exactly these members (09 README D8),
/// so a new member here changes the stored contract and needs a migration.
/// </remarks>
public enum Category
{
    AccountSecurity,
    Episodes,
    Publishing,
    Billing,
    Support,
}
