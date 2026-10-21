using ErrorOr;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Common;

namespace NightTales.Api.Http;

/// <summary>
/// The endpoint half of the 09 README D7 contract: every handler returns <c>ErrorOr&lt;T&gt;</c> and
/// hands it to one of these helpers instead of building a status by hand.
/// </summary>
/// <remarks>
/// <para>
/// An error result becomes the ProblemDetails body of <see cref="ProblemDetailsWriter"/>; a success
/// result becomes the documented status, with <c>Location</c> and <c>ETag</c> set when the route or the
/// value provides them. The <c>ETag</c> header is written on the response before the body is, and the
/// helpers that take an <c>etag</c> delegate read its value <em>after</em> the handler's unit of work
/// committed, because <c>xmin</c> is assigned at commit (09 README D5, D15).
/// </para>
/// <para>
/// The delegate overloads of <see cref="ToHttpResult{T}(ErrorOr{T}, HttpContext, Func{T, CancellationToken, Task{string?}})"/>
/// and <see cref="ToNoContentResult{T}(ErrorOr{T}, HttpContext, Func{T, CancellationToken, Task{string?}})"/>
/// are <em>authoritative</em>: a null or empty result sets no header and the value's own
/// <see cref="IHasETag"/> is not consulted, because a value built before the commit carries a tag the
/// client must not use for its next <c>If-Match</c>. <see cref="ToCreatedResult{T}"/> keeps the
/// opposite rule on purpose — there the created value's tag is what the endpoint just produced, so the
/// delegate only refines it.
/// </para>
/// </remarks>
public static class ResultExtensions
{
    /// <summary>200 with the value; sets <c>ETag</c> when the value implements <see cref="IHasETag"/>.</summary>
    /// <typeparam name="T">The success type of the handler.</typeparam>
    /// <param name="result">The handler result.</param>
    /// <param name="http">The request.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static IResult ToHttpResult<T>(this ErrorOr<T> result, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        ApplyETag(http, result.Value);

        return Results.Json<T>(result.Value, statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// 200 with the value and the <c>ETag</c> the delegate reads after the handler's transaction
    /// committed. The delegate is the only source: the value's own <see cref="IHasETag"/> is stale at
    /// that point and is never emitted (09 README D15, 09.7.1-T1).
    /// </summary>
    /// <typeparam name="T">The success type of the handler.</typeparam>
    /// <param name="result">The handler result.</param>
    /// <param name="http">The request.</param>
    /// <param name="etag">Reads the current ETag after commit; a null or empty value sets no header.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static async Task<IResult> ToHttpResult<T>(
        this ErrorOr<T> result,
        HttpContext http,
        Func<T, CancellationToken, Task<string?>> etag)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(etag);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;
        SetETag(http, await etag(value, http.RequestAborted).ConfigureAwait(false));

        return Results.Json<T>(value, statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// 201 with <c>Location</c> and the created resource's <c>ETag</c>, read after the handler's
    /// transaction committed.
    /// </summary>
    /// <typeparam name="T">The success type of the handler (the created resource or its id).</typeparam>
    /// <param name="result">The handler result.</param>
    /// <param name="http">The request.</param>
    /// <param name="location">The URI of the created resource, for example <c>/api/v1/series/{id}</c>.</param>
    /// <param name="etag">Reads the current ETag after commit; a null or empty value falls back to the value's own <see cref="IHasETag"/>.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static async Task<IResult> ToCreatedResult<T>(
        this ErrorOr<T> result,
        HttpContext http,
        Func<T, string> location,
        Func<T, CancellationToken, Task<string?>> etag)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(etag);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;
        http.Response.Headers.Location = location(value);

        var tag = await etag(value, http.RequestAborted).ConfigureAwait(false) ?? ETagOf(value);
        SetETag(http, tag);

        return Results.Json<T>(value, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>202 with the value and, when given, the <c>Location</c> of the pending resource.</summary>
    /// <typeparam name="T">The success type of the handler.</typeparam>
    /// <param name="result">The handler result.</param>
    /// <param name="http">The request.</param>
    /// <param name="location">The status URI of the accepted request; null leaves the header unset.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static IResult ToAcceptedResult<T>(this ErrorOr<T> result, HttpContext http, Func<T, string?>? location = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        if (location is not null && location(result.Value) is { Length: > 0 } statusLocation)
        {
            http.Response.Headers.Location = statusLocation;
        }

        ApplyETag(http, result.Value);

        return Results.Json<T>(result.Value, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>204 without a body; sets <c>ETag</c> when the value implements <see cref="IHasETag"/>.</summary>
    /// <typeparam name="T">The success type of the handler.</typeparam>
    /// <param name="result">The handler result.</param>
    /// <param name="http">The request.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static IResult ToNoContentResult<T>(this ErrorOr<T> result, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        ApplyETag(http, result.Value);

        return Results.NoContent();
    }

    /// <summary>
    /// 204 without a body and with the <c>ETag</c> the delegate reads after the handler's transaction
    /// committed — the mutation-on-an-existing-resource shape of 09 README D15 (09.6.4-T1). As in the
    /// delegate overload of <see cref="ToHttpResult{T}(ErrorOr{T}, HttpContext, Func{T, CancellationToken, Task{string?}})"/>,
    /// the delegate is the only source and the value's own <see cref="IHasETag"/> is not emitted.
    /// </summary>
    /// <typeparam name="T">The success type of the handler.</typeparam>
    /// <param name="result">The handler result.</param>
    /// <param name="http">The request.</param>
    /// <param name="etag">Reads the current ETag after commit; a null or empty value sets no header.</param>
    /// <returns>The result the endpoint returns unchanged.</returns>
    public static async Task<IResult> ToNoContentResult<T>(
        this ErrorOr<T> result,
        HttpContext http,
        Func<T, CancellationToken, Task<string?>> etag)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(etag);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        var value = result.Value;
        SetETag(http, await etag(value, http.RequestAborted).ConfigureAwait(false));

        return Results.NoContent();
    }

    private static void ApplyETag<T>(HttpContext http, T value) => SetETag(http, ETagOf(value));

    private static void SetETag(HttpContext http, string? tag)
    {
        if (!string.IsNullOrEmpty(tag))
        {
            http.Response.Headers.ETag = tag;
        }
    }

    private static string? ETagOf<T>(T value) => value is IHasETag tagged ? tagged.ETag : null;
}
