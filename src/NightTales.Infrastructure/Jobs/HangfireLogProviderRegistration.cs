using Hangfire.AspNetCore;
using Hangfire.Logging;
using Microsoft.Extensions.Logging;

namespace NightTales.Infrastructure.Jobs;

/// <summary>
/// Owns one host's install into Hangfire's process-static log provider and hands it back when the host's
/// container goes away (09 README D12/D13).
/// </summary>
/// <remarks>
/// <para>
/// <c>AddHangfire</c> takes the host's <see cref="ILoggerFactory"/> and writes an
/// <c>AspNetCoreLogProvider</c> into Hangfire's process-static <see cref="LogProvider"/> the first time
/// the host resolves <c>IGlobalConfiguration</c>, while every Hangfire type resolves its logger from
/// the current provider (the instance constructor of Hangfire's own <c>AutomaticRetryAttribute</c> calls
/// <c>LogProvider.For</c>). Two hosts in one process — the Worker and the Api's dashboard, the layout
/// README D13 mandates — therefore write the same static, and a host that is disposed while another is
/// live must not leave its own factory installed: the next Hangfire type to resolve a logger would then
/// throw <see cref="ObjectDisposedException"/> for a factory that no longer exists, and Hangfire's
/// <c>AutomaticRetryAttribute</c> would fail on the next host's very first scheduled job.
/// </para>
/// <para>
/// Ownership is reference-tracked and tied to the container rather than to <c>IHostedService.StopAsync</c>:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// The registration is resolved from the same container that installs the provider, so its
/// <see cref="Dispose"/> runs whenever that container is disposed — including a host the harness
/// disposes without stopping its hosted services, and including a host whose start failed after the
/// install. A host that installs late is still tracked, because the install and this registration are
/// created by the same factory call.
/// </description>
/// </item>
/// <item>
/// <description>
/// It is created after the container's <see cref="ILoggerFactory"/> (that factory is what Hangfire
/// installed), and a container disposes its singletons in reverse creation order, so this release runs
/// while the factory is still alive and hands the process to another live host rather than dropping it.
/// </description>
/// </item>
/// <item>
/// <description>
/// The last live writer wins: a disposal restores the newest host still registered, and only the last
/// one drops the provider to null — so a host that stops while another is live cannot take that host's
/// logging away (09.2.6-T2 review, F-LOG).
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed class HangfireLogProviderRegistration : IDisposable
{
    private static readonly Lock Gate = new();

    /// <summary>The hosts that installed a provider and have not been disposed, oldest first.</summary>
    private static readonly List<HangfireLogProviderRegistration> Live = [];

    private readonly ILogProvider _provider;
    private bool _disposed;

    /// <summary>Builds the provider of one host's <paramref name="loggerFactory"/> (not installed yet).</summary>
    /// <param name="loggerFactory">The factory Hangfire's own configuration already installed.</param>
    public HangfireLogProviderRegistration(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _provider = new AspNetCoreLogProvider(loggerFactory);
    }

    /// <summary>
    /// Installs this host's provider as Hangfire's current one and remembers it as live, so a later
    /// disposal can hand the process back to the newest host that is still running.
    /// </summary>
    public void Install()
    {
        lock (Gate)
        {
            Live.Add(this);

            LogProvider.SetCurrentLogProvider(_provider);
        }
    }

    /// <summary>
    /// Hands Hangfire's process-static provider back: the newest host still registered, or null when this
    /// was the last one — never this host's own factory, which is being disposed with the container.
    /// </summary>
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Live.Remove(this);

            LogProvider.SetCurrentLogProvider(Live.Count == 0 ? null : Live[^1]._provider);
        }
    }
}
