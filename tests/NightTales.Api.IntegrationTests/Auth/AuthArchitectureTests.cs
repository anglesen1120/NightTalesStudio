using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The architecture rule behind the auth routes (09.3.2-T1 Tests table, 09 README BD16/D17): a token never
/// reaches a log, and only the access token ever reaches a response body — the refresh token travels in its
/// cookie alone.
/// </summary>
/// <remarks>
/// The sink is a hand-written <see cref="ILoggerProvider"/> attached to the real host (no mocking library,
/// 09 README D13), exactly like 09.3.1-T1's register redaction test. The host is the production composition
/// root, so the log lines the pipeline actually emits for a login and a refresh are what the scan reads.
/// </remarks>
public sealed class AuthArchitectureTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Tokens_AreNeverLogged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new CapturingLoggerProvider();

        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink))));

        var client = host.CreateClient();
        var user = await AuthTestFlow.CreateUserAsync(host.Services);

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var accessToken = await AuthTestFlow.AccessTokenAsync(login, ct);
        var refreshToken = AuthTestFlow.CookieValue(cookie!);

        var (refresh, rotatedCookie) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        refresh.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var rotatedToken = AuthTestFlow.CookieValue(rotatedCookie!);
        var rotatedAccessToken = await AuthTestFlow.AccessTokenAsync(refresh, ct);

        var logs = sink.Text;

        // Non-vacuous: the request really was logged through this sink.
        logs.ShouldContain("LoginCommand", Case.Sensitive, "the mediator log line is what this scan reads");
        logs.ShouldContain("RefreshCommand", Case.Sensitive);

        foreach (var token in new[] { accessToken, rotatedAccessToken, refreshToken, rotatedToken })
        {
            logs.ShouldNotContain(token, Case.Sensitive, "a token must never be logged (BD16)");
        }

        logs.ShouldNotContain(user.Email!, Case.Sensitive, "an e-mail address must never be logged (D17)");

        // The response headers carry no token either: the access token is body-only, and the refresh token
        // appears nowhere but its own cookie.
        foreach (var response in new[] { login, refresh })
        {
            var headers = string.Join('\n', response.Headers.SelectMany(header => header.Value));

            headers.ShouldNotContain(accessToken, Case.Sensitive, "the access token travels in the body, never in a header");
            headers.ShouldNotContain(rotatedAccessToken, Case.Sensitive, "the access token travels in the body, never in a header");

            var withoutCookie = string.Join(
                '\n',
                response.Headers
                    .Where(header => !header.Key.StartsWith("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(header => header.Value));

            withoutCookie.ShouldNotContain(rotatedToken, Case.Sensitive, "only Set-Cookie may carry a refresh token");
        }
    }

    /// <summary>Keeps every rendered line; asserts the BD16/D17 redaction rule.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _messages);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _messages.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(formatter(state, exception));
            }
        }
    }
}
