using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// Reads the cached health body once per request that needs it (D19 Q1: never computed per request) and turns a
/// missing or throwing source into "unavailable", which fails closed (11.3.5-T1 Failure handling).
/// </summary>
internal enum NodeHealthState
{
    /// <summary>The source answered <c>status = "ok"</c>.</summary>
    Ok,

    /// <summary>The source answered a body whose status is not <c>ok</c>.</summary>
    NotOk,

    /// <summary>No source is registered, or the source threw.</summary>
    Unavailable,
}

internal static class NodeHealth
{
    internal static async Task<(NodeHealthState State, RenderNodeHealthV1? Body)> ReadAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var source = services.GetService<INodeHealthSource>();
        if (source is null)
        {
            return (NodeHealthState.Unavailable, null);
        }

        try
        {
            var health = await source.GetAsync(cancellationToken).ConfigureAwait(false);
            var ok = string.Equals(health.Status, RenderNodeHealthStatuses.Ok, StringComparison.Ordinal);
            return (ok ? NodeHealthState.Ok : NodeHealthState.NotOk, health);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (NodeHealthState.Unavailable, null);
        }
    }
}
