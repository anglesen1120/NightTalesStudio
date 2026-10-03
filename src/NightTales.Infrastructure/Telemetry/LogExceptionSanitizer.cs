using System.Text;
using Npgsql;
using Serilog.Core;
using Serilog.Events;

namespace NightTales.Infrastructure.Telemetry;

/// <summary>
/// Rewrites an exception that carries PostgreSQL error data into a log-safe one (09.9.3-T1): the type name,
/// the SQLSTATE and the server's message text, trimmed — never <c>Detail</c>, which holds the offending
/// key/column values (user data), and never the <c>Where</c>/<c>Statement</c> fields.
/// </summary>
/// <remarks>
/// <c>Exception.ToString()</c> of a <see cref="PostgresException"/> appends <c>Detail</c> and the statement
/// context, and both the compact-JSON formatter and the OTLP sink render exceptions through it, so the
/// rewrite happens once, in <see cref="ExceptionSanitizingSink"/>, in front of every sink.
/// </remarks>
public static class LogExceptionSanitizer
{
    private const int MaxMessageLength = 300;

    /// <summary>The exception to log in place of <paramref name="exception"/>.</summary>
    /// <param name="exception">The original exception, or <see langword="null"/>.</param>
    /// <returns>
    /// <paramref name="exception"/> itself when no <see cref="PostgresException"/> is in its chain;
    /// otherwise a safe stand-in that renders the type, trimmed message and stack trace only.
    /// </returns>
    public static Exception? Sanitize(Exception? exception)
    {
        if (exception is null || !ContainsPostgresError(exception))
        {
            return exception;
        }

        return new SanitizedException(Describe(exception), exception.StackTrace);
    }

    private static bool ContainsPostgresError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException)
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsPostgresError))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (text.Length > 0)
            {
                text.Append(" ---> ");
            }

            text.Append(current.GetType().FullName).Append(": ").Append(current switch
            {
                PostgresException postgres => Trim($"{postgres.SqlState}: {postgres.MessageText}"),
                _ => Trim(current.Message),
            });
        }

        return text.ToString();
    }

    private static string Trim(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var line = message.AsSpan();
        var newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            line = line[..newline];
        }

        return line.Length <= MaxMessageLength ? line.ToString() : string.Concat(line[..MaxMessageLength], "…");
    }

    private sealed class SanitizedException : Exception
    {
        private readonly string? _stackTrace;

        public SanitizedException(string description, string? stackTrace)
            : base(description)
        {
            _stackTrace = stackTrace;
        }

        public override string? StackTrace => _stackTrace;

        public override string ToString() =>
            string.IsNullOrEmpty(_stackTrace) ? Message : $"{Message}{Environment.NewLine}{_stackTrace}";
    }
}

/// <summary>Applies <see cref="LogExceptionSanitizer"/> to every event before it reaches the wrapped sink.</summary>
/// <param name="inner">The sink every event is forwarded to.</param>
public sealed class ExceptionSanitizingSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var sanitized = LogExceptionSanitizer.Sanitize(logEvent.Exception);
        if (ReferenceEquals(sanitized, logEvent.Exception))
        {
            inner.Emit(logEvent);

            return;
        }

        inner.Emit(new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            sanitized,
            logEvent.MessageTemplate,
            logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value)),
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default));
    }

    /// <inheritdoc />
    public void Dispose() => (inner as IDisposable)?.Dispose();
}
