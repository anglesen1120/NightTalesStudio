using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Infrastructure.Identity;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>QA probes: the legal-version configuration fails fast (never a default) and accepts exactly the documented shape.</summary>
public sealed class LegalOptionsQaStartupTests(TestDatabase database) : IntegrationTest(database)
{
    [Theory]
    [InlineData("Tos", " 1.0.0")]
    [InlineData("Privacy", "1.0.0 ")]
    [InlineData("Privacy", "1.0.0\r\n")]
    [InlineData("Tos", "1.0.0-rc1")]
    [InlineData("Tos", "1.0.0.0")]
    [InlineData("Privacy", "   ")]
    [InlineData("Tos", "\u0661.\u0660.\u0660")] // Arabic-Indic digits are not a semver (ASCII digits only)
    [InlineData("Privacy", "\uff11.\uff10.\uff10")]
    public void LegalDocuments_HostileVersion_RefusesToStart(string document, string version)
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting($"{LegalDocumentVersions.SectionName}:{document}", version));
        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }

    [Theory]
    [InlineData("Tos")]
    [InlineData("Privacy")]
    public void LegalDocuments_KeyRemovedEntirely_RefusesToStart(string document)
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting($"{LegalDocumentVersions.SectionName}:{document}", null));
        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }

    [Fact]
    public void LegalDocuments_32CharVersion_StartsAndIsServedAsConfigured()
    {
        var boundary = new string('1', 28) + ".0.0";
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting($"{LegalDocumentVersions.SectionName}:Tos", boundary));
        host.CreateClient();
        host.Services.GetRequiredService<ICurrentLegalVersions>().GetCurrentVersion(LegalDocument.Tos).ShouldBe(boundary);
    }
}
