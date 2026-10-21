using System.Net;

namespace NightTales.Domain.Research;

/// <summary>
/// The DNS port of the fetcher (02 D11, D20): the connect callback resolves here, validates, and connects to the
/// validated address, so the checked address is the one connected to.
/// </summary>
public interface IDnsResolver
{
    /// <summary>Resolves <paramref name="host"/> to its addresses; an unknown name yields an empty list.</summary>
    /// <param name="host">The punycode host name.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <returns>The addresses in resolver order; never <see langword="null"/>.</returns>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}
