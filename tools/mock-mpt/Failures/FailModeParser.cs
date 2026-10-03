using NightTales.MockMpt.Fixtures;

namespace NightTales.MockMpt.Failures;

/// <summary>
/// Parses <c>MOCK_FAIL_MODE</c> and the <c>__fail_&lt;mode&gt;__</c> keyword names (11.1.2-T2 Design). The
/// environment value is read once at start: an unknown value is exit 2 naming it and listing the accepted set.
/// A keyword never fails the process, so an unknown keyword name is simply not a mode.
/// </summary>
public static class FailModeParser
{
    /// <summary>The <c>quality:</c> prefix of the <c>quality:&lt;check&gt;</c> mode.</summary>
    public const string QualityPrefix = "quality:";

    internal const string TtsFailed = "tts_failed";
    internal const string ImageModerated = "image_moderated";
    internal const string LostTask = "lost_task";
    internal const string Timeout = "timeout";
    internal const string AssemblyFailed = "assembly_failed";
    internal const string NodeError = "node_error";

    /// <summary>The plain mode names, in the Design table's order (<c>quality:&lt;check&gt;</c> follows).</summary>
    public static readonly IReadOnlyList<string> PlainNames =
    [
        TtsFailed,
        ImageModerated,
        LostTask,
        Timeout,
        AssemblyFailed,
        NodeError,
    ];

    /// <summary>
    /// Parses the environment value. Blank means unset. A <c>quality:</c> value is checked for shape only here;
    /// <see cref="ValidateQuality"/> checks the check id against the fixtures once they are loaded.
    /// </summary>
    public static bool TryParse(string? raw, out FailMode mode, out string? error)
    {
        mode = FailMode.None;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim();
        if (TryParseName(value, out mode))
        {
            return true;
        }

        error = $"MOCK_FAIL_MODE='{value}' is not accepted; accepted: {Accepted()}";
        return false;
    }

    /// <summary>
    /// Fails (a named start-up error) when <paramref name="mode"/> is <c>quality:&lt;check&gt;</c> and the id is not
    /// a check a failing fixture marks failed — an invented id, or an existing id whose fixture value passes, is a
    /// misconfiguration and never a silent success (11.1.2-T2 Failure handling).
    /// </summary>
    public static void ValidateQuality(FailMode mode, FixtureSet fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);

        if (mode.Kind == FailKind.Quality && fixtures.QualityCheckFixture(mode.QualityCheck!) is null)
        {
            var accepted = string.Join(", ", fixtures.QualityCheckIds);
            throw new MockStartupException(
                $"MOCK_FAIL_MODE='{mode.Name}' names no failing check in the D14 fixtures; accepted: "
                + (accepted.Length == 0 ? "(none)" : accepted));
        }
    }

    /// <summary>The mode a keyword name selects, or null for an unknown name or an unusable quality check.</summary>
    public static FailMode? FromKeyword(string name, FixtureSet fixtures)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fixtures);

        if (!TryParseName(name, out var mode) || !mode.IsInjected)
        {
            return null;
        }

        return mode.Kind == FailKind.Quality && fixtures.QualityCheckFixture(mode.QualityCheck!) is null
            ? null
            : mode;
    }

    private static bool TryParseName(string value, out FailMode mode)
    {
        mode = value switch
        {
            TtsFailed => new FailMode(FailKind.TtsFailed),
            ImageModerated => new FailMode(FailKind.ImageModerated),
            LostTask => new FailMode(FailKind.LostTask),
            Timeout => new FailMode(FailKind.Timeout),
            AssemblyFailed => new FailMode(FailKind.AssemblyFailed),
            NodeError => new FailMode(FailKind.NodeError),
            _ => FailMode.None,
        };

        if (mode.IsInjected)
        {
            return true;
        }

        if (value.StartsWith(QualityPrefix, StringComparison.Ordinal) && value.Length > QualityPrefix.Length)
        {
            mode = new FailMode(FailKind.Quality, value[QualityPrefix.Length..]);
            return true;
        }

        return false;
    }

    private static string Accepted() => string.Join(", ", [.. PlainNames, QualityPrefix + "<check>"]);
}
