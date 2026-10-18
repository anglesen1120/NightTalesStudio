using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Features.Publishing.Channels;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Api;
using NightTales.TestKit.Email;

namespace NightTales.TestKit.Google;

/// <summary>
/// Points the connect tests' host at <see cref="FakeGoogleServer"/> and registers the two hand-written
/// stand-ins a real deployment gets from 06.1.2-T2 and the e-mail pipeline
/// (<see cref="FakeConnectChannelConnection"/>, <see cref="FakeEmailSender"/>). The test host only:
/// nothing under <c>src/</c> knows these types.
/// </summary>
/// <remarks>
/// Everything is configuration or a test-only service, i.e. the same seams README D19 names: the
/// <c>YouTube</c> options carry the four endpoints, the client ids and the PKCE switch, and the two fakes
/// replace ports the host does not implement yet (<c>IConnectChannelConnection</c>, D-2 of the task) or
/// whose real path would reach a template that ships later.
/// </remarks>
public static class FakeGoogleHosting
{
    /// <summary>The upload scope the connect URL must ask for (06 README D2).</summary>
    public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";

    /// <summary>The read-only scope the connect URL must ask for (06 README D2).</summary>
    public const string ReadonlyScope = "https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>The redirect URI of a test host: the public origin plus the callback route.</summary>
    public const string RedirectUri = ApiFactory.PublicOrigin + "/api/v1/channels/youtube/callback";

    /// <summary>The <c>SecretRef</c> of the primary client; its last segment is the configuration key.</summary>
    public const string PrimarySecretRef = "nts/test/oauth/YOUTUBE_CLIENT_SECRET_PRIMARY";

    /// <summary>The <c>SecretRef</c> of the second client.</summary>
    public const string SecondarySecretRef = "nts/test/oauth/YOUTUBE_CLIENT_SECRET_SECONDARY";

    /// <summary>
    /// The configuration key <see cref="PrimarySecretRef"/> resolves to: the reference's <em>last</em> path
    /// segment, which is the only key <c>ConfigurationSecretProvider</c> reads (the whole reference is never
    /// read as a key, so a test host must carry the secret under the segment — the way a deployment carries
    /// it as the environment variable of the same name).
    /// </summary>
    public const string PrimarySecretKey = "YOUTUBE_CLIENT_SECRET_PRIMARY";

    /// <summary>The configuration key <see cref="SecondarySecretRef"/> resolves to.</summary>
    public const string SecondarySecretKey = "YOUTUBE_CLIENT_SECRET_SECONDARY";

