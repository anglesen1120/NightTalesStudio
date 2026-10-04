using NightTales.Infrastructure.Research;
using NightTales.Infrastructure.Research.Http;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>The 11 T0-derived cases of the RFC 9309 subset (02.5.2-T0 spike report §2).</summary>
public sealed class RobotsRulesTests
{
    private static RobotsRules Parse(string content) => RobotsRules.Parse(content, FetcherOptions.UserAgentToken);

    [Fact]
    public void ExactGroupBeatsWildcard()
    {
        var rules = Parse("User-agent: *\nDisallow: /\n\nUser-agent: NightTalesStudioBot\nAllow: /\n");

        rules.IsAllowed("/anything").ShouldBeTrue();
    }

    [Fact]
    public void OtherBotGroup_IsIgnored_AndNoWildcardMeansAllowAll()
    {
        var rules = Parse("User-agent: Googlebot\nDisallow: /\n");

        rules.IsAllowed("/anything").ShouldBeTrue();
    }

    [Fact]
    public void WildcardGroupApplies_WhenNoExactGroup()
    {
        var rules = Parse("User-agent: Googlebot\nAllow: /\n\nUser-agent: *\nDisallow: /private\n");

        rules.IsAllowed("/private/x").ShouldBeFalse();
        rules.IsAllowed("/public").ShouldBeTrue();
    }

    [Fact]
    public void LongestMatchWins()
    {
        var rules = Parse("User-agent: *\nDisallow: /a\nAllow: /a/b\nDisallow: /a/b/c\n");

        rules.IsAllowed("/a/x").ShouldBeFalse();
        rules.IsAllowed("/a/b/x").ShouldBeTrue();
        rules.IsAllowed("/a/b/c/d").ShouldBeFalse();
    }

    [Fact]
    public void EqualLengthMatch_AllowWins()
    {
        var rules = Parse("User-agent: *\nDisallow: /page\nAllow: /page\n");

        rules.IsAllowed("/page").ShouldBeTrue();
    }

    [Fact]
    public void EmptyDisallow_AllowsEverything()
    {
        var rules = Parse("User-agent: *\nDisallow:\n");

        rules.IsAllowed("/anything").ShouldBeTrue();
    }

    [Fact]
    public void WildcardAndEndAnchor()
    {
        var rules = Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /tmp*/draft\n");

        rules.IsAllowed("/files/a.pdf").ShouldBeFalse();
        rules.IsAllowed("/files/a.pdf?x=1").ShouldBeTrue();
        rules.IsAllowed("/files/a.pdfx").ShouldBeTrue();
        rules.IsAllowed("/tmp-1/draft").ShouldBeFalse();
        rules.IsAllowed("/tmp-1/final").ShouldBeTrue();
    }

    [Fact]
    public void RuleCap_DropsTheRulesBeyondOneHundred()
    {
        var content = "User-agent: *\n" + string.Concat(Enumerable.Range(0, RobotsRules.MaxRules + 1).Select(index => $"Disallow: /p{index}/\n"));

        var rules = Parse(content);

        RobotsRules.MaxRules.ShouldBe(100);
        rules.RuleLimitExceeded.ShouldBeTrue();
        rules.IsAllowed("/p0/x").ShouldBeFalse();
        rules.IsAllowed("/p99/x").ShouldBeFalse();
        rules.IsAllowed("/p100/x").ShouldBeTrue("the rule beyond the cap is dropped (permissive degradation)");
    }

    [Fact]
    public void RuleLengthCap_IgnoresARuleLongerThan256_AndKeepsOneOfExactly256()
    {
        // 02.5.2-T1 review F4: the matcher cost is bounded by rules x pattern x path, so oversized patterns are ignored.
        RobotsRules.MaxRuleLength.ShouldBe(256);
        var atCap = "/" + new string('a', RobotsRules.MaxRuleLength - 1);
        var overCap = "/" + new string('b', RobotsRules.MaxRuleLength);

        var rules = Parse($"User-agent: *\nDisallow: {atCap}\nDisallow: {overCap}\n");

        rules.IsAllowed(atCap).ShouldBeFalse();
        rules.IsAllowed(overCap).ShouldBeTrue("a rule longer than the cap is ignored");
    }

