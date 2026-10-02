using System.Text.Json;
using System.Text.Json.Nodes;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Media;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Backend;

/// <summary>
/// Fills the terminal blocks of a finished task (11.1.2-T1 Design): <c>output</c> from the served canned file
/// and the generated SRT, <c>check</c> from the D14 <c>render-check.v1.*</c> fixture with the served file's
/// measured facts substituted, and <c>images</c> from the plan sink.
/// </summary>
/// <remarks>
/// The mock cannot run <c>render_check.py</c> (no node, no ffmpeg pipeline), so the check block is the frozen
/// fixture with <c>input</c>/<c>output</c> <c>file</c>/<c>bytes</c>/<c>sha256</c> and <c>subtitles.cues</c>
/// replaced by what this mock actually served. That keeps the 05 contract's identity
/// <c>check.output.sha256 = output.video.sha256</c> and the <c>Content-Length</c> the download sends.
/// </remarks>
public static class FakePostProcessor
{
    /// <summary>The <c>output</c> block of a succeeded task.</summary>
    public static RenderOutputV1 BuildOutput(CannedMediaFile video, SubtitleFile? subtitles, int videoSeconds)
    {
        ArgumentNullException.ThrowIfNull(video);

        return new RenderOutputV1(
            Video: new RenderFileV1(video.Bytes, video.Sha256, Cues: null),
            Subtitles: subtitles is null
                ? null
                : new RenderFileV1(subtitles.Content.Length, subtitles.Sha256, subtitles.Cues),
            NarrationSeconds: videoSeconds);
    }

    /// <summary>The <c>check</c> block, the fixture with this mock's served-file facts substituted.</summary>
    public static JsonElement BuildCheck(FixtureSet fixtures, CannedMediaFile video, SubtitleFile? subtitles)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(video);

        var id = subtitles is null ? FixtureSet.NoSubtitlesCheckId : FixtureSet.OkCheckId;
        var node = JsonNode.Parse(fixtures.Check(id).GetRawText())?.AsObject()
            ?? throw new MockStartupException($"fixture '{id}' is not a JSON object");

        PatchFile(node["input"] as JsonObject, video);
        PatchFile(node["output"] as JsonObject, video);
        PatchVideo(node["video"] as JsonObject, video);
        if (subtitles is not null && node["subtitles"] is JsonObject subtitlesNode)
        {
            subtitlesNode["cues"] = subtitles.Cues;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The <c>images</c> block of a finished task: the plan sink's report serialized with
    /// <see cref="RenderNodeJson.Strict"/>, or null when the sink has nothing to report. The store keeps the
    /// block as a <see cref="JsonElement"/>, so the core re-serves it verbatim (05 D4).
    /// </summary>
    public static JsonElement? BuildImages(IImagePlanSink planSink, Guid taskId)
    {
        ArgumentNullException.ThrowIfNull(planSink);

        var report = planSink.BuildReport(taskId);
        return report is null ? null : JsonSerializer.SerializeToElement(report, RenderNodeJson.Strict);
    }

    private static void PatchFile(JsonObject? block, CannedMediaFile video)
    {
        if (block is null)
        {
            return;
        }

        block["file"] = Path.GetFileName(video.Path);
        block["bytes"] = video.Bytes;
        block["sha256"] = video.Sha256;
    }

    /// <summary>
    /// Replaces the checked file's coded dimensions with the served canned file's own (review fix CODE-3): a
    /// portrait task must report 1080×1920, not the fixture's landscape numbers.
    /// </summary>
    private static void PatchVideo(JsonObject? block, CannedMediaFile video)
    {
        if (block is null)
        {
            return;
        }

        block["width"] = video.Width;
        block["height"] = video.Height;
    }
}
