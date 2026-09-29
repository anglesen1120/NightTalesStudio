using System.Net;
using System.Text.Json;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Api.Http;
using NightTales.Application.Common.Errors;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// QA boundary cases of <c>ProblemDetailsWriter.ToProblem</c> (09.1.4-T1): the mixed-error list, a
/// validation error without a usable field, a field name that collides with a reserved member,
/// hostile <c>retryAfterSeconds</c> values, metadata shapes and the non-HTTP codes.
/// </summary>
public sealed class ProblemBoundaryTests
{
    [Fact]
    public async Task Map_MixedList_FirstErrorDrivesStatus_OthersAreNotListed()
    {
        // The failure table: "Several non-validation errors — the first determines the status; all
        // are listed under errors only if they are validation errors."
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.SeriesArchived, "The series is archived."),
            Error.Failure(ErrorCodes.NotFound, "And it does not exist."),
            Error.Failure(ErrorCodes.RateLimited, "And the client is throttled."),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(409);
        response.Code.ShouldBe("series_archived");
        response.Title.ShouldBe("The series is archived.");
        response.Errors.ShouldBeNull("non-validation errors are never listed under errors");
        exchange.Body.ShouldNotContain("not_found", Case.Sensitive);
        exchange.Body.ShouldNotContain("rate_limited", Case.Sensitive);
    }

    [Fact]
    public async Task Map_MixedList_AnyValidationErrorForces400()
    {
        // A validation error anywhere in the list wins over a 4xx/5xx code that precedes it.
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.ServiceUnavailable, "Database is down."),
            Error.Validation(ErrorCodes.ValidationFailed, "Title is required.", Field("title")),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Code.ShouldBe("validation_failed");
        response.Errors.ShouldNotBeNull();
        response.Errors["title"].ShouldBe(["Title is required."]);
        response.Errors.Keys.ShouldBe(["title"]);
    }

    [Fact]
    public async Task Map_ValidationErrorWithoutField_GroupsUnderEmptyKey()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Validation(ErrorCodes.ValidationFailed, "The request is malformed."),
            Error.Validation(ErrorCodes.ValidationFailed, "Whitespace-only field.", Field("   ")),
            Error.Validation(ErrorCodes.ValidationFailed, "Non-string field.", new Dictionary<string, object> { ["field"] = 42 }),
            Error.Validation(ErrorCodes.ValidationFailed, "Title is required.", Field("title")),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Errors.ShouldNotBeNull();
        response.Errors[""].ShouldBe(
            ["The request is malformed.", "Whitespace-only field.", "Non-string field."],
            "a validation error without a usable field is grouped under the empty name, not dropped");
        response.Errors["title"].ShouldBe(["Title is required."]);
    }

    [Fact]
    public async Task Map_ValidationFieldNamedLikeReservedMember_StaysInsideErrors()
    {
        // A field literally called "status"/"code"/"traceId" nests under errors and must not
        // overwrite the problem members.
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Validation(ErrorCodes.ValidationFailed, "Status is not allowed.", Field("status")),
            Error.Validation(ErrorCodes.ValidationFailed, "Code is not allowed.", Field("code")),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(400);
        response.Code.ShouldBe("validation_failed");
        response.TraceId.ShouldNotBeNullOrWhiteSpace();
        response.Errors.ShouldNotBeNull();
        response.Errors["status"].ShouldBe(["Status is not allowed."]);
        response.Errors["code"].ShouldBe(["Code is not allowed."]);
        response.Type.ShouldBe("https://nighttales.app/errors/validation_failed");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("Sun, 06 Nov 1994 08:49:37 GMT")]
    [InlineData("12.5")]
    public async Task Map_429RetryAfterNonSecondsValue_SetsNoHeader(string value)
    {
        // The contract allows seconds only (09.1.4-T1: retryAfterSeconds); a string that does not
        // parse as a non-negative integer is silently dropped, HTTP-dates included.
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.RateLimited, "Slow down.", Meta("retryAfterSeconds", value))];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
        response.ShouldNotBeNull();
        response.Code.ShouldBe("rate_limited");
        response.Metadata.ShouldNotBeNull();
        response.Metadata["retryAfterSeconds"].GetString().ShouldBe(value,
            "the allow-listed metadata key still reaches the body even when it is unusable as a header");
    }

    [Fact]
    public async Task Map_429RetryAfterBoundaries_ZeroAndStringAreAccepted()
    {
        await AssertRetryAfterAsync(0, "0");
        await AssertRetryAfterAsync(300L, "300");
        await AssertRetryAfterAsync("90", "90");
    }

    [Fact]
    public async Task Map_429RetryAfterHostileShapes_SetNoHeader()
    {
        await AssertNoRetryAfterAsync(-1);
        await AssertNoRetryAfterAsync(1e9);
        await AssertNoRetryAfterAsync(-5L);
        await AssertNoRetryAfterAsync(new { seconds = 30 });
        await AssertNoRetryAfterAsync(true);
    }

    [Fact]
    public async Task Map_503RetryAfterMetadata_DoesNotSetHeader()
    {
        // Retry-After is only honoured on a 429.
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.ServiceUnavailable, "Down.", Meta("retryAfterSeconds", 30))];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        exchange.Context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
        response.ShouldNotBeNull();
        response.Metadata!["retryAfterSeconds"].GetInt32().ShouldBe(30);
    }

    [Fact]
    public async Task Map_MetadataValues_NestedOrOversizedShapesAreDropped()
    {
        using var exchange = new ProblemExchange();
        var oversized = new string('x', ProblemDetailsWriter.MaxMetadataStringLength + 1);
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.ServiceUnavailable, "Transient.", new Dictionary<string, object>
            {
                ["limit"] = new { count = 10, window = "24h" },
                ["stackTrace"] = "at NightTales.Foo.Bar()",
                ["sql"] = oversized,
                ["field"] = "db",
            }),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        exchange.Body.ShouldNotContain("stackTrace", Case.Sensitive);
        exchange.Body.ShouldNotContain("NightTales.Foo", Case.Sensitive);
        exchange.Body.ShouldNotContain(oversized, Case.Sensitive);
        exchange.Body.ShouldNotContain("window", Case.Sensitive);
        response.ShouldNotBeNull();
        response.Metadata.ShouldNotBeNull();
        response.Metadata.ShouldNotContainKey("sql");
        response.Metadata.ShouldNotContainKey("stackTrace");
        response.Metadata.ShouldNotContainKey("limit");
        // An allow-listed key is not a channel for structured data: a short string still passes.
        response.Metadata["field"].GetString().ShouldBe("db");
        exchange.Logs.Contains(LogLevel.Debug, "problem_details.metadata_value_dropped", "limit")
            .ShouldBeTrue("an allow-listed key with a non-scalar value is dropped and logged at Debug");
    }

    [Fact]
    public async Task Map_MetadataValues_UnserializableShapesAreRefusedToo()
    {
        // A non-finite float cannot be encoded as JSON at all: copying it verbatim would turn an
        // error response into a serializer exception.
        await AssertMetadataRefusedAsync(double.NaN);
        await AssertMetadataRefusedAsync(double.PositiveInfinity);
        await AssertMetadataRefusedAsync(ArrayMetadataValue);
        await AssertMetadataRefusedAsync(DateTime.UnixEpoch);
        await AssertMetadataRefusedAsync(Guid.CreateVersion7());
        await AssertMetadataRefusedAsync(new string('x', ProblemDetailsWriter.MaxMetadataStringLength + 1));
        await AssertMetadataRefusedAsync(null, rawNull: true);
    }

    [Fact]
    public async Task Map_MetadataValues_BoundedScalarsSurvive()
    {
        await AssertMetadataKeptAsync(20, "20");
        await AssertMetadataKeptAsync(20L, "20");
        await AssertMetadataKeptAsync(1.5, "1.5");
        await AssertMetadataKeptAsync(true, "true");
        await AssertMetadataKeptAsync("20", "\"20\"");
        await AssertMetadataKeptAsync(
            new string('x', ProblemDetailsWriter.MaxMetadataStringLength),
            string.Concat("\"", new string('x', ProblemDetailsWriter.MaxMetadataStringLength), "\""));
    }

    [Fact]
    public async Task Map_ValidationBody_TakesNoMetadataFromTheSuppressedError()
    {
        // A non-validation error is hidden by the 400; its allow-listed metadata must not ride along.
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.ServiceUnavailable, "Database is down.", Meta("limit", 20)),
            Error.Validation(ErrorCodes.ValidationFailed, "Title is required.", Field("title")),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Code.ShouldBe(ErrorCodes.ValidationFailed);
        response.Metadata.ShouldBeNull("the suppressed error contributes no top-level metadata");
        exchange.Body.ShouldNotContain("service_unavailable", Case.Sensitive);
        exchange.Logs.Contains(
                LogLevel.Warning,
                "problem_details.suppressed_non_validation",
                ErrorCodes.ServiceUnavailable)
            .ShouldBeTrue("a dropped non-validation error must leave a trace");
    }

    [Fact]
    public async Task Map_ValidationBody_KeepsTheValidationErrorsOwnAllowListedMetadata()
    {
        // The control for the test above: the first validation error is the metadata source, and
        // `field` stays inside the `errors` group instead of being repeated at the top level.
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.ServiceUnavailable, "Database is down.", Meta("limit", 5)),
            Error.Validation(ErrorCodes.ValidationFailed, "The page size is too large.", new Dictionary<string, object>
            {
                [ErrorMetadata.Field] = "cursor",
                [ErrorMetadata.Limit] = 100,
            }),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Errors.ShouldNotBeNull();
        response.Errors["cursor"].ShouldBe(["The page size is too large."]);
        response.Metadata.ShouldNotBeNull();
        response.Metadata[ErrorMetadata.Limit].GetInt32().ShouldBe(
            100,
            "the validation error's own limit wins over the suppressed error's");
        response.Metadata.ShouldNotContainKey(ErrorMetadata.Field);
        exchange.Logs.Contains(LogLevel.Warning, "problem_details.suppressed_non_validation")
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Map_ProblemWithoutExtraMetadata_HasExactlyTheDocumentedMembers()
    {
        // Nothing to add means no extension object at all (the writer returns null, not an empty
        // dictionary), so the body is byte-identical to the plan §5 shape.
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.NotFound, "Gone.")];

        await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        using var document = JsonDocument.Parse(exchange.Body);
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(DocumentedProblemMembers);
    }

    [Fact]
    public async Task Map_JobOutcomeCode_Returns500AndLogs()
    {
        // NonHttpCodes carry no status; returning one as an error hits the unknown-code path.
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.RenderLost, "The adapter lost the task.")];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.ShouldNotBeNull();
        response.Code.ShouldBe("internal_error");
        response.Status.ShouldBe(500);
        exchange.Body.ShouldNotContain("render_lost", Case.Sensitive);
        exchange.Logs.Contains(LogLevel.Error, "problem_details.unknown_code", ErrorCodes.RenderLost)
            .ShouldBeTrue("a job-outcome code surfaced as an error is a bug and is logged at Error");
    }

    [Fact]
    public async Task Map_NonValidationError_WritesErrorsMemberAsNull()
    {
        // The plan's example body ends with `"errors": null` (plan §5); the member is present.
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.NotFound, "Gone.")];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        response.ShouldNotBeNull();
        exchange.Body.ShouldContain("\"errors\":null");
    }

    [Fact]
    public async Task Map_EmptyDescription_FallsBackToReasonPhrase()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.ServiceUnavailable, "")];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        response.ShouldNotBeNull();
        response.Title.ShouldBe("Service Unavailable");
    }

    [Fact]
    public void Map_EmptyErrorList_Throws()
    {
        using var exchange = new ProblemExchange();

        Should.Throw<ArgumentOutOfRangeException>(
            () => ProblemDetailsWriter.ToProblem([], exchange.Context));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData(true)]
    [InlineData(-5)]
    [InlineData(1.5)]
    public async Task Map_429RetryAfterUnusableValue_LogsAWarning(object value)
    {
        // QA observed silent drops: an unusable value leaves the header unset, so the log is the only
        // trace a handler gets.
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.RateLimited, "Slow down.", Meta("retryAfterSeconds", value))];

        await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
        exchange.Logs.Contains(
                LogLevel.Warning,
                "problem_details.retry_after_unusable",
                ErrorMetadata.RetryAfterSeconds)
            .ShouldBeTrue($"retryAfterSeconds={value} cannot become a Retry-After header and must be logged");
    }

    /// <summary>An array is not a scalar, however short it is.</summary>
    private static readonly int[] ArrayMetadataValue = [20];

    /// <summary>The members every problem body carries (plan §5); nothing else, so no empty metadata object.</summary>
    private static readonly string[] DocumentedProblemMembers = ["type", "title", "status", "code", "traceId", "errors"];

    private static Dictionary<string, object> Field(string field) => new() { [ErrorMetadata.Field] = field };

    private static Dictionary<string, object> Meta(string key, object value) => new() { [key] = value };

    private static Dictionary<string, object> NullMetadata(string key) => new() { [key] = null! };

    private static async Task AssertMetadataRefusedAsync(object? value, bool rawNull = false)
    {
        using var exchange = new ProblemExchange();
        var metadata = rawNull
            ? NullMetadata("limit")
            : new Dictionary<string, object> { ["limit"] = value! };
        List<Error> errors = [Error.Failure(ErrorCodes.ServiceUnavailable, "Transient.", metadata)];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        response.ShouldNotBeNull();
        (response.Metadata is not null && response.Metadata.ContainsKey("limit")).ShouldBeFalse(
            $"a metadata value of shape {(rawNull ? "null" : value!.GetType().Name)} must not reach the body");
        exchange.Logs.Contains(LogLevel.Debug, "problem_details.metadata_value_dropped", "limit")
            .ShouldBeTrue();
    }

    private static async Task AssertMetadataKeptAsync(object value, string expectedJson)
    {
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.ServiceUnavailable, "Transient.", Meta("limit", value))];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        response.ShouldNotBeNull();
        response.Metadata.ShouldNotBeNull();
        response.Metadata!["limit"].GetRawText().ShouldBe(
            expectedJson,
            $"a bounded scalar of shape {value.GetType().Name} is exactly what an allow-listed key carries");
    }

    private static async Task AssertRetryAfterAsync(object metadataValue, string expectedHeader)
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
            [Error.Failure(ErrorCodes.RateLimited, "Slow down.", Meta("retryAfterSeconds", metadataValue))];

        await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.RetryAfter.ToString().ShouldBe(expectedHeader);
    }

    private static async Task AssertNoRetryAfterAsync(object metadataValue)
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
            [Error.Failure(ErrorCodes.RateLimited, "Slow down.", Meta("retryAfterSeconds", metadataValue))];

        await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.ContainsKey("Retry-After")
            .ShouldBeFalse($"retryAfterSeconds={metadataValue} must not become a Retry-After header");
    }
}
