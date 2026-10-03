using Microsoft.Extensions.Logging;

namespace NightTales.MptAdapter;

/// <summary>
/// The host's own <c>[LoggerMessage]</c> events (11.3.5-T4), ids 3100+ so they never collide with
/// <see cref="AdapterLog"/>. S7: ids, codes, counts and hashes only — never a key, a body or a script.
/// </summary>
internal static partial class HostLog
{
    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Information,
        Message = "adapter_tls_certificate sha256={Fingerprint} not_after={NotAfter:O}")]
    internal static partial void TlsCertificate(this ILogger logger, string fingerprint, DateTimeOffset notAfter);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "adapter listening on https://{Address}:{Port}")]
    internal static partial void Listening(this ILogger logger, string address, int port);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "health source {Source} failed: {Reason}")]
    internal static partial void SamplerFailed(this ILogger logger, string source, string reason);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Information, Message = "image relay is disabled: images are omitted and the node must point MPT at OpenAI")]
    internal static partial void RelayDisabled(this ILogger logger);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Warning, Message = "image relay is enabled but no relay host is registered: image_relay is reported not ok")]
    internal static partial void RelayHostMissing(this ILogger logger);
}
