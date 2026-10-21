using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace NightTales.Infrastructure.Telemetry;

/// <summary>
/// The file-less console logger of the host's start-up (09.9.3-T1): compact JSON on stdout, so a crash before
/// the dependency-injection container exists — a missing salt, an invalid endpoint — is still one JSON line
/// a log pipeline can parse instead of the runtime's plain-text stack trace.
/// </summary>
public static class BootstrapLogging
{
    /// <summary>The message template of the one start-up failure event.</summary>
    public const string FailureMessage = "Host terminated unexpectedly";

    /// <summary>Creates the bootstrap logger.</summary>
    /// <remarks>
    /// It masks with the frozen key list (<see cref="Redaction.RedactionOptions.Default"/>) only: it runs before the
    /// configuration is bound, so the additive <c>Logging:RedactionQueryKeys</c> do not apply to start-up events.
    /// </remarks>
    /// <param name="output">The writer to log to; <see langword="null"/> is the process's stdout.</param>
    /// <returns>A logger that writes compact JSON, one event per line, synchronously.</returns>
    public static Serilog.ILogger Create(TextWriter? output = null) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new ExceptionSanitizingSink(
                new WriterSink(output ?? Console.Out, new CompactJsonFormatter()),
                Redaction.RedactionOptions.Default.QueryKeys))
            .CreateLogger();

    /// <summary>
    /// Logs the start-up failure as one fatal JSON event. The entry point rethrows afterwards, so the process
    /// still ends with the runtime's non-zero exit.
    /// </summary>
    /// <param name="exception">What stopped the host.</param>
    /// <param name="logger">The logger to write to; <see langword="null"/> is <see cref="Log.Logger"/>.</param>
    public static void ReportFailure(Exception exception, Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        (logger ?? Log.Logger).Fatal(exception, FailureMessage);
    }

    private sealed class WriterSink(TextWriter writer, ITextFormatter formatter) : ILogEventSink
    {
        private readonly Lock _gate = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_gate)
            {
                formatter.Format(logEvent, writer);
                writer.Flush();
            }
        }
    }
}
