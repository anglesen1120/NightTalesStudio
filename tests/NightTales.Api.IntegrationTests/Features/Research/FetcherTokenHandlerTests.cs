using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.Internal.Fetcher;
using NightTales.Api.Security;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Features.Research;

/// <summary>
/// <see cref="FetcherTokenAuthenticationHandler"/>, <see cref="FetcherTokenVerifier"/> and the start-up check of the
/// internal listener (02.5.2-T2, plan §5.2). The handler is driven directly over a <see cref="DefaultHttpContext"/>: no
/// host, no database.
/// </summary>
public sealed class FetcherTokenHandlerTests
{
    private const string Token = "unit-test-fetcher-token-0123456789abcdef";

    private static IConfiguration Config(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(token is null ? [] : [new(FetcherInternalOptions.TokenKey, token)])
            .Build();

    private static async Task<(AuthenticateResult Result, RecordingLoggerFactory Logs)> AuthenticateAsync(
        string? token,
        Action<HttpContext>? arrange)
    {
        var logs = new RecordingLoggerFactory();
        var handler = new FetcherTokenAuthenticationHandler(
            new StaticOptionsMonitor(),
            logs,
            System.Text.Encodings.Web.UrlEncoder.Default,
            new FetcherTokenVerifier(Config(token)));
        var context = new DefaultHttpContext();
        arrange?.Invoke(context);
        await handler.InitializeAsync(
            new AuthenticationScheme(
                FetcherTokenAuthenticationHandler.SchemeName,
                null,
                typeof(FetcherTokenAuthenticationHandler)),
            context);

        return (await handler.AuthenticateAsync(), logs);
    }

    private static Action<HttpContext> Bearer(string value) =>
        context => context.Request.Headers.Authorization = "Bearer " + value;

    [Fact]
    public async Task NoHeader_Unauthorized()
    {
        var (result, _) = await AuthenticateAsync(Token, arrange: null);

        result.Succeeded.ShouldBeFalse();
        result.Principal.ShouldBeNull();
    }

    [Fact]
    public async Task NoHeader_ChallengeIs401WithEmptyBody()
    {
        var provider = BuildChallengeContext(out var body);

        await provider.ChallengeAsync(FetcherTokenAuthenticationHandler.SchemeName);

        provider.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        body.Length.ShouldBe(0);
    }

    [Theory]
    [InlineData("wrong-token-wrong-token-wrong-token-00")]
    [InlineData("x")]
    [InlineData("unit-test-fetcher-token-0123456789abcdeg")]
    public async Task WrongToken_Unauthorized(string presented)
    {
        var (result, _) = await AuthenticateAsync(Token, Bearer(presented));

        result.Succeeded.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("bearer")]
    [InlineData("Token unit-test-fetcher-token-0123456789abcdef")]
    public async Task MalformedHeader_Unauthorized(string header)
    {
        var (result, _) = await AuthenticateAsync(Token, context => context.Request.Headers.Authorization = header);

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task OversizedToken_Unauthorized()
    {
        var (result, _) = await AuthenticateAsync(Token, Bearer(new string('a', 100_000)));

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task TwoAuthorizationHeaders_Unauthorized()
    {
        var (result, _) = await AuthenticateAsync(
            Token,
            context => context.Request.Headers.Authorization = new Microsoft.Extensions.Primitives.StringValues(
                ["Bearer " + Token, "Bearer " + Token]));

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task UserJwt_Unauthorized()
    {
        const string jwt = "eyJhbGciOiJFUzI1NiIsImtpZCI6ImFiYyJ9.eyJzdWIiOiIxMjMiLCJyb2xlIjoiYWRtaW4ifQ.c2ln";

        var (result, _) = await AuthenticateAsync(Token, Bearer(jwt));

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task CorrectToken_AuthenticatesAsFetcherInstance()
    {
        var (result, _) = await AuthenticateAsync(Token, Bearer(Token));

        result.Succeeded.ShouldBeTrue();
        var principal = result.Principal.ShouldNotBeNull();
        principal.Identity!.IsAuthenticated.ShouldBeTrue();
        principal.Identity.AuthenticationType.ShouldBe(FetcherTokenAuthenticationHandler.SchemeName);
        principal.Identity.Name.ShouldBe("fetcher");
        principal.IsInRole("fetcher").ShouldBeTrue();
        principal.FindFirst("sub").ShouldBeNull();
        principal.FindFirst(ClaimTypes.NameIdentifier).ShouldBeNull();
    }

    [Fact]
    public async Task CorrectToken_SchemeNameIsCaseInsensitive()
    {
        var (result, _) = await AuthenticateAsync(
            Token,
            context => context.Request.Headers.Authorization = new AuthenticationHeaderValue("bEaReR", Token).ToString());

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task TokenNotConfigured_EveryPresentedValueRejected()
    {
        var (result, _) = await AuthenticateAsync(token: null, Bearer(Token));

        result.Succeeded.ShouldBeFalse();
        new FetcherTokenVerifier(Config(null)).IsConfigured.ShouldBeFalse();
        new FetcherTokenVerifier(Config("   ")).IsConfigured.ShouldBeFalse();
        new FetcherTokenVerifier(Config("")).Verify("").ShouldBeFalse();
        new FetcherTokenVerifier(Config(Token)).IsConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task Handler_NeverLogsTheTokenOrHeader()
    {
        var (_, wrongLogs) = await AuthenticateAsync(Token, Bearer("secret-presented-value-00000000000000"));
        var (_, goodLogs) = await AuthenticateAsync(Token, Bearer(Token));

        foreach (var line in wrongLogs.Lines.Concat(goodLogs.Lines))
        {
            line.ShouldNotContain("secret-presented-value");
            line.ShouldNotContain(Token);
        }
    }

    [Fact]
    public void TokenNotConfigured_ListenerDisabled()
    {
        // Disabled: no token needed, the validator never complains (the listener and routes do not exist).
        new FetcherInternalOptionsValidator(Config(null))
            .Validate(null, new FetcherInternalOptions { Enabled = false }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    public void EnabledWithoutUsableToken_FailsStartUp(string? token)
    {
        var result = new FetcherInternalOptionsValidator(Config(token))
            .Validate(null, new FetcherInternalOptions { Enabled = true });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(FetcherInternalOptions.TokenKey);
        result.FailureMessage.ShouldNotContain("too-short");
    }

    [Fact]
    public void EnabledWithLongEnoughToken_Succeeds() =>
        new FetcherInternalOptionsValidator(Config(Token))
            .Validate(null, new FetcherInternalOptions { Enabled = true }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("not-an-ip", 8081, 60, 1_048_576)]
    [InlineData("127.0.0.1", 0, 60, 1_048_576)]
    [InlineData("127.0.0.1", 70_000, 60, 1_048_576)]
    [InlineData("127.0.0.1", 8081, 0, 1_048_576)]
    [InlineData("127.0.0.1", 8081, 60, 10)]
    [InlineData("127.0.0.1", 8081, 60, 17 * 1_024 * 1_024)]
    public void Enabled_BadListenerSettings_FailStartUp(string address, int port, int rate, int bodyBytes) =>
        new FetcherInternalOptionsValidator(Config(Token))
            .Validate(
                null,
                new FetcherInternalOptions
                {
                    Enabled = true,
                    ListenAddress = address,
                    ListenPort = port,
                    MaxRequestsPerMinutePerWorker = rate,
                    MaxBodyBytes = bodyBytes,
                })
            .Failed.ShouldBeTrue();

    [Fact]
    public void Defaults_AreLoopbackPort8081_OneMiBBody()
    {
        var defaults = new FetcherInternalOptions();

        defaults.Enabled.ShouldBeFalse();
        defaults.ListenAddress.ShouldBe("127.0.0.1");
        defaults.ListenPort.ShouldBe(8081);
        defaults.MaxBodyBytes.ShouldBe(1_048_576);
    }

    private static DefaultHttpContext BuildChallengeContext(out MemoryStream body)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new FetcherTokenVerifier(Config(Token)))
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, FetcherTokenAuthenticationHandler>(
                FetcherTokenAuthenticationHandler.SchemeName,
                configureOptions: null)
            .Services
            .BuildServiceProvider();
        body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;

        return context;
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Recorder(_lines);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
