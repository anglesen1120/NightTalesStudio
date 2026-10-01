namespace NightTales.RenderNode.Contract;

/// <summary>
/// The stage names of 05.4.1-T2 §4 (MPT progress boundaries measured in the S3 trace). <c>checking</c> is
/// the adapter's own post-processing stage and is the only non-terminal stage that may report 100.
/// </summary>
public static class RenderStages
{
    public const string Queued = "queued";
    public const string Voice = "voice";
    public const string Subtitles = "subtitles";
    public const string Images = "images";
    public const string Assembling = "assembling";
    public const string Checking = "checking";
    public const string Done = "done";

    /// <summary>Every stage, in §4 order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Queued,
        Voice,
        Subtitles,
        Images,
        Assembling,
        Checking,
        Done,
    ];
}
