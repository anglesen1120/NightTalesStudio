using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Infrastructure.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>Independent QA of 02.7.2-T1: adversarial host, JSON, boundary and start-up cases.</summary>
public sealed class ResearchAllowListQaTests
{
    private const string GoodSite = "\"domain\":\"vi.wikipedia.org\",\"name\":\"Wiki\",\"language\":\"vi\",\"category\":\"encyclopedia\",\"status\":\"candidate\"";

    private static EmbeddedResearchAllowList Committed()
        => EmbeddedResearchAllowList.LoadEmbedded(ResearchAllowListFixtures.Psl, ResearchAllowListFixtures.NoBlocks);

    private static EmbeddedResearchAllowList Snapshot(DomainBlocklist blocklist, params AllowedSite[] sites)
        => new(new ResearchAllowListSnapshot(sites, "qa-hash"), blocklist);

    private static AllowedSite Site(string domain, string language, string status)
        => new(domain, domain, language, "news", status);

    private static byte[] CommittedBytes()
    {
        using var stream = typeof(EmbeddedResearchAllowList).Assembly
            .GetManifestResourceStream(EmbeddedResearchAllowList.ResourceName)
            ?? throw new InvalidOperationException("The committed allow-list is not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Doc(string siteBody) => "{\"version\":1,\"sites\":[{" + siteBody + "}]}";

    private static object[] Many(int count, string status, string prefix)
        => Enumerable.Range(0, count)
            .Select(i => status == "approved"
                ? ResearchAllowListFixtures.ApprovedSite($"{prefix}{i}.example.org")
                : ResearchAllowListFixtures.Site($"{prefix}{i}.example.org", status))
            .ToArray();

    // ---- IsAllowed: hostile and unlisted inputs must be refused ----

    [Theory]
    [InlineData("vi.wikipedia.org:443")]
    [InlineData("user@vi.wikipedia.org")]
    [InlineData("vi.wikipedia.org/path")]
    [InlineData("https://vi.wikipedia.org")]
    [InlineData("vi.wikipedia.org?x=1")]
    [InlineData("vi.wikipedia.org#frag")]
    [InlineData("vi.\twikipedia.org")]
    [InlineData("vi.wiki\npedia.org")]
    [InlineData("vi.wikipedia.org\\evil")]
    [InlineData("[::1]")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("việt.wikipedia.org")]
    [InlineData("vі.wikipedia.org")] // Cyrillic і (U+0456) look-alike; punycodes to a different host
    [InlineData("vi.wikipedia.org\u3000")] // non-ASCII ideographic space after an approved host
    [InlineData("vi.wikipedia.org.evil")]
    [InlineData("evilvi.wikipedia.org")]
    [InlineData("vnexpress.net.evil.com")]
    [InlineData("wikipedia.org")]
    [InlineData("fr.wikipedia.org")]
    [InlineData("gov.uk")]
    [InlineData("reuters.com")]
    [InlineData("vnexpress.net")]
    [InlineData("www.vnexpress.net")]
    [InlineData("m.youtube.com")]
    public void IsAllowed_HostileOrUnlistedInput_False(string host)
        => Committed().IsAllowed(host).ShouldBeFalse();

    [Theory]
    [InlineData("vi.wikipedia.org")]
    [InlineData("VI.WIKIPEDIA.ORG")]
    [InlineData("x.vi.wikipedia.org")]
    [InlineData("vi.wikipedia.org.")]
    [InlineData("  vi.wikipedia.org  ")]
    [InlineData("en.wikipedia.org")]
    [InlineData("x.www.gov.uk")]
    public void IsAllowed_ApprovedHostInAnyNormalisedForm_True(string host)
        => Committed().IsAllowed(host).ShouldBeTrue();

    // Probes: malformed hostnames that still end in an approved domain. A host is never valid
    // with an empty label, a control character, or a non-hostname character, so these must be refused.
    [Theory]
    [InlineData("evil\0.vi.wikipedia.org")]
    [InlineData("evil\u0001.vi.wikipedia.org")]
    [InlineData(".vi.wikipedia.org")]
    [InlineData("a..vi.wikipedia.org")]
    [InlineData("*.vi.wikipedia.org")]
    [InlineData("a%2f.vi.wikipedia.org")]
    [InlineData("a|b.vi.wikipedia.org")]
    [InlineData("a\"b.vi.wikipedia.org")]
    [InlineData("a<b>.vi.wikipedia.org")]
    [InlineData("a b.vi.wikipedia.org")]
    public void IsAllowed_MalformedHostnameEndingInApprovedDomain_False(string host)
        => Committed().IsAllowed(host).ShouldBeFalse();

    [Fact]
    public void IsAllowed_HostOver253Characters_False()
    {
        // AL2 caps a domain at 253 characters; a lookup should apply the same bound.
        var host = new string('a', 250) + ".vi.wikipedia.org";

        Committed().IsAllowed(host).ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_InputOver255CharactersBeforeTrim_False()
    {
        // The length guard runs on the raw input, so whitespace padding cannot bring a host under the bound.
        var host = new string(' ', 250) + "vi.wikipedia.org";

        Committed().IsAllowed(host).ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_ApprovedButBlockedHost_False()
    {
        // Blocklist wins at every level: a snapshot that somehow carries an approved, blocked host is refused.
        var list = Snapshot(
            ResearchAllowListFixtures.Blocklist("wikipedia.org"),
            Site("vi.wikipedia.org", "vi", "approved"));

        list.IsAllowed("vi.wikipedia.org").ShouldBeFalse();
        list.IsAllowed("x.vi.wikipedia.org").ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_CandidateAndRejectedEntries_False()
    {
        var list = Snapshot(
            ResearchAllowListFixtures.NoBlocks,
            Site("vnexpress.net", "vi", "candidate"),
            Site("reuters.com", "en", "rejected"));

        list.IsAllowed("vnexpress.net").ShouldBeFalse();
        list.IsAllowed("reuters.com").ShouldBeFalse();
    }

    // ---- Approved(): order, language filter, status filter ----

    [Fact]
    public void Approved_KeepsFileOrder_IncludesAny_ExcludesCandidatesAndRejected()
    {
        var list = Snapshot(
            ResearchAllowListFixtures.NoBlocks,
            Site("b.example.org", "vi", "approved"),
            Site("any.example.org", "any", "approved"),
            Site("cand.example.org", "vi", "candidate"),
            Site("a.example.org", "vi", "approved"),
            Site("en.example.org", "en", "approved"),
            Site("rej.example.org", "any", "rejected"));

        list.Approved(Language.Vi).Select(s => s.Domain).ShouldBe(["b.example.org", "any.example.org", "a.example.org"]);
        list.Approved(Language.En).Select(s => s.Domain).ShouldBe(["any.example.org", "en.example.org"]);
    }

    [Fact]
    public void Approved_LoadedFromFile_KeepsFileOrder()
    {
        // Three approved sites in non-alphabetical file order: Approved returns them in that order, not sorted.
        var bytes = Bytes(ResearchAllowListFixtures.Document(
            ResearchAllowListFixtures.ApprovedSite("c.example.org"),
            ResearchAllowListFixtures.ApprovedSite("a.example.org"),
            ResearchAllowListFixtures.ApprovedSite("b.example.org")));

        var list = new EmbeddedResearchAllowList(ResearchAllowListFixtures.Validator().Validate(bytes), ResearchAllowListFixtures.NoBlocks);

        list.Approved(Language.Vi).Select(s => s.Domain).ShouldBe(["c.example.org", "a.example.org", "b.example.org"]);
    }

    // ---- AL2: host grammar and public suffix ----

    [Theory]
    [InlineData("vi.wikipedia.org.")]
    [InlineData("localhost")]
    [InlineData("-vi.wikipedia.org")]
    [InlineData("vi-.wikipedia.org")]
    [InlineData("vi_x.wikipedia.org")]
    [InlineData("vi.wikipedia.org:443")]
    [InlineData(" vi.wikipedia.org")]
    [InlineData(".wikipedia.org")]
    [InlineData("việt.wikipedia.org")]
    [InlineData("vi..wikipedia.org")]
    [InlineData("10.0.0.1")]
    [InlineData("x.localhost")]
    [InlineData("x.internal")]
    [InlineData("x.home.arpa")]
    public void AL2_HostGrammarViolation_Fails(string domain)
        => ThrowsViolation(Bytes(Doc(GoodSite.Replace("vi.wikipedia.org", domain))))
            .ShouldViolate("AL2");

    [Fact]
    public void AL2_Label63Characters_Passes()
    {
        var domain = new string('a', 63) + ".wikipedia.org";

        Should.NotThrow(() => ResearchAllowListFixtures.Validator().Validate(Bytes(Doc(GoodSite.Replace("vi.wikipedia.org", domain)))));
    }

    [Fact]
    public void AL2_Label64Characters_FailsAL2()
    {
        var domain = new string('a', 64) + ".wikipedia.org";

        ThrowsViolation(Bytes(Doc(GoodSite.Replace("vi.wikipedia.org", domain))))
            .ShouldViolate("AL2");
    }

    [Fact]
    public void AL2_DomainOver253Characters_FailsAL2()
    {
        var domain = new string('a', 250) + ".org";

        ThrowsViolation(Bytes(Doc(GoodSite.Replace("vi.wikipedia.org", domain))))
            .ShouldViolate("AL2");
    }

    [Fact]
    public void AL2_UppercaseDuplicateOfApprovedDomain_FailsAL2()
    {
        var json = "{\"version\":1,\"sites\":["
            + "{" + GoodSite.Replace("candidate", "approved").Replace("\"Wiki\"", "\"Wiki\",\"automated_access_basis\":\"robots allows\",\"reviewed_at\":\"2026-10-16\",\"reviewer\":\"owner\"") + "},"
            + "{" + GoodSite.Replace("vi.wikipedia.org", "VI.WIKIPEDIA.ORG") + "}]}";

        ThrowsViolation(Bytes(json)).ShouldViolate("AL2");
    }

    [Theory]
    [InlineData("gov.uk")]
    [InlineData("co.uk")]
    [InlineData("com.vn")]
    public void AL2_PublicSuffixEntry_FailsAL2(string domain)
    {
        ResearchAllowListFixtures.Psl.GetRegistrableDomain(domain).ShouldBeNull();
        ThrowsViolation(Bytes(Doc(GoodSite.Replace("vi.wikipedia.org", domain))))
            .ShouldViolate("AL2");
    }

    [Fact]
    public void AL2_RegistrableDomainUnderPublicSuffix_Passes()
    {
        ResearchAllowListFixtures.Psl.GetRegistrableDomain("www.gov.uk").ShouldBe("www.gov.uk");
        Should.NotThrow(() => ResearchAllowListFixtures.Validator().Validate(Bytes(Doc(GoodSite.Replace("vi.wikipedia.org", "www.gov.uk")))));
    }

    // ---- AL1: JSON and schema edge cases must fail closed ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"sites\"")]
    [InlineData("{\"version\":1,\"sites\":[],}")]
    [InlineData("{\"version\":1,\"sites\":[] /* note */}")]
    [InlineData("{\"version\":\"1\",\"sites\":[]}")]
    [InlineData("{\"version\":1.0,\"sites\":[]}")]
    [InlineData("{\"version\":null,\"sites\":[]}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"sites\":{}}")]
    [InlineData("{\"version\":1,\"sites\":[null]}")]
    [InlineData("{\"version\":1,\"sites\":[\"vi.wikipedia.org\"]}")]
    public void AL1_MalformedDocument_Fails(string json)
        => ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL1");

    [Fact]
    public void AL1_DeeplyNestedDocument_FailsWithoutStackOverflow()
    {
        var nested = new string('[', 2000) + new string(']', 2000);
        var json = "{\"version\":1,\"sites\":" + nested + "}";

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL1");
    }

    [Fact]
    public void AL1_DuplicateTopLevelKey_Fails()
        => ResearchAllowListFixtures.ThrowsViolation("{\"version\":1,\"version\":2,\"sites\":[]}").ShouldViolate("AL1");

    [Fact]
    public void AL1_DuplicateSiteStatus_Fails()
    {
        // A file that says both rejected and approved for one entry must not be read either way.
        var json = "{\"version\":1,\"sites\":[{"
            + "\"domain\":\"vi.wikipedia.org\",\"name\":\"Wiki\",\"language\":\"vi\",\"category\":\"encyclopedia\","
            + "\"status\":\"rejected\",\"status\":\"approved\","
            + "\"automated_access_basis\":\"robots allows\",\"reviewed_at\":\"2026-10-16\",\"reviewer\":\"owner\"}]}";

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL1");
    }

    [Theory]
    [InlineData("\"domain\":5,\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":null,\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\"")]
    [InlineData("\"domain\":null,\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"VI\",\"category\":\"news\",\"status\":\"candidate\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"News\",\"status\":\"candidate\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"Approved\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\",\"reviewed_at\":20261016")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\",\"reviewed_at\":\"2026-02-30\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\",\"reviewed_at\":\"16/10/2026\"")]
    [InlineData("\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"candidate\",\"automated_access_basis\":true")]
    public void AL1_WrongTypeOrValueInSiteMember_Fails(string siteBody)
        => ResearchAllowListFixtures.ThrowsViolation(Doc(siteBody)).ShouldViolate("AL1");

    [Fact]
    public void AL1_ByteOrderMarkPrefixedFile_FailsWithAL1()
    {
        // The committed file has no BOM. A BOM-prefixed copy is refused, closed, as AL1 and never with another exception type.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("{\"version\":1,\"sites\":[]}")).ToArray();

        ThrowsViolation(bytes).ShouldViolate("AL1");
    }

    // ---- AL4: approved entries need all three governance fields ----

    [Theory]
    [InlineData("\"automated_access_basis\":\"   \",\"reviewed_at\":\"2026-10-16\",\"reviewer\":\"owner\"")]
    [InlineData("\"automated_access_basis\":\"robots allows\",\"reviewed_at\":\"\",\"reviewer\":\"owner\"")]
    [InlineData("\"automated_access_basis\":\"robots allows\",\"reviewed_at\":\"2026-10-16\",\"reviewer\":\"  \"")]
    [InlineData("\"automated_access_basis\":\"robots allows\",\"reviewed_at\":null,\"reviewer\":\"owner\"")]
    [InlineData("\"automated_access_basis\":\"robots allows\",\"reviewed_at\":\"2026-10-16\"")]
    [InlineData("\"reviewed_at\":\"2026-10-16\",\"reviewer\":\"owner\"")]
    public void AL4_ApprovedMissingOrBlankGovernanceField_Fails(string governance)
    {
        var body = "\"domain\":\"vi.wikipedia.org\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"approved\"," + governance;

        ResearchAllowListFixtures.ThrowsViolation(Doc(body)).ShouldViolate("AL4");
    }

    // ---- AL3: blocklist applies to the entry host and its registrable domain ----

    [Fact]
    public void AL3_ApprovedEntryUnderBlockedParent_FailsAL3()
    {
        // wikipedia.org blocked: vi.wikipedia.org is refused through the registrable domain.
        ResearchAllowListFixtures.ThrowsViolation(CommittedText(), ResearchAllowListFixtures.Blocklist("wikipedia.org"))
            .ShouldViolate("AL3");
    }

    [Fact]
    public void AL3_ApprovedMYouTubeHost_FailsAL3()
    {
        var body = "\"domain\":\"m.youtube.com\",\"name\":\"n\",\"language\":\"vi\",\"category\":\"news\",\"status\":\"approved\","
            + "\"automated_access_basis\":\"robots allows\",\"reviewed_at\":\"2026-10-16\",\"reviewer\":\"owner\"";

        ResearchAllowListFixtures.ThrowsViolation(Doc(body), ResearchAllowListFixtures.Blocklist("youtube.com")).ShouldViolate("AL3");
    }

    private static string CommittedText() => System.Text.Encoding.UTF8.GetString(CommittedBytes());

    // ---- AL5: boundary; candidates and rejected entries are not counted ----

    [Fact]
    public void AL5_HundredApprovedWithFiftyCandidatesAndRejected_Passes()
    {
        var sites = Many(100, "approved", "a").Concat(Many(50, "candidate", "c")).Concat(Many(1, "rejected", "r")).ToArray();
        var bytes = Bytes(ResearchAllowListFixtures.Document(sites));

        Should.NotThrow(() => ResearchAllowListFixtures.Validator().Validate(bytes));
    }

    [Fact]
    public void AL5_HundredOneApprovedWithCandidates_FailsAL5()
    {
        var sites = Many(101, "approved", "a").Concat(Many(5, "candidate", "c")).ToArray();

        ResearchAllowListFixtures.ThrowsViolation(ResearchAllowListFixtures.Document(sites)).ShouldViolate("AL5");
    }

    // ---- Hash ----

    [Fact]
    public void Hash_IsLowerHexSha256OfRawBytes()
    {
        var bytes = Bytes(ResearchAllowListFixtures.Document(ResearchAllowListFixtures.ApprovedSite("vi.wikipedia.org")));

        var snapshot = ResearchAllowListFixtures.Validator().Validate(bytes);

        snapshot.Hash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    // ---- Start-up: a broken or blocked file fails host start through DI, not on the first request ----

    [Fact]
    public async Task StartAsync_ApprovedEntryOnBlockedDomain_ThrowsAL3AtStart()
    {
        var hosted = StartupValidator(ResearchAllowListFixtures.Blocklist("wikipedia.org"));

        var error = await Should.ThrowAsync<ResearchAllowListException>(() => hosted.StartAsync(CancellationToken.None));

        error.ShouldViolate("AL3");
        error.Message.ShouldContain("AL3:");
    }

    [Fact]
    public async Task StartAsync_CommittedListWithNoBlocks_Completes()
    {
        var hosted = StartupValidator(ResearchAllowListFixtures.NoBlocks);

        await hosted.StartAsync(CancellationToken.None);
    }

    private static ResearchAllowListStartupValidator StartupValidator(DomainBlocklist blocklist)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NightTales.Domain.Research.IPublicSuffixList>(ResearchAllowListFixtures.Psl);
        services.AddSingleton(blocklist);
        services.AddResearchAllowList();

        return services.BuildServiceProvider()
            .GetServices<IHostedService>()
            .OfType<ResearchAllowListStartupValidator>()
            .Single();
    }

    private static byte[] Bytes(string json) => ResearchAllowListFixtures.Bytes(json);

    private static ResearchAllowListException ThrowsViolation(byte[] bytes)
        => Should.Throw<ResearchAllowListException>(() => ResearchAllowListFixtures.Validator().Validate(bytes));
}
