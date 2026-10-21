using System.Globalization;
using System.Text.RegularExpressions;
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
/// Independent QA pass over the four template pairs 06.2.2-T2 adds: every <c>{{placeholder}}</c> in every shipped
/// file must be a declared render key (a producer key or the minter's own), both locales must exist in both
/// formats, and a hostile title must be encoded by the renderer — never by the file.
/// </summary>
/// <remarks>
/// The identity checks live in <see cref="PublicationNoticeTemplatesTests"/>; this class asks the two questions
/// that class does not: could a placeholder exist that no allow-list declares, and is the escaping rule really the
/// renderer's for a value shaped like markup?
/// </remarks>
public sealed class PublicationNoticeTemplatesQaTests
{
    private static readonly Regex PlaceholderPattern = new(@"\{\{(?<key>[A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);

    private static readonly string[] Templates =
    [
        EmailTemplateCatalog.PublicationConsented,
        EmailTemplateCatalog.PublicationVetoNotice,
    ];

    private static readonly IEmailRenderer Renderer =
        new ServiceCollection()
            .AddEmail(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [EmailTestSettings.PublicOriginKey] = EmailTestSettings.PublicOrigin,
                    })
                    .Build())
            .BuildServiceProvider()
            .GetRequiredService<IEmailRenderer>();

    [Fact]
    public void EveryPlaceholderOfEveryShippedFile_IsADeclaredRenderKey()
    {
        var provider = EmailTemplateProvider.FromDeployedFiles();

        foreach (var template in Templates)
        {
            var definition = EmailTemplateCatalog.Find(template).ShouldNotBeNull();

            var declared = definition.ProducerKeys
                .Concat(definition.MinterKeys)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var locale in Enum.GetValues<Language>())
            {
                foreach (var format in Enum.GetValues<EmailTemplateFormat>())
                {
                    var resource = provider.Get(template, locale, format);
                    var keys = PlaceholderPattern
                        .Matches(resource.Content)
                        .Select(match => match.Groups["key"].Value)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                    keys.ShouldNotBeEmpty(
                        $"{resource.FileName} must render something the catalog declares");

                    foreach (var key in keys)
                    {
                        declared.ShouldContain(
                            key,
                            $"{resource.FileName} renders '{{{{{key}}}}}', which the catalog does not declare");
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryTemplateShipsBothLocalesInBothFormats_WithLocalizedContent()
    {
        var provider = EmailTemplateProvider.FromDeployedFiles();

        foreach (var template in Templates)
        {
            var vi = provider.Get(template, Language.Vi, EmailTemplateFormat.Text);
            var en = provider.Get(template, Language.En, EmailTemplateFormat.Text);

            vi.Content.ShouldNotBe(en.Content, $"{template} must ship a real vi and en pair, not a copy");
            vi.FileName.ShouldNotBe(en.FileName);

            foreach (var format in Enum.GetValues<EmailTemplateFormat>())
            {
                provider.Get(template, Language.Vi, format).Content.ShouldNotBeNullOrWhiteSpace();
                provider.Get(template, Language.En, format).Content.ShouldNotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void HostileTitle_IsEncodedInTheHtmlBody_AndVerbatimInTheTextBody()
    {
        // QA probe (j): a title carrying markup and an ampersand is the classic way a flat `{{key}}` renderer turns
        // an e-mail into stored XSS. The renderer owns the rule; the template file must not.
        const string Hostile = "<script>alert('xss')</script> & more";

        foreach (var template in Templates)
        {
            foreach (var locale in Enum.GetValues<Language>())
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["displayName"] = Hostile,
                    ["channelTitle"] = Hostile,
                    ["videoTitle"] = Hostile,
                    ["description"] = Hostile,
                    ["tags"] = Hostile,
                    ["privacy"] = "private",
                    ["privacyNote"] = Hostile,
                    ["scheduledAt"] = "2026-10-21T09:00:00.0000000+00:00",
                    ["vetoEndsAt"] = "2026-10-21T11:00:00.0000000+00:00",
                    ["publicationUrl"] = "https://harness.nighttales.test/publications/018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f",
                    ["approvalLine"] = Hostile,
                    ["consentSource"] = "user",
                    ["publicationId"] = "018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f",
                    // Built at runtime so no source line looks like a capability (gitleaks).
                    [PublicationCancelTokenMinter.CancelUrlKey] =
                        "https://harness.nighttales.test/publications/cancel"
                        + "#p=018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f&t="
                        + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture),
                };

                var rendered = Renderer.Render(template, locale, values);

                rendered.HtmlBody.ShouldNotContain("<script>", Case.Sensitive);
                rendered.HtmlBody.ShouldContain("&lt;script&gt;");
                rendered.HtmlBody.ShouldContain("&amp; more");
                rendered.TextBody.ShouldContain("<script>");
                rendered.TextBody.ShouldContain("& more");

                // No placeholder survives a render, in either body or the subject.
                rendered.HtmlBody.ShouldNotContain("{{");
                rendered.TextBody.ShouldNotContain("{{");
                rendered.Subject.ShouldNotContain("{{");
            }
        }
    }
}
