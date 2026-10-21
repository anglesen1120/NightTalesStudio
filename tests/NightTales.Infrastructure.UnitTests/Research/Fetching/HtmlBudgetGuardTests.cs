using NightTales.Domain.Research;
using NightTales.Fetcher.Core.Research.Extraction;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

/// <summary>
/// The guard measures the DOM AngleSharp really builds (CR-01, CR-05, F1), so every payload below is one the old
/// tokenizer approximation under-counted: it must be rejected at the same caps as plain nesting.
/// </summary>
public sealed class HtmlBudgetGuardTests
{
    private const int Depth = 512;
    private const int Elements = 50_000;

    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static void ShouldBeTooLarge(string html, int maxElements = Elements, int maxDepth = Depth) =>
        Should.Throw<SourceFetchException>(() => HtmlBudgetGuard.Check(html, maxElements, maxDepth)).Error.Code.ShouldBe(SourceErrors.SourceTooLargeCode);

    [Fact]
    public void NormalDocument_Passes() =>
        Should.NotThrow(() => HtmlBudgetGuard.Check("<html><head><title>x</title></head><body><p>a<br>b</p><img src=a></body></html>", 100, 10));

    [Fact]
    public void DeepNesting_Throws() => ShouldBeTooLarge(Repeat("<div>", 100_000), maxElements: 1_000_000);

    [Fact]
    public void ManyElements_Throws() => ShouldBeTooLarge(Repeat("<span></span>", 51), maxElements: 50);

    [Fact]
    public void ClosedSiblings_DoNotAccumulateDepth() =>
        Should.NotThrow(() => HtmlBudgetGuard.Check(Repeat("<div></div>", 10_000), 100_000, 4));

    [Theory]
    [InlineData(510, true)]
    [InlineData(511, false)]
    public void DepthBoundary_CountsHtmlAsTheFirstLevel(int divs, bool passes)
    {
        // html > body > N divs: depth N + 2, against a cap of 512.
        var html = Repeat("<div>", divs);

        if (passes)
        {
            Should.NotThrow(() => HtmlBudgetGuard.Check(html, Elements, Depth));
        }
        else
        {
            ShouldBeTooLarge(html);
        }
    }

    [Fact]
    public void AngleBracketsInsideScriptCommentsAndAttributes_AreNotElements()
    {
        var html = "<script>if (a<b) { x = '<div><div><div>'; }</script><!-- <p><p><p> --><a title=\"<b><b><b>\" href=x>t</a>";

        // html, head, body, script, a: five elements; html > body > a is three deep.
        Should.NotThrow(() => HtmlBudgetGuard.Check(html, 5, 3));
    }

    [Fact]
    public void ImplicitlyClosedSiblings_AreMeasuredOnTheRealTree()
    {
        // The parser closes each <li> before opening the next: 2,000 items are 2,000 elements but only a few levels deep.
        var html = Repeat("<li>item", 2_000);

        Should.NotThrow(() => HtmlBudgetGuard.Check(html, 100_000, Depth));
        ShouldBeTooLarge(html, maxElements: 100);
    }

    // CR-01 / F1(a): the self-closing flag is ignored on non-void HTML elements, so each <div/> stays open.
    [Fact]
    public void SelfClosingNonVoidElements_NestLikeOpenOnes() => ShouldBeTooLarge(Repeat("<div/>", 1_000));

    [Fact]
    public void SelfClosingNonVoidElements_AtTheDefaultCaps_AreRejectedWithoutACrash() => ShouldBeTooLarge(Repeat("<div/>", 60_000));

    [Fact]
    public void UnquotedAttributeEndingInSlash_DoesNotHideTheElement() => ShouldBeTooLarge(Repeat("<div class=a/>", 1_000));

    // F1(b): inside <svg>, <script> is not raw text; the first <div> breaks out of foreign content and nests in HTML.
    [Fact]
    public void ScriptInsideSvg_IsNotSkippedAsRawText() =>
        ShouldBeTooLarge("<svg><script>" + Repeat("<div>", 1_000) + "</script>");

    // CR-05 / F1(c): `<!-->`, `<!--->` and `--!>` close a comment, so what follows is markup.
    [Theory]
    [InlineData("<!-->")]
    [InlineData("<!--->")]
    [InlineData("<!-- note --!>")]
    public void AbruptlyClosedComments_DoNotSwallowTheMarkupAfterThem(string comment) =>
        ShouldBeTooLarge(comment + Repeat("<div>", 1_000) + "<!-- -->");

    // F1(d): `</div.x>` names an end tag nothing matches, so the parser ignores it and the div stays open.
    [Fact]
    public void MismatchedEndTagNames_DoNotPopTheStack() => ShouldBeTooLarge(Repeat("<div></div.x>", 1_000));

    // F1(d): <object> is a scope marker, so `</div>` inside it closes nothing and each block adds three levels.
    [Fact]
    public void ObjectScopeMarker_StopsEndTagsFromClosing() => ShouldBeTooLarge(Repeat("<div><object><span></div>", 200));

    // F1(e): a quote in an attribute name does not open a quoted value; the tag ends at the first '>'.
    [Fact]
    public void QuoteInAttributeName_DoesNotSwallowFollowingMarkup() =>
        ShouldBeTooLarge("<a b'>" + Repeat("<div>", 1_000) + "'>");

    // A <template>'s children live in a content fragment, not in ChildNodes; the depth walk must still enter it.
    [Fact]
    public void NestingInsideTemplateContent_IsMeasured() =>
        ShouldBeTooLarge("<template>" + Repeat("<div>", 1_000) + "</template>");

    [Fact]
    public void ElementCap_StopsTheParseBeforeTheWholeDocumentIsBuilt() =>
        ShouldBeTooLarge(Repeat("<p>x</p>", 500_000), maxElements: 1_000, maxDepth: 100_000);

    [Fact]
    public void Parse_ReturnsTheCheckedDocument()
    {
        using var document = HtmlBudgetGuard.Parse("<html><body><p>hello</p></body></html>", 100, 10);

        document.Body!.TextContent.ShouldBe("hello");
    }
}
