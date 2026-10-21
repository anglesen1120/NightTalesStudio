using NightTales.Domain.Rendering;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The one producer of Worker → node HTTP clients (11 README D13/F13, S3): every call to a node's
/// <c>mpt-adapter</c> — the health poll of this story and 09.6.2-T1's <c>MptRenderClient</c> — goes through
/// it, so the base URL, the pinned certificate and the <c>x-api-key</c> header are decided in exactly one
/// place and no code path can fall back to unvalidated TLS (F26).
/// </summary>
public interface IRenderNodeConnectionFactory
{
    /// <summary>
    /// Builds a client for <paramref name="node"/>: the base URL, the <c>x-api-key</c> default header and a
    /// <see cref="SocketsHttpHandler"/> whose TLS callback accepts exactly the pinned SHA-256 (and refuses an
    /// expired certificate).
    /// </summary>
    /// <param name="node">The node row; a removed or reconfigured node is handed in on the next tick.</param>
    /// <returns>The client; the caller owns and disposes it.</returns>
    /// <exception cref="InvalidOperationException">
    /// The node is not <c>https</c>, has no pinned certificate, or its key is absent — outside
    /// <c>Development</c>, where the D14 <c>mock</c> node is a plain <c>http://</c> URL.
    /// </exception>
    HttpClient Create(WorkerNode node);
}
