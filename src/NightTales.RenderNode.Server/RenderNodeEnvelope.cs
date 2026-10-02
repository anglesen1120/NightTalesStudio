using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The single writer of the §1 error envelope and of every JSON body the core returns, so no endpoint can
/// invent a different shape. Bodies are serialized with <see cref="RenderNodeJson.Strict"/> — the same member
/// names the fixtures pin.
/// </summary>
internal static class RenderNodeEnvelope
{
    /// <summary>Writes the envelope for an error code, with the code's fixed message and an optional retry delay.</summary>
    internal static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        IReadOnlyList<AdapterFieldErrorV1>? errors = null,
        int? retryAfterSeconds = null) =>
        WriteJsonAsync(
            context,
            statusCode,
            new AdapterErrorV1(new AdapterErrorBody(code, AdapterErrorMessages.For(code), errors)),
            retryAfterSeconds);

    /// <summary>Writes a JSON body with the given status and optional <c>Retry-After</c>.</summary>
    internal static async Task WriteJsonAsync<TBody>(
        HttpContext context,
        int statusCode,
        TBody body,
        int? retryAfterSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
        {
            // The handler already started streaming (an output download): the status is committed and the
            // only safe thing left is to stop writing.
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = RenderNodeHeaders.JsonContentType;
        if (retryAfterSeconds is not null)
        {
            context.Response.Headers[RenderNodeHeaders.RetryAfter] =
                retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        await JsonSerializer
            .SerializeAsync(context.Response.Body, body, RenderNodeJson.Strict, context.RequestAborted)
            .ConfigureAwait(false);
    }
}
