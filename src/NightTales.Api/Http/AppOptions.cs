namespace NightTales.Api.Http;

/// <summary>
/// The host's own public identity (09 README BD3/BD16): the single origin the SPA is served from and the
/// value every cookie-authenticated POST's <c>Origin</c> header must equal.
/// </summary>
/// <remarks>
/// <para>
/// 10-web-ui D3/F20 fixes one origin per environment (<c>https://app.&lt;domain&gt;</c> in production,
/// <c>http://localhost:4200</c> on the daily dev path). 09.9.4-T1 owns the same value for CORS and for the
/// hub handshake; this task only reads it, through the one place that needs it here —
/// <see cref="Filters.RequireSameOriginFilter"/>.
/// </para>
/// <para>
/// It is configuration, not a secret (the value is visible in every request): an absent value would make
/// every refresh answer 403, so the route's behaviour is at least loud rather than silently permissive.
/// </para>
/// </remarks>
public sealed class AppOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "App";

    /// <summary>The one browser origin of this deployment, for example <c>https://app.nighttales.app</c>.</summary>
    public string PublicOrigin { get; set; } = string.Empty;
}
