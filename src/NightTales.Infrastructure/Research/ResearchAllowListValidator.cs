using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Research;

namespace NightTales.Infrastructure.Research;

/// <summary>The validated allow-list: its sites and the lowercase hex SHA-256 of the file's raw bytes.</summary>
/// <param name="Sites">Every entry, in file order.</param>
/// <param name="Hash">The lowercase hex SHA-256 of the raw bytes.</param>
public sealed record ResearchAllowListSnapshot(IReadOnlyList<AllowedSite> Sites, string Hash);

/// <summary>Thrown when the allow-list breaks an AL rule. It carries every violation, not only the first.</summary>
public sealed class ResearchAllowListException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="violations">Each violation, prefixed with its rule id (AL1–AL6).</param>
    public ResearchAllowListException(IReadOnlyList<string> violations)
        : base("The research allow-list failed validation: " + string.Join("; ", violations))
    {
        Violations = violations;
    }

    /// <summary>The violations, each prefixed with its rule id.</summary>
    public IReadOnlyList<string> Violations { get; }
}

/// <summary>
/// Validates <c>allowed-sites.v1.json</c> at start-up (02.7.2-T1): AL1 the JSON parses with <c>version = 1</c> and
/// the enum fields hold allowed values; AL2 each domain is a lowercase punycode host with no scheme, path, port or
/// trailing dot and is not a public suffix; AL3 no entry is on the <see cref="DomainBlocklist"/> (host or registrable
/// domain, D11); AL4 approved entries carry a basis, a review date and a reviewer; AL5 at most 100 approved entries
/// (W2); AL6 no duplicate domains.
/// </summary>
/// <remarks>
/// The validator is pure: it does no I/O and reads only the bytes it is given, so the start-up check and the tests run
/// the same code. The public-suffix list and the blocklist are the shared helpers, not second copies.
/// </remarks>
public sealed class ResearchAllowListValidator
{
    /// <summary>W2: the most entries that may be <c>approved</c>.</summary>
    public const int MaxApproved = 100;

    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal) { "vi", "en", "any" };
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "encyclopedia", "news", "court", "archive", "government", "culture",
    };
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "candidate", "approved", "rejected" };

    /// <summary>
    /// AL2 reserved suffixes. Mirrors the private ReservedLabels and HasReservedSuffix in SourceUrl (Domain),
    /// which Infrastructure cannot reach.
    /// </summary>
    private static readonly string[] ReservedSuffixes = ["localhost", "local", "localdomain", "internal", "lan", "intranet", "corp", "invalid", "test", "example", "onion", "home.arpa"];

    private readonly IPublicSuffixList _suffixes;
    private readonly DomainBlocklist _blocklist;

    /// <summary>Creates the validator.</summary>
    /// <param name="suffixes">The public-suffix list (02.5.1).</param>
    /// <param name="blocklist">The domain blocklist (02 D11).</param>
    public ResearchAllowListValidator(IPublicSuffixList suffixes, DomainBlocklist blocklist)
    {
        _suffixes = suffixes ?? throw new ArgumentNullException(nameof(suffixes));
        _blocklist = blocklist ?? throw new ArgumentNullException(nameof(blocklist));
    }

    /// <summary>Parses and validates the file.</summary>
    /// <param name="bytes">The file's raw bytes.</param>
    /// <returns>The snapshot, when every rule holds.</returns>
    /// <exception cref="ResearchAllowListException">When any AL rule is broken; the exception lists all violations.</exception>
    public ResearchAllowListSnapshot Validate(ReadOnlyMemory<byte> bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        var violations = new List<string>();
        var entries = new List<Entry>();

        try
        {
            using var document = JsonDocument.Parse(bytes);
            CheckDuplicateMembers(document.RootElement, "root", violations);
            ReadDocument(document.RootElement, entries, violations);
        }
        catch (JsonException ex)
        {
            violations.Add($"AL1: the file is not valid JSON ({ex.Message}).");
        }

        CheckEntries(entries, violations);

        if (violations.Count > 0)
        {
            throw new ResearchAllowListException(violations);
        }

        var sites = entries.Select(e => new AllowedSite(e.Domain, e.Name, e.Language, e.Category, e.Status)).ToList();

        return new ResearchAllowListSnapshot(sites, hash);
    }

    private static void CheckDuplicateMembers(JsonElement element, string where, List<string> violations)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    violations.Add($"AL1: {where} repeats the member '{property.Name}'.");
                }

                CheckDuplicateMembers(property.Value, $"{where}.{property.Name}", violations);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                CheckDuplicateMembers(item, $"{where}[{index++}]", violations);
            }
        }
    }

    private static void ReadDocument(JsonElement root, List<Entry> entries, List<string> violations)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            violations.Add("AL1: the root is not a JSON object.");
            return;
        }

        var versionOk = root.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var number)
            && number == 1;
        if (!versionOk)
        {
            violations.Add("AL1: version must be 1.");
        }

        if (!root.TryGetProperty("sites", out var sites) || sites.ValueKind != JsonValueKind.Array)
        {
            violations.Add("AL1: sites must be an array.");
            return;
        }

        var index = 0;
        foreach (var site in sites.EnumerateArray())
        {
            ReadSite(site, index++, entries, violations);
        }
    }

    private static void ReadSite(JsonElement site, int index, List<Entry> entries, List<string> violations)
    {
        if (site.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"AL1: sites[{index}] is not an object.");
            return;
        }

        var domain = Member(site, "domain", index, allowed: null, violations);
        var name = Member(site, "name", index, allowed: null, violations);
        var language = Member(site, "language", index, Languages, violations);
        var category = Member(site, "category", index, Categories, violations);
        var status = Member(site, "status", index, Statuses, violations);
        var basis = OptionalString(site, "automated_access_basis", index, violations);
        var reviewedAt = OptionalString(site, "reviewed_at", index, violations);
        var reviewer = OptionalString(site, "reviewer", index, violations);

        if (reviewedAt is not null
            && !DateOnly.TryParseExact(reviewedAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            violations.Add($"AL1: sites[{index}].reviewed_at must be an ISO date (yyyy-MM-dd).");
        }

        if (domain is null || name is null || language is null || category is null || status is null)
        {
            return;
        }

        entries.Add(new Entry(domain, name, language, category, status, basis, reviewedAt, reviewer));
    }

    private static string? Member(JsonElement site, string key, int index, HashSet<string>? allowed, List<string> violations)
    {
        if (!site.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            violations.Add($"AL1: sites[{index}].{key} must be a string.");
            return null;
        }

        if (allowed is not null && !allowed.Contains(text))
        {
            violations.Add($"AL1: sites[{index}].{key} '{text}' is not an allowed value.");
            return null;
        }

        return text;
    }

    private static string? OptionalString(JsonElement site, string key, int index, List<string> violations)
    {
        if (!site.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        violations.Add($"AL1: sites[{index}].{key} must be a string or null.");
        return null;
    }

    private void CheckEntries(List<Entry> entries, List<string> violations)
    {
        foreach (var entry in entries)
        {
            CheckDomain(entry, violations);

            if (entry.Status == "approved")
            {
                CheckApprovedBasis(entry, violations);
            }
        }

        var approved = entries.Count(e => e.Status == "approved");
        if (approved > MaxApproved)
        {
            violations.Add($"AL5: {approved} approved entries; at most {MaxApproved} are allowed (W2).");
        }

        foreach (var group in entries.GroupBy(e => e.Domain, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            violations.Add($"AL6: domain '{group.Key}' appears {group.Count()} times.");
        }
    }

    private void CheckDomain(Entry entry, List<string> violations)
    {
        if (!ResearchHostGrammar.IsCanonicalHost(entry.Domain))
        {
            violations.Add($"AL2: domain '{entry.Domain}' must be a lowercase punycode host without scheme, path, port or trailing dot.");
            return;
        }

        if (IsIpLiteral(entry.Domain))
        {
            violations.Add($"AL2: domain '{entry.Domain}' is an IP literal; list a host name instead.");
            return;
        }

        if (HasReservedSuffix(entry.Domain))
        {
            violations.Add($"AL2: domain '{entry.Domain}' is under a reserved suffix (localhost, local, localdomain, internal, lan, intranet, corp, invalid, test, example, onion, home.arpa).");
            return;
        }

        var registrable = _suffixes.GetRegistrableDomain(entry.Domain);
        if (registrable is null)
        {
            violations.Add($"AL2: domain '{entry.Domain}' is a public suffix; list the registrable site instead (for example www.gov.uk, not gov.uk).");
            return;
        }

        if (_blocklist.IsBlocked(entry.Domain) || _blocklist.IsBlocked(registrable))
        {
            violations.Add($"AL3: entry '{entry.Domain}' is on the domain blocklist (02 D11).");
        }
    }

    private static bool IsIpLiteral(string domain)
    {
        var lastLabel = domain[(domain.LastIndexOf('.') + 1)..];
        return lastLabel.All(char.IsAsciiDigit);
    }

    private static bool HasReservedSuffix(string domain)
    {
        foreach (var suffix in ReservedSuffixes)
        {
            if (domain == suffix || domain.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckApprovedBasis(Entry entry, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(entry.Basis))
        {
            violations.Add($"AL4: approved entry '{entry.Domain}' needs a non-empty automated_access_basis.");
        }

        if (string.IsNullOrWhiteSpace(entry.ReviewedAt))
        {
            violations.Add($"AL4: approved entry '{entry.Domain}' needs a reviewed_at date.");
        }

        if (string.IsNullOrWhiteSpace(entry.Reviewer))
        {
            violations.Add($"AL4: approved entry '{entry.Domain}' needs a reviewer.");
        }
    }

    private sealed record Entry(
        string Domain,
        string Name,
        string Language,
        string Category,
        string Status,
        string? Basis,
        string? ReviewedAt,
        string? Reviewer);
}
