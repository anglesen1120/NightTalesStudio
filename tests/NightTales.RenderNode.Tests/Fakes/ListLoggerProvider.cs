using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NightTales.RenderNode.Tests.Fakes;

/// <summary>
/// Captures everything the host logs, so a test can assert a secret never appears in any record (05 D15 S3:
/// key material is never logged).
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _records = new();

    /// <summary>Every formatted message, in arrival order.</summary>
    internal IReadOnlyCollection<string> Records => _records;

    /// <summary>True when any record carries <paramref name="needle"/>.</summary>
    internal bool Contains(string needle) => _records.Any(record => record.Contains(needle, StringComparison.Ordinal));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _records);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class ListLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            sink.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
