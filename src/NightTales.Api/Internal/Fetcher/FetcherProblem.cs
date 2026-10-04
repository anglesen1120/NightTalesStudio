using System.Diagnostics;
using ErrorOr;
using NightTales.Api.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Internal.Fetcher;

/// <summary>
/// The error answer of the internal listener (02.5.2-T2): the platform's ProblemDetails shape (09 README D7), plus the
/// three D8 job codes that are a <c>400</c> only on this listener. Public routes never answer a job code (D8), so they
/// stay out of <see cref="ErrorCatalogue.Status"/>; here they are the contract between the Api and the fetcher.
/// </summary>
public static class FetcherProblem
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>The D8 job codes the result route answers as a 400 with the code itself.</summary>
    private static readonly HashSet<string> JobCodes = new(StringComparer.Ordinal)
    {
        ErrorCodes.SourceNoText,
        ErrorCodes.SourceTooLarge,
        ErrorCodes.SourceUnsupportedType,
    };

    /// <summary>The 400 <c>validation_failed</c> of one body member.</summary>
    /// <param name="field">The member.</param>
    /// <param name="description">What is wrong.</param>
    public static IResult Invalid(string field, string description) =>
        Results.Json(
            new ProblemBody("https://nighttales.app/errors/" + ErrorCodes.ValidationFailed, "Validation failed.", StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, Activity.Current?.Id, new Dictionary<string, string[]> { [field] = [description] }),
            contentType: ProblemJson,
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Maps a command's errors to the response.</summary>
    /// <param name="errors">The errors; the first one drives the status.</param>
    /// <param name="http">The request.</param>
    public static IResult From(List<Error> errors, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(http);

        var first = errors[0];
        if (JobCodes.Contains(first.Code))
        {
            return Results.Json(
                new ProblemBody(ErrorCatalogue.ProblemType(first.Code), "The result was refused.", StatusCodes.Status400BadRequest, first.Code, Activity.Current?.Id, null),
                contentType: ProblemJson,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ProblemDetailsWriter.ToProblem(errors, http);
    }

    private sealed record ProblemBody(
        string Type,
        string Title,
        int Status,
        string Code,
        string? TraceId,
        Dictionary<string, string[]>? Errors);
}
