using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using ErrorOr;
using NightTales.Fetcher.Core.Fetching;
using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Research;
using NightTales.Fetcher.Core.Research.Http;
using NightTales.TestKit.Fetching;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>
/// <see cref="SafeHttpSourceFetcher"/> end to end over a real loopback socket: the real client, connector, robots policy
/// and (unless a test substitutes one) the real SmartReader extractor. Only DNS and the address validator are faked (D20).
/// The T0 corpus HTML is git-ignored and regenerated on demand, so the Vietnamese and SPA pages are inline fixtures of the
/// same shape.
/// </summary>
public sealed class SafeHttpSourceFetcherTests
{
    private const string Html = "text/html; charset=utf-8";

    private static readonly string[] VietnameseParagraphs =
    [
        "Hà Nội những ngày cuối thu, hoa sữa nở thơm ngát khắp các con phố nhỏ và người đi đường chậm bước hơn bao giờ hết.",
        "Người dân thủ đô quen với tiếng rao quen thuộc của những gánh hàng rong, mùi cốm mới và hương cà phê buổi sáng sớm.",
        "Đường Nguyễn Du, Phan Đình Phùng và Hoàng Diệu rợp bóng cây xà cừ, nơi những cặp vợ chồng già vẫn dắt tay nhau đi dạo mỗi chiều.",
        "Câu chuyện của họ là câu chuyện của một thành phố biết giữ gìn ký ức, biết yêu thương những điều bình dị nhất của cuộc sống.",
        "Mỗi con ngõ nhỏ đều có một người kể chuyện, mỗi mái nhà cổ đều lưu giữ một mùa đông đã đi qua nhưng chưa bao giờ phai nhạt.",
        "Trời se lạnh, những chiếc áo len được lấy ra từ đáy tủ, mang theo mùi long não và cả những kỷ niệm tưởng chừng đã ngủ yên.",
    ];

    private static string Article(string title, IEnumerable<string> paragraphs) =>
        $"<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><title>{title}</title></head><body>" +
        "<nav><a href=\"/\">Trang chủ</a></nav><article><h1>" + title + "</h1>" +
        string.Concat(paragraphs.Select(p => $"<p>{p}</p>")) +
        "</article><footer>Bản quyền thuộc về tòa soạn</footer></body></html>";

    private static TestResponse Page(string html, string contentType = Html) => TestResponse.Text(200, contentType, html);

    private static TestResponse Gzip(byte[] raw, string contentType)
    {
        using var buffer = new MemoryStream();
        using (var zip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zip.Write(raw);
        }

        return new TestResponse(200, contentType, buffer.ToArray())
        {
            Headers = new Dictionary<string, string> { ["Content-Encoding"] = "gzip" },
        };
    }

    private static string Code(ErrorOr<FetchedSource> result)
    {
        result.IsError.ShouldBeTrue("expected a fetch error");

        return result.FirstError.Code;
    }

