using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Autopilot;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Autopilot;

/// <summary>
/// Plan §3.7's authorization row (<see cref="SeriesAutopilotAuthorization"/>): the acceptance factory, the
/// idempotent revoke and the six wire reasons the stored column and its check constraint carry.
/// </summary>
/// <remarks>
/// Pure tests: no database, no clock, no context. The hash and the version are the two inputs the row cannot
/// repair, so their refusals are asserted here; the hash <em>matching</em> the rendered text is the accept
/// command's comparison and is tested where it runs.
/// </remarks>
public sealed class SeriesAutopilotAuthorizationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.Parse("018f6c1e-0000-7000-8000-000000000001");

    private static readonly Guid SeriesId = Guid.Parse("018f6c1e-0000-7000-8000-000000000002");

    private static readonly Guid ChannelId = Guid.Parse("018f6c1e-0000-7000-8000-000000000003");

    /// <summary>A lowercase SHA-256 hex digest, the only shape <see cref="SeriesAutopilotAuthorization.Accept"/> stores.</summary>
    private const string Hash = "8b1a9953c4611296a827abf8c47804d7e6d2a1f5c0b1d9f0e5a7c3b9d1e2f4a6";

    [Fact]
    public void Accept_HappyPath_StoresEveryColumn()
    {
        var authorization = Accept();

        authorization.Id.ShouldNotBe(Guid.Empty);
        authorization.UserId.ShouldBe(UserId);
        authorization.SeriesId.ShouldBe(SeriesId);
        authorization.Version.ShouldBe(3);
        authorization.TextVersion.ShouldBe("autopilot-auth.v1");
        authorization.TextHash.ShouldBe(Hash);
        authorization.Topic.ShouldBe(Topic.Ghost);
        authorization.ChannelConnectionId.ShouldBe(ChannelId);
        authorization.Cadence.ShouldBe("""{"snapshot":1}""");
        authorization.PrivacyStatus.ShouldBe(PrivacyStatus.Private);
        authorization.AcceptedAt.ShouldBe(FixedNow);
        authorization.CreatedAt.ShouldBe(FixedNow);
        authorization.UpdatedAt.ShouldBe(FixedNow);
        authorization.RevokedAt.ShouldBeNull();
        authorization.RevokedReason.ShouldBeNull();
        authorization.IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public void Accept_EachAcceptance_HasItsOwnId()
    {
        Accept().Id.ShouldNotBe(Accept().Id);
    }

    [Theory]
    [InlineData("8b1a9953c4611296a827abf8c47804d7e6d2a1f5c0b1d9f0e5a7c3b9d1e2f4a")] // 63 characters
    [InlineData("8b1a9953c4611296a827abf8c47804d7e6d2a1f5c0b1d9f0e5a7c3b9d1e2f4a6b")] // 65
    [InlineData("8B1A9953C4611296A827ABF8C47804D7E6D2A1F5C0B1D9F0E5A7C3B9D1E2F4A6")] // uppercase
    [InlineData("8b1a9953c4611296a827abf8c47804d7e6d2a1f5c0b1d9f0e5a7c3b9d1e2f4zz")] // not hex
    [InlineData("")]
    public void Accept_TextHashThatIsNotLowercaseSha256Hex_Throws(string textHash)
    {
        Should.Throw<ArgumentException>(() => SeriesAutopilotAuthorization.Accept(
            UserId,
            SeriesId,
            1,
            "autopilot-auth.v1",
            textHash,
            Topic.Ghost,
            ChannelId,
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Accept_VersionBelowOne_Throws(int version)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SeriesAutopilotAuthorization.Accept(
            UserId,
            SeriesId,
            version,
            "autopilot-auth.v1",
            Hash,
            Topic.Ghost,
            ChannelId,
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Fact]
    public void Accept_EmptyUserId_Throws()
    {
        Should.Throw<ArgumentException>(() => SeriesAutopilotAuthorization.Accept(
            Guid.Empty,
            SeriesId,
            1,
            "autopilot-auth.v1",
            Hash,
            Topic.Ghost,
            ChannelId,
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Fact]
    public void Accept_EmptySeriesId_Throws()
    {
        Should.Throw<ArgumentException>(() => SeriesAutopilotAuthorization.Accept(
            UserId,
            Guid.Empty,
            1,
            "autopilot-auth.v1",
            Hash,
            Topic.Ghost,
            ChannelId,
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Fact]
    public void Accept_EmptyChannelConnection_Throws()
    {
        // The text names the channel it covers, so a row without one can never be diffed (plan §3.7).
        Should.Throw<ArgumentException>(() => SeriesAutopilotAuthorization.Accept(
            UserId,
            SeriesId,
            1,
            "autopilot-auth.v1",
            Hash,
            Topic.Ghost,
            Guid.Empty,
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Accept_EmptyTextVersion_Throws(string textVersion)
    {
        Should.Throw<ArgumentException>(() => SeriesAutopilotAuthorization.Accept(
            UserId,
            SeriesId,
            1,
            textVersion,
            Hash,
            Topic.Ghost,
            ChannelId,
            """{"snapshot":1}""",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Fact]
    public void Accept_EmptySnapshot_Throws()
    {
        Should.Throw<ArgumentException>(() => SeriesAutopilotAuthorization.Accept(
            UserId,
            SeriesId,
            1,
            "autopilot-auth.v1",
            Hash,
            Topic.Ghost,
            ChannelId,
            "  ",
            PrivacyStatus.Private,
            FixedNow));
    }

    [Fact]
    public void Revoke_FirstCall_SetsTheReasonAndTheInstant()
    {
        var authorization = Accept();
        var revokedAt = FixedNow.AddHours(1);

        var changed = authorization.Revoke(AutopilotAuthorizationRevokeReason.TextChanged, revokedAt);

        changed.ShouldBeTrue();
        authorization.IsRevoked.ShouldBeTrue();
        authorization.RevokedAt.ShouldBe(revokedAt);
        authorization.RevokedReason.ShouldBe(AutopilotAuthorizationRevokeReason.TextChanged);
        authorization.UpdatedAt.ShouldBe(revokedAt);
        authorization.AcceptedAt.ShouldBe(FixedNow, "revoking never rewrites what the owner accepted");
        authorization.TextHash.ShouldBe(Hash);
    }

    [Fact]
    public void Revoke_SecondCall_KeepsTheFirstReasonAndAnswersFalse()
    {
        var authorization = Accept();
        authorization.Revoke(AutopilotAuthorizationRevokeReason.Disabled, FixedNow.AddHours(1));

        var changed = authorization.Revoke(AutopilotAuthorizationRevokeReason.OwnerRevoked, FixedNow.AddHours(2));

        changed.ShouldBeFalse();
        authorization.RevokedReason.ShouldBe(AutopilotAuthorizationRevokeReason.Disabled);
        authorization.RevokedAt.ShouldBe(FixedNow.AddHours(1));
        authorization.UpdatedAt.ShouldBe(FixedNow.AddHours(1), "a refused revoke changes nothing");
    }

    [Fact]
    public void RevokeReasons_ToWire_IsThePlanWireValue()
    {
        AutopilotAuthorizationRevokeReason.Disabled.ToWire().ShouldBe("disabled");
        AutopilotAuthorizationRevokeReason.TextChanged.ToWire().ShouldBe("text_changed");
        AutopilotAuthorizationRevokeReason.TopicChanged.ToWire().ShouldBe("topic_changed");
        AutopilotAuthorizationRevokeReason.ChannelChanged.ToWire().ShouldBe("channel_changed");
        AutopilotAuthorizationRevokeReason.Superseded.ToWire().ShouldBe("superseded");
        AutopilotAuthorizationRevokeReason.OwnerRevoked.ToWire().ShouldBe("owner_revoked");
    }

    [Fact]
    public void RevokeReasons_All_IsTheSixWireValues()
    {
        AutopilotAuthorizationRevokeReasons.All.ShouldBe(
            ["disabled", "text_changed", "topic_changed", "channel_changed", "superseded", "owner_revoked"]);
    }

    [Fact]
    public void RevokeReasons_TryParse_RoundTripsEveryMember()
    {
        foreach (var reason in Enum.GetValues<AutopilotAuthorizationRevokeReason>())
        {
            AutopilotAuthorizationRevokeReasons.TryParse(reason.ToWire(), out var parsed).ShouldBeTrue();

            parsed.ShouldBe(reason);
        }
    }

    [Theory]
    [InlineData("TEXT_CHANGED")]
    [InlineData("TextChanged")]
    [InlineData("text-changed")]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void RevokeReasons_TryParse_RefusesAnythingElse(string? wire)
    {
        AutopilotAuthorizationRevokeReasons.TryParse(wire, out var reason).ShouldBeFalse();
        reason.ShouldBe(default);
    }

    private static SeriesAutopilotAuthorization Accept() => SeriesAutopilotAuthorization.Accept(
        UserId,
        SeriesId,
        3,
        "autopilot-auth.v1",
        Hash,
        Topic.Ghost,
        ChannelId,
        """{"snapshot":1}""",
        PrivacyStatus.Private,
        FixedNow);
}
