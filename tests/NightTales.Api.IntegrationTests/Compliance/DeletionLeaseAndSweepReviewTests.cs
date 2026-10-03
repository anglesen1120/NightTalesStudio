using System.Runtime.CompilerServices;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Features.Compliance.Deletion;
using NightTales.Domain.Compliance.Deletion;
using NightTales.Infrastructure.Compliance.Deletion.Handlers;
using NightTales.TestKit;
using NightTales.TestKit.Compliance;
using NightTales.TestKit.Database;
using NightTales.TestKit.Storage;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Compliance;

/// <summary>
/// Review fixes of the deletion engine (13.3.1-T1): CR-1/PERF-1 (a row is claimed right before it runs and its
/// completion/failure only lands while that claim is still ours), CR-3 (a provider timeout surfacing as an uncancelled
/// <see cref="OperationCanceledException"/> fails the row instead of aborting the batch; stale claims stop at
/// <c>MaxAttempts</c>) and PERF-2 (the episode sweep deletes in bounded chunks).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeletionLeaseAndSweepReviewTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Micro(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private Task<Guid> Due(
        DeletionHarness harness,
        DeletionScope scope = DeletionScope.Channel,
        DeletionStatus status = DeletionStatus.Pending,
        int attempts = 0,
        DateTimeOffset? claimedAt = null,
        TimeSpan? overdueBy = null,
        Guid? target = null) =>
        DeletionRequestFactory.InsertAsync(
            Database,
            Guid.CreateVersion7(),
            scope,
            target ?? Guid.CreateVersion7(),
            scope == DeletionScope.Episode ? DeletionReason.UserRequest : DeletionReason.InAppRevoke,
            requestedAt: harness.Clock.UtcNow - TimeSpan.FromDays(8) - (overdueBy ?? TimeSpan.Zero),
            status: status,
            attempts: attempts,
            claimedAt: claimedAt,
            ct: Ct);

    private async Task<string> StatusOf(Guid id) =>
        (await Database.QueryStringsAsync(
            $"SELECT status || ':' || attempts || ':' || coalesce(last_error_code, '-') FROM data_deletion_requests WHERE id = '{id}'", Ct))
            .Single();

    private async Task<string> AuditCount(Guid id) =>
        (await Database.QueryStringsAsync($"SELECT count(*)::text FROM audit_logs WHERE entity_id = '{id}' AND action = 'account.data_deleted'", Ct))
            .Single();

    [Fact]
    public async Task Job_LeaseLapsesMidBatch_EachRowCompletesOnce_AndTheDelayedWorkerCannotRerunOrCompleteTwice()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var first = await Due(harness, overdueBy: TimeSpan.FromDays(1));
        var second = await Due(harness);
        var nested = false;

        // Worker A is stuck inside its first row past the lease; worker B starts a run meanwhile.
        harness.Probe.Behavior = request =>
        {
            if (request.Id == first && !nested)
            {
                nested = true;
                harness.Clock.Advance(TimeSpan.FromMinutes(20));
#pragma warning disable xUnit1031 // The handler seam is synchronous by contract.
                harness.RunJobAsync(Ct).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }

            return new DeletionResult(1, 1);
        };

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.Count(execution => execution.RequestId == second).ShouldBe(1, "A never runs a row B already completed");
        (await StatusOf(first)).ShouldBe("completed:2:-");
        (await StatusOf(second)).ShouldBe("completed:1:-");
        (await AuditCount(first)).ShouldBe("1", "A's late completion wrote nothing");
        (await AuditCount(second)).ShouldBe("1");
    }

    [Fact]
    public async Task Executor_ClaimReplacedDuringHandler_DoesNotCompleteOrAudit()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var claimed = Micro(harness.Clock.UtcNow);
        var id = await Due(harness, status: DeletionStatus.InProgress, attempts: 1, claimedAt: claimed);
        harness.Probe.Behavior = _ => StealClaim(id);

        var outcome = await harness.Host.Services.GetRequiredService<DeletionRowExecutor>()
            .ExecuteAsync(await UserOf(id), id, DeletionActor.Job, Ct, claimed);

        outcome.Kind.ShouldBe(DeletionOutcomeKind.NotClaimed);
        (await StatusOf(id)).ShouldBe("in_progress:1:-");
        (await AuditCount(id)).ShouldBe("0");
    }

    [Fact]
    public async Task Executor_ClaimReplacedDuringHandler_FailureDoesNotTouchTheNewClaim()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var claimed = Micro(harness.Clock.UtcNow);
        var id = await Due(harness, status: DeletionStatus.InProgress, attempts: 1, claimedAt: claimed);
        harness.Probe.Behavior = _ =>
        {
            StealClaim(id).IsError.ShouldBeFalse();

            return Error.Failure("boom");
        };

        await harness.Host.Services.GetRequiredService<DeletionRowExecutor>()
            .ExecuteAsync(await UserOf(id), id, DeletionActor.Job, Ct, claimed);

        (await StatusOf(id)).ShouldBe("in_progress:1:-", "B's active claim is not marked failed by A");
    }

    [Fact]
    public async Task Executor_TokenOlderThanTheRowsClaim_SkipsTheHandler()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var now = Micro(harness.Clock.UtcNow);
        var id = await Due(harness, status: DeletionStatus.InProgress, attempts: 2, claimedAt: now);

        var outcome = await harness.Host.Services.GetRequiredService<DeletionRowExecutor>()
            .ExecuteAsync(await UserOf(id), id, DeletionActor.Job, Ct, now - TimeSpan.FromMinutes(20));

        outcome.Kind.ShouldBe(DeletionOutcomeKind.NotClaimed);
        harness.Probe.Executions.ShouldBeEmpty();
        (await StatusOf(id)).ShouldBe("in_progress:2:-");
    }

    [Fact]
    public async Task Claim_StaleInProgress_CountsTowardsMaxAttempts_AndStopsAtTheCap()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var stale = harness.Clock.UtcNow - TimeSpan.FromMinutes(16);
        var exhausted = await Due(harness, status: DeletionStatus.InProgress, attempts: 5, claimedAt: stale);
        var lastChance = await Due(harness, status: DeletionStatus.InProgress, attempts: 4, claimedAt: stale);

        await harness.RunJobAsync(Ct);

        harness.Probe.Executions.ShouldHaveSingleItem().RequestId.ShouldBe(lastChance);
        (await StatusOf(exhausted)).ShouldBe("in_progress:5:-");
        (await StatusOf(lastChance)).ShouldBe("completed:5:-");
    }

    [Fact]
    public async Task Job_HandlerThrowsUncancelledOperationCanceled_FailsTheRow_AndBatchContinues()
    {
        await using var harness = await DeletionHarness.StartAsync(Database, ct: Ct);
        var timedOut = await Due(harness, overdueBy: TimeSpan.FromDays(1));
        var good = await Due(harness);
        harness.Probe.Behavior = request => request.Id == timedOut ? throw new OperationCanceledException("provider timeout") : new DeletionResult(0, 0);

        await harness.RunJobAsync(Ct);

        (await StatusOf(timedOut)).ShouldBe("failed:1:service_unavailable");
        (await StatusOf(good)).ShouldBe("completed:1:-");
    }

    [Fact]
    public async Task Job_EpisodeListingTimesOutUncancelled_FailsTheRowAsServiceUnavailable_AndBatchContinues()
    {
        var store = new ScriptedBlobStore(new InMemoryBlobStore()) { ListThrows = () => new OperationCanceledException("list timeout") };
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => UseStore(host, store), ct: Ct);
        var episode = await Due(harness, DeletionScope.Episode, overdueBy: TimeSpan.FromDays(1));
        var good = await Due(harness);

        await harness.RunJobAsync(Ct);

        (await StatusOf(episode)).ShouldBe("failed:1:service_unavailable");
        (await StatusOf(good)).ShouldBe("completed:1:-");
    }

    [Fact]
    public async Task Job_EpisodeSweep_DeletesEveryKeyBeyondOneChunk_AsTheListingStreams()
    {
        var inner = new InMemoryBlobStore();
        var store = new ScriptedBlobStore(inner);
        await using var harness = await DeletionHarness.StartAsync(Database, configure: host => UseStore(host, store), ct: Ct);

        var userId = Guid.CreateVersion7();
        var episodeId = Guid.CreateVersion7();
        var bucket = harness.Host.Services.GetRequiredService<IOptions<StorageSettings>>().Value.Buckets.Media;
        var total = (EpisodeDeletionScopeHandler.SweepChunkSize * 2) + 37;
        var prefix = EpisodeDeletionScopeHandler.ResearchPrefix(userId, episodeId);
        for (var index = 0; index < total; index++)
        {
            inner.Seed(bucket, $"{prefix}{index:D5}.txt", [1]);
        }

        inner.Seed(bucket, $"research/{userId:D}/{Guid.CreateVersion7():D}/other.txt", [1]);
        var id = await DeletionRequestFactory.InsertAsync(
            Database, userId, DeletionScope.Episode, episodeId, DeletionReason.UserRequest,
            harness.Clock.UtcNow - TimeSpan.FromDays(8), ct: Ct);

        await harness.RunJobAsync(Ct);

        (await StatusOf(id)).ShouldBe("completed:1:-");
        inner.CountOf(BlobOperation.Delete).ShouldBe(total, "every swept key, and nothing else, is deleted");
        for (var index = 0; index < total; index++)
        {
            inner.Contains(bucket, $"{prefix}{index:D5}.txt").ShouldBeFalse();
        }

        store.KeysYieldedAtFirstDelete.ShouldBeLessThanOrEqualTo(
            EpisodeDeletionScopeHandler.SweepChunkSize, "deletion starts once one chunk is buffered, not after the whole listing");
    }

    private static void UseStore(NightTales.TestKit.Threading.WorkerHost host, IBlobStore store) =>
        host.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton(store);
        });

    private ErrorOr<DeletionResult> StealClaim(Guid id)
    {
#pragma warning disable xUnit1031 // The handler seam is synchronous by contract.
        Database.ExecuteAsync(
            $"UPDATE data_deletion_requests SET claimed_at = claimed_at + interval '20 minutes' WHERE id = '{id}'", Ct)
            .GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        return new DeletionResult(1, 1);
    }

    private async Task<Guid> UserOf(Guid id) =>
        Guid.Parse((await Database.QueryStringsAsync($"SELECT user_id::text FROM data_deletion_requests WHERE id = '{id}'", Ct)).Single());

    /// <summary>Delegates to an in-memory store; can fail the listing and records how far the listing had got at the first delete.</summary>
    private sealed class ScriptedBlobStore(InMemoryBlobStore inner) : IBlobStore
    {
        private int _yielded;
        private int _firstDelete = -1;

        public Func<Exception>? ListThrows { get; set; }

        public int KeysYieldedAtFirstDelete => _firstDelete;

        public Task<ErrorOr<BlobInfo>> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken) =>
            inner.PutAsync(bucket, key, content, contentType, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, cancellationToken);

        public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, long fromByte, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(bucket, key, fromByte, cancellationToken);

        public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            if (_firstDelete < 0)
            {
                _firstDelete = _yielded;
            }

            return inner.DeleteAsync(bucket, key, cancellationToken);
        }

        public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken) =>
            inner.ExistsAsync(bucket, key, cancellationToken);

        public async IAsyncEnumerable<string> ListKeysAsync(
            string bucket,
            string prefix,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (ListThrows is { } fail)
            {
                await Task.Yield();

                throw fail();
            }

            await foreach (var key in inner.ListKeysAsync(bucket, prefix, cancellationToken).ConfigureAwait(false))
            {
                _yielded++;

                yield return key;
            }
        }

        public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName) =>
            inner.CreateReadUrl(bucket, key, ttl, downloadFileName);
    }
}
