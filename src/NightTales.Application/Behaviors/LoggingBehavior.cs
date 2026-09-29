using System.Diagnostics;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.Logging;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Wraps every message, measures it with <see cref="Stopwatch.GetTimestamp"/>, and emits the
/// structured <c>mediator.request</c> log: <c>Information</c> on success, <c>Warning</c> on error.
/// Logs the message type name and the first error code only — never message property values
/// (09 README D4, plan §5/§8).
/// </summary>
public sealed partial class LoggingBehavior<TMessage, TResponse>(ILogger<LoggingBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    private const string OutcomeOk = "ok";

    private const string OutcomeUnknownError = "error";

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var response = await next(message, cancellationToken).ConfigureAwait(false);
        var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var messageName = typeof(TMessage).Name;
        var traceId = Activity.Current?.Id;

        if (response.IsError)
        {
            var outcome = response.Errors is { Count: > 0 } errors ? errors[0].Code : OutcomeUnknownError;
            LogRequestWarning(messageName, durationMs, outcome, traceId);
        }
        else
        {
            LogRequest(messageName, durationMs, OutcomeOk, traceId);
        }

        return response;
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "mediator.request message={Message} durationMs={DurationMs} outcome={Outcome} traceId={TraceId}")]
    private partial void LogRequest(string message, double durationMs, string outcome, string? traceId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "mediator.request message={Message} durationMs={DurationMs} outcome={Outcome} traceId={TraceId}")]
    private partial void LogRequestWarning(string message, double durationMs, string outcome, string? traceId);
}
