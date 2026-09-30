using System.Data.Common;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// <c>IdempotencyBehavior</c> and its store against a real PostgreSQL 17 (09 README D5/D6, 09.1.3-T2):
/// a replayed key returns the stored response without running the handler, twenty racing twins execute
/// once, a reused key with a different body is a 422, an expired key runs again, a crash between the
/// save and the commit leaves no key, and keys are scoped per user.
/// </summary>
public sealed class IdempotencyBehaviorTests(TestDatabase database) : IntegrationTest(database)
{
    private const int TwinCount = 20;

    [Fact]
    public async Task Idempotency_Replay_ReturnsStoredResponse_WithoutHandler()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = NewRunner(user.Id, clock, _ => Guid.CreateVersion7());

        var command = new PipelineProbeCommand(Body: "replay", IdempotencyKey: "key-replay");
        var first = await runner.SendAsync(command, ct);
        var replayed = await runner.SendAsync(command, ct);

        first.IsError.ShouldBeFalse();
        replayed.IsError.ShouldBeFalse();
        replayed.Value.ShouldBe(first.Value, "the second call returns the stored value");
        runner.Executions.ShouldBe(1, "the handler ran once");
    }

    [Fact]
    public async Task Idempotency_ConcurrentTwins_ExecuteOnce_BothGetSameResponse()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await PipelineProbeRunner.CreateEffectTableAsync(Database, ct);

        var effectId = Guid.CreateVersion7();
        var runner = NewRunner(user.Id, clock, context =>
        {
            context.Database.ExecuteSqlRaw(
                $"INSERT INTO {PipelineProbeRunner.EffectTable} (id) VALUES ({{0}})",
                [effectId]);

            return effectId;
        });

        var command = new PipelineProbeCommand(Body: "twins", IdempotencyKey: "key-twins");
        var results = await Task.WhenAll(
            Enumerable.Range(0, TwinCount).Select(_ => runner.SendAsync(command, ct)));

        results.ShouldAllBe(result => !result.IsError);
        results.Select(result => result.Value).Distinct().ShouldHaveSingleItem();
        runner.Executions.ShouldBe(1, "the twins wait on the unique index and replay the first response");
        CountEffects(effectId).ShouldBe(1, "only the first request wrote its effect row");
        CountKeys(user.Id, "key-twins").ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_SameKeyDifferentBody_Returns422()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = NewRunner(user.Id, clock, _ => Guid.CreateVersion7());

        var first = await runner.SendAsync(new PipelineProbeCommand(Body: "one", IdempotencyKey: "key-reused"), ct);
        var reused = await runner.SendAsync(new PipelineProbeCommand(Body: "two", IdempotencyKey: "key-reused"), ct);

        first.IsError.ShouldBeFalse();
        reused.IsError.ShouldBeTrue();
        reused.FirstError.Code.ShouldBe(ErrorCodes.IdempotencyKeyReused);
        runner.Executions.ShouldBe(1, "the mismatch never reaches the handler");
    }

    [Fact]
    public async Task Idempotency_ExpiredKey_ExecutesAgain()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = NewRunner(user.Id, clock, _ => Guid.CreateVersion7());

        var command = new PipelineProbeCommand(Body: "expiry", IdempotencyKey: "key-expired");
        var first = await runner.SendAsync(command, ct);

        clock.Advance(TimeSpan.FromHours(25));

        var second = await runner.SendAsync(command, ct);

        first.IsError.ShouldBeFalse();
        second.IsError.ShouldBeFalse();
        runner.Executions.ShouldBe(2, "a key outlives its 24 h window and runs again");
    }

    [Fact]
    public async Task Idempotency_CrashBeforeCommit_RetryExecutesOnce()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await PipelineProbeRunner.CreateEffectTableAsync(Database, ct);

        var effectId = Guid.CreateVersion7();
        var command = new PipelineProbeCommand(Body: "crash", IdempotencyKey: "key-crash");

        async ValueTask<ErrorOr<Guid>> Handler(AppDbContext context, PipelineProbeCommand _, CancellationToken token)
        {
            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {PipelineProbeRunner.EffectTable} (id) VALUES ({{0}})",
                [effectId],
                token);

            return effectId;
        }

        var crashing = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            Handler,
            unitOfWork: context => new CrashingUnitOfWork(context));

        var crash = await Should.ThrowAsync<InvalidOperationException>(() => crashing.SendAsync(command, ct));

        crash.Message.ShouldBe(CrashingUnitOfWork.CrashMessage);
        CountKeys(user.Id, "key-crash").ShouldBe(0, "no COMMIT ran, so the claim and the effect are gone");
        CountEffects(effectId).ShouldBe(0);

        var retry = NewRunner(user.Id, clock, Handler);
        var result = await retry.SendAsync(command, ct);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(effectId);
        retry.Executions.ShouldBe(1, "the retry is a fresh claim and executes once");
        CountEffects(effectId).ShouldBe(1, "the crashed attempt left no effect behind");
        CountKeys(user.Id, "key-crash").ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_KeysAreScopedPerUser()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var actor = new TestCurrentUser();
        var userA = await new UserBuilder(Database, actor).CreateAsync(ct: ct);
        var userB = await new UserBuilder(Database, actor).CreateAsync(ct: ct);

        var runnerA = NewRunner(userA.Id, clock, _ => Guid.CreateVersion7());
        var runnerB = NewRunner(userB.Id, clock, _ => Guid.CreateVersion7());

        var command = new PipelineProbeCommand(Body: "scoped", IdempotencyKey: "key-shared");
        var resultA = await runnerA.SendAsync(command, ct);
        var resultB = await runnerB.SendAsync(command, ct);

        resultA.IsError.ShouldBeFalse();
        resultB.IsError.ShouldBeFalse();
        resultB.Value.ShouldNotBe(resultA.Value, "the same key value of another user is another row");
        runnerA.Executions.ShouldBe(1);
        runnerB.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_RequestHash_IgnoresTheKeyAndCoversTheBody()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var runner = NewRunner(user.Id, clock, _ => Guid.CreateVersion7());

        // One body under two keys, and a second body: the hash identifies the body alone.
        await runner.SendAsync(new PipelineProbeCommand(Body: "same", IdempotencyKey: "hash-a"), ct);
        await runner.SendAsync(new PipelineProbeCommand(Body: "same", IdempotencyKey: "hash-b"), ct);
        await runner.SendAsync(new PipelineProbeCommand(Body: "other", IdempotencyKey: "hash-c"), ct);

        HashOf(user.Id, "hash-a").ShouldBe(HashOf(user.Id, "hash-b"), "the idempotency key never enters the hash");
        HashOf(user.Id, "hash-a").ShouldNotBe(HashOf(user.Id, "hash-c"), "the body does");
        HashOf(user.Id, "hash-a").Length.ShouldBe(IdempotencyKey.HashLength);
        runner.Executions.ShouldBe(3, "different keys are different rows, so each one executes once");
    }

    /// <summary>A runner whose handler writes one effect row and returns its id.</summary>
    private PipelineProbeRunner NewRunner(Guid userId, FakeClock clock, Func<AppDbContext, Guid> effect) =>
        NewRunner(userId, clock, (context, command, token) => new ValueTask<ErrorOr<Guid>>(effect(context)));

    private PipelineProbeRunner NewRunner(
        Guid userId,
        FakeClock clock,
        Func<AppDbContext, PipelineProbeCommand, CancellationToken, ValueTask<ErrorOr<Guid>>> handler) =>
        new(Database, new TestCurrentUser { Id = userId }, clock, handler);

    private long CountEffects(Guid effectId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM probe_pipeline_effects WHERE id = '{effectId}'");

    private long CountKeys(Guid userId, string key) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{userId}' AND key = '{key}'");

    /// <summary>The stored canonical request hash of one key, the way the behaviour computed it.</summary>
    private string HashOf(Guid userId, string key) =>
        SqlProbe.ScalarText(Database, $"SELECT request_hash FROM idempotency_keys WHERE user_id = '{userId}' AND key = '{key}'");

    /// <summary>
    /// Simulates the crash the protocol must survive (09 README D6): the handler's effects are written,
    /// <c>SaveChanges</c> runs, and the transaction is then aborted before it can commit — so neither the
    /// key nor the effects survive, exactly like a process death between the save and the COMMIT.
    /// </summary>
    private sealed class CrashingUnitOfWork(AppDbContext database) : IUnitOfWork
    {
        internal const string CrashMessage = "probe crash before commit (09.1.3-T2)";

        private readonly EfUnitOfWork _inner = new(database);

        public Task BeginAsync(CancellationToken cancellationToken) => _inner.BeginAsync(cancellationToken);

        public async Task<ErrorOr<Success>> CommitAsync(CancellationToken cancellationToken)
        {
            await database.SaveChangesAsync(cancellationToken);

            // The crash: the statement fails inside the open transaction and PostgreSQL aborts the whole
            // transaction, so nothing of it can ever become visible. The expected database error is
            // swallowed — the state it leaves is the point.
            try
            {
                await database.Database.ExecuteSqlRawAsync("SELECT 1/0", cancellationToken);
            }
            catch (DbException)
            {
            }

            throw new InvalidOperationException(CrashMessage);
        }

        public Task RollbackAsync(CancellationToken cancellationToken) => _inner.RollbackAsync(cancellationToken);
    }
}
