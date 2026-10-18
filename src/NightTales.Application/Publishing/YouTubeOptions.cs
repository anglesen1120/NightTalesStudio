namespace NightTales.Application.Publishing;

/// <summary>
/// The <c>YouTube</c> configuration section (06 README D11; the class arrived with 06.3.1-T1 and the
/// connect/time and publication members are declared here once, so 02 and the other 06 folders read one
/// option class instead of their own key). Bound through <c>IOptionsMonitor&lt;YouTubeOptions&gt;</c>, so
/// a consumer that must see a change without a redeploy (<c>AuditStatus</c>, WT27) reads
/// <c>CurrentValue</c>.
/// </summary>
public sealed class YouTubeOptions
{
    /// <summary>The configuration section the composition root binds.</summary>
    public const string SectionName = "YouTube";

    /// <summary>The upload quota settings.</summary>
    public YouTubeQuotaOptions Quota { get; set; } = new();

    /// <summary>
    /// The client id every new connect pins on its state row (<c>YouTube:ActiveClientId</c>). The exchange
    /// later uses the client recorded on the state row, so rotating this value cannot break a connect that
    /// is already in flight (06 README D5/F19).
    /// </summary>
    public string ActiveClientId { get; set; } = string.Empty;

    /// <summary>
    /// The configured OAuth clients, keyed by client id (case-sensitive), each naming the environment
    /// secret reference of its client secret (06 README D5/D11).
    /// </summary>
    public Dictionary<string, YouTubeClientOptions> Clients { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The absolute callback URI of the connect flow; Google answers here with <c>code</c> + <c>state</c>.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// The scopes a connect requests: exactly Google's upload and read-only YouTube scopes, in this order
    /// (06 README D2). The validator refuses any other set, so a widened scope list cannot ship silently.
    /// <see langword="null"/> (not a default array) means <see cref="YouTubeScopes.Required"/>: the binder
    /// appends the configured items to a non-empty collection instance instead of replacing them, so a
    /// non-empty default would send every scope twice. Read <see cref="EffectiveScopes"/>.
    /// </summary>
    public IReadOnlyList<string>? Scopes { get; set; }

    /// <summary>The scopes a connect requests: the configured list, or the frozen pair when the key is absent.</summary>
    public IReadOnlyList<string> EffectiveScopes() => Scopes ?? YouTubeScopes.Required;

    /// <summary>The Google authorization endpoint the browser is sent to (06 README D2).</summary>
    public string AuthorizationEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>The Google token endpoint of the code exchange and the refresh (06 README D2).</summary>
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";

    /// <summary>The Google revocation endpoint 06.4.1-T1 calls (06 README D2).</summary>
    public string RevokeEndpoint { get; set; } = "https://oauth2.googleapis.com/revoke";

    /// <summary>
    /// The YouTube Data API v3 base URL the channel read and the later uploads use; the deployment points
    /// it at <c>FakeGoogleServer</c> for the integration suite (06 README D19).
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://www.googleapis.com/youtube/v3";

    /// <summary>
    /// Whether the flow carries PKCE S256 (default true, F12). The fallback to <see langword="false"/> is a
    /// configuration change, not a code edit, and it must match the staging observation recorded in
    /// <c>docs/ops/youtube-oauth-setup.md</c>.
    /// </summary>
    public bool Pkce { get; set; } = true;

    /// <summary>How long a minted state stays usable; the validator bounds it to 5..30 minutes (06 README D3).</summary>
    public int OAuthStateMinutes { get; set; } = 10;

    /// <summary>The HTTP settings of the Google calls (06.1.2-T1).</summary>
    public YouTubeHttpOptions Http { get; set; } = new();

    /// <summary>How long before <c>publishAt</c> a publication is queued (02 reads this instead of its own key, 06 CR-3).</summary>
    public int PublishLeadMinutes { get; set; } = 120;

    /// <summary>The length of the story veto window a publication waits out (02's key, 06 CR-3).</summary>
    public int MinVetoWindowHours { get; set; } = 2;

    /// <summary>
    /// Whether Google's own review of this client is complete (<c>pending|approved|rejected</c>). While it
    /// is not <c>approved</c> an upload goes out <c>private</c> and is flagged pre-audit (06 README D12).
    /// Flip it by environment after the 13.4.2-T1 audit: read it through <see cref="Quota"/>-style
    /// <c>IOptionsMonitor&lt;YouTubeOptions&gt;.CurrentValue</c>, never a cached snapshot (WT27).
    /// </summary>
    public string AuditStatus { get; set; } = YouTubeAuditStatuses.Pending;

    /// <summary>The consent-text version a publication records at consent time (06 README D11).</summary>
    public string ConsentTextVersion { get; set; } = "pc.v1";

    /// <summary>The processing-poll settings (06 README D11/D12).</summary>
    public YouTubePollOptions Poll { get; set; } = new();

    /// <summary>The go-live confirmation settings (06 README D11/D12).</summary>
    public YouTubeGoLiveOptions GoLive { get; set; } = new();
}

/// <summary>One client of the <c>YouTube:Clients</c> map (06 README D5).</summary>
public sealed class YouTubeClientOptions
{
    /// <summary>
    /// The environment secret reference of this client's secret (for example
    /// <c>nts/&lt;env&gt;/api/YOUTUBE_CLIENT_SECRET</c>), read through <c>ISecretProvider</c>: the secret
    /// itself never appears in configuration under version control.
    /// </summary>
    public string SecretRef { get; set; } = string.Empty;
}

/// <summary>The <c>YouTube:Http</c> settings (06.1.2-T1).</summary>
public sealed class YouTubeHttpOptions
{
    /// <summary>How long one Google request may take before the caller treats it as a failure.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>The <c>YouTube:Poll</c> settings (06 README D11).</summary>
public sealed class YouTubePollOptions
{
    /// <summary>Seconds between two processing polls.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>How long a publication is polled before it is failed as unconfirmed.</summary>
    public int MaxMinutes { get; set; } = 120;
}

/// <summary>The <c>YouTube:GoLive</c> settings (06 README D11).</summary>
public sealed class YouTubeGoLiveOptions
{
    /// <summary>Minutes after <c>publishAt</c> when the go-live confirmation starts.</summary>
    public int DelayMinutes { get; set; } = 5;

