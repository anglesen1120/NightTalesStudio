using NightTales.Domain.Common;

namespace NightTales.Application.Features.Research.SourceFinder;

/// <summary>One reviewed research site from <c>config/source-finder/allowed-sites.v1.json</c> (02.7.2-T1).</summary>
/// <param name="Domain">The host without scheme or path, lowercase punycode.</param>
/// <param name="Name">The display name.</param>
/// <param name="Language">The language: <c>vi</c>, <c>en</c> or <c>any</c>.</param>
/// <param name="Category">The category: <c>encyclopedia</c>, <c>news</c>, <c>court</c>, <c>archive</c>, <c>government</c> or <c>culture</c>.</param>
/// <param name="Status">The review status: <c>candidate</c>, <c>approved</c> or <c>rejected</c>.</param>
public sealed record AllowedSite(string Domain, string Name, string Language, string Category, string Status);

/// <summary>
/// The tracked, reviewed allow-list of research sites (02.7.2-T1). The finder may only search approved sites, and
/// approval needs a recorded basis that automated access is permitted (DECISIONS §10.10).
/// </summary>
public interface IResearchAllowList
{
    /// <summary>The approved sites for <paramref name="language"/>: entries whose language is that language or <c>any</c>.</summary>
    /// <param name="language">The requested language.</param>
    /// <returns>The approved sites, in file order.</returns>
    IReadOnlyList<AllowedSite> Approved(Language language);

    /// <summary>Whether <paramref name="registrableOrHost"/> is an approved domain or a subdomain of one.</summary>
    /// <param name="registrableOrHost">A registrable domain or a host name. Callers pass <c>SourceUrl.Host</c> (canonical ASCII punycode); any non-ASCII character returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when an approved entry matches at a label boundary and the blocklist does not block it.</returns>
    bool IsAllowed(string registrableOrHost);

    /// <summary>The lowercase hex SHA-256 of the file's raw bytes, logged at start-up.</summary>
    string Hash { get; }
}
