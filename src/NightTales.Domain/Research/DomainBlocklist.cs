namespace NightTales.Domain.Research;

/// <summary>
/// The blocked-domain set of 02 D11 (<c>Fetcher:BlockedDomains</c>). Matching is on the dotted suffixes of the
/// given name at label boundaries, so <c>m.youtube.com</c> and <c>youtube.com</c> both hit the <c>youtube.com</c>
/// entry while <c>notyoutube.com</c> and <c>youtube.com.evil.net</c> do not.
/// </summary>
public sealed class DomainBlocklist
{
    private readonly IReadOnlySet<string> _domains;

    /// <summary>Creates the blocklist over lowercase registrable domains.</summary>
    /// <param name="domains">The blocked registrable domains (data, validated at start-up by the options validator).</param>
    public DomainBlocklist(IReadOnlySet<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);

        _domains = domains;
    }

    /// <summary>Whether <paramref name="host"/> is a blocked domain or a subdomain of one.</summary>
    /// <param name="host">A registrable domain or any host name; compared lowercase.</param>
    /// <returns><see langword="true"/> when an entry equals the name or one of its label-boundary suffixes.</returns>
    public bool IsBlocked(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var candidate = host.Trim().TrimEnd('.').ToLowerInvariant();
        while (candidate.Length > 0)
        {
            if (_domains.Contains(candidate))
            {
                return true;
            }

            var dot = candidate.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                return false;
            }

            candidate = candidate[(dot + 1)..];
        }

        return false;
    }
}
