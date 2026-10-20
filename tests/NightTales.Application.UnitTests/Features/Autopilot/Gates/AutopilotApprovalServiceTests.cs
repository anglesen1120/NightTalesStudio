using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="AutopilotApprovalService"/>'s decisions (02.8.2 plan §4 UC2/UC4): a dry run and a verdict that did
/// not pass — or that is held for the owner by design — are refused before any command could be built, and the
/// failure code of a refused approval is the one the run log shows.
/// </summary>
/// <remarks>
/// The scope is never entered on these paths, and <see cref="RecordingSystemWork"/> counts every owner lookup: a
/// call that reaches an approve command would have to resolve the run's owner first, so <c>Queries == 0</c> is
/// the assertion that no <c>ApproveStoryCommand</c>/<c>ApproveVideoCommand</c> was sent. The live pass, the
/// refused approval and the replay are covered end to end by the <c>AutoApproveStory_*</c> integration rows,
/// which run the real pipeline and the real audit writer.
/// </remarks>
public sealed class AutopilotApprovalServiceTests
{
    private static readonly Guid RunId = Guid.Parse("018f0000-0000-7000-8000-00000000000a");

    [Fact]
    public async Task AutopilotApproval_DryRun_NeverCallsApproveCommand()
    {
        var work = new RecordingSystemWork();
        var service = Service(work);

        var outcome = await service.ApproveStoryAsync(
            RunId,
            Story(pass: true, hold: false),
            Evidence(),
            GateMode.DryRun,
            CancellationToken.None);

        outcome.IsError.ShouldBeFalse();
        outcome.Value.ShouldBe(ApprovalOutcome.DryRunSkipped);
        work.Queries.ShouldBe(0, "a dry run must not even look up the run, let alone approve it");
    }

    [Fact]
    public async Task AutopilotApproval_VideoDryRun_AlsoSendsNothing()
    {
        var work = new RecordingSystemWork();

        var outcome = await Service(work).ApproveVideoAsync(
            RunId,
            new VideoGateResult(true, Guid.CreateVersion7(), Guid.CreateVersion7(), [], []),
            GateMode.DryRun,
            CancellationToken.None);

        outcome.Value.ShouldBe(ApprovalOutcome.DryRunSkipped);
        work.Queries.ShouldBe(0);
    }

    [Fact]
    public async Task AutopilotApproval_Hold_IsRefused()
    {
        var work = new RecordingSystemWork();

        var outcome = await Service(work).ApproveStoryAsync(
            RunId,
            Story(pass: false, hold: true),
            Evidence(),
            GateMode.Live,
            CancellationToken.None);

        outcome.IsError.ShouldBeTrue("a held story is never approved, not even by a live gate");
        outcome.FirstError.Code.ShouldBe("gate_not_passed");
        work.Queries.ShouldBe(0);
    }

    [Fact]
    public async Task AutopilotApproval_NotPassingVerdict_IsRefused()
    {
        var work = new RecordingSystemWork();

        var outcome = await Service(work).ApproveStoryAsync(
            RunId,
            Story(pass: false, hold: false),
            Evidence(),
            GateMode.Live,
            CancellationToken.None);

        outcome.IsError.ShouldBeTrue("the caller bug is refused, never turned into an episode state");
        outcome.FirstError.Code.ShouldBe("gate_not_passed");
        work.Queries.ShouldBe(0);
    }

    [Fact]
    public async Task AutopilotApproval_VideoNotPassing_IsRefused()
    {
        var work = new RecordingSystemWork();

        var outcome = await Service(work).ApproveVideoAsync(
            RunId,
            new VideoGateResult(false, Guid.CreateVersion7(), Guid.CreateVersion7(), [], ["loudness"]),
            GateMode.Live,
            CancellationToken.None);

        outcome.IsError.ShouldBeTrue();
        outcome.FirstError.Code.ShouldBe("gate_not_passed");
        work.Queries.ShouldBe(0);
    }

    [Fact]
    public void ApprovalFailureCodes_MapTheRefusalOntoTheRunLog()
    {
        ApprovalFailureCodes.Of(Error.Conflict(ErrorCodes.StoryVersionConflict, "moved"))
            .ShouldBe(ApprovalFailureCodes.EtagConflict);
        ApprovalFailureCodes.Of(Error.Conflict(ErrorCodes.EpisodeConflict, "the video moved"))
            .ShouldBe(
                ApprovalFailureCodes.EtagConflict,
                "the video approval now sends the episode ETag it read, so its stale tag is the same refusal");
        ApprovalFailureCodes.Of(Error.Conflict(ErrorCodes.EpisodeInvalidState, "state"))
            .ShouldBe(ApprovalFailureCodes.ApproveFailed);
        ApprovalFailureCodes.Of(Error.Failure("anything_else", "other"))
            .ShouldBe(ApprovalFailureCodes.ApproveFailed);
    }

    private static AutopilotApprovalService Service(ISystemWorkQuery work) => new(
        work,
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        new FixedClock(),
        NullLogger<AutopilotApprovalService>.Instance);

    private static StoryGateResult Story(bool pass, bool hold) => new(
        Pass: pass,
        Hold: hold,
        Attempt: 1,
        StoryVersionId: Guid.Parse("018f0000-0000-7000-8000-00000000000b"),
        Checks: [],
        Failures: pass ? [] : [StoryGateCheckIds.SelfReviewMeanScore]);

    private static GateStoryEvidence Evidence() => new(
        SourceSimilarity: 0.01m,
        PastEpisodeSimilarity: null,
        CorpusMax: 0,
        RealWorldHit: false,
        RealWorldKinds: [],
        RealWorldSegments: [],
        ModerationFlagged: false,
        ModerationCategories: [],
        ModerationSegments: [],
        ModerationUnavailableDetail: null,
        ModerationCheckedAt: null,
        EpisodeStatusAtEvaluation: "story_review");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>The owner lookup, counted: a path that never asks for an owner never approved anything.</summary>
    private sealed class RecordingSystemWork : ISystemWorkQuery
    {
        /// <summary>How many times an owner was asked for.</summary>
        public int Queries { get; private set; }

        /// <inheritdoc />
        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
            Expression<Func<T, bool>> where,
            int batch,
            [EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            Queries++;

            await Task.CompletedTask.ConfigureAwait(false);

            yield return (RunId, Guid.Parse("018f0000-0000-7000-8000-00000000000c"));
        }
    }
}
