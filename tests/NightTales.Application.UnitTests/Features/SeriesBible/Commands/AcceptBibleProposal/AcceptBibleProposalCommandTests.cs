using NightTales.Application.Features.SeriesBible.Commands.AcceptBibleProposal;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible.Commands.AcceptBibleProposal;

/// <summary>
/// The audit projection of an accepted proposal (02.6.3-T2, plan §4 U4, D13/D19): the ids, the two versions and
/// the applied/skipped counts — the same five keys the autopilot auto-accept of the job writes for the action,
/// so one action has one <c>data</c> shape whoever wrote it.
/// </summary>
public sealed class AcceptBibleProposalCommandTests
{
    private static readonly Guid SeriesId = Guid.Parse("0197c000-0000-7000-8000-000000000001");
    private static readonly Guid EpisodeId = Guid.Parse("0197c000-0000-7000-8000-0000000000a1");

    [Fact]
    public void ToAuditData_WithoutAnOutcome_WritesNothing()
    {
        // A refused accept (409) leaves no row, and the projection it never runs would name no write either.
        new AcceptBibleProposalCommand(SeriesId).ToAuditData().ShouldBeNull();
    }

    [Fact]
    public void ToAuditData_ProjectsTheSeriesVersionsCountsAndEpisode()
    {
        var command = new AcceptBibleProposalCommand(SeriesId)
        {
            Outcome = new AcceptBibleProposalOutcome(
                FromVersion: 3,
                ToVersion: 4,
                Applied: 2,
                Skipped: 1,
                ProposedFromEpisodeId: EpisodeId),
        };

        var data = command.ToAuditData();

        data.ShouldNotBeNull();
        data.SubjectUserId.ShouldBeNull("a self-service command records the caller as the subject");
        data.EntityType.ShouldBe("series");
        data.EntityId.ShouldBe(SeriesId);
        data.Summary.Keys.ShouldBe(
            ["seriesId", "fromVersion", "toVersion", "applied", "skipped", "proposedFromEpisodeId"],
            ignoreOrder: true);

        // Ids go on the row as strings, the way the bible save's own projection writes them.
        data.Summary["seriesId"].ShouldBe(SeriesId.ToString("D"));
        data.Summary["fromVersion"].ShouldBe(3);
        data.Summary["toVersion"].ShouldBe(4);
        data.Summary["applied"].ShouldBe(2);
        data.Summary["skipped"].ShouldBe(1);
        data.Summary["proposedFromEpisodeId"].ShouldBe(EpisodeId.ToString("D"));
    }
}
