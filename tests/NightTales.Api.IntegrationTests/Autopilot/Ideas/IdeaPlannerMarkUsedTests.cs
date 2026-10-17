using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Text;
using Shouldly;
using Xunit;

using static NightTales.Api.IntegrationTests.Autopilot.Ideas.IdeaPlannerHarness;

namespace NightTales.Api.IntegrationTests.Autopilot.Ideas;

/// <summary>
/// The pick-to-episode half of 02.8.1-T1 (<c>MarkUsedAsync</c>, plan §4 U4): the one transition 02.8.3-T1's
/// episode creation drives, its two conflicts and its two not-found shapes, all against the real partial unique
/// index <c>UX_episode_ideas_episode</c>.
/// </summary>
/// <remarks>
/// The rows are written through the Domain aggregate (<c>Proposed → Picked</c>) instead of through the planner, so
/// these tests start exactly where T2's pick leaves off. The conflicts are told apart by which guard fired: an
/// idea that is not picked is refused by the aggregate before any SQL, while a second idea for an episode the
/// planner already linked is refused by the database — and the planner must answer both with the same 409 class
/// without leaving the caller's tracked model corrupted.
/// </remarks>
public sealed class IdeaPlannerMarkUsedTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>
    /// The acceptance check's sequence: the first <c>MarkUsed</c> links the picked idea to the episode, and a
    /// second idea for the same episode is refused by the unique index as a 409 — the first link survives, the
    /// refused idea stays <c>picked</c>, and the caller's own flush does not replay the refusal.
    /// </summary>
    [Fact]
    public async Task MarkUsed_SetsStatusAndEpisode_ThenSecondCallReturnsConflict()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var episode = await SeedEpisodeAsync(Database, world);
        var first = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng thứ nhất");
        var second = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng thứ hai");

        // No scripted outcome: the transition never reaches the provider.
        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            var used = await scope.Planner.MarkUsedAsync(first, episode, Ct);

            used.IsError.ShouldBeFalse();
            IdeaState(Database, first).ShouldBe($"used|{Pg(episode)}", "the first idea owns the episode now");

            var conflict = await scope.Planner.MarkUsedAsync(second, episode, Ct);

            conflict.IsError.ShouldBeTrue();
            conflict.FirstError.Type.ShouldBe(ErrorType.Conflict);
            conflict.FirstError.Code.ShouldBe(
                EpisodeIdea.InvalidStateCode,
                "both refusals are one class of answer: the state of the idea/episode pair forbids the transition");

            // The refused update was attached to the caller's own tracked model; its flush must not replay it.
            await scope.Db.SaveChangesAsync(Ct);
            await scope.Db.SaveChangesAsync(Ct);
        }

        IdeaState(Database, first).ShouldBe($"used|{Pg(episode)}");
        IdeaState(Database, second).ShouldBe("picked|-", "the refused idea was neither consumed nor corrupted");
    }

    /// <summary>
    /// The aggregate's own refusal: an idea that is already <c>used</c> cannot be used again, even for the very
    /// episode it points at — the same 409 code the database guard answers with.
    /// </summary>
    [Fact]
    public async Task MarkUsed_SameIdeaTwice_ReturnsInvalidStateConflict()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var episode = await SeedEpisodeAsync(Database, world);
        var idea = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng dùng hai lần");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            (await scope.Planner.MarkUsedAsync(idea, episode, Ct)).IsError.ShouldBeFalse();

            var again = await scope.Planner.MarkUsedAsync(idea, episode, Ct);

            again.IsError.ShouldBeTrue();
            again.FirstError.Type.ShouldBe(ErrorType.Conflict);
            again.FirstError.Code.ShouldBe(EpisodeIdea.InvalidStateCode, "a used idea is not picked any more");
        }

        IdeaState(Database, idea).ShouldBe($"used|{Pg(episode)}");
    }

    /// <summary>An unknown idea answers <c>not_found</c> (09 README D10), and touching nothing else.</summary>
    [Fact]
    public async Task MarkUsed_UnknownIdea_ReturnsNotFound()
    {
        var world = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var episode = await SeedEpisodeAsync(Database, world);
        var known = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng có thật");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            var missing = await scope.Planner.MarkUsedAsync(Guid.CreateVersion7(), episode, Ct);

            missing.IsError.ShouldBeTrue();
            missing.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        }

        IdeaState(Database, known).ShouldBe("picked|-", "the untouched sibling is still picked");
    }

    /// <summary>
    /// Another user's idea is <c>not_found</c>, never a 409: the tenant filter of the caller's scope hides it, so
    /// the planner cannot even see that the id exists (09 README D10).
    /// </summary>
    [Fact]
    public async Task MarkUsed_OtherUsersIdea_ReturnsNotFound()
    {
        var world = await SeedAsync(Database);
        var other = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var otherRun = await CreateRunAsync(Database, other);
        var episode = await SeedEpisodeAsync(Database, world);
        var foreign = await SeedPickedIdeaAsync(Database, other, otherRun.GenerationRunId, "Ý tưởng của người khác");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            var result = await scope.Planner.MarkUsedAsync(foreign, episode, Ct);

            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
            result.FirstError.Type.ShouldBe(ErrorType.NotFound, "no leak: not a conflict, not a forbidden");
        }

        IdeaState(Database, foreign).ShouldBe("picked|-", "the other user's idea is untouched");
        RunCount(Database, other.SeriesId).ShouldBe(1);
    }

    /// <summary>
    /// Another user's episode cannot be linked even through the caller's own idea: the same-owner key
    /// (<c>episode_id, user_id</c> → <c>episodes (id, user_id)</c>) refuses the write, so a cross-tenant link is
    /// impossible and the idea is left <c>picked</c>. The refusal surfaces as the same <c>not_found</c> a lookup of
    /// that episode would answer — never a 409 and never a raw provider error — so the planner maps it through
    /// <see cref="EpisodeIdeaConflict.IsEpisodeForeignKeyViolation"/> and recovers the entry before the caller's
    /// own flush.
    /// </summary>
    [Fact]
    public async Task MarkUsed_AnotherUsersEpisode_CannotBeLinked()
    {
        var world = await SeedAsync(Database);
        var other = await SeedAsync(Database);
        var run = await CreateRunAsync(Database, world);
        var foreignEpisode = await SeedEpisodeAsync(Database, other);
        var idea = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng của tôi");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            var result = await scope.Planner.MarkUsedAsync(idea, foreignEpisode, Ct);

            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
            result.FirstError.Type.ShouldBe(ErrorType.NotFound, "no leak: not a conflict, not a forbidden");

            // The refused link was attached to the caller's own tracked model; its flush must not replay it.
            await scope.Db.SaveChangesAsync(Ct);
            await scope.Db.SaveChangesAsync(Ct);
        }

        IdeaState(Database, idea).ShouldBe("picked|-", "the cross-tenant link was refused, the idea is not consumed");
    }

    /// <summary>
    /// SEC-4: the same-owner key ties the link to the owner, not to the series, so an episode of another series of
    /// the same user would pass it and corrupt this series' corpus. The planner resolves the episode inside the
    /// idea's own series first and answers the same <c>not_found</c> a lookup would, leaving the idea <c>picked</c>
    /// and writing nothing the caller's flush could replay.
    /// </summary>
    [Fact]
    public async Task MarkUsed_EpisodeOfAnotherSeries_ReturnsNotFound()
    {
        var world = await SeedAsync(Database);
        var otherSeries = await SeedOtherSeriesAsync(Database, world);
        var run = await CreateRunAsync(Database, world);
        var foreignEpisode = await SeedEpisodeAsync(Database, world with { SeriesId = otherSeries });
        var idea = await SeedPickedIdeaAsync(Database, world, run.GenerationRunId, "Ý tưởng của series này");

        await using var worker = await StartWorkerAsync(Database, new FakeTextClient());

        using (var scope = OpenScope(worker, world.UserId))
        {
            var result = await scope.Planner.MarkUsedAsync(idea, foreignEpisode, Ct);

            result.IsError.ShouldBeTrue();
            result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
            result.FirstError.Type.ShouldBe(ErrorType.NotFound, "another series of the same owner reads as no episode");

            await scope.Db.SaveChangesAsync(Ct);
        }

        IdeaState(Database, idea).ShouldBe("picked|-", "the refused link left the idea untouched");
    }
}
