namespace NightTales.Application.Publishing;

/// <summary>
/// Checks the <c>YouTube</c> section so a missing or unusable OAuth configuration stops the start-up and
/// never a request (06 README D11: "validated at start"). Application cannot reference
/// <c>IValidateOptions</c> (Architecture test), so Infrastructure adapts this to it exactly as it does for
/// <see cref="NightTales.Application.Features.Research.SourceFinder.FindSourcesCostOptionsValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the ones a <em>connect</em> needs: a client must be named and configured, every configured client
/// must carry a well-formed secret reference, the callback URI must be an absolute URI, the scope set must be
/// exactly the two scopes the design froze, the state lifetime must stay inside 5..30 minutes (the state is the
/// callback's only authenticator, so a long one widens the window YR10 depends on), and the HTTP timeout must stay
/// inside 1..60 seconds: it is the bound on how long a stuck Google call pins the callback, and
/// <c>HttpClient.Timeout</c> throws for a value outside it when the client is first created — which happens after
/// the state was already spent, i.e. the user would get a 500 and a burned connect.
/// </para>
/// <para>
/// Every message names the configuration key and the rule and never echoes a value: a client id, a secret
/// reference and an endpoint are configuration, and a start-up failure must not put any of them in a log
/// (06 README D17). The scheme of the endpoints is deliberately not checked here: it depends on the host's
/// environment (Development may use a loopback <c>http://</c> fake), so the Infrastructure adapter owns
/// that rule beside the options binding.
/// </para>
/// <para>
/// The resumable-upload rules arrive with 06.2.3-T1: <c>UploadBaseUrl</c> must be usable as the <em>root</em> of the
/// session-create URL and as the origin the session URI is validated against, so its shape (absolute http/https, no
/// user-info, no query or fragment, trailing <c>/</c>) is checked with it; and the <c>Upload</c> block's chunk size,
/// retry count and three timeouts must sit inside the bounds the client's streaming and backoff rely on. The
/// chunk-size rule is the one the resumable protocol itself imposes (a non-final chunk that is not a multiple of
/// 256 KiB is refused by Google), not a preference.
/// </para>
/// </remarks>
public static class YouTubeOptionsValidator
{
    /// <summary>The shortest and longest accepted <c>OAuthStateMinutes</c> (06 README D3/D11).</summary>
    public const int MinOAuthStateMinutes = 5;

    /// <summary>The longest accepted <c>OAuthStateMinutes</c>.</summary>
    public const int MaxOAuthStateMinutes = 30;

    /// <summary>The shortest accepted <c>Http:TimeoutSeconds</c> (06.1.2-T1).</summary>
    public const int MinHttpTimeoutSeconds = 1;

    /// <summary>
    /// The longest accepted <c>Http:TimeoutSeconds</c>: the bound on how long one stuck Google call may pin a
    /// callback (and, with it, a pooled database connection).
    /// </summary>
    public const int MaxHttpTimeoutSeconds = 60;

    /// <summary>
    /// The granularity every non-final chunk of a resumable upload must be a multiple of (G7): 256 KiB. Google
    /// refuses a smaller non-final chunk, and a chunk that is not a multiple makes the session's byte accounting
    /// disagree with the object.
    /// </summary>
    public const int UploadChunkSizeMultipleBytes = 256 * 1024;

    /// <summary>The largest accepted <c>Upload:ChunkSizeBytes</c>: 256 MiB, one rented buffer at a time.</summary>
    public const int MaxUploadChunkSizeBytes = 256 * 1024 * 1024;

    /// <summary>The shortest accepted <c>Upload:MaxChunkRetries</c>: one retry, never zero (a chunk that fails once would end the upload).</summary>
    public const int MinUploadChunkRetries = 1;

    /// <summary>
    /// The longest accepted <c>Upload:MaxChunkRetries</c>: ten re-sends of one chunk. The bound matters because every
    /// retry spends the same quota units as the first attempt and the job's own runtime budget is finite.
    /// </summary>
    public const int MaxUploadChunkRetries = 10;

    /// <summary>The shortest accepted <c>Upload:*TimeoutSeconds</c>: zero would make every call time out at once.</summary>
    public const int MinUploadTimeoutSeconds = 1;

