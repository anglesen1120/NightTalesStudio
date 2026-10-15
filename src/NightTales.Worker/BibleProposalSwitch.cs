using Microsoft.Extensions.Options;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Abstractions;

namespace NightTales.Worker;

/// <summary>
/// The Worker's <see cref="IBibleProposalSwitch"/> (02.6.3-T2): the host's answer to whether <c>Bible:Proposal</c>
/// is switched on.
/// </summary>
/// <remarks>
/// It holds the <c>IOptionsMonitor</c> of the section rather than the bound snapshot, so every delivery reads the
/// current configuration value: the rollback in plan §11 (09.10.3) switches the feature off from configuration and
/// takes effect without a restart. The Worker composition root registers it beside the section's binding and its
/// op caps (<c>WorkerServiceCollectionExtensions</c>).
/// </remarks>
/// <param name="proposalOptions">The monitored <c>Bible:Proposal</c> section, bound and validated on start.</param>
public sealed class BibleProposalSwitch(IOptionsMonitor<BibleProposalJobOptions> proposalOptions) : IBibleProposalSwitch
{
    /// <inheritdoc />
    public bool Enabled => proposalOptions.CurrentValue.Enabled;
}
