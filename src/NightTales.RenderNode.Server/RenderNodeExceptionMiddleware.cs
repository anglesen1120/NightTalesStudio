using Microsoft.AspNetCore.Http;
using NightTales.RenderNode.Contract;

namespace NightTales.RenderNode.Server;

/// <summary>
/// The outermost middleware of the core: it turns every fault into the §1 envelope, so no caller ever sees an
/// HTML error page or a bare status. In particular the Kestrel body-limit fault becomes
/// <c>413 adapter_body_too_large</c> (05 D15 S2) and an unexpected fault becomes a 500 with a message that
/// carries no node internals (D15 S4).
/// </summary>
public sealed class RenderNodeExceptionMiddleware
{
    private const int BodyTooLarge = StatusCodes.Status413PayloadTooLarge;

    private readonly RequestDelegate _next;

    public RenderNodeExceptionMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Runs the pipeline, mapping its faults onto envelopes.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == BodyTooLarge)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(context, BodyTooLarge, AdapterErrorCodes.BodyTooLarge)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode < StatusCodes.Status500InternalServerError)
        {
            await RenderNodeEnvelope
                .WriteErrorAsync(
                    context,
                    exception.StatusCode,
                    AdapterErrorCodes.ContractViolation,
                    [new AdapterFieldErrorV1(AdapterFieldNames.Body, SubmitRules.RequestInvalid)])
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed with a code and a fixed sentence; the fault itself stays on the node's own log
            // (the host logs it), never in the response and never as upstream text (§1, D15 S4).
            await RenderNodeEnvelope
                .WriteErrorAsync(context, StatusCodes.Status500InternalServerError, AdapterErrorCodes.InternalError)
                .ConfigureAwait(false);
        }
    }
}
