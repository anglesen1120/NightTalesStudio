using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Outbox;
using NightTales.Application.Abstractions.Realtime;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Events;
using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.Domain.Series.Events;
using NightTales.Infrastructure.Identity;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Time;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Email;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The owner-action notice of a gate stop (02.8.2 plan §4 UC6, task 02.8.2-T1): the three rows it adds — the
/// <c>autopilot_owner_action_required.v1</c> outbox row, the <c>autopilot.owner_action_required</c> audit row and
/// the <c>autopilot_owner_action_required</c> <c>email_messages</c> row — belong to the caller's unit of work, not
/// to a transaction of their own.
/// </summary>
/// <remarks>
/// <para>
/// The service is built by hand over the production context, the production <c>EfAuditWriter</c>, the production
/// <c>UserProfileReader</c> and the production clock, so what these tests drive is the write itself: the outbox
/// interceptor that turns the aggregate's event into <c>outbox_messages</c>, the audit write's own transaction
/// guard, and the tenant scope. The e-mail queue is the TestKit's real-row <see cref="FakeEmailSender"/>, which
/// writes a genuine <c>email_messages</c> row into the ambient unit — so a rollback assertion is evidence about the
/// transaction rather than about a list the test holds.
/// </para>
/// <para>
/// The service queues the e-mail in the verdict's own transaction on purpose (the precedent of
/// <c>ConnectChannelConnectionHandler</c>): the outbox delivery that raises <c>EpisodeUpdated</c> runs after the
/// verdict committed and can be redelivered, so queueing there would let the owner receive the notice twice. The
/// last test proves the handler that remains has nothing to write with.
/// </para>
/// <para>
/// The probes read through the fixture's own connection, so a row that only exists inside the test's transaction is
/// invisible to them: what a probe sees after a commit exists after the commit, and what it sees after a rollback is
/// the real absence.
/// </para>
/// </remarks>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
public sealed class AutopilotNotificationTests(TestDatabase database) : IntegrationTest(database)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The four audit keys plan §4 allows the notice to carry (D19, 09 README BD16).</summary>
    private static readonly string[] AuditDataKeys = ["episodeId", "failures", "reason", "runId"];

    /// <summary>The five catalog values the owner's e-mail carries (plan §4).</summary>
    private static readonly string[] EmailParamKeys = ["seriesTitle", "episodeTitle", "slot", "failedChecks", "deepLink"];

    private const string SeriesTitle = "Gate series";
    private const string EpisodeTitle = "Tập cổng";

    /// <summary>The acceptance check: all three rows commit with the caller's own <c>SaveChangesAsync</c>.</summary>
    [Fact]
    public async Task AutopilotNotification_Failure_WritesOutboxInSameTransaction()
    {
        var world = await SeedWorldAsync();
        using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var state = new FakeEmailSenderState();
        var service = Service(context, state);

        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        var notified = await service.NotifyOwnerActionRequiredAsync(
            world.RunId,
            OwnerActionReasons.FailedChecks,
            ["self_review_mean_score", "similarity_sources"],
            CancellationToken.None);

        notified.IsError.ShouldBeFalse();

        // Nothing has reached the database yet: the service adds rows, the caller saves them.
        (await OutboxTypesAsync(world.RunId)).ShouldBeEmpty();
        (await AuditActionsAsync(world.RunId)).ShouldBeEmpty();
        (await EmailTemplatesAsync(world.UserId)).ShouldBeEmpty();

        await context.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        (await OutboxTypesAsync(world.RunId))
            .ShouldHaveSingleItem()
            .ShouldBe("autopilot_owner_action_required.v1");
        (await AuditActionsAsync(world.RunId))
            .ShouldHaveSingleItem()
            .ShouldBe(AutopilotNotificationService.AuditAction);
        (await EmailTemplatesAsync(world.UserId))
            .ShouldHaveSingleItem()
            .ShouldBe(AutopilotNotificationService.EmailTemplate);

        // Ids and codes only (D19, 09 README BD16): the four keys of plan §4's table, and nothing else.
        (await AuditDataKeysAsync(world.RunId))
            .ShouldBe(AuditDataKeys);
        (await AuditReasonAsync(world.RunId))
            .ShouldHaveSingleItem()
            .ShouldBe(OwnerActionReasons.FailedChecks);

        // The five catalog values: the owner's own metadata (both titles), the slot, the failing checks in words
        // and the episode path — never a story text, a brief, a fact sheet or a source URL.
        var message = state.Calls.ShouldHaveSingleItem();
        message.Template.ShouldBe(AutopilotNotificationService.EmailTemplate);
        message.UserId.ShouldBe(world.UserId);
        message.Locale.ShouldBe(Language.Vi, "the render locale is the account's own ui_locale (09 README D16)");
        message.Params.Keys.ShouldBe(EmailParamKeys, ignoreOrder: true);
        message.Params["seriesTitle"].ShouldBe(SeriesTitle);
        message.Params["episodeTitle"].ShouldBe(EpisodeTitle);
        message.Params["failedChecks"].ShouldBe("self review mean score, similarity sources");
        message.Params["deepLink"].ShouldBe($"/episodes/{world.EpisodeId}");
        message.Params["slot"].ShouldBe(Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>A rolled-back unit leaves none of the three rows behind (00 §5.2: one transaction or none).</summary>
    [Fact]
    public async Task AutopilotNotification_Rollback_WritesNeitherRow()
    {
        var world = await SeedWorldAsync();
        using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var service = Service(context, new FakeEmailSenderState());

        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        var notified = await service.NotifyOwnerActionRequiredAsync(
            world.RunId,
            OwnerActionReasons.TrueCrimeHold,
            ["true_crime_hold"],
            CancellationToken.None);

        notified.IsError.ShouldBeFalse();

        await context.SaveChangesAsync(CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        (await OutboxTypesAsync(world.RunId)).ShouldBeEmpty();
        (await AuditActionsAsync(world.RunId)).ShouldBeEmpty();
        (await EmailTemplatesAsync(world.UserId)).ShouldBeEmpty();
    }

    /// <summary>
    /// A refused e-mail queue is an error the whole unit rolls back: the sender wrote its real row before
    /// answering the failure (R2), so the absent row after the rollback is evidence about a row that existed, and
    /// the notice's outbox and audit rows were never added at all.
    /// </summary>
    [Fact]
    public async Task AutopilotNotification_EmailQueueRefused_WritesNothing()
    {
        var world = await SeedWorldAsync();
        using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var service = Service(context, new FakeEmailSenderState { Fail = true, WriteBeforeFailing = true });

        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        var notified = await service.NotifyOwnerActionRequiredAsync(
            world.RunId,
            OwnerActionReasons.FailedChecks,
            ["self_review_mean_score"],
            CancellationToken.None);

        notified.IsError.ShouldBeTrue();
        notified.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);

        await context.SaveChangesAsync(CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        (await OutboxTypesAsync(world.RunId)).ShouldBeEmpty();
        (await AuditActionsAsync(world.RunId)).ShouldBeEmpty();
        (await EmailTemplatesAsync(world.UserId)).ShouldBeEmpty();
    }

    /// <summary>
    /// A reason outside <see cref="OwnerActionReasons"/> and a run without an episode write nothing: the notice can
    /// never be addressed to a stop this feature did not define (fail closed).
    /// </summary>
    [Fact]
    public async Task AutopilotNotification_UnknownReason_WritesNothing()
    {
        var world = await SeedWorldAsync();
        using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var service = Service(context, new FakeEmailSenderState());

        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        var notified = await service.NotifyOwnerActionRequiredAsync(
            world.RunId,
            "publish_now",
            [],
            CancellationToken.None);

        notified.IsError.ShouldBeTrue();
        (await OutboxTypesAsync(world.RunId)).ShouldBeEmpty();
        (await EmailTemplatesAsync(world.UserId)).ShouldBeEmpty();

        await transaction.RollbackAsync(CancellationToken.None);
    }

    /// <summary>
    /// A message that cannot be stored is refused with a fixed code instead of an exception at the caller's
    /// commit (02.8.2-T1 SEC-8): the caller's own failing-check codes are the one substitution value that does not
    /// come from a PostgreSQL <c>text</c> column, and nothing of the notice reaches the unit of work either way.
    /// </summary>
    [Fact]
    public async Task AutopilotNotification_NonStorableParam_IsRefusedWithTheFixedCode()
    {
        var world = await SeedWorldAsync();
        using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = world.UserId });
        var state = new FakeEmailSenderState();
        var service = Service(context, state);

        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        var notified = await service.NotifyOwnerActionRequiredAsync(
            world.RunId,
            OwnerActionReasons.FailedChecks,
            ["self_review\u0000mean_score"],
            CancellationToken.None);

        notified.IsError.ShouldBeTrue();
        notified.FirstError.Code.ShouldBe(AutopilotNotificationService.ParamsNotPersistableErrorCode);
        state.Calls.ShouldBeEmpty("nothing may be queued for a notice that cannot be stored");
        (await OutboxTypesAsync(world.RunId)).ShouldBeEmpty();
        (await AuditActionsAsync(world.RunId)).ShouldBeEmpty();
        (await EmailTemplatesAsync(world.UserId)).ShouldBeEmpty();

        await transaction.RollbackAsync(CancellationToken.None);
    }

    /// <summary>
    /// The delivery that remains is realtime only (02.8.2 plan §5, UC6): it publishes one ids-only
    /// <c>EpisodeUpdated</c> for the run's episode and nothing else — the notice's write half moved into the
    /// verdict's transaction, so an at-least-once redelivery cannot queue a second message.
    /// </summary>
    [Fact]
    public async Task OwnerActionRequiredHandler_PublishesEpisodeUpdatedOnly()
    {
        var notifier = new RecordingRealtimeNotifier();
        var handler = new AutopilotOwnerActionRequiredHandler(notifier);
        var userId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();

        await handler.HandleAsync(
            new AutopilotOwnerActionRequired(
                userId,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                episodeId,
                OwnerActionReasons.TrueCrimeHold,
                ["true_crime_hold"],
                "autopilot.gate.hold.true_crime",
                Start),
            new OutboxContext(Guid.CreateVersion7(), userId, 1, Start),
            CancellationToken.None);

        var published = notifier.Events.ShouldHaveSingleItem();
        published.UserId.ShouldBe(userId);
        published.Event.Name.ShouldBe(RealtimeEventName.EpisodeUpdated);
        published.Event.Ids.EpisodeId.ShouldBe((Guid?)episodeId);
        published.Event.Ids.SeriesId.ShouldBeNull();
    }

    /// <summary>
    /// The handler has no write path at all: the notifier is its only dependency, so there is no unit of work, no
    /// e-mail sender and no profile reader it could queue a duplicate message with.
    /// </summary>
    [Fact]
    public void OwnerActionRequiredHandler_TakesOnlyTheNotifier()
    {
        typeof(AutopilotOwnerActionRequiredHandler)
            .GetConstructors()
            .ShouldHaveSingleItem()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ShouldBe(new[] { typeof(IRealtimeNotifier) });
    }

    private AutopilotNotificationService Service(AppDbContext context, FakeEmailSenderState state) =>
        new(
            context,
            new EfAuditWriter(context),
            new FakeEmailSender(state, context, new SystemClock()),
            new UserProfileReader(Database.CreateReadDbContext()),
            new SystemClock(),
            NullLogger<AutopilotNotificationService>.Instance);

    private Task<IReadOnlyList<string>> OutboxTypesAsync(Guid runId) =>
        Database.QueryStringsAsync(
            $"SELECT type FROM outbox_messages WHERE payload->>'runId' = '{runId}' ORDER BY type");

    private Task<IReadOnlyList<string>> AuditActionsAsync(Guid runId) =>
        Database.QueryStringsAsync(
            $"SELECT action FROM audit_logs WHERE data->>'runId' = '{runId}' ORDER BY action");

    private Task<IReadOnlyList<string>> AuditDataKeysAsync(Guid runId) =>
        Database.QueryStringsAsync(
            "SELECT k.key FROM audit_logs, LATERAL jsonb_object_keys(audit_logs.data) AS k(key) "
            + $"WHERE audit_logs.data->>'runId' = '{runId}' ORDER BY k.key");

    private Task<IReadOnlyList<string>> AuditReasonAsync(Guid runId) =>
        Database.QueryStringsAsync(
            $"SELECT data->>'reason' FROM audit_logs WHERE data->>'runId' = '{runId}'");

    private Task<IReadOnlyList<string>> EmailTemplatesAsync(Guid userId) =>
        Database.QueryStringsAsync(
            $"SELECT template FROM email_messages WHERE user_id = '{userId}' ORDER BY template");

    private async Task<World> SeedWorldAsync()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: CancellationToken.None);
        actor.Id = user.Id;

        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, title: SeriesTitle);
        var episode = await new EpisodeBuilder(Database, actor)
            .CreateAsync(user.Id, series.Id, title: EpisodeTitle, ct: CancellationToken.None);
        var run = await new AutopilotRunBuilder(Database, actor).CreateAsync(
            user.Id,
            series.Id,
            slotAt: Start,
            startAt: Start,
            now: Start,
            episodeId: episode.Id,
            state: AutopilotRunState.GateFailed,
            outcome: AutopilotRunOutcome.GateFailed,
            ct: CancellationToken.None);
        var runId = run.Id;

        return new World(user.Id, runId, episode.Id);
    }

    /// <summary>The owner, the run the notice is about and the episode it names.</summary>
    private sealed record World(Guid UserId, Guid RunId, Guid EpisodeId);

    /// <summary>The realtime notifier of the delivery test: it records every event and writes nothing.</summary>
    private sealed class RecordingRealtimeNotifier : IRealtimeNotifier
    {
        /// <summary>Every notification, in arrival order.</summary>
        public List<(Guid UserId, RealtimeEvent Event)> Events { get; } = [];

        /// <inheritdoc />
        public Task NotifyAsync(Guid userId, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
        {
            Events.Add((userId, realtimeEvent));

            return Task.CompletedTask;
        }
    }
}
