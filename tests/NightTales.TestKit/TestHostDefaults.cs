using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace NightTales.TestKit;

/// <summary>
/// Process-wide defaults for every host a test process builds (Api through <c>WebApplicationFactory</c>,
/// Worker, the adapter), applied once when the TestKit assembly loads — before any of its factories exist.
/// </summary>
/// <remarks>
/// <c>WebApplication.CreateBuilder</c> and <c>Host.CreateApplicationBuilder</c> add <c>appsettings*.json</c> with
/// <c>reloadOnChange: true</c>, so every test host creates file watchers; on macOS each one starts an FSEvents
/// stream. A suite builds hundreds of hosts, and local gates saw host builds wait out the
/// <c>HostFactoryResolver</c> 5-minute limit before <c>builder.Build()</c> while CI (Linux) never did. No test
/// edits a settings file while its host runs, so the watchers only cost time. The documented host switch
/// <c>hostBuilder:reloadConfigOnChange</c> (read from <c>DOTNET_</c> environment variables) turns them off;
/// an explicit value from the environment wins.
/// </remarks>
internal static class TestHostDefaults
{
    internal const string ReloadConfigOnChange = "DOTNET_hostBuilder__reloadConfigOnChange";

    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "TestKit is loaded only by test processes; the switch must be set before any host is built.")]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable(ReloadConfigOnChange) is null)
        {
            Environment.SetEnvironmentVariable(ReloadConfigOnChange, "false");
        }
    }
}
