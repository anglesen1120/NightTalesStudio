using NightTales.Application.Audit;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Audit;

/// <summary>
/// The ordered retention list of 09.3.6-T1 (09 README G5, plan §3): first match wins, so the payment link
/// outranks the security window, the security category outranks the staff rule, and the never-delete and
/// 24/12-month windows are the ones 09.7.3-T1's sweep deletes through.
/// </summary>
public sealed class AuditRetentionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrderedRules_FirstMatchWins()
    {
        var paymentLinked = new AuditRetentionRow(
            Category.AccountSecurity,
            ActorType.Admin,
            Visibility.Internal,
            HasPaymentLink: true,
            Now);

        var matched = FirstMatch(paymentLinked);

        matched.Id.ShouldBe("payment_linked", "the payment link outranks both the category and the staff rule");
        matched.Retain.ShouldBeNull("a payment-linked row is never deleted");
    }

    [Fact]
    public void StaffAndUserWindows()
    {
        FirstMatch(new AuditRetentionRow(Category.AccountSecurity, ActorType.User, Visibility.User, false, Now))
            .ShouldBe(("account_security", TimeSpan.FromDays(730)));

        FirstMatch(new AuditRetentionRow(Category.AccountSecurity, ActorType.Admin, Visibility.Internal, false, Now))
            .ShouldBe(("account_security", TimeSpan.FromDays(730)));

        FirstMatch(new AuditRetentionRow(Category.Episodes, ActorType.Admin, Visibility.User, false, Now))
            .ShouldBe(("staff_or_internal", TimeSpan.FromDays(730)));

        FirstMatch(new AuditRetentionRow(Category.Episodes, ActorType.System, Visibility.Internal, false, Now))
            .ShouldBe(("staff_or_internal", TimeSpan.FromDays(730)));

        FirstMatch(new AuditRetentionRow(Category.Episodes, ActorType.User, Visibility.User, false, Now))
            .ShouldBe(("user_visible", TimeSpan.FromDays(365)));
    }

    [Fact]
    public void Ordered_IsTheFrozenRuleList()
    {
        AuditRetentionRules.Ordered.Select(rule => rule.Id).ShouldBe(
            ["payment_linked", "account_security", "staff_or_internal", "user_visible"]);

        AuditRetentionRules.Ordered.ShouldAllBe(rule => !string.IsNullOrWhiteSpace(rule.Description));
    }

    /// <summary>The rule 09.7.3-T1's sweep deletes through: the first one whose predicate matches wins.</summary>
    /// <remarks>
    /// The last rule matches every row, so <see cref="Enumerable.First{TSource}(IEnumerable{TSource}, Func{TSource, bool})"/>
    /// can only fail when the list itself was changed into a broken one.
    /// </remarks>
    private static (string Id, TimeSpan? Retain) FirstMatch(AuditRetentionRow row)
    {
        var rule = AuditRetentionRules.Ordered.First(candidate => candidate.Matches(row));

        return (rule.Id, rule.Retain);
    }
}
