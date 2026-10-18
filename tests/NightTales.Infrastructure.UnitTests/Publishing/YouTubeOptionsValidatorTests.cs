using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Publishing;

/// <summary>
/// The <c>YouTube</c> section of 06.1.2-T1 (06 README D11): a complete section binds, every invalid combination
/// fails the start-up with a message that names the key and carries no secret value, and a host that already bound
/// the section through 06.3.1-T1's registration keeps one binding and one start-up validation.
/// </summary>
/// <remarks>
/// The scheme rule is the Infrastructure adapter's (<c>YouTubeOptionsValidation</c>): outside Development each
/// endpoint must be <c>https</c>, while Development may point them at a loopback fake. The value rules belong to
/// <c>NightTales.Application.Publishing.YouTubeOptionsValidator</c>.
/// </remarks>
public sealed class YouTubeOptionsValidatorTests
{
    private const string Section = YouTubeOptions.SectionName;
    private const string ClientId = "nts-youtube-dev";
    private const string SecretRef = "nts/dev/api/YOUTUBE_CLIENT_SECRET";
    private const string SecretKeyName = "YOUTUBE_CLIENT_SECRET";
    private const string SecretCanary = "canary-secret-aaaa";
    private const string RedirectUri = "http://127.0.0.1:5000/api/v1/channels/youtube/callback";
    private const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    private const string ReadScope = "https://www.googleapis.com/auth/youtube.readonly";
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string ApiBaseUrl = "https://www.googleapis.com";

    [Fact]
    public void A_complete_section_binds()
    {
        var options = Resolve(Configuration());

        options.ActiveClientId.ShouldBe(ClientId);
        options.Clients[ClientId].SecretRef.ShouldBe(SecretRef);
        options.RedirectUri.ShouldBe(RedirectUri);
        options.Scopes.ShouldBe(new[] { UploadScope, ReadScope });
        options.AuthorizationEndpoint.ShouldBe(AuthorizationEndpoint);
        options.TokenEndpoint.ShouldBe(TokenEndpoint);
        options.RevokeEndpoint.ShouldBe(RevokeEndpoint);
        options.ApiBaseUrl.ShouldBe(ApiBaseUrl);
        options.Pkce.ShouldBeTrue();
        options.OAuthStateMinutes.ShouldBe(10);
        options.Http.TimeoutSeconds.ShouldBe(15);
    }

    [Theory]
    [InlineData("ActiveClientId", "", $"{Section}:ActiveClientId")]
    [InlineData("ActiveClientId", "nts-some-other-client", $"{Section}:Clients")]
    [InlineData($"Clients:{ClientId}:SecretRef", "", "SecretRef")]
    [InlineData($"Clients:{ClientId}:SecretRef", "ConnectionStrings:Default", "SecretRef")]
    [InlineData("Clients:nts-youtube-next:SecretRef", "", "SecretRef")]
    [InlineData("Clients:nts-youtube-next:SecretRef", "nts/dev/api/YOUTUBE SECRET", "SecretRef")]
    [InlineData("RedirectUri", "", $"{Section}:RedirectUri")]
    [InlineData("RedirectUri", "api/v1/channels/youtube/callback", $"{Section}:RedirectUri")]
    [InlineData("RedirectUri", "/api/v1/channels/youtube/callback", $"{Section}:RedirectUri")]
    [InlineData("RedirectUri", "file:///tmp/channels/youtube/callback", $"{Section}:RedirectUri")]
    [InlineData("RedirectUri", "ftp://example.test/channels/youtube/callback", $"{Section}:RedirectUri")]
    [InlineData("Scopes:1", UploadScope, "Scopes")]
    [InlineData("OAuthStateMinutes", "4", $"{Section}:OAuthStateMinutes")]
    [InlineData("OAuthStateMinutes", "31", $"{Section}:OAuthStateMinutes")]
    [InlineData("Http:TimeoutSeconds", "0", $"{Section}:Http:TimeoutSeconds")]
    [InlineData("Http:TimeoutSeconds", "61", $"{Section}:Http:TimeoutSeconds")]
    [InlineData("AuthorizationEndpoint", "http://accounts.google.com/o/oauth2/v2/auth", $"{Section}:AuthorizationEndpoint")]
    [InlineData("TokenEndpoint", "http://oauth2.googleapis.com/token", $"{Section}:TokenEndpoint")]
    [InlineData("RevokeEndpoint", "http://oauth2.googleapis.com/revoke", $"{Section}:RevokeEndpoint")]
    [InlineData("ApiBaseUrl", "http://127.0.0.1:5000", $"{Section}:ApiBaseUrl")]
    public void An_invalid_section_fails_the_start_without_a_secret(string key, string value, string namedKey)
    {
        var configuration = Configuration((key, value));

        var exception = Should.Throw<OptionsValidationException>(() => StartUp(configuration));

        exception.Message.ShouldContain(namedKey);
        exception.Message.ShouldNotContain(SecretCanary);
        exception.Message.ShouldNotContain(SecretRef);
        exception.Message.ShouldNotContain("ConnectionStrings:Default");
        exception.Message.ShouldNotContain("YOUTUBE SECRET");
    }

    [Fact]
    public void A_second_client_with_a_well_formed_reference_passes()
    {
        // The map exists for a rotation: a reference with '/', '_' and '-' is a secret-manager path or an environment
        // variable name, and the validation of the entry must not demand the active client's shape only.
        StartUp(Configuration(("Clients:nts-youtube-next:SecretRef", "nts/dev/api/YOUTUBE_CLIENT_SECRET-NEXT")));
    }

