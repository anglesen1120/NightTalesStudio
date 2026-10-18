using NightTales.Application.Features.Email;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Email;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Email;

/// <summary>
/// The drift guard of the one template 06.1.2-T2 adds (<c>channel_connected</c>, 06 README CR-6): the template
/// must stay a catalog entry and the shipped set must serve it in both locales and both formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists next to <see cref="EmailTemplateProviderTests"/>.</b> Those tests already own the generic
/// rules — a text file without a subject, an undeclared placeholder, an orphan file, a catalog entry whose file
/// is missing (by removing <c>password_reset.en.html</c>), and that every <see cref="EmailTemplateCatalog.All"/>
/// entry ships all four of its files. They cannot, however, see the <em>absence</em> of this template: they
/// iterate the catalog, so deleting the <c>channel_connected</c> row and its four files together leaves nothing
/// orphaned and nothing missing, the host still starts, and the connect flow (06.1.2-T2, 06 README CR-6) queues
/// a message no template can render. Pinning the identity of the template this task adds is the part the generic
/// rules provably cannot cover, and it is this class's whole job — the file-level validation rules are left to
/// the generic tests.
/// </para>
/// <para>
/// The set is read with <see cref="EmailTemplateProvider.ReadDeployedFiles"/>, so what this guards is the real
/// output of the build (the templates copied beside this project's own assembly), not a hand-written fixture.
/// </para>
/// </remarks>
public sealed class ChannelConnectedTemplateTests
{
    /// <summary>The catalog name 06.1.2-T2 adds.</summary>
    private const string Template = "channel_connected";

    [Fact]
    public void ShippedTemplate_IsADeclaredCatalogEntryAndServesBothLocalesAndFormats()
    {
        EmailTemplateCatalog.Find(Template).ShouldNotBeNull(
            "the connect e-mail must stay a catalog entry to be queued and rendered (06.1.2-T2 §E-mail template)");

        // FromDeployedFiles() runs ValidateOrThrow over the real shipped set, so a missing, orphaned or
        // placeholder-broken file of this template fails here too, and Get proves it is actually renderable.
        var provider = EmailTemplateProvider.FromDeployedFiles();

        foreach (var locale in Enum.GetValues<Language>())
        {
            foreach (var format in Enum.GetValues<EmailTemplateFormat>())
            {
                var resource = provider.Get(Template, locale, format);

                resource.Content.ShouldNotBeNullOrWhiteSpace();
                resource.FileName.ShouldBe(EmailTemplateResource.FileNameOf(Template, locale, format));
            }
        }
    }
}
