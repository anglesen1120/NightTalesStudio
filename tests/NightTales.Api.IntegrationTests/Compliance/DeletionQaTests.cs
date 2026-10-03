using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using NightTales.Worker.Cli;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// Independent QA probes for 13.3.1-T1 (written by the QA agent, not the implementer): caller-transaction atomicity,
/// lease boundary, cross-tenant handler isolation, audit/log PII, cascade-blocking foreign keys, retry after a
/// recovered store, not-yet-due rows, option validation and CLI input validation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string Bucket = "nt-media-dev";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<(int Code, string Out, string Err)> RunCliAsync(DeletionHarness harness, string[] args, string? input = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await DeletionsCommand.RunAsync(args, harness.Host.Services, Ct, new StringReader(input ?? string.Empty), output, error);

        return (code, output.ToString(), error.ToString());
    }

    private async Task<string> StatusOf(Guid id) =>
        (await Database.QueryStringsAsync($"SELECT status || ':' || attempts || ':' || coalesce(completed_at::text, '-') FROM data_deletion_requests WHERE id = '{id}'", Ct))
            .Single();

    private Task<Guid> DueChannel(DeletionHarness harness, DeletionStatus status = DeletionStatus.Pending, int attempts = 0,
        DateTimeOffset? claimedAt = null, TimeSpan? dueIn = null) =>
        DeletionRequestFactory.InsertAsync(
            Database,
            Guid.CreateVersion7(),
            DeletionScope.Channel,
            Guid.CreateVersion7(),
            DeletionReason.InAppRevoke,
            requestedAt: harness.Clock.UtcNow - TimeSpan.FromDays(8),
            dueAt: dueIn is null ? null : harness.Clock.UtcNow + dueIn.Value,
            status: status,
            attempts: attempts,
            claimedAt: claimedAt,
            ct: Ct);

    private sealed record Seeded(Guid UserId, Guid EpisodeId, IReadOnlyList<string> Keys);

    private async Task<Seeded> SeedEpisodeWithBlobsAsync(DeletionHarness harness)
    {
        var userId = (await Database.CreateUserBuilder().CreateAsync(ct: Ct)).Id;
        var actor = new TestCurrentUser { Id = userId };
        var series = await new SeriesBuilder(Database, actor).CreateAsync(userId, ct: Ct);
        var episodes = new EpisodeBuilder(Database, actor);
        var episode = await episodes.CreateAsync(userId, series.Id, ct: Ct);
        await episodes.CreateAssetAsync(userId, episode.Id, ct: Ct);
        await episodes.CreateAssetAsync(userId, episode.Id, NightTales.Domain.Assets.AssetKind.Thumbnail, ct: Ct);

        var keys = (await Database.QueryStringsAsync($"SELECT storage_key FROM assets WHERE episode_id = '{episode.Id}' ORDER BY storage_key", Ct)).ToList();
        keys.Add($"research/{userId:D}/{episode.Id:D}/{Guid.NewGuid():N}/{new string('b', 64)}.txt");
        foreach (var key in keys)
        {
            harness.Blobs.Seed(Bucket, key, [1, 2, 3]);
        }

        return new Seeded(userId, episode.Id, keys);
    }

    // --- UC1: atomicity with the caller's transaction -------------------------------------------------------------

    [Fact]
    public async Task QA_Create_RolledBackCaller_LeavesNoRow()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();

        using (var scope = harness.Host.CreateScope())
        {
            using var pinned = scope.ServiceProvider.GetRequiredService<ISystemContext>().For(user);
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unit.BeginAsync(Ct);
            var created = await scope.ServiceProvider.GetRequiredService<IDeletionRequests>()
                .CreateAsync(user, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct);
            created.IsError.ShouldBeFalse();
            await unit.RollbackAsync(Ct);
        }

        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM data_deletion_requests WHERE user_id = '{user}'", Ct))
            .ShouldBe(["0"], "the duty commits or rolls back with the caller's own transaction");
    }

    [Fact]
    public async Task QA_Create_AfterOpenRowCompleted_StartsANewClock()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();

        var first = (await harness.CreateAsync(user, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct)).Value;
        await Database.ExecuteAsync($"UPDATE data_deletion_requests SET status = 'completed', completed_at = now() WHERE id = '{first.Id}'", Ct);
        harness.Clock.Advance(TimeSpan.FromDays(3));

        var second = (await harness.CreateAsync(user, DeletionScope.Account, null, DeletionReason.AccountDelete, Ct)).Value;

        second.Id.ShouldNotBe(first.Id);
        second.DueAt.ShouldBeGreaterThan(first.DueAt);
    }

    // --- claim / lease boundary ------------------------------------------------------------------------------------

    [Fact]
    public async Task QA_Claim_LeaseBoundary_ExactlyLeaseIsNotReclaimed_OneMicrosecondOlderIs()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var now = harness.Clock.UtcNow;
        var atBoundary = await DueChannel(harness, DeletionStatus.InProgress, 1, now - TimeSpan.FromMinutes(15));
        var justOlder = await DueChannel(harness, DeletionStatus.InProgress, 1, now - TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(10));

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.ShouldHaveSingleItem().RequestId.ShouldBe(justOlder);
        (await StatusOf(atBoundary)).ShouldBe("in_progress:1:-");
    }

    [Fact]
    public async Task QA_Job_NotYetDuePendingRow_IsUntouched_AndCompletedRowIsNeverReExecuted()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var future = await DueChannel(harness, dueIn: TimeSpan.FromHours(1));
        var done = await DeletionRequestFactory.InsertAsync(
            Database, Guid.CreateVersion7(), DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.InAppRevoke,
            harness.Clock.UtcNow - TimeSpan.FromDays(9), status: DeletionStatus.Completed, attempts: 1,
            completedAt: harness.Clock.UtcNow - TimeSpan.FromDays(1), ct: Ct);

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.ShouldBeEmpty();
        (await StatusOf(future)).ShouldBe("pending:0:-");
        (await StatusOf(done)).ShouldStartWith("completed:1:");
    }

    [Fact]
    public async Task QA_Job_FailedRowBelowMaxAttempts_IsRetriedAndExhaustedRowIsNot()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var retry = await DueChannel(harness, DeletionStatus.Failed, attempts: 4);
        var exhausted = await DueChannel(harness, DeletionStatus.Failed, attempts: 5);

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.ShouldHaveSingleItem().RequestId.ShouldBe(retry);
        (await StatusOf(retry)).ShouldStartWith("completed:5:");
        (await StatusOf(exhausted)).ShouldBe("failed:5:-");
    }

    // --- handler isolation / idempotency ---------------------------------------------------------------------------

    [Fact]
    public async Task QA_EpisodeHandler_RequestOfAnotherUser_NeverTouchesTheVictimsEpisodeOrObjects()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var victim = await SeedEpisodeWithBlobsAsync(harness);
        var attacker = Guid.CreateVersion7();
        await DeletionRequestFactory.InsertAsync(
            Database, attacker, DeletionScope.Episode, victim.EpisodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);

        await harness.RunJobAsync(Ct);

        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM episodes WHERE id = '{victim.EpisodeId}'", Ct)).ShouldBe(["1"]);
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM assets WHERE episode_id = '{victim.EpisodeId}'", Ct)).ShouldBe(["2"]);
        foreach (var key in victim.Keys)
        {
            harness.Blobs.Contains(Bucket, key).ShouldBeTrue($"{key} belongs to another user");
        }
    }

    [Fact]
    public async Task QA_EpisodeHandler_StoreRecovers_RetryDeletesEverything_AndLogsCarryNoStorageKey()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var seeded = await SeedEpisodeWithBlobsAsync(harness);
        var id = await DeletionRequestFactory.InsertAsync(
            Database, seeded.UserId, DeletionScope.Episode, seeded.EpisodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);

        harness.Blobs.AsyncFailure = Error.Failure(ErrorCodes.ServiceUnavailable, "down");
        await harness.RunJobAsync(Ct);
        (await StatusOf(id)).ShouldBe("failed:1:-");

        harness.Blobs.AsyncFailure = null;
        await harness.RunJobAsync(Ct);

        (await StatusOf(id)).ShouldStartWith("completed:2:");
        foreach (var key in seeded.Keys)
        {
            harness.Blobs.Contains(Bucket, key).ShouldBeFalse(key);
            harness.Logs.Lines.ShouldNotContain(line => line.Message.Contains(key, StringComparison.Ordinal), "no log line carries a full storage key");
        }

        harness.Logs.Lines.ShouldNotContain(line => line.Message.Contains("research/", StringComparison.Ordinal));
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM audit_logs WHERE entity_id = '{id}'", Ct)).ShouldBe(["1"], "exactly one audit row despite the retry");
    }

    [Fact]
    public async Task QA_AuditRow_CarriesIdsScopeAndReasonOnly()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var user = Guid.CreateVersion7();
        var id = await DeletionRequestFactory.InsertAsync(
            Database, user, DeletionScope.Channel, Guid.CreateVersion7(), DeletionReason.GoogleRevoke,
            harness.Clock.UtcNow - TimeSpan.FromDays(31), ct: Ct);

        await harness.RunJobAsync(Ct);

        var data = (await Database.QueryStringsAsync($"SELECT data::text FROM audit_logs WHERE entity_id = '{id}'", Ct)).Single();
        data.ShouldNotContain("@");
        data.ShouldNotContain("research/");
        data.ShouldContain("google_revoke");
        data.ShouldContain("channel");
        (await Database.QueryStringsAsync($"SELECT coalesce(ip::text, '-') || '|' || coalesce(user_agent, '-') FROM audit_logs WHERE entity_id = '{id}'", Ct))
            .ShouldBe(["-|-"]);
    }

    // --- schema: nothing may block the episode cascade -------------------------------------------------------------

    [Fact]
    public async Task QA_NoForeignKeyToEpisodes_BlocksTheCascade()
    {
        (await Database.QueryStringsAsync(
            "SELECT conrelid::regclass::text || '.' || conname || ':' || confdeltype::text FROM pg_constraint "
            + "WHERE confrelid = 'episodes'::regclass AND contype = 'f' AND confdeltype IN ('a', 'r') ORDER BY 1", Ct))
            .ShouldBeEmpty("a NO ACTION/RESTRICT child would make the episode handler fail forever once such rows exist");
    }

    // --- host options / start-up -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("DataDeletion:BatchSize", "0")]
    [InlineData("DataDeletion:LeaseMinutes", "0")]
    [InlineData("DataDeletion:MaxAttempts", "0")]
    [InlineData("DataDeletion:AlertLeadHours", "-1")]
    [InlineData("DataDeletion:DeferredScopes:0", "bogus")]
    public async Task QA_Worker_FailsToStart_OnInvalidDataDeletionOption(string key, string value)
    {
        await using var host = new WorkerHost(Database, new FakeClock()).WithSetting(key, value);

        await Should.ThrowAsync<Exception>(() => host.StartAsync(Ct));
    }

    // --- CLI input validation --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("deletions")]
    [InlineData("deletions", "export", "--since")]
    [InlineData("deletions", "export", "--since", "not-a-date", "--out", "/tmp/x")]
    [InlineData("deletions", "export", "--since", "2025-12-20T00:00:00Z")]
    [InlineData("deletions", "export", "--out", "/tmp/x")]
    [InlineData("deletions", "export", "since", "2025-12-20T00:00:00Z", "--out", "/tmp/x")]
    [InlineData("deletions", "reapply")]
    [InlineData("deletions", "reapply", "--since", "yesterday")]
    public async Task QA_Cli_MalformedInvocation_ExitsTwoWithUsage_AndWritesNothing(params string[] args)
    {
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => host.WithSetting("DataDeletion:AllowRestoreReapply", "true"), ct: Ct);

        var result = await RunCliAsync(harness, args);

        result.Code.ShouldBe(2, result.Err);
        File.Exists("/tmp/x").ShouldBeFalse();
    }

    [Fact]
    public async Task QA_Cli_Export_ToUnwritablePath_ExitsTwoWithoutLeakingMessage()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);

        var result = await RunCliAsync(harness, ["deletions", "export", "--since", "2025-12-20T00:00:00Z", "--out", "/nonexistent-dir-qa/x.ndjson"]);

        result.Code.ShouldBe(2);
        result.Err.ShouldNotContain("nonexistent-dir-qa", Case.Insensitive, "only the exception type is reported");
    }

    [Fact]
    public async Task QA_Cli_Reapply_TamperedOrInconsistentRows_ExitNonZero_AndInsertNothing()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => host.WithSetting("DataDeletion:AllowRestoreReapply", "true"), ct: Ct);
        var user = Guid.CreateVersion7();
        var now = harness.Clock.UtcNow;
        var since = Iso(now - TimeSpan.FromDays(30));

        string Row(Guid id, Guid u, string scope, Guid? target, string reason, DateTimeOffset requested, DateTimeOffset due) =>
            $$"""{"id":"{{id}}","user_id":"{{u}}","scope":"{{scope}}","target_id":{{(target is null ? "null" : $"\"{target}\"")}},"reason":"{{reason}}","requested_at":"{{Iso(requested)}}","due_at":"{{Iso(due)}}","completed_at":null}""";

        var requested = now - TimeSpan.FromDays(9);

        // Deadline shortened by the file (a forged due_at).
        (await RunCliAsync(harness, ["deletions", "reapply", "--since", since], Row(Guid.CreateVersion7(), user, "channel", Guid.CreateVersion7(), "in_app_revoke", requested, requested.AddDays(1)))).Code.ShouldBe(1);
        // Scope/reason mismatch.
        (await RunCliAsync(harness, ["deletions", "reapply", "--since", since], Row(Guid.CreateVersion7(), user, "episode", Guid.CreateVersion7(), "account_delete", requested, requested.AddDays(7)))).Code.ShouldBe(1);
        // Unknown scope.
        (await RunCliAsync(harness, ["deletions", "reapply", "--since", since], Row(Guid.CreateVersion7(), user, "bogus", null, "user_request", requested, requested.AddDays(7)))).Code.ShouldBe(1);
        // Missing target for a channel row.
        (await RunCliAsync(harness, ["deletions", "reapply", "--since", since], Row(Guid.CreateVersion7(), user, "channel", null, "in_app_revoke", requested, requested.AddDays(7)))).Code.ShouldBe(1);
        // Empty user id.
        (await RunCliAsync(harness, ["deletions", "reapply", "--since", since], Row(Guid.CreateVersion7(), Guid.Empty, "channel", Guid.CreateVersion7(), "in_app_revoke", requested, requested.AddDays(7)))).Code.ShouldBe(1);

        (await Database.QueryStringsAsync("SELECT count(*)::text FROM data_deletion_requests", Ct)).ShouldBe(["0"]);
        (await Database.QueryStringsAsync("SELECT count(*)::text FROM audit_logs WHERE entity_type = 'deletion_request'", Ct)).ShouldBe(["0"]);
    }
}
