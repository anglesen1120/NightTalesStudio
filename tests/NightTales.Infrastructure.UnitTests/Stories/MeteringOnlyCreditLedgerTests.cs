using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Usage;
using NightTales.Infrastructure.Money;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>The phase-P ledger (02.2.2-T2, 02 README D1): it meters only and never rejects.</summary>
public sealed class MeteringOnlyCreditLedgerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MeteringOnlyCreditLedger_WritesNoLedgerRows_SettleAndRefundAreNoOps()
    {
        // The ledger has no constructor dependency, so it cannot reach the database: nothing is written to
        // credit_ledger or credit_lots, whatever it is asked to do.
        typeof(MeteringOnlyCreditLedger).GetConstructors().Single().GetParameters().ShouldBeEmpty();

        var ledger = new MeteringOnlyCreditLedger();
        var bound = new UsageBound(1_000_000m, 1, 1);

        var first = await ledger.ReserveAsync(Guid.CreateVersion7(), CreditOperation.StoryGenerate, Guid.CreateVersion7(), bound, Ct);
        var second = await ledger.ReserveAsync(Guid.CreateVersion7(), CreditOperation.StoryGenerate, Guid.CreateVersion7(), bound, Ct);

        first.IsError.ShouldBeFalse("phase P never rejects, whatever the bound");
        second.IsError.ShouldBeFalse();
        first.Value.Value.ShouldNotBe(Guid.Empty);
        first.Value.ShouldNotBe(second.Value, "every reservation is a fresh id");

        (await ledger.SettleAsync(first.Value, Ct)).IsError.ShouldBeFalse();
        (await ledger.RefundAsync(first.Value, "story_generation_cancelled", Ct)).IsError.ShouldBeFalse();
        (await ledger.RefundAsync(first.Value, "story_generation_cancelled", Ct)).IsError.ShouldBeFalse("a repeated refund is idempotent");
        (await ledger.SettleAsync(second.Value, Ct)).IsError.ShouldBeFalse();
    }
}
