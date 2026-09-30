using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.Http;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;

namespace NightTales.TestKit.Api;

/// <summary>
/// Runs the real Api in-process for integration tests (09 README D13): the host of <c>Program</c>
/// with the class database's connection string, the <see cref="FakeClock"/> in place of the system
/// clock, 09.3.2-T1's required auth configuration (an ephemeral ES256 key and the public origin — see
/// <see cref="ApplyRequiredAuthSettings"/>), and the <see cref="TestAuthHandler"/> scheme
/// (<c>Test</c>) as the default — nothing else differs from the production composition root.
/// </summary>
/// <param name="database">The database of the test class; its connection string is handed to the host.</param>
/// <param name="clock">The clock every component resolves instead of the system clock.</param>
public sealed class ApiFactory(TestDatabase database, FakeClock clock) : WebApplicationFactory<Program>
{
    /// <summary>
    /// The origin every test request must present as <c>Origin</c> (<c>App:PublicOrigin</c>): the value this
    /// factory configures the host with, so a test can assert both the accepted and the refused case.
    /// </summary>
    public const string PublicOrigin = "https://api.nighttales.test";

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
            ApplyRequiredAuthSettings(builder);

            builder.UseSetting(
                $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
                connectionString);

            configure?.Invoke(builder);
        });
    }

    /// <summary>
    /// The configuration 09.3.2-T1's start-up validation requires and a real deployment takes from its
    /// secret store and its environment: an ES256 key (ephemeral here, generated per run — never a committed
    /// secret) and the SPA's origin. This is configuration, not a test double, which is why even the
    /// production-composition-root host gets it.
    /// </summary>
    private static void ApplyRequiredAuthSettings(IWebHostBuilder builder)
    {
        builder.UseSetting(TestSigningKey.ConfigurationKey, TestSigningKey.Pem);
        builder.UseSetting($"{AppOptions.SectionName}:{nameof(AppOptions.PublicOrigin)}", PublicOrigin);
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

        ApplyRequiredAuthSettings(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    configureOptions: null);

            // The test scheme is the default, whatever scheme the host's own composition root registered
            // (09.3.2-T1 adds the real bearer scheme): a client with the test headers authenticates, one
            // without them is anonymous. Set explicitly rather than left to registration order.
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            if (_endpointMappings.Count > 0)
            {
                services.AddSingleton<IStartupFilter>(
                    new MapEndpointsStartupFilter([.. _endpointMappings]));
            }
        });
    }
}
