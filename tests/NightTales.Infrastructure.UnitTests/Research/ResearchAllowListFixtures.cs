using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Research;
using NightTales.Infrastructure.Research;
using Shouldly;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>Inline builders for <c>allowed-sites.v1.json</c> documents used by the allow-list tests (02.7.2-T1).</summary>
internal static class ResearchAllowListFixtures
{
    /// <summary>The real public-suffix list (02.5.1), the same instance type the Api registers.</summary>
    public static readonly IPublicSuffixList Psl = new PublicSuffixListProvider(NullLogger<PublicSuffixListProvider>.Instance);

    /// <summary>A blocklist with no entries.</summary>
    public static readonly DomainBlocklist NoBlocks = Blocklist();

    /// <summary>Builds a blocklist over the given lowercase registrable domains.</summary>
    public static DomainBlocklist Blocklist(params string[] domains)
        => new(new HashSet<string>(domains, StringComparer.Ordinal));

    /// <summary>UTF-8 bytes of a JSON document.</summary>
    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>A version-1 document holding the given site objects.</summary>
    public static string Document(params object[] sites) => JsonSerializer.Serialize(new { version = 1, sites });

    /// <summary>One site object in the committed file's schema.</summary>
    public static object Site(
        string domain,
        string status = "candidate",
        string language = "vi",
        string category = "news",
        string? basis = null,
        string? reviewedAt = null,
        string? reviewer = null)
        => new
        {
            domain,
            name = domain,
            language,
            category,
            terms_url = (string?)null,
            robots_checked = false,
            automated_access_basis = basis,
            status,
            reviewed_at = reviewedAt,
            reviewer,
        };

    /// <summary>An approved site with every governance field filled in.</summary>
    public static object ApprovedSite(string domain, string language = "vi")
        => Site(domain, "approved", language, basis: "robots allows", reviewedAt: "2026-10-16", reviewer: "owner");

    /// <summary>A validator over the real public-suffix list and the given blocklist.</summary>
    public static ResearchAllowListValidator Validator(DomainBlocklist? blocklist = null)
        => new(Psl, blocklist ?? NoBlocks);

    /// <summary>Validates the JSON and returns the thrown violation exception.</summary>
    public static ResearchAllowListException ThrowsViolation(string json, DomainBlocklist? blocklist = null)
        => Should.Throw<ResearchAllowListException>(() => Validator(blocklist).Validate(Bytes(json)));

    /// <summary>Asserts that at least one violation carries the given rule id.</summary>
    public static void ShouldViolate(this ResearchAllowListException exception, string rule)
        => exception.Violations.Any(v => v.StartsWith(rule + ":", StringComparison.Ordinal))
            .ShouldBeTrue(string.Join("; ", exception.Violations));
}
