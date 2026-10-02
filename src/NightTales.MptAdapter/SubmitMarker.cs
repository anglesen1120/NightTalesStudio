using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.RenderNode.Contract;

namespace NightTales.MptAdapter;

/// <summary>
/// The two submit-body facts the check needs and the stored row does not carry: <c>--aspect</c> and
/// <c>--subtitles</c> (D6a). Written beside the report under <c>&lt;DataDir&gt;/tasks/&lt;task_id&gt;</c> when MPT
/// accepts the submit, so a restarted adapter re-checks with the same arguments.
/// </summary>
internal sealed record SubmitMarker(
    [property: JsonPropertyName("aspect")] string Aspect,
    [property: JsonPropertyName("subtitles")] bool Subtitles)
{
    internal const string FileName = "submit.json";
    internal const string Landscape = "landscape";
    internal const string Portrait = "portrait";

    /// <summary>05 D3's <c>video_aspect</c> (<c>16:9</c>/<c>9:16</c>) as the tool's <c>--aspect</c> value.</summary>
    internal static string AspectFor(string videoAspect) =>
        string.Equals(videoAspect, "9:16", StringComparison.Ordinal) ? Portrait : Landscape;

    internal static string PathFor(AdapterOptions options, Guid taskId) =>
        Path.Combine(options.AdapterTasksDir, taskId.ToString("D"), FileName);

    internal static Task WriteAsync(
        AdapterOptions options,
        Guid taskId,
        MptSubmitFields mpt,
        CancellationToken cancellationToken)
    {
        var marker = new SubmitMarker(AspectFor(mpt.VideoAspect), mpt.SubtitleEnabled);
        return SafePath.WriteAtomicAsync(
            PathFor(options, taskId),
            JsonSerializer.SerializeToUtf8Bytes(marker),
            cancellationToken);
    }

    /// <summary>The stored marker, or null when it is missing or unreadable.</summary>
    internal static async Task<SubmitMarker?> ReadAsync(
        AdapterOptions options,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var result = SafePath.OpenRead(
            options.AdapterTasksDir,
            taskId.ToString("D"),
            FileName,
            maxBytes: 4096,
            out var stream);
        if (result != SafePathResult.Ok || stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var marker = await JsonSerializer
                    .DeserializeAsync<SubmitMarker>(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return marker is { Aspect: Landscape or Portrait } ? marker : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
