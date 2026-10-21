using System.Text;
using Npgsql;
using Serilog.Core;
using NightTales.Infrastructure.Telemetry.Redaction;
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
    /// <param name="redactKeys">
    /// The query keys to mask in the messages and stack trace (09.9.3-T2): an exception that quotes the request
    /// URL (<c>…?token=abc</c>) must not carry the value into a log line. <see langword="null"/> masks nothing.
    /// </param>
    /// <returns>
    /// <paramref name="exception"/> itself when no <see cref="PostgresException"/> is in its chain and no
    /// listed key occurs in it; otherwise a safe stand-in that renders the type, trimmed and redacted message
    /// and the stack trace only.
    /// </returns>
    public static Exception? Sanitize(Exception? exception, IReadOnlySet<string>? redactKeys = null)
    {
        if (exception is null || !(ContainsPostgresError(exception) || CarriesListedKey(exception, redactKeys)))
        {
            return exception;
        }

        var stackTrace = redactKeys is null
            ? exception.StackTrace
            : QueryStringRedactor.RedactText(exception.StackTrace, redactKeys);

        return new SanitizedException(Describe(exception, redactKeys), stackTrace);
    }

    private static bool CarriesListedKey(Exception exception, IReadOnlySet<string>? keys)
    {
        if (keys is null)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (Changes(current.Message, keys) || Changes(current.StackTrace, keys))
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => CarriesListedKey(inner, keys)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Changes(string? text, IReadOnlySet<string> keys) =>
        !ReferenceEquals(QueryStringRedactor.RedactText(text, keys), text);

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

    private static string Describe(Exception exception, IReadOnlySet<string>? redactKeys)
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
                PostgresException postgres => Trim(Redact($"{postgres.SqlState}: {postgres.MessageText}", redactKeys)),
                _ => Trim(Redact(current.Message, redactKeys)),
            });
        }

        return text.ToString();
    }

    private static string? Redact(string? text, IReadOnlySet<string>? keys) =>
        keys is null ? text : QueryStringRedactor.RedactText(text, keys);

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
/// <param name="redactKeys">The query keys masked in exception text; see <see cref="LogExceptionSanitizer.Sanitize"/>.</param>
public sealed class ExceptionSanitizingSink(ILogEventSink inner, IReadOnlySet<string>? redactKeys = null) : ILogEventSink, IDisposable
{
    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var sanitized = LogExceptionSanitizer.Sanitize(logEvent.Exception, redactKeys);
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
