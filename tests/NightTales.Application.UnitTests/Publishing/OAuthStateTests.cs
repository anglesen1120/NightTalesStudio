using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// <see cref="OAuthState"/> (06.1.2-T1, 06 README D3): the domain twin of the callback's atomic claim — a state
/// is usable exactly once, inside its ten-minute TTL, so a replay can never complete a second connect.
/// </summary>
public sealed class OAuthStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The configured <c>YouTube:OAuthStateMinutes</c> of README D11.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static readonly Guid FamilyId = Guid.Parse("01920000-0000-7000-8000-0000000000f1");

    private static readonly Guid UserId = Guid.Parse("01920000-0000-7000-8000-0000000000f2");

    [Fact]
    public void Create_MintsAnUnusedStateThatExpiresAfterTheTtl()
    {
        var state = Create();

        state.Id.ShouldNotBe(Guid.Empty);
        state.UserId.ShouldBe(UserId);
        state.Platform.ShouldBe(Platform.YouTube);
        state.StateHash.ShouldBe("state-hash");
        state.CodeVerifierProtected.ShouldBe("verifier-protected");
        state.BrowserNonceHash.ShouldBe("nonce-hash");
        state.OAuthClientId.ShouldBe("client-a");
        state.RefreshFamilyId.ShouldBe(FamilyId);
        state.CreatedAt.ShouldBe(Now);
        state.ExpiresAt.ShouldBe(Now + Ttl);
        state.UsedAt.ShouldBeNull();
        state.IsUsable(Now).ShouldBeTrue();
    }

    [Fact]
    public void Create_RefusesBlankInputs()
    {
        Should.Throw<ArgumentException>(() => Create(stateHash: " "));
        Should.Throw<ArgumentException>(() => Create(codeVerifierProtected: ""));
        Should.Throw<ArgumentException>(() => Create(browserNonceHash: "   "));
        Should.Throw<ArgumentException>(() => Create(oauthClientId: ""));
    }

    [Fact]
    public void Create_RefusesANonPositiveTtl()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Create(ttl: TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => Create(ttl: TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void IsUsable_StopsExactlyAtTheExpiry()
    {
        var state = Create();

        state.IsUsable(Now + Ttl - TimeSpan.FromSeconds(1)).ShouldBeTrue();
        state.IsUsable(Now + Ttl).ShouldBeFalse("the boundary is excluded: expires_at > now");
        state.IsUsable(Now + Ttl + TimeSpan.FromSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void Claim_TwiceThrows_AndKeepsTheFirstInstant()
    {
        var state = Create();
        var claimedAt = Now + TimeSpan.FromMinutes(1);

        state.Claim(claimedAt);

        state.UsedAt.ShouldBe(claimedAt);
        state.IsUsable(claimedAt).ShouldBeFalse();

        Should.Throw<InvalidOperationException>(() => state.Claim(claimedAt + TimeSpan.FromSeconds(1)));
        state.UsedAt.ShouldBe(claimedAt, "a refused claim must not move the instant the state was spent");
    }

    [Fact]
    public void Claim_AfterExpiryThrows()
    {
        var state = Create();

        Should.Throw<InvalidOperationException>(() => state.Claim(Now + Ttl + TimeSpan.FromSeconds(1)));
        state.UsedAt.ShouldBeNull("an expired state was never spent");
    }

    private static OAuthState Create(
        string stateHash = "state-hash",
        string codeVerifierProtected = "verifier-protected",
        string browserNonceHash = "nonce-hash",
        string oauthClientId = "client-a",
        TimeSpan? ttl = null) =>
        OAuthState.Create(
            UserId,
            Platform.YouTube,
            stateHash,
            codeVerifierProtected,
            browserNonceHash,
            oauthClientId,
            FamilyId,
            Now,
            ttl ?? Ttl);
}
