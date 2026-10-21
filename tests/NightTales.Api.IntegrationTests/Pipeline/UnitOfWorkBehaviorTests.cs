using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Npgsql;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// <c>UnitOfWorkBehavior</c> against a real PostgreSQL 17 (09 README D5, 09.1.3-T2): one transaction
/// per command, opened before the handler, committing the handler's effects and the idempotency claim
/// together and leaving nothing behind on an error result or a thrown exception. It also proves the lock
/// semantics 02 relies on (<c>pg_advisory_xact_lock</c> held until the commit) and the lost-update
/// mapping.
/// </summary>
/// <remarks>
/// The outbox half of the original "effects and outbox rows together" assertion moved to
/// <c>OutboxSaveChangesInterceptorTests</c> (09.2.4-T1), which writes a real domain event through the real
/// interceptor; until then this class had to add an outbox row by hand.
/// </remarks>
public sealed class UnitOfWorkBehaviorTests(TestDatabase database) : IntegrationTest(database)
{
    private const string HandlerFailureMessage = "probe handler failure (09.1.3-T2)";

    [Fact]
    public async Task UnitOfWork_Success_CommitsEffectsAndIdempotencyClaim()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await PipelineProbeRunner.CreateEffectTableAsync(Database, ct);

        var effectId = Guid.CreateVersion7();
        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            async (context, command, token) =>
            {
                await InsertEffectAsync(context, effectId, token);

                return effectId;
            });

        var result = await runner.SendAsync(new PipelineProbeCommand(IdempotencyKey: "commit"), ct);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(effectId);
        CountEffects(effectId).ShouldBe(1, "the effect row the handler wrote is committed");
        CountKeys(user.Id, "commit").ShouldBe(1, "the idempotency claim committed with the effects it guards");
    }

    [Fact]
    public async Task UnitOfWork_ErrorResult_PersistsNothing()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await PipelineProbeRunner.CreateEffectTableAsync(Database, ct);

        var effectId = Guid.CreateVersion7();
        const string key = "rollback";
        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            async (context, command, token) =>
            {
                await InsertEffectAsync(context, effectId, token);

                return Error.Failure("probe_failure", "The probe command failed on purpose (09.1.3-T2).");
            });

        var result = await runner.SendAsync(new PipelineProbeCommand(IdempotencyKey: key), ct);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("probe_failure");
        CountEffects(effectId).ShouldBe(0, "an error result rolls the transaction back");
        CountKeys(user.Id, key).ShouldBe(0, "the claim is removed with the effects it guards");
    }

    [Fact]
    public async Task UnitOfWork_HandlerThrows_RollsBackAndRethrows()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await PipelineProbeRunner.CreateEffectTableAsync(Database, ct);

        var effectId = Guid.CreateVersion7();
        const string key = "throw";
        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            async (context, command, token) =>
            {
                await InsertEffectAsync(context, effectId, token);

                throw new InvalidOperationException(HandlerFailureMessage);
            });

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => runner.SendAsync(new PipelineProbeCommand(IdempotencyKey: key), ct));

        exception.Message.ShouldBe(HandlerFailureMessage, "the handler's own exception reaches the caller");
        CountEffects(effectId).ShouldBe(0);
        CountKeys(user.Id, key).ShouldBe(0);
    }

    [Fact]
    public async Task UnitOfWork_LostUpdate_ReturnsConcurrencyConflict()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        var actor = new TestCurrentUser { Id = user.Id };
        var series = await new SeriesBuilder(Database, actor, clock).CreateAsync(user.Id, ct: ct);

        var twinLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = new PipelineProbeRunner(
            Database,
            actor,
            clock,
            (context, command, token) => RenameAsync(context, series.Id, "first title", token));

        // The second scope loads the series at the version it will not be able to save over.
        var second = new PipelineProbeRunner(
            Database,
            actor,
            clock,
            async (context, command, token) =>
            {
                var loaded = await context.Series.SingleAsync(row => row.Id == series.Id, token);
                twinLoaded.TrySetResult();
                await firstCommitted.Task.WaitAsync(token);

                context.Entry(loaded).Property(nameof(Series.Title)).CurrentValue = "second title";

                return series.Id;
            });

        var lost = second.SendAsync(new PipelineProbeCommand(), ct);
        await twinLoaded.Task.WaitAsync(ct);

        (await first.SendAsync(new PipelineProbeCommand(), ct)).IsError.ShouldBeFalse("the first save wins");
        firstCommitted.TrySetResult();

        var result = await lost;

        result.IsError.ShouldBeTrue("xmin moved between the load and the save");
        result.FirstError.Code.ShouldBe(ErrorCodes.ConcurrencyConflict);
    }

    [Fact]
    public async Task AdvisoryLock_InHandler_HeldUntilCommit()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        var user = await new UserBuilder(Database, new TestCurrentUser()).CreateAsync(ct: ct);
        await PipelineProbeRunner.CreateEffectTableAsync(Database, ct);

        const long lockKey = 8_531_201_913;
        var effectId = Guid.CreateVersion7();
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new PipelineProbeRunner(
            Database,
            new TestCurrentUser { Id = user.Id },
            clock,
            async (context, command, token) =>
            {
                (await PipelineProbeRunner.TryAdvisoryLockAsync(context, lockKey, token))
                    .ShouldBeTrue("the handler takes the transaction-scoped lock on the command's own transaction");
                await InsertEffectAsync(context, effectId, token);
                locked.TrySetResult();

                await release.Task.WaitAsync(token);

                return effectId;
            });

        var send = runner.SendAsync(new PipelineProbeCommand(), ct);
        await locked.Task.WaitAsync(ct);

        // A foreign connection cannot take it while the request is in flight: pg_advisory_xact_lock is
        // released at commit, and this connection's statement is its own transaction. The effect row the
        // same transaction wrote is invisible for the same reason — the transaction is still open.
        (await TryForeignAdvisoryLockAsync(lockKey, ct)).ShouldBeFalse();
        CountEffects(effectId).ShouldBe(0, "nothing of an open transaction is visible to another connection");

        release.TrySetResult();
        (await send).IsError.ShouldBeFalse();

        (await TryForeignAdvisoryLockAsync(lockKey, ct)).ShouldBeTrue("the commit released the lock");
        CountEffects(effectId).ShouldBe(1, "and the same commit made the handler's effect row visible");
    }

    /// <summary>Stores one probe effect row inside the scope's transaction (raw SQL, no SaveChanges).</summary>
    private static Task<int> InsertEffectAsync(AppDbContext context, Guid effectId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {PipelineProbeRunner.EffectTable} (id) VALUES ({{0}})",
            [effectId],
            cancellationToken);

    private static async ValueTask<ErrorOr<Guid>> RenameAsync(
        AppDbContext context,
        Guid seriesId,
        string title,
        CancellationToken cancellationToken)
    {
        var row = await context.Series.SingleAsync(series => series.Id == seriesId, cancellationToken);
        context.Entry(row).Property(nameof(Series.Title)).CurrentValue = title;

        return seriesId;
    }

    private long CountEffects(Guid effectId) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM probe_pipeline_effects WHERE id = '{effectId}'");

    private long CountKeys(Guid userId, string key) =>
        SqlProbe.Count(Database, $"SELECT count(*) FROM idempotency_keys WHERE user_id = '{userId}' AND key = '{key}'");

    private async Task<bool> TryForeignAdvisoryLockAsync(long key, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(@key)";
        command.Parameters.AddWithValue("key", key);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}