    /// <summary>The longest accepted <c>Upload:StartTimeoutSeconds</c> and <c>Upload:ProbeTimeoutSeconds</c>.</summary>
    public const int MaxUploadHandshakeTimeoutSeconds = 600;

    /// <summary>
    /// The longest accepted <c>Upload:ChunkTimeoutSeconds</c>: one chunk carries up to a quarter of a gigabyte, so it
    /// gets a longer bound than the two handshakes.
    /// </summary>
    public const int MaxUploadChunkTimeoutSeconds = 3600;

    /// <summary>Every problem of the section; empty when valid.</summary>
    /// <param name="options">The bound <c>YouTube</c> section.</param>
    /// <returns>One message per problem.</returns>
    public static IReadOnlyList<string> Validate(YouTubeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ActiveClientId))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.ActiveClientId)}' must name the OAuth "
                + "client new connects use.");
        }
        else if (!options.Clients.ContainsKey(options.ActiveClientId))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}' must contain an entry for "
                + $"'{nameof(YouTubeOptions.ActiveClientId)}'.");
        }

        // Every entry, not only the active one: the map carries the identities a rotation moves between, and a
        // reference whose shape cannot be a secret name would otherwise be found only when that client is used.
        foreach (var client in options.Clients.Values)
        {
            var secretRef = client?.SecretRef;

            if (string.IsNullOrWhiteSpace(secretRef))
            {
                problems.Add(
                    $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}:<id>:"
                    + $"{nameof(YouTubeClientOptions.SecretRef)}' must be set for every configured client.");
            }
            else if (!IsWellFormedSecretRef(secretRef))
            {
                problems.Add(
                    $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}:<id>:"
                    + $"{nameof(YouTubeClientOptions.SecretRef)}' must be a secret-manager reference or an "
                    + "environment variable name (ASCII letters, digits, '_', '/' and '-'; never a ':' path).");
            }
        }

        // Absolute AND http/https: a rooted path such as "/api/v1/.../callback" can parse as an absolute URI on
        // Unix (as a file URI), which Google would then be handed as the redirect target.
        if (!Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out var redirectUri)
            || !IsHttpScheme(redirectUri.Scheme))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.RedirectUri)}' must be an absolute http or "
                + "https URI: Google answers the callback here and rejects an unusable one.");
        }

        if (options.Http.TimeoutSeconds < MinHttpTimeoutSeconds
            || options.Http.TimeoutSeconds > MaxHttpTimeoutSeconds)
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Http)}:"
                + $"{nameof(YouTubeHttpOptions.TimeoutSeconds)}' must be "
                + $"{MinHttpTimeoutSeconds}..{MaxHttpTimeoutSeconds}.");
        }

        if (options.OAuthStateMinutes < MinOAuthStateMinutes || options.OAuthStateMinutes > MaxOAuthStateMinutes)
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.OAuthStateMinutes)}' must be "
                + $"{MinOAuthStateMinutes}..{MaxOAuthStateMinutes}.");
        }

        if (!IsRequiredScopeSet(options.EffectiveScopes()))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Scopes)}' must be exactly the two "
                + "Google scopes '.../auth/youtube.upload' and '.../auth/youtube.readonly'.");
        }

        if (!YouTubeAuditStatuses.All.Contains(options.AuditStatus, StringComparer.Ordinal))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.AuditStatus)}' must be one of "
                + string.Join('|', YouTubeAuditStatuses.All) + ".");
        }

        // 06.2.3-T1. `UploadBaseUrl` is the only host an upload may reach (D19): the client appends
        // `videos?uploadType=resumable` to it and accepts the session URI Google answers only on its scheme, host
        // and port (SSRF), so a value that cannot carry that path — or that carries a user-info, query or fragment
        // the comparison would then have to ignore — is refused here instead of at the first upload.
        if (!IsUploadBaseUrl(options.UploadBaseUrl))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.UploadBaseUrl)}' must be an absolute http or "
                + "https URI with no user-info, query or fragment, ending in '/': it is the root the resumable "
                + "session is created under and the only origin an upload may reach.");
        }

        if (options.Upload.ChunkSizeBytes < UploadChunkSizeMultipleBytes
            || options.Upload.ChunkSizeBytes % UploadChunkSizeMultipleBytes != 0
            || options.Upload.ChunkSizeBytes > MaxUploadChunkSizeBytes)
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Upload)}:"
                + $"{nameof(YouTubeUploadOptions.ChunkSizeBytes)}' must be a positive multiple of "
                + $"{UploadChunkSizeMultipleBytes} no larger than {MaxUploadChunkSizeBytes}.");
        }

        if (options.Upload.MaxChunkRetries < MinUploadChunkRetries
            || options.Upload.MaxChunkRetries > MaxUploadChunkRetries)
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Upload)}:"
                + $"{nameof(YouTubeUploadOptions.MaxChunkRetries)}' must be "
                + $"{MinUploadChunkRetries}..{MaxUploadChunkRetries}.");
        }

        AddUploadTimeoutProblem(
            problems,
            nameof(YouTubeUploadOptions.StartTimeoutSeconds),
            options.Upload.StartTimeoutSeconds,
            MaxUploadHandshakeTimeoutSeconds);

        AddUploadTimeoutProblem(
            problems,
            nameof(YouTubeUploadOptions.ProbeTimeoutSeconds),
            options.Upload.ProbeTimeoutSeconds,
            MaxUploadHandshakeTimeoutSeconds);

        AddUploadTimeoutProblem(
            problems,
            nameof(YouTubeUploadOptions.ChunkTimeoutSeconds),
            options.Upload.ChunkTimeoutSeconds,
            MaxUploadChunkTimeoutSeconds);

        return problems;
    }

    /// <summary>Reports one <c>Upload:*TimeoutSeconds</c> outside 1..<paramref name="max"/>.</summary>
    private static void AddUploadTimeoutProblem(
        List<string> problems,
        string key,
        int seconds,
        int max)
    {
        if (seconds < MinUploadTimeoutSeconds || seconds > max)
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Upload)}:{key}' must be "
                + $"{MinUploadTimeoutSeconds}..{max}.");
        }
    }

    /// <summary>
    /// Whether <paramref name="uploadBaseUrl"/> can be the resumable-upload root: absolute, http or https, no
    /// user-info, no query and no fragment, and ending in <c>/</c> so that the <c>videos?uploadType=resumable</c>
    /// path appended to it lands under the configured path instead of replacing its last segment.
    /// </summary>
    private static bool IsUploadBaseUrl(string uploadBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(uploadBaseUrl)
            || !uploadBaseUrl.EndsWith('/')
            || !Uri.TryCreate(uploadBaseUrl, UriKind.Absolute, out var uri)
            || !IsHttpScheme(uri.Scheme))
        {
            return false;
        }

        return uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;
    }

    /// <summary>
    /// Whether <paramref name="secretRef"/> can be a secret reference: ASCII letters, digits, <c>_</c>, <c>/</c>
    /// and <c>-</c>, nothing else. A <c>:</c> is refused because the configuration-backed
    /// <c>ISecretProvider</c> reads a reference's <em>last</em> path segment as an <c>IConfiguration</c> key, and a
    /// colon would turn a reference into a path to an unrelated section (<c>ConnectionStrings:Default</c>) whose
    /// value would then be sent to Google as the client secret (security review F5).
    /// </summary>
    private static bool IsWellFormedSecretRef(string secretRef) =>
        secretRef.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '/' or '-');

    /// <summary>
    /// Whether <paramref name="scheme"/> is <c>http</c> or <c>https</c>: the only two schemes a Google OAuth
    /// redirect URI can use, and the check that rules out a rooted path parsed as a <c>file</c> URI.
    /// </summary>
    private static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="scopes"/> is the frozen pair: both scopes present, each exactly once, and
    /// nothing else. The order is not significant — Google is told the same set either way — but an extra,
    /// a missing or a duplicated scope is a configuration a reviewer must see.
    /// </summary>
    private static bool IsRequiredScopeSet(IReadOnlyList<string> scopes)
    {
        if (scopes.Count != YouTubeScopes.Required.Count)
        {
            return false;
        }

        return scopes.Distinct(StringComparer.Ordinal).Count() == YouTubeScopes.Required.Count
            && scopes.All(scope => YouTubeScopes.Required.Contains(scope, StringComparer.Ordinal));
    }
}
