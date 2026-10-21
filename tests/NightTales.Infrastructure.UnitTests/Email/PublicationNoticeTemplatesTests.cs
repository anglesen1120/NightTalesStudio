using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Features.Email;
using NightTales.Application.Features.Email.TokenMinters;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Email;
using NightTales.TestKit.Email;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Email;

/// <summary>
/// The drift guard of the two templates 06.2.2-T2 adds (<c>publication_consented</c> and
/// <c>publication_veto_notice</c>, 06 README CR-6): they must stay catalog entries, ship the four files of BD9,
/// and render with the keys their catalog rows declare — including the minter's <c>cancelUrl</c>, and
/// excluding the producer-only keys that are deliberately never rendered (<c>consentSource</c>,
/// <c>publicationId</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists next to <see cref="EmailTemplateProviderTests"/>.</b> Those tests own the generic rules —
/// an undeclared placeholder, an orphan file, a catalog entry whose file is missing, and that every
/// <see cref="EmailTemplateCatalog.All"/> entry ships all four of its files — but they iterate the catalog, so
/// deleting a template's row together with its four files leaves nothing orphaned and nothing missing and the
/// host still starts. Pinning the identity of the two templates this task adds is the part those rules provably
/// cannot cover.
/// </para>
/// <para>
/// The renderer is taken from the real composition (<c>AddEmail</c>), exactly as <see cref="EmailRendererTests"/>
/// does: what these tests read is the same instance a host renders with, and the same HTML encoding rule — values
/// are encoded by the renderer, never by the template file.
/// </para>
/// </remarks>
public sealed class PublicationNoticeTemplatesTests
{
    private static readonly Guid PublicationId = Guid.Parse("018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f");

    private static readonly IEmailRenderer Renderer = CreateProvider().GetRequiredService<IEmailRenderer>();

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
    public void ShippedTemplates_AreCatalogEntriesAndServeBothLocalesAndFormats()
    {
        // FromDeployedFiles() runs ValidateOrThrow over the real shipped set, so a missing, orphaned or
        // placeholder-broken file of either template fails here too.
        var provider = EmailTemplateProvider.FromDeployedFiles();

        string[] templates =
        [
            EmailTemplateCatalog.PublicationConsented,
            EmailTemplateCatalog.PublicationVetoNotice,
        ];

        foreach (var template in templates)
        {
            EmailTemplateCatalog.Find(template).ShouldNotBeNull(
                $"'{template}' must stay a catalog entry to be queued and rendered (06.2.2-T2)");

            foreach (var locale in Enum.GetValues<Language>())
            {
                foreach (var format in Enum.GetValues<EmailTemplateFormat>())
                {
                    var resource = provider.Get(template, locale, format);

                    resource.Content.ShouldNotBeNullOrWhiteSpace();
                    resource.FileName.ShouldBe(EmailTemplateResource.FileNameOf(template, locale, format));
                }
            }
        }
    }

    [Fact]
    public void ConsentedTemplate_RendersEveryLocale_WithTheDeclaredKeysAndTheRenderersEncoding()
    {
        foreach (var locale in Enum.GetValues<Language>())
        {
            var rendered = Renderer.Render(
                EmailTemplateCatalog.PublicationConsented,
                locale,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["displayName"] = "<b>An</b>",
                    ["channelTitle"] = "Late Night Tales",
                    ["videoTitle"] = "The Lighthouse — Episode 1",
                    ["privacy"] = "private",
                    ["privacyNote"] = "(will be published as Private because the project has not passed Google's audit)",
                    ["scheduledAt"] = string.Empty,
                    ["publicationUrl"] = "https://app.nighttales.app/publications/" + PublicationId.ToString("D"),
                    ["approvalLine"] = locale == Language.Vi ? "Bạn đã duyệt video này." : "You approved this upload.",

                    // A producer key that must never be rendered: a marker proves it stays out of both bodies.
                    ["consentSource"] = "consent-source-marker",
                });

            rendered.Subject.ShouldNotContain("{{");
            rendered.Subject.ShouldContain("The Lighthouse");

            // The renderer encodes html substitutes and leaves the text body verbatim (EmailRenderer).
            rendered.HtmlBody.ShouldContain("&lt;b&gt;An&lt;/b&gt;");
            rendered.HtmlBody.ShouldNotContain("<b>An</b>");
            rendered.TextBody.ShouldContain("<b>An</b>");

            rendered.TextBody.ShouldNotContain("{{");
            rendered.HtmlBody.ShouldNotContain("{{");
            rendered.TextBody.ShouldNotContain("consent-source-marker");
            rendered.HtmlBody.ShouldNotContain("consent-source-marker");

            // An empty `scheduledAt` (an immediate upload) leaves a label, not a dangling unit: the UTC unit
            // sits before the value, so what is left is a heading and the explanation sentence below it.
            rendered.TextBody.ShouldContain(locale == Language.Vi ? "Thời gian đăng (UTC):" : "Publishing slot (UTC):");
        }
    }

    [Fact]
    public void VetoNoticeTemplate_RendersTheCancelLinkAndUsesThePublicationIdOnlyInsideItsLinks()
    {
        const string CancelUrl =
            "https://app.nighttales.app/publications/cancel#p=018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f&t=cancel-tok-123";

        foreach (var locale in Enum.GetValues<Language>())
        {
            var rendered = Renderer.Render(
                EmailTemplateCatalog.PublicationVetoNotice,
                locale,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["displayName"] = "An",
                    ["channelTitle"] = "Late Night Tales",
                    ["videoTitle"] = "The Lighthouse — Episode 1",
                    ["description"] = "A quiet start on a loud coast.",
                    ["tags"] = "horror, radio drama",
                    ["privacy"] = "private",
                    ["privacyNote"] = string.Empty,
                    ["scheduledAt"] = string.Empty,
                    ["vetoEndsAt"] = "2026-01-02T00:00:00.0000000+00:00",
                    ["publicationUrl"] = "https://app.nighttales.app/publications/" + PublicationId.ToString("D"),
                    [PublicationCancelTokenMinter.CancelUrlKey] = CancelUrl,
                    [PublicationCancelTokenMinter.PublicationIdParameter] = PublicationId.ToString("D"),
                });

            rendered.TextBody.ShouldContain(CancelUrl);

            // The html body substitutes the value HTML-encoded, so the `&` between the two fragment members is
            // the one visible difference — which is also the proof that the renderer, and not the template, owns
            // the escaping rule.
            rendered.HtmlBody.ShouldContain(CancelUrl.Replace("&", "&amp;", StringComparison.Ordinal));
            rendered.TextBody.ShouldNotContain("{{");
            rendered.HtmlBody.ShouldNotContain("{{");

            // `publicationId` is consumed by the minter and never rendered as a value of its own: the id reaches
            // the body only through the two links that carry it (the edit link and the cancel link's fragment),
            // so a third occurrence would mean a `{{publicationId}}` placeholder crept into a template file.
            CountOf(rendered.TextBody, PublicationId.ToString("D")).ShouldBe(2);
            CountOf(rendered.HtmlBody, PublicationId.ToString("D")).ShouldBe(2);
        }
    }

    private static int CountOf(string haystack, string needle) =>
        haystack.Split(needle, StringSplitOptions.None).Length - 1;
}
