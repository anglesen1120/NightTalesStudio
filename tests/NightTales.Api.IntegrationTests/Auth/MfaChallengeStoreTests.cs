using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The <c>auth_challenges</c> store of 09.3.2-T3's Tests table: <c>BeginAsync</c> supersedes every live
/// challenge of one account so at most one row is completable, and only the SHA-256 hex of the two
/// bearer secrets ever reaches the row — the plaintext pair exists in the answer once (BD4, BD16).
/// </summary>
/// <remarks>
/// The service is resolved from the real composition root and called inside a
/// <see cref="IUnitOfWork"/> transaction, exactly as the command pipeline calls it (09 README D5): every
/// statement of <c>BeginAsync</c> requires an open transaction, so calling it outside one is a
/// programming error, not a scenario a test should exercise.
/// </remarks>
public sealed class MfaChallengeStoreTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Ip = "198.51.100.7";

    private const string UserAgent = "Tales/1.0";

    [Fact]
    public async Task Begin_SupersedesLiveChallenge_AndStoresOnlyHashes()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        using var scope = factory.Services.CreateScope();
        var challenges = scope.ServiceProvider.GetRequiredService<IMfaChallengeService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var issuer = scope.ServiceProvider.GetRequiredService<ITokenIssuer>();
        var lifetime = scope.ServiceProvider.GetRequiredService<IOptions<MfaOptions>>().Value.Mfa.ChallengeLifetime;

        var first = await BeginAsync(challenges, unitOfWork, user.Id);
        var second = await BeginAsync(challenges, unitOfWork, user.Id);

        // BD4: two password steps leave two rows behind, but only the newest is completable.
        Count(user.Id).ShouldBe(2);
        LiveCount(user.Id).ShouldBe(1);
        UsedAtIsSet(first.Value.ChallengeId).ShouldBeTrue(
            "the supersede statement of BeginAsync deadens the account's live row (BD4)");
        UsedAtIsSet(second.Value.ChallengeId).ShouldBeFalse("the second password step's own row is the live one");

        // BD16: the row carries the hashes, never the plaintext the login answer just handed back.
        var stored = StoredRow(first.Value.ChallengeId);
        stored.TokenHash.ShouldBe(issuer.HashToken(first.Value.MfaToken));
        stored.TokenHash.ShouldNotBe(first.Value.MfaToken);
        stored.PreAuthCookieHash.ShouldBe(issuer.HashToken(first.Value.PreAuthCookie));
        stored.PreAuthCookieHash.ShouldNotBe(first.Value.PreAuthCookie);
        stored.PreAuthCookieHash.ShouldNotBe(stored.TokenHash, "the two bearer secrets are distinct");

        var plaintexts = new[]
        {
            first.Value.MfaToken,
            first.Value.PreAuthCookie,
            second.Value.MfaToken,
            second.Value.PreAuthCookie,
        };

        SqlProbe.QueryStrings(
                Database,
                "SELECT token_hash || ' ' || preauth_cookie_hash || ' ' || coalesce(user_agent, '') || ' ' "
                + "|| coalesce(ip::text, '') FROM auth_challenges")
            .Any(row => plaintexts.Any(secret => row.Contains(secret, StringComparison.Ordinal)))
            .ShouldBeFalse("no column of auth_challenges may hold either plaintext secret");

        // The password step's caller metadata is recorded; the attempt counter starts at zero.
        stored.UserAgent.ShouldBe(UserAgent);
        stored.Ip.ShouldBe(Ip);
        stored.Attempts.ShouldBe(0);

        // The expiry is the configured challenge lifetime after the scope's clock reading (BD4: five minutes).
        second.Value.ExpiresAt.ShouldBe(clock.UtcNow + lifetime);
        SqlProbe.ScalarText(
                Database,
                $"SELECT (expires_at = '{second.Value.ExpiresAt:O}'::timestamptz)::text "
                + $"FROM auth_challenges WHERE id = '{second.Value.ChallengeId}'")
            .ShouldBe("true");
    }

    [Fact]
    public async Task Begin_ChallengeNeverReturnsPlaintextTwice()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        using var scope = factory.Services.CreateScope();
        var challenges = scope.ServiceProvider.GetRequiredService<IMfaChallengeService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var first = await BeginAsync(challenges, unitOfWork, user.Id);
        var second = await BeginAsync(challenges, unitOfWork, user.Id);

        // A fresh password step mints a brand-new pair; it never replays the earlier challenge's secrets.
        first.Value.ChallengeId.ShouldNotBe(second.Value.ChallengeId);
        first.Value.MfaToken.ShouldNotBe(second.Value.MfaToken);
        first.Value.PreAuthCookie.ShouldNotBe(second.Value.PreAuthCookie);

        // And the earlier pair is already unusable: its row is superseded, so the plaintext cannot be
        // exchanged a second time either.
        UsedAtIsSet(first.Value.ChallengeId).ShouldBeTrue();
        UsedAtIsSet(second.Value.ChallengeId).ShouldBeFalse();
        LiveCount(user.Id).ShouldBe(1);
    }

    /// <summary>Opens one challenge inside its own transaction, the way the login command does.</summary>
    private static async Task<ErrorOr<MfaChallenge>> BeginAsync(
        IMfaChallengeService challenges,
        IUnitOfWork unitOfWork,
        Guid userId)
    {
        await unitOfWork.BeginAsync(Ct);
        var result = await challenges.BeginAsync(userId, new RequestInfo(Ip, UserAgent), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        if (result.IsError)
        {
            throw new InvalidOperationException(
                "BeginAsync failed: " + string.Join("; ", result.Errors.Select(error => error.Code)));
        }

        return result;
    }

    private long Count(Guid userId) => SqlProbe.Count(
        Database,
        $"SELECT count(*) FROM auth_challenges WHERE user_id = '{userId}'");

    private long LiveCount(Guid userId) => SqlProbe.Count(
        Database,
        $"SELECT count(*) FROM auth_challenges WHERE user_id = '{userId}' AND used_at IS NULL");

    private bool UsedAtIsSet(Guid challengeId) =>
        SqlProbe.ScalarText(
            Database,
            $"SELECT (used_at IS NOT NULL)::text FROM auth_challenges WHERE id = '{challengeId}'") == "true";

    /// <summary>The stored column values of one challenge; only the hashed secrets are columns of interest.</summary>
    private StoredChallenge StoredRow(Guid challengeId)
    {
        var row = SqlProbe.QueryStrings(
                Database,
                "SELECT token_hash || chr(31) || preauth_cookie_hash || chr(31) || coalesce(user_agent, '<null>') "
                + "|| chr(31) || coalesce(host(ip), '<null>') || chr(31) || attempts::text "
                + $"FROM auth_challenges WHERE id = '{challengeId}'")
            .ShouldHaveSingleItem();

        var parts = row.Split('\u001f');

        return new StoredChallenge(
            parts[0],
            parts[1],
            parts[2],
            parts[3],
            int.Parse(parts[4], CultureInfo.InvariantCulture));
    }

    private sealed record StoredChallenge(
        string TokenHash,
        string PreAuthCookieHash,
        string UserAgent,
        string Ip,
        int Attempts);
}
