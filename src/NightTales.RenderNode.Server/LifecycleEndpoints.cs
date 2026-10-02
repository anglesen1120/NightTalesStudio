using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// <c>POST /v1/renders/{task_id}/cancel</c> and <c>DELETE /v1/renders/{task_id}</c> (05.4.1-T2 §2).
/// </summary>
/// <remarks>
/// Cancel is idempotent: the first call marks <c>cancel_requested</c>, releases the image plan and tells the
/// backend, a repeat only re-reads the row, and a terminal task is a 200 no-op — so a plan is released exactly
/// once (F08, C11). Delete marks <c>cleaned_at</c> and keeps the row for the 7-day purge window, so a re-delete
/// is a 204 that touches neither the backend nor the plan.
/// </remarks>
internal static class LifecycleEndpoints
{
    internal static async Task CancelAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.RequestServices;
        var (found, resolved) = await StatusEndpoint.TryResolveAsync(context).ConfigureAwait(false);
        if (!found || resolved is null)
        {
            return;
        }

        if (resolved.IsTerminal)
        {
            await RenderNodeEnvelope
                .WriteJsonAsync(context, StatusCodes.Status200OK, RenderStatusProjection.From(resolved))
                .ConfigureAwait(false);
            return;
        }

        var cancelled = resolved with { CancelRequested = true };
        if (!resolved.CancelRequested)
        {
            var store = services.GetRequiredService<ITaskStore>();
            await TaskUpdates.UpdateAsync(store, cancelled, context.RequestAborted).ConfigureAwait(false);
            services.GetService<IImagePlanSink>()?.Release(resolved.TaskId);

            var backend = services.GetRequiredService<IRenderBackend>();
            await backend.CancelAsync(cancelled, context.RequestAborted).ConfigureAwait(false);
        }

        await RenderNodeEnvelope
            .WriteJsonAsync(context, StatusCodes.Status202Accepted, RenderStatusProjection.From(cancelled))
            .ConfigureAwait(false);
    }

    internal static async Task DeleteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.RequestServices;
        var (found, resolved) = await StatusEndpoint.TryResolveAsync(context).ConfigureAwait(false);
        if (!found || resolved is null)
        {
            return;
        }

        if (resolved.CleanedAt is not null)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var backend = services.GetRequiredService<IRenderBackend>();
        var deleted = await backend.DeleteAsync(resolved, context.RequestAborted).ConfigureAwait(false);
        if (deleted.IsError)
        {
            await AdapterErrorStatus.WriteAsync(context, deleted.FirstError).ConfigureAwait(false);
            return;
        }

        var store = services.GetRequiredService<ITaskStore>();
        var cleaned = resolved with { CleanedAt = SubmitEndpoint.Clock(services).GetUtcNow() };
        await TaskUpdates.UpdateAsync(store, cleaned, context.RequestAborted).ConfigureAwait(false);
        services.GetService<IImagePlanSink>()?.Release(resolved.TaskId);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
