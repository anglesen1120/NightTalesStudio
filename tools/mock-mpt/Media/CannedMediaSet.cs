using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Media;

/// <summary>One canned MP4: where it lives and the measured facts the status and the headers report.</summary>
/// <param name="Path">The file on disk.</param>
/// <param name="Bytes">The file length, reported as <c>output.video.bytes</c> and <c>Content-Length</c>.</param>
/// <param name="Sha256">Lowercase hex sha256 of the file, reported as <c>output.video.sha256</c>.</param>
/// <param name="ContentType">Always <see cref="RenderNodeHeaders.VideoContentType"/>.</param>
/// <param name="Width">The file's coded width (1920 landscape, 1080 portrait), reported in the check block.</param>
/// <param name="Height">The file's coded height (1080 landscape, 1920 portrait), reported in the check block.</param>
public sealed record CannedMediaFile(
    string Path,
    long Bytes,
    string Sha256,
    string ContentType,
    int Width,
    int Height);

/// <summary>
/// The two canned 10-second MP4 files of 05 D14 / 11 F25, one per aspect, plus the recipe that generates them
/// (<c>ffmpeg -f lavfi</c>). The Dockerfile runs the same recipe in a media stage; the tests run it once into a
/// temp directory. Nothing binary is committed.
/// </summary>
/// <remarks>
/// The served file is always the 10-second canned one while <c>MOCK_VIDEO_SECONDS</c> only changes the reported
/// <c>narration_seconds</c> and <c>output</c> duration — deliberate, documented in the mock's README.
/// </remarks>
public sealed class CannedMediaSet
{
    /// <summary>The landscape name (16:9).</summary>
    public const string LandscapeFileName = "landscape.mp4";

    /// <summary>The portrait name (9:16).</summary>
    public const string PortraitFileName = "portrait.mp4";

    /// <summary>The canned duration every generated file has, in seconds.</summary>
    public const int CannedSeconds = 10;

    /// <summary>The landscape file's coded width/height (the pinned <c>1920x1080</c> recipe).</summary>
    public const int LandscapeWidth = 1920;

    /// <summary>The landscape file's coded height.</summary>
    public const int LandscapeHeight = 1080;

    /// <summary>The portrait file's coded width (the pinned <c>1080x1920</c> recipe).</summary>
    public const int PortraitWidth = 1080;

    /// <summary>The portrait file's coded height.</summary>
    public const int PortraitHeight = 1920;

    private const string LandscapeSize = "1920x1080";
    private const string PortraitSize = "1080x1920";

    private CannedMediaSet(string directory, CannedMediaFile landscape, CannedMediaFile portrait)
    {
        Directory = directory;
        Landscape = landscape;
        Portrait = portrait;
    }

    /// <summary>The directory holding both files and the per-task SRT files.</summary>
    public string Directory { get; }

    /// <summary>The 1920×1080 file served for <c>video_aspect = "16:9"</c>.</summary>
    public CannedMediaFile Landscape { get; }

    /// <summary>The 1080×1920 file served for <c>video_aspect = "9:16"</c>.</summary>
    public CannedMediaFile Portrait { get; }

    /// <summary>The file for a submit's <c>video_aspect</c>; the validator already closed the enum.</summary>
    public CannedMediaFile ForAspect(string aspect) =>
        string.Equals(aspect, "9:16", StringComparison.Ordinal) ? Portrait : Landscape;

    /// <summary>The SRT a task's subtitles are written to.</summary>
    public string SubtitlePath(Guid taskId) =>
        Path.Combine(Directory, string.Create(CultureInfo.InvariantCulture, $"{taskId:N}.srt"));

    /// <summary>
    /// Ensures both files exist under <paramref name="mediaDirectory"/>, generating the missing ones with the
    /// pinned recipe, and returns their measured facts. Missing media with no <c>ffmpeg</c> on the PATH is a
    /// named start-up failure, never a silent 500 at download time.
    /// </summary>
    public static CannedMediaSet Ensure(string mediaDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaDirectory);

        System.IO.Directory.CreateDirectory(mediaDirectory);
        var landscape = Path.Combine(mediaDirectory, LandscapeFileName);
        var portrait = Path.Combine(mediaDirectory, PortraitFileName);

        Generate(landscape, LandscapeSize);
        Generate(portrait, PortraitSize);

        return new CannedMediaSet(
            mediaDirectory,
            Measure(landscape, LandscapeWidth, LandscapeHeight),
            Measure(portrait, PortraitWidth, PortraitHeight));
    }

    private static void Generate(string path, string size)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return;
        }

        // The temp name keeps a `.mp4` suffix so ffmpeg picks the muxer from the extension (the pinned recipe
        // carries no `-f mp4`).
        var temporary = path + ".tmp.mp4";
        var arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size={size}:rate=30 "
            + $"-f lavfi -i sine=frequency=440 -t {CannedSeconds} -c:v libx264 -c:a aac "
            + $"-movflags +faststart \"{temporary}\"");
        try
        {
            RunFfmpeg(arguments);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            {
                throw new MockStartupException($"ffmpeg produced no media at '{path}'");
            }

            // Publish only a complete file: a half-written MP4 is never what a download streams.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RunFfmpeg(string arguments)
    {
        var startInfo = new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new MockStartupException("ffmpeg could not be started");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new MockStartupException(
                "ffmpeg was not found on the PATH: the mock generates its canned 10 s media with "
                + "`ffmpeg -f lavfi` once (tools/mock-mpt/README.md)",
                exception);
        }

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new MockStartupException(
                $"ffmpeg exited {process.ExitCode} while generating the canned media: {Tail(error)}");
        }
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        const int limit = 400;
        return trimmed.Length <= limit ? trimmed : trimmed[^limit..];
    }

    private static CannedMediaFile Measure(string path, int width, int height)
    {
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new CannedMediaFile(path, stream.Length, sha256, RenderNodeHeaders.VideoContentType, width, height);
    }
}
