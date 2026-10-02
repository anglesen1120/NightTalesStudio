namespace NightTales.Application.Abstractions.Flags;

/// <summary>
/// The read side of the platform's feature flags (09 README BD13, 09.9.2-T2 §Design): whether a feature
/// an operator can switch off is currently on.
/// </summary>
/// <remarks>
/// <para>
/// The P answer comes from <c>feature_flags.enabled</c> only — the <c>rules</c> document is not read in P
/// and a seeded non-<c>null</c> value must never change the answer (rule evaluation is 06.5.2)
/// <b>[RULED — README §B10 WT13]</b>.
/// </para>
/// <para>
/// The contract is fail-closed and never throws: an unknown key, an invalid key and a database that is
/// down all answer <see langword="false"/>, so a guarded endpoint answers 404 rather than 500. Callers on
/// system paths (jobs, outbox handlers, the CLI) read this member; an HTTP route reaches it through the
/// Api's <c>RequireFeature</c> endpoint filter, never by hand.
/// </para>
/// </remarks>
public interface IFeatureFlags
{
    /// <summary>Whether <paramref name="key"/> is enabled; <see langword="false"/> for anything unknown.</summary>
    /// <param name="key">The flag key; see <see cref="FeatureFlagKey"/> for the accepted shape.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    Task<bool> IsEnabledAsync(string key, CancellationToken ct);
}
