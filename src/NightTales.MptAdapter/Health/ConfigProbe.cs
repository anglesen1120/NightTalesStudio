using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.MptAdapter.Health;

/// <summary>The rendered MPT config's node-profile facts.</summary>
internal sealed record ConfigResult(bool Ok, string? Profile, string? WhisperDevice);

/// <summary>
/// D7's config source (start-only): the rendered <c>config.toml</c>'s <c>[whisper]</c> table must be the node
/// profile — <c>device = "cuda"</c> and <c>compute_type</c> in <c>{float16, int8_float16}</c> (05 D15 P5). Anything
/// else (cpu, mps, a missing or unreadable file) is <c>config_mismatch</c>. <c>mpt.config_profile</c> is
/// <c>node</c> only when the profile matches.
/// </summary>
internal sealed class ConfigProbe(
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<ConfigProbe> log) : IHealthSampler
{
    internal const string NodeProfile = "node";

    private static readonly string[] AllowedComputeTypes = ["float16", "int8_float16"];

    internal SourceCache<ConfigResult> Cache { get; } = new(null, clock);

    public string Name => "config";

    public TimeSpan? Interval => null;

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(options.Value.MptConfigPath, cancellationToken).ConfigureAwait(false);
            Cache.Set(Evaluate(text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            Cache.Set(new ConfigResult(false, null, null));
        }
    }

    internal static ConfigResult Evaluate(string toml)
    {
        string? device = null;
        string? compute = null;
        var inWhisper = false;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inWhisper = line.StartsWith("[whisper]", StringComparison.Ordinal);
                continue;
            }

            if (!inWhisper || line.StartsWith('#') || line.IndexOf('=') is not (> 0 and var equals))
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = StripQuotes(line[(equals + 1)..]);
            if (key == "device")
            {
                device = value;
            }
            else if (key == "compute_type")
            {
                compute = value;
            }
        }

        var ok = device == "cuda" && compute is not null && AllowedComputeTypes.Contains(compute, StringComparer.Ordinal);
        return new ConfigResult(ok, ok ? NodeProfile : null, device);
    }

    /// <summary>A TOML basic string with an optional trailing comment: <c>"cuda"  # note</c> → <c>cuda</c>.</summary>
    private static string? StripQuotes(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('"'))
        {
            return null;
        }

        var end = trimmed.IndexOf('"', 1);
        return end < 0 ? null : trimmed[1..end];
    }
}
