using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// Owns the <see cref="IAmazonS3"/> clients of a host (09.7.2 plan §5): one for the endpoint the
/// platform's own processes call, and — when the browser-reachable endpoint differs — a second one used
/// only to presign, because a SigV4 signature covers the <c>host</c> header (10 CR-21).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The v4 trap this factory exists for.</strong> The 4.x <c>ClientConfig</c> keeps
/// <c>RegionEndpoint</c> and <c>ServiceURL</c> mutually exclusive: assigning either one clears the
/// other. The task file's object initializer sets <c>RegionEndpoint</c> after <c>ServiceURL</c>, which
/// silently discards the SeaweedFS endpoint and sends every request to <c>s3.&lt;region&gt;.amazonaws.com</c>
/// — verified in the step-1 spike with a loopback listener. <see cref="Configure"/> therefore sets
/// <c>ServiceURL</c> last and carries the region through <c>AuthenticationRegion</c>, which is what the
/// SigV4 credential scope reads.
/// </para>
/// <para>
/// The clients are long-lived and thread-safe (the SDK's own rule); disposing this factory disposes
/// both, and the container owns the factory as a singleton.
/// </para>
/// </remarks>
public sealed class S3ClientFactory : IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client? _presignClient;

    /// <summary>Builds both clients from the bound <c>Storage:*</c> settings.</summary>
    /// <param name="options">The storage settings.</param>
    /// <exception cref="ArgumentException">The service URL is blank.</exception>
    public S3ClientFactory(IOptions<StorageSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;

        _client = Create(settings.ServiceUrl, settings);

        _presignClient = string.Equals(
            settings.PresignServiceUrl,
            settings.ServiceUrl,
            StringComparison.Ordinal)
            ? null
            : Create(settings.PresignServiceUrl, settings);

        PresignProtocol = settings.PresignServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTPS
            : Protocol.HTTP;
    }

    /// <summary>The client of the endpoint the platform's own processes call.</summary>
    public IAmazonS3 Client => _client;

    /// <summary>The client a read URL is signed with — the browser-reachable endpoint.</summary>
    public IAmazonS3 PresignClient => _presignClient ?? _client;

    /// <summary>
    /// The scheme a presigned URL carries. It follows the presign endpoint rather than the task file's
    /// hard-coded <c>Protocol.HTTPS</c>, so the dev emulator's <c>http://localhost:8333</c> URL is
    /// actually callable (verified in the spike).
    /// </summary>
    public Protocol PresignProtocol { get; }

    /// <summary>Creates one client for <paramref name="serviceUrl"/>.</summary>
    /// <param name="serviceUrl">The endpoint to talk to.</param>
    /// <param name="settings">The settings carrying the credentials and the region.</param>
    /// <returns>The client; the caller disposes it.</returns>
    public static AmazonS3Client Create(string serviceUrl, StorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            Configure(serviceUrl, settings));
    }

    /// <summary>Builds the client configuration for one endpoint.</summary>
    /// <param name="serviceUrl">The endpoint to talk to.</param>
    /// <param name="settings">The settings carrying the region and the path-style flag.</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="ArgumentException"><paramref name="serviceUrl"/> is blank.</exception>
    public static AmazonS3Config Configure(string serviceUrl, StorageSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUrl);
        ArgumentNullException.ThrowIfNull(settings);

        // ServiceURL is assigned last on purpose: assigning RegionEndpoint clears it (see the type docs).
        return new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),

            // The SigV4 credential scope's region. RegionEndpoint cannot be used beside ServiceURL.
            AuthenticationRegion = settings.Region,
            ServiceURL = serviceUrl,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _presignClient?.Dispose();
        _client.Dispose();
    }
}
