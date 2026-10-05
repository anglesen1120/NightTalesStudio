using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Infrastructure.Identity;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The start-up validation of 09.3.1-T1's three options types (Design: every one of them is
/// <c>ValidateOnStart</c>): a host whose registration, breached-password or legal-version configuration
/// is unusable must refuse to start rather than run with a silently wrong policy.
/// </summary>
/// <remarks>
/// The same seam carries the list-mode fail-fast for a missing breached-password resource
/// (<c>Security:BreachedPasswords:Mode = list</c> without
/// <c>NightTales.Infrastructure.Identity.Resources.breached-passwords.txt</c>). That branch cannot be
/// reached from a test because the resource ships inside the NightTales.Infrastructure assembly, so
/// what is asserted here is the machinery it rides on.
/// </remarks>
public sealed class IdentityOptionsStartupTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public void BreachedPasswords_UndefinedModeValue_RefusesToStart()
    {
        // "7" binds to (BreachedPasswordMode)7, which Enum.IsDefined rejects: only range, list, off.
        ShouldRefuseToStart(($"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}", "7"));
    }

    [Fact]
    public void BreachedPasswords_RangeModeWithoutAbsoluteBaseUrl_RefusesToStart()
    {
        ShouldRefuseToStart(
            ($"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}", nameof(BreachedPasswordMode.Range)),
            ($"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.RangeBaseUrl)}", "not-an-absolute-url"));
    }

    [Fact]
    public void BreachedPasswords_NonPositiveRangeTimeout_RefusesToStart()
    {
        ShouldRefuseToStart(
            ($"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.RangeTimeoutSeconds)}", "0"));
    }

    [Fact]
    public void BreachedPasswords_ListMode_ResolvesTheBundledChecker()
    {
        // The positive leg of the mode switch: list must wire TopListPasswordChecker — not the no-op —
        // or a typo in CreateBreachedPasswordChecker would silently accept every breached password.
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting(
                $"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}",
                nameof(BreachedPasswordMode.List)));

        host.Services.GetRequiredService<IBreachedPasswordChecker>()
            .ShouldBeOfType<TopListPasswordChecker>();
    }

    [Fact]
    public void BreachedPasswords_RangeMode_ResolvesTheRangeChecker()
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting(
                $"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.Mode)}",
                nameof(BreachedPasswordMode.Range)));

        host.Services.GetRequiredService<IBreachedPasswordChecker>()
            .ShouldBeOfType<KAnonymityRangeChecker>();
    }

    [Fact]
    public void LegalDocuments_MissingPrivacyVersion_RefusesToStart()
    {
        ShouldRefuseToStart(
            ($"{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Privacy)}", string.Empty));
    }

    [Fact]
    public void Registration_UndefinedModeValue_RefusesToStart()
    {
        // "42" binds to (RegistrationMode)42, which Enum.IsDefined rejects: only closed/invite/open.
        ShouldRefuseToStart(($"{RegistrationOptions.SectionName}:{nameof(RegistrationOptions.Mode)}", "42"));
    }

    [Fact]
    public void BreachedPasswords_NonPositiveTopListSize_RefusesToStart()
    {
        ShouldRefuseToStart(($"{BreachedPasswordOptions.SectionName}:{nameof(BreachedPasswordOptions.TopListSize)}", "0"));
    }

    [Fact]
    public void LegalDocuments_MissingTosVersion_RefusesToStart()
    {
        ShouldRefuseToStart(($"{LegalDocumentVersions.SectionName}:{nameof(LegalDocumentVersions.Tos)}", string.Empty));
    }

    [Theory]
    [InlineData("Tos", "v1")]
    [InlineData("Privacy", "1.0")]
    [InlineData("Privacy", "1.0.0\n")]
    public void LegalDocuments_NonSemverVersion_RefusesToStart(string document, string version)
    {
        ShouldRefuseToStart(($"{LegalDocumentVersions.SectionName}:{document}", version));
    }

    [Theory]
    [InlineData("Tos")]
    [InlineData("Privacy")]
    public void LegalDocuments_VersionOver32Chars_RefusesToStart(string document)
    {
        ShouldRefuseToStart(($"{LegalDocumentVersions.SectionName}:{document}", new string('1', 29) + ".0.0"));
    }

    private void ShouldRefuseToStart(params (string Key, string Value)[] settings)
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder =>
            {
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }
            });

        Should.Throw<OptionsValidationException>(() => host.CreateClient());
    }
}
