using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Common;
using NightTales.Domain.Rendering;

namespace NightTales.Infrastructure.Rendering;

/// <summary>
/// The one producer of Worker → node clients (11 README D13/F13, S3): the base URL, the pinned certificate
/// and the <c>x-api-key</c> header are decided here, so the health poll of 11.3.3-T1 and 09.6.2-T1's
/// <c>MptRenderClient</c> share one hardened transport and no other code path can build a node
/// <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Outside <c>Development</c> a node must be <c>https</c> and must carry a pinned
/// <c>TlsCertSha256</c>: the D14 <c>mock</c> node's plain <c>http://</c> URL is the only exception and
/// exists only under <c>Development</c> (11 README F26). A TLS callback failure is a hard failure — there is
/// deliberately no fallback to unvalidated TLS (D13, S3).
/// </para>
/// <para>
/// [RULED — README §10 T57] (D20 H12) the callback also refuses a certificate whose <c>NotAfter</c> is not
/// in the future, and records <c>render_node_cert_expiry_days{node}</c> for the presented certificate; the
/// 30-day alert rule itself lives in 11.6.2-T1.
/// </para>
/// <para>
/// Only the node's <em>name</em>, the fingerprints and the certificate's <c>NotAfter</c> ever reach a log or
/// a metric. The adapter key is read per node from the secret manager's configuration entry and lives only in
/// the handler's default header (S4).
/// </para>
/// </remarks>
/// <param name="configuration">The host configuration, which carries the secret-manager entries (env vars in P).</param>
/// <param name="environment">Decides the <c>Development</c> exception of 11 README F26.</param>
/// <param name="metrics">The node instruments of D13; the expiry gauge is recorded during the handshake.</param>
/// <param name="clock">The Worker's clock, so the expiry check and gauge are testable (09 README D3).</param>
public sealed class RenderNodeConnectionFactory(
    IConfiguration configuration,
    IHostEnvironment environment,
    NodeMetrics metrics,
    IClock clock) : IRenderNodeConnectionFactory
{
    /// <summary>The header the adapter authenticates with (11 README D11, 05 README D4).</summary>
    public const string ApiKeyHeaderName = "x-api-key";

    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public HttpClient Create(WorkerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var isDevelopment = environment.IsDevelopment();
        var isHttps = string.Equals(node.BaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

        if (!isDevelopment)
        {
            if (!isHttps)
            {
                throw new InvalidOperationException(
                    $"The render node '{node.Name}' must be reached over https; an http URL is refused outside Development (11 README F26).");
            }

            if (node.TlsCertSha256 is null)
            {
                throw new InvalidOperationException(
                    $"The render node '{node.Name}' has no pinned certificate; "
                    + "RenderNodes:Nodes[i]:TlsCertSha256 is required outside Development (11 README D13).");
            }
        }

        var handler = new SocketsHttpHandler
        {
            // A node never redirects, and following one would silently leave the pinned host (D13).
            AllowAutoRedirect = false,
            // The health poll runs every 30 s, so the pooled connection is long-lived: recycling every five
            // minutes still re-reads DNS and re-does the pinned handshake (D13, Q5).
            PooledConnectionLifetime = PooledConnectionLifetime,
            // The node network is protected by nftables, not by an HTTP proxy (11 README D11) [INFERENCE].
            UseProxy = false,
            ConnectTimeout = RenderNodesOptions.PullTimeout,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    Validate(node, certificate as X509Certificate2, isDevelopment),
            },
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = node.BaseUrl,
            // The D13 per-pull timeout; a caller may override it (09.6.2-T1's render calls are longer).
            Timeout = RenderNodesOptions.PullTimeout,
        };

        client.DefaultRequestHeaders.Add(ApiKeyHeaderName, ApiKeyOf(node));

        return client;
    }

    /// <summary>
    /// Accepts exactly the pinned certificate and refuses an expired one. A node without a pin is accepted
    /// only under <c>Development</c>, where <see cref="Create"/> has already refused everything else.
    /// </summary>
    private bool Validate(WorkerNode node, X509Certificate2? presented, bool isDevelopment)
    {
        if (presented is null)
        {
            return false;
        }

        if (node.TlsCertSha256 is null)
        {
            return isDevelopment;
        }

        var fingerprint = presented.GetCertHashString(HashAlgorithmName.SHA256);
        if (!string.Equals(fingerprint, node.TlsCertSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // D20 H12 / README §10 T57: an expired certificate is a failure, never a reason to skip validation.
        var now = clock.UtcNow;
        var notAfter = presented.NotAfter.ToUniversalTime();
        if (notAfter <= now.UtcDateTime)
        {
            return false;
        }

        metrics.RecordCertExpiryDays(node.Name, Math.Floor((notAfter - now.UtcDateTime).TotalDays));

        return true;
    }

    /// <summary>
    /// Reads the node's adapter key from the secret manager's configuration entry, naming the missing
    /// variable and never a value in the failure message (S4).
    /// </summary>
    private string ApiKeyOf(WorkerNode node)
    {
        var variable = RenderNodeApiKey.VariableName(node.Name);
        var key = configuration[variable];

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"The render node '{node.Name}' has no adapter key: the configuration value '{variable}' is required.");
        }

        return key;
    }
}
