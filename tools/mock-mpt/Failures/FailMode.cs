namespace NightTales.MockMpt.Failures;

/// <summary>The failure behaviours of the <c>MOCK_FAIL_MODE</c> table (11.1.2-T2 Design, 05 D5).</summary>
public enum FailKind
{
    /// <summary>No injection: the normal success path.</summary>
    None,

    /// <summary><c>tts_failed</c>: stops in the <c>voice</c> stage, outcome <c>tts_failed</c>/<c>audio</c>.</summary>
    TtsFailed,

    /// <summary><c>image_moderated</c>: the alias of <c>MOCK_IMAGE_REPORT=moderated_all</c> (05 CR-4).</summary>
    ImageModerated,

    /// <summary><c>lost_task</c>: the row disappears, every <c>GET</c> is 404 <c>adapter_task_unknown</c>.</summary>
    LostTask,

    /// <summary><c>timeout</c>: progress stops at <c>assembling</c> 95 % and never becomes terminal.</summary>
    Timeout,

    /// <summary><c>assembly_failed</c>: stops in <c>assembling</c>, outcome <c>render_assembly_failed</c>/<c>video</c>.</summary>
    AssemblyFailed,

    /// <summary><c>node_error</c>: stops at <c>preflight</c>, outcome <c>render_node_error</c>/<c>preflight</c>.</summary>
    NodeError,

    /// <summary><c>quality:&lt;check&gt;</c>: succeeds with a <c>check</c> block that fails the named check.</summary>
    Quality,
}

/// <summary>One parsed <c>MOCK_FAIL_MODE</c> value (or the mode a keyword/rate draw selected).</summary>
/// <param name="Kind">Which behaviour the mock applies.</param>
/// <param name="QualityCheck">The <c>render-quality.v1</c> check id for <see cref="FailKind.Quality"/>, else null.</param>
public readonly record struct FailMode(FailKind Kind, string? QualityCheck = null)
{
    /// <summary>The normal path.</summary>
    public static FailMode None { get; } = new(FailKind.None);

    /// <summary>True when this mode changes the mock's behaviour.</summary>
    public bool IsInjected => Kind != FailKind.None;

    /// <summary>The wire name of the mode, as written in <c>MOCK_FAIL_MODE</c> and the log: <c>none</c> when unset.</summary>
    public string Name => Kind switch
    {
        FailKind.TtsFailed => FailModeParser.TtsFailed,
        FailKind.ImageModerated => FailModeParser.ImageModerated,
        FailKind.LostTask => FailModeParser.LostTask,
        FailKind.Timeout => FailModeParser.Timeout,
        FailKind.AssemblyFailed => FailModeParser.AssemblyFailed,
        FailKind.NodeError => FailModeParser.NodeError,
        FailKind.Quality => FailModeParser.QualityPrefix + QualityCheck,
        _ => "none",
    };
}
