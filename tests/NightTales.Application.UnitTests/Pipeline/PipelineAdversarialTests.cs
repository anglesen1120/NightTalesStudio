using ErrorOr;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Idempotency;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Behaviors;
using NightTales.Application.Common.Errors;
using NightTales.Application.UnitTests.Pipeline.Probes;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Pipeline;

/// <summary>
/// QA (09.1.3-T2) adversarial probes over the pipeline contracts that the named tests do not pin:
/// a keyless command never claims, a replay whose stored <c>response_type</c> points at anything other
/// than the message's own success type is refused as a reused key (never deserialized as that stored
/// type), an unauthenticated query is denied, a query opens no transaction, and a rollback that fails
/// never masks the handler's error result.
/// </summary>
public sealed class PipelineAdversarialTests
{
    private static readonly TestCurrentUser Actor = new() { Id = MediatorTestHost.ActorId };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Idempotency_KeylessCommand_NeverClaims(string? key)
    {
        var store = new RecordingStore();
        var handlerRuns = 0;
        var behavior = new IdempotencyBehavior<ProbeCommand, ErrorOr<Guid>>(
            store,
            Actor,
            new FakeClock(),
            NullLogger<IdempotencyBehavior<ProbeCommand, ErrorOr<Guid>>>.Instance);

        var response = await behavior.Handle(
            new ProbeCommand("probe", IdempotencyKey: key),
            (_, _) =>
            {
                handlerRuns++;
                return ValueTask.FromResult<ErrorOr<Guid>>(ProbeCommandHandler.ResultId);
            },
            CancellationToken.None);

        response.IsError.ShouldBeFalse();
        handlerRuns.ShouldBe(1, "the keyless command runs once");
        store.Claims.ShouldBe(0, "a null or empty key never reaches the store");
        store.Completions.ShouldBe(0);
    }

    [Fact]
    public async Task Idempotency_Replay_StoredTypeIsNotTheSuccessType_Returns422_WithoutHandler()
    {
        // A stored response_type pointing at another CLR type that exists and whose payload deserializes
        // cleanly: an implementation that deserialized whatever name the row carries would succeed there
        // and then fail wrapping the value. The contract answers 422 idempotency_key_reused instead
        // (09.1.3-T2 Failure handling: "The response type was renamed between deploy and replay").
        var store = new RecordingStore
        {
            Claim = new IdempotencyClaim.Replay("System.DateTime", "\"2020-01-01T00:00:00+00:00\""),
        };
        var handlerRuns = 0;

        var response = await RunAsync(store, _ => handlerRuns++);

        response.IsError.ShouldBeTrue();
        response.FirstError.Code.ShouldBe(ErrorCodes.IdempotencyKeyReused);
        handlerRuns.ShouldBe(0, "a replay never runs the handler");
    }

    [Theory]
    [InlineData("\"not-a-guid\"")] // valid JSON, wrong shape for the success value type
    [InlineData("null")] // deserializes to null
    public async Task Idempotency_Replay_UnusableStoredPayload_Returns422_WithoutHandler(string storedJson)
    {
        var store = new RecordingStore
        {
            Claim = new IdempotencyClaim.Replay(typeof(Guid).FullName!, storedJson),
        };
        var handlerRuns = 0;

        var response = await RunAsync(store, _ => handlerRuns++);

        response.IsError.ShouldBeTrue();
        response.FirstError.Code.ShouldBe(ErrorCodes.IdempotencyKeyReused);
        handlerRuns.ShouldBe(0);
    }

    [Fact]
    public async Task Idempotency_Replay_MatchingStoredType_ReturnsStoredValue_WithoutHandler()
    {
        var expected = Guid.CreateVersion7();
        var store = new RecordingStore
        {
            Claim = new IdempotencyClaim.Replay(typeof(Guid).FullName!, $"\"{expected}\""),
        };
        var handlerRuns = 0;

        var response = await RunAsync(store, _ => handlerRuns++);

        response.IsError.ShouldBeFalse();
        response.Value.ShouldBe(expected);
        handlerRuns.ShouldBe(0);
    }

