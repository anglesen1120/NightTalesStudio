namespace NightTales.RenderNode.Contract;

/// <summary>
/// The schema literals of the frozen <c>render-node.v1</c> contract (05.4.1-T2, 03 D6). Every body on
/// the interface carries one, so the adapter, the mock and the backend client compare against the same
/// constants.
/// </summary>
public static class RenderNodeSchemas
{
    public const string RenderSubmit = "render-submit.v1";
    public const string RenderStatus = "render-status.v1";
    public const string RenderNodeHealth = "render-node-health.v1";
    public const string RenderCheck = "render-check.v1";
    public const string Images = "images.v1";
    public const string ProxyHealth = "proxy-health.v1";
}
