namespace NightTales.Infrastructure.UnitTests.TestKit.Probes;

/// <summary>
/// Test-only value used by the JSON-column spike: it holds a scalar and a nested list, so a passing
/// spike also proves that nested collections round-trip through the chosen mapping.
/// </summary>
internal sealed class ProbeSettings
{
    public string Tone { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];
}
