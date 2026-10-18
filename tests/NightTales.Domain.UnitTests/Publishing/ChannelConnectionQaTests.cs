using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

// The feature-flag namespace `NightTales.Domain.Platform` encloses this one, so the simple name `Platform`
// binds to it; an alias declared in this namespace body wins over that member (one declared in the
// compilation unit does not) and names the publishing enum the aggregate stores.
using Platform = NightTales.Domain.Publishing.Platform;

/// <summary>
/// QA exploratory and boundary pass over <see cref="ChannelConnection"/> (06.1.2-T2): the negative half of the
/// seven-method contract that <see cref="ChannelConnectionTests"/> does not exercise — every transition that
/// has to refuse a revoked row, the single way back out of <c>revoked</c>, the missing truncation of an
/// over-long identity, and the blank-input refusals.
/// </summary>
/// <remarks>
/// These tests were added by the independent QA agent of session 61; they assert observed behaviour of the
/// shipped aggregate, not a target the task file names directly. A test that fails here is a defect report.
/// </remarks>
public sealed class ChannelConnectionQaTests
{
    private static readonly Guid UserId = Guid.Parse("0199a2b3-0000-7000-8000-0000000000a1");

    /// <summary>The connect moment; every later instant in these tests is relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    private const string ChannelId = "UC_qa_x5XG1OV2P6uZZ5FSM9Ttw";
    private const string Title = "Night Tales";
    private const string AccessTokenCipher = "cfDJ8-access-token-ciphertext";
    private const string RefreshTokenCipher = "cfDJ8-refresh-token-ciphertext";
    private const string Scope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
    private const string ClientId = "client-a.apps.googleusercontent.com";

    /// <summary>
    /// A second disconnect of the same row is refused: <c>revoked_at</c> would move and the guard that keeps a
    /// revoked row's token columns <c>NULL</c> would have to be re-run against a row that already left
    /// <c>ux_channel_connections_account</c>.
    /// </summary>
    [Fact]
    public void Revoke_OnAnAlreadyRevokedRow_IsRefused()
    {
        var connection = Connected();
        var revokedAt = Now.AddMinutes(5);
        connection.Revoke(revokedAt);

        Should.Throw<InvalidOperationException>(() => connection.Revoke(Now.AddMinutes(9)));

        connection.RevokedAt.ShouldBe(revokedAt, "the refused second disconnect must not move the disconnect moment");
        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
    }

    /// <summary>
    /// Neither failure transition may move a revoked row off <c>revoked</c>: <c>MarkRefreshed</c> is the one
    /// that would silently resurrect the row, but the two non-token transitions are guarded the same way.
    /// </summary>
    [Fact]
    public void MarkExpired_And_MarkError_OnARevokedRow_AreRefused()
    {
        var connection = Connected();
        connection.Revoke(Now.AddMinutes(1));

        Should.Throw<InvalidOperationException>(() => connection.MarkExpired(Now.AddMinutes(2)));
        Should.Throw<InvalidOperationException>(() => connection.MarkError(Now.AddMinutes(3)));

        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        connection.AccessTokenEnc.ShouldBeNull();
        connection.RefreshTokenEnc.ShouldBeNull();
    }

    /// <summary>
    /// <c>MarkRefreshed</c> on a revoked row is refused and writes nothing: a token rotation must never revive a
    /// disconnected connection, because the ownership handshake lives in <c>Reconnect</c>.
    /// </summary>
    [Fact]
    public void MarkRefreshed_OnARevokedRow_IsRefused_AndWritesNothing()
    {
        var connection = Connected();
        var revokedAt = Now.AddMinutes(1);
        connection.Revoke(revokedAt);
        var lastVerifiedAt = connection.LastVerifiedAt;

        Should.Throw<InvalidOperationException>(() => connection.MarkRefreshed(
            "new-access-cipher",
            "new-refresh-cipher",
            Now.AddHours(2),
            Now.AddMinutes(2)));

        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        connection.AccessTokenEnc.ShouldBeNull();
        connection.RefreshTokenEnc.ShouldBeNull();
        connection.RevokedAt.ShouldBe(revokedAt);
        connection.LastVerifiedAt.ShouldBe(lastVerifiedAt, "the refusal writes no timestamp either");
    }

