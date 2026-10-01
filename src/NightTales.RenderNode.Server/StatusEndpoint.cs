using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// <c>GET /v1/renders/{task_id}</c> (05.4.1-T2 §2): the UUID guard first — a non-UUID id is
/// <c>404 adapter_task_unknown</c> <em>without</em> touching the store (§1) — then the stored row, then the
/// status projection.
/// </summary>
internal static class StatusEndpoint
{
    internal static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var store = context.RequestServices.GetRequiredService<ITaskStore>();
        var rawTaskId = context.Request.RouteValues[RenderNodeRoutes.TaskIdParameter] as string;
        if (!TaskIdGuard.TryParse(rawTaskId, out var taskId))
        {
            await WriteUnknownAsync(context).ConfigureAwait(false);
            return;
        }

        var record = await store.TryGetAsync(taskId, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            await WriteUnknownAsync(context).ConfigureAwait(false);
            return;
        }

        await RenderNodeEnvelope
            .WriteJsonAsync(context, StatusCodes.Status200OK, RenderStatusProjection.From(record))
            .ConfigureAwait(false);
    }

    /// <summary>The shared answer of every route that needs a stored task (also used by the output and lifecycle routes).</summary>
    internal static Task WriteUnknownAsync(HttpContext context) =>
        RenderNodeEnvelope.WriteErrorAsync(context, StatusCodes.Status404NotFound, AdapterErrorCodes.TaskUnknown);

    /// <summary>
    /// Resolves the task of the current route: false means the answer has already been written (unknown or
    /// non-UUID id), which keeps every task-scoped route on one guard.
    /// </summary>
    internal static async Task<(bool Found, RenderTaskRecord? Record)> TryResolveAsync(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<ITaskStore>();
        var rawTaskId = context.Request.RouteValues[RenderNodeRoutes.TaskIdParameter] as string;
        if (!TaskIdGuard.TryParse(rawTaskId, out var taskId))
        {
            await WriteUnknownAsync(context).ConfigureAwait(false);
            return (false, null);
        }

        var record = await store.TryGetAsync(taskId, context.RequestAborted).ConfigureAwait(false);
        if (record is null)
        {
            await WriteUnknownAsync(context).ConfigureAwait(false);
            return (false, null);
        }

        return (true, record);
    }
}
