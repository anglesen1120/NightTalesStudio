using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Infrastructure.Prompts;

/// <summary>
/// Rules 1-6 of the prompt catalog's start-up gate (02.2.3-T1): it loads the manifest, resolves every
/// tracked key to an embedded resource, and reports <b>every</b> violation of one load instead of
/// throwing at the first.
/// </summary>
/// <remarks>
/// <para>
/// The six rules: 1 the manifest parses and its <c>release</c> is well formed; 2 all six
/// <c>topic × language</c> presets are present; 3 the writer system prompt, the shared blocks a preset
/// substitutes and the story schema are present; 4 every manifest key is an embedded resource and every
/// embedded <em>runtime</em> file is tracked; 5 each file's sha256 equals the manifest's; 6 every
/// placeholder is in the closed list and every schema parses as JSON.
/// </para>
/// <para>
/// Rule 4 works in the dotted form of the resource names, because the reverse of the embedding's
/// <c>/</c> to <c>.</c> is ambiguous: a key is matched by the dotted suffix
/// <c>".story.ghost.vi.v1.md"</c>, and a resource is checked by comparing its dotted remainder against
/// the key set. The LinkBase prefix is derived from the resources themselves — the part every matched
/// name shares before its key — so a change to the embedding's prefix needs no re-hash and no constant
/// here.
/// </para>
/// <para>
/// The runtime set excludes <c>fixtures/**</c>, <c>tools/**</c>, <c>manifest.json</c>,
/// <c>REFERENCES.md</c>, <c>**/.gitkeep</c> and dotfiles (02 README T27/T75): a fixture edit must never
/// invalidate a release. Those files still ship embedded, because the injection and seeded-defect tests
/// read them from the same tree.
/// </para>
/// </remarks>
internal sealed class PromptCatalogValidator
{
    /// <summary>The roots a release never tracks (02 README T27/T75).</summary>
    private static readonly string[] ExcludedRoots = ["fixtures", "tools"];

    private readonly IPromptResourceSource _source;
    private readonly List<string> _violations = [];
    private readonly Dictionary<string, string> _logicalNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private string _prefix = string.Empty;

    /// <summary>Creates the validator of one load.</summary>
    /// <param name="source">The resource source the tree is read from.</param>
    internal PromptCatalogValidator(IPromptResourceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
    }

    /// <summary>The manifest the load parsed and normalised; empty when the load could not read it.</summary>
    internal PromptManifest Manifest { get; private set; } = PromptManifest.Empty;

    /// <summary>The bytes of every tracked file, keyed by its manifest key.</summary>
    internal IReadOnlyDictionary<string, byte[]> Bytes => _bytes;

    /// <summary>Every violation of the load, in rule order.</summary>
    internal IReadOnlyList<string> Violations => _violations;

    /// <summary>Runs rules 1-6 over the source's tree.</summary>
    internal void Validate()
    {
        Manifest = PromptManifestLoader.Load(_source, _violations);

        ResolveResources();
        RequirePresets();
        RequireRequiredFiles();
        ReportUntrackedRuntimeFiles();
        VerifyDigests();
        VerifyTemplates();
    }

    /// <summary>A manifest key's dotted form, the shape an embedded logical name carries.</summary>
    /// <param name="key">The key relative to <c>prompts/</c>.</param>
    /// <returns>The key with <c>/</c> replaced by <c>.</c>.</returns>
    internal static string Dotted(string key) => key.Replace('/', '.');