    [Fact]
    public void The_default_ApiBaseUrl_is_the_host_root_the_committed_section_carries()
    {
        // `ApiBaseUrl` is the Google SDK's `BaseUri`, and the SDK appends its own `youtube/v3/` path to it: a default
        // that already ended in `/youtube/v3` would build `.../youtube/v3/youtube/v3/channels`, and a section that
        // disagreed with the class default would depend on which one the host bound. Both must be the host root, and
        // the composed path must be the one the channel endpoint (and FakeGoogleServer) serves.
        new YouTubeOptions().ApiBaseUrl.ShouldBe("https://www.googleapis.com");
        new YouTubeOptions().ApiBaseUrl.ShouldBe(CommittedApiBaseUrl());
        ApiBaseUrl.ShouldBe(CommittedApiBaseUrl());

        new Uri(new Uri(new YouTubeOptions().ApiBaseUrl), "youtube/v3/channels").AbsoluteUri
            .ShouldBe("https://www.googleapis.com/youtube/v3/channels");
    }

    [Fact]
    public void Development_may_point_every_endpoint_at_a_loopback_fake()
    {
        var configuration = Configuration(
            ("AuthorizationEndpoint", "http://127.0.0.1:5100/o/oauth2/v2/auth"),
            ("TokenEndpoint", "http://127.0.0.1:5100/token"),
            ("RevokeEndpoint", "http://127.0.0.1:5100/revoke"),
            ("ApiBaseUrl", "http://127.0.0.1:5100"));

        StartUp(configuration, "Development");
    }

    [Fact]
    public void A_host_that_already_bound_the_section_binds_once_and_still_validates_it()
    {
        var services = Services(Configuration(("ActiveClientId", string.Empty)), bindFirst: true);

        // 06.3.1-T1's registration (through AddPersistence) already bound the section, so this task must not add a
        // second binding — and the section must still fail the start, because start-up validation is keyed per
        // options type and must never be skipped just because another options type of the host registered one.
        services.Count(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<YouTubeOptions>)).ShouldBe(1);

        Should.Throw<OptionsValidationException>(() => StartUp(services))
            .Message.ShouldContain($"{Section}:ActiveClientId");
    }

    [Fact]
    public void A_valid_section_survives_the_registered_startup_validation()
    {
        StartUp(Configuration());
    }

    private static YouTubeOptions Resolve(IConfiguration configuration, string environmentName = "Production")
    {
        using var provider = Services(configuration, environmentName).BuildServiceProvider();

        return provider.GetRequiredService<IOptions<YouTubeOptions>>().Value;
    }

    /// <summary>Runs what a host runs at start: every registered <see cref="IStartupValidator"/>.</summary>
    private static void StartUp(IConfiguration configuration, string environmentName = "Production") =>
        StartUp(Services(configuration, environmentName));

    private static void StartUp(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();

        foreach (var validator in provider.GetRequiredService<IEnumerable<IStartupValidator>>())
        {
            validator.Validate();
        }
    }

    /// <summary>
    /// The composition of these tests: the host environment the adapter reads, this task's registration, and —
    /// when asked — 06.3.1-T1's registration first, as <c>AddPersistence</c> runs it in the Api.
    /// </summary>
    private static ServiceCollection Services(
        IConfiguration configuration,
        string environmentName = "Production",
        bool bindFirst = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

        // A real host puts its IConfiguration in the container, and the named clients this task registers
        // come from AddHttpClient, whose factory resolves it — without it the composition is not a host's.
        services.AddSingleton(configuration);

        if (bindFirst)
        {
            services.AddYouTubePublishing();
        }

        services.AddYouTubePublishing(configuration);

        return services;
    }

    /// <summary>The committed shape of the section, plus the secret itself so a leak in a message would be seen.</summary>
    private static Dictionary<string, string?> Baseline() => new(StringComparer.Ordinal)
    {
        [$"{Section}:ActiveClientId"] = ClientId,
        [$"{Section}:Clients:{ClientId}:SecretRef"] = SecretRef,
        [$"{Section}:RedirectUri"] = RedirectUri,
        [$"{Section}:Scopes:0"] = UploadScope,
        [$"{Section}:Scopes:1"] = ReadScope,
        [$"{Section}:AuthorizationEndpoint"] = AuthorizationEndpoint,
        [$"{Section}:TokenEndpoint"] = TokenEndpoint,
        [$"{Section}:RevokeEndpoint"] = RevokeEndpoint,
        [$"{Section}:ApiBaseUrl"] = ApiBaseUrl,
        [$"{Section}:Pkce"] = "true",
        [$"{Section}:OAuthStateMinutes"] = "10",
        [$"{Section}:Http:TimeoutSeconds"] = "15",
        [SecretKeyName] = SecretCanary,
    };

    private static IConfiguration Configuration(params (string Key, string? Value)[] overrides)
    {
        var values = Baseline();
        foreach (var (key, value) in overrides)
        {
            values[$"{Section}:{key}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>
    /// The <c>YouTube:ApiBaseUrl</c> the committed Api <c>appsettings.json</c> carries, read from the file so a class
    /// default that drifted from the shipped section (or a section that changed shape) is a failing test.
    /// </summary>
    private static string CommittedApiBaseUrl()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("NightTales.sln was not found above the test output directory.");
        }

        var path = Path.Combine(directory.FullName, "src", "NightTales.Api", "appsettings.json");
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return document.RootElement.GetProperty(Section).GetProperty(nameof(YouTubeOptions.ApiBaseUrl)).GetString()!;
    }

    /// <summary>A hand-written host environment: the validator adapter reads Development only.</summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "NightTales.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
