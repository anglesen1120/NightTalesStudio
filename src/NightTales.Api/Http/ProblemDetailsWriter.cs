using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http;

/// <summary>
/// Turns an <c>ErrorOr</c> error list into the uniform ProblemDetails response of 09 README D7 and
/// 09 §5.5, and owns the one 500 shape of the host.
/// </summary>
/// <remarks>
/// <para>Mapping rules (09.1.4-T1):</para>
/// <list type="number">
/// <item><description>Any <see cref="ErrorType.Validation"/> error → 400 <c>validation_failed</c>,
/// with every validation error grouped by its camelCase <see cref="ErrorMetadata.Field"/>.</description></item>
/// <item><description>Otherwise the <em>first</em> error's code drives the status through
/// <see cref="ErrorCatalogue.Status"/>; the code — not <c>ErrorType</c> — decides, so
/// <c>Error.NotFound(code: "episode_invalid_state")</c> answers 409.</description></item>
/// <item><description>A code that is not in the catalogue, and the 202-body and job-outcome codes,
/// answer 500 <c>internal_error</c> and are logged as a bug.</description></item>
/// <item><description>A 429 whose error carries <see cref="ErrorMetadata.RetryAfterSeconds"/> also
/// sets <c>Retry-After</c> (02 CR-20); a value that cannot be read as a number of seconds is logged
/// at Warning instead of being dropped silently.</description></item>
/// </list>
/// <para>
/// The body always has <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> and <c>traceId</c>
/// (from <see cref="Activity.Current"/>, like the log lines); metadata is copied as a top-level member
/// only for the allow-listed keys <see cref="ErrorMetadata.RetryAfterSeconds"/>,
/// <see cref="ErrorMetadata.Field"/> and <see cref="ErrorMetadata.Limit"/>, and only when the value is
/// a bounded scalar (<see cref="MaxMetadataStringLength"/> characters of string, a finite number or a
/// bool), so no internal value (a SQL fragment, a provider payload, a nested object) can leak through
/// an error (plan §5, §8).
/// </para>
/// <para>
/// Every 5xx the <see cref="IProblemDetailsService"/> writes is the same shape with a fixed title and
/// no exception text: <see cref="ConfigureProblemDetails"/> is the customisation hook of
/// <c>AddProblemDetails()</c>, so the exception-handler middleware and the Development developer
/// exception page both answer <c>internal_error</c> without the exception (plan §8 UC6).
/// </para>
/// </remarks>
public static partial class ProblemDetailsWriter
{
    /// <summary>The media type of every problem response (RFC 9457).</summary>
    public const string JsonContentType = "application/problem+json";

    /// <summary>The fixed title of the <c>internal_error</c> shape; never the exception text.</summary>
    public const string InternalErrorTitle = "An unexpected error occurred.";

    /// <summary>The title of a validation response; the fields and messages are in <c>errors</c>.</summary>
    public const string ValidationFailedTitle = "Validation failed.";

    /// <summary>The body member carrying a problem's stable code, <c>code</c>.</summary>
    public const string CodeMemberName = "code";

    /// <summary>
    /// The longest string the writer copies into a problem body from an allow-listed metadata key. The
    /// only keys that reach the body are field names, limits and retry-after seconds, so anything
    /// longer is not client data the contract describes and is dropped.
    /// </summary>
    public const int MaxMetadataStringLength = 128;

    /// <summary>The metadata key the developer exception page uses for the stack trace; removed from 5xx bodies.</summary>
    private const string ExceptionExtensionKey = "exception";

    /// <summary>The only metadata keys copied into the body (plan §5 [DECISION]).</summary>
    private static readonly FrozenSet<string> AllowedMetadataKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ErrorMetadata.RetryAfterSeconds,
            ErrorMetadata.Field,
            ErrorMetadata.Limit,
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// camelCase naming on the wire (<c>traceId</c>), which is also the policy the default problem
    /// details writer of <c>AddProblemDetails()</c> uses.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Writes <paramref name="errors"/> as the ProblemDetails response their codes map to, or as 500
    /// <c>internal_error</c> when no code applies.
    /// </summary>
    /// <param name="errors">The error list of an <c>ErrorOr</c> result; must not be empty.</param>
    /// <param name="http">The request, for the trace id, the <c>Retry-After</c> header and the logger.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static IResult ToProblem(List<Error> errors, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentOutOfRangeException.ThrowIfZero(errors.Count);

        var driving = errors[0];
        var isValidation = false;
        foreach (var error in errors)
        {
            if (error.Type == ErrorType.Validation)
            {
                isValidation = true;
                break;
            }
        }

        int status;
        string code;
        string title;
        Dictionary<string, List<string>>? fieldErrors = null;

        // The error the top-level metadata comes from. In a validation response it is the first
        // validation error: a non-validation error that the 400 hides must not contribute metadata.
        var metadataSource = driving;

