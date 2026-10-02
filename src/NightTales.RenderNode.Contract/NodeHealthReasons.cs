namespace NightTales.RenderNode.Contract;

/// <summary>
/// The closed <c>reasons</c> set of <c>render-node-health.v1</c> (05.4.1-T2 §4 plus the 04 D8 proxy
/// reason the adapter maps, and <c>gpu_unavailable</c> from 11 CR-4). <c>status = "ok"</c> iff it is
/// empty, and dispatch keys off the HTTP code and these reasons (11.3.3 D13).
/// </summary>
public static class NodeHealthReasons
{
    public const string MptUnreachable = "mpt_unreachable";
    public const string MptUnauthorized = "mpt_unauthorized";
    public const string TtsProxyNotOk = "tts_proxy_not_ok";
    public const string ImageRelayNotOk = "image_relay_not_ok";
    public const string FontMissing = "font_missing";
    public const string MusicMissing = "music_missing";
    public const string DiskLow = "disk_low";
    public const string ConfigMismatch = "config_mismatch";
    public const string CodecUnavailable = "codec_unavailable";
    public const string GpuUnavailable = "gpu_unavailable";
}
