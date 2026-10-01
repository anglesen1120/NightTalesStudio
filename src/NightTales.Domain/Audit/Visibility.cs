namespace NightTales.Domain.Audit;

/// <summary>
/// Who may read an <c>audit_logs</c> row (09 README BD15, plan §3): <see cref="User"/> rows appear in
/// the owner's activity feed, <see cref="Internal"/> ones (staff actions, system events and security
/// failures on an unknown address) never do.
/// </summary>
/// <remarks>
/// A <see cref="User"/> row must carry a <c>subject_user_id</c> — the table's
/// <c>ck_audit_logs_user_visible_has_subject</c> constraint makes the other state unrepresentable.
/// Stored as snake_case text with a <c>CHECK</c> constraint (09 README D8).
/// </remarks>
public enum Visibility
{
    User,
    Internal,
}
