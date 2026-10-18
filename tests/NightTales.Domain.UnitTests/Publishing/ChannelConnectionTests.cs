using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Publishing;

// The feature-flag namespace `NightTales.Domain.Platform` encloses this one, so the simple name `Platform`
// binds to it; an alias declared in this namespace body wins over that member (one declared in the
// compilation unit does not) and names the publishing enum the aggregate stores.
using Platform = NightTales.Domain.Publishing.Platform;

/// <summary>
/// The mutation contract of <see cref="ChannelConnection"/> (06.1.2-T2, 06 README D3/D4/D5, plan §3.1): the
/// seven methods are the only ways into the row, each owns its columns, and <c>revoked</c> is terminal for every
/// one of them, <see cref="ChannelConnection.Reconnect"/> included.
/// </summary>
public sealed class ChannelConnectionTests
{
    private static readonly Guid UserId = Guid.Parse("0199a2b3-0000-7000-8000-000000000001");

    /// <summary>The connect moment; every later instant in these tests is relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    private const string ChannelId = "UC_x5XG1OV2P6uZZ5FSM9Ttw";
    private const string Title = "Night Tales";
    private const string AccessTokenCipher = "cfDJ8-access-token-ciphertext";
    private const string RefreshTokenCipher = "cfDJ8-refresh-token-ciphertext";
    private const string Scope = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
    private const string ClientId = "client-a.apps.googleusercontent.com";

    /// <summary>The client of the code a reconnect is built from: the pin moves to it (YR11).</summary>
    private const string SecondClientId = "client-b.apps.googleusercontent.com";

    private const string SecondAccessCipher = "cfDJ8-second-access-ciphertext";
    private const string SecondRefreshCipher = "cfDJ8-second-refresh-ciphertext";
    private const string ReconnectedTitle = "Night Tales (renamed)";

    [Fact]
    public void Connect_GeneratesIdBeforeEncryption()
    {
        // D4: the protection purpose is tokens.v1/{connectionId}, so the caller must mint the id and protect
        // both tokens under it BEFORE it hands them to the factory. Connect therefore pins the id it is given
        // instead of minting a second one, and refuses an id that was not minted that way.
        var id = Guid.CreateVersion7();

        var connection = Connect(id);

        connection.Id.ShouldBe(id);
        (connection.Id.ToByteArray(bigEndian: true)[6] >> 4).ShouldBe(7, "the id is the caller's UUIDv7");

        // Guid.Empty is what a caller that forgot to mint an id would pass, and a v4 Guid never named the
        // tokens' purpose either; both are refused instead of silently becoming the connection's identity.
        Should.Throw<ArgumentException>(() => Connect(Guid.Empty));
        Should.Throw<ArgumentException>(() => Connect(Guid.NewGuid()));

        // 06 README D13: a connect publishes exactly one change row, and its payload is ids plus the status —
        // no token, no channel title, no client id (OutboxJson serializes the enum as its snake_case wire value).
        var published = connection.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ChannelConnectionChanged>();

        published.ConnectionId.ShouldBe(id);
        published.UserId.ShouldBe(UserId);
        published.Status.ShouldBe(ChannelConnectionStatus.Active);
        published.OccurredAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(ChannelConnectionStatus.Active)]
    [InlineData(ChannelConnectionStatus.Expired)]
    [InlineData(ChannelConnectionStatus.Error)]
    public void Reconnect_KeepsId_RepinsClientId_SetsActive(ChannelConnectionStatus live)
    {
        var connection = Connected();
        var id = connection.Id;

        // Every live state is a row the reconnect rewrites in place: `active` itself, and the two dead grants
        // 06.4.2-T1 leaves behind. It is also the only other writer of the client pin (YR11). The one state it
        // refuses is `revoked` (Reconnect_OnARevokedOrPurgedRow_Throws).
        if (live == ChannelConnectionStatus.Expired)
        {
            connection.MarkExpired(Now.AddMinutes(1));
        }
        else if (live == ChannelConnectionStatus.Error)
        {
            connection.MarkError(Now.AddMinutes(1));
        }

        connection.Status.ShouldBe(live);

        var reconnectedAt = Now.AddMinutes(2);

        Reconnect(connection, reconnectedAt, SecondClientId);

        connection.Id.ShouldBe(id, "a reconnect updates the same row, never a second one");
        connection.ConnectedAt.ShouldBe(Now, "the first connect moment is not rewritten");
        connection.OAuthClientId.ShouldBe(SecondClientId);
        connection.OAuthClientId.ShouldNotBe(ClientId, "the pin moves to the client that minted the new code");
        connection.Status.ShouldBe(ChannelConnectionStatus.Active);
        connection.RevokedAt.ShouldBeNull("a live row has no disconnect moment, and a reconnect invents none");
        connection.LastVerifiedAt.ShouldBe(reconnectedAt);
        connection.DisplayName.ShouldBe(ReconnectedTitle);
        connection.AccessTokenEnc.ShouldBe(SecondAccessCipher);
        connection.RefreshTokenEnc.ShouldBe(SecondRefreshCipher);
        connection.TokenExpiresAt.ShouldBe(reconnectedAt.AddHours(1));

        // The reconnect publishes its own D13 change row: the connect's is still queued beside it, and the
        // reconnect's carries the status the row converged to.
        var changes = connection.DomainEvents.OfType<ChannelConnectionChanged>().ToList();

        changes.Count.ShouldBe(2, "the connect and the reconnect each publish one channel_connection_changed.v1 row");
        changes[^1].ConnectionId.ShouldBe(id);
        changes[^1].UserId.ShouldBe(UserId);
        changes[^1].Status.ShouldBe(ChannelConnectionStatus.Active);
        changes[^1].OccurredAt.ShouldBe(reconnectedAt);
    }