    /// <summary>
    /// Whether a dotted remainder is outside the release's runtime set: under <c>fixtures/</c> or
    /// <c>tools/</c>, the manifest or the reference document itself, or a dotfile — an empty dotted
    /// segment is a path component that starts with <c>.</c>, which is how <c>story/.gitkeep</c> reads
    /// as <c>story..gitkeep</c>.
    /// </summary>
    /// <param name="dotted">The remainder of a logical name after the LinkBase prefix.</param>
    /// <returns><see langword="true"/> when the file is never tracked.</returns>
    internal static bool IsExcluded(string dotted)
    {
        ArgumentNullException.ThrowIfNull(dotted);

        var segments = dotted.Split('.');

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return true;
            }
        }

        if (dotted is PromptManifestLoader.ManifestFileName or PromptManifestLoader.ReferencesFileName)
        {
            return true;
        }

        return ExcludedRoots.Contains(segments[0], StringComparer.Ordinal);
    }

    /// <summary>Rule 4a: resolves each manifest key to exactly one embedded resource, and the LinkBase prefix.</summary>
    private void ResolveResources()
    {
        var keys = Manifest.Files.Keys.Order(StringComparer.Ordinal).ToList();
        var matches = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var name in _source.LogicalNames)
        {
            foreach (var key in keys)
            {
                if (!name.EndsWith("." + Dotted(key), StringComparison.Ordinal))
                {
                    continue;
                }

                if (!matches.TryGetValue(key, out var list))
                {
                    list = [];
                    matches[key] = list;
                }

                list.Add(name);
            }
        }

        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!matches.TryGetValue(key, out var list) || list.Count != 1)
            {
                continue;
            }

            _logicalNames[key] = list[0];
            // The prefix keeps the separator dot: a logical name is <prefix>.<dotted key>, so the
            // remainder of a resource is the dotted key itself. Stripping the dot too would make every
            // remainder start with an empty segment — which `IsExcluded` reads as a dotfile — and rule 4b
            // would never report an untracked runtime file.
            prefixes.Add(list[0][..(list[0].Length - Dotted(key).Length)]);
        }

        if (prefixes.Count == 1)
        {
            _prefix = prefixes.First();
        }
        else if (prefixes.Count > 1)
        {
            _violations.Add(
                "the embedded resources do not share one LinkBase prefix: "
                + string.Join(", ", prefixes.Order(StringComparer.Ordinal)));
        }

        foreach (var key in keys)
        {
            if (!matches.TryGetValue(key, out var list) || list.Count == 0)
            {
                _violations.Add($"manifest file '{key}' is not an embedded resource (not found)");
            }
            else if (list.Count > 1)
            {
                _violations.Add($"manifest file '{key}' is ambiguous: {string.Join(", ", list)}");
            }
        }
    }

    /// <summary>Rule 2: every <c>topic × language</c> preset is present, with no gap.</summary>
    private void RequirePresets()
    {
        foreach (var topic in Enum.GetValues<Topic>())
        {
            foreach (var language in Enum.GetValues<Language>())
            {
                if (!IsPresent(PromptKeys.Preset(topic, language)))
                {
                    _violations.Add($"missing preset {PromptKeys.PresetName(topic, language)}");
                }
            }
        }
    }

    /// <summary>
    /// Rule 3: the writer system prompt, the four shared blocks a preset substitutes through its
    /// placeholders (both languages where they vary) and the story schema are present.
    /// </summary>
    private void RequireRequiredFiles()
    {
        var required = new List<string>
        {
            PromptKeys.SystemPrompt,
            "shared/keyword-rules.v1.md",
            PromptKeys.StructureVocab,
            PromptKeys.StorySchema,
        };

        foreach (var language in Enum.GetValues<Language>())
        {
            required.Add($"shared/ear-rules.{PromptKeys.Spelling(language)}.v1.md");
            required.Add($"shared/overused.{PromptKeys.Spelling(language)}.v1.txt");
        }

        foreach (var key in required)
        {
            if (!IsPresent(key))
            {
                _violations.Add($"missing required file '{key}'");
            }
        }
    }

    /// <summary>Rule 4b: every embedded runtime file is tracked by the manifest.</summary>
    private void ReportUntrackedRuntimeFiles()
    {
        if (_prefix.Length == 0)
        {
            // Rule 4a already reported that no key resolved, so there is no prefix to strip.
            return;
        }

        var tracked = new HashSet<string>(Manifest.Files.Keys.Select(Dotted), StringComparer.Ordinal);

        foreach (var name in _source.LogicalNames)
        {
            if (!name.StartsWith(_prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = name[_prefix.Length..];
            if (IsExcluded(remainder) || tracked.Contains(remainder))
            {
                continue;
            }

            _violations.Add(
                $"embedded runtime file '{remainder}' is not tracked by the manifest "
                + "(the key form, with '/' written as '.')");
        }
    }

    /// <summary>
    /// Rule 5: each tracked file's sha256 equals the manifest's. The bytes are read and cached before the
    /// digest is examined, so an entry with a missing digest still reaches rule 6 in the same load
    /// instead of hiding the file's other violations.
    /// </summary>
    private void VerifyDigests()
    {
        foreach (var (key, info) in Manifest.Files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!_logicalNames.TryGetValue(key, out var name))
            {
                continue;
            }

            var bytes = _source.Read(name);
            _bytes[key] = bytes;

            if (info.Sha256.Length == 0)
            {
                _violations.Add($"manifest entry '{key}' has no sha256");

                continue;
            }

            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(digest, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _violations.Add($"sha256 mismatch for {key}");
            }
        }
    }

    /// <summary>Rule 6: every placeholder is in the closed list and every schema parses as JSON.</summary>
    private void VerifyTemplates()
    {
        foreach (var (key, bytes) in _bytes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var token in PromptTemplate.UnterminatedPlaceholders(text))
            {
                _violations.Add($"{PromptTemplate.UnterminatedViolation(token)} in {key}");
            }

            foreach (var name in PromptTemplate.UnknownPlaceholders(text))
            {
                _violations.Add(
                    $"unknown placeholder '{PromptTemplate.Open}{name}{PromptTemplate.Close}' in {key}");
            }

            if (!IsJson(key))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _violations.Add($"{key} must be a JSON object");
                }
            }
            catch (JsonException exception)
            {
                _violations.Add($"{key} is not valid JSON: {exception.Message}");
            }
        }
    }

    /// <summary>Whether a tracked key must parse as JSON: every schema, and the structure vocabulary.</summary>
    private static bool IsJson(string key) => key == PromptKeys.StructureVocab || PromptKeys.IsSchema(key);

    /// <summary>Whether a key is in the manifest and resolved to exactly one resource.</summary>
    private bool IsPresent(string key) => Manifest.Files.ContainsKey(key) && _logicalNames.ContainsKey(key);
}
