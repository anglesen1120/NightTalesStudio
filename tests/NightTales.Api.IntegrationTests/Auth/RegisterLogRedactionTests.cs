using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// 09.3.1-T1's log acceptance check: the register path may log ids and outcome codes, never the
/// password and never the e-mail address (09 README D17). The sink is a hand-written
/// <see cref="ILoggerProvider"/> — no mocking library (09 README D13) — attached to the real host.
/// </summary>
public sealed class RegisterLogRedactionTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task Register_LogsContainNoPasswordAndNoEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"redaction-{Guid.CreateVersion7():N}@nighttales.test";
        var password = "Sup3r-Secret-Passw0rd!";
        var sink = new CapturingLoggerProvider();

        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink))));

        var response = await host.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/register",
            new
            {
                email,
                password,
                consents = new[]
                {
                    new { document = "tos", version = "1.0.0" },
                    new { document = "privacy", version = "1.0.0" },
                },
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var logs = sink.Text;

        // Non-vacuous: the request really was logged through this sink.
        logs.ShouldContain(
            "RegisterCommand",
            Case.Sensitive,
            "the mediator log line of the register request is what this scan reads");
        logs.ShouldNotContain(password, Case.Sensitive, "a password must never be logged (09 README D17)");
        logs.ShouldNotContain(email, Case.Sensitive, "an e-mail address must never be logged (09 README D17)");
    }

    /// <summary>Keeps every rendered line; asserts the redaction of the D17 rule.</summary>
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
