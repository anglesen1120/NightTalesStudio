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

    /// <summary>
    /// What the two scripts read: MPT's font and song dirs, the font register and the music catalog (05.2.2-T1,
    /// 05.3.1-T1). A file is watched through its directory.
    /// </summary>
    public IReadOnlyList<string> WatchedPaths { get; } =
    [
        Path.Combine(options.Value.MptRoot, "resource", "fonts"),
        Path.Combine(options.Value.MptRoot, "resource", "songs"),
        Path.Combine(bundle.Root, "worker-node", "fonts", "FONTS.csv"),
        Path.Combine(bundle.Root, "seed", "music-tracks.v1.json"),
    ];

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

            // check-fonts.sh -> font_glyph_check.py: exit 0 = every registered font ok; its "hash ok <file>"
            // line names the checked font ("glyphs ok n/n" is a count, not the name).
            var fontName = font is { ExitCode: 0 } ? ParseFontName(font.StandardOutput) : null;
            var fontOk = fontName is not null;

            // check-music.sh --json: exit 0 = ok, exit 1 = a check failed (still a report, with music_missing and
            // the revision); anything else is a usage or environment error with no trustworthy report.
            var report = music is { ExitCode: 0 or 1 } ? ParseMusic(music.StandardOutput) : null;
            var musicOk = music is { ExitCode: 0 } && report is { Ok: true };
            var now = clock.GetUtcNow();

            Cache.Set(new AssetsProbe(
                fontOk,
                musicOk,
                new AssetsHealthV1(
                    fontOk && musicOk,
                    fontName,
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

    /// <summary>The file named by the <c>hash ok &lt;file&gt;</c> line (<c>hash skip</c> when the font is not in the
    /// register); null when the check printed neither. <c>glyphs ok n/n</c> is a glyph count, never the name.</summary>
    private static string? ParseFontName(string output)
    {
        foreach (var prefix in new[] { "hash ok ", "hash skip " })
        {
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // "hash skip <file> (not in <register>)": the name is the first token after the prefix.
                if (raw.StartsWith(prefix, StringComparison.Ordinal)
                    && raw[prefix.Length..].Trim().Split(' ', 2)[0] is { Length: > 0 } name)
                {
                    return name;
                }
            }
        }

        return null;
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
            int? revision = root.TryGetProperty("catalog_revision", out var rev)
                && rev.ValueKind == JsonValueKind.Number && rev.TryGetInt32(out var number)
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
