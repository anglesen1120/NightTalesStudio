using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Persistence.Outbox;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.Infrastructure.Publishing;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Publishing;

/// <summary>
/// <c>IPlatformIncidentStore</c> against the class Postgres (06.2.3-T2 Tests): the idempotent raise — one row, one
/// <c>platform_incident_raised.v1</c> outbox row and one <c>publishing.incident_raised</c> audit row however many
/// publications report the refusal, including two concurrent raisers — the same-transaction contract (a rollback leaves
/// none of the three), the platform-wide affected count, the open-incident probe and the operator's clear.
/// </summary>
/// <remarks>
/// Every test names its own OAuth client, so the rows one test leaves behind cannot answer another test's probe: the
/// class database is shared by this class, and <c>ux_platform_incidents_open</c> is keyed by
/// <c>(platform, oauth_client_id)</c>. The store is built by hand rather than resolved from the host: it needs a
/// context whose transaction the test controls, and the unit of work it joins is the scope's.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlatformIncidentStoreTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Platform = "youtube";

    private const string RaisedEventType = "platform_incident_raised.v1";

    private const string ClearedEventType = "platform_incident_cleared.v1";

    private static readonly DateTimeOffset RaisedAt = new(2026, 10, 22, 3, 0, 0, TimeSpan.Zero);

    /// <summary>One registry for the class: it scans the Domain assembly, exactly as <c>AddPersistence</c> does.</summary>
    private static readonly OutboxEventRegistry Registry = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Incident_RepeatedRaises_CreateOneRowAndOneEvent()
    {
        var client = FreshClient();
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var unitOfWork = new EfUnitOfWork(db);
        await unitOfWork.BeginAsync(Ct);

        var first = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        var second = await Store(db).RaiseAsync(Platform, client, "unauthorized_client", RaisedAt.AddMinutes(1), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        first.IsError.ShouldBeFalse();
        first.Value.IsNew.ShouldBeTrue("this call inserted the row");
        second.IsError.ShouldBeFalse();
        second.Value.IsNew.ShouldBeFalse("the incident was already open, so this call wrote nothing");
        second.Value.IncidentId.ShouldBe(first.Value.IncidentId);

        IncidentRows(client).ShouldBe(1, "ux_platform_incidents_open keeps one open row per client");
        OutboxRows(RaisedEventType, client).ShouldBe(1, "only the raising call queues the event");
        AuditRows(PlatformIncidentStore.RaisedAuditAction, client).ShouldBe(1);
    }

    [Fact]
    public async Task Incident_OpenRow_SurvivesConcurrentRaises()
    {
        var client = FreshClient();
        await using var first = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        await using var second = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var firstUnitOfWork = new EfUnitOfWork(first);
        var secondUnitOfWork = new EfUnitOfWork(second);
        await firstUnitOfWork.BeginAsync(Ct);
        await secondUnitOfWork.BeginAsync(Ct);

        var winner = await Store(first).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);

        // The loser's INSERT blocks on the winner's uncommitted row: committing the winner makes it take the conflict
        // path, which is what makes a storm of refusals one incident.
        var loser = Store(second).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        (await firstUnitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();
        var loserResult = await loser;
        (await secondUnitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        winner.Value.IsNew.ShouldBeTrue();
        loserResult.IsError.ShouldBeFalse();
        loserResult.Value.IsNew.ShouldBeFalse();
        loserResult.Value.IncidentId.ShouldBe(winner.Value.IncidentId);
        IncidentRows(client).ShouldBe(1);
        OutboxRows(RaisedEventType, client).ShouldBe(1);
        AuditRows(PlatformIncidentStore.RaisedAuditAction, client).ShouldBe(1);
    }

    [Fact]
    public async Task Incident_RolledBackUnitOfWork_LeavesNeitherRowNorEventNorAudit()
    {
        var client = FreshClient();
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var unitOfWork = new EfUnitOfWork(db);

        await unitOfWork.BeginAsync(Ct);
        var raised = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        raised.IsError.ShouldBeFalse();
        await unitOfWork.RollbackAsync(Ct);

        IncidentRows(client).ShouldBe(0, "the row, its outbox row and its audit row are one unit of work");
        OutboxRows(RaisedEventType, client).ShouldBe(0);
        AuditRows(PlatformIncidentStore.RaisedAuditAction, client).ShouldBe(0);
    }

    [Fact]
    public async Task Incident_AuditRow_IsInternalSupportWithoutASubjectAndCarriesNoSecret()
    {
        var client = FreshClient();
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var unitOfWork = new EfUnitOfWork(db);
        await unitOfWork.BeginAsync(Ct);
        (await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct)).IsError.ShouldBeFalse();
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        SqlProbe.QueryStrings(
                Database,
                $"""
                SELECT actor_type || '|' || category || '|' || visibility || '|' || coalesce(actor_user_id::text, '-')
                    || '|' || coalesce(subject_user_id::text, '-') || '|' || entity_type
                FROM audit_logs
                WHERE action = '{PlatformIncidentStore.RaisedAuditAction}' AND data->>'oauthClientId' = '{client}'
                """)
            .ShouldBe(["system|support|internal|-|-|platform_incident"], "an incident is platform-wide: no subject user");

        SqlProbe.QueryStrings(
                Database,
                "SELECT key FROM audit_logs, jsonb_each(data) "
                + $"WHERE action = '{PlatformIncidentStore.RaisedAuditAction}' AND data->>'oauthClientId' = '{client}' "
                + "ORDER BY key")
            .ShouldBe(["affectedConnections", "errorCode", "incidentId", "oauthClientId", "platform"]);

        SqlProbe.Count(
                Database,
                "SELECT count(*) FROM audit_logs WHERE action = "
                + $"'{PlatformIncidentStore.RaisedAuditAction}' AND data->>'oauthClientId' = '{client}' "
                + "AND data::text ~* '(token|session|secret|title|description)'")
            .ShouldBe(0, "the audit row carries ids and codes only (06 README D17)");
    }

    [Fact]
    public async Task Incident_AffectedConnections_CountsOnlyActiveConnectionsOfThatClient()
    {
        var client = FreshClient();
        await SeedConnectionAsync(client, "channel-active", "active");
        await SeedConnectionAsync(client, "channel-revoked", "revoked");
        await SeedConnectionAsync(FreshClient(), "channel-other-client", "active");

        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var unitOfWork = new EfUnitOfWork(db);
        await unitOfWork.BeginAsync(Ct);

        var raised = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        raised.Value.AffectedConnections.ShouldBe(
            1,
            "the count is platform-wide and only the armed client's live connections count");
        SqlProbe.ScalarText(
                Database,
                $"SELECT data->>'affectedConnections' FROM audit_logs WHERE action = "
                + $"'{PlatformIncidentStore.RaisedAuditAction}' AND data->>'oauthClientId' = '{client}'")
            .ShouldBe("1");
    }

    [Fact]
    public async Task HasOpenIncident_AnswersTheIdWhileOpen_AndNullAfterTheClear()
    {
        var client = FreshClient();
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));

        (await Store(db).HasOpenIncidentAsync(Platform, client, Ct)).Value.ShouldBeNull("no incident is open yet");

        var unitOfWork = new EfUnitOfWork(db);
        await unitOfWork.BeginAsync(Ct);
        var raised = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        var whileOpen = await Store(db).HasOpenIncidentAsync(Platform, client, Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        whileOpen.Value.ShouldBe(raised.Value.IncidentId, "the job needs the id for Publication.HoldForIncident");

        await unitOfWork.BeginAsync(Ct);
        var cleared = await Store(db).ClearAsync(raised.Value.IncidentId, "linh", "rotated the client", RaisedAt.AddHours(1), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        cleared.Value.AlreadyCleared.ShouldBeFalse();
        (await Store(db).HasOpenIncidentAsync(Platform, client, Ct)).Value.ShouldBeNull("a cleared incident no longer holds");
    }

    [Fact]
    public async Task Clear_IsIdempotent_AndAnUnknownIdIsNotFound()
    {
        var client = FreshClient();
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var unitOfWork = new EfUnitOfWork(db);

        await unitOfWork.BeginAsync(Ct);
        var raised = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        var first = await Store(db).ClearAsync(raised.Value.IncidentId, "linh", "rotated the client", RaisedAt.AddHours(1), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        first.IsError.ShouldBeFalse();
        first.Value.AlreadyCleared.ShouldBeFalse();
        first.Value.PublicationsRescheduled.ShouldBe(0, "the sweep belongs to 06.2.3-T3");
        first.Value.Remaining.ShouldBeFalse();
        OutboxRows(ClearedEventType, client).ShouldBe(1);

        // The clear's audit row is 06.2.3-T3's clear command's (only its commit knows `data.rescheduled`), so the store
        // writes none: a second, competing `publishing.incident_cleared` would break T3's one-row-per-clear assertion.
        AuditRows(PlatformIncidentStore.ClearedAuditAction, client).ShouldBe(0);

        await unitOfWork.BeginAsync(Ct);
        var repeat = await Store(db).ClearAsync(raised.Value.IncidentId, "linh", "again", RaisedAt.AddHours(2), Ct);
        var unknown = await Store(db).ClearAsync(Guid.CreateVersion7(), "linh", "nothing", RaisedAt.AddHours(2), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        repeat.IsError.ShouldBeFalse();
        repeat.Value.AlreadyCleared.ShouldBeTrue("a second clear writes nothing");
        OutboxRows(ClearedEventType, client).ShouldBe(1);
        AuditRows(PlatformIncidentStore.ClearedAuditAction, client).ShouldBe(0);

        unknown.IsError.ShouldBeTrue();
        unknown.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Raise_AfterClear_OpensASecondRow()
    {
        var client = FreshClient();
        await using var db = Database.CreateAppDbContext(clock: new FakeClock(RaisedAt));
        var unitOfWork = new EfUnitOfWork(db);

        await unitOfWork.BeginAsync(Ct);
        var first = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt, Ct);
        (await Store(db).ClearAsync(first.Value.IncidentId, "linh", "rotated the client", RaisedAt.AddHours(1), Ct))
            .IsError.ShouldBeFalse();
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        await unitOfWork.BeginAsync(Ct);
        var second = await Store(db).RaiseAsync(Platform, client, "invalid_client", RaisedAt.AddHours(2), Ct);
        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        second.IsError.ShouldBeFalse();
        second.Value.IsNew.ShouldBeTrue("the cleared row left ux_platform_incidents_open");
        second.Value.IncidentId.ShouldNotBe(first.Value.IncidentId);
        IncidentRows(client).ShouldBe(2, "one cleared row and the new open one");
        SqlProbe.Count(
                Database,
                $"SELECT count(*) FROM platform_incidents WHERE oauth_client_id = '{client}' AND cleared_at IS NULL")
            .ShouldBe(1);
    }

    /// <summary>
    /// The store as the upload job composes it: the scope's context, the same <c>EfAuditWriter</c>, the class registry
    /// and the default statement timeout.
    /// </summary>
    private static PlatformIncidentStore Store(AppDbContext db) => new(
        db,
        new EfAuditWriter(db),
        Registry,
        Options.Create(new PersistenceOptions()),
        NullLogger<PlatformIncidentStore>.Instance);

    /// <summary>A client id no other test used, so the class database's rows stay isolated per test.</summary>
    private static string FreshClient() => "client-" + Guid.CreateVersion7().ToString("N");

    private long IncidentRows(string client) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM platform_incidents WHERE oauth_client_id = '{client}'");

    private long OutboxRows(string type, string client) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM outbox_messages WHERE type = '{type}' AND payload->>'oauthClientId' = '{client}'");

    private long AuditRows(string action, string client) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM audit_logs WHERE action = '{action}' AND data->>'oauthClientId' = '{client}'");

    /// <summary>
    /// One <c>channel_connections</c> row in the given status, written as raw SQL because the count under test must see
    /// rows of owners the scope does not have. The table has no foreign key (the row outlives the account), so no
    /// <c>users</c> row is needed, and each row names its own external channel id, which is what
    /// <c>ux_channel_connections_account</c> keys on.
    /// </summary>
    private Task SeedConnectionAsync(string client, string externalAccountId, string status) =>
        Database.ExecuteAsync(
            $"""
             INSERT INTO channel_connections (id, user_id, platform, external_account_id, display_name, status,
                                              access_token_enc, refresh_token_enc, token_expires_at, scope,
                                              oauth_client_id, last_verified_at, connected_at)
             VALUES ('{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}', 'youtube', '{externalAccountId}',
                     'Seed channel', '{status}', 'seed-access-token-enc', 'seed-refresh-token-enc',
                     now() + interval '1 hour', 'https://www.googleapis.com/auth/youtube.upload',
                     '{client}', now(), now())
             """,
            Ct);
}
