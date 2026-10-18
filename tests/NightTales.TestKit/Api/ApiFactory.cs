using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NightTales.Api.Http;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Storage;
using NightTales.Domain.Common;
using NightTales.Infrastructure.OpenAI;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using NightTales.TestKit.Moderation;
using NightTales.TestKit.Research;
using NightTales.TestKit.Security;
using NightTales.TestKit.Time;

namespace NightTales.TestKit.Api;

/// <summary>
/// Runs the real Api in-process for integration tests (09 README D13): the host of <c>Program</c>
/// with the class database's connection string, the <see cref="FakeClock"/> in place of the system
/// clock, the configuration a deployment always provides (an ephemeral ES256 key, the public origin and
/// the run's throwaway <c>DP_KEK</c> — see <see cref="ApplyRequiredStartupSettings"/>), and the
/// <see cref="TestAuthHandler"/> scheme (<c>Test</c>) and a deterministic content moderator. Explicit service overrides
/// run after these defaults; <see cref="ProductionHost"/> keeps production authentication but isolates moderation.
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
    private readonly List<Action<IServiceCollection>> _serviceOverrides = [];
    private readonly List<KeyValuePair<string, string?>> _settings = [];
    private readonly List<ILoggerProvider> _logProviders = [];
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private string? _environment;

    /// <summary>The default draft moderator; tests may script its verdicts before the host starts.</summary>
    public FakeContentModerator Moderator { get; } = new();

    /// <summary>
    /// The default fact extractor (02.5.3-T2); tests may script its outcomes before the host starts. A fact-sheet run
    /// inside a test host must never reach a provider, so this replaces the real adapter exactly as
    /// <see cref="Moderator"/> replaces the moderator.
    /// </summary>
    public FakeFactExtractor Extractor { get; } = new();

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
    /// Adds one configuration value on top of the host's own <c>appsettings.json</c> (the same
    /// <see cref="IWebHostBuilder.UseSetting(string?, string?)"/> the required start-up settings use, so
    /// <c>Program</c> sees it while it is still in its top-level statements). 09.3.5-T1 uses it to drive
    /// the rate-limit limits, the forwarded-header trust and the ticket keys.
    /// </summary>
    /// <param name="key">The configuration key, for example <c>RateLimiting:Auth:PermitLimit</c>.</param>
    /// <param name="value">The value; null removes the key from the host's configuration.</param>
    public ApiFactory WithSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _settings.Add(new KeyValuePair<string, string?>(key, value));

        return this;
    }

    /// <summary>
    /// Pins the host's environment (default: whatever the test process resolves). A test whose options
    /// validator carries an environment-dependent rule — an <c>https://</c> endpoint unless Development,
    /// say — states the environment it exercises instead of inheriting one (06.1.2-T1's
    /// <c>YouTubeOptionsValidator</c> accepts a loopback address in Development).
    /// </summary>
    /// <param name="environment">The environment name, for example <c>Development</c>.</param>
    public ApiFactory WithEnvironment(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        _environment = environment;

        return this;
    }

    /// <summary>
    /// Adds one logger provider to the host, so a test can scan every line the host rendered (the
    /// redaction checks of 09.3.1-T1 and 09.4.2-T1 reach the same seam through
    /// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>).
    /// </summary>
    /// <param name="provider">The provider the host logs through.</param>
    public ApiFactory WithLoggerProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _logProviders.Add(provider);

        return this;
    }

    /// <summary>
    /// A client whose requests present <paramref name="peerIp"/> as the connection's remote address.
    /// </summary>
    /// <remarks>
    /// The in-process <c>TestServer</c> has no connection feature, so a test that is about the peer's
    /// address — the forwarded-header trust and the IP-keyed rate-limit partitions (09.3.5-T1) — pins one
    /// through <c>TestServer.CreateHandler</c>. Only such a test uses this; every other client of the
    /// factory sends the address-less requests the harness's own host produces.
    /// </remarks>
    /// <param name="peerIp">The address the request appears to come from.</param>
    /// <param name="userId">The test user the request authenticates as, or null for an anonymous one.</param>
    /// <param name="role">The role of <paramref name="userId"/>.</param>
    public HttpClient PeerClient(string peerIp, Guid? userId = null, PlatformRole role = PlatformRole.User)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerIp);

        var address = IPAddress.Parse(peerIp);
        var client = new HttpClient(Server.CreateHandler(context => context.Connection.RemoteIpAddress = address))
        {
            BaseAddress = Server.BaseAddress,
        };

        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, id.ToString());
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, TestAuthHandler.RoleClaimValue(role));
        }

        return client;
    }

    /// <summary>The internal port the fetcher's lease API listens on in a test host (02.5.2-T2, D10).</summary>
    public const int FetcherInternalPort = 8081;

    /// <summary>The public port of a test host: the internal routes must not exist there.</summary>
    public const int PublicPort = 8080;

    /// <summary>A throwaway <c>FETCHER_API_TOKEN</c> for a test host (32+ characters, not a secret).</summary>
    public const string FetcherToken = "test-fetcher-api-token-0123456789abcdef";

    /// <summary>
    /// Enables the fetcher's internal lease API in this host (02.5.2-T2): <c>Fetcher:Internal:Enabled</c> and the
    /// token a deployment's secret store would provide. The in-process <c>TestServer</c> has no listener, so the tests
    /// pick the port a request "arrived on" with <see cref="InternalClient"/> / <see cref="PublicPortClient"/>.
    /// </summary>
    /// <param name="maxRequestsPerMinutePerWorker">The per-worker request budget; high by default so a loop of calls is not limited.</param>
    public ApiFactory WithFetcherInternalApi(int maxRequestsPerMinutePerWorker = 10_000)
    {
        WithSetting("Fetcher:Internal:Enabled", "true");
        WithSetting("FETCHER_API_TOKEN", FetcherToken);
        WithSetting("Fetcher:Internal:MaxRequestsPerMinutePerWorker", maxRequestsPerMinutePerWorker.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return this;
    }

    /// <summary>
    /// A client whose requests appear to arrive on the internal listener (port 8081), presenting
    /// <paramref name="token"/> as the bearer token; <see langword="null"/> sends no <c>Authorization</c> header.
    /// </summary>
    /// <param name="token">The bearer token.</param>
    public HttpClient InternalClient(string? token = FetcherToken)
    {
        var client = ClientOnPort(FetcherInternalPort);
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>A client whose requests appear to arrive on the public listener (port 8080).</summary>
    public HttpClient PublicPortClient() => ClientOnPort(PublicPort);

    private HttpClient ClientOnPort(int localPort) =>
        new(Server.CreateHandler(context => context.Connection.LocalPort = localPort))
        {
            BaseAddress = Server.BaseAddress,
        };

    /// Replaces or adds services in the host's collection — the sibling of <see cref="MapEndpoints"/>
    /// for tests that must swap a port (a recording blob store, a fixed audit writer, an aging health
    /// cache) instead of adding a route.
    /// </summary>
    /// <remarks>
    /// The actions run inside the host's own <c>ConfigureServices</c>, after the test scheme and the
    /// fake clock are in place, so a replacement wins over the production registration.
    /// </remarks>
    /// <param name="configure">The service registrations the test needs.</param>
    /// <returns>This factory, so a call site reads <c>new ApiFactory(...).ConfigureServices(...)</c>.</returns>
    public ApiFactory ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigurations.Add(configure);

        return this;
    }

    /// <summary>
    /// An authenticated client: every request carries <c>X-Test-User</c> and <c>X-Test-Role</c>, and
    /// the <see cref="TestAuthHandler"/> turns them into an authenticated principal.
    /// </summary>
    /// <param name="userId">The <c>sub</c> claim.</param>
    /// <param name="role">The <c>role</c> claim; <see cref="PlatformRole.User"/> unless a test says otherwise.</param>
    /// <param name="scope">
    /// The token's <c>scope</c> claim: null for a full access token, or
    /// <see cref="TestAuthHandler.EnrolmentScope"/> for BD4's enrolment-only token.
    /// </param>
    /// <param name="sessionId">
    /// The <c>sid</c> claim (09 README BD3): the refresh family the token belongs to. Null means the
    /// token has no family, exactly like the enrolment-only token.
    /// </param>
    public HttpClient ClientFor(
        Guid userId,
        PlatformRole role = PlatformRole.User,
        string? scope = null,
        Guid? sessionId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, TestAuthHandler.RoleClaimValue(role));

        if (scope is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, scope);
        }

        if (sessionId is { } familyId)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.SessionHeader, familyId.ToString());
        }

        return client;
    }

    /// <summary>
    /// Replaces or adds services on top of the production composition root (09 README D13: hand-written
    /// fakes, no mocking library), exactly like <c>WorkerHost.ConfigureServices</c> does for the Worker.
    /// </summary>
    /// <remarks>
    /// The action runs after the host's own registrations, so the documented pattern is
    /// <c>services.RemoveAll&lt;T&gt;(); services.AddScoped&lt;T, Fake&gt;();</c> — the same one
    /// <c>EmailHarness</c> uses for <c>ISmtpEmailSender</c>. A test that needs a port to answer
    /// deterministically (a TOTP verifier, a security alerter) adds it here before the host starts.
    /// </remarks>
    /// <param name="configure">The service-collection action to apply.</param>
    public ApiFactory WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceOverrides.Add(configure);

        return this;
    }

    /// <summary>A client without the test headers: the host sees it as anonymous.</summary>
    public HttpClient AnonymousClient() => CreateClient();

    /// <summary>
    /// An anonymous <em>browser</em> client: it does not follow a redirect, so the <c>302</c> a route answers
    /// and everything it carries (the <c>Location</c>, the cache and referrer headers, a cookie it clears)
    /// stay observable. The default client follows one, which would replace the redirect with the answer to
    /// its target.
    /// </summary>
    public HttpClient AnonymousBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// The host of <c>Program</c> with the settings a real deployment always has — the connection
    /// string, required start-up configuration and the run's Data Protection KEK — and no
    /// <see cref="TestAuthHandler"/> scheme or <see cref="FakeClock"/>. Moderation and the fact
    /// extraction alone are deterministic so authentication and start-up probes cannot contact a provider.
    /// <paramref name="configure"/> adds whatever the test itself needs on top, and can override any of
    /// the settings above. <paramref name="withKeyRing"/> false leaves
    /// <c>DataProtection:KekPfxPath</c> unset, which is how 09.9.4-T1's start-up validation is tested.
    /// </summary>
    /// <remarks>
    /// This helper exists because both hosts of the solution declare a top-level <c>Program</c>, so a
    /// test assembly that references TestKit cannot name <c>Program</c> unambiguously (CS0433); the
    /// entry point type stays inside TestKit.
    /// </remarks>
    public static WebApplicationFactory<Program> ProductionHost(
        string connectionString,
        Action<IWebHostBuilder>? configure = null,
        bool withKeyRing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            ApplyRequiredStartupSettings(builder, withKeyRing);
            builder.ConfigureServices(RateLimiterDisposal.Register);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IContentModerator>();
                services.AddSingleton<IContentModerator>(new FakeContentModerator());

                // 02.5.3-T2: the same replacement, so no path of this host can reach a provider for facts either.
                services.RemoveAll<IFactExtractor>();
                services.AddSingleton<IFactExtractor>(new FakeFactExtractor());
            });

            builder.UseSetting(
                $"ConnectionStrings:{PersistenceOptions.ConnectionStringName}",
                connectionString);

            configure?.Invoke(builder);
        });
    }

    /// <summary>
    /// The configuration every deployment provides and a host validates on start-up: an ES256 key
    /// (ephemeral here, generated per run — never a committed secret), the SPA's origin, and the
    /// <c>DP_KEK</c> PFX the Data Protection ring is encrypted with (09.3.2-T1, 09.9.4-T1). This is
    /// configuration, not a test double, which is why even the production-composition-root host gets it.
    /// </summary>
    private static void ApplyRequiredStartupSettings(IWebHostBuilder builder, bool withKeyRing = true)
    {
        builder.UseSetting(TestSigningKey.ConfigurationKey, TestSigningKey.Pem);
        builder.UseSetting($"{AppOptions.SectionName}:{nameof(AppOptions.PublicOrigin)}", PublicOrigin);

        // 09.7.2-T1 (09 README BD10/BD12): the blob store's endpoint. The checked-in `appsettings.json`
        // names the dev SeaweedFS (`http://127.0.0.1:8333`), which a test host must not try to reach: a
        // closed loopback port fails in microseconds and keeps the prober's log quiet, while the buckets,
        // TTLs and marker key still come from the checked-in section (so a test can assert them).
        builder.UseSetting($"{StorageSettings.SectionName}:{nameof(StorageSettings.ServiceUrl)}", UnreachableStorageUrl);

        // 09.2.6-T3: the pepper the address hash of `email_messages` is salted with. The committed value is
        // the development placeholder, which the e-mail validator refuses outside Development, so the test
        // host supplies the run's own — the same shape a deployment's secret store provides (09 README
        // BD16), which is why even the production-composition-root host gets it.
        builder.UseSetting(EmailTestSettings.AddressHashPepperKey, EmailTestSettings.AddressHashPepper);
        builder.UseSetting("Logging:UserHashSalt", "harness-user-hash-salt");

        // The Api shares the Worker's existing provider configuration. Tests validate that contract
        // with a placeholder credential, while ConfigureTestServices replaces only the moderator.
        builder.UseSetting($"{OpenAiOptions.SectionName}:{nameof(OpenAiOptions.ApiKey)}", "harness-openai-key-placeholder");

        if (!withKeyRing)
        {
            return;
        }

        // A throwaway self-signed RSA PFX for this run (TestKek), the same shape as the secret store's
        // `nts/<env>/shared/DP_KEK`; the Api refuses to start without one (09.9.4-T1).
        builder.UseSetting(TestKek.PathConfigurationKey, TestKek.SharedPath);
        builder.UseSetting(TestKek.PasswordConfigurationKey, TestKek.SharedPassword);
    }

    /// <summary>The storage endpoint a test host is pointed at: a closed loopback port.</summary>
    public const string UnreachableStorageUrl = "http://127.0.0.1:9";

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

        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        ApplyRequiredStartupSettings(builder);

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            RateLimiterDisposal.Register(services);

            if (_logProviders.Count > 0)
            {
                services.AddLogging(logging =>
                {
                    foreach (var provider in _logProviders)
                    {
                        logging.AddProvider(provider);
                    }
                });
            }

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);

            // Edits and approval checks must never reach a provider from an ordinary integration-test host.
            services.RemoveAll<IContentModerator>();
            services.AddSingleton<IContentModerator>(Moderator);

            // 02.5.3-T2: the same rule for the fact extraction. The Api's production graph has no extractor (it never
            // runs a generation job), so this is a test-only port a fact-sheet test may drive in this host.
            services.RemoveAll<IFactExtractor>();
            services.AddSingleton<IFactExtractor>(Extractor);

            foreach (var override1 in _serviceOverrides)
            {
                override1(services);
            }

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

            foreach (var configure in _serviceConfigurations)
            {
                configure(services);
            }
        });
    }
}
