using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Research.Sources;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Infrastructure.DependencyInjection;
using NightTales.Fetcher.Core.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// QA probes for 02.5.1-T1: the real embedded public-suffix list through <see cref="SourceUrl.Parse"/> and the D11
/// blocklist, the cache bound, logging privacy of the unlisted-TLD fallback, and extra start-up validator cases.
/// </summary>
public sealed class PublicSuffixQaProbeTests
{
    private static readonly PublicSuffixListProvider Real = new(NullLogger<PublicSuffixListProvider>.Instance);

    private static readonly DomainBlocklist Blocklist = new(new HashSet<string>(StringComparer.Ordinal)
    {
        "youtube.com", "youtu.be", "youtube-nocookie.com", "tiktok.com", "facebook.com", "fb.watch", "fb.com",
        "instagram.com", "threads.net", "x.com", "twitter.com", "t.co", "reddit.com", "vimeo.com", "dailymotion.com",
        "twitch.tv", "bilibili.com", "zalo.me", "zingmp3.vn",
    });

    [Theory]
    [InlineData("https://sub.x.com/a", true)]
    [InlineData("https://x.com.au/a", false)]
    [InlineData("https://x.com.evil.org/a", false)]
    [InlineData("https://nt.co/a", false)]
    [InlineData("https://www.t.co/a", true)]
    [InlineData("https://m.facebook.com/a", true)]
    [InlineData("https://facebook.com.vn/a", false)]
    [InlineData("https://www.tiktok.com/@u/video/1", true)]
    [InlineData("https://vm.tiktok.com/x", true)]
    [InlineData("https://youtube.com.vn/a", false)]
    [InlineData("https://youtu.be/abc", true)]
    [InlineData("https://www.youtu.be/abc", true)]
    [InlineData("https://vnexpress.net/a", false)]
    [InlineData("https://twitch.tv/x", true)]
    [InlineData("https://clips.twitch.tv/x", true)]
    [InlineData("https://zingmp3.vn/x", true)]
    [InlineData("https://mp3.zing.vn/x", false)]
    public void RealList_ThenBlocklist_BlocksOnlyTheRegistrableDomain(string url, bool blocked)
    {
        var parsed = SourceUrl.Parse(url, Real);

        parsed.IsError.ShouldBeFalse(url);
        Blocklist.IsBlocked(parsed.Value.RegistrableDomain).ShouldBe(blocked, $"{url} -> {parsed.Value.RegistrableDomain}");
    }

    [Theory]
    [InlineData("https://example.co.uk/")]
    [InlineData("https://co.uk/")]
    [InlineData("https://com.vn/")]
    [InlineData("https://uk/")]
    [InlineData("https://github.io/")]
    [InlineData("https://foo.kobe.jp/")]
    public void RealList_PublicSuffixOnlyHosts_AreRejectedAsHavingNoRegistrableDomain(string url)
    {
        var parsed = SourceUrl.Parse(url, Real);

        if (url == "https://example.co.uk/" || url == "https://github.io/")
        {
            // example.co.uk is registrable; github.io is a PRIVATE-section suffix, and only the ICANN section counts.
            parsed.IsError.ShouldBeFalse(url);
            return;
        }

        parsed.IsError.ShouldBeTrue($"{url} is itself a public suffix");
    }

    [Theory]
    [InlineData("city.kobe.jp", "city.kobe.jp")]
    [InlineData("www.city.kobe.jp", "city.kobe.jp")]
    [InlineData("a.b.foo.kobe.jp", "b.foo.kobe.jp")]
    [InlineData("example.com.au", "example.com.au")]
    [InlineData("deep.sub.example.org.uk", "example.org.uk")]
    [InlineData("a.example.unlistedtldqa", "example.unlistedtldqa")]
    public void RealList_WildcardExceptionAndUnlistedRules(string host, string expected) =>
        Real.GetRegistrableDomain(host).ShouldBe(expected);

    [Fact]
    public void Cache_PastTheLimit_StillAnswersCorrectly_AndNeverThrows()
    {
        var list = new PublicSuffixListProvider(NullLogger<PublicSuffixListProvider>.Instance);

        for (var index = 0; index < PublicSuffixListProvider.CacheLimit + 500; index++)
        {
            list.GetRegistrableDomain($"sub.host{index}.example.com").ShouldBe("example.com");
        }

        list.GetRegistrableDomain("late.example.co.uk").ShouldBe("example.co.uk");
        list.GetRegistrableDomain("sub.host0.example.com").ShouldBe("example.com");
    }

    [Fact]
    public void Concurrent_Lookups_AreStable()
    {
        var results = new string?[64];
        Parallel.For(0, results.Length, index => results[index] = Real.GetRegistrableDomain($"w{index % 8}.example.co.uk"));

        results.ShouldAllBe(result => result == "example.co.uk");
    }

    [Fact]
    public void UnlistedTldFallback_LogsOnlyTheTld_NeverTheHost()
    {
        var sink = new ListLogger<PublicSuffixListProvider>();
        var list = new PublicSuffixListProvider(sink);

        list.GetRegistrableDomain("qa-secret-host-1f2e.qa-secret-sub.unlistedtldqa");

        sink.Lines.ShouldNotBeEmpty("the fallback logs at debug");
        sink.Lines.ShouldAllBe(line => !line.Contains("qa-secret", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("youtube.com ")]
    [InlineData(" youtube.com")]
    [InlineData("youtube.com:443")]
    [InlineData("youtube.com.")]
    [InlineData(".youtube.com")]
    [InlineData("you tube.com")]
    [InlineData("1.2.3.4")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("youtube..com")]
    [InlineData("m.youtube.com")]
    [InlineData("\u4F8B\u3048.jp")]
    [InlineData("[::1]")]
    public void Validator_MoreBadEntries_Fail(string entry)
    {
        var validator = new FetcherOptionsValidator(Real);

        validator.Validate(null, new FetcherOptions { BlockedDomains = [entry] }).Failed.ShouldBeTrue($"'{entry}'");
    }

    [Fact]
    public void Validator_OneBadEntryAmongGood_FailsAndNamesTheIndex()
    {
        var validator = new FetcherOptionsValidator(Real);

        var result = validator.Validate(null, new FetcherOptions { BlockedDomains = ["youtube.com", "https://bad.example", "t.co"] });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BlockedDomains");
        result.FailureMessage.ShouldContain("[1]");
    }

    [Theory]
    [InlineData(1, 1, 1, true)]
    [InlineData(10, 30, 10_000, true)]
    [InlineData(-1, 30, 3000, false)]
    [InlineData(10, -5, 3000, false)]
    [InlineData(10, 30, -1, false)]
    [InlineData(10, 30, 10_001, false)]
    public void SourcesValidator_Boundaries(int perEpisode, int perDay, int noteChars, bool valid)
    {
        var result = new SourcesOptionsValidator().Validate(
            null,
            new SourcesOptions { MaxPerEpisode = perEpisode, MaxUrlSourcesPerUserPerDay = perDay, MaxNoteChars = noteChars });

        result.Succeeded.ShouldBe(valid);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
