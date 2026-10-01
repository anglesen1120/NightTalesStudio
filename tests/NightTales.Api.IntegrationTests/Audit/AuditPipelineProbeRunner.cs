using ErrorOr;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Audit;
using NightTales.Application.Behaviors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Audit;
using NightTales.Infrastructure.Persistence.Idempotency;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.TestKit.Database;

namespace NightTales.Api.IntegrationTests.Audit;

/// <summary>
/// Sends one <see cref="AuditProbeCommand"/> through the production behaviour chain — UnitOfWork →
/// Idempotency → Audit → handler — over the real Infrastructure implementations and one
/// <see cref="AppDbContext"/> scope per call, exactly as the D4 pipeline would.
/// </summary>
/// <remarks>
/// The behaviours are composed directly instead of through a host Mediator: the Api's Mediator is
/// generated for the Application assembly (<c>MediatorPipelineRegistration</c>), so a message declared in
/// this test assembly is never visible to it, and the slot order this suite proves — the audit row
/// joining the open transaction, a replay never reaching slot 6 — is a property of the composition, not
/// of the source generator. The Api integration suite tests the very same audit row and the very same
/// path through a real request in <see cref="SeriesPatchAuditTests"/>.
/// </remarks>
internal sealed class AuditPipelineProbeRunner(TestDatabase database, ICurrentUser currentUser, IClock clock)
{
    private int _executions;

    /// <summary>How many times the handler ran — 1 for a keyed command whatever the retries.</summary>
    internal int Executions => Volatile.Read(ref _executions);

    /// <summary>Sends one probe command in its own scope.</summary>
    internal async Task<ErrorOr<Guid>> SendAsync(AuditProbeCommand command, CancellationToken cancellationToken)
    {
        await using var context = database.CreateAppDbContext(currentUser, clock);

        // The probe context comes from the test factory, which configures no command timeout, so the store
        // takes the default options — the same 30 s the provider applies to its own commands.
        var idempotency = new IdempotencyBehavior<AuditProbeCommand, ErrorOr<Guid>>(
            new IdempotencyStore(context, Options.Create(new PersistenceOptions())),
            currentUser,
            clock,
            NullLogger<IdempotencyBehavior<AuditProbeCommand, ErrorOr<Guid>>>.Instance);
        var audit = new AuditBehavior<AuditProbeCommand, ErrorOr<Guid>>(
            new EfAuditWriter(context),
            currentUser,
            new AuditMetadataCache(),
            clock);
        var unitOfWork = new UnitOfWorkBehavior<AuditProbeCommand, ErrorOr<Guid>>(new EfUnitOfWork(context));

        MessageHandlerDelegate<AuditProbeCommand, ErrorOr<Guid>> handler =
            (message, token) => ExecuteAsync(context, message, token);

        return await unitOfWork
            .Handle(
                command,
                (message, token) => idempotency.Handle(
                    message,
                    (inner, innerToken) => audit.Handle(inner, handler, innerToken),
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The probe handler: it succeeds, refuses, or leaves a row that the commit's save refuses.</summary>
    private ValueTask<ErrorOr<Guid>> ExecuteAsync(
        AppDbContext context,
        AuditProbeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _executions);

        switch (command.Mode)
        {
            case AuditProbeMode.Conflict:
                return ValueTask.FromResult<ErrorOr<Guid>>(
                    Error.Conflict("probe_conflict", "The probe handler refused the command."));

            case AuditProbeMode.InvalidInsert:
                // A row the table's own ck_audit_logs_user_visible_has_subject CHECK refuses, tracked in the
                // same context: the handler succeeds, the behaviour then adds its own row, and the commit's
                // SaveChanges refuses the invalid one — so both roll back together.
                context.AuditLogs.Add(AuditLog.Record(
                    actorUserId: null,
                    actorType: ActorType.System,
                    subjectUserId: null,
                    action: "probe.invalid",
                    category: Category.Support,
                    visibility: Visibility.User,
                    entityType: null,
                    entityId: null,
                    dataJson: "{}",
                    ip: null,
                    userAgent: null,
                    occurredAt: clock.UtcNow));
                break;

            default:
                break;
        }

        return ValueTask.FromResult<ErrorOr<Guid>>(Guid.CreateVersion7());
    }
}
