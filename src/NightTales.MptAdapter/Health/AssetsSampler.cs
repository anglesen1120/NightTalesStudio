using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter.Health;

/// <summary>The two asset checks and the <c>assets</c> health block built from them.</summary>
internal sealed record AssetsProbe(bool FontOk, bool MusicOk, AssetsHealthV1 Health);

/// <summary>
/// D7's assets source (05 D19 P1, single-flight): <c>worker-node/bin/check-fonts.sh</c> and
/// <c>check-music.sh --json</c> every <c>Adapter:AssetsRefreshSeconds</c> (600 s, never per request). Each runs as an
/// argument list with the <c>Adapter:CheckTimeoutSeconds</c> budget. A script that cannot run, times out, exits
/// non-zero or prints no JSON fails its half closed: <c>font_missing</c> / <c>music_missing</c>, never ok.
/// </summary>
internal sealed class AssetsSampler(
    IProcessRunner runner,
    BundleInfo bundle,
    IOptions<AdapterOptions> options,
    TimeProvider clock,
    ILogger<AssetsSampler> log) : IHealthSampler
{
    private int _running;

    internal SourceCache<AssetsProbe> Cache { get; } =
        new(TimeSpan.FromSeconds(options.Value.AssetsRefreshSeconds), clock);

    public string Name => "assets";

    public TimeSpan? Interval => Cache.Interval;

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var settings = options.Value;
            var timeout = TimeSpan.FromSeconds(settings.CheckTimeoutSeconds);
            var bin = Path.Combine(bundle.Root, "worker-node", "bin");
            var args = new[] { "--mpt-root", settings.MptRoot };

            var font = await RunAsync(Path.Combine(bin, "check-fonts.sh"), args, timeout, cancellationToken)
                .ConfigureAwait(false);
            var music = await RunAsync(Path.Combine(bin, "check-music.sh"), [.. args, "--json"], timeout, cancellationToken)
                .ConfigureAwait(false);

            var fontOk = font is { ExitCode: 0 };
            var report = music is { ExitCode: 0 } ? ParseMusic(music.StandardOutput) : null;
            var musicOk = report is { Ok: true };
            var now = clock.GetUtcNow();

            Cache.Set(new AssetsProbe(
                fontOk,
                musicOk,
                new AssetsHealthV1(
                    fontOk && musicOk,
                    fontOk ? "ok" : "missing",
                    report?.Revision,
                    report?.Missing ?? [],
                    now)));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ProcessResult?> RunAsync(
        string script, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(script, arguments, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or IOException
            or InvalidOperationException)
        {
            log.SamplerFailed(Name, exception.GetType().Name);
            return null;
        }
    }

    private sealed record MusicReport(bool Ok, int? Revision, IReadOnlyList<string> Missing);

    /// <summary>The JSON line of <c>check-music.sh --json</c>; null when absent or malformed.</summary>
    private static MusicReport? ParseMusic(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        if (line is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            int? revision = root.TryGetProperty("catalog_revision", out var rev) && rev.TryGetInt32(out var number)
                ? number
                : null;
            var missing = root.TryGetProperty("music_missing", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            return new MusicReport(ok, revision, missing);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
