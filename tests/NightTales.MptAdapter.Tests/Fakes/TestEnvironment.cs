using NightTales.MptAdapter;

namespace NightTales.MptAdapter.Tests.Fakes;

/// <summary>
/// One environment for the whole assembly: <c>MptClient</c> reads <c>NTS_MPT_API_KEY</c> at construction (S3/S6),
/// so the tests set it once before any type is loaded. The value is a fake and never leaves this process.
/// </summary>
internal static class TestEnvironment
{
    internal const string MptApiKey = "test-mpt-api-key";

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable(MptClient.ApiKeyEnvironmentVariable, MptApiKey);
}
