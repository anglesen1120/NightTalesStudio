using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Application.Features.Publishing.Dtos;
using NightTales.Application.Publishing;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The create command of 06.2.1-T1 (task §Tests): the veto window of YR1, the pre-audit privacy override of D12,
/// the music attribution of C8, the partial unique index that refuses a second live publication, and the
/// acceptance checks around them (a non-ready episode, a connection that is not active, another owner's episode,
/// the ids-only audit projection and the log lines that must never carry user text).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests need a real database.</b> The command reads the aggregate, the connection, the series
/// settings and the render's params, and it flushes its own <c>SaveChanges</c> so the partial unique index
/// <c>ux_publications_active_episode_platform</c> can arbitrate the duplicate create — the 409 of
/// <c>DuplicateActivePublication_Is409</c> is the index's answer, not a fake's. The class therefore derives from
/// <see cref="IntegrationTest"/>: a per-class database copied from the run's migrated template, which also gives
/// it the <c>Category=Integration</c> trait the unit job filters out (09 README D13).
/// </para>
/// <para>
/// Every row is seeded through the real write model with a <see cref="TestCurrentUser"/> — never raw SQL except
/// where a test states one (the "after the row was cancelled" control of the duplicate test).
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class CreatePublicationHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock starts at.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The submitted metadata every happy-path test sends (well inside every G4 limit).</summary>
    private static readonly IReadOnlyList<string> Tags = ["true crime", "night tales"];

    private const string SubmittedDescription = "Một tập về ngôi nhà bỏ hoang.";

    [Fact]
    public async Task VetoWindow_ShortenedByLateVideo()
    {
        // veto_window_hours = 12, publishAt = now + 3h: the upload is due 2h after the ceiling the lead time
        // leaves ((now + 3h) - 120min = now + 1h < now + 2h), so YR1 refuses the create outright.
        var refused = await NewSceneAsync(autopilotSettings: """{"veto_window_hours":12}""", withRenderJob: false);

        // The authorization must be a real, unrevoked row: the revoked-authorization check of step 6 runs
        // before YR1, so a fabricated id would answer on autopilotAuthorizationId instead of publishAt.
        var authorizationId = await SeedAuthorizationAsync(refused);

        var tooLate = await CreateAsync(refused, publishAt: Now.AddHours(3), authorizationId: authorizationId);

        tooLate.IsError.ShouldBeTrue();
        tooLate.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        tooLate.FirstError.Metadata!["field"].ShouldBe("publishAt");
        (await PublicationCountAsync(refused)).ShouldBe(0);

        // The same settings with room to spare: the ceiling is what shortens the window, not the 12 hours.
        var shortened = await CreateAsync(refused, publishAt: Now.AddHours(6), authorizationId: authorizationId);

        shortened.IsError.ShouldBeFalse();
        shortened.Value.VetoEndsAt.ShouldBe(Now.AddHours(4));
    }

    [Fact]
    public async Task PreAudit_ForcesPrivateAndStampsRequestedPrivacy()
    {
        var scene = await NewSceneAsync(withRenderJob: false);

        var created = await CreateAsync(scene, privacy: "public", auditStatus: YouTubeAuditStatuses.Pending);

        created.IsError.ShouldBeFalse();
        created.Value.Privacy.ShouldBe("private");
        created.Value.PreAudit.ShouldBeTrue();
        created.Value.RequestedPrivacy.ShouldBe("public");

        // And with an approved client the submitted value is stored as such, with no stamp at all. The arm
        // needs its own episode: one live row per (episode, platform) is the partial unique index of D18, so a
        // second create on the scene above would be refused as a duplicate before the privacy is even stored.
        var approvedScene = await NewSceneAsync(withRenderJob: false);

        var approved = await CreateAsync(approvedScene, privacy: "public", auditStatus: YouTubeAuditStatuses.Approved);

        approved.IsError.ShouldBeFalse();
        approved.Value.Privacy.ShouldBe("public");
        approved.Value.PreAudit.ShouldBeFalse();
        approved.Value.RequestedPrivacy.ShouldBeNull();
    }

    [Fact]
    public async Task Attribution_Null_LeavesDescriptionUntouched()
    {
        // A job without music contributes no tail, so the stored description is byte-for-byte the submission.
        var silent = await NewSceneAsync(withMusic: false);

        var created = await CreateAsync(silent);

        created.IsError.ShouldBeFalse();
        created.Value.Description.ShouldBe(SubmittedDescription);

        // The other arm of C8: a job with a track appends exactly one "\n\nMusic: <attribution>" tail.
        var musical = await NewSceneAsync(attribution: RenderJobBuilder.DefaultAttribution);

        var withTail = await CreateAsync(musical);

        withTail.IsError.ShouldBeFalse();
        withTail.Value.Description.ShouldBe(
            SubmittedDescription + "\n\nMusic: " + RenderJobBuilder.DefaultAttribution);

        // A job that carries music but no attribution text is the same no-tail case.
        var unattributed = await NewSceneAsync(attribution: null);

        var untouched = await CreateAsync(unattributed);

        untouched.IsError.ShouldBeFalse();
        untouched.Value.Description.ShouldBe(SubmittedDescription);
    }

    [Fact]
    public async Task DuplicateActivePublication_Is409()
    {
        var scene = await NewSceneAsync(withRenderJob: false);

        var first = await CreateAsync(scene);
        first.IsError.ShouldBeFalse();

        // The second create is refused by the partial unique index, not by a read: the handler translates the
        // flush to 409 publication_invalid_state and the unit of work rolls the whole attempt back.
        var second = await CreateAsync(scene);

        second.IsError.ShouldBeTrue();
        second.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        (await PublicationCountAsync(scene)).ShouldBe(1);

        // Control: once the live row left the index's predicate, the episode may be published again.
        await Database.ExecuteAsync(
            $"UPDATE publications SET status = 'cancelled' WHERE episode_id = '{scene.EpisodeId:D}'",
            TestContext.Current.CancellationToken);

        var third = await CreateAsync(scene);

        third.IsError.ShouldBeFalse();
        (await PublicationCountAsync(scene)).ShouldBe(2);
    }

    [Fact]
    public async Task EpisodeNotReady_Is409()
    {
        var scene = await NewSceneAsync(episodeStatus: EpisodeStatus.Rendering, withRenderJob: false);

        var created = await CreateAsync(scene);

        created.IsError.ShouldBeTrue();
        created.FirstError.Code.ShouldBe(PublicationErrors.EpisodeInvalidStateCode);
        (await PublicationCountAsync(scene)).ShouldBe(0);
    }

    [Fact]
    public async Task ConnectionNotActive_Is409()
    {
        var scene = await NewSceneAsync(
            connectionStatus: ChannelConnectionStatus.Revoked,
            withRenderJob: false);

        var created = await CreateAsync(scene);

        created.IsError.ShouldBeTrue();
        created.FirstError.Code.ShouldBe(PublicationErrors.ChannelNotActiveCode);
        (await PublicationCountAsync(scene)).ShouldBe(0);
    }

    [Fact]
    public async Task ForeignEpisode_Is404()
    {
        var owner = await NewSceneAsync(withRenderJob: false);
        var stranger = await NewSceneAsync(withRenderJob: false);

        // The stranger's own connection against the owner's episode: the tenant filter hides the episode, so the
        // answer is the same 404 an unknown id gets — never 403 (09 README D10).
        var created = await CreateAsync(owner, asUser: stranger, connectionId: stranger.ConnectionId);

        created.IsError.ShouldBeTrue();
        created.FirstError.Code.ShouldBe("not_found");
        (await PublicationCountAsync(owner)).ShouldBe(0);
    }

    [Fact]
    public void Create_AuditProjectionCarriesIdsOnly()
    {
        // 06 README D17: the audit row's data is the three ids and nothing the caller typed. The projection is
        // what AuditBehavior writes, so asserting it here is the metadata half of the acceptance check; the row
        // itself is 09.3.6-T1's pipeline, already covered by its own tests.
        var publicationId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        var command = new CreatePublicationCommand(
            episodeId,
            connectionId,
            "Ngôi nhà bỏ hoang",
            SubmittedDescription,
            Tags,
            "unlisted",
            PublishAt: null,
            AutopilotAuthorizationId: null)
        {
            Outcome = new PublicationCreatedOutcome(publicationId, episodeId, connectionId),
        };

        var data = command.ToAuditData();

        data.ShouldNotBeNull();
        data.SubjectUserId.ShouldBeNull();
        data.EntityType.ShouldBe("publication");
        data.EntityId.ShouldBe(publicationId);
        data.Summary["publication_id"].ShouldBe(publicationId.ToString("D"));
        data.Summary["episode_id"].ShouldBe(episodeId.ToString("D"));
        data.Summary["channel_connection_id"].ShouldBe(connectionId.ToString("D"));
        data.Summary.Count.ShouldBe(3);

        // A refused create leaves no outcome, so the behaviour writes no row for it.
        var refused = command with { Outcome = null };

        refused.ToAuditData().ShouldBeNull();
    }

    [Fact]
    public async Task Create_LogsNoTitleDescriptionOrChannelName()
    {
        var scene = await NewSceneAsync(withRenderJob: false);
        var sink = new CapturingLogger();

        var created = await CreateAsync(scene, logger: sink);

        created.IsError.ShouldBeFalse();
        sink.Messages.ShouldNotBeEmpty();
        sink.Messages.ShouldAllBe(message =>
            !message.Contains(SubmittedDescription, StringComparison.Ordinal)
            && !message.Contains(ChannelConnectionBuilder.DefaultDisplayName, StringComparison.Ordinal));
    }

    /// <summary>The rows of one scene, as the tests address them.</summary>
    private sealed record Scene(
        Guid UserId,
        TestCurrentUser CurrentUser,
        Guid EpisodeId,
        Guid ConnectionId,
        Guid SeriesId);

    /// <summary>
    /// Seeds one user, series, episode, connection and (optionally) the render job whose params carry
    /// <paramref name="attribution"/>, through the real write model.
    /// </summary>
    private async Task<Scene> NewSceneAsync(
        EpisodeStatus episodeStatus = EpisodeStatus.Ready,
        ChannelConnectionStatus connectionStatus = ChannelConnectionStatus.Active,
        string? attribution = RenderJobBuilder.DefaultAttribution,
        bool withMusic = true,
        bool withRenderJob = true,
        string? autopilotSettings = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var currentUser = new TestCurrentUser { Id = userId };

        await new UserBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var series = await new SeriesBuilder(Database, currentUser)
            .CreateAsync(userId, autopilotSettings: autopilotSettings, ct: ct);

        var episode = await new EpisodeBuilder(Database, currentUser)
            .CreateAsync(userId, series.Id, status: episodeStatus, ct: ct);

        var connection = await new ChannelConnectionBuilder(Database, currentUser)
            .CreateAsync(userId, connectionStatus, ct: ct);

        if (withRenderJob)
        {
            await new RenderJobBuilder(Database, currentUser)
                .CreateAsync(userId, episode.Id, attribution, withMusic, ct: ct);
        }

        return new Scene(userId, currentUser, episode.Id, connection.Id, series.Id);
    }

    /// <summary>
    /// One live (or revoked) autopilot authorization of the scene's series, written straight into 02's table:
    /// the handler only reads it, and 02.8.3-T2 owns every write on that path (the same insert the API
    /// integration test's <c>SeedAuthorizationAsync</c> performs).
    /// </summary>
    private async Task<Guid> SeedAuthorizationAsync(Scene scene, bool revoked = false)
    {
        var id = Guid.CreateVersion7();
        var stamp = $"'{Now:O}'::timestamptz";

        await Database.ExecuteAsync(
            "INSERT INTO series_autopilot_authorizations (id, user_id, series_id, version, text_version, "
            + "text_hash, topic, cadence, privacy_status, accepted_at, revoked_at, created_at, updated_at) "
            + $"VALUES ('{id}', '{scene.UserId}', '{scene.SeriesId}', 1, 'pc.v1', '{new string('a', 64)}', "
            + $"'ghost', 'daily', 'public', {stamp}, {(revoked ? stamp : "NULL")}, {stamp}, {stamp})",
            TestContext.Current.CancellationToken);

        return id;
    }

    /// <summary>Sends the command the way the route would, through the handler under test.</summary>
    private async Task<ErrorOr<PublicationDto>> CreateAsync(
        Scene scene,
        DateTimeOffset? publishAt = null,
        Guid? authorizationId = null,
        string privacy = "unlisted",
        Guid? connectionId = null,
        Scene? asUser = null,
        string auditStatus = YouTubeAuditStatuses.Pending,
        ILogger<CreatePublicationHandler>? logger = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = asUser ?? scene;
        var clock = new FakeClock(Now);

        await using var context = Database.CreateAppDbContext(actor.CurrentUser, clock);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var command = new CreatePublicationCommand(
            scene.EpisodeId,
            connectionId ?? scene.ConnectionId,
            "Ngôi nhà bỏ hoang",
            SubmittedDescription,
            Tags,
            privacy,
            publishAt,
            authorizationId);

        var handler = new CreatePublicationHandler(
            context,
            new YouTubeOptions { AuditStatus = auditStatus },
            clock,
            logger ?? NullLogger<CreatePublicationHandler>.Instance);

        var result = await handler.Handle(command, ct);

        if (result.IsError)
        {
            await transaction.RollbackAsync(ct);
        }
        else
        {
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    /// <summary>The number of publication rows the scene's episode carries.</summary>
    private async Task<int> PublicationCountAsync(Scene scene)
    {
        var rows = await Database.QueryStringsAsync(
            $"SELECT count(*)::text FROM publications WHERE episode_id = '{scene.EpisodeId:D}'",
            TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);

        return int.Parse(rows[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The test-level sink of the acceptance check "no log line written by this path contains a title, a
    /// description or a channel display name": the rendered message of every line, in order.
    /// </summary>
    private sealed class CapturingLogger : ILogger<CreatePublicationHandler>
    {
        private readonly List<string> _messages = [];

        /// <summary>Every rendered message, in the order the handler wrote them.</summary>
        internal IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _messages.Add(formatter(state, exception));
        }
    }
}