    /// <summary>
    /// The <c>YouTube</c> configuration that points a host at <paramref name="server"/>: the four endpoints, both
    /// client identities with their secrets under the configuration keys their references resolve to, the scopes,
    /// the redirect URI, the PKCE switch, and — added by 06.2.3-T1 — the resumable-upload root and the chunk size.
    /// </summary>
    /// <remarks>
    /// One dictionary for every composition: the connect harness configures a <see cref="ApiFactory"/> with it and
    /// the upload tests configure a bare host, so an endpoint this product adds is wired in exactly one place.
    /// </remarks>
    /// <param name="server">The started fake; every endpoint comes from its bound address.</param>
    /// <param name="chunkSizeBytes">The <c>YouTube:Upload:ChunkSizeBytes</c> to bind.</param>
    public static IReadOnlyDictionary<string, string?> SettingsFor(
        FakeGoogleServer server,
        int chunkSizeBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Options;

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["YouTube:ActiveClientId"] = options.ActiveClientId,
            [$"YouTube:Clients:{options.ActiveClientId}:SecretRef"] = PrimarySecretRef,
            [PrimarySecretKey] = options.ActiveClientSecret,
            [$"YouTube:Clients:{options.SecondClientId}:SecretRef"] = SecondarySecretRef,
            [SecondarySecretKey] = options.SecondClientSecret,
            ["YouTube:AuthorizationEndpoint"] = server.AuthorizationEndpoint.ToString(),
            ["YouTube:TokenEndpoint"] = server.TokenEndpoint.ToString(),
            ["YouTube:RevokeEndpoint"] = server.RevokeEndpoint.ToString(),
            ["YouTube:ApiBaseUrl"] = server.ApiBaseUrl.ToString(),
            ["YouTube:UploadBaseUrl"] = server.UploadBaseUrl.ToString(),
            ["YouTube:Upload:ChunkSizeBytes"] = chunkSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["YouTube:RedirectUri"] = RedirectUri,
            ["YouTube:Pkce"] = "true",
            ["YouTube:OAuthStateMinutes"] = "10",
            ["YouTube:Http:TimeoutSeconds"] = "15",
            ["YouTube:Scopes:0"] = UploadScope,
            ["YouTube:Scopes:1"] = ReadonlyScope,
        };
    }

    /// <summary>
    /// Points every <c>YouTube</c> endpoint at <paramref name="server"/>, registers both client identities
    /// through their <c>SecretRef</c> and carries each secret under the configuration key that reference
    /// resolves to (the configuration secret provider's last-segment rule), and runs the host in
    /// Development so the options validator accepts the fake's plain-http loopback addresses.
    /// </summary>
    /// <param name="factory">The factory to configure.</param>
    /// <param name="server">The started fake.</param>
    /// <param name="activeClientId">The active client id; the primary client when null.</param>
    /// <param name="chunkSizeBytes">
    /// The <c>YouTube:Upload:ChunkSizeBytes</c> the host runs with. The default is 1 MiB rather than the
    /// production 10 MiB (G8), so a modest test payload spans several chunks and the resumable protocol is
    /// actually exercised; a test that needs another size passes it here.
    /// </param>
    public static ApiFactory WithFakeGoogle(
        this ApiFactory factory,
        FakeGoogleServer server,
        string? activeClientId = null,
        int chunkSizeBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(server);

        factory.WithEnvironment(Environments.Development);

        foreach (var (key, value) in SettingsFor(server, chunkSizeBytes))
        {
            factory.WithSetting(key, value);
        }

        if (activeClientId is not null)
        {
            factory.WithSetting("YouTube:ActiveClientId", activeClientId);
        }

        return factory;
    }

    /// <summary>
    /// Registers <paramref name="state"/>'s <see cref="FakeConnectChannelConnection"/> as the host's
    /// <see cref="IConnectChannelConnection"/>, sharing the log and the switches across scopes.
    /// </summary>
    /// <param name="factory">The factory to configure.</param>
    /// <param name="state">The test's switchboard and call log.</param>
    public static ApiFactory WithFakeConnectChannel(this ApiFactory factory, FakeConnectChannelState state)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(state);

        return factory.WithServices(services =>
        {
            services.RemoveAll<IConnectChannelConnection>();
            services.AddScoped<IConnectChannelConnection>(
                provider => new FakeConnectChannelConnection(
                    provider.GetRequiredService<IEmailSender>(),
                    provider.GetRequiredService<AppDbContext>(),
                    provider.GetRequiredService<IClock>(),
                    state));
        });
    }

    /// <summary>
    /// Replaces <see cref="IEmailSender"/> with <paramref name="state"/>'s <see cref="FakeEmailSender"/>:
    /// a row inside the caller's unit of work, no template-catalog dependency, and a switchable failure.
    /// </summary>
    /// <param name="factory">The factory to configure.</param>
    /// <param name="state">The test's switchboard and call log.</param>
    public static ApiFactory WithFakeEmailSender(this ApiFactory factory, FakeEmailSenderState state)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(state);

        return factory.WithServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddScoped<IEmailSender>(
                provider => new FakeEmailSender(
                    state,
                    provider.GetRequiredService<AppDbContext>(),
                    provider.GetRequiredService<IClock>()));
        });
    }
}
