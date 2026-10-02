using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// <c>GET /v1/health</c> and <c>POST /v1/probe-image</c> (05.4.1-T2 §2) — the two routes that are not about one
/// stored task.
/// </summary>
/// <remarks>
/// Health serves the cached body with 200 (<c>status = "ok"</c>) or 503 (any other status) and fails closed to a
/// <c>503 adapter_node_unhealthy</c> envelope when the source is missing or throws, so a broken health source
/// can never look healthy. The probe route answers from the <see cref="IImageProbeBackend"/> seam of 12 CR-19 and
/// answers <c>503 adapter_probe_unavailable</c> until a host registers one — 12.8.3-T3 treats that as
/// fail-closed, never as a pass.
/// </remarks>
internal static class HealthEndpoint
{
    internal static async Task HealthAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (state, body) = await NodeHealth
            .ReadAsync(context.RequestServices, context.RequestAborted)
            .ConfigureAwait(false);

        switch (state)
        {
            case NodeHealthState.Ok when body is not null:
                await RenderNodeEnvelope
                    .WriteJsonAsync(context, StatusCodes.Status200OK, body)
                    .ConfigureAwait(false);
                break;

            case NodeHealthState.NotOk when body is not null:
                await RenderNodeEnvelope
                    .WriteJsonAsync(context, StatusCodes.Status503ServiceUnavailable, body)
                    .ConfigureAwait(false);
                break;

            default:
                await RenderNodeEnvelope
                    .WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, AdapterErrorCodes.NodeUnhealthy)
                    .ConfigureAwait(false);
                break;
        }
    }

    internal static async Task ProbeImageAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var probe = context.RequestServices.GetService<IImageProbeBackend>();
        if (probe is null)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, AdapterErrorCodes.ProbeUnavailable)
                .ConfigureAwait(false);
            return;
        }

        var result = await probe.ProbeAsync(context.RequestAborted).ConfigureAwait(false);
        if (result.IsError)
        {
            await AdapterErrorStatus.WriteAsync(context, result.FirstError).ConfigureAwait(false);
            return;
        }

        await RenderNodeEnvelope
            .WriteJsonAsync(context, StatusCodes.Status200OK, result.Value)
            .ConfigureAwait(false);
    }
}
