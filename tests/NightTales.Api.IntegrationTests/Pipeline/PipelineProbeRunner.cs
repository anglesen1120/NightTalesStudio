using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Behaviors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit.Database;
using Npgsql;

namespace NightTales.Api.IntegrationTests.Pipeline;

/// <summary>
/// Runs one <see cref="PipelineProbeCommand"/> through the production behaviour chain — UnitOfWork →
/// Idempotency → handler — over the real Infrastructure implementations and one
/// <see cref="AppDbContext"/> scope per call, exactly as one request would.
/// </summary>
/// <remarks>
/// <para>
/// The behaviours are composed directly instead of through a Mediator resolved from a host: the Api's
/// Mediator is generated for the Application assembly (<c>MediatorPipelineRegistration</c>), so the
/// source generator never sees a message declared in this test assembly, and an HTTP probe could only
/// exercise a behaviour set configured by the test rather than by the host. Composing the chain keeps
/// the assertion on the behaviour and the database, which is what the task's tests state.
/// </para>
/// <para>
/// The handler receives the scope's own <see cref="AppDbContext"/>, so it can add entities and run raw
/// SQL inside the request's transaction. <see cref="Executions"/> counts the handler runs, which is the
/// measurement each idempotency test asserts on.
/// </para>
/// </remarks>
internal sealed class PipelineProbeRunner
{
    /// <summary>The probe table the handlers write their effect row into.</summary>
    internal const string EffectTable = "probe_pipeline_effects";

    private readonly TestDatabase _database;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly Func<AppDbContext, PipelineProbeCommand, CancellationToken, ValueTask<ErrorOr<Guid>>> _handler;
    private readonly Func<AppDbContext, IUnitOfWork> _unitOfWork;
    private readonly ILoggerFactory _loggerFactory;
    private int _executions;

    internal PipelineProbeRunner(
        TestDatabase database,
        ICurrentUser currentUser,
        IClock clock,
        Func<AppDbContext, PipelineProbeCommand, CancellationToken, ValueTask<ErrorOr<Guid>>> handler,
        Func<AppDbContext, IUnitOfWork>? unitOfWork = null,
        ILoggerFactory? loggerFactory = null)
    {
        _database = database;
        _currentUser = currentUser;
        _clock = clock;
        _handler = handler;
        _unitOfWork = unitOfWork ?? (context => new EfUnitOfWork(context));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>How many times the handler ran — 1 for an idempotent command whatever the retries.</summary>
    internal int Executions => Volatile.Read(ref _executions);

    /// <summary>Sends one probe command in its own scope.</summary>
    internal async Task<ErrorOr<Guid>> SendAsync(PipelineProbeCommand command, CancellationToken cancellationToken)
    {
        await using var database = _database.CreateAppDbContext(_currentUser, _clock);

        // The probe context comes from the test factory, which configures no command timeout, so the store
        // takes the default options — the same 30 s the provider applies to its own commands.
        var idempotency = new IdempotencyBehavior<PipelineProbeCommand, ErrorOr<Guid>>(
            new IdempotencyStore(database, Options.Create(new PersistenceOptions())),
            _currentUser,
            _clock,
            _loggerFactory.CreateLogger<IdempotencyBehavior<PipelineProbeCommand, ErrorOr<Guid>>>());
        var unitOfWork = new UnitOfWorkBehavior<PipelineProbeCommand, ErrorOr<Guid>>(_unitOfWork(database));
        MessageHandlerDelegate<PipelineProbeCommand, ErrorOr<Guid>> handler =
            (message, token) => ExecuteAsync(database, message, token);

        return await unitOfWork
            .Handle(command, (message, token) => idempotency.Handle(message, handler, token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Takes the transaction-scoped advisory lock of 02 D1 on the command's own transaction.</summary>
    internal static async Task<bool> TryAdvisoryLockAsync(
        AppDbContext database,
        long key,
        CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(@key)";
        ((NpgsqlParameterCollection)command.Parameters).AddWithValue("key", key);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Creates the probe table; the migration set of the platform does not know it.</summary>
    internal static Task CreateEffectTableAsync(TestDatabase database, CancellationToken cancellationToken) =>
        database.ExecuteAsync($"CREATE TABLE IF NOT EXISTS {EffectTable} (id uuid PRIMARY KEY)", cancellationToken);

    private ValueTask<ErrorOr<Guid>> ExecuteAsync(
        AppDbContext database,
        PipelineProbeCommand command,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);

        return _handler(database, command, cancellationToken);
    }
}
