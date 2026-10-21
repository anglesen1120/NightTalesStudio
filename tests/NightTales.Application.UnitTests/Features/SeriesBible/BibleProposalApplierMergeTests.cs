using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

// The test namespace ends in `SeriesBible`, which shadows the Domain entity's name inside it, so both the entity
// and the aggregate are aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// A merged proposal can carry summaries of more than one episode (plan §4 U3, ruling R2): each summary op binds to
/// its own episode, and the applier keeps or skips it on that episode's source alone.
/// </summary>
public sealed class BibleProposalApplierMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.Parse("0197b000-0000-7000-8000-000000000002");
    private static readonly Guid EpisodeId = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");
    private static readonly Guid EpisodeTwo = Guid.Parse("0197b000-0000-7000-8000-0000000000a2");

    private static readonly StoryStructureTags Structure = new(
        "haunted_house", "rural_house", "night", "revenant", "first_person", ["setup", "clue", "twist", "reveal"]);

    private static readonly StoryStructureTags OtherStructure = new(
        "haunted_house", "river_house", "storm", "confession", "third_person", ["setup", "clue", "twist", "reveal"]);

    private static readonly BibleLimits Limits = new();

    [Fact]
    public void Apply_TwoSummaryOpsOfTwoEpisodes_BindsEachToItsOwnEpisode()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(
            Proposal(
                Summary("Căn nhà cuối hành lang.", "Tập một", EpisodeId),
                Summary("Đêm mưa bên bờ sông.", "Tập hai", EpisodeTwo)),
            EpisodeId,
            clock);

        var result = Applier(clock).Apply(series, bible, Sources(), UserId);

        result.Applied.Count.ShouldBe(2, "both summaries have a source of their own");
        result.Skipped.ShouldBe(0);
        bible.Content.EpisodeSummaries.Count.ShouldBe(2, "a summary of one episode never replaces another's");

        var first = bible.Content.EpisodeSummaries.Single(summary => summary.EpisodeId == EpisodeId);
        first.Title.ShouldBe("Tập một");
        first.Structure.ShouldBe(Structure);
        first.ApprovedAt.ShouldBe(Now.AddDays(-1));

        var second = bible.Content.EpisodeSummaries.Single(summary => summary.EpisodeId == EpisodeTwo);
        second.Title.ShouldBe("Tập hai");
        second.Summary.ShouldBe("Đêm mưa bên bờ sông.");
        second.Structure.ShouldBe(OtherStructure, "the structure comes from the episode the op names");
        second.ApprovedAt.ShouldBe(Now.AddDays(-2));
    }

    [Fact]
    public void Apply_SummaryOpOfAnEpisodeWithoutASource_IsSkippedAndTheOtherSummaryKept()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        var unknownEpisode = Guid.Parse("0197b000-0000-7000-8000-0000000000a3");
        bible.SetProposal(
            Proposal(
                Summary("Đêm mưa bên bờ sông.", "Tập hai", EpisodeTwo),
                Summary("Một tập đã bị mở lại.", "Tập lạ", unknownEpisode)),
            EpisodeId,
            clock);

        var result = Applier(clock).Apply(series, bible, Sources(), UserId);

        // The episode the op names has no approval left to derive its structure and approved_at from: the op is
        // skipped and counted, and the summary of the episode that still has a source is kept.
        result.Applied.Count.ShouldBe(1);
        result.Skipped.ShouldBe(1, "applied + skipped still reconciles with the proposal's ops");
        bible.Content.EpisodeSummaries.Count.ShouldBe(1);
        bible.Content.EpisodeSummaries.Single().EpisodeId.ShouldBe(EpisodeTwo);
        bible.Content.EpisodeSummaries.ShouldNotContain(summary => summary.EpisodeId == unknownEpisode);
    }

    [Fact]
    public void Apply_SummaryOpWithoutAnEpisode_BindsToTheEpisodeTheProposalCameFrom()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);

        // A proposal staged before the op carried an episode: the summary falls back to the episode it came from.
        bible.SetProposal(
            Proposal(Summary("Căn nhà cuối hành lang.", "Tập một", episodeId: null)),
            EpisodeId,
            clock);

        var result = Applier(clock).Apply(series, bible, Sources(), UserId);

        result.Applied.Count.ShouldBe(1);
        result.Skipped.ShouldBe(0);
        bible.Content.EpisodeSummaries.Single().EpisodeId.ShouldBe(EpisodeId);
    }

    private static BibleProposalApplier Applier(IClock clock) => new(Limits, clock);

    /// <summary>The one series and the one bible row an apply moves.</summary>
    private static (SeriesAggregate Series, SeriesBibleEntity Bible) World(IClock clock)
    {
        var series = Series();

        return (series, SeriesBibleEntity.CreateDefault(series.Id, UserId, clock));
    }

    private static BibleProposal Proposal(params BibleOp[] ops) => new(ops, SeriesBibleEntity.InitialVersion, Now);

    /// <summary>Two approved episodes, each with its own structure and approval time.</summary>
    private static IReadOnlyList<BibleSummarySource> Sources() =>
    [
        new(EpisodeId, Structure, Now.AddDays(-1)),
        new(EpisodeTwo, OtherStructure, Now.AddDays(-2)),
    ];

    private static BibleOp Summary(string summary, string title, Guid? episodeId) =>
        new(BibleOpKind.AddSummary, summary, title, null, null, null, episodeId);

    private static SeriesAggregate Series() => SeriesAggregate.Create(
        UserId,
        new SeriesDraft(
            "Chuỗi thử nghiệm",
            Topic.Ghost,
            Language.Vi,
            Aspect.Landscape,
            "nts-test-voice-vi",
            Guid.NewGuid(),
            null),
        Now);
}
