using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace NightTales.Api.Http;

/// <summary>
/// Reads a request body under an endpoint's own size cap and answers "too large" instead of buffering it
/// (09 README BD12, 02.8.3-T2 task §186d).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the binder.</b> The cap has to be enforced twice, and the endpoint owns the second half: the request-limit
/// middleware refuses a declared <c>Content-Length</c> over the cap before any byte is read, and this reader refuses a
/// streamed (chunked) body that grows past it — the same two mechanisms <see cref="RequestLimitsOptions"/> documents
/// for the host-wide cap, applied to one route's smaller cap. A binder would hand the whole body to the JSON
/// deserializer first, so a client that lied about its length could still allocate it.
/// </para>
/// <para>
/// <b>What it does not do.</b> It neither parses nor validates: the caller owns the document's shape, so an empty body
/// and an unparseable body are two different answers that the endpoint's own error mapping decides.
/// </para>
/// </remarks>
public static class BoundedBodyReader
{
    /// <summary>
    /// Reads the request body, failing instead of exceeding <paramref name="limit"/>.
    /// </summary>
    /// <param name="http">The request whose body is read.</param>
    /// <param name="limit">The largest accepted body, in bytes.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// The bytes read (empty for a request without a body), or <see langword="null"/> when the body is larger than
    /// <paramref name="limit"/> — the caller answers 413 <c>payload_too_large</c>.
    /// </returns>
    public static async Task<ReadOnlyMemory<byte>?> ReadAsync(HttpContext http, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The body cap must be positive.");
        }

        ct.ThrowIfCancellationRequested();

        if (http.Request.ContentLength is { } declared && declared > limit)
        {
            return null;
        }

        if (http.Request.ContentLength == 0
            || http.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        var buffer = new byte[Math.Min(4 * 1_024, http.Request.ContentLength is { } length ? (int)length + 1 : limit + 1)];
        var total = 0;

        try
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, limit + 1L));
                }

                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.AsMemory(0, total);
                }

                total += read;
                if (total > limit)
                {
                    return null;
                }
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // The server's own cap fired while the body was still arriving (the Kestrel limit set from the endpoint
            // metadata): same answer as a body this reader measured itself.
            return null;
        }
    }
}
