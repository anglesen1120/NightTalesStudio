using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// <c>DataDeletionJob</c> and its collaborators (13.3.1-T1, 13 README D4) through the production Worker graph against
/// real PostgreSQL 17: the atomic claim, the lease, the batch cap, one scope per row, the audit row, retries, the
/// deferred scope, the K21 start-up check, the episode handler, the metric phase and the registration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataDeletionJobTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Guid> Due(DeletionHarness harness, Guid? user = null, DeletionScope scope = DeletionScope.Channel,
        DeletionStatus status = DeletionStatus.Pending, int attempts = 0, DateTimeOffset? claimedAt = null, TimeSpan? overdueBy = null) =>
        DeletionRequestFactory.InsertAsync(
            Database,
            user ?? Guid.CreateVersion7(),
            scope,
            scope == DeletionScope.Account ? null : Guid.CreateVersion7(),
            scope == DeletionScope.Account ? DeletionReason.AccountDelete : DeletionReason.InAppRevoke,
            requestedAt: harness.Clock.UtcNow - TimeSpan.FromDays(8) - (overdueBy ?? TimeSpan.Zero),
            status: status,
            attempts: attempts,
            claimedAt: claimedAt,
            ct: Ct);

    private async Task<string> StatusOf(Guid id) =>
        (await Database.QueryStringsAsync($"SELECT status || ':' || attempts || ':' || coalesce(completed_at::text, '-') FROM data_deletion_requests WHERE id = '{id}'", Ct))
            .Single();

    [Fact]
    public async Task Claim_IsExclusiveAcrossTwoConcurrentRuns()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var ids = new List<Guid>();
        for (var index = 0; index < 12; index++)
        {
            ids.Add(await Due(harness));
        }

        await Task.WhenAll(Task.Run(() => harness.RunJobAsync(Ct), Ct), Task.Run(() => harness.RunJobAsync(Ct), Ct));

        harness.Probe.Executions.Select(execution => execution.RequestId).Order().ShouldBe(ids.Order(), "every row ran exactly once");
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests WHERE status = 'completed' AND attempts = 1", Ct))
            .ShouldBe(["12"]);
    }

    [Fact]
    public async Task Claim_ReclaimsStaleInProgressAfterLease()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var stale = await Due(harness, status: DeletionStatus.InProgress, attempts: 1, claimedAt: harness.Clock.UtcNow - TimeSpan.FromMinutes(16));

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.ShouldHaveSingleItem().RequestId.ShouldBe(stale);
        (await StatusOf(stale)).ShouldStartWith("completed:2:");
    }

    [Fact]
    public async Task Claim_DoesNotReclaimBeforeLease()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var fresh = await Due(harness, status: DeletionStatus.InProgress, attempts: 1, claimedAt: harness.Clock.UtcNow - TimeSpan.FromMinutes(5));

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.ShouldBeEmpty();
        (await StatusOf(fresh)).ShouldBe("in_progress:1:-");
    }

    [Fact]
    public async Task Job_ProcessesDueRowsDueFirst_UpToBatchCap()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var ids = new List<Guid>();
        for (var index = 0; index < 25; index++)
        {
            // Row 0 is the most overdue.
            ids.Add(await Due(harness, overdueBy: TimeSpan.FromHours(25 - index)));
        }

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.Select(execution => execution.RequestId).Order().ShouldBe(ids.Take(20).Order());
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests WHERE completed_at IS NULL", Ct)).ShouldBe(["5"]);

        await harness.RunJobAsync(Ct);

        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests WHERE completed_at IS NULL", Ct)).ShouldBe(["0"]);
    }

    [Fact]
    public async Task Job_UsesOneDiScopePerRow_AndSystemContextOncePerScope()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var users = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        foreach (var user in users)
        {
            await Due(harness, user);
        }

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.Count.ShouldBe(3);
        harness.Probe.Executions.Select(execution => execution.ContextHash).Distinct().Count().ShouldBe(3);
        harness.Probe.Executions.Select(execution => execution.CurrentUserId).Order().ShouldBe(users.Cast<Guid?>().Order());
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests WHERE status = 'completed'", Ct)).ShouldBe(["3"]);
    }

    [Fact]
    public async Task Job_Success_WritesCompletedAtAndOneAuditRow()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();
        var id = await Due(harness, user);

        await harness.RunJobAsync(Ct);

        (await StatusOf(id)).ShouldStartWith("completed:1:2026-01-01");
        (await Database.QueryStringsAsync(
            $"SELECT action || '|' || category || '|' || visibility || '|' || subject_user_id || '|' || entity_id FROM audit_logs WHERE entity_type = 'deletion_request'", Ct))
            .ShouldBe([$"account.data_deleted|support|user|{user}|{id}"]);
    }

    [Fact]
    public async Task Job_HandlerFails_RetriesThenStopsAtMaxAttempts()
    {
        await using var harness = await DeletionHarness.StartAsync(
            Database,
            configure: host => host.WithSetting("DataDeletion:MaxAttempts", "3"),
            ct: Ct);
        harness.Probe.Behavior = _ => Error.Failure(ErrorCodes.ServiceUnavailable, "down");
        var id = await Due(harness);

        for (var run = 1; run <= 3; run++)
        {
            await harness.RunJobAsync(Ct);
            (await StatusOf(id)).ShouldBe($"failed:{run}:-");
        }

        await harness.RunJobAsync(Ct);

        (await StatusOf(id)).ShouldBe("failed:3:-", "an exhausted row is never reclaimed and never completed");
        harness.Probe.Executions.Count.ShouldBe(3);
        (await Database.QueryStringsAsync($"SELECT last_error_code FROM data_deletion_requests WHERE id = '{id}'", Ct)).ShouldBe(["service_unavailable"]);
        harness.Metrics()["deletion_request_failed_total{error_code=service_unavailable,scope=channel}"].ShouldBe(3);
        harness.Logs.Lines.ShouldContain(line => line.Level == LogLevel.Error && line.Message.Contains("data_deletion_exhausted"));
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM audit_logs WHERE entity_type = 'deletion_request'", Ct)).ShouldBe(["0"]);
    }

    [Fact]
    public async Task Job_HandlerThrows_RowBecomesFailed_AndBatchContinues()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var bad = await Due(harness, overdueBy: TimeSpan.FromDays(1));
        var good = await Due(harness);
        harness.Probe.Behavior = request => request.Id == bad ? throw new InvalidOperationException("boom user@example.invalid") : new DeletionResult(0, 0);

        await harness.RunJobAsync(Ct);

        (await StatusOf(bad)).ShouldBe("failed:1:-");
        (await StatusOf(good)).ShouldStartWith("completed:1:");
        harness.Logs.Lines.ShouldNotContain(line => line.Message.Contains("user@example.invalid"));
    }

    [Fact]
    public async Task Job_DeferredScope_StaysPendingAcrossTicks_AndLogsOncePerRun()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var ids = new[] { await Due(harness, scope: DeletionScope.Account), await Due(harness, scope: DeletionScope.Account), await Due(harness, scope: DeletionScope.Account) };

        await harness.RunJobAsync(Ct);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.RunJobAsync(Ct);

        foreach (var id in ids)
        {
            (await StatusOf(id)).ShouldBe("pending:0:-");
        }

        harness.Metrics()["deletion_requests_unhandled_total{scope=account}"].ShouldBe(6);
        harness.Logs.Lines.Count(line => line.Level == LogLevel.Warning && line.Message.Contains("data_deletion_scope_unregistered")).ShouldBe(2);
    }

    [Fact]
    public async Task Worker_FailsToStart_WhenRequiredScopeHandlerIsMissing()
    {
        // `account` deferred, `channel` required and unregistered.
        await using var channelMissing = new WorkerHost(Database, new FakeClock()).WithSetting("DataDeletion:DeferredScopes:0", "account");
        var missing = await Should.ThrowAsync<Exception>(() => channelMissing.StartAsync(Ct));
        missing.ToString().ShouldContain("channel");

        // Both handlers absent: even the episode handler is gone.
        await using var noHandlers = new WorkerHost(Database, new FakeClock())
            .WithSetting("DataDeletion:DeferredScopes:0", "account")
            .ConfigureServices(services => services.RemoveAll<IDeletionScopeHandler>());
        var none = await Should.ThrowAsync<Exception>(() => noHandlers.StartAsync(Ct));
        none.ToString().ShouldContain("episode");

        // With channel deferred as well the Worker starts.
        await using var deferred = new WorkerHost(Database, new FakeClock());
        await deferred.StartAsync(Ct);
    }

    [Fact]
    public async Task Job_EpisodeScope_DeletesObjects_RemovesEpisodeRow_KeepsUsageRecords_ThenCompletes()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var seeded = await SeedEpisodeAsync(harness);
        var requestId = await DeletionRequestFactory.InsertAsync(
            Database, seeded.UserId, DeletionScope.Episode, seeded.EpisodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);

        await harness.RunJobAsync(Ct);

        foreach (var key in seeded.AssetKeys.Append(seeded.ResearchKey))
        {
            harness.Blobs.Contains(seeded.Bucket, key).ShouldBeFalse(key);
        }

        harness.Blobs.Contains(seeded.Bucket, seeded.OtherEpisodeKey).ShouldBeTrue("another episode's object survives");
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM episodes WHERE id = '{seeded.EpisodeId}'", Ct)).ShouldBe(["0"]);
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM story_versions WHERE episode_id = '{seeded.EpisodeId}'", Ct)).ShouldBe(["0"]);
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM assets WHERE episode_id = '{seeded.EpisodeId}'", Ct)).ShouldBe(["0"]);
        (await Database.QueryStringsAsync($"SELECT coalesce(episode_id::text, 'null') FROM usage_records WHERE user_id = '{seeded.UserId}'", Ct)).ShouldBe(["null"]);
        (await StatusOf(requestId)).ShouldStartWith("completed:1:");
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM audit_logs WHERE entity_id = '{requestId}' AND action = 'account.data_deleted'", Ct)).ShouldBe(["1"]);
    }

    [Fact]
    public async Task Job_EpisodeScope_RunTwice_IsNoOp()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var seeded = await SeedEpisodeAsync(harness);
        await DeletionRequestFactory.InsertAsync(
            Database, seeded.UserId, DeletionScope.Episode, seeded.EpisodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);
        await harness.RunJobAsync(Ct);
        var deletes = harness.Blobs.CountOf(BlobOperation.Delete);
        var again = await DeletionRequestFactory.InsertAsync(
            Database, seeded.UserId, DeletionScope.Episode, seeded.EpisodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);

        await harness.RunJobAsync(Ct);

        harness.Blobs.CountOf(BlobOperation.Delete).ShouldBe(deletes, "the second run deletes no further objects");
        (await StatusOf(again)).ShouldStartWith("completed:1:");
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM usage_records WHERE user_id = '{seeded.UserId}'", Ct)).ShouldBe(["1"]);
    }

    [Fact]
    public async Task Job_EpisodeScope_BlobStoreDown_FailsRowWithServiceUnavailable()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var seeded = await SeedEpisodeAsync(harness);
        var requestId = await DeletionRequestFactory.InsertAsync(
            Database, seeded.UserId, DeletionScope.Episode, seeded.EpisodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);
        harness.Blobs.AsyncFailure = Error.Failure(ErrorCodes.ServiceUnavailable, "down");

        await harness.RunJobAsync(Ct);

        (await StatusOf(requestId)).ShouldBe("failed:1:-");
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM episodes WHERE id = '{seeded.EpisodeId}'", Ct)).ShouldBe(["1"], "the episode stays until its objects are gone");
    }

    [Fact]
    public async Task Job_MetricPhase_ExportsOpenOverdueAndDueSoonCounts_AndWarnsOnce()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, channelHandler: false, ct: Ct);
        var now = harness.Clock.UtcNow;
        async Task Row(TimeSpan dueIn) => await DeletionRequestFactory.InsertAsync(
            Database, Guid.CreateVersion7(), DeletionScope.Account, null, DeletionReason.AccountDelete,
            requestedAt: now - TimeSpan.FromDays(1), dueAt: now + dueIn, ct: Ct);
        await Row(TimeSpan.FromHours(-2));
        await Row(TimeSpan.FromHours(23));
        await Row(TimeSpan.FromHours(25));

        await harness.RunJobAsync(Ct);

        var metrics = harness.Metrics();
        metrics["deletion_requests_open"].ShouldBe(3);
        metrics["deletion_request_overdue_total"].ShouldBe(1);
        metrics["deletion_requests_due_soon"].ShouldBe(2, "the 23 h row counts, the 25 h row does not");
        harness.Logs.Lines.Count(line => line.Level == LogLevel.Warning && line.Message.Contains("deletion_request_due_soon")).ShouldBe(1);
    }

    [Fact]
    public async Task Job_SendsNoMail_AndKeepsNoAlertState()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        await Due(harness);
        await Due(harness, scope: DeletionScope.Account);

        await harness.RunJobAsync(Ct);

        (await Database.QueryStringsAsync("SELECT count(*)::text FROM email_messages", Ct)).ShouldBe(["0"]);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM outbox_messages WHERE type LIKE 'email%'", Ct)).ShouldBe(["0"]);
        (await Database.QueryStringsAsync(
            "SELECT count(*)::text FROM information_schema.columns WHERE table_name = 'data_deletion_requests' AND column_name LIKE 'alert%'", Ct))
            .ShouldBe(["0"]);
    }

    [Fact]
    public async Task DataDeletionRegistration_RegistersOnceWithHourlyCron()
    {
        await using var host = new WorkerHost(Database, new FakeClock());
        await host.StartAsync(Ct);

        var upserts = host.Jobs.RecurringUpserts.Where(call => call.RecurringId == "data_deletion").ToList();
        upserts.ShouldHaveSingleItem().Cron.ShouldBe("0 * * * *");
        host.Services.GetServices<NightTales.Application.Abstractions.Jobs.IRecurringJobRegistration>()
            .Count(registration => registration.Id == "data_deletion").ShouldBe(1);
    }

    [Fact]
    public async Task DataDeletionRegistration_HonoursEnabledFalse()
    {
        await using var host = new WorkerHost(Database, new FakeClock()).WithSetting("DataDeletion:Enabled", "false");
        await host.StartAsync(Ct);

        host.Jobs.RecurringUpserts.ShouldNotContain(call => call.RecurringId == "data_deletion");
        host.Jobs.RecurringRemovals.ShouldContain("data_deletion");
        _ = new DataDeletionRegistration(Options.Create(new DataDeletionOptions()));
    }

    private sealed record SeededEpisode(Guid UserId, Guid EpisodeId, IReadOnlyList<string> AssetKeys, string ResearchKey, string OtherEpisodeKey, string Bucket);

    private async Task<SeededEpisode> SeedEpisodeAsync(DeletionHarness harness)
    {
        var userId = (await Database.CreateUserBuilder().CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: Ct);
        var episodes = new EpisodeBuilder(Database, actor);
        var episode = await episodes.CreateAsync(userId, series.Id, ct: Ct);
        var other = await episodes.CreateAsync(userId, series.Id, ct: Ct);
        await episodes.CreateAssetAsync(userId, episode.Id, ct: Ct);
        await episodes.CreateAssetAsync(userId, episode.Id, NightTales.Domain.Assets.AssetKind.Thumbnail, ct: Ct);
        await episodes.CreateAssetAsync(userId, other.Id, ct: Ct);

        await Database.ExecuteAsync(
            "INSERT INTO story_versions (id, user_id, episode_id, version, source, title, body, keywords, word_count, content, created_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', '{userId}', '{episode.Id}', 1, 'generated', 't', 'b', '{{}}', 1, '{{}}'::jsonb, now())", Ct);
        await Database.ExecuteAsync(
            "INSERT INTO usage_records (id, user_id, episode_id, series_id, provider, operation, input_tokens, cached_input_tokens, output_tokens, image_count, gpu_seconds, estimated, cost_usd, created_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', '{userId}', '{episode.Id}', '{series.Id}', 'openai', 'story', 1, 0, 1, 0, 0, false, 0.01, now())", Ct);

        var bucket = harness.Host.Services.GetRequiredService<IOptions<StorageSettings>>().Value.Buckets.Media;
        var keys = await Database.QueryStringsAsync($"SELECT storage_key FROM assets WHERE episode_id = '{episode.Id}' ORDER BY storage_key", Ct);
        var otherKey = (await Database.QueryStringsAsync($"SELECT storage_key FROM assets WHERE episode_id = '{other.Id}'", Ct)).Single();
        var research = $"research/{userId:D}/{episode.Id:D}/{Guid.NewGuid():N}/{new string('a', 64)}.txt";

        foreach (var key in keys.Append(research).Append(otherKey))
        {
            harness.Blobs.Seed(bucket, key, [1, 2, 3]);
        }

        return new SeededEpisode(userId, episode.Id, keys, research, otherKey, bucket);
    }
}