    /// <summary>Minutes between two go-live attempts.</summary>
    public int RetryMinutes { get; set; } = 15;

    /// <summary>How long go-live is attempted before the publication fails.</summary>
    public int MaxMinutes { get; set; } = 120;
}

/// <summary>The wire values of <c>YouTube:AuditStatus</c> (06 README D11).</summary>
public static class YouTubeAuditStatuses
{
    /// <summary>Google has not finished reviewing this client.</summary>
    public const string Pending = "pending";

    /// <summary>Google approved the client: uploads publish with the publication's own privacy.</summary>
    public const string Approved = "approved";

    /// <summary>Google rejected the client: uploads stay <c>private</c> and are flagged pre-audit.</summary>
    public const string Rejected = "rejected";

    /// <summary>Every accepted value, in the order the design lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Pending, Approved, Rejected];
}

/// <summary>The two Google scope URIs a connect requests (06 README D2).</summary>
public static class YouTubeScopes
{
    /// <summary>The scope that allows uploading a video.</summary>
    public const string Upload = "https://www.googleapis.com/auth/youtube.upload";

    /// <summary>The scope that allows reading the account's own channel and videos.</summary>
    public const string Readonly = "https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>The exact set the validator accepts, in the order the authorization URL sends it.</summary>
    public static IReadOnlyList<string> Required { get; } = [Upload, Readonly];
}

/// <summary>The <c>YouTube:Quota</c> settings.</summary>
public sealed class YouTubeQuotaOptions
{
    /// <summary>Units the platform grants per Pacific day. The day row seeds its limit from this value once.</summary>
    public int UnitsPerDay { get; set; } = 100;
}
