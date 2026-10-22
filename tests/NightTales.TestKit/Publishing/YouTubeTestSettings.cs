using NightTales.Application.Publishing;

namespace NightTales.TestKit.Publishing;

/// <summary>
/// The <c>YouTube</c> configuration every test host boots with (06.2.3-T2): the active client of a test run, the
/// production-shaped secret reference of that client, the absolute callback URI and the frozen scope pair.
/// </summary>
/// <remarks>
/// <para>
/// 06.2.3-T2's Worker composition root calls <c>AddYouTubePublishing</c>, which start-up-validates the section: an
/// empty <c>ActiveClientId</c> or a relative <c>RedirectUri</c> now fails every Worker host's boot, not only the Api's.
/// Both hosts' harnesses own their configuration (the Worker's <c>appsettings.json</c> must not leak into a test), so
/// the values live here once instead of in each host helper — the same reason <c>EmailTestSettings</c> exists.
/// </para>
/// <para>
/// The secret reference is a secret-manager <em>name</em>, never a secret: nothing in these tests talks to Google. A
/// test that does (the integration suite's <c>FakeGoogleServer</c> hosts) overrides the whole section through
/// <c>FakeGoogleHosting.SettingsFor</c>, which points every endpoint at the fake and runs in Development.
/// </para>
/// </remarks>
public static class YouTubeTestSettings
{
    /// <summary>The client id a test host's new connects pin (<c>YouTube:ActiveClientId</c>).</summary>
    public const string ActiveClientId = "nts-youtube-dev";

    /// <summary>The secret-manager name of <see cref="ActiveClientId"/>'s client secret.</summary>
    public const string ActiveClientSecretRef = "nts/dev/api/YOUTUBE_CLIENT_SECRET";

    /// <summary>The absolute callback the validator accepts as the connect flow's redirect URI.</summary>
    public const string RedirectUri = "http://127.0.0.1:5000/api/v1/channels/youtube/callback";

    /// <summary>The configuration key of <c>YouTube:ActiveClientId</c>.</summary>
    public const string ActiveClientIdKey = $"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.ActiveClientId)}";

    /// <summary>The configuration key of <c>YouTube:RedirectUri</c>.</summary>
    public const string RedirectUriKey = $"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.RedirectUri)}";

    /// <summary>The configuration key of the active client's <c>SecretRef</c>.</summary>
    public const string ActiveClientSecretRefKey =
        $"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}:{ActiveClientId}:SecretRef";

    /// <summary>
    /// What a Worker host needs to boot its publishing graph: the active client and its secret reference, the absolute
    /// callback and exactly Google's upload and read-only scopes (<see cref="YouTubeScopes.Required"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> WorkerSettings { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ActiveClientIdKey] = ActiveClientId,
            [ActiveClientSecretRefKey] = ActiveClientSecretRef,
            [RedirectUriKey] = RedirectUri,
            [$"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Scopes)}:0"] = YouTubeScopes.Required[0],
            [$"{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Scopes)}:1"] = YouTubeScopes.Required[1],
        };
}
