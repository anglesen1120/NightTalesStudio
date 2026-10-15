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
/// The mapping rules of <c>ProblemDetailsWriter.ToProblem</c> (09 README D7, 09.1.4-T1). These run
/// without a host: the writer is handed an error list and a request context, and the response body is
/// read back as the JSON a client would receive.
/// </summary>
public sealed class ProblemMappingTests
{
    [Fact]
    public async Task Map_ValidationErrors_Returns400WithFieldErrors()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Validation(ErrorCodes.ValidationFailed, "Title must not be empty.", Field("title")),
            Error.Validation(ErrorCodes.ValidationFailed, "Brief is too long.", Field("brief")),
            Error.Validation(ErrorCodes.ValidationFailed, "Title is already used.", Field("title")),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(400);
        response.Code.ShouldBe(ErrorCodes.ValidationFailed);
        response.Type.ShouldBe("https://nighttales.app/errors/validation_failed");
        response.Errors.ShouldNotBeNull();
        response.Errors["title"].ShouldBe(["Title must not be empty.", "Title is already used."]);
        response.Errors["brief"].ShouldBe(["Brief is too long."]);
        response.TraceId.ShouldNotBeNullOrWhiteSpace();
        exchange.Logs.Entries.ShouldNotContain(
            entry => entry.Level >= LogLevel.Warning,
            "a validation failure is not a bug");
    }

    [Fact]
    public async Task Map_LoneCataloguedValidationCode_KeepsItsOwnCode()
    {
        using var exchange = new ProblemExchange();

        // 09.3.1-T1: IdentityPasswordPolicy answers Error.Validation(breached_password); 09 §5.4 wants the
        // 400 to say breached_password, not the collapsed validation_failed (T3 C2).
        List<Error> errors =
        [
            Error.Validation(ErrorCodes.BreachedPassword, "This password appears in a breached-password list."),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(400);
        response.Code.ShouldBe(ErrorCodes.BreachedPassword);
        response.Type.ShouldBe("https://nighttales.app/errors/breached_password");
        response.Errors.ShouldBeNull("a lone coded error is not a field group");
    }

    [Fact]
    public async Task Map_ValidationList_MixedCodes_StillCollapse()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Validation(ErrorCodes.BreachedPassword, "This password appears in a breached-password list."),
            Error.Validation(ErrorCodes.ValidationFailed, "Title is required.", Field("title")),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.ShouldNotBeNull();
        response.Code.ShouldBe(
            ErrorCodes.ValidationFailed,
            "a list of several validation failures has no single client-visible code");
        response.Errors.ShouldNotBeNull();
        response.Errors.ShouldContainKey(string.Empty);
        response.Errors.ShouldContainKey("title");
    }

    [Fact]
    public async Task Map_CodeDrivesStatus_NotErrorType()
    {
        using var exchange = new ProblemExchange();

        // ErrorType.NotFound, but episode_invalid_state is a 409 in the catalogue: the code wins.
        List<Error> errors = [Error.NotFound(ErrorCodes.EpisodeInvalidState, "The episode is rendering.")];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(409);
        response.Code.ShouldBe("episode_invalid_state");
        response.Title.ShouldBe("The episode is rendering.");
        response.Type.ShouldBe("https://nighttales.app/errors/episode_invalid_state");
        response.Errors.ShouldBeNull();
    }

    [Fact]
    public async Task Map_UnknownCode_Returns500AndLogs()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure("not_in_the_catalogue", "A code that was never catalogued.")];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(500);
        response.Code.ShouldBe("internal_error");
        response.Type.ShouldBe("https://nighttales.app/errors/internal_error");
        exchange.Body.ShouldNotContain("not_in_the_catalogue", Case.Sensitive);
        exchange.Logs.Contains(LogLevel.Error, "problem_details.unknown_code", "not_in_the_catalogue")
            .ShouldBeTrue("an unknown code is a bug and must be logged at Error");
    }

    [Fact]
    public async Task Map_202BodyCodeReturnedAsAnError_FailsLoudly()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.OwnerActionPending, "A success code used as an error.")];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.ShouldNotBeNull();
        response.Code.ShouldBe("internal_error");
        exchange.Logs.Contains(LogLevel.Error, "problem_details.unknown_code", ErrorCodes.OwnerActionPending)
            .ShouldBeTrue("a 202 body code must never be mapped to a status");
    }

    [Fact]
    public async Task Map_429WithRetryAfter_SetsHeader()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.StoryGenerationLimit, "5 story generations per rolling 24 h.", new Dictionary<string, object>
            {
                ["retryAfterSeconds"] = 30,
            }),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.RetryAfter.ToString().ShouldBe("30");
        response.ShouldNotBeNull();
        response.Code.ShouldBe("story_generation_limit");
        response.Metadata.ShouldNotBeNull();
        response.Metadata["retryAfterSeconds"].GetInt32().ShouldBe(30);
    }

    [Fact]
    public async Task Map_429WithoutRetryAfter_LeavesTheHeaderUnset()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors = [Error.Failure(ErrorCodes.RateLimited, "Slow down.")];

        await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exchange.Context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
    }

    [Fact]
    public async Task Map_MetadataOutsideAllowList_IsDropped()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Failure(ErrorCodes.ServiceUnavailable, "Transient database failure.", new Dictionary<string, object>
            {
                ["limit"] = 20,
                ["sql"] = "select * from users where id = 'leaked'",
            }),
        ];

        var response = await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        exchange.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        exchange.Body.ShouldNotContain("sql", Case.Sensitive);
        exchange.Body.ShouldNotContain("select * from users", Case.Sensitive);
        response.ShouldNotBeNull();
        response.Metadata.ShouldNotBeNull();
        response.Metadata.ShouldNotContainKey("sql");
        response.Metadata["limit"].GetInt32().ShouldBe(20);
    }

    /// <summary>
    /// <c>409 keywords_flagged</c> carries the <c>{ segment, index, rule }</c> object array the web
    /// <c>keyword-flags.mapper</c> reads — not the <c>field → messages</c> object (03 D10, 09.6.1-T1).
    /// </summary>
    [Fact]
    public async Task Map_KeywordsFlagged_EmitsErrorsAsObjectArray()
    {
        using var exchange = new ProblemExchange();
        var errors = new List<Error>
        {
            Errors.KeywordsFlagged(
            [
                new KeywordFlag("p:3", 1, "keyword_name_detected"),
                new KeywordFlag("hook", 0, "keyword_invalid"),
            ]),
        };

        // The internal ProblemResponse DTO types errors as the field-group object, which the
        // keywords_flagged array is not, so the body is read as raw JSON here.
        await ProblemDetailsWriter.ToProblem(errors, exchange.Context).ExecuteAsync(exchange.Context);

        exchange.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var document = JsonDocument.Parse(exchange.Body);
        var root = document.RootElement;
        root.GetProperty("code").GetString().ShouldBe(ErrorCodes.KeywordsFlagged);
        root.GetProperty("status").GetInt32().ShouldBe(409);
        var rows = root.GetProperty("errors");
        rows.ValueKind.ShouldBe(JsonValueKind.Array);
        rows.GetArrayLength().ShouldBe(2);
        rows[0].GetProperty("segment").GetString().ShouldBe("p:3");
        rows[0].GetProperty("index").GetInt32().ShouldBe(1);
        rows[0].GetProperty("rule").GetString().ShouldBe("keyword_name_detected");
        rows[1].GetProperty("rule").GetString().ShouldBe("keyword_invalid");

        // The typed flags are consumed into errors[], never flattened into the body as a second member.
        root.TryGetProperty("flags", out _).ShouldBeFalse();
    }

    /// <summary>The <c>no_scenes</c> synthetic row of an image plan with no renderable scene (03.2.2-T2).</summary>
    [Fact]
    public async Task Map_NoScenes_EmitsTheMetaRow()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors = [Errors.NoScenes()];

        await ProblemDetailsWriter.ToProblem(errors, exchange.Context).ExecuteAsync(exchange.Context);

        exchange.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var document = JsonDocument.Parse(exchange.Body);
        var row = document.RootElement.GetProperty("errors")[0];
        row.GetProperty("segment").GetString().ShouldBe("meta");
        row.GetProperty("index").GetInt32().ShouldBe(0);
        row.GetProperty("rule").GetString().ShouldBe(RenderErrorRules.NoScenes);
    }

    /// <summary>
    /// The widening is scoped: a <c>validation_failed</c> body still carries the <c>field → messages</c>
    /// object, byte-for-byte the shape it carried before (09.6.1-T1).
    /// </summary>
    [Fact]
    public async Task Map_ValidationFailed_StillEmitsFieldGroupsObject()
    {
        using var exchange = new ProblemExchange();
        List<Error> errors =
        [
            Error.Validation(ErrorCodes.ValidationFailed, "Title must not be empty.", Field("title")),
        ];

        await exchange.ExecuteAsync(ProblemDetailsWriter.ToProblem(errors, exchange.Context));

        using var document = JsonDocument.Parse(exchange.Body);
        var errorsElement = document.RootElement.GetProperty("errors");
        errorsElement.ValueKind.ShouldBe(JsonValueKind.Object);
        errorsElement.GetProperty("title")[0].GetString().ShouldBe("Title must not be empty.");
    }

    private static Dictionary<string, object> Field(string field) => new() { ["field"] = field };
}
