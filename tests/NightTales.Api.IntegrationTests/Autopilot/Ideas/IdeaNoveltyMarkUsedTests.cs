using ErrorOr;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The T2 half of <c>MarkUsedAsync</c> (IDEA-S52-2 and IDEA-S52-3): the atomic conditional update that links a
/// picked idea to its episode, the states it refuses, and the concurrency the T1 shape could not survive. The
/// single-call shapes — first link, a second idea for the same episode, an unknown or foreign idea, a foreign
/// episode — are pinned by <c>IdeaPlannerMarkUsedTests</c>; what is added here is the transition's own contract:
/// one winning call under a race, a conflict for a state that is not <c>picked</c>, and the delivered-twice
/// behaviour of the pick the link hangs off.
/// </summary>
/// <remarks>
/// <para>
/// The transition is one <c>ExecuteUpdateAsync</c> whose <c>WHERE</c> clause is the domain rule
/// (<c>picked</c> and <c>episode_id IS NULL</c>) and whose affected-row count is the answer (IDEA-S52-3), so a
/// test can drive it from two scopes and see exactly one winner. Nothing is tracked, which is why a refused call
/// leaves the caller's own write model clean.
/// </para>
/// <para>
/// No scripted provider outcome is needed anywhere here: the transition never reaches a call.
/// </para>
/// </remarks>
public sealed class IdeaNoveltyMarkUsedTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// IDEA-S52-2: <c>MarkUsed</c> on an idea that is still <c>proposed</c> — the state the pick leaves behind
    /// until the scoring step marks the winner <c>picked</c> — is the same 409 as any other wrong state, and the
    /// row is untouched.
    /// </summary>
    [Fact]
    public async Task MarkUsed_ProposedIdea_ReturnsInvalidStateConflict()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var episode = await SeedEpisodeAsync(Database, world);
        var proposed = await SeedPendingIdeaAsync(
            Database,
            world,
            run.GenerationRunId,
            "Ý tưởng còn đề xuất",
            "Một chuyện về ngọn hải đăng bỏ hoang.");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            var refused = await scope.Planner.MarkUsedAsync(proposed, episode, Ct);

            refused.IsError.ShouldBeTrue();
            refused.FirstError.Type.ShouldBe(ErrorType.Conflict, "a state the machine forbids is a conflict, not a not-found");
            refused.FirstError.Code.ShouldBe(
                ErrorCodes.IdeaInvalidState,
                "IDEA-S52-2 joins the Domain literal with the catalogue code the 409 answers with");
            refused.FirstError.Code.ShouldBe(EpisodeIdea.InvalidStateCode);

            // The refused update was attached to nothing (the transition is one conditional UPDATE): a flush of
            // the caller's own model must not replay it.
            await scope.Db.SaveChangesAsync(Ct);
        }

        IdeaState(Database, proposed).ShouldBe("proposed|-", "the refused link wrote neither a status nor an episode");
    }

    /// <summary>
    /// The same refusal for a rejected idea, produced by the real pipeline: the batch's loser stays
    /// <c>rejected|not_best</c> and can never be linked to an episode.
    /// </summary>
    [Fact]
    public async Task MarkUsed_RejectedIdea_ReturnsInvalidStateConflict()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var episode = await SeedEpisodeAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(
            TextStep.Ideas,
            IdeaBatch(DistinctIdea("Ý tưởng được chọn"), DistinctIdea("Ý tưởng thua cuộc")),
            Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m), JudgeRow(1, 0.5m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 2);

        var pick = await ProposeAsync(worker, run);

        pick.IsError.ShouldBeFalse();
        var loser = IdeaIdOf(Database, world.SeriesId, "Ý tưởng thua cuộc");
        IdeaOutcome(Database, loser).ShouldStartWith($"rejected|{IdeaRejectionReason.NotBest}");

        using (var scope = OpenScope(worker, world.UserId))
        {
            var refused = await scope.Planner.MarkUsedAsync(loser, episode, Ct);

            refused.IsError.ShouldBeTrue();
            refused.FirstError.Type.ShouldBe(ErrorType.Conflict);
            refused.FirstError.Code.ShouldBe(ErrorCodes.IdeaInvalidState, "only the batch's winner can become an episode");
        }

        IdeaState(Database, loser).ShouldBe("rejected|-");
        IdeaState(Database, pick.Value.IdeaId).ShouldBe("picked|-", "the winner is untouched by the refused call");
    }

    /// <summary>
    /// IDEA-S52-3: two calls on the same picked idea with two different episodes, from two scopes, end with one
    /// winner. The conditional UPDATE is what makes the race safe — the loser's <c>WHERE</c> no longer matches
    /// once the winner committed, so it answers the 409 the state machine spells instead of linking a second
    /// episode or failing on a snapshot/xmin conflict.
    /// </summary>
    [Fact]
    public async Task MarkUsed_TwoParallelCallsWithDifferentEpisodes_ExactlyOneWins()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var firstEpisode = await SeedEpisodeAsync(Database, world);
        var secondEpisode = await SeedEpisodeAsync(Database, world);
        var idea = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng bị hai nơi giành");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using var owner = OpenScope(worker, world.UserId);
        using var rival = OpenScope(worker, world.UserId);

        var results = await Task.WhenAll(
            owner.Planner.MarkUsedAsync(idea, firstEpisode, Ct).AsTask(),
            rival.Planner.MarkUsedAsync(idea, secondEpisode, Ct).AsTask());

        results.Count(result => result.IsError).ShouldBe(1, "exactly one of the two calls won the link");
        var refused = results.Single(result => result.IsError);
        refused.FirstError.Type.ShouldBe(ErrorType.Conflict);
        refused.FirstError.Code.ShouldBe(ErrorCodes.IdeaInvalidState, "the idea is used: it is not picked any more");

        var winnerEpisode = results[0].IsError ? secondEpisode : firstEpisode;
        IdeaState(Database, idea).ShouldBe(
            $"used|{Pg(winnerEpisode)}",
            "episode_id is set exactly once, by whichever call committed first");
        IdeaCount(Database, world.SeriesId).ShouldBe(1, "the race produced no second idea row");

        using (var check = OpenScope(worker, world.UserId))
        {
            // The loser's refusal left nothing for its own scope's flush to replay.
            await check.Db.SaveChangesAsync(Ct);
        }

        IdeaState(Database, idea).ShouldBe($"used|{Pg(winnerEpisode)}");
    }

    /// <summary>
    /// The pick step of a run delivered twice (the at-least-once outbox of 09.2.4): each delivery stores its own
    /// batch, and the first delivery's picked row is never re-transitioned — <c>MarkPicked</c>'s same-score no-op
    /// (IDEA-S52-2) is what makes a repeated write of that row harmless, and the planner never walks an existing
    /// row through a second transition.
    /// </summary>
    [Fact]
    public async Task ProposeAndPick_DeliveredTwice_DoesNotReTransitionTheFirstPick()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var text = new FakeTextClient().EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng giao lần đầu")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        text.EnqueueOk(TextStep.Ideas, IdeaBatch(DistinctIdea("Ý tưởng giao lần hai")), Usage);
        text.EnqueueOk(TextStep.Novelty, NoveltyBatch(JudgeRow(0, 0.1m)), Usage);
        await using var worker = await StartWorkerAsync(Database, text, count: 1);

        var first = await ProposeAsync(worker, run);
        var firstOutcome = IdeaOutcome(Database, first.Value.IdeaId);

        var second = await ProposeAsync(worker, run);

        second.IsError.ShouldBeFalse();
        second.Value.IdeaId.ShouldNotBe(first.Value.IdeaId, "the second delivery picked from its own batch");
        IdeaOutcome(Database, first.Value.IdeaId).ShouldBe(firstOutcome, "the delivered-again step left the first pick as it was");
        IdeaOutcome(Database, second.Value.IdeaId).ShouldStartWith("picked|-");
        IdeaCount(Database, world.SeriesId).ShouldBe(2, "each delivery stored exactly its own candidate");
    }
}
