using NightTales.Application.Features.Email;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Email;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Email;

/// <summary>
/// The composition-time template validation of 09.2.6-T3 (09 README BD9): the catalog and the shipped
/// files must describe exactly the same templates, every placeholder must be a declared key, and every text
/// file must carry the subject line — otherwise the host refuses to start.
/// </summary>
/// <remarks>
/// The cases mutate the <em>shipped</em> set rather than a hand-written one, so the regression they guard is
/// the real one: the files a build actually copies beside its assembly.
/// </remarks>
public sealed class EmailTemplateProviderTests
{
    [Fact]
    public void Validate_UnknownPlaceholderOrMissingFile_FailsStartup()
    {
        var shipped = EmailTemplateProvider.ReadDeployedFiles();

        var withUnknownPlaceholder = shipped
            .Select(resource => resource.FileName == "password_reset.vi.txt"
                ? resource with { Content = resource.Content + "\n{{nope}}\n" }
                : resource)
            .ToList();

        var unknown = Should.Throw<EmailTemplateException>(
            () => EmailTemplateProvider.ValidateOrThrow(withUnknownPlaceholder));
        unknown.Message.ShouldContain("password_reset.vi.txt");
        unknown.Message.ShouldContain("{{nope}}");

        var withMissingFile = shipped
            .Where(resource => resource.FileName != "password_reset.en.html")
            .ToList();

        var missing = Should.Throw<EmailTemplateException>(
            () => EmailTemplateProvider.ValidateOrThrow(withMissingFile));
        missing.Message.ShouldContain("password_reset.en.html");

        var withOrphan = new List<EmailTemplateResource>(shipped)
        {
            new("ghost", Language.Vi, EmailTemplateFormat.Text, "Subject: ghost\n\nbody\n"),
        };

        var orphan = Should.Throw<EmailTemplateException>(
            () => EmailTemplateProvider.ValidateOrThrow(withOrphan));
        orphan.Message.ShouldContain("ghost.vi.txt");
    }

    [Fact]
    public void Validate_TextFileWithoutSubject_FailsStartup()
    {
        var withSubjectlessText = EmailTemplateProvider
            .ReadDeployedFiles()
            .Select(resource => resource.FileName == "password_changed.en.txt"
                ? resource with { Content = "Hello {{displayName}}\n" }
                : resource)
            .ToList();

        var exception = Should.Throw<EmailTemplateException>(
            () => EmailTemplateProvider.ValidateOrThrow(withSubjectlessText));

        exception.Message.ShouldContain("password_changed.en.txt");
        exception.Message.ShouldContain(EmailTemplateProvider.SubjectPrefix);
    }

    [Fact]
    public void ShippedTemplates_CoverEveryCatalogEntryInBothLocales()
    {
        var provider = EmailTemplateProvider.FromDeployedFiles();

        foreach (var (name, _) in EmailTemplateCatalog.All)
        {
            foreach (var locale in Enum.GetValues<Language>())
            {
                foreach (var format in Enum.GetValues<EmailTemplateFormat>())
                {
                    var resource = provider.Get(name, locale, format);

                    resource.Content.ShouldNotBeNullOrWhiteSpace();
                    resource.FileName.ShouldBe(EmailTemplateResource.FileNameOf(name, locale, format));
                }
            }
        }
    }
}
