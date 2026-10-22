using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Application.Features.Autopilot.Orchestration.Steps;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The publication half of the run (02.8.3 plan §3.4, §3.5 rows <c>video_gate → publication_created</c> and
/// <c>waiting_owner → publication_created</c>, §3.6 rows 6 and 6b, §7.3 and §7.4, S71-19): the create window, the
/// approved-cut guard, the channel triggers, the pre-audit clamp and the four refusal mappings.
/// </summary>
/// <remarks>
/// The two callers (<c>VideoGateStep</c>, <c>WaitingOwnerStep</c>) are covered by their own suites, so the creator is
/// exercised directly: it is the one place the rule lives. Graph: the worktree is not indexed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class AutopilotPublicationCreatorTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = AutopilotStepContexts.DefaultNow;

    /// <summary>
    /// The create window opens at <c>publication_create_at</c> (2026-10-20T18:00Z for the tests' slot and settings):
    /// earlier the step answers <see cref="StepResultKind.Idle"/> and sends nothing.
    /// </summary>
    [Fact]
    public async Task Create_BeforeTheWindow_IsIdle()
    {
        var (run, creator, reader, sender) = await Arrange(EpisodeStatus.Ready);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, Now), Ct);

        result.Kind.ShouldBe(StepResultKind.Idle);
        sender.Sent.ShouldBeEmpty();
        reader.ChannelReads.ShouldBe(0, "the channel only matters once the create window is open");
    }

    /// <summary>At <c>upload_at − MinVetoWindowHours</c> the slot is gone: the run ends <c>slot_missed</c> and nothing is created.</summary>
    [Fact]
    public async Task Create_AtTheCutoff_EndsSlotMissed()
    {
        var (run, creator, _, sender) = await Arrange(EpisodeStatus.Ready);

        // 20:00Z − 2 h (publish lead) − 2 h (min veto window) = 16:00Z.
        var atCutoff = AutopilotStepContexts.DefaultSlotAt.AddHours(-4);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, atCutoff), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.SlotMissed);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
        run.Run.PauseReason.ShouldBe(AutopilotPauseReason.SlotMissed);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// A cut nobody approved is a <c>waiting_owner</c> outcome whatever the channel says: the approved-state guard
    /// runs before the §3.6 triggers, so the run waits instead of ending on a channel that is not even read.
    /// </summary>
    [Fact]
    public async Task Create_UnapprovedCut_WaitsForTheOwner()
    {
        var (run, creator, reader, sender) = await Arrange(EpisodeStatus.VideoReview);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(OwnerActionReasons.OwnerApprovalRequired);
        run.Run.State.ShouldBe(AutopilotRunState.WaitingOwner);
        run.Run.WaitingFor.ShouldBe(WaitingFor.Video);
        reader.ChannelReads.ShouldBe(0, "an unapproved cut never reaches the channel triggers");
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>An authorization that names no channel ends the run: there is nothing to publish through.</summary>
    [Fact]
    public async Task Create_NoChannel_EndsChannelMissing()
    {
        var (run, creator, _, sender) = await Arrange(EpisodeStatus.Ready, channel: null);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.ChannelMissing);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>Plan §3.6 row 6: a connection that is no longer active pauses the run instead of failing it.</summary>
    [Fact]
    public async Task Create_InactiveConnection_PausesTheRun()
    {
        var (run, creator, _, sender) = await Arrange(
            EpisodeStatus.Ready,
            channel: new AutopilotChannelSnapshot(Guid.CreateVersion7(), Platform.YouTube, ChannelConnectionStatus.Expired));

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.ChannelNotActive);
        run.Run.State.ShouldBe(AutopilotRunState.Paused);
        run.Run.PauseReason.ShouldBe(AutopilotPauseReason.ChannelNotActive);
        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// The create: the same command the owner's route sends, with the slot's instant as <c>publish_at</c>, the series'
    /// current authorization (not the run's snapshot) and the series' privacy; the run then holds the row's id.
    /// </summary>
    [Fact]
    public async Task Create_ReadyCut_SendsTheOwnerCommandAndAttachesTheRow()
    {
        var (run, creator, reader, sender) = await Arrange(EpisodeStatus.Ready);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        result.Code.ShouldBe(AutopilotRunStep.Publication);
        run.Run.State.ShouldBe(AutopilotRunState.PublicationCreated);
        run.Run.PublicationId.ShouldBe(sender.PublicationId);

        var command = sender.Single<CreatePublicationCommand>();
        command.EpisodeId.ShouldBe(AutopilotRuns.EpisodeId);
        command.ChannelConnectionId.ShouldBe(reader.Channel!.Id);
        command.PublishAt.ShouldBe(run.Run.SlotAt);
        command.Privacy.ShouldBe("private", "the tests' settings ask for private");
        command.AutopilotAuthorizationId.ShouldBe(reader.CurrentAuthorizationId);
    }

    /// <summary>
    /// While the channel is pre-audit the create is private in both modes (plan §7.4), whatever the settings ask for.
    /// </summary>
    [Fact]
    public async Task Create_PreAuditChannel_SubmitsPrivate()
    {
        var (run, creator, _, sender) = await Arrange(EpisodeStatus.Ready, preAudit: true);
        var settings = AutopilotStepContexts.Settings() with { Privacy = PrivacyStatus.Public };
        var context = new AutopilotStepContext(
            run.Run,
            run.Series,
            settings,
            AutopilotTiming.Compute(run.Run.SlotAt, settings, AutopilotStepContexts.TimingOptions, InWindow),
            AutopilotStepContexts.TimingOptions,
            InWindow);

        await creator.TryCreateAsync(context, Ct);

        sender.Single<CreatePublicationCommand>().Privacy.ShouldBe("private");
    }

    /// <summary>
    /// The live-publication collision of this run's own lost save is adopted, not retried: the run continues on the
    /// row it already created, so "one publication per episode and platform" stays true without a second row.
    /// </summary>
    [Fact]
    public async Task Create_DuplicateOfThisRunsPublication_IsAdopted()
    {
        var existingId = Guid.CreateVersion7();
        var (run, creator, reader, sender) = await Arrange(EpisodeStatus.Ready, refusal: ErrorCodes.PublicationInvalidState);
        reader.ActivePublication = new AutopilotActivePublication(existingId, reader.CurrentAuthorizationId);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Advanced);
        run.Run.PublicationId.ShouldBe(existingId);
        run.Run.State.ShouldBe(AutopilotRunState.PublicationCreated);
        sender.Sent.ShouldHaveSingleItem();
    }

    /// <summary>
    /// A live publication from another path (or another authorization) can never be this run's: the slot's create
    /// fails instead of adopting a row this run did not create.
    /// </summary>
    [Fact]
    public async Task Create_ForeignLivePublication_Fails()
    {
        var (run, creator, reader, _) = await Arrange(EpisodeStatus.Ready, refusal: ErrorCodes.PublicationInvalidState);
        reader.ActivePublication = new AutopilotActivePublication(Guid.CreateVersion7(), Guid.CreateVersion7());

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Stopped);
        result.Code.ShouldBe(AutopilotStepCodes.PublicationCreateFailed);
        run.Run.State.ShouldBe(AutopilotRunState.Failed);
        run.Run.PublicationId.ShouldBeNull();
    }

    /// <summary>A transient refusal is retried while the window is open: the run stays where it is.</summary>
    [Fact]
    public async Task Create_TransientRefusal_Retries()
    {
        var (run, creator, _, _) = await Arrange(EpisodeStatus.Ready, refusal: ErrorCodes.ServiceUnavailable);

        var result = await creator.TryCreateAsync(AutopilotStepContexts.For(run.Run, run.Series, InWindow), Ct);

        result.Kind.ShouldBe(StepResultKind.Retry);
        result.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        run.Run.State.ShouldBe(AutopilotRunState.VideoGate);
    }

    /// <summary>An instant inside the create window and before the cutoff: 2026-10-20T19:00Z.</summary>
    private static readonly DateTimeOffset InWindow = AutopilotStepContexts.DefaultSlotAt.AddHours(-25);

    private async Task<(
        RunAndSeries Run,
        AutopilotPublicationCreator Creator,
        FakeStepReader Reader,
        PublicationCreateSender Sender)> Arrange(
        EpisodeStatus status,
        AutopilotChannelSnapshot? channel = null,
        bool preAudit = false,
        string? refusal = null)
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor)
            .CreateAsync(user.Id, autopilotEnabled: true, ct: Ct);

        var run = AutopilotRuns.WalkTo(series, AutopilotRunState.VideoGate, Now);
        var reader = new FakeStepReader
        {
            Episode = new AutopilotRenderEpisode(
                AutopilotRuns.EpisodeId,
                status,
                Version: 3,
                Title: "an episode",
                Brief: "a brief"),
            Channel = channel ?? new AutopilotChannelSnapshot(
                Guid.CreateVersion7(),
                Platform.YouTube,
                ChannelConnectionStatus.Active),
            CurrentAuthorizationId = Guid.CreateVersion7(),
        };

        var sender = new PublicationCreateSender(Guid.CreateVersion7(), AutopilotRuns.EpisodeId, refusal);

        var creator = new AutopilotPublicationCreator(sender, reader, new FakeChannelAuditPolicy(preAudit));

        return (new RunAndSeries(run, series), creator, reader, sender);
    }

    private sealed record RunAndSeries(AutopilotRun Run, SeriesAggregate Series);
}
