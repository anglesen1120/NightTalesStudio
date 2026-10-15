using System.Security.Cryptography;
using NightTales.Infrastructure.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

public sealed class ResearchAllowListValidatorTests
{
    [Fact]
    public void AllowList_EntryOnBlockedDomain_FailsStartup()
    {
        // m.youtube.com is blocked through its registrable domain youtube.com (AL3, D11).
        var blocklist = ResearchAllowListFixtures.Blocklist("youtube.com");
        var json = ResearchAllowListFixtures.Document(ResearchAllowListFixtures.Site("m.youtube.com"));

        ResearchAllowListFixtures.ThrowsViolation(json, blocklist).ShouldViolate("AL3");
    }

    [Fact]
    public void AllowList_ApprovedWithoutBasis_FailsStartup()
    {
        // An approved entry with no automated_access_basis (AL4).
        var site = ResearchAllowListFixtures.Site(
            "vnexpress.net", "approved", basis: null, reviewedAt: "2026-10-16", reviewer: "owner");
        var json = ResearchAllowListFixtures.Document(site);

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL4");
    }

    [Fact]
    public void AllowList_Over100Approved_FailsStartup()
    {
        var sites = Enumerable.Range(0, 101)
            .Select(i => (object)ResearchAllowListFixtures.ApprovedSite($"site{i}.example.org"))
            .ToArray();
        var json = ResearchAllowListFixtures.Document(sites);

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL5");
    }

    [Fact]
    public void AllowList_Exactly100Approved_Passes()
    {
        var sites = Enumerable.Range(0, ResearchAllowListValidator.MaxApproved)
            .Select(i => (object)ResearchAllowListFixtures.ApprovedSite($"site{i}.example.org"))
            .ToArray();
        var json = ResearchAllowListFixtures.Document(sites);

        var snapshot = ResearchAllowListFixtures.Validator().Validate(ResearchAllowListFixtures.Bytes(json));

        snapshot.Sites.Count(s => s.Status == "approved").ShouldBe(ResearchAllowListValidator.MaxApproved);
    }

    [Fact]
    public void AllowList_InvalidJson_FailsAL1()
    {
        ResearchAllowListFixtures.ThrowsViolation("{").ShouldViolate("AL1");
    }

    [Fact]
    public void AllowList_WrongVersion_FailsAL1()
    {
        var json = """{"version":2,"sites":[]}""";

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL1");
    }

    [Fact]
    public void AllowList_UnknownStatus_FailsAL1()
    {
        var json = ResearchAllowListFixtures.Document(ResearchAllowListFixtures.Site("vnexpress.net", status: "maybe"));

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL1");
    }

    [Fact]
    public void AllowList_UppercaseDomain_FailsAL2()
    {
        var json = ResearchAllowListFixtures.Document(ResearchAllowListFixtures.Site("Vnexpress.net"));

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL2");
    }

    [Fact]
    public void AllowList_DomainWithScheme_FailsAL2()
    {
        var json = ResearchAllowListFixtures.Document(ResearchAllowListFixtures.Site("https://vnexpress.net"));

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL2");
    }

    [Fact]
    public void AllowList_PublicSuffixEntry_FailsAL2()
    {
        // gov.uk is a public suffix: allow-listing it would admit every *.gov.uk host (AL2).
        var json = ResearchAllowListFixtures.Document(ResearchAllowListFixtures.Site("gov.uk"));

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL2");
    }

    [Fact]
    public void AllowList_RegistrableUnderSuffix_Passes()
    {
        var json = ResearchAllowListFixtures.Document(ResearchAllowListFixtures.Site("www.gov.uk"));

        var snapshot = ResearchAllowListFixtures.Validator().Validate(ResearchAllowListFixtures.Bytes(json));

        snapshot.Sites.Single().Domain.ShouldBe("www.gov.uk");
    }

    [Fact]
    public void AllowList_DuplicateDomain_FailsAL6()
    {
        var json = ResearchAllowListFixtures.Document(
            ResearchAllowListFixtures.Site("vnexpress.net"),
            ResearchAllowListFixtures.Site("vnexpress.net"));

        ResearchAllowListFixtures.ThrowsViolation(json).ShouldViolate("AL6");
    }

    [Fact]
    public void Hash_StableForSameFile()
    {
        var bytes = ResearchAllowListFixtures.Bytes(ResearchAllowListFixtures.Document(
            ResearchAllowListFixtures.ApprovedSite("vi.wikipedia.org")));
        var validator = ResearchAllowListFixtures.Validator();

        var first = validator.Validate(bytes).Hash;
        var second = validator.Validate(bytes).Hash;

        first.ShouldBe(second);
        first.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