    [Fact]
    public async Task Authorization_AnonymousQuery_Returns401()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        host.CurrentUser.Id = null;
        var unitOfWork = (PassThroughUnitOfWork)scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var denied = await sender.Send(new ProbeQuery("probe"), CancellationToken.None);

        denied.IsError.ShouldBeTrue("deny by default applies to every message, not only commands");
        denied.FirstError.Code.ShouldBe(ErrorCodes.Unauthorized);
        host.Trace.Entries.ShouldNotContain("query-handler");

        var deniedCommand = await sender.Send(new ProbeCommand("probe"), CancellationToken.None);

        deniedCommand.FirstError.Code.ShouldBe(ErrorCodes.Unauthorized);
        unitOfWork.Begins.ShouldBe(0, "authorization runs before UnitOfWork, so a refused request opens no transaction");
    }

    [Fact]
    public async Task Pipeline_Query_OpensNoTransaction()
    {
        using var host = MediatorTestHost.Create();
        using var scope = host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var unitOfWork = (PassThroughUnitOfWork)scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        (await sender.Send(new ProbeQuery("probe"), CancellationToken.None)).IsError.ShouldBeFalse();
        unitOfWork.Begins.ShouldBe(0, "a query is not an IBaseCommand, so no transaction is opened");

        (await sender.Send(new ProbeCommand("probe"), CancellationToken.None)).IsError.ShouldBeFalse();
        unitOfWork.Begins.ShouldBe(1, "a command opens exactly one transaction");
    }

    [Fact]
    public async Task UnitOfWork_HandlerErrorResult_SurvivesAFailingRollback()
    {
        // UC3: an error result leaves nothing behind, and a rollback that fails must not replace it —
        // the scope's context disposal rolls an abandoned transaction back too, so the handler's own
        // error is what the caller must see, exactly as on the behaviour's exception paths.
        var unitOfWork = new FailingRollbackUnitOfWork();
        var behavior = new UnitOfWorkBehavior<ProbeCommand, ErrorOr<Guid>>(unitOfWork);
        var failure = Error.Failure("probe_rollback_masked", "The probe command failed on purpose (09.1.3-T2).");

        var response = await behavior.Handle(
            new ProbeCommand("probe"),
            (_, _) => ValueTask.FromResult<ErrorOr<Guid>>(failure),
            CancellationToken.None);

        response.IsError.ShouldBeTrue();
        response.FirstError.Code.ShouldBe(failure.Code);
        unitOfWork.Rollbacks.ShouldBe(1, "an error result still rolls the transaction back");
    }

    private static async Task<ErrorOr<Guid>> RunAsync(RecordingStore store, Action<ProbeCommand> handler)
    {
        var behavior = new IdempotencyBehavior<ProbeCommand, ErrorOr<Guid>>(
            store,
            Actor,
            new FakeClock(),
            NullLogger<IdempotencyBehavior<ProbeCommand, ErrorOr<Guid>>>.Instance);

        return await behavior.Handle(
            new ProbeCommand("probe", IdempotencyKey: "key"),
            (message, _) =>
            {
                handler(message);
                return ValueTask.FromResult<ErrorOr<Guid>>(Guid.CreateVersion7());
            },
            CancellationToken.None);
    }

    /// <summary>A store that counts calls and returns a canned claim.</summary>
    private sealed class RecordingStore : IIdempotencyStore
    {
        public IdempotencyClaim Claim { get; init; } = new IdempotencyClaim.Fresh(Guid.CreateVersion7());

        public int Claims { get; private set; }

        public int Completions { get; private set; }

        public Task<IdempotencyClaim> ClaimAsync(
            Guid userId,
            string key,
            string requestHash,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Claims++;
            return Task.FromResult(Claim);
        }

        public Task CompleteAsync(
            Guid claimId,
            string responseType,
            string responseJson,
            CancellationToken cancellationToken)
        {
            Completions++;
            return Task.CompletedTask;
        }
    }
}
