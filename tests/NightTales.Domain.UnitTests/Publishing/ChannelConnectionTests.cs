using System.Text.Json;
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
/// seven methods are the only ways into the row, each owns its columns, and <c>revoked</c> is terminal apart
/// from <see cref="ChannelConnection.Reconnect"/>.
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

    [Fact]
    public void Reconnect_KeepsId_RepinsClientId_SetsActive_ClearsRevokedAt()
    {
        var connection = Connected();
        var id = connection.Id;

        // The one path back out of `revoked`: it is also the only other writer of the client pin (YR11).
        connection.Revoke(Now.AddMinutes(1));
        connection.Status.ShouldBe(ChannelConnectionStatus.Revoked);

        const string secondClientId = "client-b.apps.googleusercontent.com";
        var reconnectedAt = Now.AddMinutes(2);

        connection.Reconnect(
            "Night Tales (renamed)",
            "cfDJ8-second-access-ciphertext",
            "cfDJ8-second-refresh-ciphertext",
            Now.AddHours(2),
            Scope,
            secondClientId,
            reconnectedAt);

        connection.Id.ShouldBe(id, "a reconnect updates the same row, never a second one");
        connection.ConnectedAt.ShouldBe(Now, "the first connect moment is not rewritten");
        connection.OAuthClientId.ShouldBe(secondClientId);
        connection.OAuthClientId.ShouldNotBe(ClientId, "the pin moves to the client that minted the new code");
        connection.Status.ShouldBe(ChannelConnectionStatus.Active);
        connection.RevokedAt.ShouldBeNull();
        connection.LastVerifiedAt.ShouldBe(reconnectedAt);
        connection.DisplayName.ShouldBe("Night Tales (renamed)");
        connection.AccessTokenEnc.ShouldBe("cfDJ8-second-access-ciphertext");
        connection.RefreshTokenEnc.ShouldBe("cfDJ8-second-refresh-ciphertext");
        connection.TokenExpiresAt.ShouldBe(Now.AddHours(2));

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
        connection.Status.ShouldNotBe(
            ChannelConnectionStatus.Active,
            "only an active connection is publishable, which is why the tokens can survive the state change");
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
    public void StatusWireValues_AreLowerCase()
    {
        // `HasEnumCheck` stores the member name through EnumToSnakeCaseStringConverter and the CHECK lists the
        // same four strings; the DTOs spell it with the same JsonNamingPolicy conversion, so the column, the
        // constraint and the JSON cannot drift. The Domain test cannot reach the Infrastructure converter, so
        // it pins the shared conversion and the exact wire values the migration's CHECK names.
        var wireValues = Enum.GetValues<ChannelConnectionStatus>()
            .Select(status => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString()))
            .ToArray();

        wireValues.ShouldBe(["active", "expired", "error", "revoked"], ignoreOrder: true);
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

        // The type deliberately keeps object.ToString(): a log line or a debugger view of a connection can never
        // print a token, the channel title, the Google channel id or the pinned client id.
        connection.ToString()!.ShouldNotContain("cfDJ8", Case.Insensitive);
        connection.ToString().ShouldBe("NightTales.Domain.Publishing.ChannelConnection");

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
