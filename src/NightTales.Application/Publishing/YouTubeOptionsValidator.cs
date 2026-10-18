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

        if (!Uri.TryCreate(options.RedirectUri, UriKind.Absolute, out _))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.RedirectUri)}' must be an absolute URI: "
                + "Google answers the callback here and rejects an unusable one.");
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

        return problems;
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