    [Fact]
    public void Reconnect_OnARevokedOrPurgedRow_Throws()
    {
        // A revoked row is never reused: it lost its tokens and left ux_channel_connections_account, so the
        // writer's locked read cannot even find it and the connect of that channel inserts a NEW row instead.
        var revoked = Connected();
        var revokedAt = Now.AddMinutes(1);
        revoked.Revoke(revokedAt);

        var refused = Should.Throw<InvalidOperationException>(
            () => Reconnect(revoked, Now.AddMinutes(2), SecondClientId));

        refused.Message.ShouldContain(nameof(ChannelConnection.Reconnect));
        refused.Message.ShouldNotContain("cfDJ8", Case.Insensitive, "an exception message never carries ciphertext");

        // The refusal wrote nothing: the revoked row keeps its NULL tokens, its disconnect moment and its identity.
        revoked.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        revoked.AccessTokenEnc.ShouldBeNull();
        revoked.RefreshTokenEnc.ShouldBeNull();
        revoked.RevokedAt.ShouldBe(revokedAt);
        revoked.DisplayName.ShouldBe(Title);
        revoked.OAuthClientId.ShouldBe(ClientId);

        // A purged row (13 §5.4 channel-scope deletion) has lost the identity a reconnect would rewrite, so it is
        // refused for that reason too instead of becoming an `active` row with no channel id.
        var purged = Connected();
        purged.Revoke(revokedAt);
        purged.PurgeYouTubeData(Now.AddDays(7));
        purged.ExternalAccountId.ShouldBeNull();

        Should.Throw<InvalidOperationException>(() => Reconnect(purged, Now.AddMinutes(2)));

        purged.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        purged.ExternalAccountId.ShouldBeNull();
        purged.AccessTokenEnc.ShouldBeNull();
    }

