using System.Text.Json.Nodes;

namespace NightTales.MockMpt.Tests.TestHost;

/// <summary>
/// Builds valid <c>render-submit.v1</c> bodies from the D14 submit fixtures. Every body gets a fresh
/// <c>render_job_id</c> unless the test passes one, because two submits of one job id with different bodies are
/// the contract's 409 and not two tasks (05 D4/D5).
/// </summary>
internal static class SubmitBodies
{
    internal const string Landscape = "render-submit.v1.landscape-vi";
    internal const string Portrait = "render-submit.v1.portrait-en";
    internal const string NoMusic = "render-submit.v1.no-music";

    /// <summary>The fixture body, optionally with a chosen job id, script and subtitles switch.</summary>
    internal static string Build(string id, Guid? renderJobId = null, string? script = null, bool? subtitleEnabled = null)
    {
        var node = JsonNode.Parse(FixtureFiles.Read(id)) as JsonObject
            ?? throw new InvalidOperationException($"fixture '{id}' is not a JSON object");

        node["render_job_id"] = (renderJobId ?? Guid.CreateVersion7()).ToString();
        if (script is not null)
        {
            node["mpt"]!["video_script"] = script;
        }

        if (subtitleEnabled is { } enabled)
        {
            node["mpt"]!["subtitle_enabled"] = enabled;
        }

        return node.ToJsonString();
    }

    /// <summary>The landscape fixture with a fresh job id.</summary>
    internal static string LandscapeFresh() => Build(Landscape);

    /// <summary>The portrait fixture with a fresh job id.</summary>
    internal static string PortraitFresh() => Build(Portrait);
}