    [Fact]
    public async Task Fetch_VietnameseArticle_ExtractsTitleAndText()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu Hà Nội", VietnameseParagraphs)));

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Code : string.Empty);
        var source = result.Value;
        source.Title.ShouldNotBeNull().ShouldContain("Mùa thu Hà Nội");
        source.Text.ShouldContain("hoa sữa nở thơm ngát");
        source.Text.ShouldContain("Nguyễn Du, Phan Đình Phùng và Hoàng Diệu");
        source.Text.ShouldNotContain("<");
        source.Text.ShouldNotContain(">");
        source.Text.ShouldNotContain("](");
        source.Text.ShouldNotContain("**");
        source.TextChars.ShouldBe(source.Text.Length);
        source.HttpStatus.ShouldBe(200);
        source.ContentType.ShouldBe("text/html");
        source.Sha256.Length.ShouldBe(64);
        source.FinalUrl.ShouldBe("http://www.example.com/a/secret-path?q=1");
    }

    [Fact]
    public async Task Fetch_SpaPage_ReturnsSourceNoText()
    {
        const string spa = "<!doctype html><html><head><title>App</title></head><body><div id=\"root\"></div>" +
            "<script src=\"/static/bundle.js\"></script><noscript>You need to enable JavaScript.</noscript></body></html>";
        await using var h = FetchHarness.Create(_ => Page(spa));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceNoTextCode);
    }

    [Theory]
    [InlineData(1_274, false)]
    [InlineData(1_275, true)]
    public async Task Fetch_MinTextCharsBoundary(int chars, bool succeeds)
    {
        var text = new string('a', chars);
        var extractor = new StubExtractor(_ => new ArticleExtraction("t", text, chars, IsMainTextFound: true));
        await using var h = FetchHarness.Create(_ => Page("<html><body><p>x</p></body></html>"), o => o.MinTextChars = 1_275, extractor: extractor);

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(!succeeds);
        if (!succeeds)
        {
            result.FirstError.Code.ShouldBe(SourceErrors.SourceNoTextCode);
        }
    }

    [Fact]
    public async Task Fetch_NotReadableAbove1275Chars_ReturnsSourceNoText()
    {
        var extractor = new StubExtractor(_ => new ArticleExtraction("t", new string('a', 5_000), 5_000, IsMainTextFound: false));
        await using var h = FetchHarness.Create(_ => Page("<html><body><p>x</p></body></html>"), o => o.MinTextChars = 1_275, extractor: extractor);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceNoTextCode);
    }

    [Fact]
    public async Task Fetch_TextTooLong_ReturnsSourceTooLarge()
    {
        var extractor = new StubExtractor(_ => new ArticleExtraction("t", "a", 300_001, IsMainTextFound: true));
        await using var h = FetchHarness.Create(_ => Page("<html><body><p>x</p></body></html>"), extractor: extractor);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
    }

    [Fact]
    public async Task Fetch_PlainTextContentType_SkipsExtraction()
    {
        var extractor = new StubExtractor(_ => throw new InvalidOperationException("extractor must not run for text/plain"));
        var body = string.Join(' ', Enumerable.Repeat("Một đoạn văn bản thuần túy rất dài.", 10));
        await using var h = FetchHarness.Create(_ => TestResponse.Text(200, "text/plain; charset=utf-8", body), extractor: extractor);

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        extractor.Calls.ShouldBe(0);
        result.Value.Text.ShouldContain("Một đoạn văn bản thuần túy");
        result.Value.ContentType.ShouldBe("text/plain");
    }

    [Fact]
    public async Task Fetch_EmptyBody_ReturnsSourceNoText()
    {
        await using var h = FetchHarness.Create(_ => new TestResponse(200, "text/html", []));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceNoTextCode);
    }

    [Fact]
    public async Task Fetch_UnknownCharset_FallsBackToUtf8()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs), "text/html; charset=klingon-9"));

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Value.Text.ShouldContain("hoa sữa");
    }

    [Fact]
    public async Task Fetch_MetaCharset_IsUsedWhenHeaderHasNone()
    {
        var html = Article("Mùa thu", VietnameseParagraphs).Replace("<meta charset=\"utf-8\">", "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">", StringComparison.Ordinal);
        await using var h = FetchHarness.Create(_ => Page(html, "text/html"));

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Value.Text.ShouldContain("Nguyễn Du");
    }

    [Fact]
    public async Task Fetch_RedirectToLoopback_ReturnsSourceUrlInvalid()
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect("http://127.0.0.1/"));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUrlInvalidCode);
        h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(1);
    }

    [Fact]
    public async Task Fetch_RedirectToBlockedDomain_ReturnsSourceDomainNotAllowed()
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect("https://m.youtube.com/x"));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceDomainNotAllowedCode);
    }

    [Fact]
    public async Task Fetch_RedirectWithCredentials_ReturnsSourceUrlInvalid()
    {
        await using var h = FetchHarness.Create(_ => TestResponse.Redirect("http://user:pw@www.example.com/x"));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUrlInvalidCode);
    }

    [Fact]
    public async Task Fetch_FourRedirects_ReturnsSourceUnreachable()
    {
        await using var h = FetchHarness.Create(request => TestResponse.Redirect(
            "http://www.example.com/hop" + (int.TryParse(request.Target.Replace("/hop", string.Empty, StringComparison.Ordinal), out var n) ? n + 1 : 1)));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);
        h.Server.Requests.Count(r => r.Target != "/robots.txt").ShouldBe(4, "the first request plus three followed hops");
    }

    [Fact]
    public async Task Fetch_RedirectLoop_ReturnsSourceUnreachable()
    {
        await using var h = FetchHarness.Create(request => TestResponse.Redirect(
            request.Target.StartsWith("/a", StringComparison.Ordinal) ? "http://www.example.com/b" : "http://www.example.com/a"));

        Code(await h.FetchAsync("http://www.example.com/a", ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);
    }

    [Fact]
    public async Task Fetch_RelativeRedirect_IsFollowedAndRechecked()
    {
        await using var h = FetchHarness.Create(request => request.Target.StartsWith("/start", StringComparison.Ordinal)
            ? TestResponse.Redirect("/final/page")
            : Page(Article("Mùa thu", VietnameseParagraphs)));

        var result = await h.FetchAsync("http://www.example.com/start", ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        result.Value.FinalUrl.ShouldBe("http://www.example.com/final/page");
        h.Server.Requests.Select(r => r.Target).ShouldContain("/final/page");
    }

    [Fact]
    public async Task Fetch_RedirectIsRecheckedAgainstRobots()
    {
        await using var h = FetchHarness.Create(request => request.Target.StartsWith("/start", StringComparison.Ordinal)
            ? TestResponse.Redirect("/private/page")
            : Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /private\n");

        Code(await h.FetchAsync("http://www.example.com/start", ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        h.Server.Requests.Select(r => r.Target).ShouldNotContain("/private/page");
    }

    [Fact]
    public async Task Fetch_GzipBomb_ReturnsSourceTooLarge()
    {
        var raw = new byte[3 * 1024 * 1024];
        Array.Fill(raw, (byte)'a');
        var response = Gzip(raw, Html);
        response.Body!.Length.ShouldBeLessThan(64 * 1024, "the bomb is tiny on the wire");
        await using var h = FetchHarness.Create(_ => response);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
    }

    [Fact]
    public async Task Fetch_LargeCompressedButSmallDecompressed_IsAllowed()
    {
        // The cap is on decompressed bytes (D11): a body well under it decompresses fine.
        var html = Article("Mùa thu", VietnameseParagraphs);
        await using var h = FetchHarness.Create(_ => Gzip(Encoding.UTF8.GetBytes(html), Html));

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    public async Task Fetch_WrongContentType_ReturnsSourceUnsupportedType(string contentType)
    {
        await using var h = FetchHarness.Create(_ => new TestResponse(200, contentType, [0x89, 0x50, 0x4E, 0x47]));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnsupportedTypeCode);
    }

    [Fact]
    public async Task Fetch_DeclaredHtmlButBinary_ReturnsSourceUnsupportedType()
    {
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, .. new byte[600]];
        await using var h = FetchHarness.Create(_ => new TestResponse(200, "text/html", zip));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnsupportedTypeCode);
    }

    [Fact]
    public async Task Fetch_UnexpectedContentEncoding_FailsTheSniff()
    {
        byte[] garbage = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x01, 0x02, 0x03, .. new byte[600]];
        var response = new TestResponse(200, "text/html", garbage)
        {
            Headers = new Dictionary<string, string> { ["Content-Encoding"] = "zstd" },
        };
        await using var h = FetchHarness.Create(_ => response);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnsupportedTypeCode);
    }

    [Fact]
    public async Task Fetch_RobotsDisallow_ReturnsSourceBlockedByRobots()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /\n");

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
        h.Server.Requests.Select(r => r.Target).ShouldAllBe(target => target == "/robots.txt");
    }

    [Fact]
    public async Task Fetch_RobotsMissing_Succeeds()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => new TestResponse(404);

        (await h.FetchAsync(ct: TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Fetch_RobotsServerError_ReturnsSourceBlockedByRobots()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => new TestResponse(500);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);
    }

    [Fact]
    public async Task Fetch_RobotsCached_OneRobotsRequestAcrossTwoFetches()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        var ct = TestContext.Current.CancellationToken;

        (await h.FetchAsync(ct: ct)).IsError.ShouldBeFalse();
        (await h.FetchAsync("http://www.example.com/second", ct: ct)).IsError.ShouldBeFalse();

        h.Server.Requests.Count(r => r.Target == "/robots.txt").ShouldBe(1);
    }

    [Fact]
    public async Task Fetch_CrawlDelay_FeedsTheRateLimiter()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => TestResponse.Text(200, "text/plain", "User-agent: *\nCrawl-delay: 3\n");

        (await h.FetchAsync(ct: TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();

        h.Limiter.Calls.ShouldHaveSingleItem().ShouldBe(("example.com", TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Fetch_CrawlDelayBelowFloor_FeedsTheOneSecondFloor()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => TestResponse.Text(200, "text/plain", "User-agent: *\nCrawl-delay: 0.5\n");

        (await h.FetchAsync(ct: TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();

        h.Limiter.Calls.ShouldHaveSingleItem().MinInterval.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Fetch_CrawlDelayAboveCap_ReturnsSourceBlockedByRobots_WithoutWaiting()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));
        h.Robots = _ => TestResponse.Text(200, "text/plain", "User-agent: *\nCrawl-delay: 30\n");
        var clock = Stopwatch.StartNew();

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceBlockedByRobotsCode);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        h.Limiter.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fetch_Timeout_ReturnsSourceUnreachable()
    {
        await using var h = FetchHarness.Create(
            _ => new TestResponse(200, Html, Encoding.UTF8.GetBytes("<html></html>")) { Delay = TimeSpan.FromSeconds(30) },
            o => o.TotalTimeoutSeconds = 1);
        var clock = Stopwatch.StartNew();

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6), "the shared budget, not the 30 s stall, bounds the fetch");
    }

    [Fact]
    public async Task Fetch_ShutdownCancellation_Propagates()
    {
        await using var h = FetchHarness.Create(_ => new TestResponse(200, Html, [1]) { Delay = TimeSpan.FromSeconds(30) });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(async () => await h.FetchAsync(ct: shutdown.Token));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Fetch_HttpErrorStatus_ReturnsSourceUnreachable(int status)
    {
        await using var h = FetchHarness.Create(_ => new TestResponse(status, Html, Encoding.UTF8.GetBytes("<html>nope</html>")));

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);
    }

    [Fact]
    public async Task Fetch_SetsExpectedHeaders_AndNoCookieOrAuth()
    {
        var calls = 0;
        await using var h = FetchHarness.Create(_ =>
        {
            calls++;
            var response = Page(Article("Mùa thu", VietnameseParagraphs));

            return response with { Headers = new Dictionary<string, string> { ["Set-Cookie"] = "session=abc; Path=/" } };
        });
        var ct = TestContext.Current.CancellationToken;

        (await h.FetchAsync(ct: ct, acceptLanguage: "vi")).IsError.ShouldBeFalse();
        (await h.FetchAsync(ct: ct, acceptLanguage: "vi")).IsError.ShouldBeFalse();

        calls.ShouldBe(2);
        foreach (var request in h.Server.Requests)
        {
            request.Headers["User-Agent"].ShouldStartWith("NightTalesStudioBot/");
            request.Headers["User-Agent"].ShouldContain("(+https://");
            request.Headers["Accept-Encoding"].ShouldBe("gzip, br");
            request.Host.ShouldStartWith("www.example.com");
            request.Headers.ContainsKey("Cookie").ShouldBeFalse();
            request.Headers.ContainsKey("Authorization").ShouldBeFalse();
            request.Headers.ContainsKey("Referer").ShouldBeFalse();
        }

        h.Server.Requests.Where(r => r.Target != "/robots.txt").ShouldAllBe(r => r.Headers["Accept-Language"] == "vi");
    }

    [Fact]
    public async Task Fetch_AcceptLanguageDefaultsToTheConfiguredFallback()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)), o => o.AcceptLanguage = "en");

        await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        h.Server.Requests[^1].Headers["Accept-Language"].ShouldBe("en");
    }

    [Fact]
    public async Task Fetch_DoesNotFollowMetaRefresh()
    {
        var html = Article("Mùa thu", VietnameseParagraphs).Replace("<head>", "<head><meta http-equiv=\"refresh\" content=\"0;url=http://www.example.com/elsewhere\">", StringComparison.Ordinal);
        await using var h = FetchHarness.Create(_ => Page(html));

        var result = await h.FetchAsync(ct: TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse();
        h.Server.Requests.Select(r => r.Target).ShouldNotContain("/elsewhere");
    }

    [Fact]
    public async Task Fetch_DeepHtml_DepthGuard_TooLarge_NoCrash()
    {
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<div>", 100_000)) + "x";
        html.Length.ShouldBeLessThan(2 * 1024 * 1024);
        var extractor = new StubExtractor(_ => throw new InvalidOperationException("no DOM may be built"));
        await using var h = FetchHarness.Create(_ => Page(html), extractor: extractor);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
        extractor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Fetch_ManyElements_ElementGuard_TooLarge()
    {
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<span></span>", 60)) + "</body></html>";
        await using var h = FetchHarness.Create(_ => Page(html), o => o.ExtractMaxElements = 50);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
    }

    [Fact]
    public async Task Fetch_ExtractWatchdogTimeout_NoText_ExtractTimeoutReason()
    {
        var blocking = new BlockingExtractor();
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)), o => o.ExtractTimeoutSeconds = 1, extractor: blocking);
        var clock = Stopwatch.StartNew();

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceNoTextCode);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
        h.Logs.Lines.ShouldContain(line => line.Contains("extract_timeout", StringComparison.Ordinal));
    }

    // CR-06 / PERF-01 / F2: the watchdog cannot stop a parser, so a timed-out worker keeps its in-flight slot until its
    // thread really ends. While the slots are full a fetch is answered at once and starts no thread.
    [Fact]
    public async Task Fetch_TimedOutExtractions_KeepTheirSlots_AndAFullHouseStartsNoNewWorker()
    {
        var gate = new GateExtractor();
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)), o => o.ExtractTimeoutSeconds = 1, extractor: gate);
        try
        {
            // Fill every slot with an extraction that ignores its token, as SmartReader does mid-parse.
            var filling = Enumerable.Range(0, 4).Select(_ => h.FetchAsync(ct: TestContext.Current.CancellationToken).AsTask()).ToArray();
            foreach (var result in await Task.WhenAll(filling))
            {
                Code(result).ShouldBe(SourceErrors.SourceNoTextCode);
            }

            gate.Started.ShouldBe(4);
            gate.Finished.ShouldBe(0);

            var clock = Stopwatch.StartNew();
            Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceNoTextCode);

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), "a full house must not wait for the watchdog");
            gate.Started.ShouldBe(4, "no fifth worker may start while four are alive");
            h.Logs.Lines.ShouldContain(line => line.Contains("extract_busy", StringComparison.Ordinal));

            // The threads end: the slots come back and extraction runs again.
            gate.Release();
            var deadline = Stopwatch.StartNew();
            while (gate.Started < 5 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                _ = await h.FetchAsync(ct: TestContext.Current.CancellationToken);
            }

            gate.Started.ShouldBe(5);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task Fetch_GuardRejections_ReleaseTheirSlots()
    {
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<div/>", 1_000)) + "x";
        await using var h = FetchHarness.Create(_ => Page(html));

        // More rejections than slots: each must still be source_too_large, never the full-house answer.
        for (var i = 0; i < 12; i++)
        {
            Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceTooLargeCode);
        }
    }

    [Fact]
    public async Task Fetch_ExtractorFailures_ReleaseTheirSlots()
    {
        var extractor = new StubExtractor(_ => throw new InvalidOperationException("boom"));
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)), extractor: extractor);

        for (var i = 0; i < 12; i++)
        {
            Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);
        }

        extractor.Calls.ShouldBe(12);
    }

    [Fact]
    public async Task Fetch_ExtractorThrows_ReturnsSourceUnreachable_LogsOnlyTheExceptionType()
    {
        var extractor = new StubExtractor(_ => throw new InvalidOperationException("boom http://www.example.com/a/secret-path?q=1"));
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)), extractor: extractor);

        Code(await h.FetchAsync(ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUnreachableCode);

        h.Logs.Lines.ShouldContain(line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
        h.Logs.Lines.ShouldNotContain(line => line.Contains("secret-path", StringComparison.Ordinal) || line.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fetch_BlockedDomainRequest_ReturnsSourceDomainNotAllowed_WithoutAnyRequest()
    {
        await using var h = FetchHarness.Create(_ => Page(string.Empty));

        Code(await h.FetchAsync("https://m.youtube.com/watch?v=x", ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceDomainNotAllowedCode);
        h.Server.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task Fetch_IpLiteralUrl_ReturnsSourceUrlInvalid_EvenWithTheTestValidator()
    {
        await using var h = FetchHarness.Create(_ => Page(Article("Mùa thu", VietnameseParagraphs)));

        Code(await h.FetchAsync($"http://127.0.0.1:{h.Server.Port}/", ct: TestContext.Current.CancellationToken)).ShouldBe(SourceErrors.SourceUrlInvalidCode);
        h.Server.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task Fetch_LoopbackHost_SucceedsOnlyWithTheTestValidator()
    {
        var script = (TestRequest _) => Page(Article("Mùa thu", VietnameseParagraphs));

        await using (var withTestValidator = FetchHarness.Create(script))
        {
            (await withTestValidator.FetchAsync(ct: TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();
        }

        await using var production = FetchHarness.Create(script, validator: new GlobalUnicastAddressValidator());
        var result = await production.FetchAsync(ct: TestContext.Current.CancellationToken);

        // robots (fail closed) or the page itself: either way nothing reaches the loopback server.
        result.IsError.ShouldBeTrue();
        production.Server.Connections.ShouldBe(0);
    }

    [Fact]
    public async Task Fetch_NoLogLineContainsAUrlPathQueryTitleOrText()
    {
        await using var h = FetchHarness.Create(request => request.Target.StartsWith("/a/secret-path", StringComparison.Ordinal)
            ? TestResponse.Redirect("https://www.example.com/a/other-secret?x=2")
            : Page(Article("Tiêu đề bí mật", VietnameseParagraphs)));
        h.Robots = _ => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /a/other-secret\n");

        _ = await h.FetchAsync(ct: TestContext.Current.CancellationToken);
        await using var plain = FetchHarness.Create(_ => Page(Article("Tiêu đề bí mật", VietnameseParagraphs)));
        _ = await plain.FetchAsync(ct: TestContext.Current.CancellationToken);

        foreach (var line in h.Logs.Lines.Concat(plain.Logs.Lines))
        {
            line.ShouldNotContain("example.com/a", Case.Insensitive);
            line.ShouldNotContain("secret", Case.Insensitive);
            line.ShouldNotContain("q=1");
            line.ShouldNotContain("Tiêu đề");
            line.ShouldNotContain("hoa sữa");
        }
    }

    private sealed class BlockingExtractor : IArticleExtractor
    {
        public ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct)
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();

            return new ArticleExtraction(null, string.Empty, 0, IsMainTextFound: false);
        }
    }

    // Ignores its token until released, like a parser in the middle of GetArticle().
    private sealed class GateExtractor : IArticleExtractor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _finished;

        public int Started => Volatile.Read(ref _started);

        public int Finished => Volatile.Read(ref _finished);

        public void Release() => _release.TrySetResult();

        public ArticleExtraction Extract(string html, Uri url, string? contentType, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _started);
            _ = _release.Task.Wait(TimeSpan.FromSeconds(60), CancellationToken.None);
            _ = Interlocked.Increment(ref _finished);

            return new ArticleExtraction(null, string.Empty, 0, IsMainTextFound: false);
        }
    }
}
