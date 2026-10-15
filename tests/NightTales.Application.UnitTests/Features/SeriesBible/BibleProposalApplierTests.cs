using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Series.Events;
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
/// The one write path an accepted proposal takes (02.6.3-T2, plan §4 U4): the ops of the stored proposal are
/// applied onto the current content by the Domain method, ops that cannot be stored are skipped rather than
/// refused, and the new version is announced with the actor the caller names.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no pipeline. The route contract (statuses, ETag, tenant rule) belongs to the
/// Api's integration tests of the same task.
/// </remarks>
public sealed class BibleProposalApplierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.Parse("0197b000-0000-7000-8000-000000000002");
    private static readonly Guid EpisodeId = Guid.Parse("0197b000-0000-7000-8000-0000000000a1");

    private static readonly StoryStructureTags Structure = new(
        "haunted_house", "rural_house", "night", "revenant", "first_person", ["setup", "clue", "twist", "reveal"]);

    private static readonly BibleLimits Limits = new();

    [Fact]
    public void Apply_AppliesTheOps_BumpsTheVersion_ClearsTheProposalAndAnnouncesIt()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(
            Proposal(
                Character("Bà Tư"),
                Rule("Không nhắc tên người thật.")),
            EpisodeId,
            clock);

        var result = Applier(clock).Apply(series, bible, Source(), UserId);

        result.Applied.Count.ShouldBe(2, "both ops add an entry the content did not have");
        result.Skipped.ShouldBe(0);
        bible.Version.ShouldBe(2, "an applied proposal is one new content version");
        bible.Content.RecurringCharacters.Count.ShouldBe(1);
        bible.Content.RecurringCharacters[0].Id.ShouldBe("ch1", "the id is minted by the feature's own max + 1 rule");
        bible.Content.RecurringCharacters[0].Name.ShouldBe("Bà Tư");
        bible.Content.Rules[0].Text.ShouldBe("Không nhắc tên người thật.");
        bible.ProposedContent.ShouldBeNull("the proposal is cleared by the same write");
        bible.ProposedFromEpisodeId.ShouldBeNull();
        bible.UpdatedAt.ShouldBe(Now);

        // The announcement an accept shares with a manual save (plan §5): the series aggregate raises it, so the
        // outbox row joins the accept's own transaction.
        var raised = series.DomainEvents.OfType<SeriesBibleUpdated>().Single();
        raised.SeriesId.ShouldBe(series.Id, "the announcement names the series whose bible moved");
        raised.UserId.ShouldBe(UserId);
        raised.Version.ShouldBe(2);
        raised.ChangedBy.ShouldBe(UserId, "the accepting user is the actor of a manual accept");
        raised.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Apply_AnnouncesTheActorTheCallerNames()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(Proposal(Character("Bà Tư")), EpisodeId, clock);
        var owner = Guid.Parse("0197b000-0000-7000-8000-0000000000ff");

        Applier(clock).Apply(series, bible, Source(), owner);

        // The autopilot auto-accept passes the owner of the run: one path, one changed_by, whoever triggered it.
        series.DomainEvents.OfType<SeriesBibleUpdated>().Single().ChangedBy.ShouldBe(owner);
    }

    [Fact]
    public void Apply_SummaryOp_IsBoundToTheSourceEpisode()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(
            Proposal(new BibleOp(BibleOpKind.AddSummary, "Một chuyện ma nhẹ nhàng.", "Căn nhà cuối hành lang", null, null, null)),
            EpisodeId,
            clock);

        var result = Applier(clock).Apply(series, bible, Source(), UserId);

        result.Applied.Count.ShouldBe(1);
        var summary = bible.Content.EpisodeSummaries.Single();
        summary.EpisodeId.ShouldBe(EpisodeId, "the op carries no episode; the source binds it");
        summary.Title.ShouldBe("Căn nhà cuối hành lang");
        summary.Summary.ShouldBe("Một chuyện ma nhẹ nhàng.");
        summary.Structure.ShouldBe(Structure);
        summary.ApprovedAt.ShouldBe(Now.AddDays(-1));
    }

    [Fact]
    public void Apply_ForbiddenCharacterInAnOp_DropsTheOpInsteadOfStoringIt()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(
            Proposal(
                Character("Bà <script> Tư"),
                Rule("Không nhắc tên người thật.")),
            EpisodeId,
            clock);

        var result = Applier(clock).Apply(series, bible, Source(), UserId);

        // A stored name carrying `<` would make the very next PUT refuse the document the client holds, so the op
        // is dropped and counted instead of being written (02.6.3-T1 note, plan §4 U4).
        result.Applied.Count.ShouldBe(1);
        result.Skipped.ShouldBe(1, "a dropped op counts as skipped, so applied + skipped still reconciles");
        bible.Content.RecurringCharacters.ShouldBeEmpty();
        bible.Content.Rules.Count.ShouldBe(1);
        bible.Version.ShouldBe(2, "the accept still produces its version: only the op was skipped");
    }

    [Fact]
    public void Apply_WithoutASource_SkipsTheSummaryOpsAndAppliesTheRest()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(
            Proposal(
                Character("Bà Tư"),
                new BibleOp(BibleOpKind.AddSummary, "Một chuyện ma nhẹ nhàng.", "Căn nhà cuối hành lang", null, null, null)),
            EpisodeId,
            clock);

        // The episode the ops came from was reopened after the proposal was staged: there is no approval left to
        // derive a summary's structure and approved_at from, and a summary bound to an unapproved episode would be
        // refused by the next PUT (not_approved_episode), so the accept applies what it can.
        var result = Applier(clock).Apply(series, bible, source: null, UserId);

        result.Applied.Count.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        bible.Content.EpisodeSummaries.ShouldBeEmpty();
        bible.Content.RecurringCharacters.Single().Name.ShouldBe("Bà Tư");
        bible.Version.ShouldBe(2);
    }

    [Fact]
    public void Apply_WithoutAPendingProposal_Throws()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);

        var exception = Should.Throw<InvalidOperationException>(() =>
            Applier(clock).Apply(series, bible, Source(), UserId));

        exception.Message.ShouldContain("no pending proposal");
        bible.Version.ShouldBe(1, "nothing was written");
    }

    [Fact]
    public void Apply_WithoutAnActor_Throws()
    {
        var clock = new FakeClock(Now);
        var (series, bible) = World(clock);
        bible.SetProposal(Proposal(Character("Bà Tư")), EpisodeId, clock);

        Should.Throw<ArgumentException>(() => Applier(clock).Apply(series, bible, Source(), Guid.Empty))
            .ParamName.ShouldBe("changedBy");
        bible.Version.ShouldBe(1, "nothing was written");
    }

    private static BibleProposalApplier Applier(IClock clock) => new(Limits, clock);

    /// <summary>
    /// The one series and the one bible row an apply moves: the row belongs to the aggregate, so the announcement
    /// the apply raises names the series the caller passed.
    /// </summary>
    private static (SeriesAggregate Series, SeriesBibleEntity Bible) World(IClock clock)
    {
        var series = Series();

        return (series, SeriesBibleEntity.CreateDefault(series.Id, UserId, clock));
    }

    private static BibleProposal Proposal(params BibleOp[] ops) => new(ops, SeriesBibleEntity.InitialVersion, Now);

    private static BibleSummarySource Source() => new(EpisodeId, Structure, Now.AddDays(-1));

    private static BibleOp Character(string name) =>
        new(BibleOpKind.AddCharacter, null, name, "Người phụ nữ bên sông", null, "xuất hiện ở tập này");

    private static BibleOp Rule(string text) =>
        new(BibleOpKind.AddRule, null, null, null, text, "luật của bộ truyện");

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
