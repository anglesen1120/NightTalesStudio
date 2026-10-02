namespace NightTales.RenderNode.Contract;

/// <summary>
/// The <c>status</c> literals of <c>render-node-health.v1</c> and <c>proxy-health.v1</c> (05.4.1-T2 §4,
/// 04 D8): consumers key off the HTTP code (200/503) and the reasons, never off free text.
/// </summary>
public static class RenderNodeHealthStatuses
{
    public const string Ok = "ok";
    public const string Error = "error";
}
