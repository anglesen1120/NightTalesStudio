using System.IO.Compression;
using System.Text;

namespace NightTales.TestKit.Fetching;

/// <summary>
/// The route set of the SSRF and abuse suite (02.5.2-T4) over <see cref="HttpTestServer"/>: <c>/ok</c>,
/// <c>/redirect?to=</c>, <c>/chain?n=</c>, <c>/gzip-bomb</c>, <c>/pdf</c>, <c>/binary-as-html</c>, <c>/slow?ms=</c>,
/// <c>/cookie-setter</c>, <c>/set-cookie-then-check</c>, <c>/spa</c>, <c>/short-text</c>, <c>/long-text</c>,
/// <c>/article?chars=</c>, <c>/deep-nested</c> and <c>/many-elements</c>, plus the robots.txt answers a test installs for
/// <c>/robots.txt</c>. Every page route answers an HTML page whose title and text carry fixed marker strings, so a leak scan
/// can look for them.
/// </summary>
public static class HttpTestRoutes
{
    /// <summary>The <c>Content-Type</c> of an HTML answer.</summary>
    public const string Html = "text/html; charset=utf-8";

    /// <summary>The title of every article route; a log or metric that contains it is a leak (02 D19).</summary>
    public const string SecretTitle = "Tiêu đề bí mật";

    /// <summary>A phrase of every article's text; a log or metric that contains it is a leak (02 D19).</summary>
    public const string SecretText = "hoa sữa nở thơm";

    private static readonly Lazy<byte[]> GzipBombBody = new(() =>
    {
        // 3 MiB of one byte: a few KiB on the wire, past the 2 MiB decompressed cap in milliseconds.
        var raw = new byte[3 * 1024 * 1024];
        Array.Fill(raw, (byte)'a');
        using var buffer = new MemoryStream();
        using (var zip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zip.Write(raw);
        }

        return buffer.ToArray();
    });

    /// <summary>The compressed size of the <c>/gzip-bomb</c> body, which a test can compare with the decompressed cap.</summary>
    public static int GzipBombWireBytes => GzipBombBody.Value.Length;

    /// <summary>The routes of the suite; <c>/robots.txt</c> is not one of them (the test installs its own answer).</summary>
    /// <param name="request">The request.</param>
    public static TestResponse Standard(TestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (path, query) = Split(request.Target);
        switch (path)
        {
            case "/ok":
                return Page(ArticleHtml(SecretTitle, 900));
            case "/redirect":
                return TestResponse.Redirect(Uri.UnescapeDataString(Query(query, "to") ?? "/ok"));
            case "/chain":
                return int.TryParse(Query(query, "n"), out var hops) && hops > 0
                    ? TestResponse.Redirect($"/chain?n={hops - 1}")
                    : Page(ArticleHtml(SecretTitle, 900));
            case "/gzip-bomb":
                return new TestResponse(200, Html, GzipBombBody.Value)
                {
                    Headers = new Dictionary<string, string> { ["Content-Encoding"] = "gzip" },
                };
            case "/pdf":
                return new TestResponse(200, "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"));
            case "/binary-as-html":
                return new TestResponse(200, Html, [(byte)'P', (byte)'K', 3, 4, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
            case "/slow":
                return Page(ArticleHtml(SecretTitle, 900)) with
                {
                    Delay = TimeSpan.FromMilliseconds(int.TryParse(Query(query, "ms"), out var ms) ? ms : 0),
                };
            case "/cookie-setter":
            case "/set-cookie-then-check":
                return Page(ArticleHtml(SecretTitle, 900)) with
                {
                    Headers = new Dictionary<string, string> { ["Set-Cookie"] = "sid=abc123; Path=/; HttpOnly" },
                };
            case "/spa":
                return Page("<!doctype html><html><head><title>App</title></head><body><div id=\"app\"></div><script src=\"/app.js\"></script></body></html>");
            case "/short-text":
                return Page(ArticleHtml(SecretTitle, 150));
            case "/long-text":
                return Page(ArticleHtml(SecretTitle, 300_001));
            case "/article":
                return Page(ArticleHtml(SecretTitle, int.TryParse(Query(query, "chars"), out var chars) ? chars : 900));
            case "/deep-nested":
                return Page("<!doctype html><html><body>" + string.Concat(Enumerable.Repeat("<div>", 100_000)) + "x</body></html>");
            case "/many-elements":
                return Page("<!doctype html><html><body>" + string.Concat(Enumerable.Repeat("<span></span>", 50_001)) + "</body></html>");
            default:
                return new TestResponse(404);
        }
    }

    /// <summary>A <c>robots.txt</c> that disallows everything.</summary>
    public static TestResponse RobotsDisallowAll() => TestResponse.Text(200, "text/plain", "User-agent: *\nDisallow: /\n");

    /// <summary>A <c>robots.txt</c> that answers 500 (D11: fail closed).</summary>
    public static TestResponse RobotsServerError() => new(500);

    /// <summary>A <c>robots.txt</c> that answers 404 (D11: no file, allow all).</summary>
    public static TestResponse RobotsNotFound() => new(404);

    /// <summary>A <c>robots.txt</c> that answers after <paramref name="delay"/> (a timeout when the delay passes the robots budget).</summary>
    /// <param name="delay">The pause before the answer.</param>
    public static TestResponse RobotsSlow(TimeSpan delay) => new TestResponse(404) { Delay = delay };

    /// <summary>
    /// An article page whose single paragraph holds <paramref name="chars"/> characters of Vietnamese text, so a size boundary
    /// can be hit exactly (the extractor adds a constant, which a test calibrates).
    /// </summary>
    /// <param name="title">The title and heading.</param>
    /// <param name="chars">The paragraph length.</param>
    public static string ArticleHtml(string title, int chars) =>
        "<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><title>" + title + "</title></head><body>"
        + "<nav><a href=\"/\">Trang chủ</a></nav><article><h1>" + title + "</h1><p>" + Filler(chars) + "</p></article>"
        + "<footer>Bản quyền thuộc về tòa soạn</footer></body></html>";

    /// <summary><paramref name="chars"/> characters of running text that start and end on a letter.</summary>
    /// <param name="chars">The length.</param>
    public static string Filler(int chars)
    {
        const string Sentence = "Hà Nội cuối thu, hoa sữa nở thơm ngát khắp các con phố nhỏ, người đi đường chậm bước. ";
        if (chars <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(chars + Sentence.Length);
        while (builder.Length < chars)
        {
            builder.Append(Sentence);
        }

        builder.Length = chars;
        builder[0] = 'H';
        builder[^1] = 'x';

        return builder.ToString();
    }

    private static TestResponse Page(string html) => TestResponse.Text(200, Html, html);

    private static (string Path, string Query) Split(string target)
    {
        var mark = target.IndexOf('?', StringComparison.Ordinal);

        return mark < 0 ? (target, string.Empty) : (target[..mark], target[(mark + 1)..]);
    }

    private static string? Query(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && pair[..eq] == key)
            {
                return pair[(eq + 1)..];
            }
        }

        return null;
    }
}
