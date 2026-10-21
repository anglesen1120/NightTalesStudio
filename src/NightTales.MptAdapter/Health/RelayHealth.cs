using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter.Health;

/// <summary>
/// The in-process source of the health <c>image_relay</c> block (D7: per request, no timer). 03.4.3-T1 owns the
/// relay and its <c>relay_*</c> codes and implements this seam over <c>ImagePlanRegistry</c> and
/// <c>TokenBucket</c>; this task only reads it.
/// </summary>
internal interface IImageRelayHealthProvider
{
    ImageRelayHealthV1 Read();
}

/// <summary>
/// <c>ImageRelay:Enabled = false</c> (emergency profile): MPT talks to OpenAI directly, there is no relay to be
/// unhealthy, so the block is a neutral <c>ok</c> with nothing in it.
/// </summary>
internal sealed class DisabledImageRelayHealth : IImageRelayHealthProvider
{
    public ImageRelayHealthV1 Read() => new(true, 0, 0);
}

/// <summary>
/// The relay is enabled but 03.4.3-T1's host is not part of this build: fail closed (<c>image_relay_not_ok</c>,
/// HTTP 503, no dispatch) instead of reporting a relay that is not listening.
/// </summary>
internal sealed class MissingImageRelayHealth : IImageRelayHealthProvider
{
    public ImageRelayHealthV1 Read() => new(false, 0, 0);
}
