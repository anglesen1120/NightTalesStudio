using Microsoft.Extensions.Configuration;
using NightTales.Application.Abstractions.Email;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The link origin every e-mail takes from configuration (09 README BD9, 09.2.6-T3): <c>App:PublicOrigin</c>,
/// the same key 09.9.4-T1 validates for the Api's CORS allow-list and same-origin filter.
/// </summary>
/// <remarks>
/// <para>
/// Reading the key here rather than injecting the Api host's options is what lets the Worker compose a
/// correct reset link: the Worker has no browser surface, but it is the host that renders the mail, so it
/// must agree with the Api about which origin the platform is served from.
/// </para>
/// <para>
/// The value is read when a link is first composed — the first send-time mint — and an absent or unparsable
/// origin fails that send: the Api refuses to start on the same key through 09.9.4-T1's <c>App</c>
/// options, and a host that never composes a link needs no origin. The comparison itself is delegated to
/// the composition (<c>GetLeftPart(UriPartial.Authority)</c>), so a trailing slash or a path cannot leak
/// into a rendered link.
/// </para>
/// </remarks>
internal sealed class ConfigurationEmailLinkOrigin : IEmailLinkOrigin
{
    /// <summary>The configuration key the origin is read from.</summary>
    public const string ConfigurationKey = "App:PublicOrigin";

    /// <summary>The fixed failure of an absent or unusable origin.</summary>
    public const string MissingOriginFailure =
        "'App:PublicOrigin' is required: every link inside an e-mail is composed from the origin the SPA is "
        + "served from (09 README BD9, 09.2.6-T3).";

    private ConfigurationEmailLinkOrigin(Uri origin) => Origin = origin;

    /// <inheritdoc />
    public Uri Origin { get; }

    /// <summary>Reads the origin, or throws while the composition root runs.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The origin link composition uses.</returns>
    /// <exception cref="InvalidOperationException">The key is absent or is not an absolute URI.</exception>
    public static ConfigurationEmailLinkOrigin From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[ConfigurationKey]?.Trim();

        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var origin))
        {
            throw new InvalidOperationException(MissingOriginFailure);
        }

        return new ConfigurationEmailLinkOrigin(origin);
    }
}
