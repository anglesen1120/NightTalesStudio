using System.Globalization;
using System.Text;

namespace NightTales.MockMpt;

/// <summary>
/// Reads and validates the mock's environment once at start (11 README D14, 11.1.2-T1 Steps 2/5). Every
/// invalid value is a named message and exit code 2 — never a listener that opens with a value nobody can use.
/// </summary>
/// <remarks>
/// The two core-owned values accept both spellings: <c>MOCK_MAX_CONCURRENT</c>/<c>MOCK_API_KEY</c> natively and
/// the <c>RenderNodeServer__*</c> pair the compose service sets directly (the D14 compose snippet). The
/// <c>MOCK_*</c> name wins when both are present, so the value <c>.env</c> carries is also the value
/// <c>tools/dev/up.sh</c> passes.
/// </remarks>
public static class MockMptEnvironment
{
    /// <summary>The fixtures directory relative to the repository root (11 README T33).</summary>
    public static readonly string FixturesRelativePath = Path.Combine("worker-node", "contract", "fixtures");

    /// <summary>The default runtime-state root under the repository (<c>&lt;repo&gt;/.nts</c>, 01 CR-11).</summary>
    private static readonly string NtsHomeRelativePath = ".nts";

    /// <summary>The mock's media/SRT directory below the runtime-state root.</summary>
    private static readonly string MediaRelativePath = Path.Combine("mock-mpt", "media");

    /// <summary>
    /// Reads the configuration from <paramref name="read"/> (env access is injected so a test can drive the
    /// validation without touching the process environment). <paramref name="startDirectory"/> is used to find
    /// the repository root for the default fixtures and media directories.
    /// </summary>
    public static bool TryRead(
        Func<string, string?> read,
        string startDirectory,
        out MockMptOptions options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(startDirectory);

        options = null!;
        error = null;

        var apiKey = ResolveApiKey(read);
        if (Encoding.UTF8.GetByteCount(apiKey) < 32)
        {
            error = "MOCK_API_KEY must be at least 32 bytes (RenderNodeServer:ApiKey, 05 D15 S3)";
            return false;
        }

        if (!TryReadSeconds(read, "MOCK_RENDER_SECONDS", MockMptOptions.DefaultRenderSeconds, out var renderSeconds, out error)
            || !TryReadSeconds(read, "MOCK_VIDEO_SECONDS", MockMptOptions.DefaultVideoSeconds, out var videoSeconds, out error))
        {
            return false;
        }

        var maxConcurrentRaw = FirstNonEmpty(read, "MOCK_MAX_CONCURRENT", "RenderNodeServer__MaxConcurrentTasks");
        var maxConcurrent = MockMptOptions.DefaultMaxConcurrentTasks;
        if (maxConcurrentRaw is not null
            && !TryParsePositive(maxConcurrentRaw, "MOCK_MAX_CONCURRENT", out maxConcurrent, out error))
        {
            return false;
        }

        var repositoryRoot = TryFindRepositoryRoot(startDirectory);
        var fixturesDirectory = read("MOCK_FIXTURES_DIR");
        if (string.IsNullOrWhiteSpace(fixturesDirectory))
        {
            if (repositoryRoot is null)
            {
                error =
                    $"MOCK_FIXTURES_DIR is unset and '{FixturesRelativePath}' was not found above "
                    + $"'{startDirectory}' (05 D14 fixtures)";
                return false;
            }

            fixturesDirectory = Path.Combine(repositoryRoot, FixturesRelativePath);
        }

        var mediaDirectory = read("MOCK_MEDIA_DIR");
        if (string.IsNullOrWhiteSpace(mediaDirectory))
        {
            // The runtime-state root honors NTS_HOME first (01 CR-11, review fix MOCK-FS-03): a chosen home
            // must own the canned media and every task SRT, not the working tree.
            var ntsHome = read("NTS_HOME");
            var runtimeRoot = string.IsNullOrWhiteSpace(ntsHome)
                ? Path.Combine(repositoryRoot ?? startDirectory, NtsHomeRelativePath)
                : ntsHome.Trim();
            mediaDirectory = Path.Combine(runtimeRoot, MediaRelativePath);
        }

        var fixture = read("MOCK_FIXTURE");
        options = new MockMptOptions
        {
            ApiKey = apiKey,
            RenderSeconds = renderSeconds,
            VideoSeconds = videoSeconds,
            MaxConcurrentTasks = maxConcurrent,
            FixturesDirectory = fixturesDirectory,
            MediaDirectory = mediaDirectory,
            Fixture = string.IsNullOrWhiteSpace(fixture) ? null : NormalizeFixtureId(fixture),
        };
        return true;
    }

    /// <summary>
    /// The key the core compares <c>x-api-key</c> against: <c>MOCK_API_KEY</c>, else
    /// <c>RenderNodeServer__ApiKey</c>, else the dev default. Shared with the <c>--healthcheck</c> mode so the
    /// probe sends exactly the value the server binds (review fix CODE-2).
    /// </summary>
    public static string ResolveApiKey(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return FirstNonEmpty(read, "MOCK_API_KEY", "RenderNodeServer__ApiKey") ?? MockMptOptions.DefaultApiKey;
    }

    /// <summary>The repository root above <paramref name="startDirectory"/>, or null when the marker is absent.</summary>
    public static string? TryFindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, FixturesRelativePath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Accepts a fixture id with or without the <c>.json</c> suffix.</summary>
    public static string NormalizeFixtureId(string fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var trimmed = fixture.Trim();
        return trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^".json".Length]
            : trimmed;
    }

    private static string? FirstNonEmpty(Func<string, string?> read, string primary, string secondary)
    {
        var value = read(primary);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        value = read(secondary);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryReadSeconds(
        Func<string, string?> read,
        string name,
        int fallback,
        out int value,
        out string? error)
    {
        var raw = read(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            error = null;
            return true;
        }

        return TryParsePositive(raw, name, out value, out error, minimum: 1);
    }

    private static bool TryParsePositive(
        string raw,
        string name,
        out int value,
        out string? error,
        int minimum = 1)
    {
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= minimum)
        {
            error = null;
            return true;
        }

        error = $"{name} must be an integer ≥ {minimum} (found '{raw.Trim()}')";
        value = 0;
        return false;
    }
}
