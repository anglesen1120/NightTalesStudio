using System.Globalization;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;

using NightTales.Application.Features.Publishing.Commands.CancelPublicationByLink;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The anonymous cancel-by-link command of 06.2.2-T2 (task §Tests, 06 README D7/F17/YR8/D17): one 204 that spends a
/// token and moves an <c>awaiting_consent</c> row to <c>cancelled</c>, and one generic
/// <c>400 cancel_token_invalid</c> for every failure — a spent, expired or unknown token, a token minted for another
/// publication, a missing or foreign row, and a row that left the window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a real database.</b> The handler writes through the real write model (<see cref="PublicationCancelLock"/>
/// is the production locked read) and the outbox interceptor is what raises <c>publication_updated.v1</c>, so the
/// status transition and that event are the real model's answers. The runner below does what the pipeline's
/// <c>UnitOfWorkBehavior</c> does — save and commit on success, roll back on every refusal — because the handler
/// itself only mutates the aggregate.
/// </para>
/// <para>
/// <b>What is faked.</b> Only the three seams whose <em>calls</em> are this command's own decisions: the token store
/// (what the consume answers), the quota ledger (a link cancel must never reserve, and normally releases nothing)
/// and the audit writer (the handler writes its own <c>publishing.cancelled</c> row, because the action's
/// <c>[Audited]</c> projection belongs to <c>CancelPublicationCommand</c>). The real token <em>table</em> — including
/// the "a refusal leaves <c>used_at</c> null" invariant — is the Api's <c>CancelByLinkTests</c>.
/// </para>
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class CancelPublicationByLinkHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock reads.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The plaintext token every fake consume is handed.</summary>
    private const string PresentedToken = "cancel-link-token-06.2.2";

    [Fact]
    public async Task CancelsAnAwaitingConsentPublication_AndWritesTheAuditRow()
    {
        // Acceptance #6: the link cancels exactly one publication in the veto window, records the actor as the
        // system link rather than a user, and leaves one event and one audit row of ids and statuses only.
        var scene = await NewSceneAsync(PublicationStatus.AwaitingConsent);
        var tokens = new FakeActionTokens(new ConsumedActionToken(scene.UserId, scene.PublicationId));
        var audit = new RecordingAuditWriter();
        var ledger = new RecordingQuotaLedger();

        var run = await CancelAsync(scene, tokens, ledger, audit);

        run.IsError.ShouldBeFalse();

        tokens.Consumed.ShouldBe(
            [PresentedToken],
            ignoreOrder: false,
            customMessage: "the route spends the presented token once, for the cancel kind");
        tokens.Kinds.ShouldBe([UserActionKind.PublicationCancel]);

        (await ScalarAsync(Column(scene, "status"))).ShouldBe("cancelled");
        (await ScalarAsync(Column(scene, $"(updated_at = '{Now:O}'::timestamptz)::text"))).ShouldBe("true");

        var payload = (await OutboxPayloadsAsync(scene)).ShouldHaveSingleItem();
        using (var json = JsonDocument.Parse(payload))
        {
            json.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
            json.RootElement.GetProperty("publicationId").GetGuid().ShouldBe(scene.PublicationId);
            json.RootElement.GetProperty("episodeId").GetGuid().ShouldBe(scene.EpisodeId);
        }

        ledger.Released.ShouldBeEmpty("an autopilot row in awaiting_consent holds no quota unit to give back");

        var entry = audit.Written.ShouldHaveSingleItem();

        entry.Action.ShouldBe("publishing.cancelled");
        entry.Category.ShouldBe(Category.Publishing);
        entry.Visibility.ShouldBe(Visibility.User);
        entry.ActorType.ShouldBe(ActorType.System, "the row is the link's, not a person's");
        entry.ActorUserId.ShouldBeNull("no principal made this request");
        entry.ActorLabel.ShouldBeNull();
        entry.SubjectUserId.ShouldBe(scene.UserId, "the committed row is about the token's owner");
        entry.EntityType.ShouldBe(AuditKeys.EntityType);
        entry.EntityId.ShouldBe(scene.PublicationId);
        entry.Ip.ShouldBeNull();
        entry.UserAgent.ShouldBeNull();
        entry.OccurredAt.ShouldBe(Now);

        using (var json = JsonDocument.Parse(entry.DataJson))
        {
            var root = json.RootElement;

            root.EnumerateObject().Select(property => property.Name).ShouldBe(
                [AuditKeys.PublicationIdKey, AuditKeys.EpisodeIdKey, AuditKeys.PreviousStatusKey, AuditKeys.ActorKey],
                ignoreOrder: true);

            root.GetProperty(AuditKeys.PublicationIdKey).GetString().ShouldBe(scene.PublicationId.ToString("D", CultureInfo.InvariantCulture));
            root.GetProperty(AuditKeys.EpisodeIdKey).GetString().ShouldBe(scene.EpisodeId.ToString("D", CultureInfo.InvariantCulture));
            root.GetProperty(AuditKeys.PreviousStatusKey).GetString().ShouldBe("awaiting_consent");
            root.GetProperty(AuditKeys.ActorKey).GetString().ShouldBe("system:link");
        }
    }

    [Fact]
    public async Task UsedToken_IsInvalid()
    {
        // The consumed token of 09 §4: a spent, expired or unknown token is one 400 with one code and no metadata.
        var scene = await NewSceneAsync(PublicationStatus.AwaitingConsent);
        var tokens = new FakeActionTokens(
            Error.Validation(ErrorCodes.ValidationFailed, "The link is invalid, already used or expired."));
        var audit = new RecordingAuditWriter();

        var result = await CancelAsync(scene, tokens, new RecordingQuotaLedger(), audit);

        result.FirstError.Code.ShouldBe(ErrorCodes.CancelTokenInvalid);
        result.Errors.Count.ShouldBe(1);

        await AssertUntouchedAsync(scene, audit, "a refused consume changes nothing");
    }

    [Fact]
    public async Task TransientStoreFailure_IsServiceUnavailable_AndIsNotTheLinkError()
    {
        // 09 README D5: the store's transient failure keeps its own 503. It says nothing about whether the token
        // exists, so it is not disguised as the link's refusal — and nothing is written either way.
        var scene = await NewSceneAsync(PublicationStatus.AwaitingConsent);
        var tokens = new FakeActionTokens(
            Error.Failure(ErrorCodes.ServiceUnavailable, "The database is temporarily unavailable; retry."));
        var audit = new RecordingAuditWriter();

        var result = await CancelAsync(scene, tokens, new RecordingQuotaLedger(), audit);

        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        await AssertUntouchedAsync(scene, audit, "a store outage writes nothing");
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("missing")]
    [InlineData("wrong-status")]
    public async Task AllFailures_ReturnTheSameCode(string failure)
    {
        // Acceptance #7: the four failure families are one code with one description and no metadata, so the route
        // cannot be told apart from a probe. Each case here is a distinct mechanism, not a distinct answer.
        var scene = await NewSceneAsync(
            failure == "wrong-status" ? PublicationStatus.Scheduled : PublicationStatus.AwaitingConsent);

        var tokens = failure == "subject"
            ? new FakeActionTokens(new ConsumedActionToken(scene.UserId, Guid.CreateVersion7()))
            : new FakeActionTokens(new ConsumedActionToken(scene.UserId, scene.PublicationId));

        var publicationId = failure == "missing" ? Guid.CreateVersion7() : scene.PublicationId;
        var audit = new RecordingAuditWriter();

        var result = await CancelAsync(
            scene,
            tokens,
            new RecordingQuotaLedger(),
            audit,
            publicationId);

        result.FirstError.Code.ShouldBe(ErrorCodes.CancelTokenInvalid);
        result.FirstError.Description.ShouldBe("The cancel link is invalid, already used or expired.");
        result.FirstError.Metadata.ShouldBeNull("a metadata key would make one failure distinguishable from another");
        result.Errors.Count.ShouldBe(1);

        await AssertUntouchedAsync(scene, audit, $"the '{failure}' refusal writes nothing");
    }

    [Theory]
    [InlineData(PublicationStatus.Scheduled)]
    [InlineData(PublicationStatus.Uploading)]
    [InlineData(PublicationStatus.Processing)]
    [InlineData(PublicationStatus.Published)]
    [InlineData(PublicationStatus.Failed)]
    [InlineData(PublicationStatus.Cancelled)]
    public async Task OnlyTheVetoWindowIsCancellable(PublicationStatus status)
    {
        // The link's guard is the state, not the sibling route's idempotent arm: every other state answers the same
        // generic refusal and keeps the row — including `cancelled`, which the app may have reached first.
        var scene = await NewSceneAsync(status);
        var tokens = new FakeActionTokens(new ConsumedActionToken(scene.UserId, scene.PublicationId));
        var audit = new RecordingAuditWriter();

        var result = await CancelAsync(scene, tokens, new RecordingQuotaLedger(), audit);

        result.FirstError.Code.ShouldBe(ErrorCodes.CancelTokenInvalid);

        (await ScalarAsync(Column(scene, "status"))).ShouldBe(PublicationBuilder.Wire(status));
        audit.Written.ShouldBeEmpty();
        (await OutboxPayloadsAsync(scene)).ShouldBeEmpty();
    }

    /// <summary>
    /// Asserts that a refusal wrote nothing: the row, the outbox and the audit are exactly as the scene seeded them.
    /// </summary>
    /// <param name="scene">The seeded rows.</param>
    /// <param name="audit">The audit writer, which must have seen no entry.</param>
    /// <param name="reason">What the assertion is about, for the failure message.</param>
    private async Task AssertUntouchedAsync(Scene scene, RecordingAuditWriter audit, string reason)
    {
        (await ScalarAsync(Column(scene, "status"))).ShouldBe(PublicationBuilder.Wire(scene.Status), reason);
        (await ScalarAsync(Column(scene, "updated_at::text"))).ShouldBe(scene.UpdatedAt, reason);
        (await OutboxPayloadsAsync(scene)).ShouldBeEmpty(reason);
        audit.Written.ShouldBeEmpty(reason);
    }

    /// <summary>The rows of one scene, as the tests address them.</summary>
    /// <param name="UserId">The publication's owner and the token's owner.</param>
    /// <param name="EpisodeId">The episode the publication belongs to.</param>
    /// <param name="PublicationId">The publication the token is scoped to.</param>
    /// <param name="Status">The state the row was seeded in.</param>
    /// <param name="UpdatedAt">The row's <c>updated_at</c> as seeded, so "unchanged" is one comparison.</param>
    private sealed record Scene(
        Guid UserId,
        Guid EpisodeId,
        Guid PublicationId,
        PublicationStatus Status,
        string UpdatedAt);

    /// <summary>Seeds one owner, series, episode, active connection and the episode's publication.</summary>
    /// <param name="status">The state of the seeded publication.</param>
    private async Task<Scene> NewSceneAsync(PublicationStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var currentUser = new TestCurrentUser { Id = userId };

        await new UserBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var series = await new SeriesBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var episode = await new EpisodeBuilder(Database, currentUser).CreateAsync(userId, series.Id, ct: ct);

        var connection = await new ChannelConnectionBuilder(Database, currentUser)
            .CreateAsync(userId, ChannelConnectionStatus.Active, ct: ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            status,
            ct: ct);

        var scene = new Scene(userId, episode.Id, publicationId, status, string.Empty);

        return scene with { UpdatedAt = await ScalarAsync(Column(scene, "updated_at::text")) };
    }

    /// <summary>
    /// Sends the command the way the route would, through the handler under test, and finishes its unit of work the
    /// way the pipeline's <c>UnitOfWorkBehavior</c> does: save and commit on success, roll back on any refusal.
    /// </summary>
    /// <param name="scene">The seeded rows.</param>
    /// <param name="tokens">What the consume answers.</param>
    /// <param name="ledger">The recording ledger.</param>
    /// <param name="audit">The recording audit writer.</param>
    /// <param name="publicationId">The id the body names; the scene's own unless a test targets another row.</param>
    private async Task<ErrorOr<Success>> CancelAsync(
        Scene scene,
        FakeActionTokens tokens,
        RecordingQuotaLedger ledger,
        RecordingAuditWriter audit,
        Guid? publicationId = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Now);

        await using var context = Database.CreateAppDbContext(new TestCurrentUser { Id = scene.UserId }, clock);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var handler = new CancelPublicationByLinkHandler(
            tokens,
            new PublicationCancelLock(context),
            ledger,
            audit,
            clock,
            NullLogger<CancelPublicationByLinkHandler>.Instance);

        var result = await handler.Handle(
            new CancelPublicationByLinkCommand(publicationId ?? scene.PublicationId, PresentedToken),
            ct);

        if (result.IsError)
        {
            // UC3: an error leaves nothing behind — which is why a refused request does not even spend its token.
            await transaction.RollbackAsync(ct);
        }
        else
        {
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    /// <summary>The <c>publication_updated.v1</c> payloads this scene's publication raised.</summary>
    /// <param name="scene">The seeded rows.</param>
    private Task<IReadOnlyList<string>> OutboxPayloadsAsync(Scene scene) =>
        Database.QueryStringsAsync(
            "SELECT payload::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{scene.PublicationId:D}%'",
            TestContext.Current.CancellationToken);

    /// <summary>One expression of the scene's own publication row.</summary>
    /// <param name="scene">The seeded rows.</param>
    /// <param name="expression">The SQL expression to read.</param>
    private static string Column(Scene scene, string expression) =>
        $"SELECT {expression} FROM publications WHERE id = '{scene.PublicationId:D}'";

    /// <summary>Reads one scalar of the scene's row.</summary>
    /// <param name="sql">The statement.</param>
    private async Task<string> ScalarAsync(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);

        return rows[0];
    }

    /// <summary>
    /// The frozen spellings of the cancelled-publication audit projection, duplicated here because
    /// <c>CancelAuditKeys</c> is internal to the Application assembly and this test project has no
    /// <c>InternalsVisibleTo</c>: the integration test asserts the same four keys against the stored row.
    /// </summary>
    private static class AuditKeys
    {
        /// <summary>The <c>audit_logs.subject_type</c> of a publication row.</summary>
        internal const string EntityType = "publication";

        /// <summary>The audit <c>data</c> key of the cancelled publication id.</summary>
        internal const string PublicationIdKey = "publicationId";

        /// <summary>The audit <c>data</c> key of the episode the publication belongs to.</summary>
        internal const string EpisodeIdKey = "episodeId";

        /// <summary>The audit <c>data</c> key of the status the row left.</summary>
        internal const string PreviousStatusKey = "previousStatus";

        /// <summary>The audit <c>data</c> key of who cancelled.</summary>
        internal const string ActorKey = "actor";
    }

    /// <summary>
    /// The token store of these tests: it records the presented token and the kind, and answers the consume with the
    /// value a test injected. Issuing is never this command's call, so both issue members throw.
    /// </summary>
    private sealed class FakeActionTokens(ErrorOr<ConsumedActionToken> answer) : IUserActionTokenService
    {
        private readonly List<string> _consumed = [];
        private readonly List<UserActionKind> _kinds = [];

        /// <summary>Every presented token, in order.</summary>
        internal List<string> Consumed => _consumed;

        /// <summary>Every kind the calls asked for, in order.</summary>
        internal List<UserActionKind> Kinds => _kinds;

        /// <inheritdoc />
        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The cancel route never issues a token; the send-time minter does.");

        /// <inheritdoc />
        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            Guid subjectId,
            DateTimeOffset expiresAt,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The cancel route never issues a token; the send-time minter does.");

        /// <inheritdoc />
        public Task<ErrorOr<ConsumedActionToken>> ConsumeAsync(
            string token,
            UserActionKind kind,
            CancellationToken cancellationToken)
        {
            _consumed.Add(token);
            _kinds.Add(kind);

            return Task.FromResult(answer);
        }
    }

    /// <summary>The audit writer of these tests: it records every entry the handler writes, and saves nothing.</summary>
    private sealed class RecordingAuditWriter : IAuditWriter
    {
        private readonly List<AuditEntry> _written = [];

        /// <summary>Every entry, in order.</summary>
        internal List<AuditEntry> Written => _written;

        /// <inheritdoc />
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            _written.Add(entry);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The ledger of these tests: a reservation is a defect on this path, so it throws; a release is recorded and
    /// answers a success, because an <c>awaiting_consent</c> row holds no unit.
    /// </summary>
    private sealed class RecordingQuotaLedger : IQuotaLedger
    {
        private readonly List<QuotaReservation> _released = [];

        /// <summary>Every reservation <see cref="ReleaseAsync"/> was handed, in order.</summary>
        internal List<QuotaReservation> Released => _released;

        /// <inheritdoc />
        public Task<ErrorOr<QuotaReservation>> TryReserveAsync(
            string platform,
            DateTimeOffset now,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The cancel route never reserves quota.");

        /// <inheritdoc />
        public Task<ErrorOr<Success>> ReleaseAsync(QuotaReservation reservation, CancellationToken ct = default)
        {
            _released.Add(reservation);

            return Task.FromResult<ErrorOr<Success>>(Result.Success);
        }
    }
}
