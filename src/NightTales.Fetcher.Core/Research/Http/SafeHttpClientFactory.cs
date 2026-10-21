using System.Net;

namespace NightTales.Fetcher.Core.Research.Http;

/// <summary>
/// Builds the one <see cref="HttpClient"/> the source fetcher and the robots policy share (02 D11, 02.5.2-T1 §HttpClient).
/// Every handler setting is a security control and is asserted by <c>HttpClientFactoryTests</c>. This factory is the
/// only producer of that client, so it is on the architecture allow-list of <c>new HttpClient</c> call sites.
/// </summary>
public static class SafeHttpClientFactory
{
    /// <summary>
    /// The handler: no automatic redirect (the URL is re-checked at every hop), no proxy (no environment proxy can reroute
    /// the connection), no cookies (no jar to leak or replay), no automatic decompression (we decompress to count bytes),
    /// and the connection made by <paramref name="connector"/>.
    /// </summary>
    /// <param name="connector">The connect callback that resolves, validates and connects.</param>
    public static SocketsHttpHandler CreateHandler(SafeHttpConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => connector.ConnectAsync(context, ct),
        };
    }

    /// <summary>
    /// The client over <see cref="CreateHandler"/>. <see cref="HttpClient.Timeout"/> is infinite: the 10 s budget is a
    /// <see cref="CancellationTokenSource"/> per fetch, so it covers robots, DNS and every redirect as one budget.
    /// </summary>
    /// <param name="connector">The connect callback that resolves, validates and connects.</param>
    public static HttpClient Create(SafeHttpConnector connector) =>
        new(CreateHandler(connector), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
}
