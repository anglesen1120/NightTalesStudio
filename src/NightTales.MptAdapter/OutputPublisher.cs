using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter;

/// <summary>What <see cref="OutputPublisher"/> decided: the output, or the closed <c>detail</c> of the failure.</summary>
internal sealed record PublishResult(RenderOutputV1? Output, string? FailureDetail)
{
    internal static PublishResult Failed(string detail) => new(null, detail);
}

/// <summary>
/// Builds <c>RenderOutputV1</c> from the check (T3 design table). The video block is the report's own
/// measurement - bytes and sha256 are copied, never recomputed (05 D15 P3). Only the subtitles file, which
/// the report has no <c>output</c> entry for, is hashed - once, in 1 MiB chunks.
/// </summary>
internal sealed class OutputPublisher
{
    internal const string VideoFileName = "final-1.norm.mp4";
    internal const string SubtitlesFileName = "subtitle.srt";
    internal const string CheckFailed = "check_failed";
    private const int HashChunkBytes = 1024 * 1024;

    private readonly AdapterOptions _options;

    public OutputPublisher(IOptions<AdapterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    internal async Task<PublishResult> PublishAsync(
        string dirName,
        RenderCheckV1 report,
        bool subtitlesRequested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        // P2: the served file must be the measured, normalised one.
        if (report.Output is not { Normalized: true } measured
            || !string.Equals(Path.GetFileName(measured.File), VideoFileName, StringComparison.Ordinal)
            || !IsSha256(measured.Sha256)
            || measured.Bytes <= 0
            || measured.Bytes > _options.MaxVideoBytes)
        {
            return PublishResult.Failed(CheckFailed);
        }

        // A stat, not a read: the file the report measured must still be there with the measured size.
        var video = SafePath.OpenRead(_options.MptTasksDir, dirName, VideoFileName, _options.MaxVideoBytes, out var probe);
        if (video != SafePathResult.Ok || probe is null)
        {
            return PublishResult.Failed(CheckFailed);
        }

        await using (probe.ConfigureAwait(false))
        {
            if (probe.Length != measured.Bytes)
            {
                return PublishResult.Failed(CheckFailed);
            }
        }

        RenderFileV1? subtitles = null;
        if (subtitlesRequested)
        {
            var opened = SafePath.OpenRead(
                _options.MptTasksDir,
                dirName,
                SubtitlesFileName,
                _options.MaxSubtitleBytes,
                out var srt);
            if (opened == SafePathResult.NotFound)
            {
                // The SRT is absent: the output carries no subtitles and /subtitles answers 404.
            }
            else if (opened != SafePathResult.Ok || srt is null)
            {
                return PublishResult.Failed(CheckFailed);
            }
            else
            {
                await using (srt.ConfigureAwait(false))
                {
                    var (bytes, sha256) = await HashAsync(srt, cancellationToken).ConfigureAwait(false);
                    subtitles = new RenderFileV1(bytes, sha256, report.Subtitles?.Cues ?? 0);
                }
            }
        }

        var output = new RenderOutputV1(
            new RenderFileV1(measured.Bytes, measured.Sha256, Cues: null),
            subtitles,
            report.Narration?.DurationS ?? 0);
        return new PublishResult(output, null);
    }

    private static async Task<(long Bytes, string Sha256)> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashChunkBytes];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
