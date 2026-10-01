using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// Composes a public catalog URL from configuration only (09.4.2-T1, plan §3): the stored
/// <c>preview_storage_key</c> is appended to <see cref="StorageOptions.PublicBaseUrl"/> after both
/// sides have been checked, so a bad key yields <see langword="null"/> instead of a redirect, a 500 or
/// a URL built from text the platform does not control.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here signs, presigns or authenticates: the public bucket is read-only for the app key and
/// needs no credential (BD10/WF16 keep signed URLs behind 09.7.2-T1), so the response never carries a
/// storage secret.
/// </para>
/// <para>
/// No log line carries the key or the composed URL (plan §8): the warnings name the event, not the
/// value, so a rejected key cannot be read back from a log.
/// </para>
/// </remarks>
/// <param name="options">The <c>Storage</c> section.</param>
/// <param name="environment">Decides whether <c>https</c> is required (Development may serve <c>http</c>).</param>
/// <param name="logger">The rejection warnings.</param>
public sealed partial class ConfigurationPublicAssetUrl(
    IOptions<StorageOptions> options,
    IHostEnvironment environment,
    ILogger<ConfigurationPublicAssetUrl> logger) : IPublicAssetUrl
{
    /// <inheritdoc />
    /// <remarks>
    /// A key is safe when it is relative (rejected whenever <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/>
    /// accepts it as an absolute URI), does not start with <c>/</c>, holds no backslash, and has no
    /// <c>..</c> segment even after percent-decoding. The composed URL must still sit under the base
    /// path, so a `<c>%2e%2e</c>` traversal that <see cref="Uri"/> normalizes away is refused. The base
    /// URL is usable when it is present, absolute and — outside Development, where the emulator may be
    /// plain <c>http</c> — <c>https</c>.
    /// </remarks>
    public Uri? Build(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return null;
        }

        if (!IsSafeKey(storageKey))
        {
            LogUnsafeKey();

            return null;
        }

        if (!TryBaseUri(out var baseUri))
        {
            return null;
        }

        // The base always ends with `/` here, so the key is appended to the base path instead of
        // replacing its last segment.
        var composed = new Uri(baseUri, storageKey);

        // System.Uri decodes percent-encoded dots and collapses `..` while composing, so a key such as
        // `%2e%2e/outside.mp3` leaves the configured base path even though it passed IsSafeKey. Both
        // paths always end with `/`, so the composed path must still start with the base path.
        if (!composed.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            LogUnsafeKey();

            return null;
        }

        return composed;
    }

    private static bool IsSafeKey(string key) =>
        !Uri.TryCreate(key, UriKind.Absolute, out _)
        && key[0] != '/'
        && !key.Contains('\\')
        && !key.Split('/').Any(IsTraversalSegment);

    // Reject a segment whose percent-decoded form is `..`, the same normalization Uri applies: the
    // raw-string compare alone lets `%2e%2e` and `.%2e` through.
    private static bool IsTraversalSegment(string segment) =>
        segment == ".." || Uri.UnescapeDataString(segment) == "..";

    private bool TryBaseUri([NotNullWhen(true)] out Uri? baseUri)
    {
        baseUri = null;

        var configured = options.Value.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // The common Development case: the startup warning of PublicAssetUrlWarning already said
            // it once, so a request does not repeat it.
            return false;
        }

        var normalized = configured.EndsWith('/') ? configured : configured + "/";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed))
        {
            LogUnusableBaseUrl();

            return false;
        }

        if (options.Value.RequireHttps && !environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            LogUnusableBaseUrl();

            return false;
        }

        baseUri = parsed;

        return true;
    }

    [LoggerMessage(EventId = 2400, Level = LogLevel.Warning, Message = "storage.public-asset-url.unsafe-key")]
    private partial void LogUnsafeKey();

    [LoggerMessage(EventId = 2401, Level = LogLevel.Warning, Message = "storage.public-asset-url.unusable-base-url")]
    private partial void LogUnusableBaseUrl();
}
