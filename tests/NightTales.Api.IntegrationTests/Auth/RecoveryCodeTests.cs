using System.Net;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The recovery codes of 09.10.4-T1 (Tests table): ten codes replaced atomically, single use, compared
/// without an early exit, and safe under two concurrent redemptions.
/// </summary>
/// <remarks>
/// Redemption itself is not a route of this task — 09.3.2-T3's <c>/auth/login/mfa</c> is its HTTP caller —
/// so the service is exercised through its port inside real transactions, which is what the row lock and
/// the "removes the matched hash" rule need.
/// </remarks>
public sealed class RecoveryCodeTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Regenerate_ReplacesAll_AndReturnsTenCodes()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        enrolment.RecoveryCodes.Count.ShouldBe(10);
        enrolment.RecoveryCodes.ShouldAllBe(code => code.Length == 10);

        var before = await StoredAsync(user.Id);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var regenerated = await client.PostAsync(MfaTestFlow.RecoveryCodesPath, content: null, Ct);
        regenerated.StatusCode.ShouldBe(HttpStatusCode.OK);

        var codes = await MfaTestFlow.RecoveryCodesAsync(regenerated, Ct);
        codes.Count.ShouldBe(10, "a regeneration replaces the whole set with exactly ten codes");
        codes.ShouldAllBe(code => code.Length == 10);

        var after = await StoredAsync(user.Id);

        after.ShouldNotBe(before, "the whole set is replaced");
        after.Split(';').Length.ShouldBe(10);
        after.Contains(codes[0], StringComparison.Ordinal).ShouldBeFalse(
            "a code is never stored in plaintext (BD16)");
        after.Contains(enrolment.RecoveryCodes[0], StringComparison.Ordinal).ShouldBeFalse(
            "the previous set no longer exists");
    }

    [Fact]
    public async Task Redeem_IsSingleUse()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var first = await RedeemAsync(factory, user.Id, enrolment.RecoveryCodes[0]);
        first.IsError.ShouldBeFalse();

        var second = await RedeemAsync(factory, user.Id, enrolment.RecoveryCodes[0]);

        second.IsError.ShouldBeTrue("a code is consumed by its first redemption");
        second.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);

        (await CountAsync(factory, user.Id)).ShouldBe(9);
    }

    [Fact]
    public async Task Redeem_ConstantTime_AcceptsAnyStoredCode()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        // The last stored code is accepted just like the first: the comparison walks the whole list, so a
        // match can never depend on its position.
        (await RedeemAsync(factory, user.Id, enrolment.RecoveryCodes[9])).IsError.ShouldBeFalse();
        (await RedeemAsync(factory, user.Id, enrolment.RecoveryCodes[0])).IsError.ShouldBeFalse();

        (await CountAsync(factory, user.Id)).ShouldBe(8);

        var unknown = await RedeemAsync(factory, user.Id, "ZZZZZZZZZZ");
        unknown.IsError.ShouldBeTrue("a code that is not stored is refused");
        unknown.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);
    }

    [Fact]
    public async Task Redeem_Concurrent_OnlyOneWins()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var code = enrolment.RecoveryCodes[3];

        var results = await Task.WhenAll(
            Task.Run(() => RedeemAsync(factory, user.Id, code), Ct),
            Task.Run(() => RedeemAsync(factory, user.Id, code), Ct));

        results.Count(result => !result.IsError).ShouldBe(
            1,
            "the row lock lets exactly one redemption of the same code win");

        (await CountAsync(factory, user.Id)).ShouldBe(9);
    }

    /// <summary>The stored <c>;</c>-joined hash list of the user's row.</summary>
    private async Task<string> StoredAsync(Guid userId) =>
        (await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT value FROM user_tokens WHERE user_id = '{userId}' "
            + $"AND login_provider = '[AspNetUserStore]' AND name = 'recovery_codes'"))!;

    /// <summary>How many codes the user still holds, through the port.</summary>
    private static async Task<int> CountAsync(ApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>();

        return await codes.CountRemainingAsync(userId, Ct);
    }

    /// <summary>One redemption, in its own transaction, exactly as a handler would run it.</summary>
    private static async Task<ErrorOr<Success>> RedeemAsync(ApiFactory factory, Guid userId, string code)
    {
        using var scope = factory.Services.CreateScope();
        var codes = scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(Ct);

        var result = await codes.RedeemAsync(userId, code, Ct);

        var committed = await unitOfWork.CommitAsync(Ct);
        committed.IsError.ShouldBeFalse();

        return result;
    }
}
