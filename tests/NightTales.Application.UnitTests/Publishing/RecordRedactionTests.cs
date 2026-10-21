using NightTales.Api.Contracts.Publishing;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect;
using NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;
using NightTales.Domain.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// R8/F4: a positional record keeps the compiler-generated <c>ToString</c>, which prints every member — so any
/// record that carries a bearer secret must override <c>PrintMembers</c> and print only the safe ones. This test
/// feeds each such record a low-entropy canary secret and proves the canary never reaches the string, while the
/// type name still does (the record <c>ToString</c> shape is untouched).
/// </summary>
public sealed class RecordRedactionTests
{
    /// <summary>A low-entropy canary: it can trip no secret scanner and cannot be mistaken for a real value.</summary>
    private const string Canary = "canary-secret-aaaa";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid SomeId = Guid.Parse("01920000-0000-7000-8000-000000000001");

    [Fact]
    public void OAuthTokens_DoesNotPrintItsBearerTokens()
    {
        var tokens = new OAuthTokens(Canary, Canary, Now, "scope");

        AssertRedacted(tokens);
    }

    [Fact]
    public void OAuthClient_DoesNotPrintItsSecret()
    {
        var client = new OAuthClient("client-a", Canary);

        AssertRedacted(client);
    }

    [Fact]
    public void OAuthStateClaim_DoesNotPrintTheVerifierOrTheNonceHash()
    {
        var claim = new OAuthStateClaim(SomeId, SomeId, Platform.YouTube, Canary, "client-a", SomeId, Canary);

        AssertRedacted(claim);
    }

    [Fact]
    public void ConnectYouTubeResponse_DoesNotPrintTheAuthorizationUrlOrTheNonce()
    {
        var response = new ConnectYouTubeResponse("https://accounts.google.com/o/oauth2/v2/auth?state=" + Canary, Now, Canary);

        AssertRedacted(response);
    }

    [Fact]
    public void ConnectChannelResponse_DoesNotPrintTheAuthorizationUrl()
    {
        var response = new ConnectChannelResponse("https://accounts.google.com/o/oauth2/v2/auth?state=" + Canary, Now);

        AssertRedacted(response);
    }

    [Fact]
    public void ConnectChannelConnectionRequest_DoesNotPrintItsNestedTokens()
    {
        var request = new ConnectChannelConnectionRequest(
            SomeId,
            Platform.YouTube,
            new GoogleChannel("channel-1", "Title"),
            new OAuthTokens(Canary, Canary, Now, "scope"),
            "client-a",
            Now);

        AssertRedacted(request);
    }

    [Fact]
    public void CompleteYouTubeConnectCommand_DoesNotPrintTheCodeStateOrNonce()
    {
        var command = new CompleteYouTubeConnectCommand(
            Platform.YouTube,
            Canary,
            Canary,
            Canary,
            Canary,
            new RequestInfo("203.0.113.7", null));

        AssertRedacted(command);
    }

    [Fact]
    public void VideoInsertRequest_DoesNotPrintTheTitleTheDescriptionOrTheTags()
    {
        // D17: a title, a description and the tags are user content, and this record is the one the upload path
        // logs and audits; its printed form carries lengths and counts only, so a log line, an exception message or
        // a debugger string never shows the text.
        var request = new VideoInsertRequest(
            $"Title {Canary}",
            $"Description {Canary}",
            [Canary, $"tag-{Canary}"],
            "private",
            Now,
            ContainsSyntheticMedia: true,
            SelfDeclaredMadeForKids: false,
            Length: 1024,
            MimeType: "video/mp4");

        AssertRedacted(request);

        var text = request.ToString()!;

        text.ShouldContain("TitleLength = ");
        text.ShouldContain("DescriptionLength = ");
        text.ShouldContain("TagCount = 2");
    }

    /// <summary>The string omits the canary and still names the record type (its <c>ToString</c> shape survives).</summary>
    private static void AssertRedacted(object record)
    {
        var text = record.ToString() ?? string.Empty;

        text.ShouldNotContain(Canary);
        text.ShouldContain(record.GetType().Name);
    }
}
