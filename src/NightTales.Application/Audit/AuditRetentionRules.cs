using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Application.Audit;

/// <summary>
/// One <c>audit_logs</c> row as the retention rules see it (09 README BD15, plan §3 §7): the row's
/// classification plus the payment link that only plan 12 can resolve.
/// </summary>
/// <param name="Category">The row's area (<c>account_security</c> keeps rows for 24 months).</param>
/// <param name="ActorType">A staff action (<c>admin</c>) keeps the row for 24 months.</param>
/// <param name="Visibility">An <c>internal</c> row keeps the row for 24 months.</param>
/// <param name="HasPaymentLink">
/// Whether a payment record still points at the row. Always <c>false</c> in P — payments are S — so the
/// never-delete rule is inert until plan 12.
/// </param>
/// <param name="CreatedAt">When the row was written.</param>
public sealed record AuditRetentionRow(
    Category Category,
    ActorType ActorType,
    Visibility Visibility,
    bool HasPaymentLink,
    DateTimeOffset CreatedAt);

/// <summary>
/// One ordered retention rule (09 README BD15/G5, plan §3): <see cref="Retain"/> is how long a row this
/// rule matches is kept, and <see cref="Matches"/> is its predicate.
/// </summary>
/// <param name="Id">The stable rule id, e.g. <c>account_security</c>.</param>
/// <param name="Description">The rule in one sentence, for the sweep's logs and the reviewer.</param>
/// <param name="Retain">The retention window, or <c>null</c> to never delete the matched rows.</param>
/// <param name="Matches">Whether the rule owns this row; the first rule that matches wins.</param>
public sealed record AuditRetentionRule(
    string Id,
    string Description,
    TimeSpan? Retain,
    Func<AuditRetentionRow, bool> Matches);

/// <summary>
/// The one ordered retention list of <c>audit_logs</c> (09 README G5, plan §3), consumed by
/// 09.7.3-T1's <c>RetentionSweepJob</c>.
/// </summary>
/// <remarks>
/// <para>
/// First match wins, so the list is ordered from the most specific rule to the least: a payment-linked
/// row is never deleted, <c>account_security</c> keeps 24 months, a staff or internal row keeps 24
/// months, and every other user-visible row keeps 12 months. <see cref="AuditRetentionRule.Retain"/>
/// <c>null</c> means "never delete".
/// </para>
/// <para>
/// The numbers live here and nowhere else: the sweep walks this list and must not restate them. A
/// null-subject row is always <c>visibility = internal</c>, so it matches rule 3 and needs no null
/// branch.
/// </para>
/// <para>
/// The legal review of 13.6 is still open on these windows (plan §3 ⚠️); changing one is changing this
/// list alone.
/// </para>
/// </remarks>
public static class AuditRetentionRules
{
    /// <summary>The rules, most specific first.</summary>
    public static readonly IReadOnlyList<AuditRetentionRule> Ordered =
    [
        new(
            "payment_linked",
            "kept as long as the payment record (12, accountant ⚠️)",
            null,
            row => row.HasPaymentLink),
        new(
            "account_security",
            "24 months [DECISION, 13.6]",
            TimeSpan.FromDays(730),
            row => row.Category == Category.AccountSecurity),
        new(
            "staff_or_internal",
            "24 months [DECISION]",
            TimeSpan.FromDays(730),
            row => row.ActorType == ActorType.Admin || row.Visibility == Visibility.Internal),
        new(
            "user_visible",
            "12 months [DECISION]",
            TimeSpan.FromDays(365),
            _ => true),
    ];
}