    [Fact]
    public void Revoke_NullsBothTokensAndSetsRevokedAt()
    {
        var connection = Connected();
        var revokedAt = Now.AddMinutes(15);

        connection.Revoke(revokedAt);

        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        connection.AccessTokenEnc.ShouldBeNull();
        connection.RefreshTokenEnc.ShouldBeNull();
        connection.RevokedAt.ShouldBe(revokedAt);
        connection.TokenExpiresAt.ShouldBe(revokedAt);

        // The disconnect keeps the identity: nulling it is the deletion engine's purge, days later.
        connection.ExternalAccountId.ShouldBe(ChannelId);
        connection.DisplayName.ShouldBe(Title);

        // `revoked` is terminal (06.4.1-T1 checks the status before it calls, and the row must stay out of
        // ux_channel_connections_account with its NULL tokens).
        Should.Throw<InvalidOperationException>(() => connection.Revoke(Now.AddMinutes(16)));
    }

    [Fact]
    public void MarkExpired_KeepsTokensButBlocksUpload()
    {
        var connection = Connected();

        connection.MarkExpired(Now.AddMinutes(30));

        connection.Status.ShouldBe(ChannelConnectionStatus.Expired);
        connection.AccessTokenEnc.ShouldBe(AccessTokenCipher);
        connection.RefreshTokenEnc.ShouldBe(RefreshTokenCipher);
        connection.TokenExpiresAt.ShouldBe(Now.AddHours(1));
        connection.RevokedAt.ShouldBeNull();

        // A revoked row is terminal: neither status of a failed token operation may bring it back.
        connection.Revoke(Now.AddMinutes(31));

        Should.Throw<InvalidOperationException>(() => connection.MarkExpired(Now.AddMinutes(32)));
        Should.Throw<InvalidOperationException>(() => connection.MarkError(Now.AddMinutes(33)));
        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
    }

    [Fact]
    public void MarkRefreshed_UpdatesTokensAndClearsRevokedAt()
    {
        var connection = Connected();
        connection.MarkExpired(Now.AddMinutes(1));
        var refreshedAt = Now.AddMinutes(40);

        connection.MarkRefreshed("cfDJ8-rotated-access", "cfDJ8-rotated-refresh", Now.AddHours(3), refreshedAt);

        connection.AccessTokenEnc.ShouldBe("cfDJ8-rotated-access");
        connection.RefreshTokenEnc.ShouldBe("cfDJ8-rotated-refresh");
        connection.TokenExpiresAt.ShouldBe(Now.AddHours(3));
        connection.Status.ShouldBe(ChannelConnectionStatus.Active, "a successful rotation makes the row live again");
        connection.RevokedAt.ShouldBeNull();
        connection.LastVerifiedAt.ShouldBe(refreshedAt);
        connection.OAuthClientId.ShouldBe(ClientId, "a rotation keeps the pinned client (D5/YR11)");
    }

    [Fact]
    public void MarkRefreshed_OnRevokedRow_Throws()
    {
        var connection = Connected();
        var revokedAt = Now.AddMinutes(1);
        connection.Revoke(revokedAt);

        var thrown = Should.Throw<InvalidOperationException>(
            () => connection.MarkRefreshed("cfDJ8-late-access", "cfDJ8-late-refresh", Now.AddHours(4), Now.AddMinutes(2)));

        thrown.Message.ShouldContain(nameof(ChannelConnection.MarkRefreshed));
        thrown.Message.ShouldNotContain("cfDJ8-late-access", Case.Insensitive, "an exception message never carries ciphertext");

        // The refusal wrote nothing: the revoked row keeps its NULL tokens and its disconnect moment.
        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        connection.AccessTokenEnc.ShouldBeNull();
        connection.RefreshTokenEnc.ShouldBeNull();
        connection.RevokedAt.ShouldBe(revokedAt);
        connection.TokenExpiresAt.ShouldBe(revokedAt);
    }

