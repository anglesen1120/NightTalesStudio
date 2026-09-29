using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Harness;

/// <summary>
/// The production host must never know the test scheme (09 README D13, plan §8): <c>TestAuthHandler</c>
/// lives in TestKit and is registered only by <see cref="ApiFactory"/>. This test builds the host from
/// <c>Program</c> without <see cref="ApiFactory"/> and proves both halves — the scheme is absent and a
/// request carrying the test header stays anonymous.
/// </summary>
public sealed class ProductionHostTests : IntegrationTest
{
    public ProductionHostTests(TestDatabase database)
        : base(database)
    {
    }

    [Fact]
    public async Task ProductionHost_DoesNotRegisterTestScheme()
    {
        // The probe route is mapped by the test, not by Program: the production host gains no test
        // endpoint of its own.
        await using var factory = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
                new MapEndpointsStartupFilter(ProbeEndpoints.Map))));

        // The production host registers no authentication of its own yet, so there is either no
        // scheme provider at all or one without the Test scheme: both mean nothing can authenticate
        // through the test headers.
        AuthenticationScheme? testScheme = null;
        var schemes = factory.Services.GetService<IAuthenticationSchemeProvider>();
        if (schemes is not null)
        {
            testScheme = await schemes.GetSchemeAsync(TestAuthHandler.SchemeName);
        }

        testScheme.ShouldBeNull("the host built from Program never registers the Test scheme (README D13)");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        var who = await client.GetFromJsonAsync<ProbeEndpoints.WhoAmI>(
            ProbeEndpoints.WhoAmIPath,
            TestContextToken.Value);

        who.ShouldNotBeNull();
        who.IsAuthenticated.ShouldBeFalse("Program alone never turns X-Test-User into an authenticated user");
        who.Sub.ShouldBeNull();
    }
}
