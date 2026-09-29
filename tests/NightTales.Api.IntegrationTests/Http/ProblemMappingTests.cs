using System.Net;
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

    private static Dictionary<string, object> Field(string field) => new() { ["field"] = field };
}
