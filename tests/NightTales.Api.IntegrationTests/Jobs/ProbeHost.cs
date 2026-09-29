using Microsoft.Extensions.DependencyInjection;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;

namespace NightTales.Api.IntegrationTests.Jobs;

/// <summary>
/// Builds a <see cref="WorkerHost"/> over the probe jobs of this project (09.2.6-T1): the host scans
/// this assembly, so every job below is registered the way a production job class is.
/// </summary>
internal static class ProbeHost
{
    internal static WorkerHost New(TestDatabase database, ProbeJobEffects effects) =>
        new WorkerHost(database, new FakeClock())
            .WithJobAssembly(typeof(ProbeHost).Assembly)
            .ConfigureServices(services =>
            {
                services.AddSingleton(effects);
                services.AddSingleton<ProbeJobDependency>();
            });
}
