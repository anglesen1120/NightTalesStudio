using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.MptAdapter.Health;

/// <summary>The codec assertion: does <c>Adapter:FfmpegPath</c> list <c>Adapter:VideoCodec</c>.</summary>
internal sealed record CodecResult(bool Available);

/// <summary>
/// D7's codec source (start-only, 05 D15 P5): <c>ffmpeg -hide_banner -encoders</c> must list the configured
/// encoder as a whole word. A missing ffmpeg, a failing exit or a codec that is not listed is
/// <c>codec_unavailable</c> — a health reason, never an exit.
/// </summary>
internal sealed class CodecProbe(
    IProcessRunner runner,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<CodecProbe> log) : IHealthSampler
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    internal SourceCache<CodecResult> Cache { get; } = new(null, clock);

    public string Name => "codec";

    public TimeSpan? Interval => null;

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        try
        {
            var result = await runner
                .RunAsync(settings.FfmpegPath, ["-hide_banner", "-encoders"], ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            Cache.Set(new CodecResult(result.ExitCode == 0 && Lists(result.StandardOutput, settings.VideoCodec)));
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or IOException
            or InvalidOperationException)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            Cache.Set(new CodecResult(false));
        }
    }

    /// <summary>An encoder row is <c> V….. libx264 …</c>: the codec is its own whitespace-delimited token.</summary>
    internal static bool Lists(string encoders, string codec) =>
        !string.IsNullOrWhiteSpace(codec)
        && encoders.Split('\n').Any(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(1).Contains(codec, StringComparer.Ordinal));
}
