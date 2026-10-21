using NightTales.Application.Abstractions.Flags;

namespace NightTales.Application.Features.Autopilot.Orchestration.Steps;

/// <summary>
/// The "02.7.1 recorded a go" half of the true-crime research branch (02.8.3 plan §3.5, F33): whether this
/// platform may spend a source search for a run, which decides between <see cref="AutopilotRunState.Researching"/>
/// with a <c>find_sources</c> run and the owner ask of <c>waiting_owner</c> with
/// <see cref="WaitingFor.Sources"/>.
/// </summary>
/// <remarks>
/// <para>
/// The port exists so the step is testable without the finder: the shipped implementation reads the
/// <c>source_finder</c> feature flag, and 02.7.2-T3's runner — the other half of the go — is not built yet, so the
/// flag staying off is what keeps the F33 default ("no go") in force. When the runner lands it registers itself
/// beside this port and this implementation adds its availability probe to the conjunction; the receiver of that
/// work is 02.7.2-T3 (and 02.8.3-T2 for the wiring review).
/// </para>
/// <para>
/// <b>No owner argument.</b> Per-series rollout of a flag needs <c>IFeatureFlags.IsEnabledForAsync</c>, which
/// 06.5.2-T1 owns; until it exists the flag is platform-wide, so the answer cannot depend on the run's owner
/// without inventing a subject the platform cannot evaluate.
/// </para>
/// </remarks>
public interface ISourceFinderGo
{
    /// <summary>Whether a true-crime run of this slot may run a source search.</summary>
    /// <param name="ct">Cancels the flag read.</param>
    /// <returns>The <c>source_finder</c> flag (fail-closed), and the runner probe once it exists.</returns>
    ValueTask<bool> HasGoAsync(CancellationToken ct);
}

/// <summary>
/// The shipped <see cref="ISourceFinderGo"/>: the <c>source_finder</c> feature flag, read through the fail-closed
/// <see cref="IFeatureFlags"/> port, so a missing or unreadable flag is "no go" — the F33 branch, which never
/// spends money.
/// </summary>
/// <remarks>
/// 02.7.2-T3's finder runner does not exist yet, so there is nothing to probe: the flag alone is the answer, and
/// with it off this implementation answers <see langword="false"/> for every run, which is the plan's default
/// (<c>TrueCrimeRun_NoFinderGo_WaitsForSources</c>).
/// </remarks>
/// <param name="flags">The feature-flag port.</param>
public sealed class SourceFinderGo(IFeatureFlags flags) : ISourceFinderGo
{
    /// <summary>The flag key the source finder ships behind.</summary>
    public const string FeatureKey = "source_finder";

    /// <inheritdoc />
    public async ValueTask<bool> HasGoAsync(CancellationToken ct) =>
        await flags.IsEnabledAsync(FeatureKey, ct).ConfigureAwait(false);
}
