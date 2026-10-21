using Microsoft.Extensions.Logging;
using NightTales.Application.UnitTests.Pipeline.Probes;

namespace NightTales.Application.UnitTests.Pipeline;

/// <summary>Hand-written log capture (no mocking library, 09 README D13). Records into the pipeline trace.</summary>
public sealed class CapturingLoggerProvider(PipelineTrace trace) : ILoggerProvider
{
    private readonly List<string> _messages = [];
    private readonly Lock _gate = new();

    /// <summary>Every rendered log line, in order, prefixed with its level.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
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

        trace.Record("log");
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
            owner.Capture($"{logLevel}:{formatter(state, exception)}");
        }
    }
}
