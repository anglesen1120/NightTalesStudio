namespace NightTales.Application.Publishing;

/// <summary>
/// Checks the <c>YouTube</c> section so a missing or unusable OAuth configuration stops the start-up and
/// never a request (06 README D11: "validated at start"). Application cannot reference
/// <c>IValidateOptions</c> (Architecture test), so Infrastructure adapts this to it exactly as it does for
/// <see cref="NightTales.Application.Features.Research.SourceFinder.FindSourcesCostOptionsValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the ones a <em>connect</em> needs: a client must be named and configured, its secret must
/// have a reference, the scope set must be exactly the two scopes the design froze, and the state lifetime
/// must stay inside 5..30 minutes (the state is the callback's only authenticator, so a long one widens
/// the window YR10 depends on).
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
        else if (!options.Clients.TryGetValue(options.ActiveClientId, out var active))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}' must contain an entry for "
                + $"'{nameof(YouTubeOptions.ActiveClientId)}'.");
        }
        else if (string.IsNullOrWhiteSpace(active.SecretRef))
        {
            problems.Add(
                $"'{YouTubeOptions.SectionName}:{nameof(YouTubeOptions.Clients)}:<id>:"
                + $"{nameof(YouTubeClientOptions.SecretRef)}' must be set for "
                + $"'{nameof(YouTubeOptions.ActiveClientId)}'.");
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
