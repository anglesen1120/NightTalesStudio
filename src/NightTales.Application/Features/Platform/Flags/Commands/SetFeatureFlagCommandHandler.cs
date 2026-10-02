using System.Text.Json;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Platform.Flags.Commands;

/// <summary>
/// The one writer of <c>feature_flags</c> in P (09 README BD13, 09.9.2-T2 §Operator CLI): validates the
/// key, flips the row, records the operator's audit row and announces the change — all in the pipeline's
/// one transaction, so a failure leaves none of the three behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audited only on a real transition.</b> A set to the state the row already has is a successful
/// no-op: <c>Changed = false</c>, no audit row, no notification (09.9.2-T2 failure table). The audit row
/// therefore lives here and not in pipeline slot 6, which cannot express "changed nothing"
/// (see <see cref="SetFeatureFlagCommand"/>).
/// </para>
/// <para>
/// <b>Both eviction paths are fed by one call.</b> <see cref="IFeatureFlagChangeNotifier"/> publishes
/// <c>NOTIFY feature_flags_changed</c> inside this transaction — so every instance's <c>LISTEN</c>
/// invalidator drops the key at commit — and queues the <c>feature_flags_changed.v1</c> outbox row the
/// Worker's handler consumes. The row is queued by the notifier and not by an aggregate's domain event
/// because the update below goes through <c>ExecuteUpdateAsync</c> and <c>FeatureFlag</c> is not an
/// <c>AggregateRoot</c>; the update also bypasses the save-changes interceptors, so <c>updated_at</c> is
/// stamped explicitly (09 README D8).
/// </para>
/// <para>
/// <b>Last committed write wins.</b> A concurrent <c>flags set</c> may leave the cache serving the other
/// value for at most the 30 s window: a single-row update needs no lock, and the design accepts that
/// bound (09.9.2-T2 failure table).
/// </para>
/// </remarks>
/// <param name="database">The tracked write model; the row update and the audit insert share its transaction.</param>
/// <param name="audit">The one writer of <c>audit_logs</c>; it adds to the ambient transaction.</param>
/// <param name="notifier">Publishes the cross-instance eviction, in the same transaction.</param>
/// <param name="clock">The scope's clock; one instant is read for the row and the audit entry.</param>
public sealed class SetFeatureFlagCommandHandler(
    IAppDbContext database,
    IAuditWriter audit,
    IFeatureFlagChangeNotifier notifier,
    IClock clock) : ICommandHandler<SetFeatureFlagCommand, ErrorOr<FeatureFlagSetDto>>
{
    /// <summary>
    /// The frozen <c>audit_logs.action</c> of an operator flag change. [INFERENCE name] — 09.9.2-T2 §Notes
    /// lists it as this mechanism's own key, because BD13 names the row's actor, category and visibility
    /// but not its action.
    /// </summary>
    public const string AuditAction = "feature_flag_changed";

    /// <summary>The <c>data.actor</c> label every operator verb persists (09 README BD15).</summary>
    public const string OperatorActor = "system:operator";

    private static readonly JsonSerializerOptions DataJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async ValueTask<ErrorOr<FeatureFlagSetDto>> Handle(
        SetFeatureFlagCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!FeatureFlagKey.IsValid(command.Key))
        {
            return Errors.ValidationFailed(
                "key",
                $"A feature-flag key is {FeatureFlagKey.MinLength}-{FeatureFlagKey.MaxLength} characters: "
                + "a lowercase letter followed by lowercase letters, digits or underscores.");
        }

        var current = await database.FeatureFlags
            .AsNoTracking()
            .Where(flag => flag.Key == command.Key)
            .Select(flag => (bool?)flag.Enabled)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            // A valid key with no row: the seed catalog never made this flag, so there is nothing to flip
            // (09.9.2-T2 failure table). The CLI turns this into exit 1 without an audit row.
            return Errors.NotFound();
        }

        if (current.Value == command.Enabled)
        {
            return new FeatureFlagSetDto(command.Key, command.Enabled, Changed: false);
        }

        var now = clock.UtcNow;

        // Set-based update: it bypasses both interceptors, so updated_at is written here (09 README D8).
        var affected = await database.FeatureFlags
            .Where(flag => flag.Key == command.Key)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(flag => flag.Enabled, command.Enabled)
                    .SetProperty(flag => flag.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            // The row disappeared between the read and the update (a concurrent delete the mechanism does
            // not have in P). Nothing changed, so nothing is recorded.
            return Errors.NotFound();
        }

        await audit.WriteAsync(
            new AuditEntry(
                ActorUserId: null,
                ActorType: ActorType.System,
                SubjectUserId: null,
                Action: AuditAction,
                Category: Category.Support,
                Visibility: Visibility.Internal,
                EntityType: "feature_flag",
                EntityId: null,
                DataJson: JsonSerializer.Serialize(new ChangedFlag(command.Key, command.Enabled), DataJson),
                Ip: null,
                UserAgent: null,
                ActorLabel: OperatorActor,
                OccurredAt: now),
            cancellationToken).ConfigureAwait(false);

        await notifier.NotifyChangedAsync(command.Key, command.Enabled, cancellationToken).ConfigureAwait(false);

        return new FeatureFlagSetDto(command.Key, command.Enabled, Changed: true);
    }

    /// <summary>The safe <c>data</c> payload of the row: the key and the state it now has (BD16).</summary>
    private sealed record ChangedFlag(string Key, bool Enabled);
}