    [Fact]
    public void RefreshFromApi_UpdatesDisplayNameAndLastVerifiedOnly()
    {
        var connection = Connected();
        var checkedAt = Now.AddDays(25);

        connection.RefreshFromApi("Night Tales — renamed at Google", checkedAt);

        connection.DisplayName.ShouldBe("Night Tales — renamed at Google");
        connection.LastVerifiedAt.ShouldBe(checkedAt);

        // Nothing else moved: no status, no token, no expiry, no scope and no client pin.
        connection.Status.ShouldBe(ChannelConnectionStatus.Active);
        connection.AccessTokenEnc.ShouldBe(AccessTokenCipher);
        connection.RefreshTokenEnc.ShouldBe(RefreshTokenCipher);
        connection.TokenExpiresAt.ShouldBe(Now.AddHours(1));
        connection.Scope.ShouldBe(Scope);
        connection.OAuthClientId.ShouldBe(ClientId);
        connection.ExternalAccountId.ShouldBe(ChannelId);
        connection.ConnectedAt.ShouldBe(Now);
        connection.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public void PurgeYouTubeData_NullsTokensAndIdentity_OnRevokedRow()
    {
        var connection = Connected();
        var revokedAt = Now.AddMinutes(1);
        connection.Revoke(revokedAt);

        connection.PurgeYouTubeData(Now.AddDays(7));

        connection.AccessTokenEnc.ShouldBeNull();
        connection.RefreshTokenEnc.ShouldBeNull();
        connection.ExternalAccountId.ShouldBeNull("the column is NULL-able, so no `deleted` placeholder is written");
        connection.DisplayName.ShouldBeNull();

        // The row stays revoked with the disconnect moment it already had (never the purge moment), and the
        // non-identifying columns of the historical row survive (10 wave 2 renders the removed channel).
        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);
        connection.RevokedAt.ShouldBe(revokedAt);
        connection.Scope.ShouldBe(Scope);
        connection.OAuthClientId.ShouldBe(ClientId);
        connection.TokenExpiresAt.ShouldBe(revokedAt);
    }

    [Fact]
    public void PurgeYouTubeData_OnNonRevokedRow_Throws()
    {
        var connection = Connected();

        Should.Throw<InvalidOperationException>(() => connection.PurgeYouTubeData(Now.AddDays(1)));

        connection.MarkExpired(Now.AddMinutes(1));

        Should.Throw<InvalidOperationException>(() => connection.PurgeYouTubeData(Now.AddDays(1)));

        // The refusal wrote nothing: an expired channel is still the user's, with its tokens and its identity.
        connection.Status.ShouldBe(ChannelConnectionStatus.Expired);
        connection.AccessTokenEnc.ShouldBe(AccessTokenCipher);
        connection.RefreshTokenEnc.ShouldBe(RefreshTokenCipher);
        connection.ExternalAccountId.ShouldBe(ChannelId);
        connection.DisplayName.ShouldBe(Title);
    }

    /// <summary>A live connection, minted the way the callback does: the UUIDv7 first, then the ciphertexts.</summary>
    private static ChannelConnection Connected() => Connect(Guid.CreateVersion7());

    /// <summary>
    /// One reconnect of <paramref name="connection"/>, written the way the second callback writes it: a whole new
    /// pair under the same id, the title Google reported this time and <paramref name="now"/> as the verification
    /// moment, with the row's expiry one hour after it.
    /// </summary>
    /// <param name="connection">The row to reconnect.</param>
    /// <param name="now">The reconnect moment.</param>
    /// <param name="oauthClientId">The client of the code that minted the new pair.</param>
    private static void Reconnect(
        ChannelConnection connection,
        DateTimeOffset now,
        string oauthClientId = ClientId) =>
        connection.Reconnect(
            ReconnectedTitle,
            SecondAccessCipher,
            SecondRefreshCipher,
            now.AddHours(1),
            Scope,
            oauthClientId,
            now);

    private static ChannelConnection Connect(Guid id, string oauthClientId = ClientId) =>
        ChannelConnection.Connect(
            id,
            UserId,
            Platform.YouTube,
            ChannelId,
            Title,
            AccessTokenCipher,
            RefreshTokenCipher,
            Now.AddHours(1),
            Scope,
            oauthClientId,
            Now);
}
