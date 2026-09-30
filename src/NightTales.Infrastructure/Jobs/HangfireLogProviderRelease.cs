using Hangfire.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Releases the process-global Hangfire log provider this host installs (09 README D12/D13).
/// </summary>
/// <remarks>
/// <para>
/// <c>AddHangfire</c> takes the host's <see cref="ILoggerFactory"/> and writes an
/// <c>AspNetCoreLogProvider</c> into Hangfire's process-static <see cref="LogProvider"/>, while every
/// Hangfire type resolves its logger in a <b>static</b> constructor. A host that installs its factory
/// and is then disposed therefore leaves that static pointing at a disposed factory, and the next
/// host's first use of any Hangfire type whose static constructor has not run yet throws
/// <see cref="ObjectDisposedException"/> for the rest of the process.
/// </para>
/// <para>
/// Measured with Hangfire.PostgreSql 1.21.1 in the two-host-per-process layout README D13 mandates:
/// after a disposed host was the last writer, <c>PostgreSqlObjectsInstaller</c>'s static constructor
/// failed and stayed poisoned, so every later storage silently fell back to degraded mode
/// (<c>AllowDegradedModeWithoutStorage</c> defaults to true) and no job could be enqueued — the
/// failure surfaced far from its cause as <c>42P01: relation "hangfire.job" does not exist</c>.
/// </para>
/// <para>
/// The release runs last — the service is registered first and hosted services stop in reverse order
/// — so the host's own shutdown still logs through its factory. It drops the provider rather than
/// restoring a previous one, because the previous writer may have been a sibling host whose factory
/// this host does not own; Hangfire then resolves its own default provider, which depends on no
/// container.
/// </para>
/// </remarks>
public sealed class HangfireLogProviderRelease : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        LogProvider.SetCurrentLogProvider(null);

        return Task.CompletedTask;
    }
}
