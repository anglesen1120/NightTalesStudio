using System.Globalization;
using System.Reflection;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research;

/// <summary>
/// The research allow-list read from the embedded <c>allowed-sites.v1.json</c> and validated by
/// <see cref="ResearchAllowListValidator"/> (02.7.2-T1). Only <c>approved</c> entries count for <see cref="IsAllowed"/>.
/// </summary>
public sealed class EmbeddedResearchAllowList : IResearchAllowList
{
    /// <summary>The logical name of the embedded file (see the Infrastructure csproj).</summary>
    public const string ResourceName = "NightTales.Infrastructure.Research.allowed-sites.v1.json";

    private readonly IReadOnlyList<AllowedSite> _approved;
    private readonly DomainBlocklist _blocklist;

    /// <summary>Creates the allow-list over a validated snapshot.</summary>
    /// <param name="snapshot">The validated snapshot.</param>
    /// <param name="blocklist">The domain blocklist, checked again on every lookup (blocklist wins at every level).</param>
    public EmbeddedResearchAllowList(ResearchAllowListSnapshot snapshot, DomainBlocklist blocklist)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _blocklist = blocklist ?? throw new ArgumentNullException(nameof(blocklist));
        _approved = snapshot.Sites.Where(s => s.Status == "approved").ToList();
        Hash = snapshot.Hash;
    }

    /// <inheritdoc />
    public string Hash { get; }

    /// <summary>Reads, validates and wraps the embedded file.</summary>
    /// <param name="suffixes">The public-suffix list.</param>
    /// <param name="blocklist">The domain blocklist.</param>
    /// <returns>The allow-list.</returns>
    /// <exception cref="InvalidOperationException">When the embedded resource is missing.</exception>
    /// <exception cref="ResearchAllowListException">When the file breaks an AL rule.</exception>
    public static EmbeddedResearchAllowList LoadEmbedded(IPublicSuffixList suffixes, DomainBlocklist blocklist)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        var validator = new ResearchAllowListValidator(suffixes, blocklist);
        var snapshot = validator.Validate(buffer.ToArray());
        return new EmbeddedResearchAllowList(snapshot, blocklist);
    }

    /// <inheritdoc />
    public IReadOnlyList<AllowedSite> Approved(Language language)
    {
        var code = language.ToString().ToLowerInvariant();
        return _approved
            .Where(s => s.Language == code || s.Language == "any")
            .ToList();
    }

    /// <inheritdoc />
    public bool IsAllowed(string registrableOrHost)
    {
        ArgumentNullException.ThrowIfNull(registrableOrHost);

        var host = registrableOrHost.Trim();
        if (host.EndsWith('.'))
        {
            host = host[..^1];
        }

        host = host.ToLowerInvariant();
        if (host.Any(c => c > 127))
        {
            try
            {
                host = new IdnMapping().GetAscii(host);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (!ResearchHostGrammar.IsCanonicalHost(host) || _blocklist.IsBlocked(host))
        {
            return false;
        }

        foreach (var site in _approved)
        {
            if (host == site.Domain || host.EndsWith("." + site.Domain, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