    /// <summary>
    /// <c>Reconnect</c> is the documented one path back out of <c>revoked</c>: the same row, the new pair, the
    /// re-pinned client, no disconnect moment. The 06.1.2-T2 handler never reaches this state (its locked read
    /// excludes revoked rows, so the row it would revive is a fresh insert), so this pins the domain contract
    /// the 06.4.x callers rely on rather than a production path.
    /// </summary>
    [Fact]
    public void Reconnect_OnARevokedRow_IsTheOnePathBackToActive()
    {
        var connection = Connected();
        var revokedAt = Now.AddMinutes(5);
        connection.Revoke(revokedAt);

        var reconnectedAt = Now.AddDays(1);
        connection.Reconnect(
            "Renamed at Google",
            "new-access-cipher",
            "new-refresh-cipher",
            reconnectedAt.AddHours(1),
            Scope,
            "client-b.apps.googleusercontent.com",
            reconnectedAt);

        connection.Status.ShouldBe(ChannelConnectionStatus.Active);
        connection.RevokedAt.ShouldBeNull();
        connection.AccessTokenEnc.ShouldBe("new-access-cipher");
        connection.RefreshTokenEnc.ShouldBe("new-refresh-cipher");
        connection.TokenExpiresAt.ShouldBe(reconnectedAt.AddHours(1));
        connection.LastVerifiedAt.ShouldBe(reconnectedAt);
        connection.OAuthClientId.ShouldBe("client-b.apps.googleusercontent.com");
        connection.ConnectedAt.ShouldBe(Now, "a reconnect keeps the first connect moment");
        connection.Id.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    /// A row Google marked <c>expired</c> (or <c>error</c>) is reconnected in place: the identity, the id and
    /// <c>connected_at</c> survive, and the row becomes publishable again — the failure table's
    /// "expired/error row, same user reconnects" case at the domain level.
    /// </summary>
    [Fact]
    public void Reconnect_OnAnExpiredRow_KeepsTheRowIdentity_AndSetsActive()
    {
        var connection = Connected();
        var id = connection.Id;
        connection.MarkExpired(Now.AddMinutes(10));

        var reconnectedAt = Now.AddHours(3);
        connection.Reconnect(
            Title,
            "rotated-access-cipher",
            "rotated-refresh-cipher",
            reconnectedAt.AddHours(1),
            Scope,
            ClientId,
            reconnectedAt);

        connection.Id.ShouldBe(id);
        connection.Status.ShouldBe(ChannelConnectionStatus.Active);
        connection.ConnectedAt.ShouldBe(Now);
        connection.ExternalAccountId.ShouldBe(ChannelId);
        connection.LastVerifiedAt.ShouldBe(reconnectedAt);
        connection.RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// A successful refresh returns an <c>expired</c>/<c>error</c> row to <c>active</c> without touching the
    /// identity: the 06.2.3-T2 recovery path after 06.4.2-T1 marked the connection.
    /// </summary>
    [Fact]
    public void MarkRefreshed_OnAnExpiredRow_ReturnsToActive_AndKeepsTheIdentity()
    {
        var connection = Connected();
        connection.MarkError(Now.AddMinutes(10));

        var refreshedAt = Now.AddHours(2);
        connection.MarkRefreshed("access-2-cipher", "refresh-2-cipher", refreshedAt.AddHours(1), refreshedAt);

        connection.Status.ShouldBe(ChannelConnectionStatus.Active);
        connection.ExternalAccountId.ShouldBe(ChannelId);
        connection.DisplayName.ShouldBe(Title);
        connection.Scope.ShouldBe(Scope);
        connection.OAuthClientId.ShouldBe(ClientId);
        connection.LastVerifiedAt.ShouldBe(refreshedAt);
        connection.RevokedAt.ShouldBeNull();
    }

    /// <summary>
    /// The domain never truncates an over-long channel title or scope: "an over-long title fails the insert
    /// loudly rather than truncating silently" (task file, failure table) is only true if the value reaches the
    /// column whole, so the width guard is the database's and this test pins the half the domain owns.
    /// </summary>
    [Fact]
    public void Connect_OverLongTitleAndScope_AreStoredVerbatim()
    {
        var title = new string('t', 201);
        var scope = new string('s', 513);

        var connection = Connect(displayName: title, scope: scope);

        connection.DisplayName.ShouldBe(title, "the domain must not cut the title to the column width");
        connection.DisplayName!.Length.ShouldBe(201);
        connection.Scope.Length.ShouldBe(513);
    }

    /// <summary>
    /// A blank identity, token, scope or client id is refused at the factory instead of reaching a column the
    /// schema marks <c>NOT NULL</c>: the negative input the callback can only produce by a bug.
    /// </summary>
    [Fact]
    public void Connect_BlankIdentityOrTokenValues_AreRefused()
    {
        Should.Throw<ArgumentException>(() => Connect(externalAccountId: string.Empty));
        Should.Throw<ArgumentException>(() => Connect(displayName: "   "));
        Should.Throw<ArgumentException>(() => Connect(accessTokenEnc: string.Empty));
        Should.Throw<ArgumentException>(() => Connect(refreshTokenEnc: " "));
        Should.Throw<ArgumentException>(() => Connect(scope: string.Empty));
        Should.Throw<ArgumentException>(() => Connect(oauthClientId: " "));
    }

    /// <summary>A live connection, minted the way the callback does: the UUIDv7 first, then the ciphertexts.</summary>
    private static ChannelConnection Connected() => Connect();

    private static ChannelConnection Connect(
        string? externalAccountId = null,
        string? displayName = null,
        string? accessTokenEnc = null,
        string? refreshTokenEnc = null,
        string? scope = null,
        string? oauthClientId = null) =>
        ChannelConnection.Connect(
            Guid.CreateVersion7(),
            UserId,
            Platform.YouTube,
            externalAccountId ?? ChannelId,
            displayName ?? Title,
            accessTokenEnc ?? AccessTokenCipher,
            refreshTokenEnc ?? RefreshTokenCipher,
            Now.AddHours(1),
            scope ?? Scope,
            oauthClientId ?? ClientId,
            Now);
}
