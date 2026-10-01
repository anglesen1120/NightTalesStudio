using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Applies the per-endpoint Kestrel body cap of 05.4.1-T2 §1 (05 D15 S2, 11 README E7): an endpoint that
/// accepts a body (marked with <see cref="RequestBodyMetadata"/>) gets
/// <c>IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = MaxBodyBytes</c>, so Kestrel refuses an oversized
/// upload while it is being read instead of after it is buffered. The handlers check the same limit again on
/// their own read, because the feature is unavailable when a host reads the body before routing.
/// </summary>
public sealed class BodySizeLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<RenderNodeServerOptions> _options;

    public BodySizeLimitMiddleware(RequestDelegate next, IOptions<RenderNodeServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _options = options;
    }

    /// <summary>Sets the body cap for body-accepting endpoints, then continues.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.GetEndpoint()?.Metadata.GetMetadata<RequestBodyMetadata>() is not null
            && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = _options.Value.MaxBodyBytes;
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Marks an endpoint as accepting a request body, which is what <see cref="BodySizeLimitMiddleware"/> turns
/// into the configured cap. Only <c>POST /v1/renders</c> and <c>POST /v1/probe-image</c> carry one; every
/// other route takes no body (§2).
/// </summary>
public sealed record RequestBodyMetadata;

/// <summary>
/// Reads a request body with a hard cap (05 D15 S2): returns null as soon as more than <paramref name="maxBytes"/>
/// have arrived, so the caller can answer <c>413 adapter_body_too_large</c> without ever buffering the rest.
/// </summary>
/// <remarks>
/// The bytes are read into a pooled buffer whose initial size comes from a valid <c>Content-Length</c>, so a
/// declared body is copied exactly once and never through a <see cref="MemoryStream"/> plus a shrinking copy.
/// An undeclared (chunked) body grows by doubling, still bounded by <paramref name="maxBytes"/>. The caller
/// owns the returned buffer and MUST dispose it.
/// </remarks>
internal static class RequestBodyReader
{
    private const int ChunkBytes = 64 * 1024;

    internal static async Task<RequestBodyBytes?> ReadAsync(
        HttpRequest request,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var declared = request.ContentLength ?? 0;
        if (declared > maxBytes)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(declared > 0 ? (int)Math.Min(declared, maxBytes) : ChunkBytes);
        var owned = true;
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            var total = 0;
            while (true)
            {
                var read = await request.Body.ReadAsync(chunk.AsMemory(0, ChunkBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (total + read > maxBytes)
                {
                    return null;
                }

                if (total + read > buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)(total + read) * 2, maxBytes));
                    buffer.AsSpan(0, total).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                chunk.AsSpan(0, read).CopyTo(buffer.AsSpan(total));
                total += read;
            }

            var body = new RequestBodyBytes(buffer, total);
            owned = false;
            return body;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            if (owned)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}

/// <summary>
/// A request body read into a pooled buffer: <see cref="Memory"/> is the exact body, and <see cref="Dispose"/>
/// returns the buffer to the pool. Owning the buffer is what removes the reader's final <c>ToArray</c> copy.
/// </summary>
internal sealed class RequestBodyBytes(byte[] buffer, int length) : IDisposable
{
    internal ReadOnlyMemory<byte> Memory { get; } = buffer.AsMemory(0, length);

    /// <inheritdoc />
    public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);
}
