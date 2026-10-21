namespace NightTales.RenderNode.Contract;

/// <summary>
/// The eight route patterns of 05.4.1-T2 §2 — "eight routes, nothing else". The core maps exactly these
/// and answers every other path with <see cref="AdapterErrorCodes.RouteNotFound"/>; the backend client and
/// the mock build their URLs from the same literals.
/// </summary>
public static class RenderNodeRoutes
{
    public const string Renders = "/v1/renders";
    public const string RenderById = "/v1/renders/{task_id}";
    public const string RenderVideo = "/v1/renders/{task_id}/video";
    public const string RenderSubtitles = "/v1/renders/{task_id}/subtitles";
    public const string RenderCancel = "/v1/renders/{task_id}/cancel";
    public const string Health = "/v1/health";
    public const string ProbeImage = "/v1/probe-image";

    /// <summary>The route parameter the two <c>{task_id}</c> consumers parse (never a store lookup before the parse).</summary>
    public const string TaskIdParameter = "task_id";
}
