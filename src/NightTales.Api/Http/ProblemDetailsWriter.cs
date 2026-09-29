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
/// with every validation error grouped by its camelCase <c>metadata["field"]</c>.</description></item>
/// <item><description>Otherwise the <em>first</em> error's code drives the status through
/// <see cref="ErrorCatalogue.Status"/>; the code — not <c>ErrorType</c> — decides, so
/// <c>Error.NotFound(code: "episode_invalid_state")</c> answers 409.</description></item>
/// <item><description>A code that is not in the catalogue, and the 202-body and job-outcome codes,
/// answer 500 <c>internal_error</c> and are logged as a bug.</description></item>
/// <item><description>A 429 whose error carries <c>retryAfterSeconds</c> also sets
/// <c>Retry-After</c> (02 CR-20).</description></item>
/// </list>
/// <para>
/// The body always has <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> and <c>traceId</c>
/// (from <see cref="Activity.Current"/>, like the log lines); metadata is copied as a top-level member
/// only for the allow-listed keys <c>retryAfterSeconds</c>, <c>field</c> and <c>limit</c>, so no
/// internal value (a SQL fragment, a provider payload) can leak through an error (plan §5, §8).
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

    /// <summary>The error metadata key holding the camelCase field name of a validation failure.</summary>
    public const string FieldMetadataKey = "field";

    /// <summary>The error metadata key holding the seconds a 429 client must wait.</summary>
    public const string RetryAfterMetadataKey = "retryAfterSeconds";

    /// <summary>The metadata key the developer exception page uses for the stack trace; removed from 5xx bodies.</summary>
    private const string ExceptionExtensionKey = "exception";

    /// <summary>The only metadata keys copied into the body (plan §5 [DECISION]).</summary>
    private static readonly FrozenSet<string> AllowedMetadataKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RetryAfterMetadataKey,
            FieldMetadataKey,
            "limit",
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
        Dictionary<string, IReadOnlyList<string>>? fieldErrors = null;

        if (isValidation)
        {
            status = StatusCodes.Status400BadRequest;
            code = ErrorCodes.ValidationFailed;
            title = ValidationFailedTitle;
            fieldErrors = GroupByField(errors);
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
            Metadata = AllowedMetadata(driving, includeField: !isValidation),
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
    private static Dictionary<string, IReadOnlyList<string>> GroupByField(List<Error> errors)
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

        var grouped = new Dictionary<string, IReadOnlyList<string>>(messages.Count, StringComparer.Ordinal);
        foreach (var (field, fieldMessages) in messages)
        {
            grouped[field] = fieldMessages;
        }

        return grouped;
    }

    private static string FieldOf(Error error) =>
        error.Metadata is not null
        && error.Metadata.TryGetValue(FieldMetadataKey, out var value)
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
            || !error.Metadata.TryGetValue(RetryAfterMetadataKey, out var value))
        {
            return;
        }

        var seconds = SecondsText(value);
        if (seconds is not null)
        {
            http.Response.Headers.RetryAfter = seconds;
        }
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
    /// The allow-listed metadata of the driving error. In a validation response <c>field</c> is what
    /// the <c>errors</c> group is keyed by, so it is not repeated at the top level.
    /// </summary>
    private static Dictionary<string, object?> AllowedMetadata(Error error, bool includeField)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (error.Metadata is null)
        {
            return metadata;
        }

        foreach (var (key, value) in error.Metadata)
        {
            if (!AllowedMetadataKeys.Contains(key))
            {
                continue;
            }

            if (!includeField && string.Equals(key, FieldMetadataKey, StringComparison.Ordinal))
            {
                continue;
            }

            metadata[key] = value;
        }

        return metadata;
    }

    private static string TraceId(HttpContext http) =>
        Activity.Current?.Id ?? http.TraceIdentifier;

    private static void LogUnknownCode(HttpContext http, string code)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ProblemDetailsWriter).FullName!);

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

    /// <summary>The ProblemDetails body of this host (09 §5.5); metadata is flattened into it.</summary>
    internal sealed record ProblemBody
    {
        public required string Type { get; init; }

        public required string Title { get; init; }

        public required int Status { get; init; }

        public required string Code { get; init; }

        public required string TraceId { get; init; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors { get; init; }

        [JsonExtensionData]
        public Dictionary<string, object?>? Metadata { get; init; }
    }
}
