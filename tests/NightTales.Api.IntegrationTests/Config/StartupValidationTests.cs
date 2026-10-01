using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Api.Http;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Security;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Config;

/// <summary>
/// The start-up gate of 09.9.4-T1 (BD12/WF15): a host whose <c>App:PublicOrigin</c> is absent or
/// malformed, or whose <c>DP_KEK</c> is missing, refuses to start with one fixed message that names the
/// configuration key and never echoes the value.
/// </summary>
/// <remarks>
/// A blank <c>App:PublicOrigin</c> is the absent case: <c>AppOptions.TryNormalizeOrigin</c> treats
/// <c>null</c> and the empty string alike, and the host's own <c>appsettings.json</c> value has to be
/// overridden to make the key absent at all.
/// </remarks>
public sealed class StartupValidationTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public void Missing_public_origin_fails_startup()
    {
        var exception = Should.Throw<OptionsValidationException>(
            () => StartHost(("App:PublicOrigin", string.Empty)));

        exception.Message.ShouldBe(AppOptions.InvalidOriginFailure);
    }

    [Fact]
    public void Invalid_public_origin_fails_startup()
    {
        // BD12's explicit misconfiguration: the trailing slash never equals the origin a browser sends,
        // so the CORS allow-list would silently refuse every call.
        var exception = Should.Throw<OptionsValidationException>(
            () => StartHost(("App:PublicOrigin", "https://app.nighttales.app/")));

        exception.Message.ShouldBe(AppOptions.InvalidOriginFailure);
    }

    [Fact]
    public void Missing_kek_fails_startup()
    {
        // The path is blanked explicitly, not just left to `withKeyRing: false`: a developer shell that
        // exports `DataProtection__KekPfxPath` for the build would otherwise hand the host a real KEK
        // through the environment, and the environment is one of the host's configuration sources.
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.UseSetting(TestKek.PathConfigurationKey, string.Empty),
            withKeyRing: false);

        var exception = Should.Throw<OptionsValidationException>(() => host.CreateClient());

        exception.Message.ShouldBe(DataProtectionKek.MissingPathFailure);
        exception.Message.ShouldNotContain("DataProtection:KekPfxPassword", Case.Sensitive);
    }

    /// <summary>Builds the production-composition-root host with <paramref name="settings"/> on top and starts it.</summary>
    private void StartHost(params (string Key, string Value)[] settings)
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

        host.CreateClient();
    }
}
