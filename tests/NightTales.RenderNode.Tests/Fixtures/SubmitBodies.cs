using System.Text.Json.Nodes;

namespace NightTales.RenderNode.Tests.Fixtures;

/// <summary>
/// Builds a valid <c>render-submit.v1</c> body from a D14 fixture with one field replaced — the fixture is the
/// wire, so a test body can never drift from the contract, and the request body's sha256 changes with the
/// idempotency key the same way a real submit's would.
/// </summary>
internal static class SubmitBodies
{
    internal const string LandscapeVi = "render-submit.v1.landscape-vi.json";
    internal const string PortraitEn = "render-submit.v1.portrait-en.json";
    internal const string NoMusic = "render-submit.v1.no-music.json";

    internal static string From(Guid renderJobId, string fileName = LandscapeVi)
    {
        var body = JsonNode.Parse(FixtureFiles.Read(fileName))
            ?? throw new InvalidOperationException($"{fileName} is not a JSON object.");
        body["render_job_id"] = renderJobId.ToString();
        return body.ToJsonString();
    }

    /// <summary>The body with one <c>mpt</c> member replaced or added, for a validation test.</summary>
    internal static string WithMpt(Guid renderJobId, string key, JsonNode? value, bool remove = false)
    {
        var body = JsonNode.Parse(FixtureFiles.Read(LandscapeVi))
            ?? throw new InvalidOperationException($"{LandscapeVi} is not a JSON object.");
        body["render_job_id"] = renderJobId.ToString();
        var mpt = body["mpt"]!.AsObject();
        if (remove)
        {
            mpt.Remove(key);
        }
        else
        {
            mpt[key] = value;
        }

        return body.ToJsonString();
    }

    /// <summary>The body with one <c>image_plan</c> member replaced, for a validation test.</summary>
    internal static string WithImagePlan(Guid renderJobId, string key, JsonNode? value)
    {
        var body = JsonNode.Parse(FixtureFiles.Read(LandscapeVi))
            ?? throw new InvalidOperationException($"{LandscapeVi} is not a JSON object.");
        body["render_job_id"] = renderJobId.ToString();
        body["image_plan"]!.AsObject()[key] = value;
        return body.ToJsonString();
    }
}
