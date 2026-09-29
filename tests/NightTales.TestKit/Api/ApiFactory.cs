using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;

namespace NightTales.TestKit.Api;

/// <summary>
/// Runs the real Api in-process for integration tests (09 README D13): the host of <c>Program</c>
/// with the class database's connection string, the <see cref="FakeClock"/> in place of the system
/// clock, and the <see cref="TestAuthHandler"/> scheme (<c>Test</c>) — nothing else differs from the
/// production composition root.
/// </summary>
/// <param name="database">The database of the test class; its connection string is handed to the host.</param>
/// <param name="clock">The clock every component resolves instead of the system clock.</param>
public sealed class ApiFactory(TestDatabase database, FakeClock clock) : WebApplicationFactory<Program>
{
    private readonly List<Action<IEndpointRouteBuilder>> _endpointMappings = [];

    /// <summary>
    /// Registers routes that only exist in the test host (the probe endpoints of
    /// <c>NightTales.Api.IntegrationTests.Harness</c>). 09.1.4-T1 reuses this hook.
    /// </summary>
    public ApiFactory MapEndpoints(Action<IEndpointRouteBuilder> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _endpointMappings.Add(map);

        return this;
    }

    /// <summary>
    /// An authenticated client: every request carries <c>X-Test-User</c> and <c>X-Test-Role</c>, and
    /// the <see cref="TestAuthHandler"/> turns them into an authenticated principal.
    /// </summary>
    public HttpClient ClientFor(Guid userId, PlatformRole role = PlatformRole.User)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, TestAuthHandler.RoleClaimValue(role));

        return client;
    }

    /// <summary>A client without the test headers: the host sees it as anonymous.</summary>
    public HttpClient AnonymousClient() => CreateClient();

    /// <summary>
    /// The host of <c>Program</c> with only the connection string set — no <see cref="TestAuthHandler"/>
    /// scheme, no <see cref="FakeClock"/> — for tests that prove the production composition root stays
    /// free of test doubles (09 README D13, plan §8). <paramref name="configure"/> adds whatever the
    /// test itself needs on top.
    /// </summary>
    /// <remarks>
    /// This helper exists because both hosts of the solution declare a top-level <c>Program</c>, so a
    /// test assembly that references TestKit cannot name <c>Program</c> unambiguously (CS0433); the
    /// entry point type stays inside TestKit.
    /// </remarks>
    public static WebApplicationFactory<Program> ProductionHost(
        string connectionString,
        Action<IWebHostBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
                connectionString);

            configure?.Invoke(builder);
        });
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Program reads `ConnectionStrings:Postgres` through AddPersistence while it is still in
        // top-level statements, so the setting must be visible before Build() — UseSetting writes
        // into the configuration of the builder Program already holds.
        builder.UseSetting(
            $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
            database.ConnectionString);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    configureOptions: null);

            if (_endpointMappings.Count > 0)
            {
                services.AddSingleton<IStartupFilter>(
                    new MapEndpointsStartupFilter([.. _endpointMappings]));
            }
        });
    }
}
