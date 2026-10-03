using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// <c>GET /v1/renders/{task_id}/video</c> and <c>…/subtitles</c> (05.4.1-T2 §2). A download is served only for
/// a succeeded, not yet cleaned task; the <c>Length</c> and <c>Sha256</c> come from the seam, which takes them
/// from the measured check output, so the core never re-hashes a file (D15 P2/P3) and streams it instead of
/// buffering it (Q3). The core sets <c>Content-Length</c> and <c>X-Content-SHA256</c> and nothing else: no
/// range processing, no redirects.
/// </summary>
internal static class OutputEndpoint
{
    internal static Task VideoAsync(HttpContext context) => HandleAsync(context, OutputKind.Video);

    internal static Task SubtitlesAsync(HttpContext context) => HandleAsync(context, OutputKind.Subtitles);

    private static async Task HandleAsync(HttpContext context, OutputKind kind)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (found, resolved) = await StatusEndpoint.TryResolveAsync(context).ConfigureAwait(false);
        if (!found || resolved is null)
        {
            return;
        }

        if (!string.Equals(resolved.State, RenderStates.Succeeded, StringComparison.Ordinal) || resolved.Output is null)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status409Conflict, AdapterErrorCodes.OutputNotReady)
                .ConfigureAwait(false);
            return;
        }

        if (resolved.CleanedAt is not null
            || (kind == OutputKind.Subtitles && resolved.Output.Subtitles is null))
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status404NotFound, AdapterErrorCodes.OutputAbsent)
                .ConfigureAwait(false);
            return;
        }

        var backend = context.RequestServices.GetRequiredService<IRenderBackend>();
        var opened = await backend
            .OpenOutputAsync(resolved.TaskId, kind, context.RequestAborted)
            .ConfigureAwait(false);
        if (opened.IsError)
        {
            await AdapterErrorStatus.WriteAsync(context, opened.FirstError).ConfigureAwait(false);
            return;
        }

        var file = opened.Value;
        // The core owns the seam's stream for the whole response: `await using` closes it on success, on a
        // copy failure and when the client aborts (CopyToAsync observes RequestAborted), so a download never
        // leaks a file handle (11.3.5-T1 Failure handling).
        await using var content = file.Content;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = file.ContentType;
        context.Response.ContentLength = file.Length;
        context.Response.Headers[RenderNodeHeaders.ContentSha256] = file.Sha256;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "no-store";

        await content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }
}
