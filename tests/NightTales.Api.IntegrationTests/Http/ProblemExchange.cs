using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// Runs one <see cref="IResult"/> against a request context without starting a host, so the mapper,
/// the catalogue and the result helpers can be asserted at unit speed (09.1.4-T1 Tests table).
/// </summary>
/// <remarks>
/// The context carries a real service provider with logging, because
/// <c>ProblemDetailsWriter</c> resolves its logger from <c>HttpContext.RequestServices</c>; the body
/// lands in a <see cref="MemoryStream"/> the caller reads as text.
/// </remarks>
internal sealed class ProblemExchange : IDisposable
{
    private readonly MemoryStream _body = new();
    private readonly ServiceProvider _services;

    internal ProblemExchange()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(Logs);
        });

        _services = services.BuildServiceProvider();
        Context = new DefaultHttpContext { RequestServices = _services };
        Context.Request.Path = "/__unit/problem";
        Context.Response.Body = _body;
    }

    /// <summary>Every log line the code under test emitted.</summary>
    internal CapturingLogs Logs { get; } = new();

    /// <summary>The request context handed to the code under test.</summary>
    internal DefaultHttpContext Context { get; }

    /// <summary>The response body, as text.</summary>
    internal string Body => Encoding.UTF8.GetString(_body.ToArray());

    /// <summary>The response status, after the result ran.</summary>
    internal HttpStatusCode StatusCode => (HttpStatusCode)Context.Response.StatusCode;

    /// <summary>Executes the result and deserializes the body into <see cref="ProblemResponse"/>.</summary>
    /// <param name="result">The result an endpoint would return.</param>
    /// <returns>The problem body, or null when the body was empty.</returns>
    internal async Task<ProblemResponse?> ExecuteAsync(IResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        await result.ExecuteAsync(Context);

        return string.IsNullOrWhiteSpace(Body)
            ? null
            : JsonSerializer.Deserialize<ProblemResponse>(Body, ProblemResponse.Options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _body.Dispose();
        _services.Dispose();
        Logs.Dispose();
    }

    /// <summary>An <see cref="ILoggerProvider"/> that keeps the rendered messages for assertions.</summary>
    internal sealed class CapturingLogs : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        /// <summary>The captured lines, in order.</summary>
        internal IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        /// <summary>True when a line at <paramref name="level"/> contains every fragment.</summary>
        /// <param name="level">The level to look for.</param>
        /// <param name="fragments">Fragments that must all appear in the same line.</param>
        /// <returns>True when such a line exists.</returns>
        internal bool Contains(LogLevel level, params string[] fragments) =>
            _entries.Exists(entry =>
                entry.Level == level
                && fragments.All(fragment => entry.Message.Contains(fragment, StringComparison.Ordinal)));

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        /// <inheritdoc />
        public void Dispose()
            => _entries.Clear();

        private void Add(LogLevel level, string message) => _entries.Add((level, message));

        private sealed class CapturingLogger(CapturingLogs owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                owner.Add(logLevel, $"{categoryName}: {formatter(state, exception)}");
            }
        }
    }
}
