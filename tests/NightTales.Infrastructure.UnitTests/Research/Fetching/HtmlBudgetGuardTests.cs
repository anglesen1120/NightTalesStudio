using System.Text;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research.Extraction;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research.Fetching;

public sealed class HtmlBudgetGuardTests
{
    [Fact]
    public void NormalDocument_Passes() =>
        Should.NotThrow(() => HtmlBudgetGuard.Check("<html><head><title>x</title></head><body><p>a<br>b</p><img src=a></body></html>", 100, 10));

    [Fact]
    public void DeepNesting_Throws()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 100_000));

        Should.Throw<SourceFetchException>(() => HtmlBudgetGuard.Check(html, 1_000_000, 512)).Error.Code.ShouldBe(SourceErrors.SourceTooLargeCode);
    }

    [Fact]
    public void ManyElements_Throws()
    {
        var html = string.Concat(Enumerable.Repeat("<span></span>", 51));

        Should.Throw<SourceFetchException>(() => HtmlBudgetGuard.Check(html, 50, 512));
    }

    [Fact]
    public void ClosedSiblings_DoNotAccumulateDepth() =>
        Should.NotThrow(() => HtmlBudgetGuard.Check(string.Concat(Enumerable.Repeat("<div></div>", 10_000)), 100_000, 4));

    [Fact]
    public void AngleBracketsInsideScriptCommentsAndAttributes_AreNotElements()
    {
        var html = "<script>if (a<b) { x = '<div><div><div>'; }</script><!-- <p><p><p> --><a title=\"<b><b><b>\" href=x>t</a>";

        Should.NotThrow(() => HtmlBudgetGuard.Check(html, 3, 2));
    }

    [Fact]
    public void ImplicitlyClosedSiblings_AreCountedConservatively_AndStillBounded()
    {
        var html = new StringBuilder();
        for (var i = 0; i < 2_000; i++)
        {
            html.Append("<li>item");
        }

        Should.Throw<SourceFetchException>(() => HtmlBudgetGuard.Check(html.ToString(), 100_000, 512));
    }
}
