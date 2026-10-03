using System.Text.Json;

namespace NightTales.MptAdapter.Health;

/// <summary>
/// The release bundle's static facts, read once at startup (README §10 T19): <c>mpt.commit</c> comes from the
/// bundle's <c>MANIFEST.json</c> (<c>mpt_commit</c>) or, failing that, <c>worker-node/pins.env</c>
/// (<c>MPT_COMMIT=</c>). Neither being readable is not fatal: the commit is null and the node says so.
/// </summary>
internal sealed record BundleInfo(string Root, string? MptCommit)
{
    /// <summary>The bundle root is the parent of <c>Adapter:MptRoot</c> (<c>/opt/nts/current/mpt</c> → <c>/opt/nts/current</c>).</summary>
    internal static BundleInfo Load(AdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(options.MptRoot)) ?? options.MptRoot;
        return new BundleInfo(root, FromManifest(root) ?? FromPins(root));
    }

    private static string? FromManifest(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "MANIFEST.json")));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("mpt_commit", out var commit)
                   && commit.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(commit.GetString())
                ? commit.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? FromPins(string root)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(root, "worker-node", "pins.env")))
            {
                if (line.StartsWith("MPT_COMMIT=", StringComparison.Ordinal))
                {
                    var value = line["MPT_COMMIT=".Length..].Trim().Trim('"', '\'');
                    return value.Length == 0 ? null : value;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