        if (isValidation)
        {
            status = StatusCodes.Status400BadRequest;
            code = ErrorCodes.ValidationFailed;
            title = ValidationFailedTitle;
            fieldErrors = GroupByField(errors);
            metadataSource = FirstValidationError(errors);
            LogSuppressedNonValidationErrors(http, errors);
        }
        else if (ErrorCatalogue.Status.TryGetValue(driving.Code, out var mapped))
        {
            status = mapped;
            code = driving.Code;
            title = TitleFor(driving, mapped);
        }
        else
        {
            status = StatusCodes.Status500InternalServerError;
            code = ErrorCodes.InternalError;
            title = InternalErrorTitle;
            LogUnknownCode(http, driving.Code);
        }

        if (status == StatusCodes.Status429TooManyRequests)
        {
            ApplyRetryAfter(http, driving);
        }

        var body = new ProblemBody
        {
            Type = ErrorCatalogue.ProblemType(code),
            Title = title,
            Status = status,
            Code = code,
            TraceId = TraceId(http),
            Errors = fieldErrors,
            Metadata = AllowedMetadata(metadataSource, includeField: !isValidation, http),
        };

        return Results.Json(body, SerializerOptions, contentType: JsonContentType, statusCode: status);
    }

    /// <summary>
    /// The <c>AddProblemDetails</c> customisation: every 5xx body written through
    /// <see cref="IProblemDetailsService"/> becomes the fixed <c>internal_error</c> shape with the
    /// trace id of the request and without any exception text (plan §8).
    /// </summary>
    /// <param name="options">The options of the registered problem details service.</param>
    public static void ConfigureProblemDetails(ProblemDetailsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.CustomizeProblemDetails = CustomizeProblemDetails;
    }

    private static void CustomizeProblemDetails(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        if (status < StatusCodes.Status500InternalServerError
            || problem.Extensions.ContainsKey(CodeMemberName))
        {
            // A framework 4xx body (binding, routing) or an already-coded problem stays untouched.
            return;
        }

        problem.Status = StatusCodes.Status500InternalServerError;
        problem.Title = InternalErrorTitle;
        problem.Type = ErrorCatalogue.ProblemType(ErrorCodes.InternalError);
        problem.Detail = null;
        problem.Extensions.Remove(ExceptionExtensionKey);
        problem.Extensions[CodeMemberName] = ErrorCodes.InternalError;
    }

    /// <summary>
    /// Groups the validation errors by their camelCase field; an error without a usable
    /// <c>field</c> key lands under the empty name, so no message is ever dropped. Errors of another
    /// <see cref="ErrorType"/> are not field errors and stay out of the group.
    /// </summary>
    private static Dictionary<string, List<string>> GroupByField(List<Error> errors)
    {
        var messages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (error.Type != ErrorType.Validation)
            {
                continue;
            }

            var field = FieldOf(error);
            if (!messages.TryGetValue(field, out var fieldMessages))
            {
                fieldMessages = [];
                messages[field] = fieldMessages;
            }

            fieldMessages.Add(error.Description);
        }

        return messages;
    }

    /// <summary>The first validation error; the caller has already proven one exists.</summary>
    private static Error FirstValidationError(List<Error> errors)
    {
        foreach (var error in errors)
        {
            if (error.Type == ErrorType.Validation)
            {
                return error;
            }
        }

        return errors[0];
    }

    private static string FieldOf(Error error) =>
        error.Metadata is not null
        && error.Metadata.TryGetValue(ErrorMetadata.Field, out var value)
        && value is string field
        && !string.IsNullOrWhiteSpace(field)
            ? field
            : string.Empty;

    /// <summary>The description is the client-safe title; an empty one falls back to the status phrase.</summary>
    private static string TitleFor(Error error, int status) =>
        string.IsNullOrWhiteSpace(error.Description)
            ? ReasonPhrases.GetReasonPhrase(status)
            : error.Description;

    private static void ApplyRetryAfter(HttpContext http, Error error)
    {
        if (error.Metadata is null
            || !error.Metadata.TryGetValue(ErrorMetadata.RetryAfterSeconds, out var value))
        {
            return;
        }

        var seconds = SecondsText(value);
        if (seconds is not null)
        {
            http.Response.Headers.RetryAfter = seconds;
            return;
        }

        var logger = Logger(http);
        if (logger is null || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        LogUnusableRetryAfter(logger, ErrorMetadata.RetryAfterSeconds, ShapeOf(value), TraceId(http));
    }

    private static string? SecondsText(object? value) => value switch
    {
        int seconds when seconds >= 0 => seconds.ToString(CultureInfo.InvariantCulture),
        long seconds when seconds >= 0 => seconds.ToString(CultureInfo.InvariantCulture),
        string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0 => seconds.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// The allow-listed metadata of <paramref name="error"/>, or null when it contributes none (so the
    /// body carries no empty extension object). In a validation response <c>field</c> is what the
    /// <c>errors</c> group is keyed by, so it is not repeated at the top level. Only a bounded scalar
    /// value is copied; anything else is dropped and logged at Debug.
    /// </summary>
    private static Dictionary<string, object?>? AllowedMetadata(Error error, bool includeField, HttpContext http)
    {
        if (error.Metadata is null)
        {
            return null;
        }

        Dictionary<string, object?>? metadata = null;
        foreach (var (key, value) in error.Metadata)
        {
            if (!AllowedMetadataKeys.Contains(key)
                || (!includeField && string.Equals(key, ErrorMetadata.Field, StringComparison.Ordinal)))
            {
                continue;
            }

            if (!IsBoundedScalar(value))
            {
                LogDroppedMetadataValue(http, key, value);
                continue;
            }

            metadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            metadata[key] = value;
        }

        return metadata;
    }

    /// <summary>
    /// The value shapes the body may carry: a string of at most <see cref="MaxMetadataStringLength"/>
    /// characters, a finite number or a bool. A nested object, an array, a <see cref="DateTime"/> or
    /// <see cref="Guid"/>, an oversized string and a non-finite float (which JSON cannot encode) are
    /// all refused, so an allow-listed key is not a channel for structured data.
    /// </summary>
    private static bool IsBoundedScalar(object? value) => value switch
    {
        string text => text.Length <= MaxMetadataStringLength,
        bool => true,
        sbyte or byte or short or ushort or int or uint or long or ulong => true,
        float number => float.IsFinite(number),
        double number => double.IsFinite(number),
        decimal => true,
        _ => false,
    };

    private static void LogDroppedMetadataValue(HttpContext http, string key, object? value)
    {
        var logger = Logger(http);
        if (logger is null || !logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        // CA1873 (error in this repo) refuses invocations in the argument list of a Debug-level log
        // call, so the two Debug-only strings are materialised after the guard above.
        var shape = ShapeOf(value);
        var traceId = TraceId(http);
        LogMetadataValueDropped(logger, key, shape, traceId);
    }

    /// <summary>
    /// The value's shape for a log line: never the value itself, which may be the very payload the
    /// allow-list exists to keep out of the response.
    /// </summary>
    private static string ShapeOf(object? value) => value switch
    {
        null => "null",
        string text => string.Concat("String(", text.Length.ToString(CultureInfo.InvariantCulture), ")"),
        _ => value.GetType().Name,
    };

    /// <summary>
    /// A validation response hides every non-validation error, so the dropped ones are logged: the
    /// handler asked for a status the client never sees.
    /// </summary>
    private static void LogSuppressedNonValidationErrors(HttpContext http, List<Error> errors)
    {
        List<string>? codes = null;
        foreach (var error in errors)
        {
            if (error.Type != ErrorType.Validation)
            {
                codes ??= [];
                codes.Add(error.Code);
            }
        }

        if (codes is null)
        {
            return;
        }

        var logger = Logger(http);
        if (logger is null || !logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        LogSuppressedNonValidationError(logger, string.Join(", ", codes), TraceId(http));
    }

    private static string TraceId(HttpContext http) =>
        Activity.Current?.Id ?? http.TraceIdentifier;

    private static ILogger? Logger(HttpContext http) =>
        http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ProblemDetailsWriter).FullName!);

    private static void LogUnknownCode(HttpContext http, string code)
    {
        var logger = Logger(http);
        if (logger is not null)
        {
            LogUnknownErrorCode(logger, code, TraceId(http));
        }
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Error,
        Message = "problem_details.unknown_code code={Code} traceId={TraceId}: the code is not in ErrorCatalogue.Status; answering 500 internal_error.")]
    private static partial void LogUnknownErrorCode(ILogger logger, string code, string traceId);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "problem_details.suppressed_non_validation codes={Codes} traceId={TraceId}: the 400 validation_failed response drops these non-validation errors; only validation errors reach the body.")]
    private static partial void LogSuppressedNonValidationError(ILogger logger, string codes, string traceId);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "problem_details.retry_after_unusable key={Key} shape={Shape} traceId={TraceId}: the value is not a non-negative integer number of seconds; no Retry-After header was set.")]
    private static partial void LogUnusableRetryAfter(ILogger logger, string key, string shape, string traceId);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Debug,
        Message = "problem_details.metadata_value_dropped key={Key} shape={Shape} traceId={TraceId}: only a bounded scalar (a short string, a finite number or a bool) is copied into the problem body.")]
    private static partial void LogMetadataValueDropped(ILogger logger, string key, string shape, string traceId);

    /// <summary>The ProblemDetails body of this host (09 §5.5); metadata is flattened into it.</summary>
    internal sealed record ProblemBody
    {
        public required string Type { get; init; }

        public required string Title { get; init; }

        public required int Status { get; init; }

        public required string Code { get; init; }

        public required string TraceId { get; init; }

        public IReadOnlyDictionary<string, List<string>>? Errors { get; init; }

        [JsonExtensionData]
        public Dictionary<string, object?>? Metadata { get; init; }
    }
}
