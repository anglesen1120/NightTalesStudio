using NightTales.Application.Features.SeriesBible.Commands.DismissBibleProposal;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible.Commands.DismissBibleProposal;

/// <summary>
/// The audit projection of a dismissed proposal (02.6.3-T2, plan §4 U3, D13/D19): the series, how many ops were
/// thrown away and the episode they came from — never an op's text.
/// </summary>
public sealed class DismissBibleProposalCommandTests
{
    private static readonly Guid SeriesId = Guid.Parse("0197c000-0000-7000-8000-000000000001");
    private static readonly Guid EpisodeId = Guid.Parse("0197c000-0000-7000-8000-0000000000a1");

    [Fact]
    public void ToAuditData_WithoutAnOutcome_WritesNothing()
    {
        new DismissBibleProposalCommand(SeriesId).ToAuditData().ShouldBeNull();
    }

    [Fact]
    public void ToAuditData_ProjectsTheSeriesTheCountAndTheEpisode()
    {
        var command = new DismissBibleProposalCommand(SeriesId)
        {
            Outcome = new DismissBibleProposalOutcome(Ops: 4, ProposedFromEpisodeId: EpisodeId),
        };

        var data = command.ToAuditData();

        data.ShouldNotBeNull();
        data.SubjectUserId.ShouldBeNull("a self-service command records the caller as the subject");
        data.EntityType.ShouldBe("series");
        data.EntityId.ShouldBe(SeriesId);
        data.Summary.Keys.ShouldBe(["seriesId", "ops", "proposedFromEpisodeId"], ignoreOrder: true);
        data.Summary["seriesId"].ShouldBe(SeriesId.ToString("D"));
        data.Summary["ops"].ShouldBe(4);
        data.Summary["proposedFromEpisodeId"].ShouldBe(EpisodeId.ToString("D"));
    }
}
