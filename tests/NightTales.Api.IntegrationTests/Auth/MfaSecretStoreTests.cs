using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The authenticator secret store of 09.10.4-T1 (Tests table): the payload is Data-Protection-protected
/// under <c>identity.totp.v1</c> and nothing else (WT43), and a key ring that cannot read it back is a 401
/// with an alert — never a silent "MFA is off" (WF15).
/// </summary>
public sealed class MfaSecretStoreTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SecretIsProtected_AndRoundTrips()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var secret = Base32.NewSecret();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(Ct);
        (await store.SetPendingSecretAsync(user.Id, secret, Ct)).IsError.ShouldBeFalse();
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        var stored = await StoredAsync(user.Id);

        stored.ShouldNotBe(secret, "the secret is never stored in plaintext (BD16)");
        stored.Contains(secret, StringComparison.Ordinal).ShouldBeFalse();

        (await store.GetPendingSecretAsync(user.Id, Ct)).Value.ShouldBe(
            secret,
            "the store round-trips its own payload");
    }

    [Fact]
    public async Task SecretProtector_UsesIdentityTotpPurpose_NotTokenProtector()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var secret = Base32.NewSecret();

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(Ct);
        _ = await store.SetPendingSecretAsync(user.Id, secret, Ct);
        _ = await unitOfWork.CommitAsync(Ct);

        var stored = await StoredAsync(user.Id);

        var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var directly = protection.CreateProtector(DataProtectionTotpSecretStore.Purpose).Unprotect(stored);

        directly.ShouldBe(secret, "the payload is written under identity.totp.v1 itself (WT43)");

        var tokenProtector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();

        tokenProtector
            .TryUnprotect(DataProtectionTotpSecretStore.Purpose, stored, out _)
            .ShouldBeFalse(
                "going through ITokenProtector would have produced tokens.v1/identity.totp.v1 — a "
                + "different purpose root the cipher cannot be read under (WT43)");
    }

    [Fact]
    public async Task SecretStore_UnavailableKeyRing_AnswersInvalidMfaCode_AndAlerts()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = user.Id }, clock);
        var broken = new DataProtectionTotpSecretStore(context, new UnavailableKeyRing());
        var alerter = new RecordingAlerter();
        var verifier = new Rfc6238TotpVerifier(
            context,
            broken,
            Options.Create(new MfaOptions()),
            clock,
            alerter);

        var result = await verifier.VerifyAsync(user.Id, "000000", TotpSlot.Active, Ct);

        result.IsError.ShouldBeTrue("a secret that cannot be read is not a valid code");
        result.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);

        alerter.Events.ShouldContain(
            evt => evt.Kind == SecurityEventKinds.TotpSecretUnreadable && evt.SubjectUserId == user.Id,
            "WF15: the unreadable secret is a P1 alert, never a silent two_factor_enabled = false");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'"))
            .ShouldBe("true", "the flag is untouched by the failure");
    }

    [Fact]
    public async Task Promote_Concurrent_ConsumesThePendingRowExactlyOnce()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var secret = Base32.NewSecret();

        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var store = seed.ServiceProvider.GetRequiredService<ITotpSecretStore>();
            var unitOfWork = seed.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await unitOfWork.BeginAsync(Ct);
            (await store.SetPendingSecretAsync(user.Id, secret, Ct)).IsError.ShouldBeFalse();
            (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();
        }

        await using var firstScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var firstUnitOfWork = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await firstUnitOfWork.BeginAsync(Ct);

        (await first.PromotePendingSecretAsync(user.Id, Ct)).IsError.ShouldBeFalse();

        // The second promotion starts while the first is still uncommitted: it must wait on the pending row
        // the first consumed, and then find nothing to consume.
        await using var secondScope = factory.Services.CreateAsyncScope();
        var second = secondScope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var secondUnitOfWork = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await secondUnitOfWork.BeginAsync(Ct);

        var secondPromotion = second.PromotePendingSecretAsync(user.Id, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        secondPromotion.IsCompleted.ShouldBeFalse(
            "one pending row can be consumed only once; the second waits on the first's row lock");

        (await firstUnitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        var secondResult = await secondPromotion;
        secondResult.IsError.ShouldBeTrue("a consumed pending row cannot be promoted a second time");
        secondResult.FirstError.Code.ShouldBe(ErrorCodes.InvalidMfaCode);
        (await secondUnitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        var active = await first.GetActiveSecretAsync(user.Id, Ct);
        active.IsError.ShouldBeFalse();
        active.Value.ShouldBe(secret, "the active row holds exactly the payload the promotion consumed");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM user_tokens WHERE user_id = '{user.Id}' "
                + "AND name = 'totp_secret_pending'"))
            .ShouldBe("0", "the consumed pending row is gone");
    }

    /// <summary>The stored <c>totp_secret_pending</c> payload of the user.</summary>
    private async Task<string> StoredAsync(Guid userId) =>
        (await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT value FROM user_tokens WHERE user_id = '{userId}' "
            + $"AND login_provider = '[AspNetUserStore]' AND name = 'totp_secret_pending'"))!;

    /// <summary>A key ring whose protector cannot protect or unprotect anything (WF15).</summary>
    private sealed class UnavailableKeyRing : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new UnavailableProtector();

        private sealed class UnavailableProtector : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => this;

            public byte[] Protect(byte[] plaintext) =>
                throw new CryptographicException("The key ring is unavailable.");

            public byte[] Unprotect(byte[] protectedData) =>
                throw new CryptographicException("The key ring is unavailable.");
        }
    }

    /// <summary>A hand-written <see cref="ISecurityAlerter"/> that records what it was told.</summary>
    private sealed class RecordingAlerter : ISecurityAlerter
    {
        public List<SecurityEvent> Events { get; } = [];

        public Task AlertAsync(SecurityEvent evt, CancellationToken ct)
        {
            Events.Add(evt);

            return Task.CompletedTask;
        }
    }
}
