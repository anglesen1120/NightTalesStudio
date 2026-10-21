using System.Text;
using System.Text.Json;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using NightTales.Infrastructure.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Bible;

/// <summary>
/// The stored bytes and the validated bytes are the same bytes (02.6.3 plan §3.3): the two EF value converters
/// write through the source-generated <see cref="BibleJsonContext"/>, while <c>BibleContentValidator</c> counts
/// <c>MaxContentBytes</c> over the Domain's own serialiser.
/// </summary>
/// <remarks>
/// The Domain may not reference Infrastructure, so it cannot assert this equality itself; if the two ever
/// diverged, a document just under the byte budget would be stored larger than the budget allows.
/// </remarks>
public sealed class BibleJsonContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid EpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");

    [Fact]
    public void BibleJsonContext_Content_WritesTheCanonicalDomainDocument()
    {
        var content = FullContent();

        var stored = JsonSerializer.Serialize(content, BibleJsonContext.Default.SeriesBibleContent);

        stored.ShouldBe(BibleJson.Serialize(content));
        Encoding.UTF8.GetByteCount(stored).ShouldBe(BibleJson.Utf8ByteCount(content));
    }

    [Fact]
    public void BibleJsonContext_Content_ReadsBackWhatItWrote()
    {
        var content = FullContent();

        var restored = JsonSerializer.Deserialize(BibleJson.Serialize(content), BibleJsonContext.Default.SeriesBibleContent);

        restored.ShouldNotBeNull();
        BibleJson.Serialize(restored!).ShouldBe(BibleJson.Serialize(content));
        restored!.EpisodeSummaries[0].Structure.Beats.ShouldBe(["setup", "clue", "twist", "reveal"]);
    }

    [Fact]
    public void BibleJsonContext_EmptyContent_MatchesTheBackfillDocument()
    {
        var stored = JsonSerializer.Serialize(SeriesBibleContent.Empty, BibleJsonContext.Default.SeriesBibleContent);

        stored.ShouldBe(BibleJson.Serialize(SeriesBibleContent.Empty));
        stored.ShouldContain("\"schema_version\":\"bible.v1\"");
    }

    [Fact]
    public void BibleJsonContext_Proposal_KeepsTheSnakeCaseOpKinds()
    {
        var proposal = new BibleProposal(
            [
                new BibleOp(BibleOpKind.AddSummary, "Một câu chuyện.", "Tập một", null, null, "vì sao"),
                new BibleOp(BibleOpKind.AddCharacter, null, "Bà Tư", "Người giữ nhà.", null, null),
            ],
            2,
            Now);

        var stored = JsonSerializer.Serialize(proposal, BibleJsonContext.Default.BibleProposal);

        stored.ShouldBe(BibleJson.SerializeProposal(proposal));
        stored.ShouldContain("\"kind\":\"add_summary\"");
        stored.ShouldContain("\"kind\":\"add_character\"");
        stored.ShouldContain("\"based_on_version\":2");

        var restored = JsonSerializer.Deserialize(stored, BibleJsonContext.Default.BibleProposal);

        restored.ShouldNotBeNull();
        restored!.Ops.Count.ShouldBe(2);
        restored.Ops[1].Kind.ShouldBe(BibleOpKind.AddCharacter);
        stored.ShouldBe(BibleJson.SerializeProposal(restored));
    }

    private static SeriesBibleContent FullContent() => new(
        SeriesBibleContent.V1,
        new BibleNarrator("Người kể chuyện", "Kể chuyện đêm khuya cho người lớn.", "Giọng trầm, chậm."),
        [new BibleCharacter("ch1", "Bà Tư", "Người giữ căn nhà cũ.", EpisodeId)],
        [new BiblePlace("pl1", "Căn nhà bên sông", "Nhà gỗ hai tầng.")],
        [new BibleRule("rl1", "Không nói tên người chết.")],
        [new BibleEpisodeSummary(
            EpisodeId,
            "Tiếng gõ lúc nửa đêm",
            "Một người trở về căn nhà cũ và nghe thấy tiếng gõ.",
            new StoryStructureTags("haunted_house", "rural_house", "night", "revenant", "first_person", ["setup", "clue", "twist", "reveal"]),
            Now)]);
}
