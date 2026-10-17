using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The invariants of <see cref="EpisodeIdea"/> (02.8.1 plan §3.1/§3.3): the factory's guards, the four
/// status transitions and the conflict a second <see cref="EpisodeIdea.MarkUsed"/> answers.
/// </summary>
public sealed class EpisodeIdeaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CreateProposed_SetsTheScopeAndProposesUnscored()
    {
        var userId = Guid.CreateVersion7();
        var seriesId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var clock = new FakeClock(Now);
        var candidate = Candidate();

        var idea = EpisodeIdea.CreateProposed(seriesId, userId, runId, candidate, clock);

        idea.Id.ShouldNotBe(Guid.Empty);
        idea.UserId.ShouldBe(userId);
        idea.SeriesId.ShouldBe(seriesId);
        idea.GenerationRunId.ShouldBe(runId);
        idea.Title.ShouldBe(candidate.Title);
        idea.Logline.ShouldBe(candidate.Logline);
        idea.Hook.ShouldBe(candidate.Hook);
        idea.Setting.ShouldBe(candidate.Setting);
        idea.Structure.Tags.ShouldBeEquivalentTo(candidate.Structure with { Beats = candidate.Beats });
        idea.Structure.BeatSignature.ShouldBe("setup>clue>twist>reveal");
        idea.Status.ShouldBe(IdeaStatus.Proposed);
        idea.Score.ShouldBeNull();
        idea.Similarity.ShouldBeNull();
        idea.Novelty.ShouldBeNull();
        idea.RejectedReason.ShouldBeNull();
        idea.EpisodeId.ShouldBeNull();
        idea.CreatedAt.ShouldBe(Now);
        idea.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void CreateProposed_TrimsTheFourTexts()
    {
        var idea = Idea(
            Candidate() with { Title = "  Căn nhà bên sông  ", Logline = " Một người trở về. " });

        idea.Title.ShouldBe("Căn nhà bên sông");
        idea.Logline.ShouldBe("Một người trở về.");
    }

    [Fact]
    public void CreateProposed_WithoutSeriesOwnerOrRun_Fails()
    {
        var clock = new FakeClock(Now);
        var candidate = Candidate();

        Should.Throw<ArgumentException>(() => EpisodeIdea.CreateProposed(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), candidate, clock)).ParamName.ShouldBe("seriesId");
        Should.Throw<ArgumentException>(() => EpisodeIdea.CreateProposed(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), candidate, clock)).ParamName.ShouldBe("userId");
        Should.Throw<ArgumentException>(() => EpisodeIdea.CreateProposed(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, candidate, clock)).ParamName.ShouldBe("generationRunId");
    }

    [Theory]
    [InlineData("", "logline", "hook", "setting", "title")]
    [InlineData("title", "  ", "hook", "setting", "logline")]
    [InlineData("title", "logline", null, "setting", "hook")]
    [InlineData("title", "logline", "hook", "", "setting")]
    public void CreateProposed_BlankText_Fails(string title, string? logline, string? hook, string setting, string expectedParameter)
    {
        var candidate = Candidate() with { Title = title, Logline = logline!, Hook = hook!, Setting = setting };

        Should.Throw<ArgumentException>(() => EpisodeIdea.CreateProposed(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), candidate, new FakeClock(Now)))
            .ParamName.ShouldBe(expectedParameter);
    }

    [Fact]
    public void CreateProposed_NullCandidateOrClock_Throws()
    {
        Should.Throw<ArgumentNullException>(() => EpisodeIdea.CreateProposed(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null!, new FakeClock(Now)));
        Should.Throw<ArgumentNullException>(() => EpisodeIdea.CreateProposed(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Candidate(), null!));
    }

    [Fact]
    public void MarkPicked_Proposed_PicksAndStoresTheScore()
    {
        var clock = new FakeClock(Now);
        var idea = Idea(clock: clock);
        clock.Advance(TimeSpan.FromMinutes(1));

        idea.MarkPicked(0.8125m, clock);

        idea.Status.ShouldBe(IdeaStatus.Picked);
        idea.Score.ShouldBe(0.8125m);
        idea.UpdatedAt.ShouldBe(Now.AddMinutes(1));
        idea.CreatedAt.ShouldBe(Now, "a transition never moves created_at");
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    public void MarkPicked_ScoreOutsideZeroToOne_Throws(double score)
    {
        var idea = Idea();

        Should.Throw<ArgumentOutOfRangeException>(() => idea.MarkPicked((decimal)score, new FakeClock(Now)))
            .ParamName.ShouldBe("score");
        idea.Status.ShouldBe(IdeaStatus.Proposed);
    }

    [Fact]
    public void MarkPicked_RejectedIdea_Throws()
    {
        var idea = Idea();
        idea.MarkRejected(IdeaRejectionReason.NoveltyLexical, new FakeClock(Now));

        Should.Throw<InvalidOperationException>(() => idea.MarkPicked(0.5m, new FakeClock(Now)));
        idea.Status.ShouldBe(IdeaStatus.Rejected);
        idea.Score.ShouldBeNull();
    }

    [Fact]
    public void MarkPicked_Twice_Throws()
    {
        var idea = Idea();
        idea.MarkPicked(0.5m, new FakeClock(Now));

        Should.Throw<InvalidOperationException>(() => idea.MarkPicked(0.9m, new FakeClock(Now)));
        idea.Score.ShouldBe(0.5m, "the first score is the one the run log showed");
    }

    [Fact]
    public void MarkRejected_Proposed_RejectsWithTheReason()
    {
        var clock = new FakeClock(Now);
        var idea = Idea(clock: clock);
        clock.Advance(TimeSpan.FromMinutes(2));

        idea.MarkRejected(IdeaRejectionReason.NotBest, clock);

        idea.Status.ShouldBe(IdeaStatus.Rejected);
        idea.RejectedReason.ShouldBe(IdeaRejectionReason.NotBest);
        idea.UpdatedAt.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void MarkRejected_TwiceWithTheSameReason_IsANoOp()
    {
        var clock = new FakeClock(Now);
        var idea = Idea(clock: clock);
        idea.MarkRejected(IdeaRejectionReason.NotBest, clock);
        clock.Advance(TimeSpan.FromMinutes(5));

        idea.MarkRejected(IdeaRejectionReason.NotBest, clock);

        idea.Status.ShouldBe(IdeaStatus.Rejected);
        idea.UpdatedAt.ShouldBe(Now, "a repeated transition writes nothing");
    }

    [Fact]
    public void MarkRejected_AgainWithAnotherReason_Throws()
    {
        var idea = Idea();
        idea.MarkRejected(IdeaRejectionReason.NotBest, new FakeClock(Now));

        Should.Throw<InvalidOperationException>(() => idea.MarkRejected(IdeaRejectionReason.Duplicate, new FakeClock(Now)));
        idea.RejectedReason.ShouldBe(IdeaRejectionReason.NotBest);
    }

    [Fact]
    public void MarkRejected_BlankReason_Throws()
    {
        var idea = Idea();

        Should.Throw<ArgumentException>(() => idea.MarkRejected("  ", new FakeClock(Now))).ParamName.ShouldBe("reason");
    }

    [Fact]
    public void MarkRejected_ReasonOverFortyChars_Throws()
    {
        var idea = Idea();

        Should.Throw<ArgumentException>(() => idea.MarkRejected(new string('r', 41), new FakeClock(Now)))
            .ParamName.ShouldBe("reason");
    }

    [Fact]
    public void MarkRejected_UsedIdea_Throws()
    {
        var idea = Used();

        Should.Throw<InvalidOperationException>(() => idea.MarkRejected(IdeaRejectionReason.NotBest, new FakeClock(Now)));
    }

    [Fact]
    public void SetSignals_StoresScoreSimilarityAndNoveltyWithoutChangingStatus()
    {
        var clock = new FakeClock(Now);
        var idea = Idea(clock: clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        var novelty = Novelty();

        idea.SetSignals(0.7m, 0.22m, novelty, clock);

        idea.Status.ShouldBe(IdeaStatus.Proposed, "the picker decides the status; signals are measurement");
        idea.Score.ShouldBe(0.7m);
        idea.Similarity.ShouldBe(0.22m);
        idea.Novelty.ShouldBeSameAs(novelty);
        idea.UpdatedAt.ShouldBe(Now.AddMinutes(3));
    }

    [Fact]
    public void SetSignals_OutsideZeroToOne_Throws()
    {
        var idea = Idea();
        var novelty = Novelty();

        Should.Throw<ArgumentOutOfRangeException>(() => idea.SetSignals(1.1m, 0m, novelty, new FakeClock(Now)))
            .ParamName.ShouldBe("score");
        Should.Throw<ArgumentOutOfRangeException>(() => idea.SetSignals(0m, -0.1m, novelty, new FakeClock(Now)))
            .ParamName.ShouldBe("similarity");
        Should.Throw<ArgumentNullException>(() => idea.SetSignals(0.5m, 0.1m, null!, new FakeClock(Now)));
    }

    [Fact]
    public void MarkUsed_PickedIdea_SetsStatusAndEpisode()
    {
        var clock = new FakeClock(Now);
        var idea = Idea(clock: clock);
        idea.MarkPicked(0.8m, clock);
        clock.Advance(TimeSpan.FromMinutes(4));
        var episodeId = Guid.CreateVersion7();

        var result = idea.MarkUsed(episodeId, clock);

        result.IsError.ShouldBeFalse();
        idea.Status.ShouldBe(IdeaStatus.Used);
        idea.EpisodeId.ShouldBe(episodeId);
        idea.UpdatedAt.ShouldBe(Now.AddMinutes(4));
    }

    [Fact]
    public void MarkUsed_SecondCall_ReturnsConflict()
    {
        var idea = Used();

        var second = idea.MarkUsed(Guid.CreateVersion7(), new FakeClock(Now));

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(EpisodeIdea.InvalidStateCode);
        second.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        idea.EpisodeId.ShouldNotBeNull("the first episode stays the one the idea became");
    }

    [Fact]
    public void MarkUsed_ProposedOrRejectedIdea_ReturnsConflict()
    {
        var proposed = Idea();
        var rejected = Idea();
        rejected.MarkRejected(IdeaRejectionReason.PolicyModerated, new FakeClock(Now));

        proposed.MarkUsed(Guid.CreateVersion7(), new FakeClock(Now)).FirstError.Code
            .ShouldBe(EpisodeIdea.InvalidStateCode);
        rejected.MarkUsed(Guid.CreateVersion7(), new FakeClock(Now)).FirstError.Code
            .ShouldBe(EpisodeIdea.InvalidStateCode);
        proposed.Status.ShouldBe(IdeaStatus.Proposed);
        rejected.EpisodeId.ShouldBeNull();
    }

    [Fact]
    public void MarkUsed_WithoutEpisode_Throws()
    {
        var idea = Idea();
        idea.MarkPicked(0.8m, new FakeClock(Now));

        Should.Throw<ArgumentException>(() => idea.MarkUsed(Guid.Empty, new FakeClock(Now))).ParamName.ShouldBe("episodeId");
        idea.Status.ShouldBe(IdeaStatus.Picked);
    }

    [Fact]
    public void EpisodeIdea_InvalidStateCode_IsThePlannedCatalogueCode()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (D1: ErrorOr is its only package),
        // so it spells the code itself. The plan (§4 U4) names the code idea_invalid_state; the
        // ErrorCodes.IdeaInvalidState constant and its 409 catalogue row are 02.8.1-T2's to add, and the join
        // test (SourceCandidate_NotProposedCode_MatchesTheErrorCatalogue's shape) lands with them.
        EpisodeIdea.InvalidStateCode.ShouldBe("idea_invalid_state");
    }

    /// <summary>A proposed idea at <see cref="Now"/>.</summary>
    private static EpisodeIdea Idea(IdeaCandidate? candidate = null, FakeClock? clock = null) =>
        EpisodeIdea.CreateProposed(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            candidate ?? Candidate(),
            clock ?? new FakeClock(Now));

    /// <summary>A picked idea, the only status <see cref="EpisodeIdea.MarkUsed"/> accepts.</summary>
    private static EpisodeIdea Used()
    {
        var idea = Idea();
        idea.MarkPicked(0.8m, new FakeClock(Now));
        idea.MarkUsed(Guid.CreateVersion7(), new FakeClock(Now)).IsError.ShouldBeFalse();

        return idea;
    }

    private static IdeaCandidate Candidate() =>
        new(
            "Căn nhà bên sông",
            "Một người trở về căn nhà cũ và nghe tiếng gọi lúc nửa đêm.",
            "Tiếng gọi chỉ cất lên khi đèn tắt.",
            "Căn nhà gỗ hai tầng bên sông, đêm mưa.",
            new StoryStructureTags("haunted_house", "riverside_house", "night", "twin", "first_person", []),
            ["setup", "clue", "twist", "reveal"]);

    private static IdeaNoveltyDetail Novelty() =>
        new(
            new IdeaNoveltyLexical(0.22m, "episode", null),
            new IdeaNoveltyJudge(0.15m, 0.8m, 0.6m, "Ý tưởng mới.", null),
            new IdeaNoveltyCorpus(100, 2, 10));
}
