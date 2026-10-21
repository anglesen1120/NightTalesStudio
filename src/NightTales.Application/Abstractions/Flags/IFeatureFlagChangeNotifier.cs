namespace NightTales.Application.Abstractions.Flags;

/// <summary>
/// Publishes "this flag changed" so every instance's read cache drops the key immediately
/// (09 README BD13, 09.9.2-T2 §Eviction).
/// </summary>
/// <remarks>
/// <para>
/// One call feeds <b>both</b> eviction paths the read service honours, in the caller's transaction:
/// a PostgreSQL <c>NOTIFY feature_flags_changed</c> (the ruled cross-instance path
/// <b>[RULED — README §B10 WT13]</b>, delivered at commit) and the <c>feature_flags_changed.v1</c>
/// outbox row the Worker's <c>FeatureFlagsChangedHandler</c> consumes. The statement runs on the
/// caller's own context, so it joins the writer's transaction (09 README D5) and a rolled-back
/// <c>flags set</c> announces nothing and queues nothing. The 30 s cache window bounds staleness when
/// both are lost; a lost notification is never fatal.
/// </para>
/// <para>
/// A caller outside the Mediator pipeline must hold an open transaction, exactly like
/// <c>IAuditWriter</c>: the command handler that calls this runs inside <c>UnitOfWorkBehavior</c>'s
/// transaction.
/// </para>
/// </remarks>
public interface IFeatureFlagChangeNotifier
{
    /// <summary>Announces that the flag named <paramref name="key"/> changed, in the caller's transaction.</summary>
    /// <param name="key">The changed flag key; at most <see cref="FeatureFlagKey.MaxLength"/> characters.</param>
    /// <param name="enabled">The state the flag now has, carried by the outbox payload.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    Task NotifyChangedAsync(string key, bool enabled, CancellationToken ct);
}
