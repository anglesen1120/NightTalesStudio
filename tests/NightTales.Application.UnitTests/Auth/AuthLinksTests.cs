using NightTales.Application.Features.Auth.Password;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Auth;

/// <summary>
/// The link shape of 09.3.3-T1's <see cref="AuthLinks"/> (Tests table): the single-use token always sits
/// <em>after</em> the <c>#</c>, so no server, proxy or <c>Referer</c> header ever sees it (09 README
/// BD3/WF11).
/// </summary>
public sealed class AuthLinksTests
{
    private static readonly Uri Origin = new("https://app.nighttales.app");

    [Fact]
    public void Links_PutTheTokenAfterTheHash()
    {
        var reset = AuthLinks.PasswordReset(Origin, "tok-123");

        reset.ShouldBe("https://app.nighttales.app/reset-password#t=tok-123");

        // The contract, not the formatting: everything after the first `#` is the fragment the browser
        // keeps to itself.
        reset[(reset.IndexOf('#', StringComparison.Ordinal) + 1)..].ShouldBe("t=tok-123");
        reset.ShouldNotContain("?t=");
        reset.ShouldNotContain("&t=");

        AuthLinks.VerifyEmail(Origin, "tok-123").ShouldBe("https://app.nighttales.app/verify-email#t=tok-123");
    }

    [Fact]
    public void Links_UseTheAuthority_AndKeepANonDefaultPort()
    {
        // `App:PublicOrigin` may be configured with a path; the link uses only the authority, so a stray
        // path can never land in the middle of the route.
        AuthLinks.PasswordReset(new Uri("https://app.nighttales.app/ignored/path"), "t")
            .ShouldBe("https://app.nighttales.app/reset-password#t=t");

        AuthLinks.PasswordReset(new Uri("http://localhost:4200"), "t")
            .ShouldBe("http://localhost:4200/reset-password#t=t");
    }

    [Fact]
    public void Links_RejectAMissingToken()
    {
        Should.Throw<ArgumentException>(() => AuthLinks.PasswordReset(Origin, " "));
        Should.Throw<ArgumentNullException>(() => AuthLinks.PasswordReset(null!, "t"));
    }

    [Fact]
    public void PublicationCancel_PutsTheTokenAfterTheHash()
    {
        // 06.2.2-T2 (06 CR-6): the same fragment invariant as reset/verify, plus the publication the token is
        // scoped to — the page reads both from the fragment, so neither reaches a server log nor a `Referer`.
        var publicationId = Guid.Parse("018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f");

        var cancel = AuthLinks.PublicationCancel(Origin, publicationId, "tok-123");

        cancel.ShouldBe("https://app.nighttales.app/publications/cancel#p=018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f&t=tok-123");

        cancel[(cancel.IndexOf('#', StringComparison.Ordinal) + 1)..]
            .ShouldBe("p=018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f&t=tok-123");

        // The authority is the only part of the origin that a link uses, exactly as for the other two.
        AuthLinks.PublicationCancel(new Uri("https://app.nighttales.app/ignored/path"), publicationId, "tok-123")
            .ShouldBe(cancel);

        Should.Throw<ArgumentException>(() => AuthLinks.PublicationCancel(Origin, publicationId, " "));
        Should.Throw<ArgumentNullException>(() => AuthLinks.PublicationCancel(null!, publicationId, "t"));
    }
}
