using System.Globalization;
using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Series.Bible;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// The pending proposal DTO of <c>GET /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U1): the snake_case
/// op kinds the panel reads and the counts its banner shows without resolving op semantics.
/// </summary>
public sealed class BibleProposalDtoTests
{
    private static readonly Guid EpisodeId = Guid.Parse("4d3a4d5f-3d4e-4f60-b172-8d9e0f1a2b3c");
    private static readonly DateTimeOffset ProposedAt =
        DateTimeOffset.Parse("2026-03-04T05:06:07Z", CultureInfo.InvariantCulture);

    [Fact]
    public void FromDomain_CountsEveryOpKindAndNamesTheSummaryEpisode()
    {
        var proposal = new BibleProposal(
            [
                new BibleOp(BibleOpKind.AddCharacter, null, "Linh", "A character.", null, "reason"),
                new BibleOp(BibleOpKind.AddPlace, null, "The pier", "A place.", null, null),
                new BibleOp(BibleOpKind.AddRule, null, null, null, "A rule.", null),
                new BibleOp(BibleOpKind.AddSummary, "The summary.", "The title", null, null, "reason"),
            ],
            3,
            ProposedAt);

        var dto = BibleProposalDto.FromDomain(proposal, EpisodeId);

        dto.BasedOnVersion.ShouldBe(3);
        dto.ProposedFromEpisodeId.ShouldBe(EpisodeId);
        dto.ProposedAt.ShouldBe(ProposedAt);
        dto.Ops.Count.ShouldBe(4);
        dto.Ops[0].Kind.ShouldBe("add_character");
        dto.Ops[1].Kind.ShouldBe("add_place");
        dto.Ops[2].Kind.ShouldBe("add_rule");
        dto.Ops[3].Kind.ShouldBe("add_summary");
        dto.Ops[3].Summary.ShouldBe("The summary.");
        dto.Ops[3].Name.ShouldBe("The title");
        dto.Preview.Characters.ShouldBe(1);
        dto.Preview.Places.ShouldBe(1);
        dto.Preview.Rules.ShouldBe(1);
        dto.Preview.SummaryForEpisodeId.ShouldBe(EpisodeId);
    }

    [Fact]
    public void FromDomain_NoSummaryOp_LeavesThePreviewEpisodeNull()
    {
        var proposal = new BibleProposal(
            [new BibleOp(BibleOpKind.AddRule, null, null, null, "A rule.", null)],
            1,
            ProposedAt);

        var dto = BibleProposalDto.FromDomain(proposal, EpisodeId);

        dto.Preview.ShouldBe(new BibleProposalPreviewDto(0, 0, 1, null));
    }

    [Fact]
    public void FromDomain_EmptyOpList_CountsNothing() =>
        BibleProposalDto
            .FromDomain(new BibleProposal([], 1, ProposedAt), null)
            .Preview.ShouldBe(new BibleProposalPreviewDto(0, 0, 0, null));

    [Fact]
    public void Wire_UnknownKind_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => BibleOpDto.Wire((BibleOpKind)99));
}
