using System.Globalization;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Common;
using NightTales.Application.Features.Publishing.Commands.ConsentPublication;
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
/// The consent command of 06.2.2-T1 (task §Tests): the text-version guard, the idempotent repeat that survives a
/// stale <c>If-Match</c>, the 412/404/409 refusals, and the one transaction that moves the publication, the
/// episode and the outbox row together (06 README D7/D12/D13/D17).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests need a real database.</b> The handler reads the publication through the tenant filter,
/// writes it through the real write model and relies on the outbox interceptor to store
/// <c>publication_updated.v1</c> beside the row — the 404 of <see cref="ForeignUser_404"/> is the filter's answer
/// and the outbox assertions read the interceptor's rows, so a fake would assert the wrong thing. The class
/// therefore derives from <see cref="IntegrationTest"/> and carries the <c>Category=Integration</c> trait.
/// </para>
/// <para>
/// <b>What the unit level cannot see.</b> The <c>audit_logs</c> row is written by the pipeline's
/// <c>AuditBehavior</c>, which these tests call without: they assert the command's own projection
/// (<c>IAuditData.ToAuditData</c>) instead, and the row itself belongs to the Api's
/// <c>ConsentPublicationTests</c>. Every other acceptance check — the row state, the outbox row, the episode
/// transition and the no-write refusals — is asserted on this level.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class ConsentPublicationHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock starts at.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The consent text version the host holds in these tests (06 README D11's default).</summary>
    private const string TextVersion = "pc.v1";

    [Fact]
    public async Task WrongTextVersion_NoWrite()
    {
        var scene = await NewSceneAsync();

        var run = await ConsentAsync(scene, textVersion: "pc.v2");

        // 400 consent_text_outdated on the request field the client re-reads (D7).
        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(PublicationErrors.ConsentTextOutdatedCode);
        run.Result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        run.Result.FirstError.Metadata!["field"].ShouldBe("consentTextVersion");

        // Nothing the command could audit, and nothing written: the row is still awaiting_consent, the episode
        // never left ready and the outbox stayed empty.
        run.Command.ToAuditData().ShouldBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("awaiting_consent");
        (await ScalarAsync(Column(scene, "(consent_at is null)::text"))).ShouldBe("true");
        (await OutboxCountAsync(scene)).ShouldBe(0);
        (await EpisodeStatusAsync(scene)).ShouldBe("ready");
    }

    [Fact]
    public async Task RepeatConsent_IsNoOp()
    {
        var scene = await NewSceneAsync();
        var preConsentEtag = await EtagAsync(scene);

        var first = await ConsentAsync(scene, ifMatch: preConsentEtag);
        first.Result.IsError.ShouldBeFalse();
        first.Result.Value.Status.ShouldBe("scheduled");
        first.Result.Value.ConsentSource.ShouldBe("user");
        first.Result.Value.Channel.DisplayName.ShouldBe(ChannelConnectionBuilder.DefaultDisplayName);
        first.Command.ToAuditData().ShouldNotBeNull();

        // The three evidence fields the row stores, plus the status (D7).
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");
        (await ScalarAsync(Column(scene, "consent_text_version"))).ShouldBe(TextVersion);
        (await ScalarAsync(Column(scene, "consent_source"))).ShouldBe("user");
        (await ScalarAsync(Column(scene, "(consent_at is null)::text"))).ShouldBe("false");

        var consentedXmin = await ScalarAsync(Column(scene, "xmin::text"));
        var consentAt = await ScalarAsync(Column(scene, "consent_at::text"));

        // The lost-response retry still carries the PRE-consent tag: it must answer 200 with the same DTO and
        // write nothing at all — no second row bump, no second outbox message, no second audit projection.
        var repeat = await ConsentAsync(scene, ifMatch: preConsentEtag);

        repeat.Result.IsError.ShouldBeFalse();
        repeat.Result.Value.Etag.ShouldBe(first.Result.Value.Etag);
        repeat.Result.Value.ConsentAt.ShouldBe(first.Result.Value.ConsentAt);
        repeat.Result.Value.Status.ShouldBe("scheduled");
        repeat.Command.ToAuditData().ShouldBeNull();

        (await ScalarAsync(Column(scene, "xmin::text"))).ShouldBe(consentedXmin);
        (await ScalarAsync(Column(scene, "consent_at::text"))).ShouldBe(consentAt);
        (await OutboxCountAsync(scene)).ShouldBe(1);

        // The one write of the whole test produced exactly one publication_updated.v1 carrying the scheduled
        // status and ids only (D13/D17).
        var payload = await ScalarAsync(
            $"SELECT payload::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{scene.PublicationId:D}%'");
        payload.ShouldContain("\"publicationId\":\"" + scene.PublicationId.ToString("D") + "\"");
        payload.ShouldContain("\"episodeId\":\"" + scene.EpisodeId.ToString("D") + "\"");
        payload.ShouldContain("\"userId\":\"" + scene.UserId.ToString("D") + "\"");
        payload.ShouldContain("\"status\":\"scheduled\"");
        payload.ShouldNotContain(PublicationBuilder.DefaultTitle);
        payload.ShouldNotContain(PublicationBuilder.DefaultDescription);
        payload.ShouldNotContain(ChannelConnectionBuilder.DefaultDisplayName);
    }

    [Fact]
    public async Task StaleETag_412()
    {
        var scene = await NewSceneAsync();
        var xmin = uint.Parse(await ScalarAsync(Column(scene, "xmin::text")), CultureInfo.InvariantCulture);
        var stale = ETags.Publication(xmin - 1);

        var run = await ConsentAsync(scene, ifMatch: stale);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(PublicationErrors.ConflictCode);
        run.Command.ToAuditData().ShouldBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("awaiting_consent");
        (await OutboxCountAsync(scene)).ShouldBe(0);
        (await EpisodeStatusAsync(scene)).ShouldBe("ready");
    }

    [Fact]
    public async Task ForeignUser_404()
    {
        var scene = await NewSceneAsync();
        var strangerId = Guid.CreateVersion7();
        var stranger = new TestCurrentUser { Id = strangerId };
        await new UserBuilder(Database, stranger).CreateAsync(strangerId, ct: TestContext.Current.CancellationToken);

        // The tenant filter hides the row, so the answer is the same 404 an unknown id gets — never 403.
        var run = await ConsentAsync(scene, asUser: stranger);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe("not_found");
        run.Command.ToAuditData().ShouldBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("awaiting_consent");
        (await OutboxCountAsync(scene)).ShouldBe(0);
    }

    [Fact]
    public async Task AutopilotConsentedRepeat_409()
    {
        var scene = await NewSceneAsync();

        // The row the autopilot job already consented: a user's explicit consent on top of it is not silently
        // merged (D7), even with the current tag and the current text version.
        await Database.ExecuteAsync(
            "UPDATE publications SET status = 'scheduled', consent_at = "
            + $"'{Now.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz, consent_text_version = 'pc.v1', "
            + "consent_source = 'autopilot' "
            + $"WHERE id = '{scene.PublicationId:D}'",
            TestContext.Current.CancellationToken);

        var run = await ConsentAsync(scene);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(PublicationErrors.InvalidStateCode);
        run.Command.ToAuditData().ShouldBeNull();
        (await ScalarAsync(Column(scene, "consent_source"))).ShouldBe("autopilot");
        (await OutboxCountAsync(scene)).ShouldBe(0);
    }

    [Fact]
    public async Task EpisodeNotReady_FailsAndRollsBack()
    {
        // A publication whose episode is rendering: the consent write and the episode move share one transaction,
        // so the episode's refusal leaves no consent behind.
        var scene = await NewSceneAsync(EpisodeStatus.Rendering);

        var run = await ConsentAsync(scene);

        run.Result.IsError.ShouldBeTrue();
        run.Result.FirstError.Code.ShouldBe(Episode.InvalidStateCode);
        run.Command.ToAuditData().ShouldBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("awaiting_consent");
        (await ScalarAsync(Column(scene, "(consent_at is null)::text"))).ShouldBe("true");
        (await ScalarAsync(Column(scene, "(consent_text_version is null)::text"))).ShouldBe("true");
        (await OutboxCountAsync(scene)).ShouldBe(0);
        (await EpisodeStatusAsync(scene)).ShouldBe("rendering");
    }

    [Fact]
    public async Task SecondPublicationOfScheduledEpisode_Succeeds()
    {
        // The episode is already scheduled by an earlier publication (possibly cancelled later): a second
        // publication of the same episode is a legitimate command, so MarkScheduled is an idempotent success.
        var scene = await NewSceneAsync(EpisodeStatus.Scheduled);

        var run = await ConsentAsync(scene);

        run.Result.IsError.ShouldBeFalse();
        run.Result.Value.Status.ShouldBe("scheduled");
        run.Command.ToAuditData().ShouldNotBeNull();
        (await ScalarAsync(Column(scene, "status"))).ShouldBe("scheduled");
        (await OutboxCountAsync(scene)).ShouldBe(1);
        (await EpisodeStatusAsync(scene)).ShouldBe("scheduled");
    }

    [Fact]
    public async Task Consent_AuditProjectionCarriesIdsAndVersionOnly()
    {
        var scene = await NewSceneAsync();

        var run = await ConsentAsync(scene);

        run.Result.IsError.ShouldBeFalse();

        var data = run.Command.ToAuditData();

        data.ShouldNotBeNull();
        data.SubjectUserId.ShouldBeNull();
        data.EntityType.ShouldBe("publication");
        data.EntityId.ShouldBe(scene.PublicationId);
        data.Summary["publicationId"].ShouldBe(scene.PublicationId.ToString("D"));
        data.Summary["episodeId"].ShouldBe(scene.EpisodeId.ToString("D"));
        data.Summary["consentTextVersion"].ShouldBe(TextVersion);
        data.Summary["source"].ShouldBe("user");
        data.Summary.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Consent_LogsIdsOnly()
    {
        var scene = await NewSceneAsync();
        var sink = new CapturingLogger();

        var run = await ConsentAsync(scene, logger: sink);

        run.Result.IsError.ShouldBeFalse();
        sink.Messages.ShouldNotBeEmpty();

        // D17: ids only — never the title, the description or the channel display name.
        sink.Messages.ShouldAllBe(message =>
            !message.Contains(PublicationBuilder.DefaultTitle, StringComparison.Ordinal)
            && !message.Contains(PublicationBuilder.DefaultDescription, StringComparison.Ordinal)
            && !message.Contains(ChannelConnectionBuilder.DefaultDisplayName, StringComparison.Ordinal));
    }

    /// <summary>The rows of one scene, as the tests address them.</summary>
    private sealed record Scene(
        Guid UserId,
        TestCurrentUser CurrentUser,
        Guid EpisodeId,
        Guid ConnectionId,
        Guid PublicationId);

    /// <summary>One command's result and the command itself, so its audit projection can be asserted.</summary>
    private sealed record ConsentRun(ErrorOr<PublicationDto> Result, ConsentPublicationCommand Command);

    /// <summary>
    /// Seeds one user, series, episode, active connection and the episode's publication in
    /// <c>awaiting_consent</c>. The publication goes in through <see cref="PublicationBuilder"/>'s raw insert, which
    /// writes no outbox row: the aggregate's factory would, and each test counts only its command's messages.
    /// </summary>
    private async Task<Scene> NewSceneAsync(EpisodeStatus episodeStatus = EpisodeStatus.Ready)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var currentUser = new TestCurrentUser { Id = userId };

        await new UserBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var series = await new SeriesBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var episode = await new EpisodeBuilder(Database, currentUser)
            .CreateAsync(userId, series.Id, status: episodeStatus, ct: ct);

        var connection = await new ChannelConnectionBuilder(Database, currentUser)
            .CreateAsync(userId, ChannelConnectionStatus.Active, ct: ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            PublicationStatus.AwaitingConsent,
            privacy: "unlisted",
            ct: ct);

        return new Scene(userId, currentUser, episode.Id, connection.Id, publicationId);
    }

    /// <summary>Sends the command the way the route would, through the handler under test.</summary>
    private async Task<ConsentRun> ConsentAsync(
        Scene scene,
        string? ifMatch = null,
        string textVersion = TextVersion,
        TestCurrentUser? asUser = null,
        string currentVersion = TextVersion,
        ILogger<ConsentPublicationHandler>? logger = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = asUser ?? scene.CurrentUser;
        var clock = new FakeClock(Now);

        await using var context = Database.CreateAppDbContext(actor, clock);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var command = new ConsentPublicationCommand(
            scene.PublicationId,
            textVersion,
            ifMatch ?? await EtagAsync(scene));

        var handler = new ConsentPublicationHandler(
            context,
            new FixedYouTubeOptions(currentVersion),
            clock,
            logger ?? NullLogger<ConsentPublicationHandler>.Instance);

        var result = await handler.Handle(command, ct);

        if (result.IsError)
        {
            await transaction.RollbackAsync(ct);
        }
        else
        {
            await transaction.CommitAsync(ct);
        }

        return new ConsentRun(result, command);
    }

    /// <summary>The row's current strong ETag (<c>"pu-&lt;xmin&gt;"</c>).</summary>
    private async Task<string> EtagAsync(Scene scene)
    {
        var xmin = uint.Parse(
            await ScalarAsync(Column(scene, "xmin::text")),
            CultureInfo.InvariantCulture);

        return ETags.Publication(xmin);
    }

    /// <summary>The <c>publication_updated.v1</c> rows this scene's publication raised.</summary>
    private Task<int> OutboxCountAsync(Scene scene) =>
        CountAsync(
            $"SELECT count(*)::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{scene.PublicationId:D}%'");

    /// <summary>The episode's stored status.</summary>
    private Task<string> EpisodeStatusAsync(Scene scene) =>
        ScalarAsync($"SELECT status FROM episodes WHERE id = '{scene.EpisodeId:D}'");

    /// <summary>One expression of the scene's own publication row.</summary>
    private static string Column(Scene scene, string expression) =>
        $"SELECT {expression} FROM publications WHERE id = '{scene.PublicationId:D}'";

    private async Task<string> ScalarAsync(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);

        return rows[0];
    }

    private async Task<int> CountAsync(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);

        return int.Parse(rows[0], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The sink of the acceptance check "no log line written by this path contains a title, a description or a
    /// channel display name": the rendered message of every line, in order.
    /// </summary>
    private sealed class CapturingLogger : ILogger<ConsentPublicationHandler>
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

    /// <summary>
    /// A fixed options monitor: the handler reads <c>YouTube:ConsentTextVersion</c> once per command, and a test
    /// that changes it changes the version the row must echo.
    /// </summary>
    private sealed class FixedYouTubeOptions(string textVersion) : IOptionsMonitor<YouTubeOptions>
    {
        private readonly YouTubeOptions _value = new() { ConsentTextVersion = textVersion };

        public YouTubeOptions CurrentValue => _value;

        public YouTubeOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<YouTubeOptions, string?> listener) => null;
    }
}
