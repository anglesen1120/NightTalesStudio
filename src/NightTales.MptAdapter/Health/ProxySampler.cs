using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter.Health;

/// <summary>The proxy's <c>proxy-health.v1</c> body (verbatim) and what <see cref="ProxyHealthView"/> made of it.</summary>
internal sealed record ProxyProbe(JsonElement Body, bool Ok);

/// <summary>
/// D7's proxy source: <c>GET Adapter:ProxyHealthUrl</c> (2 s) every 15 s. The body is kept byte-for-byte as a
/// <see cref="JsonElement"/> (04 D8) and judged by <see cref="ProxyHealthView.From"/>. The proxy answers 503 with the
/// same shape when it is not ok, so the body is read whatever the status. A body that is not JSON, or an
/// unreachable proxy, is a failed sample whose previous good body is never returned.
/// </summary>
internal sealed class ProxySampler(
    HttpClient http,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<ProxySampler> log) : IHealthSampler
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    internal SourceCache<ProxyProbe> Cache { get; } =
        new(SamplerCadence.Fixed, clock);

    public string Name => "proxy";

    public TimeSpan? Interval => Cache.Interval;

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(RequestTimeout);

            using var response = await http
                .GetAsync(options.Value.ProxyHealthUrl, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            var text = await ReadCappedAsync(response.Content, limit.Token).ConfigureAwait(false);

            using var document = JsonDocument.Parse(text);
            var body = document.RootElement.Clone();
            Cache.Set(new ProxyProbe(body, ProxyHealthView.From(body).Ok));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            Cache.Set(new ProxyProbe(EmptyObject, false));
        }
    }

    /// <summary>SEC-6: a proxy health body is a few hundred bytes; anything over this is a failed sample.</summary>
    internal const int MaxBodyBytes = 64 * 1024;

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBodyBytes)
        {
            throw new InvalidDataException("The proxy body exceeded its cap.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new InvalidDataException("The proxy body exceeded its cap.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
