using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Fetching;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Fetcher;
using NightTales.Domain.Research;
using NightTales.TestKit.Research;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Research;

/// <summary>
/// <see cref="FetchTaskResultValidator"/> (02.5.2-T2 Design items 2-4, T39): the order of the checks, the D8 codes and the
/// T3 body defects (control characters, lone surrogates, missing or unknown status, caps).
/// </summary>
public sealed class FetchTaskResultValidatorTests
{
    private const string GoodUrl = "https://news.example.com/article";

    private static readonly FakePublicSuffixList Psl = new();

    private static readonly DomainBlocklist Blocklist = new(new HashSet<string>(StringComparer.Ordinal) { "blocked.com" });

    private static readonly FetchResultLimits Limits = new(MaxTextChars: 300_000);

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static FetchResultRequest Done(
        string? text = "Some article text.",
        string? sha = null,
        string? finalUrl = GoodUrl,
        string? contentType = "text/html; charset=utf-8",
        string? title = "A title") =>
        new(
            WorkerId: "w1",
            Status: "done",
            HttpStatus: 200,
            FinalUrl: finalUrl,
            Title: title,
            ContentType: contentType,
            Text: text,
            TextSha256: sha ?? (text is null ? null : Sha(text)),
            ErrorCode: null,
            Candidates: null);

    private static FetchResultRequest Failed(string? errorCode, int? httpStatus = 503) =>
        new("w1", "failed", httpStatus, null, null, null, null, null, errorCode, null);

    private static FetchResultRequest Found(params FetchCandidateRequest?[] candidates) =>
        new("w1", "done", null, null, null, null, null, null, null, candidates);

    private static ErrorOr<FetchTaskResult> Fetch(FetchResultRequest body) =>
        FetchTaskResultValidator.Interpret(FetchTaskKind.FetchSource, body, Limits, Psl, Blocklist);

    private static ErrorOr<FetchTaskResult> Find(FetchResultRequest body) =>
        FetchTaskResultValidator.Interpret(FetchTaskKind.FindSources, body, Limits, Psl, Blocklist);

