using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

public sealed class EmbeddedResearchAllowListTests
{
    /// <summary>The production blocklist, read from <c>Fetcher:BlockedDomains</c> in the Api's <c>appsettings.json</c>.</summary>
    private static readonly string[] ProductionBlockedDomains = ReadProductionBlockedDomains();

    private static string[] ReadProductionBlockedDomains()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NightTales.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("NightTales.sln was not found above the test output directory.");
        }

        var path = Path.Combine(dir.FullName, "src", "NightTales.Api", "appsettings.json");
        using var doc = JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var domains = new List<string>();
        foreach (var element in doc.RootElement.GetProperty("Fetcher").GetProperty("BlockedDomains").EnumerateArray())
        {
            domains.Add(element.GetString()!);
        }

        return domains.ToArray();
    }

    private static readonly string[] ViApproved = ["vi-only.example.org", "any.example.org"];
    private static readonly string[] EnApproved = ["en-only.example.org", "any.example.org"];

    private static EmbeddedResearchAllowList Load()
        => EmbeddedResearchAllowList.LoadEmbedded(
            ResearchAllowListFixtures.Psl,
            ResearchAllowListFixtures.Blocklist(ProductionBlockedDomains));

    [Fact]
    public void IsAllowed_SubdomainOfApproved_True()
    {
        // vi.wikipedia.org is approved in the committed file, so its subdomain is allowed.
        Load().IsAllowed("fr.vi.wikipedia.org").ShouldBeTrue();
    }

    [Fact]
    public void IsAllowed_LookalikeDomain_False()
    {
        // vnexpress.net.evil.com only starts with an allowed name; it is not under it.
        Load().IsAllowed("vnexpress.net.evil.com").ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_LookalikeOfApprovedDomain_False()
    {
        Load().IsAllowed("vi.wikipedia.org.evil.com").ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_BlockedHost_False()
    {
        Load().IsAllowed("m.youtube.com").ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_RegistrableUnderPublicSuffix_True()
    {
        // www.gov.uk is approved; the bare suffix gov.uk is not an entry, so other *.gov.uk hosts stay out.
        Load().IsAllowed("www.gov.uk").ShouldBeTrue();
    }

    [Fact]
    public void IsAllowed_OtherHostsUnderPublicSuffix_False()
    {
        var list = Load();

        list.IsAllowed("nhs.uk").ShouldBeFalse();
        list.IsAllowed("foo.gov.uk").ShouldBeFalse();
    }

    [Fact]
    public void IsAllowed_EmptyOrMalformedHost_False()
    {
        var list = Load();

        list.IsAllowed(string.Empty).ShouldBeFalse();
        list.IsAllowed("https://vi.wikipedia.org").ShouldBeFalse();
        list.IsAllowed("vi.wikipedia.org/wiki").ShouldBeFalse();
    }

    [Fact]
    public void Approved_FiltersByLanguageAndAny()
    {
        // vi-only and en-only serve one language; "any" serves both; candidate and rejected never serve.
        var json = """
            {"version":1,"sites":[
              {"domain":"vi-only.example.org","name":"Vi","language":"vi","category":"news","status":"approved","automated_access_basis":"clause quoted","reviewed_at":"2026-10-16","reviewer":"owner"},
              {"domain":"en-only.example.org","name":"En","language":"en","category":"news","status":"approved","automated_access_basis":"clause quoted","reviewed_at":"2026-10-16","reviewer":"owner"},
              {"domain":"any.example.org","name":"Any","language":"any","category":"encyclopedia","status":"approved","automated_access_basis":"clause quoted","reviewed_at":"2026-10-16","reviewer":"owner"},
              {"domain":"candidate.example.org","name":"Candidate","language":"vi","category":"news","status":"candidate"},
              {"domain":"rejected.example.org","name":"Rejected","language":"any","category":"news","status":"rejected"}
            ]}
            """;
        var snapshot = ResearchAllowListFixtures.Validator().Validate(ResearchAllowListFixtures.Bytes(json));
        var list = new EmbeddedResearchAllowList(snapshot, ResearchAllowListFixtures.NoBlocks);

        list.Approved(Language.Vi).Select(s => s.Domain)
            .ShouldBe(ViApproved);
        list.Approved(Language.En).Select(s => s.Domain)
            .ShouldBe(EnApproved);
        list.IsAllowed("candidate.example.org").ShouldBeFalse();
        list.IsAllowed("rejected.example.org").ShouldBeFalse();
    }

    [Fact]
    public void CommittedAllowList_LoadsAndValidates()
    {
        // The committed file must load under the real suffix list and the production blocklist.
        var list = Load();

        list.Hash.Length.ShouldBe(64);
        list.Hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void CommittedAllowList_BlockedRegistrableDomain_FailsAL3()
    {
        // vnexpress.net is a committed entry; blocking its registrable domain must fail the file (AL3).
        var bytes = CommittedBytes();
        var validator = new ResearchAllowListValidator(
            ResearchAllowListFixtures.Psl,
            ResearchAllowListFixtures.Blocklist("vnexpress.net"));

        var exception = Should.Throw<ResearchAllowListException>(() => validator.Validate(bytes));

        exception.ShouldViolate("AL3");
    }

    private static byte[] CommittedBytes()
    {
        using var stream = typeof(EmbeddedResearchAllowList).Assembly
            .GetManifestResourceStream(EmbeddedResearchAllowList.ResourceName)
            ?? throw new InvalidOperationException("The committed allow-list is not embedded.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
