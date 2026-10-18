using Microsoft.Extensions.Configuration;

namespace NightTales.Infrastructure.Telemetry.Redaction;

/// <summary>
/// The one redaction list both writers share — the Serilog enricher and the OpenTelemetry activity processor
/// (09.9.3-T2, 09 README BD12/BD16). The five query keys of BD12 are frozen, and the resumable-upload session
/// handle is frozen with them (06.2.3-T1): configuration can only add to the list, so no deployment can switch the
/// redaction of a credential off.
/// </summary>
public sealed class RedactionOptions
{
    /// <summary>The text that replaces a redacted value.</summary>
    public const string Mask = "<redacted>";

    /// <summary>The configuration key that adds query keys to the frozen list.</summary>
    public const string QueryKeysKey = "Logging:RedactionQueryKeys";

    /// <summary>The configuration key that adds header names to the default list.</summary>
    public const string HeadersKey = "Logging:RedactionHeaders";

    /// <summary>
    /// The BD12 query keys plus the resumable-upload session handle — never configurable away. A session URI is a
    /// bearer capability for the upload it names (06.2.3-T1), so <c>upload_id</c> is masked wherever a URL is
    /// written — the <c>url.full</c> span tag of an <c>HttpClient</c> call included.
    /// </summary>
    public static readonly IReadOnlyList<string> FrozenQueryKeys =
        ["access_token", "ticket", "token", "code", "state", "upload_id"];

    /// <summary>The default header names whose values are masked.</summary>
    public static readonly IReadOnlyList<string> DefaultHeaders = ["authorization", "cookie", "set-cookie"];

    private RedactionOptions(HashSet<string> queryKeys, HashSet<string> headers)
    {
        QueryKeys = queryKeys;
        Headers = headers;
    }

    /// <summary>The frozen defaults, for hosts and tests that read no configuration.</summary>
    public static RedactionOptions Default { get; } = Create([], []);

    /// <summary>The query keys whose values are masked, compared ordinal-ignore-case on the decoded key.</summary>
    public IReadOnlySet<string> QueryKeys { get; }

    /// <summary>The header names whose values are masked, compared ordinal-ignore-case.</summary>
    public IReadOnlySet<string> Headers { get; }

    /// <summary>Reads <see cref="QueryKeysKey"/> and <see cref="HeadersKey"/>.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The frozen defaults plus whatever the configuration adds.</returns>
    /// <exception cref="InvalidOperationException">
    /// A configured list is present but empty or holds a blank entry: an operator who sets the key meant to
    /// redact something, and silently ignoring the value would hide a misconfiguration.
    /// </exception>
    public static RedactionOptions Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Create(Read(configuration, QueryKeysKey), Read(configuration, HeadersKey));
    }

    private static RedactionOptions Create(IEnumerable<string> extraQueryKeys, IEnumerable<string> extraHeaders)
    {
        var queryKeys = new HashSet<string>(FrozenQueryKeys, StringComparer.OrdinalIgnoreCase);
        queryKeys.UnionWith(extraQueryKeys);
        var headers = new HashSet<string>(DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        headers.UnionWith(extraHeaders);

        return new RedactionOptions(queryKeys, headers);
    }

    private static List<string> Read(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var entries = new List<string>();
        if (section.Value is { } scalar)
        {
            // An environment variable carries a list as one comma-separated value.
            entries.AddRange(scalar.Split(',', StringSplitOptions.TrimEntries));
        }

        foreach (var child in section.GetChildren())
        {
            entries.Add(child.Value ?? string.Empty);
        }

        if (section.Value is null && entries.Count == 0)
        {
            return entries;
        }

        if (entries.Count == 0 || entries.Exists(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"Configuration value '{key}' must not be empty or hold a blank entry.");
        }

        return entries.ConvertAll(static entry => entry.Trim());
    }
}
