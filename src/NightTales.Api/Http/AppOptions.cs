using System.Diagnostics.CodeAnalysis;

namespace NightTales.Api.Http;

/// <summary>
/// The host's own public identity (09 README BD3/BD16): the single origin the SPA is served from, the
/// value every cookie-authenticated POST's <c>Origin</c> header must equal, and the one origin the CORS
/// allow-list accepts (09.9.4-T1, BD12).
/// </summary>
/// <remarks>
/// <para>
/// 10-web-ui D3/F20 fixes one origin per environment (<c>https://app.&lt;domain&gt;</c> in production,
/// <c>http://localhost:4200</c> on the daily dev path). 09.9.4-T1 owns the value: it validates it on
/// start-up, builds both CORS policies from it, and
/// <see cref="Filters.RequireSameOriginFilter"/> compares the <c>Origin</c> header against it.
/// </para>
/// <para>
/// It is configuration, not a secret (the value is visible in every request): an absent value would make
/// every refresh answer 403 and every browser call fail its preflight, so the host refuses to start
/// rather than serving traffic with a broken or permissive CORS posture.
/// </para>
/// </remarks>
public sealed class AppOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "App";

    /// <summary>
    /// The failure reported when <c>App:PublicOrigin</c> is absent or malformed. It names the
    /// configuration key and the rule, and never echoes the value.
    /// </summary>
    public const string InvalidOriginFailure =
        "'App:PublicOrigin' is required and must be an absolute https origin without a trailing slash and "
        + "without a path, query or fragment, for example https://app.nighttales.app; in Development an "
        + "http loopback origin (http://localhost:<port>) is also accepted (09 README BD3, 10 CR-3).";

    /// <summary>The one browser origin of this deployment, for example <c>https://app.nighttales.app</c>.</summary>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>
    /// Normalizes <paramref name="origin"/> into the value a browser sends in an <c>Origin</c> header,
    /// or reports that it is not a usable origin at all.
    /// </summary>
    /// <remarks>
    /// The rules are BD12's: absolute, <c>https</c>, no trailing slash and no path/query/fragment. In
    /// Development an <c>http</c> loopback origin is accepted too — that is the daily dev path (10 CR-3),
    /// and it is kept to loopback so "Development" can never mean "plain http to anywhere". The
    /// normalized form is scheme plus authority (lowercased host, default port dropped), which is exactly
    /// what CORS compares and what <see cref="Filters.RequireSameOriginFilter"/> compares by parsing.
    /// </remarks>
    /// <param name="origin">The configured value.</param>
    /// <param name="allowInsecureDevelopmentOrigin">Whether an <c>http://localhost:&lt;port&gt;</c> origin is acceptable.</param>
    /// <param name="normalized">The origin a browser would send, when the answer is <see langword="true"/>.</param>
    public static bool TryNormalizeOrigin(
        string? origin,
        bool allowInsecureDevelopmentOrigin,
        [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;

        var candidate = origin?.Trim();

        // A trailing slash is BD12's explicit misconfiguration: `https://app.nighttales.app/` never
        // equals the origin a browser sends, so the CORS allow-list would silently refuse every call.
        if (string.IsNullOrEmpty(candidate)
            || candidate.EndsWith('/')
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isDevelopmentHttp = allowInsecureDevelopmentOrigin
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;

        if (!isHttps && !isDevelopmentHttp)
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);

        return true;
    }
}
