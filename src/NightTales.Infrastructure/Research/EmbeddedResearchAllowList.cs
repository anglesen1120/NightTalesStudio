using System.Reflection;
using System.Text;
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

    /// <summary>Raw input longer than this is rejected before any normalisation (perf P1, sec F3).</summary>
    private const int MaxInputLength = 255;

    private readonly (string Domain, string Suffix)[] _approvedHosts;
    private readonly Dictionary<string, IReadOnlyList<AllowedSite>> _approvedByLanguage;
    private readonly DomainBlocklist _blocklist;

    /// <summary>Creates the allow-list over a validated snapshot.</summary>
    /// <param name="snapshot">The validated snapshot.</param>
    /// <param name="blocklist">The domain blocklist, checked again on every lookup (blocklist wins at every level).</param>
    public EmbeddedResearchAllowList(ResearchAllowListSnapshot snapshot, DomainBlocklist blocklist)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _blocklist = blocklist ?? throw new ArgumentNullException(nameof(blocklist));

        var approved = snapshot.Sites.Where(s => s.Status == "approved").ToList();
        _approvedHosts = approved.Select(s => (s.Domain, "." + s.Domain)).ToArray();
        _approvedByLanguage = BuildByLanguage(approved);
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
        return _approvedByLanguage.TryGetValue(code, out var sites) ? sites : Array.Empty<AllowedSite>();
    }

    /// <inheritdoc />
    public bool IsAllowed(string registrableOrHost)
    {
        ArgumentNullException.ThrowIfNull(registrableOrHost);

        if (registrableOrHost.Length > MaxInputLength || !Ascii.IsValid(registrableOrHost))
        {
            return false;
        }

        var host = registrableOrHost.Trim();
        if (host.EndsWith('.'))
        {
            host = host[..^1];
        }

        host = host.ToLowerInvariant();
        if (!ResearchHostGrammar.IsCanonicalHost(host) || _blocklist.IsBlocked(host))
        {
            return false;
        }

        foreach (var (domain, suffix) in _approvedHosts)
        {
            if (host == domain || host.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, IReadOnlyList<AllowedSite>> BuildByLanguage(IReadOnlyList<AllowedSite> approved)
    {
        var byLanguage = new Dictionary<string, IReadOnlyList<AllowedSite>>(StringComparer.Ordinal);
        foreach (var language in Enum.GetValues<Language>())
        {
            var code = language.ToString().ToLowerInvariant();
            byLanguage[code] = approved.Where(s => s.Language == code || s.Language == "any").ToList();
        }

        return byLanguage;
    }
}
