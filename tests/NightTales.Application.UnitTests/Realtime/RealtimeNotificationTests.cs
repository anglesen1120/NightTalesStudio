using System.Text.Json;
using NightTales.Application.Abstractions.Realtime;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Realtime;

/// <summary>
/// The parse of the <c>LISTEN</c> side (09.8.1-T1 plan §5): a <c>realtime</c> body becomes a
/// notification with the user it is routed to, and a <c>security</c> body is the user id alone. A message
/// the listener does not serve is refused <em>without throwing</em>, because a producer of a later wave
/// must not be able to take an Api instance's loop down.
/// </summary>
public sealed class RealtimeNotificationTests
{
    private static readonly Guid UserId = Guid.Parse("018f2c58-4444-7a5b-8f7a-1c2d3e4f5a64");

    [Fact]
    public void TryParse_RealtimeBody_CarriesUserIdEventAndBody()
    {
        var body = $$"""{"userId":"{{UserId:D}}","event":"episode_updated","ids":{"episodeId":null},"data":null}""";

        RealtimeNotification.TryParse(RealtimeNotification.RealtimeChannel, body, out var notification)
            .ShouldBeTrue();

        notification.ShouldNotBeNull();
        notification!.Channel.ShouldBe(RealtimeNotification.RealtimeChannel);
        notification.UserId.ShouldBe(UserId);
        notification.EventName.ShouldBe("episode_updated");

        // The forwarded body is detached from the parser's document, so a later read cannot fail.
        notification.Body.ShouldNotBeNull();
        notification.Body!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
        notification.Body.Value.GetProperty("event").GetString().ShouldBe("episode_updated");
    }

    [Fact]
    public void TryParse_SecurityBody_IsTheUserIdAlone()
    {
        RealtimeNotification.TryParse(
                RealtimeNotification.SecurityChannel,
                UserId.ToString("D"),
                out var notification)
            .ShouldBeTrue();

        notification.ShouldNotBeNull();
        notification!.Channel.ShouldBe(RealtimeNotification.SecurityChannel);
        notification.UserId.ShouldBe(UserId);
        notification.EventName.ShouldBeNull();
        notification.Body.ShouldBeNull();
    }

    [Fact]
    public void TryParse_UnknownChannel_IsRefused()
    {
        RealtimeNotification.TryParse("other", "{}", out var notification).ShouldBeFalse();
        notification.ShouldBeNull();
    }

    [Fact]
    public void TryParse_UnparsableMessage_IsRefused()
    {
        // Not JSON at all.
        RealtimeNotification.TryParse(RealtimeNotification.RealtimeChannel, "not-json", out _).ShouldBeFalse();

        // JSON, but not an object with a usable user id.
        RealtimeNotification.TryParse(RealtimeNotification.RealtimeChannel, "[]", out _).ShouldBeFalse();
        RealtimeNotification.TryParse(RealtimeNotification.RealtimeChannel, "{}", out _).ShouldBeFalse();
        RealtimeNotification.TryParse(
            RealtimeNotification.RealtimeChannel,
            """{"userId":"not-a-guid"}""",
            out _)
            .ShouldBeFalse();

        // Guid.Empty is not a user: routing it would target a group nobody can own.
        RealtimeNotification.TryParse(
            RealtimeNotification.RealtimeChannel,
            $$"""{"userId":"{{Guid.Empty:D}}"}""",
            out _)
            .ShouldBeFalse();

        // Nor is a security payload that is not a user id.
        RealtimeNotification.TryParse(RealtimeNotification.SecurityChannel, "nobody", out _).ShouldBeFalse();
        RealtimeNotification.TryParse(RealtimeNotification.SecurityChannel, "{}", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_UnknownEventName_IsKept_SoTheListenerCanIgnoreIt()
    {
        // A fifth event of a later wave (02.8.2 L227-228 asks for one) is not an error at the transport
        // boundary: the name is kept so the listener can log it and forward nothing (plan §5).
        var body = $$$"""{"userId":"{{{UserId:D}}}","event":"autopilot_run_updated","ids":{}}""";

        RealtimeNotification.TryParse(RealtimeNotification.RealtimeChannel, body, out var notification)
            .ShouldBeTrue();

        notification.ShouldNotBeNull();
        notification!.EventName.ShouldBe("autopilot_run_updated");
        RealtimeEventNames.TryParseWireName(notification.EventName, out _).ShouldBeFalse();

        // An absent event member is the same case: kept as null, not a parse failure.
        RealtimeNotification.TryParse(
                RealtimeNotification.RealtimeChannel,
                $$"""{"userId":"{{UserId:D}}"}""",
                out var withoutEvent)
            .ShouldBeTrue();

        withoutEvent.ShouldNotBeNull();
        withoutEvent!.EventName.ShouldBeNull();
        withoutEvent.Body.ShouldNotBeNull();
    }
}
