using NightTales.Application.Audit;
using NightTales.Application.Features.Platform.Retention;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Audit;

/// <summary>
/// <see cref="AuditRetentionPredicate"/> (09.7.3-T1): the decision is the first matching rule of
/// <see cref="AuditRetentionRules.Ordered"/>, and no window is restated by the predicate.
/// </summary>
public sealed class AuditRetentionPredicateTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Predicate_PaymentLinkedRule_NeverRetainsARow()
    {
        var row = Candidate(Category.Billing, ActorType.User, Visibility.User, Now.AddDays(-5000), paymentLinked: true);

        AuditRetentionPredicate.Expired([row], AuditRetentionRules.Ordered, Now).ShouldBeEmpty();
    }

    [Fact]
    public void Predicate_AccountSecurityRule_Uses730Days()
    {
        Expired(Category.AccountSecurity, ActorType.User, Visibility.User, 729).ShouldBeFalse();
        Expired(Category.AccountSecurity, ActorType.User, Visibility.User, 731).ShouldBeTrue();
    }

    [Fact]
    public void Predicate_AdminOrInternalRule_Uses730Days()
    {
        Expired(Category.Episodes, ActorType.Admin, Visibility.User, 729).ShouldBeFalse();
        Expired(Category.Episodes, ActorType.Admin, Visibility.User, 731).ShouldBeTrue();
        Expired(Category.Episodes, ActorType.System, Visibility.Internal, 729).ShouldBeFalse();
        Expired(Category.Episodes, ActorType.System, Visibility.Internal, 731).ShouldBeTrue();
    }

    [Fact]
    public void Predicate_DefaultRule_Uses365Days()
    {
        Expired(Category.Episodes, ActorType.User, Visibility.User, 364).ShouldBeFalse();
        Expired(Category.Episodes, ActorType.User, Visibility.User, 366).ShouldBeTrue();
    }

    [Fact]
    public void Predicate_FirstMatchWins_ForABillingUserRow()
    {
        // The row the first draft would have deleted: a 12-month-old billing row written by a user actor,
        // payment-linked. Rule 1 owns it, so it stays; without the link the 365-day rule owns it.
        var linked = Candidate(Category.Billing, ActorType.User, Visibility.User, Now.AddDays(-400), paymentLinked: true);
        var unlinked = Candidate(Category.Billing, ActorType.User, Visibility.User, Now.AddDays(-400));

        AuditRetentionPredicate.Expired([linked, unlinked], AuditRetentionRules.Ordered, Now)
            .ShouldBe([unlinked.Id]);
    }

    private static bool Expired(Category category, ActorType actor, Visibility visibility, int ageDays)
    {
        var row = Candidate(category, actor, visibility, Now.AddDays(-ageDays));

        return AuditRetentionPredicate.Expired([row], AuditRetentionRules.Ordered, Now).Count == 1;
    }

    private static AuditRetentionCandidate Candidate(
        Category category,
        ActorType actor,
        Visibility visibility,
        DateTimeOffset createdAt,
        bool paymentLinked = false) =>
        new(Guid.CreateVersion7(), new AuditRetentionRow(category, actor, visibility, paymentLinked, createdAt), createdAt);
}
