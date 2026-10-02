using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Email;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Email;
using NightTales.TestKit.Email;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Email;

/// <summary>
/// The renderer of 09.2.6-T3 (09 README BD9): values are HTML-encoded in the html body and verbatim in the
/// text body, the subject comes from the text file, and a placeholder the caller did not collect is a
/// failure rather than a message with a literal <c>{{key}}</c> in it.
/// </summary>
public sealed class EmailRendererTests
{
    // `EmailRenderer` is internal, so the test takes the renderer from the host's own composition —
    // `AddEmail` — and exercises exactly the instance a host renders with (09 README D13).
    private static readonly ServiceProvider Provider = CreateProvider();

    private static readonly IEmailRenderer Renderer = Provider.GetRequiredService<IEmailRenderer>();

    /// <summary>The smallest composition that carries the renderer: <c>AddEmail</c> and its origin key.</summary>
    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [EmailTestSettings.PublicOriginKey] = EmailTestSettings.PublicOrigin,
            })
            .Build();

        return new ServiceCollection().AddEmail(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Render_HtmlEncodesSubstitutedValue()
    {
        var rendered = Renderer.Render(
            "password_changed",
            Language.Vi,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["displayName"] = "<b>An</b>",
                ["occurredAt"] = "2026-01-01 10:00",
            });

        rendered.HtmlBody.ShouldContain("&lt;b&gt;An&lt;/b&gt;");
        rendered.TextBody.ShouldContain("<b>An</b>");
        rendered.Subject.ShouldNotBeNullOrWhiteSpace();
        rendered.Subject.ShouldNotContain("Subject:");
    }

    [Fact]
    public void Render_UndeclaredKey_Throws()
    {
        // `occurredAt` is a declared key of `password_changed`, so leaving it out is the caller asking for a
        // message with a literal placeholder: the renderer refuses it and names the file (09 README BD9).
        var exception = Should.Throw<EmailTemplateException>(() => Renderer.Render(
            "password_changed",
            Language.Vi,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["displayName"] = "An",
            }));

        exception.Message.ShouldContain("password_changed.vi.txt");
        exception.Message.ShouldContain("occurredAt");
    }

    [Fact]
    public void Render_TokenBearingTemplate_RendersTheMintedLink()
    {
        var rendered = Renderer.Render(
            "password_reset",
            Language.En,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["displayName"] = "Owner",
                ["expiresMinutes"] = "60",
                ["link"] = "https://app.nighttales.test/reset-password#t=abc",
            });

        rendered.TextBody.ShouldContain("https://app.nighttales.test/reset-password#t=abc");
        rendered.HtmlBody.ShouldContain("https://app.nighttales.test/reset-password#t=abc");
    }

    [Fact]
    public void Render_SubjectValueWithLineBreak_Throws()
    {
        // `approval_cancel` renders {{requestKind}} into its subject line; a line break inside that value
        // would end the subject early and push the rest into the body, so the renderer refuses it
        // (09.2.6-T3).
        var exception = Should.Throw<EmailTemplateException>(() => Renderer.Render(
            "approval_cancel",
            Language.En,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["displayName"] = "Owner",
                ["requestKind"] = "privacy\r\nBcc: attacker@evil.test",
                ["reason"] = "reason",
                ["deadline"] = "2026-01-02 00:00",
                ["requestId"] = "req-1",
                ["cancelLink"] = "https://app.nighttales.test/cancel#t=abc",
            }));

        exception.Message.ShouldContain("requestKind");
        exception.Message.ShouldContain("approval_cancel.en.txt");
    }

    [Fact]
    public void Render_BodyValueWithLineBreak_IsVerbatim()
    {
        // The guard is scoped to the subject line: a line break in a body value stays legal.
        var rendered = Renderer.Render(
            "approval_cancel",
            Language.En,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["displayName"] = "Owner",
                ["requestKind"] = "privacy",
                ["reason"] = "first line\r\nsecond line",
                ["deadline"] = "2026-01-02 00:00",
                ["requestId"] = "req-1",
                ["cancelLink"] = "https://app.nighttales.test/cancel#t=abc",
            });

        rendered.Subject.ShouldContain("privacy");
        rendered.TextBody.ShouldContain("second line");
    }
}