    [Fact]
    public void UnknownDirectives_AreIgnored_AndCrawlDelayIsRead()
    {
        var rules = Parse("User-agent: *\nSitemap: https://example.com/s.xml\nHost: example.com\nRequest-rate: 1/5\nCrawl-delay: 3\nDisallow: /x\n");

        rules.CrawlDelay.ShouldBe(TimeSpan.FromSeconds(3));
        rules.IsAllowed("/x").ShouldBeFalse();
        rules.IsAllowed("/y").ShouldBeTrue();
    }

    [Fact]
    public void FractionalCrawlDelay_IsRead()
    {
        Parse("User-agent: *\nCrawl-delay: 0.5\n").CrawlDelay.ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void PathNormalisation_PercentDecodesBeforeMatching()
    {
        var rules = Parse("User-agent: *\nDisallow: /a b\nDisallow: /caf\u00e9\n");

        rules.IsAllowed("/a%20b/c").ShouldBeFalse();
        rules.IsAllowed("/caf%C3%A9").ShouldBeFalse();
        rules.IsAllowed("/other").ShouldBeTrue();
    }

    [Fact]
    public void CommentHandling_DropsEverythingFromTheHash()
    {
        var rules = Parse("# a comment\nUser-agent: * # everyone\nDisallow: /secret# keep out\nDisallow: # nothing\n");

        rules.IsAllowed("/secret").ShouldBeFalse();
        rules.IsAllowed("/open").ShouldBeTrue();
    }

    [Fact]
    public void GroupSplitting_ConsecutiveUserAgentLinesFormOneGroup_ANewOneAfterRulesStartsAnother()
    {
        var rules = Parse("User-agent: Googlebot\nUser-agent: NightTalesStudioBot\nDisallow: /one\nUser-agent: Bingbot\nDisallow: /two\n");

        rules.IsAllowed("/one").ShouldBeFalse();
        rules.IsAllowed("/two").ShouldBeTrue("/two belongs to the Bingbot group");
    }

    [Fact]
    public void IgnoredDirective_EndsTheRunOfUserAgentLines_ANewUserAgentStartsANewGroup()
    {
        // 02.5.2-T1 review CR-04: the Sitemap line ends the run, so OtherBot's Disallow does not belong to NightTalesStudioBot.
        var rules = Parse("User-agent: NightTalesStudioBot\nSitemap: https://example.com/s.xml\nUser-agent: OtherBot\nDisallow: /\n");

        rules.IsAllowed("/anything").ShouldBeTrue("NightTalesStudioBot's own group is empty; the Disallow is OtherBot's");
    }

    [Fact]
    public void IgnoredDirective_BetweenUserAgentLines_DoesNotHideTheRulesOfTheGroupItFollows()
    {
        var rules = Parse("User-agent: NightTalesStudioBot\nDisallow: /one\nSitemap: https://example.com/s.xml\nUser-agent: OtherBot\nDisallow: /two\n");

        rules.IsAllowed("/one").ShouldBeFalse();
        rules.IsAllowed("/two").ShouldBeTrue();
    }

    [Fact]
    public void DirectivesBeforeAnyUserAgent_AreIgnored()
    {
        Parse("Disallow: /\nUser-agent: *\nDisallow: /x\n").IsAllowed("/y").ShouldBeTrue();
    }

    [Fact]
    public void TokenMatch_IsCaseInsensitive_AndPrefersExact()
    {
        Parse("User-agent: nighttalesstudiobot\nDisallow: /\nUser-agent: *\nAllow: /\n").IsAllowed("/x").ShouldBeFalse();
    }

    [Fact]
    public void Garbage_AllowsAll() => Parse("\u0000\u0001 not robots <html>").IsAllowed("/x").ShouldBeTrue();
}
