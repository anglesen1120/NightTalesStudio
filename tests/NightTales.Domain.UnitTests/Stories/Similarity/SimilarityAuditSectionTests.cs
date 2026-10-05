using System.Text.Json.Nodes;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Similarity;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Similarity;

public sealed class SimilarityAuditSectionTests
{
    private static readonly string[] RootKeys = ["score", "n", "corpus", "segments"];
    private static readonly string[] SegmentKeys = ["segment", "share"];

    [Fact]
    public void ToAuditSection_MatchesD22Shape()
    {
        var result = new SimilarityResult(0.12345, 10, SimilarityScorer.SourcesCorpus, [new SegmentOverlap("p:0", 0.87655)]);
        var section = result.ToAuditSection().ShouldBeOfType<JsonObject>();
        section.Select(pair => pair.Key).ShouldBe(RootKeys);
        section["score"]!.GetValue<double>().ShouldBe(0.1235);
        section["n"]!.GetValue<int>().ShouldBe(10);
        section["corpus"]!.GetValue<string>().ShouldBe("sources");
        var segment = section["segments"]!.AsArray().ShouldHaveSingleItem().ShouldBeOfType<JsonObject>();
        segment.Select(pair => pair.Key).ShouldBe(SegmentKeys);
        segment["segment"]!.GetValue<string>().ShouldBe("p:0");
        segment["share"]!.GetValue<double>().ShouldBe(0.8766);
        section.ToJsonString().ShouldBe("{\"score\":0.1235,\"n\":10,\"corpus\":\"sources\",\"segments\":[{\"segment\":\"p:0\",\"share\":0.8766}]}");
    }

    [Fact]
    public void ToAuditSection_NoContentBeyondAddresses()
    {
        var text = SimilarityFixtures.EnglishSource;
        var id = Guid.Parse("c7546f82-4cc2-4417-97b4-05f272d73081");
        var corpus = new CorpusText(id, text);
        var result = SimilarityScorer.Score(SimilarityFixtures.Story(text), [corpus], SimilarityFixtures.Options(Language.En));
        var section = result.ToAuditSection().ShouldBeOfType<JsonObject>();
        var serialized = section.ToJsonString();
        serialized.ShouldNotContain(text);
        serialized.ShouldNotContain("lantern");
        serialized.ShouldNotContain(id.ToString());
        section.Select(pair => pair.Key).ShouldBe(RootKeys);
        foreach (var node in section["segments"]!.AsArray())
        {
            node.ShouldBeOfType<JsonObject>().Select(pair => pair.Key).ShouldBe(SegmentKeys);
        }
    }

    [Fact]
    public void CorpusText_ToString_ContainsOnlySourceId()
    {
        var id = Guid.Parse("c7546f82-4cc2-4417-97b4-05f272d73081");
        new CorpusText(id, "private source prose").ToString().ShouldBe($"CorpusText {{ SourceId = {id} }}");
    }

    [Fact]
    public void ToAuditSection_EmptySegments_IsAnArray()
    {
        var section = new SimilarityResult(0, 8, SimilarityScorer.SourcesCorpus, []).ToAuditSection().ShouldBeOfType<JsonObject>();
        section["segments"]!.AsArray().ShouldBeEmpty();
    }
}
