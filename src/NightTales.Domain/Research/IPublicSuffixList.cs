namespace NightTales.Domain.Research;

/// <summary>
/// The public-suffix port (02.5.1 plan §3.1): the Domain decides URL syntax, the Infrastructure owns the list.
/// </summary>
public interface IPublicSuffixList
{
    /// <summary>
    /// The registrable domain of <paramref name="host"/> (a lowercase punycode DNS name without a trailing dot):
    /// the public suffix plus one label, or <see langword="null"/> when the host is itself a public suffix or has no
    /// label above it. A TLD the list does not know is treated as a one-label suffix; this never throws.
    /// </summary>
    /// <param name="host">The host to reduce.</param>
    /// <returns>The registrable domain, or <see langword="null"/>.</returns>
    string? GetRegistrableDomain(string host);
}