    private static void ShouldFailWith(ErrorOr<FetchTaskResult> result, string code)
    {
        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.Validation);
        result.FirstError.Code.ShouldBe(code);
    }

    [Fact]
    public void Done_ValidText_ReturnsFetchedResultWithRecomputedHash()
    {
        var text = "Xin chào\nline two\ttabbed\r\n";

        var result = Fetch(Done(text, sha: Sha(text).ToUpperInvariant()));

        result.IsError.ShouldBeFalse();
        var fetched = result.Value.ShouldBeOfType<FetchedResult>();
        fetched.Text.ShouldBe(text);
        fetched.Sha256.ShouldBe(Sha(text));
        fetched.ContentType.ShouldBe("text/html");
        fetched.FinalUrl.ShouldBe(GoodUrl);
        fetched.Title.ShouldBe("A title");
        fetched.HttpStatus.ShouldBe(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Done_TextMissing_ReturnsSourceNoText(string? text) =>
        ShouldFailWith(Fetch(Done(text, sha: "x")), ErrorCodes.SourceNoText);

    [Fact]
    public void Done_Sha256Mismatch_ReturnsSourceNoText() =>
        ShouldFailWith(Fetch(Done("text", sha: Sha("other text"))), ErrorCodes.SourceNoText);

    [Fact]
    public void Done_Sha256Missing_ReturnsSourceNoText() =>
        ShouldFailWith(Fetch(Done("text", sha: null) with { TextSha256 = null }), ErrorCodes.SourceNoText);

    [Fact]
    public void Done_OverMaxChars_ReturnsSourceTooLarge()
    {
        var text = new string('a', 300_001);

        ShouldFailWith(Fetch(Done(text)), ErrorCodes.SourceTooLarge);
    }

    [Fact]
    public void Done_ExactlyMaxChars_Accepted()
    {
        var text = new string('a', 300_000);

        var result = Fetch(Done(text));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<FetchedResult>().Text.Length.ShouldBe(300_000);
    }

    [Fact]
    public void Done_OverMaxChars_WinsOverHashMismatch() =>
        ShouldFailWith(Fetch(Done(new string('a', 300_001), sha: "wrong")), ErrorCodes.SourceTooLarge);

    [Theory]
    [InlineData("before\0after")]
    [InlineData("bell\u0007")]
    [InlineData("esc\u001b[0m")]
    [InlineData("del\u007f")]
    [InlineData("\u0000")]
    public void Done_NulOrC0ControlInText_ReturnsSourceNoText(string text) =>
        ShouldFailWith(Fetch(Done(text)), ErrorCodes.SourceNoText);

    public static TheoryData<string> LoneSurrogateTexts() =>
    [
        "lone high " + '\ud800' + " surrogate",
        "lone low " + '\udc00' + " surrogate",
        "trailing high " + '\ud83d',
        "reversed " + '\ude00' + '\ud83d' + " pair",
    ];

    [Theory]
    [MemberData(nameof(LoneSurrogateTexts))]
    public void Done_LoneSurrogateInText_ReturnsSourceNoText(string text)
    {
        // The hash is the correct one for the lossy UTF-8 form, so only the surrogate check can reject it.
        var sha = Sha(text);

        ShouldFailWith(Fetch(Done(text, sha: sha)), ErrorCodes.SourceNoText);
    }

    [Fact]
    public void Done_WellFormedSurrogatePair_Accepted()
    {
        var text = "emoji \ud83d\ude00 ok";

        Fetch(Done(text)).IsError.ShouldBeFalse();
    }

    [Fact]
    public void Done_BlockedFinalUrl_ReturnsSourceDomainNotAllowed() =>
        ShouldFailWith(Fetch(Done(finalUrl: "https://www.blocked.com/x")), ErrorCodes.SourceDomainNotAllowed);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://news.example.com/a")]
    [InlineData("http://127.0.0.1/a")]
    [InlineData("http://localhost/a")]
    [InlineData("https://user:pw@news.example.com/a")]
    public void Done_UnparseableFinalUrl_ReturnsSourceDomainNotAllowed(string? url) =>
        ShouldFailWith(Fetch(Done(finalUrl: url)), ErrorCodes.SourceDomainNotAllowed);

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    [InlineData("text/htmlx")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("; charset=utf-8")]
    public void Done_UnsupportedContentType_ReturnsSourceUnsupportedType(string? contentType) =>
        ShouldFailWith(Fetch(Done(contentType: contentType)), ErrorCodes.SourceUnsupportedType);

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("TEXT/HTML; charset=ISO-8859-1", "text/html")]
    [InlineData("  text/html  ;q=1", "text/html")]
    public void Done_AcceptedContentType_IsNormalisedToMediaType(string contentType, string expected)
    {
        var result = Fetch(Done(contentType: contentType));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<FetchedResult>().ContentType.ShouldBe(expected);
    }

    [Fact]
    public void Done_ChecksRunInContractOrder_TextBeforeUrlBeforeType()
    {
        // Hash wrong AND url blocked AND type unsupported: the hash (source_no_text) is reported first.
        ShouldFailWith(
            Fetch(Done("t", sha: "bad", finalUrl: "https://blocked.com/", contentType: "image/png")),
            ErrorCodes.SourceNoText);

        // URL blocked AND type unsupported: the domain comes first.
        ShouldFailWith(
            Fetch(Done(finalUrl: "https://blocked.com/", contentType: "image/png")),
            ErrorCodes.SourceDomainNotAllowed);
    }

    [Fact]
    public void Done_Title_IsStrippedOfControlsAndCapped()
    {
        var title = "  Head\u0001line\n" + new string('x', 400) + "  ";

        var fetched = Fetch(Done(title: title)).Value.ShouldBeOfType<FetchedResult>();

        fetched.Title.ShouldNotBeNull();
        fetched.Title.ShouldStartWith("Headline");
        fetched.Title.Length.ShouldBe(FetchResultLimits.MaxLabelLength);
        fetched.Title.ShouldNotContain("\u0001");
    }

    public static TheoryData<string> UnusableTitles() =>
        ["   ", "bad " + '\ud800' + " surrogate", "\u0001\u0002"];

    [Fact]
    public void Done_NullTitle_StaysNull() =>
        Fetch(Done(title: null)).Value.ShouldBeOfType<FetchedResult>().Title.ShouldBeNull();

    [Theory]
    [MemberData(nameof(UnusableTitles))]
    public void Done_UnusableTitle_BecomesNull(string title) =>
        Fetch(Done(title: title)).Value.ShouldBeOfType<FetchedResult>().Title.ShouldBeNull();

    [Fact]
    public void Done_Title_CutNeverSplitsASurrogatePair()
    {
        var title = new string('a', FetchResultLimits.MaxLabelLength - 1) + "\ud83d\ude00";

        var cleaned = Fetch(Done(title: title)).Value.ShouldBeOfType<FetchedResult>().Title;

        cleaned.ShouldNotBeNull();
        cleaned.Length.ShouldBe(FetchResultLimits.MaxLabelLength - 1);
        cleaned.Any(char.IsSurrogate).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("DONE")]
    [InlineData("Done")]
    [InlineData("1")]
    [InlineData("unknown")]
    public void Done_NoStatus_Returns400(string? status)
    {
        foreach (var kind in new[] { FetchTaskKind.FetchSource, FetchTaskKind.FindSources })
        {
            var result = FetchTaskResultValidator.Interpret(kind, Done() with { Status = status }, Limits, Psl, Blocklist);

            result.IsError.ShouldBeTrue();
            result.FirstError.Type.ShouldBe(ErrorType.Validation);
            result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
            result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe("status");
        }
    }

    [Theory]
    [InlineData(ErrorCodes.SourceUnreachable)]
    [InlineData(ErrorCodes.SourceTooLarge)]
    [InlineData(ErrorCodes.SourceBlockedByRobots)]
    [InlineData(ErrorCodes.SourceUnsupportedType)]
    [InlineData(ErrorCodes.SourceNoText)]
    [InlineData(ErrorCodes.SourceDomainNotAllowed)]
    public void Failed_KnownErrorCode_IsKept(string code)
    {
        var result = Fetch(Failed(code));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(new FailedResult(code, 503));
    }

    [Theory]
    [InlineData("totally_made_up")]
    [InlineData("internal_error")]
    [InlineData("SOURCE_NO_TEXT")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("source_no_text\0")]
    public void Failed_UnknownErrorCode_CoercedToSourceUnreachable(string? code) =>
        Fetch(Failed(code)).Value.ShouldBe(new FailedResult(ErrorCodes.SourceUnreachable, 503));

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(-1)]
    public void Failed_HttpStatusOutOfRange_IsDropped(int status) =>
        Fetch(Failed(ErrorCodes.SourceUnreachable, status)).Value.ShouldBe(new FailedResult(ErrorCodes.SourceUnreachable, null));

    [Fact]
    public void Failed_FindSourcesTask_IsGenerationFailed() =>
        FetchTaskResultValidator.Interpret(FetchTaskKind.FindSources, Failed("x"), Limits, Psl, Blocklist)
            .Value.ShouldBe(new FailedResult(ErrorCodes.FindSourcesGenerationFailed, 503));

    [Fact]
    public void Candidates_OverFifty_Rejected()
    {
        var candidates = Enumerable.Range(0, 51)
            .Select(i => new FetchCandidateRequest($"https://s{i}.example.com/a", "t", "s"))
            .ToArray();

        var result = Find(Found(candidates));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ValidationFailed);
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe("candidates");
    }

    [Fact]
    public void Candidates_ExactlyFifty_Accepted()
    {
        var candidates = Enumerable.Range(0, 50)
            .Select(i => new FetchCandidateRequest($"https://s{i}.example.com/a", "t", "s"))
            .ToArray();

        Find(Found(candidates)).Value.ShouldBeOfType<CandidatesResult>().Candidates.Count.ShouldBe(50);
    }

    [Fact]
    public void Candidates_Missing_Rejected()
    {
        var result = Find(Found() with { Candidates = null });

        result.IsError.ShouldBeTrue();
        result.FirstError.Metadata![ErrorMetadata.Field].ShouldBe("candidates");
    }

    [Fact]
    public void Candidates_InvalidUrl_Dropped()
    {
        var result = Find(Found(
            new FetchCandidateRequest("https://good.example.com/a", "Good", "Site"),
            new FetchCandidateRequest("javascript:alert(1)", "Bad", null),
            new FetchCandidateRequest("http://10.0.0.1/", null, null),
            new FetchCandidateRequest(null, "NoUrl", null),
            null,
            new FetchCandidateRequest("https://also.example.com/b", "\u0001  ", " Site2 ")));

        var accepted = result.Value.ShouldBeOfType<CandidatesResult>().Candidates;
        accepted.Count.ShouldBe(2);
        accepted[0].ShouldBe(new FetchCandidate("https://good.example.com/a", "Good", "Site"));
        accepted[1].Url.ShouldBe("https://also.example.com/b");
        accepted[1].Title.ShouldBeNull();
        accepted[1].Site.ShouldBe("Site2");
    }

    [Fact]
    public void Candidates_AllInvalid_IsAnEmptyListNotAnError()
    {
        var result = Find(Found(new FetchCandidateRequest("nope", null, null)));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<CandidatesResult>().Candidates.ShouldBeEmpty();
    }
}
